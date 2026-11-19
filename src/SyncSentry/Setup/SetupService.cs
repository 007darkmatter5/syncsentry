using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Rclone;
using SyncSentry.Security;
using SyncSentry.Services;

namespace SyncSentry.Setup;

public sealed class WizardModel
{
    public SetupState State { get; init; } = new();
    public bool HasAccount { get; init; }
    public string RcUrl { get; init; } = "";
    public string RcUser { get; init; } = "";
    public bool HasRcPassword { get; init; }
    public string NtfyServerUrl { get; init; } = "";
    public string NtfyTopic { get; init; } = "";
    public bool HasNtfyToken { get; init; }
    public bool AlertJobFailed { get; init; } = true;
    public bool AlertJobCompleted { get; init; }
    public bool AlertMountUnresponsive { get; init; } = true;
    public bool AlertMountUnmounted { get; init; } = true;
    public bool AlertAcceptRoutesOff { get; init; } = true;
    public string BandwidthMode { get; init; } = "off";
    public double BandwidthMegabytes { get; init; } = 1;
    public int ScheduleStartHour { get; init; } = 7;
    public int ScheduleEndHour { get; init; } = 23;
    public double ScheduleMegabytes { get; init; } = 1;
    public string TailscaleStatusPath { get; init; } = "/config/host/tailscale.json";
    public string SuggestedAppdata { get; init; } = SetupGuide.DefaultAppdata;
    public List<CopyDraft> Pairs { get; init; } = [];
    public EnvironmentReport Environment { get; init; } = SetupGuide.Inspect([]);
    public HelperView Helper { get; init; } = new("red", "", "", null);
    public IReadOnlyList<SetupGap> Gaps { get; init; } = [];
    public IReadOnlyList<ChecklistItem> Checklist { get; init; } = [];
}

