using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using SyncSentry.Alerts;
using SyncSentry.Host;
using SyncSentry.Jobs;
using SyncSentry.Outbox;
using SyncSentry.Rclone;
using SyncSentry.Security;

namespace SyncSentry.Tests;

public class MountTests
{
    [Fact]
    public void Parse_strips_credentials_and_unescapes_spaces()
    {
        var text = """
            36 35 98:0 / /mnt/user rw,relatime shared:1 - fuse.mergerfs /mnt/disk1:/mnt/disk2 rw
            234 45 0:56 / /mnt/remotes/UNASPRO_Media rw,relatime shared:400 - cifs //user:supersecret@192.168.1.20/Media rw,username=media,password=supersecret
            55 1 8:1 / /mnt/remotes/My\040Media rw - cifs //server/My\040Media rw
            70 1 8:2 / / rw - ext4 /dev/root rw
            """;
        var mounts = MountInfo.Parse(text);
        var unas = Assert.Single(mounts, item => item.MountPoint.EndsWith("UNASPRO_Media", StringComparison.Ordinal));
        Assert.Equal("cifs", unas.FsType);
        Assert.Equal("//192.168.1.20/Media", unas.Source);
        Assert.DoesNotContain("supersecret", unas.Source);
        Assert.DoesNotContain("supersecret", string.Join(" ", mounts.Select(item => item.Source + item.FsType + item.MountPoint)));
        Assert.Contains(mounts, item => item.MountPoint == "/mnt/remotes/My Media");
        Assert.Equal("/mnt/user", MountInfo.FindCovering(mounts, "/mnt/user/data/media")!.MountPoint);
        Assert.Null(MountInfo.FindExact(mounts, "/mnt/user/data/media"));
    }

    [Fact]
    public void Planner_requires_an_exact_mount_point()
    {
        var table = MountInfo.Parse("10 1 0:1 / /mnt/remotes rw - ext4 /dev/sda1 rw\n20 10 0:2 / /mnt/user rw - fuse.mergerfs /mnt/disk1:/mnt/disk2 rw\n");
        var plan = MountPlanner.Plan([("UNAS", "/mnt/remotes/UNASPRO_Media"), ("shares", "/mnt/user")], table);
        var unas = Assert.Single(plan, item => item.Path == "/mnt/remotes/UNASPRO_Media");
        Assert.False(unas.Mounted);
        var shares = Assert.Single(plan, item => item.Path == "/mnt/user");
        Assert.True(shares.Mounted);
        Assert.Equal("fuse.mergerfs", shares.FsType);
        Assert.DoesNotContain(plan, item => item.Path == "/mnt/remotes");
    }

    [Fact]
    public void Risky_paths_are_isolated()
    {
        var cifs = new MountEntry("/mnt/remotes/UNASPRO_Media", "cifs", "//192.168.1.20/Media");
        Assert.True(MountRisk.PathNeedsIsolation("/mnt/remotes/UNASPRO_Media", cifs));
        Assert.True(MountRisk.IsRisky("cifs"));
        Assert.False(MountRisk.IsRisky("xfs"));
        Assert.False(MountRisk.PathNeedsIsolation("/tmp/syncsentry-demo/log", new MountEntry("/tmp", "tmpfs", "tmpfs")));
    }
}

public class LogTests
{
    [Fact]
    public void Parses_rclone_176_one_line_stats()
    {
        var text = """
            2026/11/19 18:01:00 INFO  : Starting copy
            2026/11/19 18:01:00 INFO  :     1.500 GiB / 12.000 GiB, 13%, 25.000 MiB/s, ETA 7m12s (xfr#4/120)
            2026/11/19 18:01:02 ERROR : shows/episode.mkv: Failed to copy: connection reset
            """;
        var snapshot = RcloneLogParser.Parse(text);
        Assert.NotNull(snapshot.Stats);
        Assert.Equal((long)Math.Round(1.5 * 1024 * 1024 * 1024), snapshot.Stats.Bytes);
        Assert.Equal((long)Math.Round(12.0 * 1024 * 1024 * 1024), snapshot.Stats.TotalBytes);
        Assert.Equal(13, snapshot.Stats.Percent);
        Assert.Equal((long)Math.Round(25.0 * 1024 * 1024), (long)Math.Round(snapshot.Stats.SpeedBytesPerSecond!.Value));
        Assert.Equal(7 * 60 + 12, snapshot.Stats.EtaSeconds);
        Assert.Equal(4, snapshot.Stats.Files);
        Assert.Equal(120, snapshot.Stats.TotalFiles);
        Assert.Equal(1, snapshot.ErrorCount);
        Assert.Contains("connection reset", snapshot.LastError);
    }

