using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SyncSentry.Data;
using SyncSentry.Rclone;

namespace SyncSentry.Jobs;

public sealed class TransferLog(IDbContextFactory<ApplicationDbContext> factory, ILogger<TransferLog> logger)
{
    public async Task RememberAsync(
        IReadOnlyList<RcFinished> finished,
        int historyDays,
        DateTimeOffset now,
        DateTimeOffset? processStart,
        CancellationToken cancellationToken)
    {
        if (finished.Count == 0 && historyDays <= 0)
            return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (finished.Count > 0)
        {
            var stored = await db.TransferEvents.ToListAsync(cancellationToken);
            var days = Math.Clamp(historyDays <= 0 ? 30 : historyDays, 1, 3650);
            var cutoff = now.AddDays(-days);
            foreach (var item in finished)
            {
                if (NoiseFiles.IsJunk(item.Name) || TransferRules.IsRecheck(item) || TransferRules.IsBeforeProcess(item, processStart))
                    continue;
                var match = stored.FirstOrDefault(row => TransferRules.AlreadyRecorded(item, [row], cutoff));
                if (match is not null)
                {
                    FillMissingPaths(match, item, stored);
                    continue;
                }

                var row = new TransferEvent
                {
                    DedupeKey = TransferRules.DedupeKey(item),
                    Name = Trim(item.Name, 500),
                    Size = item.Size,
                    CompletedUtc = item.CompletedAt ?? now,
                    Ok = item.Ok,
                    Error = item.Error,
                    SourcePath = Trim(item.Source, 500),
                    DestinationPath = Trim(item.Destination, 500)
                };
                db.TransferEvents.Add(row);
                stored.Add(row);
            }
        }

        var keepDays = Math.Clamp(historyDays <= 0 ? 30 : historyDays, 1, 3650);
        var staleCutoff = now.AddDays(-keepDays);
        var stamps = await db.TransferEvents.AsNoTracking().Select(item => new { item.Id, item.CompletedUtc }).ToListAsync(cancellationToken);
        var stale = stamps.Where(item => item.CompletedUtc < staleCutoff).Select(item => item.Id).ToArray();
        if (stale.Length > 0)
            await db.TransferEvents.Where(item => stale.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CleanupDuplicatesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.TransferEvents.ToListAsync(cancellationToken);
        var remove = new List<TransferEvent>();
        foreach (var group in rows.GroupBy(row => TransferRules.Identity(row.Name, row.Size, row.Ok)))
        {
            var keeper = TransferRules.ChooseKeeper(group);
            remove.AddRange(group.Where(row => row.Id != keeper.Id));
        }

        if (remove.Count > 0)
            db.TransferEvents.RemoveRange(remove);
        await db.SaveChangesAsync(cancellationToken);

        var changed = false;
        foreach (var row in rows.Where(row => !remove.Contains(row)))
        {
            var key = TransferRules.DedupeKey(row.Name, row.Size, row.Ok);
            if (row.DedupeKey == key)
                continue;
            row.DedupeKey = key;
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(cancellationToken);
        if (remove.Count > 0)
            logger.LogInformation("Removed {Count} duplicate finished-copy rows.", remove.Count);
        return remove.Count;
    }

    public async Task<IReadOnlyList<HistoryRow>> RecentAsync(bool failedOnly, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.TransferEvents.AsNoTracking().AsQueryable();
        if (failedOnly)
            query = query.Where(item => !item.Ok);
        var rows = await query.OrderByDescending(item => item.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken);
        return Project(rows);
    }

    public async Task<IReadOnlyList<HistoryRow>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.TransferEvents.AsNoTracking()
            .OrderByDescending(item => item.Id)
            .Take(Math.Clamp(take, 1, 8000))
            .ToListAsync(cancellationToken);
        return Project(rows);
    }

    private static IReadOnlyList<HistoryRow> Project(IEnumerable<TransferEvent> rows)
        => TransferRules.Collapse(rows.Where(item => !NoiseFiles.IsJunk(item.Name)))
            .OrderByDescending(item => item.CompletedUtc)
            .ThenBy(item => item.Id)
            .Select(item => new HistoryRow(item.Name, item.Size, item.CompletedUtc, item.Ok, item.Error, item.SourcePath, item.DestinationPath))
            .ToArray();

    private static void FillMissingPaths(TransferEvent row, RcFinished item, IReadOnlyList<TransferEvent> stored)
    {
        if (string.IsNullOrWhiteSpace(row.SourcePath) && !string.IsNullOrWhiteSpace(item.Source))
            row.SourcePath = Trim(item.Source, 500);
        if (string.IsNullOrWhiteSpace(row.DestinationPath) && !string.IsNullOrWhiteSpace(item.Destination))
            row.DestinationPath = Trim(item.Destination, 500);
        var key = TransferRules.DedupeKey(row.Name, row.Size, row.Ok);
        if (row.DedupeKey != key && stored.All(other => ReferenceEquals(other, row) || other.DedupeKey != key))
            row.DedupeKey = key;
    }

    private static string Trim(string? value, int max)
    {
        var text = value ?? "";
        return text.Length <= max ? text : text[..max];
    }
}
