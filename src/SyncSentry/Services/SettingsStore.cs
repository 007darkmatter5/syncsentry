using Microsoft.EntityFrameworkCore;
using SyncSentry.Alerts;
using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Security;

namespace SyncSentry.Services;

public sealed class GeneralEdit
{
    public string RcUrl { get; set; } = "";
    public string RcUser { get; set; } = "";
    public string NewRcPassword { get; set; } = "";
    public bool ClearRcPassword { get; set; }
    public bool HasRcPassword { get; set; }
    public string NtfyServerUrl { get; set; } = "";
    public string NtfyTopic { get; set; } = "";
    public string NewNtfyToken { get; set; } = "";
    public bool ClearNtfyToken { get; set; }
    public bool HasNtfyToken { get; set; }
    public bool AlertJobFailed { get; set; }
    public bool AlertJobCompleted { get; set; }
    public bool AlertMountUnresponsive { get; set; }
    public bool AlertMountUnmounted { get; set; }
    public bool AlertAcceptRoutesOff { get; set; }
    public int ProbeTimeoutSeconds { get; set; }
    public int PollSeconds { get; set; }
    public int OutboxCacheMinutes { get; set; }
    public int LogStaleSeconds { get; set; }
    public int ScanMaxFiles { get; set; }
    public int ScanSeconds { get; set; }
    public string TailscaleStatusPath { get; set; } = "";
    public string TailscaleSocketPath { get; set; } = "";
    public string TriggerDirectory { get; set; } = "";
    public int HistoryDays { get; set; } = 30;
    public string BandwidthMode { get; set; } = "off";
    public double BandwidthMegabytes { get; set; } = 1;
    public int ScheduleStartHour { get; set; } = 7;
    public int ScheduleEndHour { get; set; } = 23;
    public double ScheduleMegabytes { get; set; } = 1;
}

public sealed class MountEdit
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class LogEdit
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class JobEdit
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string StartMode { get; set; } = "trigger";
    public string Operation { get; set; } = "copy";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string ScriptKey { get; set; } = "";
    public string IncludeRules { get; set; } = "";
    public string ExcludeRules { get; set; } = "";
    public int? LogSourceId { get; set; }
}

public sealed class OutboxEdit
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Destination { get; set; } = "";
    public string DestinationKind { get; set; } = "path";
    public bool Enabled { get; set; } = true;
}

