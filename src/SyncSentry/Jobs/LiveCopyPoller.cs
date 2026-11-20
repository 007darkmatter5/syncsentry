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

public sealed record ServerFace(
    string Hostname,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    string LimitText,
    bool? AcceptRoutes,
    bool RoutesStale,
    TimeSpan Uptime,
    string AppVersion,
    string? RcloneVersion);

public sealed record LiveCopySnapshot(
    DateTimeOffset? UpdatedUtc,
    RcAccess Access,
    string? Message,
    IReadOnlyList<CopyCard> Copies,
    IReadOnlyList<LiveFileRow> Files,
    IReadOnlyList<FinishedFileRow> Finished,
    DateTimeOffset? LastActivityUtc,
    IReadOnlyList<FolderPanel>? Panels = null,
    ServerFace? Server = null)
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
    TransferLog transfers,
    IConfiguration configuration,
    ILogger<LiveCopyPoller> logger,
    TimeProvider clock) : BackgroundService
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private string _lastSource = "";
    private string _lastDestination = "";
    private DateTimeOffset _lastPlaceUtc;
    private int _busy;
    private double? _lastElapsed;
    private string? _lastApplied;

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
                catch (Exception ex)
                {
                    logger.LogWarning("Live copy poll failed ({Type}): {Detail}", ex.GetType().Name, SecretRedactor.ForDisplay(ex.Message, 240));
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
                var definition = await context.Jobs.AsNoTracking().OrderBy(item => item.Id).FirstOrDefaultAsync(item => item.Id == job.DefinitionId, cancellationToken);
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

        var finishedSource = AttachPlaces(snapshot.Finished ?? [], copies, now);
        var finished = finishedSource
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

        var historyDays = settings.HistoryDays <= 0 ? 30 : settings.HistoryDays;
        var elapsed = snapshot.Global?.ElapsedSeconds ?? copies.Select(item => item.ElapsedSeconds).FirstOrDefault(item => item is > 0);
        DateTimeOffset? processStart = elapsed is > 0 ? now.AddSeconds(-elapsed.Value) : null;
        await transfers.RememberAsync(finishedSource, historyDays, now, processStart, cancellationToken);
        var history = await transfers.RecentAsync(false, 400, cancellationToken);
        var saved = await context.Jobs.AsNoTracking().Where(item => item.Enabled).ToListAsync(cancellationToken);
        var panels = FolderBoards.Build(copies, files, history, saved, mountViews);
        var limit = await ApplyLimitAsync(settings, snapshot.Global?.ElapsedSeconds, password.Value, cancellationToken);
        var version = snapshot.Access == RcAccess.Ok
            ? await rclone.GetVersionAsync(settings.RcUrl, settings.RcUser, password.Value, cancellationToken)
            : null;
        var (down, up) = Rates(copies);
        var tail = mounts.Current.Tailscale;
        var previous = board.Current;
        var active = copies.Count > 0 || files.Count > 0;
        var lastSeen = active ? now : previous.LastActivityUtc;
        board.Publish(new LiveCopySnapshot(
            now,
            snapshot.Access,
            snapshot.Error,
            copies,
            files,
            finished,
            lastSeen,
            panels,
            new ServerFace(
                Environment.MachineName,
                down,
                up,
                limit,
                tail.AcceptRoutes,
                tail.Stale,
                now - Started,
                AppVersion(),
                version)));
    }

    private async Task<string> ApplyLimitAsync(InstanceSetting settings, double? elapsed, string? password, CancellationToken cancellationToken)
    {
        var wanted = BandwidthPlan.DesiredRate(
            settings.BandwidthMode,
            settings.BandwidthMegabytes,
            settings.ScheduleMegabytes,
            settings.ScheduleStartHour,
            settings.ScheduleEndHour,
            clock.GetLocalNow());
        if (!Rules.RcUrl(settings.RcUrl))
            return BandwidthPlan.Display(wanted, wanted == "off" ? -1 : 0);

        var current = await rclone.GetBandwidthAsync(settings.RcUrl, settings.RcUser, password, cancellationToken);
        var freshProcess = BandwidthPlan.IsNewSession(_lastElapsed, elapsed);
        _lastElapsed = elapsed;
        var matches = current.Ok && string.Equals(current.Rate, wanted, StringComparison.OrdinalIgnoreCase);
        if (matches && !freshProcess)
            return BandwidthPlan.Display(current.Rate, current.BytesPerSecond);
        if (!freshProcess && !current.Ok && string.Equals(_lastApplied, wanted, StringComparison.OrdinalIgnoreCase))
            return BandwidthPlan.Display(wanted, -1);

        var applied = await rclone.SetBandwidthAsync(settings.RcUrl, settings.RcUser, password, wanted, cancellationToken);
        _lastApplied = wanted;
        logger.LogInformation("Bandwidth limit set to {Rate}.", wanted);
        return applied.Ok ? BandwidthPlan.Display(applied.Rate, applied.BytesPerSecond) : BandwidthPlan.Display(wanted, -1);
    }

    private static (double Download, double Upload) Rates(IReadOnlyList<CopyCard> copies)
    {
        double down = 0, up = 0;
        foreach (var copy in copies)
        {
            var remoteSource = copy.SourcePath.Contains("/mnt/remotes", StringComparison.Ordinal);
            var remoteDest = copy.DestinationPath.Contains("/mnt/remotes", StringComparison.Ordinal);
            if (remoteSource && !remoteDest)
                down += copy.Speed;
            else if (remoteDest && !remoteSource)
                up += copy.Speed;
            else
                down += copy.Speed;
        }

        return (down, up);
    }

    private static string AppVersion()
    {
        var version = typeof(LiveCopyPoller).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    private IReadOnlyList<RcFinished> AttachPlaces(IReadOnlyList<RcFinished> finished, IReadOnlyList<CopyCard> copies, DateTimeOffset now)
    {
        var known = copies.Where(item => !string.IsNullOrWhiteSpace(item.SourcePath) && !string.IsNullOrWhiteSpace(item.DestinationPath)).ToArray();
        if (known.Length == 1)
        {
            _lastSource = known[0].SourcePath;
            _lastDestination = known[0].DestinationPath;
            _lastPlaceUtc = now;
        }

        var fresh = now - _lastPlaceUtc < TimeSpan.FromMinutes(10) && _lastSource.Length > 0;
        return finished
            .Where(item => !NoiseFiles.IsJunk(item.Name))
            .Select(item =>
            {
                if (!string.IsNullOrWhiteSpace(item.Source) && !string.IsNullOrWhiteSpace(item.Destination))
                    return item;
                if (!fresh)
                    return item;
                return item with
                {
                    Source = string.IsNullOrWhiteSpace(item.Source) ? _lastSource : item.Source,
                    Destination = string.IsNullOrWhiteSpace(item.Destination) ? _lastDestination : item.Destination
                };
            })
            .ToArray();
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
                if (NoiseFiles.IsJunk(file.Name))
                    continue;
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
