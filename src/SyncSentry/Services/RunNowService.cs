using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Jobs;
using SyncSentry.Rclone;
using SyncSentry.Security;

namespace SyncSentry.Services;

public sealed record RunNowResult(bool Ok, string Message);

public sealed class RunNowService(
    IDbContextFactory<ApplicationDbContext> factory,
    IRcloneRc rclone,
    IMountTable mounts,
    LiveBoard board,
    ISecretProtector secrets,
    TimeProvider clock)
{
    public async Task<RunNowResult> StartAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.Include(item => item.LogSource).OrderBy(item => item.Id).FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        var settings = await db.Settings.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        if (job is null)
            return new RunNowResult(false, "That job no longer exists.");

        var live = board.Current.Jobs.Any(item => item.DefinitionId == job.Id && item.Status == "running");
        var logFresh = LogIsFresh(job, settings, mounts.Read());
        var pending = job.StartMode == "trigger" && TriggerFiles.IsPending(settings.TriggerDirectory, job.Id);
        var decision = RunNowGuard.Decide(job.Enabled, live || logFresh, pending);
        if (!decision.Allowed)
            return new RunNowResult(false, decision.Reason ?? "That job cannot be started.");

        if (job.StartMode == "rc")
        {
            if (job.Operation is not ("copy" or "sync"))
                return new RunNowResult(false, "Operation must be copy or sync.");
            var password = Unprotect(settings.RcPasswordProtected);
            if (password.Failed)
                return new RunNowResult(false, "Saved rclone password could not be read.");
            var started = await rclone.StartAsync(settings.RcUrl, settings.RcUser, password.Value, new RcStartRequest(
                job.Operation,
                job.Source,
                job.Destination,
                JobGroups.For(job.Id),
                Rules.SplitRules(job.IncludeRules),
                Rules.SplitRules(job.ExcludeRules)), cancellationToken);
            if (started.AlreadyRunning)
                return new RunNowResult(false, "That job is already running.");
            return started.Ok
                ? new RunNowResult(true, "Started through rclone remote control.")
                : new RunNowResult(false, started.Error ?? "rclone did not start the job.");
        }

        if (job.StartMode != "trigger")
            return new RunNowResult(false, "This job has no start mode.");

        var write = TriggerFiles.Write(settings.TriggerDirectory, job.Id, job.Name, job.ScriptKey, clock.GetUtcNow());
        if (write.AlreadyPending)
            return new RunNowResult(false, write.Error ?? "That job is already waiting.");
        return write.Ok
            ? new RunNowResult(true, "Trigger filed. The host script will run the allowlisted job.")
            : new RunNowResult(false, write.Error ?? "Could not write the trigger.");
    }

    private bool LogIsFresh(SyncJobDefinition job, InstanceSetting settings, IReadOnlyList<MountEntry> table)
    {
        var path = job.LogSource?.Path;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var covering = MountInfo.FindCovering(table, path);
        if (MountRisk.PathNeedsIsolation(path, covering))
            return false;
        try
        {
            if (!File.Exists(path))
                return false;
            var written = new DateTimeOffset(DateTime.SpecifyKind(File.GetLastWriteTimeUtc(path), DateTimeKind.Utc));
            return clock.GetUtcNow() - written <= TimeSpan.FromSeconds(Rules.Clamp(settings.LogStaleSeconds, 30, 3600));
        }
        catch (IOException)
        {
            return false;
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
