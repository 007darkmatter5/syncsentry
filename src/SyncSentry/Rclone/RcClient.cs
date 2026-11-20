using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SyncSentry.Outbox;
using SyncSentry.Security;

namespace SyncSentry.Rclone;

public sealed record RcTransfer(
    string Name,
    long Bytes,
    long Size,
    int? Percentage,
    double Speed,
    string Source = "",
    string Destination = "",
    int? EtaSeconds = null);

public sealed record RcFinished(
    string Name,
    long Size,
    DateTimeOffset? CompletedAt,
    bool Ok,
    string? Error,
    string Source = "",
    string Destination = "",
    bool Checked = false,
    long Bytes = -1);

public enum RcAccess
{
    Ok,
    NotConfigured,
    Unreachable,
    Unauthorized,
    Failed
}

public sealed record RcStats(
    long Bytes,
    long TotalBytes,
    long Files,
    long TotalFiles,
    double Speed,
    int? EtaSeconds,
    int Errors,
    string? LastError,
    IReadOnlyList<RcTransfer> Transferring,
    double? ElapsedSeconds = null);

public sealed record RcJob(
    long Id,
    string Group,
    bool Finished,
    bool Success,
    string? Error,
    DateTimeOffset? StartedUtc,
    RcStats? Stats);

public sealed record RcSnapshot(
    bool Reachable,
    string? Error,
    IReadOnlyList<RcJob> Jobs,
    RcStats? Global,
    RcAccess Access = RcAccess.Ok,
    IReadOnlyList<RcFinished>? Finished = null);

public sealed record RcStartRequest(
    string Operation,
    string Source,
    string Destination,
    string Group,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude);

public sealed record RcCallResult(bool Ok, bool AlreadyRunning, long? JobId, string? Error);

public interface IRcloneRc
{
    Task<RcSnapshot> GetSnapshotAsync(string? url, string? user, string? password, CancellationToken cancellationToken);

    Task<RcCallResult> StartAsync(string? url, string? user, string? password, RcStartRequest request, CancellationToken cancellationToken);

    Task<RemoteListResult> ListRemoteAsync(string? url, string? user, string? password, string remote, int maxFiles, CancellationToken cancellationToken);

    Task<BandwidthReading> GetBandwidthAsync(string? url, string? user, string? password, CancellationToken cancellationToken);

    Task<BandwidthReading> SetBandwidthAsync(string? url, string? user, string? password, string rate, CancellationToken cancellationToken);

    Task<string?> GetVersionAsync(string? url, string? user, string? password, CancellationToken cancellationToken);

    Task<RcVersionProbe> ProbeVersionAsync(string? url, string? user, string? password, CancellationToken cancellationToken);
}

public sealed record BandwidthReading(string Rate, long BytesPerSecond, bool Ok);

public sealed record RcVersionProbe(RcAccess Access, string? Version);

public sealed record RemoteListResult(bool Ok, bool Truncated, string? Error, IReadOnlyList<FileFingerprint> Files);

public static class RcJson
{
    public static RcStats ParseStats(JsonElement root)
    {
        var bytes = Long(root, "bytes");
        var total = Long(root, "totalBytes");
        var files = Long(root, "transfers");
        var totalFiles = Long(root, "totalTransfers");
        var speed = Double(root, "speed");
        int? eta = null;
        if (root.TryGetProperty("eta", out var etaValue) && etaValue.ValueKind == JsonValueKind.Number && etaValue.TryGetInt32(out var etaSeconds) && etaSeconds >= 0)
            eta = etaSeconds;
        var errors = (int)Math.Min(int.MaxValue, Long(root, "errors"));
        var last = root.TryGetProperty("lastError", out var lastError) ? lastError.GetString() : null;
        if (string.IsNullOrWhiteSpace(last))
            last = null;
        double? elapsed = null;
        if (root.TryGetProperty("elapsedTime", out var elapsedValue) && elapsedValue.TryGetDouble(out var elapsedSeconds) && elapsedSeconds >= 0)
            elapsed = elapsedSeconds;
        var transferring = new List<RcTransfer>();
        if (root.TryGetProperty("transferring", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (transferring.Count >= 200)
                    break;
                transferring.Add(ParseTransfer(item));
            }
        }

        return new RcStats(bytes, total, files, totalFiles, speed, eta, errors, SecretRedactor.ForDisplay(last, 240), transferring, elapsed);
    }