    [Fact]
    public void Parses_multiline_stats_and_assesses_freshness()
    {
        var text = """
            2026/11/19 18:02:00 INFO  : Transferred:   	    2.000 GiB / 4.000 GiB, 50%, 10.000 MiB/s, ETA 3m
            2026/11/19 18:02:00 INFO  : Transferred:            10 / 20, 50%
            2026/11/19 18:02:00 INFO  : Errors:                   2 (no need to retry)
            """;
        var snapshot = RcloneLogParser.Parse(text);
        Assert.NotNull(snapshot.Stats);
        Assert.Equal(10, snapshot.Stats.Files);
        Assert.Equal(20, snapshot.Stats.TotalFiles);
        Assert.Equal(2, snapshot.ErrorCount);
        var now = DateTimeOffset.Parse("2026-11-19T18:03:00Z");
        Assert.Equal(LogRunAssessor.Running, LogRunAssessor.Assess(snapshot, now.AddSeconds(-30), now, TimeSpan.FromSeconds(150)));
        Assert.Equal(LogRunAssessor.Failed, LogRunAssessor.Assess(snapshot, now.AddMinutes(-10), now, TimeSpan.FromSeconds(150)));

        var done = RcloneLogParser.Parse("2026/11/19 18:00:00 INFO  :    12.000 GiB / 12.000 GiB, 100%, 20.000 MiB/s, ETA 0s (xfr#80/80)\n");
        Assert.Equal(LogRunAssessor.Completed, LogRunAssessor.Assess(done, now.AddHours(-1), now, TimeSpan.FromSeconds(150)));
        Assert.Equal(0, done.Stats!.EtaSeconds);
    }
}

public class OutboxTests
{
    [Fact]
    public void Compare_uses_size_and_mtime_and_refuses_a_truncated_scan()
    {
        var source = new List<FileFingerprint>
        {
            new("movies/Ready.mkv", 100, 1_000),
            new("movies/New.mkv", 200, 2_000),
            new("shows/Pilot.mkv", 50, 2_000)
        };
        var dest = new List<FileFingerprint>
        {
            new("movies/Ready.mkv", 100, 1_000),
            new("movies/New.mkv", 80, 1_000),
            new("extra/Old.mkv", 10, 1_000)
        };
        var diff = OutboxComparer.Compare(source, dest, false, false);
        Assert.Equal(2, diff.Count);
        Assert.Equal(250, diff.Bytes);
        Assert.Contains(diff.Sample, item => item.Path == "movies/New.mkv" && item.Reason == "size");
        Assert.Contains(diff.Sample, item => item.Path == "shows/Pilot.mkv" && item.Reason == "missing");
        Assert.DoesNotContain(diff.Sample, item => item.Path.Contains("Ready", StringComparison.Ordinal));

        var incomplete = OutboxComparer.Compare(source, dest, true, false);
        Assert.True(incomplete.Incomplete);
        Assert.Empty(incomplete.Sample);
        Assert.Equal(0, incomplete.Count);
    }
}

public class AlertTests
{
    [Fact]
    public void Alerts_only_on_transitions()
    {
        var toggles = new AlertToggles(true, true, true, true, true);
        var since = DateTimeOffset.Parse("2026-11-19T00:00:00Z");
        Assert.Empty(AlertPolicy.ForMount(toggles, "UNAS", "/mnt/remotes/UNASPRO_Media", null, "unresponsive", since));
        var fired = AlertPolicy.ForMount(toggles, "UNAS", "/mnt/remotes/UNASPRO_Media", "mounted", "unresponsive", since);
        Assert.Equal("mount-unresponsive", Assert.Single(fired).Kind);
        Assert.Empty(AlertPolicy.ForMount(toggles, "UNAS", "/mnt/remotes/UNASPRO_Media", "unresponsive", "unresponsive", since));
        Assert.Empty(AlertPolicy.ForJob(toggles, "copy", null, "failed", "log:1", since));
        Assert.Equal("job-failed", Assert.Single(AlertPolicy.ForJob(toggles, "copy", "running", "failed", "log:1", since)).Kind);
        Assert.Equal("job-completed", Assert.Single(AlertPolicy.ForJob(toggles, "copy", "running", "completed", "log:1", since)).Kind);
        Assert.Empty(AlertPolicy.ForRoutes(toggles, true, true, since));
        Assert.Equal("accept-routes", Assert.Single(AlertPolicy.ForRoutes(toggles, true, false, since)).Kind);
        Assert.Empty(AlertPolicy.ForRoutes(toggles, false, false, since));
    }
}