public sealed class SettingsStore(IDbContextFactory<ApplicationDbContext> factory, ISecretProtector secrets, INtfyClient ntfy, TimeProvider clock)
{
    public async Task<GeneralEdit> LoadGeneralAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.AsNoTracking().OrderBy(item => item.Id).FirstAsync(cancellationToken);
        return new GeneralEdit
        {
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
            ProbeTimeoutSeconds = setting.ProbeTimeoutSeconds,
            PollSeconds = setting.PollSeconds,
            OutboxCacheMinutes = setting.OutboxCacheMinutes,
            LogStaleSeconds = setting.LogStaleSeconds,
            ScanMaxFiles = setting.ScanMaxFiles,
            ScanSeconds = setting.ScanSeconds,
            TailscaleStatusPath = setting.TailscaleStatusPath,
            TailscaleSocketPath = setting.TailscaleSocketPath,
            TriggerDirectory = setting.TriggerDirectory,
            HistoryDays = setting.HistoryDays,
            BandwidthMode = setting.BandwidthMode,
            BandwidthMegabytes = setting.BandwidthMegabytes,
            ScheduleStartHour = setting.ScheduleStartHour,
            ScheduleEndHour = setting.ScheduleEndHour,
            ScheduleMegabytes = setting.ScheduleMegabytes
        };
    }

    public async Task<string?> SaveGeneralAsync(GeneralEdit edit, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(edit.RcUrl) && !Rules.RcUrl(edit.RcUrl))
            return "rclone URL must be a plain http(s) address with no password in it.";
        if (!string.IsNullOrWhiteSpace(edit.NtfyServerUrl) && !Rules.NtfyServer(edit.NtfyServerUrl))
            return "Ntfy URL must be a plain http(s) address.";
        if (!string.IsNullOrWhiteSpace(edit.NtfyTopic) && !Rules.NtfyTopic(edit.NtfyTopic))
            return "Ntfy topic must be letters, numbers, _ or -.";
        if (!string.IsNullOrWhiteSpace(edit.TailscaleStatusPath) && !Rules.AbsolutePath(edit.TailscaleStatusPath))
            return "Tailscale status path must be absolute.";
        if (!string.IsNullOrWhiteSpace(edit.TailscaleSocketPath) && !Rules.AbsolutePath(edit.TailscaleSocketPath))
            return "Tailscale socket path must be absolute.";
        if (!Rules.AbsolutePath(edit.TriggerDirectory))
            return "Trigger directory must be absolute.";
        if (edit.NewRcPassword.Length > 0 && edit.NewRcPassword.Length < 4)
            return "rclone password is too short.";
        if (edit.NewNtfyToken.Length > 256)
            return "Ntfy token is too long.";

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        setting.RcUrl = edit.RcUrl.Trim();
        setting.RcUser = edit.RcUser.Trim();
        if (edit.ClearRcPassword)
            setting.RcPasswordProtected = "";
        if (!string.IsNullOrEmpty(edit.NewRcPassword))
            setting.RcPasswordProtected = secrets.Protect(edit.NewRcPassword);
        setting.NtfyServerUrl = edit.NtfyServerUrl.Trim();
        setting.NtfyTopic = edit.NtfyTopic.Trim();
        if (edit.ClearNtfyToken)
            setting.NtfyTokenProtected = "";
        if (!string.IsNullOrEmpty(edit.NewNtfyToken))
            setting.NtfyTokenProtected = secrets.Protect(edit.NewNtfyToken);
        setting.AlertJobFailed = edit.AlertJobFailed;
        setting.AlertJobCompleted = edit.AlertJobCompleted;
        setting.AlertMountUnresponsive = edit.AlertMountUnresponsive;
        setting.AlertMountUnmounted = edit.AlertMountUnmounted;
        setting.AlertAcceptRoutesOff = edit.AlertAcceptRoutesOff;
        setting.ProbeTimeoutSeconds = Rules.Clamp(edit.ProbeTimeoutSeconds, 1, 15);
        setting.PollSeconds = Rules.Clamp(edit.PollSeconds, 5, 300);
        setting.OutboxCacheMinutes = Rules.Clamp(edit.OutboxCacheMinutes, 1, 24 * 60);
        setting.LogStaleSeconds = Rules.Clamp(edit.LogStaleSeconds, 30, 3600);
        setting.ScanMaxFiles = Rules.Clamp(edit.ScanMaxFiles, 1000, 500_000);
        setting.ScanSeconds = Rules.Clamp(edit.ScanSeconds, 5, 300);
        setting.TailscaleStatusPath = edit.TailscaleStatusPath.Trim();
        setting.TailscaleSocketPath = edit.TailscaleSocketPath.Trim();
        setting.TriggerDirectory = edit.TriggerDirectory.Trim();
        setting.HistoryDays = Rules.Clamp(edit.HistoryDays, 1, 3650);
        setting.BandwidthMode = edit.BandwidthMode is "custom" or "schedule" ? edit.BandwidthMode : "off";
        setting.BandwidthMegabytes = Math.Clamp(edit.BandwidthMegabytes, 0, 10_000);
        setting.ScheduleStartHour = Rules.Clamp(edit.ScheduleStartHour, 0, 23);
        setting.ScheduleEndHour = Rules.Clamp(edit.ScheduleEndHour, 0, 24);
        setting.ScheduleMegabytes = Math.Clamp(edit.ScheduleMegabytes, 0, 10_000);
        setting.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<List<MountEdit>> LoadMountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.WatchedMounts.AsNoTracking().OrderBy(item => item.Name)
            .Select(item => new MountEdit { Name = item.Name, Path = item.Path, Enabled = item.Enabled })
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> SaveMountsAsync(IReadOnlyList<MountEdit> mounts, CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            if (string.IsNullOrWhiteSpace(mount.Name) || mount.Name.Length > 120)
                return "Each mount needs a name.";
            if (!Rules.AbsolutePath(mount.Path))
                return "Mount paths must be absolute.";
            if (!seen.Add(MountInfo.Normalize(mount.Path)))
                return "Mount paths must be unique.";
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.WatchedMounts.ExecuteDeleteAsync(cancellationToken);
        foreach (var mount in mounts)
        {
            db.WatchedMounts.Add(new WatchedMount
            {
                Name = mount.Name.Trim(),
                Path = MountInfo.Normalize(mount.Path),
                Enabled = mount.Enabled
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<List<LogEdit>> LoadLogsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.LogSources.AsNoTracking().OrderBy(item => item.Name)
            .Select(item => new LogEdit { Id = item.Id, Name = item.Name, Path = item.Path, Enabled = item.Enabled })
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> SaveLogsAsync(IReadOnlyList<LogEdit> logs, CancellationToken cancellationToken = default)
    {
        foreach (var log in logs)
        {
            if (string.IsNullOrWhiteSpace(log.Name))
                return "Each log needs a name.";
            if (!Rules.AbsolutePath(log.Path))
                return "Log paths must be absolute.";
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.LogSources.ToListAsync(cancellationToken);
        var keep = new HashSet<int>();
        foreach (var log in logs)
        {
            var row = existing.FirstOrDefault(item => item.Id == log.Id && log.Id != 0);
            if (row is null)
            {
                row = new LogSource();
                db.LogSources.Add(row);
            }

            row.Name = log.Name.Trim();
            row.Path = log.Path.Trim();
            row.Enabled = log.Enabled;
            if (row.Id != 0)
                keep.Add(row.Id);
        }

        foreach (var row in existing.Where(item => !keep.Contains(item.Id)).ToList())
        {
            var used = await db.Jobs.AnyAsync(item => item.LogSourceId == row.Id, cancellationToken);
            if (used)
                return "A job still uses " + row.Name + ".";
            db.LogSources.Remove(row);
        }

        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<List<JobEdit>> LoadJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Jobs.AsNoTracking().OrderBy(item => item.Name)
            .Select(item => new JobEdit
            {
                Id = item.Id,
                Name = item.Name,
                Enabled = item.Enabled,
                StartMode = item.StartMode,
                Operation = item.Operation,
                Source = item.Source,
                Destination = item.Destination,
                ScriptKey = item.ScriptKey,
                IncludeRules = item.IncludeRules,
                ExcludeRules = item.ExcludeRules,
                LogSourceId = item.LogSourceId
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> SaveJobAsync(JobEdit edit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Length > 120)
            return "Job name is required.";
        if (edit.StartMode is not ("rc" or "trigger"))
            return "Start mode must be rclone RC or a host trigger.";
        if (edit.Operation is not ("copy" or "sync"))
            return "Operation must be copy or sync.";
        if (edit.StartMode == "trigger" && !Rules.ScriptKey(edit.ScriptKey))
            return "Script key must be lowercase letters, numbers, and dashes.";
        if (edit.StartMode == "rc" && (string.IsNullOrWhiteSpace(edit.Source) || string.IsNullOrWhiteSpace(edit.Destination)))
            return "rclone jobs need a source and a destination.";
        foreach (var rule in edit.IncludeRules.Split('\n').Concat(edit.ExcludeRules.Split('\n')))
        {
            if (string.IsNullOrWhiteSpace(rule))
                continue;
            if (!Rules.FilterRule(rule.Trim()))
                return "Filter rules must be a single line each.";
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = edit.Id == Guid.Empty
            ? null
            : await db.Jobs.FirstOrDefaultAsync(item => item.Id == edit.Id, cancellationToken);
        if (row is null)
        {
            row = new SyncJobDefinition { Id = Guid.NewGuid() };
            db.Jobs.Add(row);
        }

        row.Name = edit.Name.Trim();
        row.Enabled = edit.Enabled;
        row.StartMode = edit.StartMode;
        row.Operation = edit.Operation;
        row.Source = edit.Source.Trim();
        row.Destination = edit.Destination.Trim();
        row.ScriptKey = edit.ScriptKey.Trim();
        row.IncludeRules = edit.IncludeRules.Trim();
        row.ExcludeRules = edit.ExcludeRules.Trim();
        row.LogSourceId = edit.LogSourceId is 0 ? null : edit.LogSourceId;
        await db.SaveChangesAsync(cancellationToken);
        edit.Id = row.Id;
        return null;
    }

    public async Task DeleteJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Jobs.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null)
            return;
        db.Jobs.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<OutboxEdit>> LoadOutboxAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.OutboxPairs.AsNoTracking().OrderBy(item => item.Name)
            .Select(item => new OutboxEdit
            {
                Id = item.Id,
                Name = item.Name,
                SourcePath = item.SourcePath,
                Destination = item.Destination,
                DestinationKind = item.DestinationKind,
                Enabled = item.Enabled
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> SaveOutboxAsync(OutboxEdit edit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(edit.Name))
            return "Outbox pair needs a name.";
        if (!Rules.AbsolutePath(edit.SourcePath))
            return "Source path must be absolute.";
        if (edit.DestinationKind == "path")
        {
            if (!Rules.AbsolutePath(edit.Destination))
                return "Destination path must be absolute.";
        }
        else if (edit.DestinationKind == "remote")
        {
            if (!Rules.TrySplitRemote(edit.Destination, out _, out _))
                return "Remote destination must look like name:path.";
        }
        else
        {
            return "Destination kind must be a path or an rclone remote.";
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = edit.Id == 0
            ? null
            : await db.OutboxPairs.FirstOrDefaultAsync(item => item.Id == edit.Id, cancellationToken);
        if (row is null)
        {
            row = new OutboxPair();
            db.OutboxPairs.Add(row);
        }

        row.Name = edit.Name.Trim();
        row.SourcePath = edit.SourcePath.Trim();
        row.Destination = edit.Destination.Trim();
        row.DestinationKind = edit.DestinationKind;
        row.Enabled = edit.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        edit.Id = row.Id;
        return null;
    }

    public async Task DeleteOutboxAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.OutboxPairs.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null)
            return;
        db.OutboxPairs.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveBandwidthAsync(string mode, double megabytes, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        setting.BandwidthMode = mode is "custom" or "schedule" ? mode : "off";
        if (mode == "custom")
            setting.BandwidthMegabytes = Math.Clamp(megabytes, 0, 10_000);
        setting.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> SendTestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(setting.NtfyServerUrl) || string.IsNullOrWhiteSpace(setting.NtfyTopic))
            return "Save an ntfy server and topic first.";
        string? token = null;
        if (!string.IsNullOrEmpty(setting.NtfyTokenProtected))
        {
            try
            {
                token = secrets.Unprotect(setting.NtfyTokenProtected);
            }
            catch (Exception ex) when (Crypto.IsProtectionFailure(ex))
            {
                return "Saved ntfy token could not be read.";
            }
        }

        var result = await ntfy.SendAsync(
            new NtfyTarget(setting.NtfyServerUrl, setting.NtfyTopic, token),
            "SyncSentry test",
            "SyncSentry can reach this ntfy topic.",
            cancellationToken);
        db.AlertEvents.Add(new AlertEvent
        {
            Key = "test:" + clock.GetUtcNow().ToUnixTimeSeconds(),
            Kind = "test",
            Title = "SyncSentry test",
            Message = "Test notification",
            Delivered = result.Ok,
            Detail = SecretRedactor.ForDisplay(result.Detail, 200),
            CreatedUtc = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        return result.Ok ? "Test notification sent." : result.Detail;
    }

    public async Task<IReadOnlyList<AlertEvent>> RecentAlertsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AlertEvents.AsNoTracking().OrderByDescending(item => item.Id).Take(15).ToListAsync(cancellationToken);
    }

    public async Task RequestRefreshAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.OutboxPairs.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null)
            return;
        row.ForceRefresh = true;
        await db.SaveChangesAsync(cancellationToken);
    }
}
