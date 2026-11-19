using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Rclone;
using SyncSentry.Security;
using SyncSentry.Setup;

namespace SyncSentry.Tests;

public class SetupTests
{
    [Fact]
    public void Read_only_is_the_mount_option_not_the_super_options()
    {
        var text = """
            36 35 98:0 / /mnt/user ro,relatime shared:1 - fuse.shfs shfs rw
            40 35 98:0 / /mnt/remotes rw,relatime - xfs /dev/sda1 rw
            100 1 0:48 /appdata/syncsentry /config rw,relatime - fuse.shfs shfs rw
            """;
        var mounts = MountInfo.Parse(text);
        var user = Assert.Single(mounts, item => item.MountPoint == "/mnt/user");
        Assert.True(user.ReadOnly);
        Assert.False(Assert.Single(mounts, item => item.MountPoint == "/mnt/remotes").ReadOnly);
        Assert.Equal("/mnt/user/appdata/syncsentry", SetupGuide.GuessAppdata(mounts));

        var report = SetupGuide.Inspect(mounts);
        Assert.True(report.UserShares.Ok);
        Assert.False(report.Remotes.Ok);
        Assert.Contains("Read only", report.Remotes.Fix);
        Assert.Contains(report.Shares, item => item.Path == "/mnt/user");
    }

    [Fact]
    public void A_missing_folder_names_the_template_path()
    {
        var report = SetupGuide.Inspect([]);
        Assert.False(report.UserShares.Mapped);
        Assert.Contains("/mnt/user", report.UserShares.Fix);
        Assert.Contains("rslave", report.UserShares.Fix);
        Assert.Contains("Remote mounts", report.Remotes.Fix);
    }

    [Fact]
    public void An_existing_account_is_not_sent_through_the_wizard()
    {
        var now = DateTimeOffset.Parse("2026-11-19T22:00:00Z");
        var fresh = SetupGuide.InitialState(false, false, false, now);
        Assert.False(fresh.Finished);
        Assert.False(fresh.LegacyUpgrade);

        var upgraded = SetupGuide.InitialState(true, true, true, now);
        Assert.True(upgraded.Finished);
        Assert.True(upgraded.LegacyUpgrade);
        Assert.False(upgraded.RcloneSkipped);
        Assert.False(upgraded.HelperSkipped);

        var logOnly = SetupGuide.InitialState(true, false, false, now);
        Assert.True(logOnly.RcloneSkipped);
        Assert.True(logOnly.HelperSkipped);
        var gaps = SetupGuide.Gaps(logOnly, SetupGuide.Inspect([]), false, new HelperView("red", "missing", "", null), 1);
        Assert.Contains(gaps, item => item.Href.Contains("step=2", StringComparison.Ordinal));
        Assert.DoesNotContain(gaps, item => item.Href.Contains("step=3", StringComparison.Ordinal));
        Assert.DoesNotContain(gaps, item => item.Href.Contains("step=4", StringComparison.Ordinal));
    }

