using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;
using SyncSentry.Rclone;
using SyncSentry.Security;
using SyncSentry.Services;

namespace SyncSentry.Jobs;

public sealed record CopyCard(
    string SourcePath,
    string DestinationPath,
    string SourceLabel,
    string DestinationLabel,
    long Bytes,
    long TotalBytes,
    long FilesDone,
    long FilesTotal,
    double Speed,
    int? EtaSeconds,
    int Errors,
    double? ElapsedSeconds,
    bool FromLog,
    string? Note);

public sealed record LiveFileRow(
    string Name,
    string SourcePath,
    string DestinationPath,
    string SourceLabel,
    string DestinationLabel,
    long Size,
    long Bytes,
    int Percent,
    double Speed,
    int? EtaSeconds);

public sealed record FinishedFileRow(
    string Name,
    long Size,
    DateTimeOffset? CompletedAt,
    bool Ok,
    string? Error,
    string SourceLabel,
    string DestinationLabel,
    string SourcePath,
    string DestinationPath);

public sealed record LiveCopySnapshot(
    DateTimeOffset? UpdatedUtc,
    RcAccess Access,
    string? Message,
    IReadOnlyList<CopyCard> Copies,
    IReadOnlyList<LiveFileRow> Files,
    IReadOnlyList<FinishedFileRow> Finished,
    DateTimeOffset? LastActivityUtc)
{
    public static LiveCopySnapshot Empty { get; } = new(null, RcAccess.Ok, null, [], [], [], null);
}

public sealed class LiveCopyBoard
{
    private readonly object _gate = new();
    private LiveCopySnapshot _current = LiveCopySnapshot.Empty;

    public LiveCopySnapshot Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public void Publish(LiveCopySnapshot next)
    {
        lock (_gate)
            _current = next;
    }
}

public sealed class LiveCopyPoller(
    IServiceScopeFactory scopes,
    IRcloneRc rclone,
    ISecretProtector secrets,
    LiveBoard mounts,
    LiveCopyBoard board,
    IConfiguration configuration,
    TimeProvider clock) : BackgroundService
{
    private int _busy;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!AppFlags.Workers(configuration))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 0)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // The page shows the last good snapshot. A bad poll must not stop the loop.
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var context = await db.CreateDbContextAsync(cancellationToken);
        var settings = await context.Settings.OrderBy(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
            return;

        var mountViews = mounts.Current.Mounts;
        var now = clock.GetUtcNow();
        var password = Unprotect(settings.RcPasswordProtected);
        RcSnapshot snapshot;
        if (password.Failed)
            snapshot = new RcSnapshot(false, "The saved rclone password could not be read.", [], null, RcAccess.Failed);
        else
            snapshot = await rclone.GetSnapshotAsync(settings.RcUrl, settings.RcUser, password.Value, cancellationToken);

        var copies = new List<CopyCard>();
        var files = new List<LiveFileRow>();
        if (snapshot.Access == RcAccess.Ok && snapshot.Global is not null && HasActivity(snapshot.Global))
            AddStats(snapshot.Global, copies, files, mountViews, fromLog: false, note: null);

        if (copies.Count == 0)
        {
            foreach (var job in snapshot.Jobs.Where(item => !item.Finished && item.Stats is not null && HasActivity(item.Stats)))
                AddStats(job.Stats!, copies, files, mountViews, fromLog: false, note: null);
        }

        if (snapshot.Access != RcAccess.Ok)
        {
            foreach (var job in mounts.Current.Jobs.Where(item => item.Origin == "log" && item.Status == "running"))
            {
                var definition = await context.Jobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == job.DefinitionId, cancellationToken);
                copies.Add(new CopyCard(
                    definition?.Source ?? "",
                    definition?.Destination ?? "",
                    MountLabels.Label(definition?.Source, mountViews),
                    MountLabels.Label(definition?.Destination, mountViews),
                    job.Bytes,
                    job.TotalBytes,
                    job.Files,
                    job.TotalFiles,
                    job.Speed,
                    job.EtaSeconds,
                    job.Errors,
                    null,
                    true,
                    "Shown from the log file, because rclone's status page did not answer."));
            }
        }

        var finished = (snapshot.Finished ?? [])
            .Take(50)
            .Select(item => new FinishedFileRow(
                item.Name,
                item.Size,
                item.CompletedAt,
                item.Ok,
                item.Error,
                MountLabels.Label(item.Source, mountViews),
                MountLabels.Label(item.Destination, mountViews),
                item.Source,
                item.Destination))
            .ToArray();

        var previous = board.Current;
        var active = copies.Count > 0 || files.Count > 0;
        var last = active ? now : previous.LastActivityUtc;
        board.Publish(new LiveCopySnapshot(now, snapshot.Access, snapshot.Error, copies, files, finished, last));
    }

    private static bool HasActivity(RcStats stats)
        => stats.Transferring.Count > 0 || stats.Speed > 1 || (stats.TotalBytes > 0 && stats.Bytes > 0 && stats.Bytes < stats.TotalBytes);

    private static void AddStats(RcStats stats, List<CopyCard> copies, List<LiveFileRow> files, IReadOnlyList<MountView> mounts, bool fromLog, string? note)
    {
        var groups = stats.Transferring
            .GroupBy(item => item.Source + "\n" + item.Destination)
            .ToList();
        if (groups.Count == 0)
        {
            copies.Add(new CopyCard("", "", "Unknown place", "Unknown place", stats.Bytes, stats.TotalBytes, stats.Files, stats.TotalFiles, stats.Speed, stats.EtaSeconds, stats.Errors, stats.ElapsedSeconds, fromLog, note));
            return;
        }

        var first = groups.Count == 1;
        foreach (var group in groups)
        {
            var sample = group.First();
            copies.Add(new CopyCard(
                sample.Source,
                sample.Destination,
                MountLabels.Label(sample.Source, mounts),
                MountLabels.Label(sample.Destination, mounts),
                first ? stats.Bytes : group.Sum(item => item.Bytes),
                first ? stats.TotalBytes : group.Sum(item => item.Size),
                first ? stats.Files : group.Count(),
                first ? stats.TotalFiles : group.Count(),
                first ? stats.Speed : group.Sum(item => item.Speed),
                first ? stats.EtaSeconds : sample.EtaSeconds,
                stats.Errors,
                stats.ElapsedSeconds,
                fromLog,
                note));
            foreach (var file in group)
            {
                files.Add(new LiveFileRow(
                    file.Name,
                    file.Source,
                    file.Destination,
                    MountLabels.Label(string.IsNullOrEmpty(file.Source) ? file.Name : file.Source, mounts),
                    MountLabels.Label(string.IsNullOrEmpty(file.Destination) ? file.Name : file.Destination, mounts),
                    file.Size,
                    file.Bytes,
                    file.Percentage ?? 0,
                    file.Speed,
                    file.EtaSeconds));
            }
        }
    }

    private (string? Value, bool Failed) Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return (null, false);
        try
        {
            return (secrets.Unprotect(protectedValue), false);
        }
        catch (Exception ex) when (Crypto.IsProtectionFailure(ex))
        {
            return (null, true);
        }
    }
}