public sealed class SetupService(
    IDbContextFactory<ApplicationDbContext> factory,
    ISecretProtector secrets,
    IMountTable mounts,
    IRcloneRc rclone,
    SettingsStore settings,
    TimeProvider clock)
{
    public async Task<WizardModel> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.AsNoTracking().OrderBy(item => item.Id).FirstAsync(cancellationToken);
        var state = await db.SetupStates.AsNoTracking().OrderBy(item => item.Id).FirstAsync(cancellationToken);
        var jobs = await db.Jobs.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        var hasAccount = await db.Users.AnyAsync(cancellationToken);
        var table = mounts.Read();
        var environment = SetupGuide.Inspect(table);
        var helper = ReadHelper(setting.TailscaleStatusPath, table);
        var pairs = jobs.Select(ToDraft).ToList();
        var hasRc = SetupGuide.HasRcIdentity(setting.RcUser, setting.RcPasswordProtected);
        var guessed = SetupGuide.GuessAppdata(table);
        var appdata = SetupGuide.HostPath(state.AppdataHostPath)
            ? state.AppdataHostPath
            : guessed ?? SetupGuide.DefaultAppdata;
        var gaps = SetupGuide.Gaps(state, environment, hasRc, helper, pairs.Count);
        return new WizardModel
        {
            State = state,
            HasAccount = hasAccount,
            RcUrl = setting.RcUrl,
            RcUser = setting.RcUser,
            HasRcPassword = setting.RcPasswordProtected.Length > 0,
            NtfyServerUrl = setting.NtfyServerUrl,
            NtfyTopic = setting.NtfyTopic,
            HasNtfyToken = setting.NtfyTokenProtected.Length > 0,
            AlertJobFailed = setting.AlertJobFailed,
            AlertJobCompleted = setting.AlertJobCompleted,
            AlertMountUnresponsive = setting.AlertMountUnresponsive,
            AlertMountUnmounted = setting.AlertMountUnmounted,
            AlertAcceptRoutesOff = setting.AlertAcceptRoutesOff,
            BandwidthMode = setting.BandwidthMode,
            BandwidthMegabytes = setting.BandwidthMegabytes,
            ScheduleStartHour = setting.ScheduleStartHour,
            ScheduleEndHour = setting.ScheduleEndHour,
            ScheduleMegabytes = setting.ScheduleMegabytes,
            TailscaleStatusPath = setting.TailscaleStatusPath,
            SuggestedAppdata = appdata,
            Pairs = pairs,
            Environment = environment,
            Helper = helper,
            Gaps = gaps,
            Checklist = SetupGuide.Checklist(state, hasAccount, environment, hasRc, helper, pairs.Count, !string.IsNullOrWhiteSpace(setting.NtfyTopic), setting.BandwidthMode)
        };
    }

    public async Task SkipRcloneAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var state = await db.SetupStates.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        state.RcloneSkipped = true;
        state.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> SaveRcloneAsync(string url, string user, string? newPassword, CancellationToken cancellationToken = default)
    {
        if (!Rules.RcUrl(url))
            return "The rclone address has to be a plain http(s) URL with no password in it.";
        if (!string.IsNullOrEmpty(newPassword) && newPassword.Length < 8)
            return "Use a password of at least 8 characters, or generate one.";
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        var state = await db.SetupStates.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        setting.RcUrl = url.Trim();
        setting.RcUser = string.IsNullOrWhiteSpace(user) ? SetupGuide.DefaultRcUser : user.Trim();
        if (!string.IsNullOrEmpty(newPassword))
            setting.RcPasswordProtected = secrets.Protect(newPassword);
        setting.UpdatedUtc = clock.GetUtcNow();
        state.RcloneSkipped = false;
        state.UpdatedUtc = setting.UpdatedUtc;
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<RcVersionProbe> TestRcloneAsync(string url, string user, string? typedPassword, CancellationToken cancellationToken = default)
    {
        var password = typedPassword;
        if (string.IsNullOrEmpty(password))
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var setting = await db.Settings.AsNoTracking().OrderBy(item => item.Id).FirstAsync(cancellationToken);
            if (!string.IsNullOrEmpty(setting.RcPasswordProtected))
            {
                try
                {
                    password = secrets.Unprotect(setting.RcPasswordProtected);
                }
                catch (Exception ex) when (Crypto.IsProtectionFailure(ex))
                {
                    return new RcVersionProbe(RcAccess.Unauthorized, null);
                }
            }
        }

        return await rclone.ProbeVersionAsync(url, user, password, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> SaveAppdataAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!SetupGuide.HostPath(path))
            return "The appdata path has to be a full host path without spaces or quotes, such as /mnt/user/appdata/syncsentry.";
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var state = await db.SetupStates.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        state.AppdataHostPath = path.Trim();
        state.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public HelperView ReadHelper(string? path, IReadOnlyList<MountEntry>? table = null)
    {
        table ??= mounts.Read();
        if (!Rules.AbsolutePath(path))
            return new HelperView("red", "The helper status path is not set.", "Settings should point it at /config/host/tailscale.json.", null);
        var covering = MountInfo.FindCovering(table, path!);
        if (MountRisk.PathNeedsIsolation(path!, covering))
            return new HelperView("yellow", "The status file should stay on appdata, not on a share.", "Leave the path at /config/host/tailscale.json so a frozen mount cannot stall this page.", null);
        var exists = false;
        DateTimeOffset? written = null;
        TailscaleView? view = null;
        try
        {
            exists = File.Exists(path);
            if (exists)
            {
                written = new DateTimeOffset(DateTime.SpecifyKind(File.GetLastWriteTimeUtc(path!), DateTimeKind.Utc));
                view = TailscaleReader.ReadFile(path!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HelperView("red", "The status file could not be read.", "Check that /config is the appdata folder and that the helper can write host/tailscale.json there.", null);
        }

        return SetupGuide.DescribeHelper(exists, written, clock.GetUtcNow(), view);
    }

    public async Task<string?> SavePairsAsync(IReadOnlyList<CopyDraft> drafts, CancellationToken cancellationToken = default)
    {
        var error = SetupGuide.ValidatePairs(drafts);
        if (error is not null)
            return error;
        var kept = drafts.Where(item =>
            !string.IsNullOrWhiteSpace(item.Name)
            || !string.IsNullOrWhiteSpace(item.Source)
            || !string.IsNullOrWhiteSpace(item.Destination)).ToArray();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var jobs = await db.Jobs.ToListAsync(cancellationToken);
        var ids = kept.Select(item => item.Id).ToHashSet();
        foreach (var extra in jobs.Where(item => !ids.Contains(item.Id)).ToArray())
            db.Jobs.Remove(extra);
        foreach (var draft in kept)
        {
            var job = jobs.FirstOrDefault(item => item.Id == draft.Id);
            if (job is null)
            {
                job = new SyncJobDefinition
                {
                    Id = draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id,
                    StartMode = Rules.ScriptKey(draft.ScriptKey) ? "trigger" : "rc",
                    Operation = "copy"
                };
                db.Jobs.Add(job);
            }

            SetupGuide.ApplyPair(job, draft);
        }

        var state = await db.SetupStates.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        state.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<string?> SaveAlertsAsync(
        string server,
        string topic,
        string? newToken,
        bool failed,
        bool completed,
        bool stale,
        bool disconnected,
        bool routesOff,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(server) && !Rules.NtfyServer(server))
            return "The ntfy address has to be a plain http(s) URL.";
        if (!string.IsNullOrWhiteSpace(topic) && !Rules.NtfyTopic(topic))
            return "The topic can use letters, numbers, _ and -.";
        if (newToken is { Length: > 256 })
            return "That token is too long.";
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        setting.NtfyServerUrl = server.Trim();
        setting.NtfyTopic = topic.Trim();
        if (!string.IsNullOrEmpty(newToken))
            setting.NtfyTokenProtected = secrets.Protect(newToken);
        setting.AlertJobFailed = failed;
        setting.AlertJobCompleted = completed;
        setting.AlertMountUnresponsive = stale;
        setting.AlertMountUnmounted = disconnected;
        setting.AlertAcceptRoutesOff = routesOff;
        setting.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public Task<string> SendTestAsync(CancellationToken cancellationToken = default)
        => settings.SendTestAsync(cancellationToken);

    public async Task SaveSpeedAsync(string mode, double custom, double scheduled, int startHour, int endHour, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        setting.BandwidthMode = mode is "custom" or "schedule" ? mode : "off";
        setting.BandwidthMegabytes = Math.Clamp(custom, 0, 10_000);
        setting.ScheduleMegabytes = Math.Clamp(scheduled, 0, 10_000);
        setting.ScheduleStartHour = Rules.Clamp(startHour, 0, 23);
        setting.ScheduleEndHour = Rules.Clamp(endHour, 0, 24);
        setting.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFinishedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var state = await db.SetupStates.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        state.Finished = true;
        state.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private static CopyDraft ToDraft(SyncJobDefinition job) => new()
    {
        Id = job.Id,
        Name = job.Name,
        Source = job.Source,
        Destination = job.Destination,
        ScriptKey = job.ScriptKey,
        IncludeRules = job.IncludeRules,
        ExcludeRules = job.ExcludeRules,
        StartMode = job.StartMode,
        Operation = job.Operation,
        LogSourceId = job.LogSourceId
    };
}
