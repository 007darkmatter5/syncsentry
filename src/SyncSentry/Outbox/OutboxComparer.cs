namespace SyncSentry.Outbox;

public sealed record FileFingerprint(string RelativePath, long Size, long MtimeSeconds);

public sealed record PendingFile(string Path, long Size, string Reason);

public sealed record OutboxDiff(
    int Count,
    long Bytes,
    bool Incomplete,
    string? Error,
    IReadOnlyList<PendingFile> Sample);

public static class OutboxComparer
{
    public const int SampleLimit = 400;
    public const int MtimeSkewSeconds = 2;

    public static OutboxDiff Compare(
        IReadOnlyList<FileFingerprint> source,
        IReadOnlyList<FileFingerprint> destination,
        bool sourceTruncated,
        bool destinationTruncated)
    {
        if (sourceTruncated || destinationTruncated)
        {
            return new OutboxDiff(
                0,
                0,
                true,
                "The scan hit its file or time cap, so pending files were not counted. Narrow the pair or raise the cap.",
                []);
        }

        var dest = new Dictionary<string, FileFingerprint>(destination.Count, StringComparer.Ordinal);
        foreach (var file in destination)
            dest[Normalize(file.RelativePath)] = file;

        var sample = new List<PendingFile>();
        long bytes = 0;
        var count = 0;
        foreach (var file in source)
        {
            var path = Normalize(file.RelativePath);
            if (path.Length == 0 || path.StartsWith("../", StringComparison.Ordinal) || path == "..")
                continue;
            string? reason = null;
            if (!dest.TryGetValue(path, out var other))
                reason = "missing";
            else if (other.Size != file.Size)
                reason = "size";
            else if (file.MtimeSeconds > other.MtimeSeconds + MtimeSkewSeconds)
                reason = "newer";
            if (reason is null)
                continue;
            count++;
            bytes += Math.Max(0, file.Size);
            if (sample.Count < SampleLimit)
                sample.Add(new PendingFile(path, file.Size, reason));
        }

        return new OutboxDiff(count, bytes, false, null, sample);
    }

    public static string Normalize(string path)
    {
        var value = path.Replace('\\', '/').Trim();
        while (value.StartsWith("./", StringComparison.Ordinal))
            value = value[2..];
        return value.TrimStart('/');
    }
}