public class SecretTests
{
    [Fact]
    public void Scrub_removes_bearer_tokens_and_passwords()
    {
        var scrubbed = SecretRedactor.Scrub("Authorization: Bearer abcdefgh token=hunter2 password=secret https://user:secret@files.example/a");
        Assert.DoesNotContain("abcdefgh", scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.DoesNotContain("password=secret", scrubbed);
        Assert.DoesNotContain("user:secret", scrubbed);
    }

    [Fact]
    public async Task Ntfy_request_keeps_the_token_out_of_the_url_and_body()
    {
        Assert.True(NtfyRequest.TryCreate(new NtfyTarget("https://ntfy.example/base", "syncsentry", "s3cret-token"), "Hello", "body text", out var message, out var error));
        Assert.Null(error);
        Assert.NotNull(message);
        Assert.DoesNotContain("s3cret-token", message.RequestUri!.ToString());
        var body = await message.Content!.ReadAsStringAsync();
        Assert.Equal("body text", body);
        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
        Assert.Equal("s3cret-token", message.Headers.Authorization.Parameter);
        Assert.False(NtfyRequest.TryCreate(new NtfyTarget("http://user:pass@bad", "topic", null), "t", "b", out _, out var bad));
        Assert.False(bad is null);
    }

    [Fact]
    public void Rc_stats_parse_without_keeping_raw_errors_unbounded()
    {
        using var doc = JsonDocument.Parse("""
            {"bytes":10,"totalBytes":100,"transfers":2,"totalTransfers":8,"speed":1000,"eta":5,"errors":1,"lastError":"Bearer hunter2","transferring":[{"name":"a.mkv","bytes":1,"size":2,"percentage":50,"speed":3}],"jobids":[7]}
            """);
        var stats = RcJson.ParseStats(doc.RootElement);
        Assert.Equal(10, stats.Bytes);
        Assert.Equal(5, stats.EtaSeconds);
        Assert.Equal("a.mkv", Assert.Single(stats.Transferring).Name);
        Assert.DoesNotContain("hunter2", stats.LastError);
        Assert.Equal(7, Assert.Single(RcJson.ParseJobIds(doc.RootElement)));
    }
}

public class TriggerTests
{
    [Fact]
    public void A_pending_trigger_is_not_written_twice()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-trigger-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-19T00:00:00Z");
        var first = TriggerFiles.Write(dir, id, "Media copy", "media-copy", now);
        Assert.True(first.Ok);
        var second = TriggerFiles.Write(dir, id, "Media copy", "media-copy", now);
        Assert.True(second.AlreadyPending);
        Assert.False(RunNowGuard.Decide(true, false, true).Allowed);
        Assert.False(RunNowGuard.Decide(true, true, false).Allowed);
        Assert.True(RunNowGuard.Decide(true, false, false).Allowed);
        var text = File.ReadAllText(Path.Combine(dir, id.ToString("N") + ".request"));
        Assert.Contains("scriptKey=media-copy", text);
        Assert.DoesNotContain("\nscriptKey=", text.Replace("scriptKey=media-copy", ""));
        Directory.Delete(dir, true);
    }
}

public class ProcessTests
{
    [Fact]
    public async Task Parent_returns_when_the_child_does_not()
    {
        var start = new ProcessStartInfo("/bin/sleep")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("30");
        var watch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        watch.Stop();
        Assert.True(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "elapsed " + watch.Elapsed);
    }

    [Fact]
    public void Stuck_registry_does_not_stack_probes()
    {
        var stuck = new StuckProcesses();
        var proc = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            stuck.Remember("probe:/mnt/remotes/UNASPRO_Media", proc);
            Assert.True(stuck.IsStuck("probe:/mnt/remotes/UNASPRO_Media"));
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
            Assert.False(stuck.IsStuck("probe:/mnt/remotes/UNASPRO_Media"));
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public async Task Io_probe_and_scan_run_as_a_child_process()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
            ? Path.Combine(root, "dotnet")
            : "dotnet";
        var dll = typeof(MountInfo).Assembly.Location;
        var probe = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        probe.ArgumentList.Add(dll);
        probe.ArgumentList.Add("--io-probe");
        probe.ArgumentList.Add("/tmp");
        var probed = await ProcessRunner.RunAsync(probe, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.False(probed.TimedOut);
        Assert.Equal(0, probed.ExitCode);
        Assert.Contains("\"Total\":", probed.Stdout);

        var dir = Path.Combine(Path.GetTempPath(), "ss-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "movies"));
        await File.WriteAllBytesAsync(Path.Combine(dir, "movies", "Ready.mkv"), new byte[12]);
        var scan = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        scan.ArgumentList.Add(dll);
        scan.ArgumentList.Add("--io-scan");
        scan.ArgumentList.Add(dir);
        scan.ArgumentList.Add("100");
        scan.ArgumentList.Add("10");
        var scanned = await ProcessRunner.RunAsync(scan, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(0, scanned.ExitCode);
        Assert.Contains("Ready.mkv", scanned.Stdout);
        Directory.Delete(dir, true);
    }
}
