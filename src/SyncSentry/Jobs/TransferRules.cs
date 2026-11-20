using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Rclone;

namespace SyncSentry.Jobs;

public readonly record struct CopyIdentity(string Name, long Size, bool Ok);

public static class TransferRules
{
    public static string NormalizeName(string? name)
    {
        var value = (name ?? "").Replace('\\', '/').Trim();
        while (value.StartsWith("./", StringComparison.Ordinal))
            value = value[2..];
        return value.Trim('/');
    }

    public static CopyIdentity Identity(string? name, long size, bool ok)
        => new(NormalizeName(name), size, ok);

    public static string DedupeKey(string? name, long size, bool ok)
    {
        var text = NormalizeName(name) + "|" + size + "|" + (ok ? "1" : "0");
        return text.Length <= 240 ? text : text[..240];
    }

    public static string DedupeKey(RcFinished item)
        => DedupeKey(item.Name, item.Size, item.Ok);

    public static bool IsRecheck(RcFinished item)
    {
        if (item.Checked)
            return true;
        return item.Ok && string.IsNullOrEmpty(item.Error) && item.Bytes == 0 && item.Size > 0;
    }

    public static bool IsBeforeProcess(RcFinished item, DateTimeOffset? processStart)
    {
        if (processStart is null || item.CompletedAt is null)
            return false;
        return item.CompletedAt.Value < processStart.Value.AddSeconds(-2);
    }

    public static bool AlreadyRecorded(RcFinished item, IEnumerable<TransferEvent> existing, DateTimeOffset cutoff)
    {
        var identity = Identity(item.Name, item.Size, item.Ok);
        foreach (var row in existing)
        {
            if (row.CompletedUtc < cutoff)
                continue;
            if (Identity(row.Name, row.Size, row.Ok) != identity)
                continue;
            if (!PlacesCompatible(item.Source, row.SourcePath) || !PlacesCompatible(item.Destination, row.DestinationPath))
                continue;
            return true;
        }

        return false;
    }

    public static bool PlacesCompatible(string? incoming, string? stored)
    {
        if (string.IsNullOrWhiteSpace(incoming) || string.IsNullOrWhiteSpace(stored))
            return true;
        return MountInfo.Normalize(incoming) == MountInfo.Normalize(stored);
    }

    public static bool HasPaths(TransferEvent row)
        => !string.IsNullOrWhiteSpace(row.SourcePath) || !string.IsNullOrWhiteSpace(row.DestinationPath);

    public static TransferEvent ChooseKeeper(IEnumerable<TransferEvent> group)
        => group.OrderByDescending(HasPaths).ThenBy(row => row.Id).First();

    public static IReadOnlyList<TransferEvent> Collapse(IEnumerable<TransferEvent> rows)
        => rows
            .GroupBy(row => Identity(row.Name, row.Size, row.Ok))
            .Select(ChooseKeeper)
            .ToArray();
}