    public static RcTransfer ParseTransfer(JsonElement item)
    {
        var name = Text(item, "name");
        var size = Long(item, "size");
        var bytes = Long(item, "bytes");
        int? percent = Percent(item, "percentage");
        if (percent is null && size > 0)
            percent = (int)Math.Clamp(Math.Round(bytes * 100.0 / size), 0, 100);
        int? eta = null;
        if (item.TryGetProperty("eta", out var etaValue) && etaValue.TryGetInt32(out var etaSeconds) && etaSeconds >= 0)
            eta = etaSeconds;
        return new RcTransfer(name, bytes, size, percent, Double(item, "speed"), Text(item, "srcFs"), Text(item, "dstFs"), eta);
    }

    public static IReadOnlyList<RcFinished> ParseFinished(JsonElement root)
    {
        if (!root.TryGetProperty("transferred", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<RcFinished>();
        foreach (var item in list.EnumerateArray())
        {
            var error = Text(item, "error");
            if (error.Length == 0 && item.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.Object)
                error = "error";
            DateTimeOffset? completed = null;
            foreach (var key in new[] { "completedAt", "CompletedAt", "timestamp" })
            {
                if (item.TryGetProperty(key, out var stamp) && stamp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(stamp.GetString(), out var parsed))
                {
                    completed = parsed;
                    break;
                }
            }

            items.Add(new RcFinished(
                Text(item, "name"),
                Long(item, "size"),
                completed,
                error.Length == 0,
                error.Length == 0 ? null : SecretRedactor.ForDisplay(error, 200),
                Text(item, "srcFs"),
                Text(item, "dstFs"),
                Flag(item, "checked"),
                item.TryGetProperty("bytes", out _) ? Long(item, "bytes") : -1));
        }

        return items
            .OrderByDescending(item => item.CompletedAt ?? DateTimeOffset.MinValue)
            .Take(50)
            .ToArray();
    }

    public static IReadOnlyList<long> ParseJobIds(JsonElement root)
    {
        if (!root.TryGetProperty("jobids", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var ids = new List<long>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetInt64(out var id))
                ids.Add(id);
        }

        return ids;
    }

    public static RcJob ParseJob(JsonElement root, RcStats? stats)
    {
        var id = Long(root, "id");
        var group = root.TryGetProperty("group", out var groupValue) ? groupValue.GetString() ?? "" : "";
        var finished = root.TryGetProperty("finished", out var finishedValue) && finishedValue.ValueKind == JsonValueKind.True;
        var success = root.TryGetProperty("success", out var successValue) && successValue.ValueKind == JsonValueKind.True;
        var error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(error))
            error = null;
        DateTimeOffset? started = null;
        if (root.TryGetProperty("startTime", out var start) && start.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(start.GetString(), out var parsed))
            started = parsed;
        return new RcJob(id, group, finished, success, SecretRedactor.ForDisplay(error, 240), started, stats);
    }

    public static long? ParseStartedJob(JsonElement root)
        => root.TryGetProperty("jobid", out var id) && id.TryGetInt64(out var value) ? value : null;

    private static bool Flag(JsonElement root, string name)
    {
        foreach (var key in new[] { name, char.ToUpperInvariant(name[0]) + name[1..] })
        {
            if (!root.TryGetProperty(key, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
        }

        return false;
    }

    private static long Long(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static double Double(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int? Percent(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        if (value.TryGetInt32(out var whole))
            return Math.Clamp(whole, 0, 100);
        if (value.TryGetDouble(out var number))
            return (int)Math.Clamp(Math.Round(number), 0, 100);
        return null;
    }
}

public sealed class RcloneRcClient(IHttpClientFactory factory) : IRcloneRc
{
    public async Task<RcSnapshot> GetSnapshotAsync(string? url, string? user, string? password, CancellationToken cancellationToken)
    {
        if (!Rules.RcUrl(url))
            return new RcSnapshot(false, "rclone's status address is not set.", [], null, RcAccess.NotConfigured);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var listed = await PostAsync(url!, user, password, "job/list", new { }, timeout.Token).ConfigureAwait(false);
            if (listed.Status == 401)
                return new RcSnapshot(false, "rclone refused the username or password.", [], null, RcAccess.Unauthorized);
            if (listed.Document is null && listed.Status == 0)
                return new RcSnapshot(false, "rclone did not answer.", [], null, RcAccess.Unreachable);
            var ids = listed.Document is null ? [] : RcJson.ParseJobIds(listed.Document.RootElement);
            var jobs = new List<RcJob>();
            foreach (var id in ids.Take(30))
            {
                var status = await PostAsync(url!, user, password, "job/status", new { jobid = id }, timeout.Token).ConfigureAwait(false);
                if (status.Document is null)
                {
                    status.Document?.Dispose();
                    continue;
                }

                using (status.Document)
                {
                    var group = status.Document.RootElement.TryGetProperty("group", out var groupValue) ? groupValue.GetString() : null;
                    RcStats? stats = null;
                    if (!string.IsNullOrWhiteSpace(group))
                    {
                        var statsResult = await PostAsync(url!, user, password, "core/stats", new { group }, timeout.Token).ConfigureAwait(false);
                        if (statsResult.Document is not null)
                        {
                            using (statsResult.Document)
                                stats = RcJson.ParseStats(statsResult.Document.RootElement);
                        }
                    }

                    jobs.Add(RcJson.ParseJob(status.Document.RootElement, stats));
                }
            }

            listed.Document?.Dispose();
            var globalResult = await PostAsync(url!, user, password, "core/stats", new { }, timeout.Token).ConfigureAwait(false);
            if (globalResult.Status == 401)
                return new RcSnapshot(false, "rclone refused the username or password.", [], null, RcAccess.Unauthorized);
            RcStats? global = null;
            if (globalResult.Document is not null)
            {
                using (globalResult.Document)
                    global = RcJson.ParseStats(globalResult.Document.RootElement);
            }

            var unfinished = jobs.Any(job => !job.Finished);
            if (!unfinished && global is { } active && (active.Transferring.Count > 0 || active.Speed > 1))
                jobs.Add(new RcJob(0, "global", false, false, null, null, active));

            IReadOnlyList<RcFinished> finished = [];
            var transferred = await PostAsync(url!, user, password, "core/transferred", new { }, timeout.Token).ConfigureAwait(false);
            if (transferred.Document is not null)
            {
                using (transferred.Document)
                    finished = RcJson.ParseFinished(transferred.Document.RootElement);
            }

            return new RcSnapshot(true, null, jobs, global, RcAccess.Ok, finished);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new RcSnapshot(false, "rclone did not answer.", [], null, RcAccess.Unreachable);
        }
    }

    public async Task<RcCallResult> StartAsync(string? url, string? user, string? password, RcStartRequest request, CancellationToken cancellationToken)
    {
        if (!Rules.RcUrl(url))
            return new RcCallResult(false, false, null, "rclone RC URL is not set.");
        if (request.Operation is not ("copy" or "sync"))
            return new RcCallResult(false, false, null, "Operation must be copy or sync.");
        if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Destination))
            return new RcCallResult(false, false, null, "Source and destination are required.");

        var snapshot = await GetSnapshotAsync(url, user, password, cancellationToken).ConfigureAwait(false);
        if (!snapshot.Reachable)
            return new RcCallResult(false, false, null, snapshot.Error);
        if (snapshot.Jobs.Any(job => !job.Finished && string.Equals(job.Group, request.Group, StringComparison.Ordinal)))
            return new RcCallResult(false, true, null, "That job is already running.");

        var payload = new Dictionary<string, object?>
        {
            ["srcFs"] = request.Source,
            ["dstFs"] = request.Destination,
            ["_async"] = true,
            ["_group"] = request.Group
        };
        if (request.Include.Count > 0 || request.Exclude.Count > 0)
        {
            payload["_filter"] = new Dictionary<string, object?>
            {
                ["IncludeRule"] = request.Include,
                ["ExcludeRule"] = request.Exclude
            };
        }

        try
        {
            var started = await PostAsync(url!, user, password, "sync/" + request.Operation, payload, cancellationToken).ConfigureAwait(false);
            if (started.Document is null)
                return new RcCallResult(false, false, null, started.Status == 401 ? "rclone refused the username or password." : "rclone did not start the copy.");
            using var doc = started.Document;
            var id = RcJson.ParseStartedJob(doc.RootElement);
            return id is null
                ? new RcCallResult(false, false, null, "rclone RC did not return a job id.")
                : new RcCallResult(true, false, id, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new RcCallResult(false, false, null, "rclone RC could not be reached.");
        }
    }

    public async Task<BandwidthReading> GetBandwidthAsync(string? url, string? user, string? password, CancellationToken cancellationToken)
        => await BandwidthAsync(url, user, password, null, cancellationToken).ConfigureAwait(false);

    public async Task<BandwidthReading> SetBandwidthAsync(string? url, string? user, string? password, string rate, CancellationToken cancellationToken)
        => await BandwidthAsync(url, user, password, string.IsNullOrWhiteSpace(rate) ? "off" : rate.Trim(), cancellationToken).ConfigureAwait(false);

    public async Task<string?> GetVersionAsync(string? url, string? user, string? password, CancellationToken cancellationToken)
    {
        var probe = await ProbeVersionAsync(url, user, password, cancellationToken).ConfigureAwait(false);
        return probe.Access == RcAccess.Ok ? probe.Version : null;
    }

    public async Task<RcVersionProbe> ProbeVersionAsync(string? url, string? user, string? password, CancellationToken cancellationToken)
    {
        if (!Rules.RcUrl(url))
            return new RcVersionProbe(RcAccess.NotConfigured, null);
        try
        {
            var result = await PostAsync(url!, user, password, "core/version", new { }, cancellationToken).ConfigureAwait(false);
            if (result.Status == 401)
                return new RcVersionProbe(RcAccess.Unauthorized, null);
            if (result.Document is null)
                return new RcVersionProbe(result.Status == 0 ? RcAccess.Unreachable : RcAccess.Failed, null);
            using var doc = result.Document;
            var version = doc.RootElement.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            return new RcVersionProbe(RcAccess.Ok, version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new RcVersionProbe(RcAccess.Unreachable, null);
        }
    }

    private async Task<BandwidthReading> BandwidthAsync(string? url, string? user, string? password, string? rate, CancellationToken cancellationToken)
    {
        if (!Rules.RcUrl(url))
            return new BandwidthReading("off", -1, false);
        object payload = rate is null ? new { } : new { rate };
        var result = await PostAsync(url!, user, password, "core/bwlimit", payload, cancellationToken).ConfigureAwait(false);
        if (result.Document is null)
            return new BandwidthReading(rate ?? "off", -1, false);
        using var doc = result.Document;
        var root = doc.RootElement;
        var text = root.TryGetProperty("rate", out var rateValue) ? rateValue.GetString() ?? "off" : rate ?? "off";
        long bytes = -1;
        if (root.TryGetProperty("bytesPerSecond", out var bytesValue) && bytesValue.TryGetInt64(out var parsed))
            bytes = parsed;
        return new BandwidthReading(text, bytes, true);
    }

    public async Task<RemoteListResult> ListRemoteAsync(string? url, string? user, string? password, string remote, int maxFiles, CancellationToken cancellationToken)
    {
        if (!Rules.RcUrl(url))
            return new RemoteListResult(false, false, "rclone RC URL is not set.", []);
        if (!Rules.TrySplitRemote(remote, out var fs, out var path))
            return new RemoteListResult(false, false, "Destination is not an rclone remote like name:path.", []);

        try
        {
            var listed = await PostAsync(url!, user, password, "operations/list", new
            {
                fs,
                remote = path,
                opt = new { recurse = true, filesOnly = true, noMimeType = true }
            }, cancellationToken).ConfigureAwait(false);
            if (listed.Document is null)
                return new RemoteListResult(false, false, "rclone did not list the remote.", []);
            using var doc = listed.Document;
            if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                return new RemoteListResult(true, false, null, []);
            var files = new List<FileFingerprint>();
            var truncated = false;
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("IsDir", out var dir) && dir.ValueKind == JsonValueKind.True)
                    continue;
                if (files.Count >= maxFiles)
                {
                    truncated = true;
                    break;
                }

                var rel = item.TryGetProperty("Path", out var pathValue) ? pathValue.GetString() ?? "" : "";
                var size = item.TryGetProperty("Size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                long mtime = 0;
                if (item.TryGetProperty("ModTime", out var mod) && mod.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(mod.GetString(), out var stamp))
                    mtime = stamp.ToUnixTimeSeconds();
                if (rel.Length == 0)
                    continue;
                files.Add(new FileFingerprint(OutboxComparer.Normalize(rel), size, mtime));
            }

            return new RemoteListResult(true, truncated, null, files);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new RemoteListResult(false, false, "rclone RC could not list the remote.", []);
        }
    }

    private readonly record struct RcHttpResult(JsonDocument? Document, int Status);

    private async Task<RcHttpResult> PostAsync(string url, string? user, string? password, string method, object payload, CancellationToken cancellationToken)
    {
        var client = factory.CreateClient("rclone");
        var endpoint = url.TrimEnd('/') + "/" + method;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(user))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + (password ?? "")));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new RcHttpResult(null, 0);
        }

        using (response)
        {
        if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
            return new RcHttpResult(null, 401);
        if (!response.IsSuccessStatusCode)
            return new RcHttpResult(null, (int)response.StatusCode);
        var length = response.Content.Headers.ContentLength;
        if (length > 48_000_000)
            throw new InvalidOperationException("response too large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > 48_000_000)
                throw new InvalidOperationException("response too large");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new RcHttpResult(document, (int)response.StatusCode);
        }
    }
}
