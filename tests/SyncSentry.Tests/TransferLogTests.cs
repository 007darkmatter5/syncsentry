using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSentry.Data;
using SyncSentry.Jobs;
using SyncSentry.Rclone;

namespace SyncSentry.Tests;

public class TransferLogTests
{
    [Fact]
    public async Task A_restart_that_relists_the_same_files_does_not_insert_them_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-xfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "t.db")).Options;
        await using (var db = new ApplicationDbContext(options))
            await db.Database.MigrateAsync();

        var log = new TransferLog(new Factory(options), NullLogger<TransferLog>.Instance);
        var firstSeen = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
        var restart = DateTimeOffset.Parse("2026-11-20T15:02:30Z");
        var original = new RcFinished(
            "A Few Good Men (1992)/A Few Good Men (1992).mkv",
            8_600_000_000,
            firstSeen,
            true,
            null,
            "/mnt/remotes/UNASPRO_Media/Movies",
            "/mnt/user/data/media/Movies",
            false,
            8_600_000_000);
        await log.RememberAsync([original], 30, firstSeen, processStart: firstSeen.AddHours(-1), CancellationToken.None);

        var relisted = new RcFinished(
            "A Few Good Men (1992)/A Few Good Men (1992).mkv",
            8_600_000_000,
            restart,
            true,
            null,
            "",
            "",
            false,
            8_600_000_000);
        await log.RememberAsync([relisted], 30, restart, processStart: restart.AddMinutes(-1), CancellationToken.None);

        var shown = await log.ListAsync(20);
        var row = Assert.Single(shown);
        Assert.Equal(original.Name, row.Name);
        Assert.Equal("/mnt/remotes/UNASPRO_Media/Movies", row.SourcePath);
        Assert.Equal("/mnt/user/data/media/Movies", row.DestinationPath);
        Assert.Equal(firstSeen, row.CompletedUtc);

        await using var check = new ApplicationDbContext(options);
        Assert.Equal(1, await check.TransferEvents.CountAsync());
    }

    [Fact]
    public async Task Cleanup_keeps_the_earliest_row_that_has_paths_and_is_idempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "t.db")).Options;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.TransferEvents.AddRange(
                new TransferEvent
                {
                    DedupeKey = "old-with-time",
                    Name = "Movies/A.mkv",
                    Size = 10,
                    CompletedUtc = DateTimeOffset.Parse("2026-11-20T10:00:00Z"),
                    Ok = true,
                    SourcePath = "/mnt/remotes/UNASPRO_Media/Movies",
                    DestinationPath = "/mnt/user/data/media/Movies"
                },
                new TransferEvent
                {
                    DedupeKey = "Movies/A.mkv|10|none|||True",
                    Name = "Movies/A.mkv",
                    Size = 10,
                    CompletedUtc = DateTimeOffset.Parse("2026-11-20T15:02:00Z"),
                    Ok = true,
                    SourcePath = "",
                    DestinationPath = ""
                });
            await db.SaveChangesAsync();
        }

        var log = new TransferLog(new Factory(options), NullLogger<TransferLog>.Instance);
        Assert.Equal(1, await log.CleanupDuplicatesAsync());
        Assert.Equal(0, await log.CleanupDuplicatesAsync());

        var shown = await log.ListAsync(10);
        var row = Assert.Single(shown);
        Assert.Equal("/mnt/remotes/UNASPRO_Media/Movies", row.SourcePath);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:00:00Z"), row.CompletedUtc);
    }

    [Fact]
    public async Task Checks_and_completions_from_before_this_process_are_not_stored()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "t.db")).Options;
        await using (var db = new ApplicationDbContext(options))
            await db.Database.MigrateAsync();

        var now = DateTimeOffset.Parse("2026-11-20T15:03:00Z");
        var log = new TransferLog(new Factory(options), NullLogger<TransferLog>.Instance);
        await log.RememberAsync(
            [
                new RcFinished("Movies/Checked.mkv", 50, now, true, null, "", "", true, 0),
                new RcFinished("Movies/Empty-bytes.mkv", 50, now, true, null, "", "", false, 0),
                new RcFinished("Movies/Earlier.mkv", 50, now.AddHours(-4), true, null, "/src", "/dst", false, 50),
                new RcFinished("Movies/This-run.mkv", 50, now.AddSeconds(-20), true, null, "/src", "/dst", false, 50)
            ],
            30,
            now,
            processStart: now.AddMinutes(-2),
            CancellationToken.None);

        var shown = await log.ListAsync(10);
        var row = Assert.Single(shown);
        Assert.Equal("Movies/This-run.mkv", row.Name);
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
