using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SyncSentry.Alerts;
using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Rclone;
using SyncSentry.Security;
using SyncSentry.Services;

namespace SyncSentry.Jobs;

public sealed class MonitorTick(
    IDbContextFactory<ApplicationDbContext> factory,
    LiveBoard board,
    IMountTable mountTable,
    IPathProbe probe,
    IRcloneRc rclone,
    ILogReader logs,
    ITailscaleReader tailscale,
    AlertDispatcher alerts,
    ISecretProtector secrets,
    TimeProvider clock,
    ILogger<MonitorTick> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await db.Settings.OrderBy(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
            return;

        var now = clock.GetUtcNow();
        var timeout = TimeSpan.FromSeconds(Rules.Clamp(settings.ProbeTimeoutSeconds, 1, 15));
        var stale = TimeSpan.FromSeconds(Rules.Clamp(settings.LogStaleSeconds, 30, 3600));
        var toggles = new AlertToggles(
            settings.AlertJobFailed,
            settings.AlertJobCompleted,
            settings.AlertMountUnresponsive,
            settings.AlertMountUnmounted,
            settings.AlertAcceptRoutesOff);
        var (target, blocked) = BuildTarget(settings);

        var configured = await db.WatchedMounts.AsNoTracking().Where(item => item.Enabled).ToListAsync(cancellationToken);
        var plan = MountPlanner.Plan(configured.Select(item => (item.Name, item.Path)), mountTable.Read());
        var probes = new ConcurrentDictionary<string, ProbeResult>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            plan.Where(item => item.Mounted),
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
            async (item, token) => probes[item.Path] = await probe.ProbeAsync(item.Path, timeout, token));

        var states = await db.MountStates.ToListAsync(cancellationToken);
        var mountViews = new List<MountView>();
        foreach (var item in plan)
        {
            probes.TryGetValue(item.Path, out var space);
            var state = !item.Mounted ? "unmounted" : space?.State ?? "unresponsive";
            var row = states.FirstOrDefault(entry => entry.Path == item.Path);
            var previous = row?.State;
            var since = now;
            if (row is null)
            {
                row = new MountStateRow { Path = item.Path, State = state, SinceUtc = now, UpdatedUtc = now };
                db.MountStates.Add(row);
            }
            else if (!string.Equals(row.State, state, StringComparison.Ordinal))
            {
                row.State = state;
                row.SinceUtc = now;
                row.UpdatedUtc = now;
            }
            else
            {
                since = row.SinceUtc;
                row.UpdatedUtc = now;
            }

            mountViews.Add(new MountView(
                item.Name, item.Path, state, item.FsType, item.Source,
                space?.Total, space?.Used, space?.Free, space?.Detail, item.Configured,
                space?.ResponseMilliseconds));

            foreach (var message in AlertPolicy.ForMount(toggles, item.Name, item.Path, previous, state, since))
                await alerts.DispatchAsync(db, target, message, blocked, cancellationToken);
        }

        var tail = await tailscale.ReadAsync(settings.TailscaleStatusPath, settings.TailscaleSocketPath, cancellationToken);
        var route = await db.RouteStates.OrderBy(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        var previousRoutes = route?.AcceptRoutes;
        var routeSince = now;
        if (route is null)
        {
            db.RouteStates.Add(new RouteStateRow { AcceptRoutes = tail.AcceptRoutes, SinceUtc = now, UpdatedUtc = now });
        }
        else if (route.AcceptRoutes != tail.AcceptRoutes)
        {
            route.AcceptRoutes = tail.AcceptRoutes;
            route.SinceUtc = now;
            route.UpdatedUtc = now;
        }
        else
        {
            routeSince = route.SinceUtc;
            route.UpdatedUtc = now;
        }

        foreach (var message in AlertPolicy.ForRoutes(toggles, previousRoutes, tail.AcceptRoutes, routeSince))
            await alerts.DispatchAsync(db, target, message, blocked, cancellationToken);

        var definitions = await db.Jobs.AsNoTracking().Include(item => item.LogSource).ToListAsync(cancellationToken);
        var sources = await db.LogSources.AsNoTracking().Where(item => item.Enabled).ToListAsync(cancellationToken);
        var openRuns = await db.JobRuns.Where(item => item.Status == "running").ToListAsync(cancellationToken);
        var views = new List<JobView>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var read = await logs.ReadAsync(source.Path, 262_144, timeout, cancellationToken);
            if (!read.Ok || read.Text is null)
                continue;
            var snapshot = RcloneLogParser.Parse(read.Text);
            var status = LogRunAssessor.Assess(snapshot, read.WrittenUtc, now, stale);
            if (status == LogRunAssessor.Idle)
                continue;
            var definition = definitions.FirstOrDefault(item => item.LogSourceId == source.Id);
            var external = "log:" + source.Id;
            seen.Add(external);
            var view = await UpsertAsync(db, openRuns, definition, source.Name, "log", external, status, snapshot.Stats, snapshot.ErrorCount, snapshot.LastError, [], now, toggles, target, blocked, cancellationToken);
            if (view is not null)
                views.Add(view);
        }

        var password = Unprotect(settings.RcPasswordProtected);
        var rcError = password.Failed ? "Saved rclone password could not be read." : null;
        RcSnapshot rc = password.Failed
            ? new RcSnapshot(false, rcError, [], null)
            : await rclone.GetSnapshotAsync(settings.RcUrl, settings.RcUser, password.Value, cancellationToken);
        if (rc.Reachable)
        {
            foreach (var job in rc.Jobs)
            {
                var external = job.Id == 0 ? "rc:global" : "rc:" + job.Id;
                seen.Add(external);
                var status = !job.Finished
                    ? "running"
                    : job.Success && string.IsNullOrEmpty(job.Error) ? "completed" : "failed";
                var definition = definitions.FirstOrDefault(item =>
                    item.StartMode == "rc" && string.Equals(JobGroups.For(item.Id), job.Group, StringComparison.Ordinal));
                var name = definition?.Name ?? (string.IsNullOrWhiteSpace(job.Group) ? "rclone" : job.Group);
                var stats = job.Stats;
                var view = await UpsertAsync(
                    db, openRuns, definition, name, "rc", external, status,
                    stats is null ? null : new StatsLine(stats.Bytes, stats.TotalBytes, stats.TotalBytes == 0 ? 0 : stats.Bytes * 100.0 / stats.TotalBytes, stats.Speed, stats.EtaSeconds, stats.Files, stats.TotalFiles, ""),
                    stats?.Errors ?? (string.IsNullOrEmpty(job.Error) ? 0 : 1),
                    job.Error ?? stats?.LastError,
                    stats?.Transferring.Select(item => item.Name).Where(item => item.Length > 0).Take(8).ToArray() ?? [],
                    now, toggles, target, blocked, cancellationToken);
                if (view is not null)
                    views.Add(view);
            }

            foreach (var orphan in openRuns.Where(item => item.Origin == "rc" && !seen.Contains(item.ExternalId)).ToList())
            {
                var previous = orphan.Status;
                orphan.Status = "completed";
                orphan.FinishedUtc = now;
                orphan.UpdatedUtc = now;
                foreach (var message in AlertPolicy.ForJob(toggles, orphan.Name, previous, orphan.Status, orphan.ExternalId, orphan.StartedUtc))
                    await alerts.DispatchAsync(db, target, message, blocked, cancellationToken);
            }
        }
        else
        {
            rcError ??= rc.Error;
        }

        foreach (var job in definitions.Where(item => item.Enabled && item.StartMode == "trigger" && TriggerFiles.IsPending(settings.TriggerDirectory, item.Id)))
        {
            if (views.Any(item => item.DefinitionId == job.Id && item.Status == "running"))
                continue;
            views.Add(new JobView(null, job.Id, job.Name, "trigger", "running", 0, 0, 0, 0, 0, null, 0, "Waiting for the host script.", now, []));
        }

        await db.SaveChangesAsync(cancellationToken);
        await TrimAsync(db, cancellationToken);
        board.Publish(new Snapshot(now, mountViews, tail, views, rc.Reachable, rcError));
        logger.LogDebug("Monitor updated {Mounts} mounts and {Jobs} jobs.", mountViews.Count, views.Count);
    }

    private async Task<JobView?> UpsertAsync(
        ApplicationDbContext db,
        List<JobRun> openRuns,
        SyncJobDefinition? definition,
        string name,
        string origin,
        string external,
        string status,
        StatsLine? stats,
        int errors,
        string? lastError,
        IReadOnlyList<string> transferring,
        DateTimeOffset now,
        AlertToggles toggles,
        NtfyTarget? target,
        string? blocked,
        CancellationToken cancellationToken)
    {
        var open = openRuns.FirstOrDefault(item => item.ExternalId == external && item.Status == "running");
        if (status != "running" && open is null)
        {
            var exists = await db.JobRuns.AnyAsync(item => item.ExternalId == external, cancellationToken);
            if (exists)
                return null;
        }

        var previous = open?.Status;
        var run = open ?? new JobRun
        {
            JobDefinitionId = definition?.Id,
            Name = name,
            Origin = origin,
            ExternalId = external,
            StartedUtc = now
        };
        if (open is null)
        {
            db.JobRuns.Add(run);
            openRuns.Add(run);
        }

        run.Name = name;
        run.JobDefinitionId ??= definition?.Id;
        run.Status = status;
        if (stats is not null)
        {
            if (!(status == "running" && stats.Bytes == 0 && run.BytesTransferred > 0))
            {
                run.BytesTransferred = stats.Bytes;
                run.TotalBytes = stats.TotalBytes;
                run.SpeedBytesPerSecond = stats.SpeedBytesPerSecond ?? 0;
                run.EtaSeconds = stats.EtaSeconds;
            }

            if (stats.Files is not null)
                run.FilesTransferred = stats.Files.Value;
            if (stats.TotalFiles is not null)
                run.TotalFiles = stats.TotalFiles.Value;
        }

        run.ErrorCount = errors;
        run.LastError = SecretRedactor.ForDisplay(lastError, 300);
        if (string.IsNullOrWhiteSpace(run.LastError))
            run.LastError = null;
        run.Transferring = transferring.Count == 0 ? null : string.Join("\n", transferring);
        run.UpdatedUtc = now;
        if (status != "running")
            run.FinishedUtc ??= now;

        foreach (var message in AlertPolicy.ForJob(toggles, name, previous, status, external, run.StartedUtc))
            await alerts.DispatchAsync(db, target, message, blocked, cancellationToken);

        return new JobView(
            run.Id == 0 ? null : run.Id,
            definition?.Id ?? run.JobDefinitionId,
            name,
            origin,
            status,
            run.BytesTransferred,
            run.TotalBytes,
            run.FilesTransferred,
            run.TotalFiles,
            run.SpeedBytesPerSecond,
            run.EtaSeconds,
            run.ErrorCount,
            run.LastError,
            now,
            transferring);
    }

    private (NtfyTarget? Target, string? Blocked) BuildTarget(InstanceSetting settings)
    {
        if (string.IsNullOrWhiteSpace(settings.NtfyServerUrl) || string.IsNullOrWhiteSpace(settings.NtfyTopic))
            return (null, null);
        if (!string.IsNullOrEmpty(settings.NtfyTokenProtected))
        {
            var token = Unprotect(settings.NtfyTokenProtected);
            if (token.Failed)
                return (null, "Saved ntfy token could not be read.");
            return (new NtfyTarget(settings.NtfyServerUrl, settings.NtfyTopic, token.Value), null);
        }

        return (new NtfyTarget(settings.NtfyServerUrl, settings.NtfyTopic, null), null);
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

    private static async Task TrimAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        if (await db.JobRuns.CountAsync(cancellationToken) > 600)
        {
            var keep = await db.JobRuns.OrderByDescending(item => item.Id).Select(item => item.Id).Take(500).ToListAsync(cancellationToken);
            await db.JobRuns.Where(item => !keep.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        if (await db.AlertEvents.CountAsync(cancellationToken) > 400)
        {
            var keep = await db.AlertEvents.OrderByDescending(item => item.Id).Select(item => item.Id).Take(300).ToListAsync(cancellationToken);
            await db.AlertEvents.Where(item => !keep.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }
}

public sealed class MonitorService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<MonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!AppFlags.Workers(configuration))
            return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(20);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var tick = scope.ServiceProvider.GetRequiredService<MonitorTick>();
                await tick.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Monitor tick failed ({Type}): {Detail}", ex.GetType().Name, SecretRedactor.ForDisplay(ex.Message, 180));
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var seconds = await db.Settings.OrderBy(item => item.Id).Select(item => item.PollSeconds).FirstOrDefaultAsync(stoppingToken);
                if (seconds > 0)
                    delay = TimeSpan.FromSeconds(Rules.Clamp(seconds, 5, 300));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delay = TimeSpan.FromSeconds(20);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
