using System.Text.Json;
using SyncSentry.Outbox;

namespace SyncSentry.Host;

public sealed record ProbeResult(string State, long? Total, long? Free, long? Used, string? Detail)
{
    public int? ResponseMilliseconds { get; init; }

    public static ProbeResult Mounted(long total, long free, long used) => new("mounted", total, free, used, null);

    public static ProbeResult Unresponsive(string detail) => new("unresponsive", null, null, null, detail);

    public static ProbeResult Answered(string detail) => new("mounted", null, null, null, detail);
}

public interface IPathProbe
{
    Task<ProbeResult> ProbeAsync(string path, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class PathProbeService(StuckProcesses stuck) : IPathProbe
{
    public async Task<ProbeResult> ProbeAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = "probe:" + MountInfo.Normalize(path);
        if (stuck.IsStuck(key))
            return ProbeResult.Unresponsive("an earlier check is still stuck");

        var start = AppSpawn.Create("--io-probe", path);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(start, timeout, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        var elapsed = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);
        if (result.TimedOut || !result.Exited)
        {
            if (result.LiveProcess is not null)
                stuck.Remember(key, result.LiveProcess);
            return ProbeResult.Unresponsive("timed out") with { ResponseMilliseconds = elapsed };
        }

        if (result.ExitCode == 0)
        {
            try
            {
                using var json = JsonDocument.Parse(result.Stdout);
                var root = json.RootElement;
                var total = root.GetProperty("Total").GetInt64();
                var free = root.GetProperty("Free").GetInt64();
                var used = root.TryGetProperty("Used", out var usedValue) ? usedValue.GetInt64() : Math.Max(0, total - free);
                return ProbeResult.Mounted(total, free, used) with { ResponseMilliseconds = elapsed };
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
            {
                return ProbeResult.Answered("space check returned nothing useful") with { ResponseMilliseconds = elapsed };
            }
        }

        return ProbeResult.Answered("space check failed") with { ResponseMilliseconds = elapsed };
    }
}

public interface ITreeScanner
{
    Task<TreeScan> ScanAsync(string root, int maxFiles, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record TreeScan(bool Ok, bool Truncated, bool Unresponsive, string? Error, IReadOnlyList<FileFingerprint> Files);

public sealed class TreeScanner(IMountTable mounts, StuckProcesses stuck) : ITreeScanner
{
    public async Task<TreeScan> ScanAsync(string root, int maxFiles, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Rules.AbsolutePath(root))
            return new TreeScan(false, false, false, "Path must be absolute.", []);

        var table = mounts.Read();
        var covering = MountInfo.FindCovering(table, root);
        var isolate = MountRisk.PathNeedsIsolation(root, covering);
        if (!isolate)
        {
            var walk = TreeWalker.Walk(root, maxFiles, timeout);
            return new TreeScan(true, walk.Truncated, false, null, walk.Files);
        }

        var key = "scan:" + MountInfo.Normalize(root);
        if (stuck.IsStuck(key))
            return new TreeScan(false, false, true, "An earlier scan of this path is still stuck.", []);

        var seconds = Math.Clamp((int)timeout.TotalSeconds, 1, 300);
        var start = AppSpawn.Create("--io-scan", root, maxFiles.ToString(), seconds.ToString());
        var result = await ProcessRunner.RunAsync(start, timeout + TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || !result.Exited)
        {
            if (result.LiveProcess is not null)
                stuck.Remember(key, result.LiveProcess);
            return new TreeScan(false, false, true, "The scan timed out. The mount may be frozen.", []);
        }

        var files = new List<FileFingerprint>();
        foreach (var line in result.Stdout.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var path = json.RootElement.GetProperty("P").GetString() ?? "";
                var size = json.RootElement.GetProperty("S").GetInt64();
                var mtime = json.RootElement.GetProperty("M").GetInt64();
                if (path.Length == 0 || path.StartsWith('/') || path.StartsWith("..", StringComparison.Ordinal))
                    continue;
                files.Add(new FileFingerprint(path, size, mtime));
            }
            catch (JsonException)
            {
            }
        }

        var truncated = result.ExitCode == 3 || result.TruncatedOutput;
        return new TreeScan(true, truncated, false, null, files);
    }
}

public interface ILogReader
{
    Task<LogRead> ReadAsync(string path, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed record LogRead(bool Ok, string? Text, DateTimeOffset? WrittenUtc, string? Error, bool Unresponsive);

public sealed class LogReader(IMountTable mounts, StuckProcesses stuck) : ILogReader
{
    public async Task<LogRead> ReadAsync(string path, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Rules.AbsolutePath(path))
            return new LogRead(false, null, null, "Path must be absolute.", false);

        var table = mounts.Read();
        var covering = MountInfo.FindCovering(table, path);
        if (!MountRisk.PathNeedsIsolation(path, covering))
        {
            try
            {
                var (text, stamp) = LocalTail.Read(path, maxBytes);
                return new LogRead(true, text, stamp, null, false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                return new LogRead(false, null, null, ex is FileNotFoundException or DirectoryNotFoundException ? "Log file not found." : "Could not read the log.", false);
            }
        }

        var key = "tail:" + MountInfo.Normalize(path);
        if (stuck.IsStuck(key))
            return new LogRead(false, null, null, "An earlier read is still stuck.", true);

        var start = AppSpawn.Create("--io-tail", path, maxBytes.ToString());
        var result = await ProcessRunner.RunAsync(start, timeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || !result.Exited)
        {
            if (result.LiveProcess is not null)
                stuck.Remember(key, result.LiveProcess);
            return new LogRead(false, null, null, "The log read timed out.", true);
        }

        if (result.ExitCode != 0)
            return new LogRead(false, null, null, "Log file not found.", false);

        DateTimeOffset? written = null;
        foreach (var line in result.Stderr.Split('\n'))
        {
            if (line.StartsWith("mtime:", StringComparison.Ordinal) && long.TryParse(line["mtime:".Length..].Trim(), out var unix))
                written = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return new LogRead(true, result.Stdout, written, null, false);
    }
}

public interface IMountTable
{
    IReadOnlyList<MountEntry> Read();
}

public sealed class ProcMountTable : IMountTable
{
    public IReadOnlyList<MountEntry> Read()
    {
        try
        {
            return MountInfo.Parse(File.ReadAllText("/proc/self/mountinfo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