    [Fact]
    public void Rclone_snippet_uses_the_requested_flags_and_hides_them_from_a_blank_password()
    {
        var text = SetupGuide.RcloneSnippet("syncsentry", "secret-value", "http://172.17.0.1:5572");
        Assert.Contains("--rc --rc-addr 172.17.0.1:5572 --rc-enable-metrics=false", text, StringComparison.Ordinal);
        Assert.Contains("RCLONE_RC_USER=syncsentry", text, StringComparison.Ordinal);
        Assert.Contains("RCLONE_RC_PASS='secret-value'", text, StringComparison.Ordinal);
        var script = SetupGuide.HelperScript("/mnt/user/appdata/syncsentry-beta");
        Assert.Contains("/mnt/user/appdata/syncsentry-beta", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/mnt/user/appdata/syncsentry\"", script, StringComparison.Ordinal);
        Assert.Contains("write_tailscale", script, StringComparison.Ordinal);
        Assert.Contains("rclone copy", SetupGuide.ExampleCopyScript(), StringComparison.Ordinal);
        Assert.Contains("scriptKey=movies", SetupGuide.AllowlistNote("movies"), StringComparison.Ordinal);
        Assert.Equal("rclone answered (v1.76.0).", SetupGuide.VersionGuidance(RcAccess.Ok, "v1.76.0"));
        Assert.Contains("refused", SetupGuide.VersionGuidance(RcAccess.Unauthorized, null), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("172.17.0.1", SetupGuide.VersionGuidance(RcAccess.Unreachable, null), StringComparison.Ordinal);
    }

    [Fact]
    public void Password_is_long_and_a_copy_pair_rejects_a_relative_path()
    {
        var password = SetupGuide.GeneratePassword();
        Assert.Equal(24, password.Length);
        Assert.Matches("^[A-Za-z0-9]+$", password);
        var bad = new CopyDraft { Name = "Movies", Source = "Movies", Destination = "/mnt/user/data", ScriptKey = "Movies" };
        Assert.NotNull(SetupGuide.ValidatePairs([bad]));
        var good = new CopyDraft { Name = "Movies", Source = "/mnt/remotes/UNASPRO_Media/Movies", Destination = "/mnt/user/data/media/Movies", ScriptKey = "movies" };
        Assert.Null(SetupGuide.ValidatePairs([good]));
    }

    [Fact]
    public async Task Saving_a_copy_does_not_clear_filters_or_an_existing_password()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "t.db")).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.MigrateAsync();
        db.Settings.Add(new InstanceSetting { UpdatedUtc = DateTimeOffset.UtcNow, RcUrl = "http://192.168.1.10:5572", RcUser = "syncsentry", RcPasswordProtected = "enc:keep-me" });
        var id = Guid.NewGuid();
        db.Jobs.Add(new SyncJobDefinition
        {
            Id = id,
            Name = "Movies",
            Source = "/mnt/remotes/UNASPRO_Media/Movies",
            Destination = "/mnt/user/data/media/Movies",
            IncludeRules = "+ *.mkv",
            StartMode = "rc",
            Operation = "copy",
            ScriptKey = ""
        });
        db.SetupStates.Add(new SetupState { UpdatedUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var service = new SetupService(new Factory(options), new PlainProtector(), new EmptyMounts(), new QuietVersion(), new Services.SettingsStore(new Factory(options), new PlainProtector(), new QuietNtfy(), TimeProvider.System), TimeProvider.System);
        var error = await service.SavePairsAsync(
        [
            new CopyDraft
            {
                Id = id,
                Name = "Movies",
                Source = "/mnt/remotes/UNASPRO_Media/Movies",
                Destination = "/mnt/user/data/media/Movies",
                ScriptKey = "movies"
            }
        ]);
        Assert.Null(error);
        await using var check = new ApplicationDbContext(options);
        var job = await check.Jobs.SingleAsync();
        Assert.Equal("+ *.mkv", job.IncludeRules);
        Assert.Equal("trigger", job.StartMode);
        Assert.Equal("movies", job.ScriptKey);
        var saved = await service.SaveRcloneAsync("http://192.168.1.10:5572", "syncsentry", null);
        Assert.Null(saved);
        await using var again = new ApplicationDbContext(options);
        var setting = await again.Settings.SingleAsync();
        Assert.Equal("enc:keep-me", setting.RcPasswordProtected);
        Assert.Equal("http://192.168.1.10:5572", setting.RcUrl);
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string protectedPayload) => protectedPayload["enc:".Length..];
    }

    private sealed class EmptyMounts : IMountTable
    {
        public IReadOnlyList<MountEntry> Read() => [];
    }

    private sealed class QuietVersion : IRcloneRc
    {
        public Task<RcSnapshot> GetSnapshotAsync(string? url, string? user, string? password, CancellationToken cancellationToken) => Task.FromResult(new RcSnapshot(true, null, [], null));
        public Task<RcCallResult> StartAsync(string? url, string? user, string? password, RcStartRequest request, CancellationToken cancellationToken) => Task.FromResult(new RcCallResult(true, false, 1, null));
        public Task<RemoteListResult> ListRemoteAsync(string? url, string? user, string? password, string remote, int maxFiles, CancellationToken cancellationToken) => Task.FromResult(new RemoteListResult(true, false, null, []));
        public Task<BandwidthReading> GetBandwidthAsync(string? url, string? user, string? password, CancellationToken cancellationToken) => Task.FromResult(new BandwidthReading("off", -1, true));
        public Task<BandwidthReading> SetBandwidthAsync(string? url, string? user, string? password, string rate, CancellationToken cancellationToken) => Task.FromResult(new BandwidthReading(rate, -1, true));
        public Task<string?> GetVersionAsync(string? url, string? user, string? password, CancellationToken cancellationToken) => Task.FromResult<string?>("v1.76.0");
        public Task<RcVersionProbe> ProbeVersionAsync(string? url, string? user, string? password, CancellationToken cancellationToken) => Task.FromResult(new RcVersionProbe(RcAccess.Ok, "v1.76.0"));
    }

    private sealed class QuietNtfy : Alerts.INtfyClient
    {
        public Task<Alerts.NtfySendResult> SendAsync(Alerts.NtfyTarget target, string title, string body, CancellationToken cancellationToken)
            => Task.FromResult(new Alerts.NtfySendResult(true, "sent"));
    }
}
