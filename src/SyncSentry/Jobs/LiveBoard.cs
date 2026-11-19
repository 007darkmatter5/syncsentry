using SyncSentry.Host;

namespace SyncSentry.Jobs;

public sealed record MountView(
    string Name,
    string Path,
    string State,
    string FsType,
    string Source,
    long? Total,
    long? Used,
    long? Free,
    string? Detail,
    bool Configured);

public sealed record JobView(
    long? RunId,
    Guid? DefinitionId,
    string Name,
    string Origin,
    string Status,
    long Bytes,
    long TotalBytes,
    long Files,
    long TotalFiles,
    double Speed,
    int? EtaSeconds,
    int Errors,
    string? LastError,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<string> Transferring);

public sealed record Snapshot(
    DateTimeOffset? UpdatedUtc,
    IReadOnlyList<MountView> Mounts,
    TailscaleView Tailscale,
    IReadOnlyList<JobView> Jobs,
    bool RcReachable,
    string? RcError)
{
    public static Snapshot Empty { get; } = new(null, [], new TailscaleView(null, false, "none", null), [], false, null);
}

public sealed class LiveBoard
{
    private readonly object _gate = new();
    private Snapshot _current = Snapshot.Empty;

    public Snapshot Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public void Publish(Snapshot next)
    {
        lock (_gate)
            _current = next;
    }
}

public static class JobGroups
{
    public static string For(Guid id) => "syncsentry-" + id.ToString("N");
}
