using System.Net;
using System.Text;
using SyncSentry.Host;
using SyncSentry.Outbox;
using SyncSentry.Rclone;

namespace SyncSentry.Demo;

public sealed class DemoMountTable : IMountTable
{
    public IReadOnlyList<MountEntry> Read() =>
    [
        new("/mnt/user", "fuse.mergerfs", "/mnt/disk1:/mnt/disk2"),
        new("/mnt/remotes/UNASPRO_Media", "cifs", "//192.168.1.20/Media"),
        new("/mnt/remotes/backup", "cifs", "//192.168.1.20/backup")
    ];
}

public sealed class DemoProbe : IPathProbe
{
    public Task<ProbeResult> ProbeAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var full = MountInfo.Normalize(path);
        if (full.EndsWith("UNASPRO_Media", StringComparison.Ordinal))
            return Task.FromResult(ProbeResult.Unresponsive("timed out"));
        if (full == "/mnt/user")
        {
            const long tebibyte = 1024L * 1024 * 1024 * 1024;
            return Task.FromResult(ProbeResult.Mounted(20 * tebibyte, 8 * tebibyte, 12 * tebibyte));
        }

        const long gibibyte = 1024L * 1024 * 1024;
        return Task.FromResult(ProbeResult.Mounted(10 * 1024 * gibibyte, 4 * 1024 * gibibyte, 6 * 1024 * gibibyte));
    }
}

public sealed class DemoRclone : IRcloneRc
{
    private readonly object _gate = new();
    private readonly List<RcJob> _jobs = [];
    private readonly List<RcFinished> _finished = [];

    public DemoRclone()
    {
        _jobs.Add(Sample(7, "syncsentry-demo"));
        _finished.Add(new RcFinished(
            "Movies/Already-There.mkv",
            2_200_000_000,
            DateTimeOffset.UtcNow.AddMinutes(-3),
            true,
            null,
            "/mnt/remotes/UNASPRO_Media/Movies",
            "/mnt/user/data/media/Movies"));
        _finished.Add(new RcFinished(
            "Movies/Skipped.mkv",
            800_000_000,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            false,
            "connection reset",
            "/mnt/remotes/UNASPRO_Media/Movies",
            "/mnt/user/data/media/Movies"));
    }

    public Task<RcSnapshot> GetSnapshotAsync(string? url, string? user, string? password, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(new RcSnapshot(true, null, _jobs.ToArray(), _jobs[0].Stats, RcAccess.Ok, _finished.ToArray()));
    }

    public Task<RcCallResult> StartAsync(string? url, string? user, string? password, RcStartRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_jobs.Any(job => !job.Finished && job.Group == request.Group))
                return Task.FromResult(new RcCallResult(false, true, null, "That job is already running."));
            var id = _jobs.Max(job => job.Id) + 1;
            _jobs.Add(Sample(id, request.Group));
            return Task.FromResult(new RcCallResult(true, false, id, null));
        }
    }

    public Task<RemoteListResult> ListRemoteAsync(string? url, string? user, string? password, string remote, int maxFiles, CancellationToken cancellationToken)
    {
        IReadOnlyList<FileFingerprint> files =
        [
            new("movies/Ready.mkv", 100, 1_700_000_000)
        ];
        return Task.FromResult(new RemoteListResult(true, false, null, files));
    }

    private static RcJob Sample(long id, string group)
    {
        var stats = new RcStats(
            5L * 1024 * 1024 * 1024,
            20L * 1024 * 1024 * 1024,
            12,
            80,
            30d * 1024 * 1024,
            500,
            0,
            null,
            [
                new RcTransfer("Movies/Example.mkv", 540_000_000, 1_200_000_000, 45, 18d * 1024 * 1024, "/mnt/remotes/UNASPRO_Media/Movies", "/mnt/user/data/media/Movies", 36),
                new RcTransfer("Movies/Trailer.mkv", 80_000_000, 640_000_000, 12, 8d * 1024 * 1024, "/mnt/remotes/UNASPRO_Media/Movies", "/mnt/user/data/media/Movies", 70)
            ],
            480);
        return new RcJob(id, group, false, false, null, DateTimeOffset.UtcNow.AddMinutes(-8), stats);
    }
}

public static class DemoInbox
{
    private static readonly List<(string Title, string Body)> Items = [];
    private static readonly object Gate = new();
    private static int _started;

    public static string? Url { get; private set; }

    public static IReadOnlyList<(string Title, string Body)> Recent
    {
        get
        {
            lock (Gate)
                return Items.ToArray();
        }
    }

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add("http://127.0.0.1:18099/");
            listener.Start();
        }
        catch (HttpListenerException)
        {
            Interlocked.Exchange(ref _started, 0);
            return;
        }

        Url = "http://127.0.0.1:18099";
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var title = context.Request.Headers["Title"] ?? "";
                    lock (Gate)
                    {
                        Items.Add((title, body));
                        if (Items.Count > 20)
                            Items.RemoveAt(0);
                    }

                    var bytes = "ok"u8.ToArray();
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (Exception)
                {
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        });
    }
}

public static class DemoFiles
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "syncsentry-demo");

    public static void Write()
    {
        var source = Path.Combine(Root, "src");
        var destination = Path.Combine(Root, "dst");
        Directory.CreateDirectory(Path.Combine(source, "movies"));
        Directory.CreateDirectory(Path.Combine(source, "shows"));
        Directory.CreateDirectory(Path.Combine(destination, "movies"));
        WriteFile(Path.Combine(source, "movies", "Ready.mkv"), 100, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteFile(Path.Combine(destination, "movies", "Ready.mkv"), 100, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteFile(Path.Combine(source, "movies", "New.mkv"), 200, new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc));
        WriteFile(Path.Combine(destination, "movies", "New.mkv"), 80, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        WriteFile(Path.Combine(source, "shows", "Pilot.mkv"), 50, new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc));

        var log = Path.Combine(Root, "initial-copy.log");
        var stamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
        File.WriteAllText(log, $"""
            {stamp} INFO  : Starting copy
            {stamp} INFO  :     4.250 GiB / 18.000 GiB, 24%, 32.500 MiB/s, ETA 7m12s (xfr#12/80)
            {stamp} ERROR : shows/Pilot.mkv: Failed to copy: connection reset by peer
            """);

        var tail = Path.Combine(Root, "tailscale.json");
        File.WriteAllText(tail, "{\"acceptRoutes\": true, \"backendState\": \"Running\"}\n");
    }

    private static void WriteFile(string path, int bytes, DateTime written)
    {
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, written);
    }
}
