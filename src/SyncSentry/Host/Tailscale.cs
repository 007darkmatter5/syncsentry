using System.Net.Sockets;
using System.Text.Json;

namespace SyncSentry.Host;

public sealed record TailscaleView(bool? AcceptRoutes, bool Stale, string Source, string? Detail);

public interface ITailscaleReader
{
    Task<TailscaleView> ReadAsync(string? statusPath, string? socketPath, CancellationToken cancellationToken);
}

public sealed class TailscaleReader : ITailscaleReader
{
    public async Task<TailscaleView> ReadAsync(string? statusPath, string? socketPath, CancellationToken cancellationToken)
    {
        TailscaleView? fromFile = null;
        if (!string.IsNullOrWhiteSpace(statusPath) && Rules.AbsolutePath(statusPath))
            fromFile = ReadFile(statusPath);

        if (fromFile is { Stale: false, AcceptRoutes: not null })
            return fromFile;

        if (!string.IsNullOrWhiteSpace(socketPath) && Rules.AbsolutePath(socketPath))
        {
            var fromSocket = await ReadSocketAsync(socketPath, cancellationToken).ConfigureAwait(false);
            if (fromSocket.AcceptRoutes is not null || fromFile is null)
                return fromSocket;
        }

        return fromFile ?? new TailscaleView(null, false, "none", "No Tailscale status file or socket is configured.");
    }

    public static TailscaleView ReadFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var written = new DateTimeOffset(DateTime.SpecifyKind(File.GetLastWriteTimeUtc(path), DateTimeKind.Utc));
            var view = Parse(text, "file");
            var stale = DateTimeOffset.UtcNow - written > TimeSpan.FromMinutes(5);
            return view with { Stale = stale, Detail = stale ? "Status file is more than 5 minutes old." : view.Detail };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TailscaleView(null, false, "file", "Status file could not be read.");
        }
    }

    public static TailscaleView Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        bool? accept = null;
        if (root.TryGetProperty("acceptRoutes", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            accept = flag.GetBoolean();
        else if (root.TryGetProperty("RouteAll", out var route) && route.ValueKind is JsonValueKind.True or JsonValueKind.False)
            accept = route.GetBoolean();
        var state = root.TryGetProperty("backendState", out var backend) ? backend.GetString() : null;
        return new TailscaleView(accept, false, source, state);
    }

    private static async Task<TailscaleView> ReadSocketAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), connect.Token).ConfigureAwait(false);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = "GET /localapi/v0/prefs HTTP/1.0\r\nHost: local-tailscaled.sock\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(request, connect.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var response = await reader.ReadToEndAsync(connect.Token).ConfigureAwait(false);
            var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0)
                return new TailscaleView(null, false, "socket", "Tailscale socket returned no body.");
            var body = response[(split + 4)..];
            return Parse(body, "socket");
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
        {
            return new TailscaleView(null, false, "socket", "Tailscale socket could not be read.");
        }
    }
}
