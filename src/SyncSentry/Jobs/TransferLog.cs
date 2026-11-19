using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;
using SyncSentry.Rclone;

namespace SyncSentry.Jobs;

public sealed class TransferLog(IDbContextFactory<ApplicationDbContext> factory)
{
    public async Task RememberAsync(IReadOnlyList<RcFinished> finished, int historyDays, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (finished.Count == 0 && historyDays <= 0)
            return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (finished.Count > 0)
        {
            var rows = finished.Select(item => new
            {
                Item = item,
                Key = DedupeKey(item),
                When = item.CompletedAt ?? now
            }).ToArray();
            var keys = rows.Select(item => item.Key).ToArray();
            var existing = await db.TransferEvents.Where(item => keys.Contains(item.DedupeKey)).Select(item => item.DedupeKey).ToListAsync(cancellationToken);
            var known = existing.ToHashSet(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!known.Add(row.Key))
                    continue;
                db.TransferEvents.Add(new TransferEvent
                {
                    DedupeKey = row.Key,
                    Name = Trim(row.Item.Name, 500),
                    Size = row.Item.Size,
                    CompletedUtc = row.When,
                    Ok = row.Item.Ok,
                    Error = row.Item.Error,
                    SourcePath = Trim(row.Item.Source, 500),
                    DestinationPath = Trim(row.Item.Destination, 500)
                });
            }
        }

        var days = Math.Clamp(historyDays, 1, 3650);
        var cutoff = now.AddDays(-days);
        var stamps = await db.TransferEvents.AsNoTracking().Select(item => new { item.Id, item.CompletedUtc }).ToListAsync(cancellationToken);
        var stale = stamps.Where(item => item.CompletedUtc < cutoff).Select(item => item.Id).ToArray();
        if (stale.Length > 0)
            await db.TransferEvents.Where(item => stale.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryRow>> RecentAsync(bool failedOnly, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.TransferEvents.AsNoTracking().AsQueryable();
        if (failedOnly)
            query = query.Where(item => !item.Ok);
        var rows = await query.OrderByDescending(item => item.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken);
        return rows.Select(item => new HistoryRow(item.Name, item.Size, item.CompletedUtc, item.Ok, item.Error, item.SourcePath, item.DestinationPath)).ToArray();
    }

    public static string DedupeKey(RcFinished item)
    {
        var when = item.CompletedAt?.ToUnixTimeSeconds().ToString() ?? "none";
        var text = item.Name + "|" + item.Size + "|" + when + "|" + item.Source + "|" + item.Destination + "|" + item.Ok;
        return text.Length <= 240 ? text : text[..240];
    }

    private static string Trim(string? value, int max)
    {
        var text = value ?? "";
        return text.Length <= max ? text : text[..max];
    }
}
