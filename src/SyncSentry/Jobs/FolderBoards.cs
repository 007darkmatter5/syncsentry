using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Text;

namespace SyncSentry.Jobs;

public sealed record FolderPanel(
    string Key,
    string Title,
    string Status,
    int? Percent,
    string SourcePath,
    string DestinationPath,
    string SourceLabel,
    string DestinationLabel,
    long LeftFiles,
    long LeftBytes,
    double Speed,
    int? EtaSeconds,
    string? LastFile,
    DateTimeOffset? LastRun,
    int Errors,
    Guid? SavedTaskId,
    IReadOnlyList<LiveFileRow> Files);

public sealed record HistoryRow(
    string Name,
    long Size,
    DateTimeOffset CompletedUtc,
    bool Ok,
    string? Error,
    string SourcePath,
    string DestinationPath);

public static class FolderBoards
{
    public static IReadOnlyList<FolderPanel> Build(
        IReadOnlyList<CopyCard> copies,
        IReadOnlyList<LiveFileRow> files,
        IReadOnlyList<HistoryRow> history,
        IReadOnlyList<SyncJobDefinition> jobs,
        IReadOnlyList<MountView> mounts)
    {
        var panels = new Dictionary<string, Mutable>(StringComparer.Ordinal);
        foreach (var copy in copies)
        {
            var key = KeyFor(copy.SourcePath, copy.DestinationPath);
            var panel = Get(panels, key, copy.SourcePath, copy.DestinationPath, mounts);
            panel.Status = "copying";
            panel.Percent = TextFormat.Percent(copy.Bytes, copy.TotalBytes);
            panel.LeftFiles = Math.Max(0, copy.FilesTotal - copy.FilesDone);
            panel.LeftBytes = Math.Max(0, copy.TotalBytes - copy.Bytes);
            panel.Speed = copy.Speed;
            panel.EtaSeconds = copy.EtaSeconds;
            panel.Errors = Math.Max(panel.Errors, copy.Errors);
            panel.Files = files.Where(file => SamePair(file.SourcePath, file.DestinationPath, copy.SourcePath, copy.DestinationPath)).ToArray();
        }

        foreach (var row in history.OrderByDescending(item => item.CompletedUtc))
        {
            var key = KeyFor(row.SourcePath, row.DestinationPath);
            var panel = Get(panels, key, row.SourcePath, row.DestinationPath, mounts);
            panel.LastRun ??= row.CompletedUtc;
            if (row.Ok && panel.LastFile is null)
                panel.LastFile = row.Name;
            if (panel.Status is "unknown" or "uptodate")
                panel.Status = row.Ok || panel.Status == "uptodate" ? "uptodate" : "error";
            if (panel.Status == "unknown")
                panel.Status = row.Ok ? "uptodate" : "error";
        }

        foreach (var panel in panels.Values)
        {
            var failed = history.Count(item => SamePair(item.SourcePath, item.DestinationPath, panel.SourcePath, panel.DestinationPath) && !item.Ok);
            panel.Errors = Math.Max(panel.Errors, failed);
            if (panel.Status != "unknown")
                continue;
            var ok = history.Any(item => SamePair(item.SourcePath, item.DestinationPath, panel.SourcePath, panel.DestinationPath) && item.Ok);
            panel.Status = failed > 0 && !ok ? "error" : ok ? "uptodate" : "unknown";
        }

        foreach (var job in jobs.Where(item => item.Enabled))
        {
            var match = panels.Values.FirstOrDefault(item => PathsMatch(job.Source, item.SourcePath) && PathsMatch(job.Destination, item.DestinationPath));
            if (match is null)
            {
                var created = Get(panels, KeyFor(job.Source, job.Destination), job.Source, job.Destination, mounts);
                created.Status = "waiting";
                created.SavedTaskId = job.Id;
                continue;
            }

            match.SavedTaskId = job.Id;
        }

        return panels.Values
            .OrderBy(item => item.Status == "copying" ? 0 : item.Status == "error" ? 1 : item.Status == "waiting" ? 2 : 3)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Freeze())
            .ToArray();
    }

    public static string KeyFor(string? source, string? destination)
        => MountInfo.Normalize(source ?? "") + "\n" + MountInfo.Normalize(destination ?? "");

    public static bool PathsMatch(string? configured, string? actual)
    {
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(actual))
            return false;
        var left = MountInfo.Normalize(configured);
        var right = MountInfo.Normalize(actual);
        return left == right || right.StartsWith(left + "/", StringComparison.Ordinal) || left.StartsWith(right + "/", StringComparison.Ordinal);
    }

    private static bool SamePair(string? leftSource, string? leftDestination, string? rightSource, string? rightDestination)
        => PathsMatch(leftSource, rightSource) && PathsMatch(leftDestination, rightDestination);

    private static Mutable Get(Dictionary<string, Mutable> panels, string key, string source, string destination, IReadOnlyList<MountView> mounts)
    {
        if (panels.TryGetValue(key, out var existing))
            return existing;
        var created = new Mutable
        {
            Key = key,
            SourcePath = source,
            DestinationPath = destination,
            SourceLabel = MountLabels.Label(source, mounts),
            DestinationLabel = MountLabels.Label(destination, mounts),
            Title = TitleFor(source, destination, mounts),
            Status = "unknown"
        };
        panels[key] = created;
        return created;
    }

    private static string TitleFor(string source, string destination, IReadOnlyList<MountView> mounts)
    {
        var leaf = source.TrimEnd('/').Split('/').LastOrDefault(part => part.Length > 0) ?? "Copy";
        return leaf + ": " + MountLabels.Label(source, mounts) + " → " + MountLabels.Label(destination, mounts);
    }

    private sealed class Mutable
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "unknown";
        public int? Percent { get; set; }
        public string SourcePath { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public string SourceLabel { get; set; } = "";
        public string DestinationLabel { get; set; } = "";
        public long LeftFiles { get; set; }
        public long LeftBytes { get; set; }
        public double Speed { get; set; }
        public int? EtaSeconds { get; set; }
        public string? LastFile { get; set; }
        public DateTimeOffset? LastRun { get; set; }
        public int Errors { get; set; }
        public Guid? SavedTaskId { get; set; }
        public IReadOnlyList<LiveFileRow> Files { get; set; } = [];

        public FolderPanel Freeze() => new(
            Key, Title, Status, Percent, SourcePath, DestinationPath, SourceLabel, DestinationLabel,
            LeftFiles, LeftBytes, Speed, EtaSeconds, LastFile, LastRun, Errors, SavedTaskId, Files);
    }
}
