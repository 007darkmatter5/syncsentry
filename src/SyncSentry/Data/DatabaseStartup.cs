using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SyncSentry.Demo;
using SyncSentry.Security;
using SyncSentry.Services;
using SyncSentry.Setup;

namespace SyncSentry.Data;

public static class DatabaseStartup
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Auth.AdminRole))
            await roles.CreateAsync(new IdentityRole(Auth.AdminRole));

        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        if (!await db.Settings.AnyAsync())
        {
            db.Settings.Add(new InstanceSetting { UpdatedUtc = now });
        }

        if (!await db.WatchedMounts.AnyAsync())
        {
            db.WatchedMounts.Add(new WatchedMount { Name = "User shares", Path = "/mnt/user", Enabled = true });
            db.WatchedMounts.Add(new WatchedMount { Name = "UNASPRO Media", Path = "/mnt/remotes/UNASPRO_Media", Enabled = true });
        }

        if (!await db.LogSources.AnyAsync())
        {
            db.LogSources.Add(new LogSource
            {
                Name = "initial-copy",
                Path = "/logs/mediamanny/initial-copy.log",
                Enabled = true
            });
        }

        await db.SaveChangesAsync();

        if (!await db.OutboxPairs.AnyAsync())
        {
            db.OutboxPairs.Add(new OutboxPair
            {
                Name = "Media to UNAS",
                SourcePath = "/mnt/user/data/media",
                Destination = "/mnt/remotes/UNASPRO_Media/media",
                DestinationKind = "path",
                Enabled = true,
                SampleJson = "[]",
                Status = "idle"
            });
        }

        await db.SaveChangesAsync();

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var setting = await db.Settings.OrderBy(item => item.Id).FirstAsync();
        ApplyEnvironment(setting, configuration, protector, now);
        await db.SaveChangesAsync();

        if (AppFlags.Demo(configuration))
            await ApplyDemoAsync(db, setting, now);

        if (!await db.SetupStates.AnyAsync())
        {
            var hasUser = await db.Users.AnyAsync();
            var hasRc = SetupGuide.HasRcIdentity(setting.RcUser, setting.RcPasswordProtected);
            var helperPath = setting.TailscaleStatusPath;
            var helperThere = Rules.AbsolutePath(helperPath)
                && !helperPath.StartsWith("/mnt", StringComparison.Ordinal)
                && File.Exists(helperPath);
            db.SetupStates.Add(SetupGuide.InitialState(hasUser, hasRc, helperThere, now));
            await db.SaveChangesAsync();
        }
    }

    public static void ApplyEnvironment(InstanceSetting setting, IConfiguration configuration, ISecretProtector protector, DateTimeOffset now)
    {
        var changed = false;
        changed |= Set(configuration["SyncSentry:Rclone:RcUrl"], value =>
        {
            if (Rules.RcUrl(value))
                setting.RcUrl = value;
        });
        changed |= Set(configuration["SyncSentry:Rclone:RcUser"], value => setting.RcUser = value);
        var password = configuration["SyncSentry:Rclone:RcPassword"];
        if (!string.IsNullOrWhiteSpace(password))
        {
            setting.RcPasswordProtected = protector.Protect(password.Trim());
            changed = true;
        }

        changed |= Set(configuration["SyncSentry:Ntfy:ServerUrl"], value =>
        {
            if (Rules.NtfyServer(value))
                setting.NtfyServerUrl = value;
        });
        changed |= Set(configuration["SyncSentry:Ntfy:Topic"], value =>
        {
            if (Rules.NtfyTopic(value))
                setting.NtfyTopic = value;
        });
        var token = configuration["SyncSentry:Ntfy:Token"];
        if (!string.IsNullOrWhiteSpace(token))
        {
            setting.NtfyTokenProtected = protector.Protect(token.Trim());
            changed = true;
        }

        if (changed)
            setting.UpdatedUtc = now;
    }

    private static async Task ApplyDemoAsync(ApplicationDbContext db, InstanceSetting setting, DateTimeOffset now)
    {
        DemoInbox.Start();
        DemoFiles.Write();
        setting.NtfyServerUrl = DemoInbox.Url ?? "";
        setting.NtfyTopic = "syncsentry-demo";
        setting.TailscaleStatusPath = Path.Combine(DemoFiles.Root, "tailscale.json");
        setting.TriggerDirectory = Path.Combine(DemoFiles.Root, "triggers");
        setting.UpdatedUtc = now;
        Directory.CreateDirectory(setting.TriggerDirectory);

        var log = await db.LogSources.FirstOrDefaultAsync();
        if (log is not null)
            log.Path = Path.Combine(DemoFiles.Root, "initial-copy.log");

        var pair = await db.OutboxPairs.FirstOrDefaultAsync(item => item.DestinationKind == "path");
        if (pair is not null)
        {
            pair.SourcePath = Path.Combine(DemoFiles.Root, "src");
            pair.Destination = Path.Combine(DemoFiles.Root, "dst");
            pair.ForceRefresh = true;
        }

        if (!await db.OutboxPairs.AnyAsync(item => item.DestinationKind == "remote"))
        {
            db.OutboxPairs.Add(new OutboxPair
            {
                Name = "Media via rclone",
                SourcePath = Path.Combine(DemoFiles.Root, "src"),
                Destination = "unas:media",
                DestinationKind = "remote",
                Enabled = true,
                ForceRefresh = true,
                SampleJson = "[]",
                Status = "idle"
            });
        }

        var demoCopy = await db.Jobs.FirstOrDefaultAsync(item => item.StartMode == "rc");
        if (demoCopy is null)
        {
            demoCopy = new SyncJobDefinition { Id = Guid.NewGuid(), StartMode = "rc", Operation = "copy" };
            db.Jobs.Add(demoCopy);
        }

        demoCopy.Name = "Movies";
        demoCopy.Enabled = true;
        demoCopy.Source = "/mnt/remotes/UNASPRO_Media/Movies";
        demoCopy.Destination = "/mnt/user/data/media/Movies";

        if (!await db.JobRuns.AnyAsync())
        {
            db.JobRuns.Add(new JobRun
            {
                Name = "Earlier media copy",
                Origin = "log",
                ExternalId = "log:history",
                Status = "completed",
                BytesTransferred = 12L * 1024 * 1024 * 1024,
                TotalBytes = 12L * 1024 * 1024 * 1024,
                FilesTransferred = 40,
                TotalFiles = 40,
                SpeedBytesPerSecond = 20d * 1024 * 1024,
                StartedUtc = now.AddHours(-5),
                FinishedUtc = now.AddHours(-4),
                UpdatedUtc = now.AddHours(-4)
            });
        }

        await db.SaveChangesAsync();
    }

    private static bool Set(string? value, Action<string> apply)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        apply(value.Trim());
        return true;
    }
}
