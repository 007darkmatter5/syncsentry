using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Rclone;
using SyncSentry.Security;
using SyncSentry.Services;

namespace SyncSentry.Outbox;

public sealed class OutboxService(
    IDbContextFactory<ApplicationDbContext> factory,
    ITreeScanner scanner,
    IRcloneRc rclone,
    ISecretProtector secrets,
    TimeProvider clock,
    ILogger<OutboxService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ScanAsync(int pairId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ScanCoreAsync(pairId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int?> NextStaleIdAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
            return null;
        var cache = TimeSpan.FromMinutes(Rules.Clamp(settings.OutboxCacheMinutes, 1, 24 * 60));
        var now = clock.GetUtcNow();
        var pairs = await db.OutboxPairs.AsNoTracking().Where(item => item.Enabled).ToListAsync(cancellationToken);
        var pair = pairs
            .Where(item => item.ForceRefresh || item.LastScanUtc is null || item.LastScanUtc < now - cache)
            .OrderByDescending(item => item.ForceRefresh)
            .ThenBy(item => item.LastScanUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        return pair?.Id;
    }

    private async Task ScanCoreAsync(int pairId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var pair = await db.OutboxPairs.FirstOrDefaultAsync(item => item.Id == pairId, cancellationToken);
        var settings = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (pair is null || settings is null)
            return;

        pair.Status = "scanning";
        pair.ForceRefresh = false;
        await db.SaveChangesAsync(cancellationToken);

        var maxFiles = Rules.Clamp(settings.ScanMaxFiles, 1000, 500_000);
        var budget = TimeSpan.FromSeconds(Rules.Clamp(settings.ScanSeconds, 5, 300));
        var source = await scanner.ScanAsync(pair.SourcePath, maxFiles, budget, cancellationToken);
        if (!source.Ok)
        {
            pair.Status = source.Unresponsive ? "unresponsive" : "error";
            pair.Error = source.Error;
            pair.LastScanUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        IReadOnlyList<FileFingerprint> destinationFiles;
        var destinationTruncated = false;
        string? destinationError = null;
        if (pair.DestinationKind == "remote")
        {
            var password = Unprotect(settings.RcPasswordProtected);
            if (password.Failed)
            {
                pair.Status = "error";
                pair.Error = "Saved rclone password could not be read.";
                pair.LastScanUtc = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var listed = await rclone.ListRemoteAsync(settings.RcUrl, settings.RcUser, password.Value, pair.Destination, maxFiles, cancellationToken);
            if (!listed.Ok)
            {
                pair.Status = "error";
                pair.Error = listed.Error;
                pair.LastScanUtc = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            destinationFiles = listed.Files;
            destinationTruncated = listed.Truncated;
        }
        else
        {
            var dest = await scanner.ScanAsync(pair.Destination, maxFiles, budget, cancellationToken);
            if (!dest.Ok)
            {
                pair.Status = dest.Unresponsive ? "unresponsive" : "error";
                pair.Error = dest.Error;
                pair.LastScanUtc = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            destinationFiles = dest.Files;
            destinationTruncated = dest.Truncated;
            destinationError = dest.Error;
        }

        var diff = OutboxComparer.Compare(source.Files, destinationFiles, source.Truncated, destinationTruncated);
        pair.PendingCount = diff.Count;
        pair.PendingBytes = diff.Bytes;
        pair.Truncated = diff.Incomplete;
        pair.Error = diff.Error ?? destinationError;
        pair.SampleJson = JsonSerializer.Serialize(diff.Sample);
        pair.Status = pair.Error is null ? "ready" : "error";
        pair.LastScanUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Outbox {Name} has {Count} pending files.", pair.Name, pair.PendingCount);
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

public sealed class OutboxWorker(OutboxService outbox, IConfiguration configuration, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!AppFlags.Workers(configuration))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var id = await outbox.NextStaleIdAsync(stoppingToken);
                if (id is not null)
                    await outbox.ScanAsync(id.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Outbox scan failed ({Type}): {Detail}", ex.GetType().Name, SecretRedactor.ForDisplay(ex.Message, 180));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
