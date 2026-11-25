using System.Globalization;
using System.Text.RegularExpressions;

namespace SyncSentry.Pipeline;

public enum PushLineKind
{
    Copied,
    AlreadySynced,
    ScanRequested,
    RunSummary,
    PossibleFailure
}

public sealed record PushLine(
    DateTimeOffset Timestamp,
    PushLineKind Kind,
    string LibraryTag,
    string LibraryFolder,
    string RelativeFolder,
    long? Bytes,
    int? NewFiles,
    int? OkCount,
    int? FailedCount,
    string Message);

public sealed record PushFolder(
    string LibraryTag,
    string LibraryFolder,
    string RelativeFolder,
    DateTimeOffset? CopiedUtc,
    DateTimeOffset? ScanUtc,
    int? ScanNewFiles,
    bool Failed,
    DateTimeOffset? FailureUtc,
    string? Failure,
    long? Bytes);

/// <summary>
/// Reads the MediaManny UNAS push logs. Copy lines are per folder (a movie folder or a season folder).
/// Any line in a run that is not an OK, a Plex scan, or the run summary is a possible failure,
/// because the scripts do not use one fixed failure sentence.
/// </summary>
public static partial class UnasPushLog
{
    public static IReadOnlyList<PushLine> Parse(string? text)
    {
        var lines = new List<PushLine>();
        if (string.IsNullOrEmpty(text))
            return lines;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var head = Stamp().Match(line);
            if (!head.Success || !PipelineTime.TryParseLocal(head.Groups["ts"].Value, out var stamp))
                continue;
            var rest = head.Groups["rest"].Value.Trim();
            if (rest.Length == 0)
                continue;
            if (TryParse(stamp, rest, out var parsed))
                lines.Add(parsed);
        }

