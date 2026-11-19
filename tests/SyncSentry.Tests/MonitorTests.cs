using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSentry.Alerts;
using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Jobs;
using SyncSentry.Rclone;
using SyncSentry.Security;

namespace SyncSentry.Tests;

public class MonitorTests
{
    [Fact]
    public async Task Tick_reports_unresponsive_without_waiting_and_alerts_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-mon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "t.db"))
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.Settings.Add(new InstanceSetting
            {
                UpdatedUtc = DateTimeOffset.UtcNow,
                ProbeTimeoutSeconds = 3,
                NtfyServerUrl = "https://ntfy.example",
                NtfyTopic = "syncsentry",
                AlertMountUnresponsive = true
            });
            db.WatchedMounts.Add(new WatchedMount { Name = "UNASPRO Media", Path = "/mnt/remotes/UNASPRO_Media", Enabled = true });
            db.WatchedMounts.Add(new WatchedMount { Name = "User shares", Path = "/mnt/user", Enabled = true });
            var log = Path.Combine(root, "initial-copy.log");
            await File.WriteAllTextAsync(log, "2026/11/19 18:01:00 INFO  :     1.500 GiB / 12.000 GiB, 13%, 25.000 MiB/s, ETA 7m12s (xfr#4/120)\n");
            File.SetLastWriteTimeUtc(log, DateTime.UtcNow);
            db.LogSources.Add(new LogSource { Name = "initial-copy", Path = log, Enabled = true });
            await db.SaveChangesAsync();
        }

        var ntfy = new RecordingNtfy();
        var probe = new ScriptedProbe();
        var board = new LiveBoard();
        var tick = new MonitorTick(
            new Factory(options),
            board,
            new FixedMounts(),
            probe,
            new QuietRc(),
            new DirectLogs(),
            new FixedTailscale(),
            new AlertDispatcher(ntfy, TimeProvider.System),
            new PrefixProtector(),
            TimeProvider.System,
            NullLogger<MonitorTick>.Instance);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await tick.RunAsync(CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Contains(board.Current.Mounts, view => view.Path == "/mnt/user" && view.State == "mounted" && view.Free > 0);
        Assert.Contains(board.Current.Jobs, job => job.Origin == "log" && job.Status == "running" && job.Bytes > 0);
        Assert.Empty(ntfy.Bodies);

        probe.UnasDown = true;
        await tick.RunAsync(CancellationToken.None);
        Assert.Contains(board.Current.Mounts, view => view.Path.EndsWith("UNASPRO_Media", StringComparison.Ordinal) && view.State == "unresponsive");
        Assert.Single(ntfy.Bodies);
        Assert.Contains("UNAS", ntfy.Titles[0]);
        await tick.RunAsync(CancellationToken.None);
        Assert.Single(ntfy.Bodies);
        Directory.Delete(root, true);
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class FixedMounts : IMountTable
    {
        public IReadOnlyList<MountEntry> Read() =>
        [
            new("/mnt/user", "fuse.mergerfs", "/mnt/disk1:/mnt/disk2"),
            new("/mnt/remotes/UNASPRO_Media", "cifs", "//192.168.1.20/Media"),
            new("/mnt/remotes/backup", "cifs", "//192.168.1.20/backup")
        ];
    }

    private sealed class ScriptedProbe : IPathProbe
    {
        public bool UnasDown { get; set; }

        public Task<ProbeResult> ProbeAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (UnasDown && path.Contains("UNASPRO_Media", StringComparison.Ordinal))
                return Task.FromResult(ProbeResult.Unresponsive("timed out"));
            return Task.FromResult(ProbeResult.Mounted(1000, 400, 600));
        }
    }

    private sealed class QuietRc : IRcloneRc
    {
        public Task<RcSnapshot> GetSnapshotAsync(string? url, string? user, string? password, CancellationToken cancellationToken)
            => Task.FromResult(new RcSnapshot(true, null, [], null));

        public Task<RcCallResult> StartAsync(string? url, string? user, string? password, RcStartRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RcCallResult(true, false, 1, null));

        public Task<RemoteListResult> ListRemoteAsync(string? url, string? user, string? password, string remote, int maxFiles, CancellationToken cancellationToken)
            => Task.FromResult(new RemoteListResult(true, false, null, []));
    }

    private sealed class DirectLogs : ILogReader
    {
        public async Task<LogRead> ReadAsync(string path, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            var written = new DateTimeOffset(DateTime.SpecifyKind(File.GetLastWriteTimeUtc(path), DateTimeKind.Utc));
            return new LogRead(true, text, written, null, false);
        }
    }

    private sealed class FixedTailscale : ITailscaleReader
    {
        public Task<TailscaleView> ReadAsync(string? statusPath, string? socketPath, CancellationToken cancellationToken)
            => Task.FromResult(new TailscaleView(true, false, "file", "Running"));
    }

    private sealed class RecordingNtfy : INtfyClient
    {
        public List<string> Titles { get; } = [];
        public List<string> Bodies { get; } = [];

        public Task<NtfySendResult> SendAsync(NtfyTarget target, string title, string body, CancellationToken cancellationToken)
        {
            Titles.Add(title);
            Bodies.Add(body);
            Assert.True(target.Token is null || !body.Contains(target.Token, StringComparison.Ordinal));
            return Task.FromResult(new NtfySendResult(true, "Sent"));
        }
    }

    private sealed class PrefixProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string protectedPayload) => protectedPayload[4..];
    }
}