        return lines;
    }

    public static IReadOnlyList<PushFolder> Fold(IReadOnlyList<PushLine> lines)
    {
        var runs = new List<List<PushLine>>();
        var open = new List<PushLine>();
        foreach (var line in lines)
        {
            if (line.Kind == PushLineKind.RunSummary)
            {
                open.Add(line);
                runs.Add(open);
                open = [];
                continue;
            }

            open.Add(line);
        }

        if (open.Count > 0)
            runs.Add(open);

        var map = new Dictionary<string, MutableFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
            Apply(run, map);

        return map.Values
            .Select(item => item.Freeze())
            .OrderByDescending(item => item.CopiedUtc ?? item.ScanUtc ?? item.FailureUtc ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public static IReadOnlyList<PushFolder> Combine(params IReadOnlyList<PushFolder>[] sets)
    {
        var map = new Dictionary<string, MutableFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in sets.SelectMany(set => set))
        {
            var key = FolderKey.DiskKey(folder.LibraryTag, folder.RelativeFolder);
            if (!map.TryGetValue(key, out var current))
            {
                map[key] = MutableFolder.From(folder);
                continue;
            }

            current.Absorb(folder);
        }

        return map.Values.Select(item => item.Freeze()).ToList();
    }

    private static void Apply(List<PushLine> run, Dictionary<string, MutableFolder> map)
    {
        var summary = run.LastOrDefault(line => line.Kind == PushLineKind.RunSummary);
        var failedCount = summary?.FailedCount ?? 0;
        var failures = new List<PushLine>();
        foreach (var line in run)
        {
            if (line.Kind is PushLineKind.Copied or PushLineKind.AlreadySynced or PushLineKind.ScanRequested)
            {
                Touch(map, line).NoteSuccess(line);
                continue;
            }

            if (line.Kind == PushLineKind.PossibleFailure)
                failures.Add(line);
        }

        var blame = failedCount > 0 || failures.Any(LooksLikeError);
        if (!blame || failures.Count == 0)
            return;

        var named = failures.Where(line => line.RelativeFolder.Length > 0).ToList();
        if (named.Count > 0)
        {
            foreach (var line in named)
                Touch(map, line).NoteFailure(line);
            return;
        }

        if (failedCount <= 0)
            return;

        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new List<string>();
        foreach (var line in run)
        {
            if (line.RelativeFolder.Length == 0)
                continue;
            var key = FolderKey.DiskKey(line.LibraryTag, line.RelativeFolder);
            if (!seen.Contains(key, StringComparer.OrdinalIgnoreCase))
                seen.Add(key);
            if (line.Kind is PushLineKind.Copied or PushLineKind.AlreadySynced)
                copied.Add(key);
        }

        var unnamed = failures[0];
        foreach (var key in seen.Where(key => !copied.Contains(key)))
        {
            if (map.TryGetValue(key, out var folder))
                folder.NoteFailure(unnamed);
        }
    }

    private static MutableFolder Touch(Dictionary<string, MutableFolder> map, PushLine line)
    {
        var key = FolderKey.DiskKey(line.LibraryTag, line.RelativeFolder);
        if (map.TryGetValue(key, out var folder))
            return folder;
        folder = new MutableFolder(line.LibraryTag, line.LibraryFolder, line.RelativeFolder);
        map[key] = folder;
        return folder;
    }

    private static bool TryParse(DateTimeOffset stamp, string rest, out PushLine line)
    {
        line = null!;
        var copied = CopiedLine().Match(rest);
        if (copied.Success)
        {
            var (tag, folder, relative, bytes) = FolderOf(copied.Groups["lib"].Value, copied.Groups["path"].Value, copied.Groups["bytes"].Value);
            if (relative.Length == 0)
                return false;
            line = new PushLine(stamp, PushLineKind.Copied, tag, folder, relative, bytes, null, null, null, rest);
            return true;
        }

        var synced = SyncedLine().Match(rest);
        if (synced.Success)
        {
            var (tag, folder, relative, _) = FolderOf(synced.Groups["lib"].Value, synced.Groups["path"].Value, "");
            if (relative.Length == 0)
                return false;
            line = new PushLine(stamp, PushLineKind.AlreadySynced, tag, folder, relative, null, null, null, null, rest);
            return true;
        }

        if (rest.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            return false;

        var scan = ScanLine().Match(rest);
        if (scan.Success)
        {
            var tag = MediaLibrary.Tag(scan.Groups["lib"].Value);
            var split = PlexPath.Split(scan.Groups["path"].Value, MediaLibrary.Folder(tag));
            var relative = FolderKey.Normalize(split.RelativeFolder);
            if (relative.Length == 0)
                return false;
            var files = int.TryParse(scan.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : (int?)null;
            var libraryFolder = split.LibraryFolder.Length > 0 ? split.LibraryFolder : MediaLibrary.Folder(tag);
            line = new PushLine(stamp, PushLineKind.ScanRequested, tag, libraryFolder, relative, null, files, null, null, rest);
            return true;
        }

        var done = DoneLine().Match(rest);
        if (done.Success)
        {
            var tag = MediaLibrary.Tag(done.Groups["lib"].Value);
            var ok = int.TryParse(done.Groups["ok"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var okCount) ? okCount : 0;
            var failed = int.TryParse(done.Groups["failed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var failedCount) ? failedCount : 0;
            line = new PushLine(stamp, PushLineKind.RunSummary, tag, MediaLibrary.Folder(tag), "", null, null, ok, failed, rest);
            return true;
        }

        var (failTag, failFolder, failRelative) = FailureTarget(rest);
        line = new PushLine(stamp, PushLineKind.PossibleFailure, failTag, failFolder, failRelative, null, null, null, null, TrimMessage(rest));
        return true;
    }

    private static (string Tag, string Folder, string Relative, long? Bytes) FolderOf(string library, string path, string bytesText)
    {
        var tag = MediaLibrary.Tag(library);
        var relative = FolderKey.Normalize(StripBytes(path));
        long? bytes = null;
        if (long.TryParse(bytesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
            bytes = parsed;
        return (tag, MediaLibrary.Folder(tag), relative, bytes);
    }

    private static string StripBytes(string path)
    {
        var match = BytesSuffix().Match(path.Trim());
        return match.Success ? match.Groups["path"].Value.Trim() : path.Trim();
    }

    private static (string Tag, string Folder, string Relative) FailureTarget(string rest)
    {
        var match = LibraryTag().Match(rest);
        if (!match.Success)
            return ("", "", "");
        var tag = MediaLibrary.Tag(match.Groups["lib"].Value);
        var path = match.Groups["path"].Value.Trim();
        var colon = path.IndexOf(':');
        if (colon >= 0)
            path = path[..colon];
        path = StripBytes(path).Trim().TrimEnd('.', ',', ';');
        var relative = FolderKey.Normalize(path);
        if (!FolderKey.IsSafe(relative))
            relative = "";
        return (tag, MediaLibrary.Folder(tag), relative);
    }

    private static bool LooksLikeError(PushLine line)
    {
        var text = line.Message;
        return text.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimMessage(string text)
    {
        var value = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 300 ? value : value[..300].TrimEnd();
    }

    private sealed class MutableFolder(string tag, string folder, string relative)
    {
        public string Tag { get; } = tag;
        public string Folder { get; private set; } = folder;
        public string Relative { get; } = relative;
        public DateTimeOffset? CopiedUtc { get; private set; }
        public DateTimeOffset? ScanUtc { get; private set; }
        public int? ScanNewFiles { get; private set; }
        public bool Failed { get; private set; }
        public DateTimeOffset? FailureUtc { get; private set; }
        public string? Failure { get; private set; }
        public long? Bytes { get; private set; }

        public static MutableFolder From(PushFolder folder)
        {
            var item = new MutableFolder(folder.LibraryTag, folder.LibraryFolder, folder.RelativeFolder)
            {
                CopiedUtc = folder.CopiedUtc,
                ScanUtc = folder.ScanUtc,
                ScanNewFiles = folder.ScanNewFiles,
                Failed = folder.Failed,
                FailureUtc = folder.FailureUtc,
                Failure = folder.Failure,
                Bytes = folder.Bytes
            };
            return item;
        }

        public void NoteSuccess(PushLine line)
        {
            if (line.LibraryFolder.Length > 0)
                Folder = line.LibraryFolder;
            if (line.Kind is PushLineKind.Copied or PushLineKind.AlreadySynced)
            {
                CopiedUtc = Earlier(CopiedUtc, line.Timestamp);
                if (line.Bytes is not null)
                    Bytes = line.Bytes;
                if (FailureUtc is null || line.Timestamp >= FailureUtc)
                {
                    Failed = false;
                    Failure = null;
                }
            }

            if (line.Kind == PushLineKind.ScanRequested)
            {
                if (ScanUtc is null || line.Timestamp >= ScanUtc)
                {
                    ScanUtc = line.Timestamp;
                    ScanNewFiles = line.NewFiles ?? ScanNewFiles;
                }
            }
        }

        public void NoteFailure(PushLine line)
        {
            if (CopiedUtc is not null && CopiedUtc > line.Timestamp)
                return;
            Failed = true;
            FailureUtc = FailureUtc is null || line.Timestamp >= FailureUtc ? line.Timestamp : FailureUtc;
            if (!string.IsNullOrWhiteSpace(line.Message))
                Failure = line.Message;
        }

        public void Absorb(PushFolder folder)
        {
            if (folder.LibraryFolder.Length > 0)
                Folder = folder.LibraryFolder;
            if (folder.CopiedUtc is not null)
                CopiedUtc = Earlier(CopiedUtc, folder.CopiedUtc.Value);
            if (folder.Bytes is not null)
                Bytes = folder.Bytes;
            if (folder.ScanUtc is not null && (ScanUtc is null || folder.ScanUtc >= ScanUtc))
            {
                ScanUtc = folder.ScanUtc;
                ScanNewFiles = folder.ScanNewFiles ?? ScanNewFiles;
            }

            if (folder.Failed && (CopiedUtc is null || folder.FailureUtc is null || folder.FailureUtc >= CopiedUtc))
            {
                Failed = true;
                FailureUtc = folder.FailureUtc ?? FailureUtc;
                Failure = folder.Failure ?? Failure;
            }

            if (CopiedUtc is not null && FailureUtc is not null && CopiedUtc > FailureUtc)
            {
                Failed = false;
                Failure = null;
            }
        }

        public PushFolder Freeze()
            => new(Tag, Folder, Relative, CopiedUtc, ScanUtc, ScanNewFiles, Failed, FailureUtc, Failure, Bytes);

        private static DateTimeOffset Earlier(DateTimeOffset? current, DateTimeOffset candidate)
            => current is null || candidate < current ? candidate : current.Value;
    }

    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s+(?<rest>.*)$")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^OK copied:\s+\[(?<lib>[^\]]+)\]\s+(?<path>.+?)(?:\s+\((?<bytes>\d+)\s+bytes\))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopiedLine();

    [GeneratedRegex(@"^OK\s+\[(?<lib>[^\]]+)\]\s+(?<path>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SyncedLine();

    [GeneratedRegex(@"^PLEX scan asked \((?<count>\d+) new file(?:s|\(s\))\):\s+\[(?<lib>[^\]]+)\]\s+(?<path>.+?)(?:\s+via\s+\S+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScanLine();

    [GeneratedRegex(@"^run done\s+\[(?<lib>[^\]]+)\]\s+ok=(?<ok>\d+)\s+failed=(?<failed>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoneLine();

    [GeneratedRegex(@"^(?<path>.+?)\s+\(\d+\s+bytes\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BytesSuffix();

    [GeneratedRegex(@"\[(?<lib>[^\]]+)\]\s*(?<path>.*)$")]
    private static partial Regex LibraryTag();
}
