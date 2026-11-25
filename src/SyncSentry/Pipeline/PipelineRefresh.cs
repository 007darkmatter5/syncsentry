using Microsoft.EntityFrameworkCore;
using SyncSentry.Data;
using SyncSentry.Demo;
using SyncSentry.Host;
using SyncSentry.Security;
using SyncSentry.Services;

namespace SyncSentry.Pipeline;

public interface IFolderExists
{
    Task<IReadOnlySet<string>> PresentAsync(string root, IReadOnlyList<(string Key, string Relative)> paths, CancellationToken cancellationToken);
}

public sealed class FolderExists(IMountTable mounts, StuckProcesses stuck, TimeProvider clock) : IFolderExists
{
    private readonly Dictionary<string, (bool Present, DateTimeOffset Until)> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _hung = [];

    public async Task<IReadOnlySet<string>> PresentAsync(string root, IReadOnlyList<(string Key, string Relative)> paths, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Rules.AbsolutePath(root) || paths.Count == 0)
            return found;

        var now = clock.GetUtcNow();
        ReapHung();
        var fullRoot = Path.GetFullPath(root);
        var local = new List<(string Key, string Full)>();
        var isolated = new List<(string Key, string Full)>();
        var table = mounts.Read();
        foreach (var (key, relative) in paths)
        {
            foreach (var spelling in FolderKey.SeasonSpellings(relative))
            {
                if (!FolderKey.IsSafe(spelling))
                    continue;
                var full = Combine(fullRoot, spelling);
                if (full is null)
                    continue;
                if (TryCache(full, now, out var present))
                {
                    if (present)
                        found.Add(key);
                    continue;
                }

                if (IsHung(full))
                    continue;
                var covering = MountInfo.FindCovering(table, full);
                if (MountRisk.PathNeedsIsolation(full, covering))
                    isolated.Add((key, full));
                else
                    local.Add((key, full));
            }
        }

        foreach (var (key, full) in local)
        {
            var present = false;
            try
            {
                present = File.Exists(full) || Directory.Exists(full);
            }
            catch (IOException)
            {
            }

            Remember(full, present, now);
            if (present)
                found.Add(key);
        }

        if (isolated.Count == 0 || _hung.Count >= 2)
            return found;

        var batch = isolated.Take(250).ToList();
        var start = AppSpawn.Create(["--io-exists", .. batch.Select(item => item.Full)]);
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(12), cancellationToken);
        var answers = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < batch.Count && i < answers.Length; i++)
        {
            var present = answers[i].Equals("yes", StringComparison.OrdinalIgnoreCase);
            Remember(batch[i].Full, present, now);
            if (present)
                found.Add(batch[i].Key);
        }

        if ((result.TimedOut || !result.Exited) && answers.Length < batch.Count && result.LiveProcess is not null)
        {
            var hung = batch[answers.Length].Full;
            stuck.Remember("exists:" + hung, result.LiveProcess);
            lock (_cache)
                _hung.Add(hung);
        }

        return found;
    }

    private bool TryCache(string full, DateTimeOffset now, out bool present)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(full, out var cached) && cached.Until > now)
            {
                present = cached.Present;
                return true;
            }
        }

        present = false;
        return false;
    }

    private void Remember(string full, bool present, DateTimeOffset now)
    {
        var until = now + (present ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(2));
        lock (_cache)
            _cache[full] = (present, until);
    }

    private bool IsHung(string full)
    {
        lock (_cache)
            return _hung.Contains(full);
    }

    private void ReapHung()
    {
        lock (_cache)
            _hung.RemoveAll(path => !stuck.IsStuck("exists:" + path));
    }

    internal static string? Combine(string fullRoot, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        return full;
    }
}

public sealed class PipelineBoard
{
    private readonly object _gate = new();
    private PipelineInput? _input;
    private int _days = PipelinePaths.DefaultWindowDays;

    public PipelineSnapshot Current { get; private set; } = PipelineSnapshot.Empty(PipelinePaths.DefaultWindowDays);

    public void SetWindow(int days)
    {
        lock (_gate)
        {
            _days = Math.Clamp(days, 1, PipelinePaths.MaxWindowDays);
            Current = _input is null
                ? PipelineSnapshot.Empty(_days)
                : PipelineBuilder.Build(_input with { WindowDays = _days });
        }
    }

    public void Publish(PipelineInput input)
    {
        lock (_gate)
        {
            _input = input with { WindowDays = _days };
            Current = PipelineBuilder.Build(_input);
        }
    }
}

public sealed class PipelineRefresher(
    IDbContextFactory<ApplicationDbContext> factory,
    ISecretProtector secrets,
    IArrHistorySource arr,
    IPlexCatalog plex,
    ILogReader logs,
    IFolderExists folders,
    PipelineBoard board,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<PipelineRefresher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!AppFlags.Workers(configuration))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Pipeline refresh failed ({Type}).", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var since = now - TimeSpan.FromDays(PipelinePaths.MaxWindowDays);
        var demo = AppFlags.Demo(configuration);
        if (demo)
            DemoPipeline.Write(now);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.AsNoTracking().OrderBy(item => item.Id).FirstAsync(cancellationToken);
        var sonarrKey = Unprotect(setting.SonarrApiKeyProtected);
        var radarrKey = Unprotect(setting.RadarrApiKeyProtected);
        var lidarrKey = Unprotect(setting.LidarrApiKeyProtected);
        var plexToken = Unprotect(setting.PlexTokenProtected);

        ArrFetch sonarr;
        ArrFetch radarr;
        ArrFetch lidarr;
        var plexFetch = PlexFetch.NotConfigured;
        if (demo)
        {
            var demoItems = DemoPipeline.Items(now);
            sonarr = new ArrFetch(true, true, null, demoItems.Where(item => item.App == "Sonarr").ToList());
            radarr = new ArrFetch(true, true, null, demoItems.Where(item => item.App == "Radarr").ToList());
            lidarr = ArrFetch.NotConfigured;
            plexFetch = new PlexFetch(true, true, null, DemoPipeline.Plex(now));
        }
        else
        {
            sonarr = await FetchArrAsync("Sonarr", setting.SonarrUrl, sonarrKey, since, cancellationToken);
            radarr = await FetchArrAsync("Radarr", setting.RadarrUrl, radarrKey, since, cancellationToken);
            lidarr = await FetchArrAsync("Lidarr", setting.LidarrUrl, lidarrKey, since, cancellationToken);
        }

        var workerPath = demo ? DemoPipeline.WorkerLog : PipelinePaths.WorkerLog;
        var hourlyPath = demo ? DemoPipeline.HourlyLog : PipelinePaths.HourlyLog;
        var unasRoot = demo ? DemoPipeline.UnasRoot : PipelinePaths.UnasRoot;
        var worker = await logs.ReadAsync(workerPath, 768 * 1024, TimeSpan.FromSeconds(4), cancellationToken);
        var hourly = await logs.ReadAsync(hourlyPath, 768 * 1024, TimeSpan.FromSeconds(4), cancellationToken);
        var lines = new List<PushLine>();
        if (worker.Ok && worker.Text is not null)
            lines.AddRange(UnasPushLog.Parse(worker.Text));
        if (hourly.Ok && hourly.Text is not null)
            lines.AddRange(UnasPushLog.Parse(hourly.Text));

        var arrItems = sonarr.Items.Concat(radarr.Items).Concat(lidarr.Items).ToList();
        plexFetch = demo
            ? plexFetch
            : await FetchPlexAsync(setting.PlexUrl, plexToken, cancellationToken);
        var checks = DiskChecks(arrItems, lines);
        var present = await folders.PresentAsync(unasRoot, checks, cancellationToken);
        var sources = demo
            ? new PipelineSources(new SourceLink(true, null), new SourceLink(true, null), ArrLink(setting.LidarrUrl, setting.LidarrApiKeyProtected, lidarrKey, lidarr), new SourceLink(true, null))
            : new PipelineSources(
                ArrLink(setting.SonarrUrl, setting.SonarrApiKeyProtected, sonarrKey, sonarr),
                ArrLink(setting.RadarrUrl, setting.RadarrApiKeyProtected, radarrKey, radarr),
                ArrLink(setting.LidarrUrl, setting.LidarrApiKeyProtected, lidarrKey, lidarr),
                PlexLink(setting.PlexUrl, setting.PlexTokenProtected, plexToken, plexFetch));

        board.Publish(new PipelineInput(
            now,
            PipelinePaths.DefaultWindowDays,
            lines,
            arrItems,
            plexFetch.Hits,
            sources,
            present,
            Missing(worker),
            Missing(hourly),
            LogProblem(worker, hourly)));
    }

    private async Task<ArrFetch> FetchArrAsync(string app, string url, string? apiKey, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (!Configured(url, apiKey))
            return ArrFetch.NotConfigured;
        return await arr.FetchAsync(app, url.Trim(), apiKey!, since, cancellationToken);
    }

    private async Task<PlexFetch> FetchPlexAsync(string url, string? token, CancellationToken cancellationToken)
    {
        if (!Configured(url, token))
            return PlexFetch.NotConfigured;
        return await plex.FetchAsync(url.Trim(), token!, cancellationToken);
    }

    private string? Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return null;
        try
        {
            var value = secrets.Unprotect(protectedValue);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (Crypto.IsProtectionFailure(ex))
        {
            return null;
        }
    }

    private static bool Configured(string url, string? secret)
        => Rules.RemoteUrl(url) && !string.IsNullOrWhiteSpace(secret);

    private static SourceLink ArrLink(string url, string protectedValue, string? secret, ArrFetch fetch)
    {
        if (protectedValue.Length > 0 && secret is null && Rules.RemoteUrl(url))
            return new SourceLink(true, "unreadable");
        if (!Configured(url, secret))
            return new SourceLink(false, null);
        return new SourceLink(true, fetch.Ok ? null : fetch.Problem ?? "unreachable");
    }

    private static SourceLink PlexLink(string url, string protectedValue, string? secret, PlexFetch fetch)
    {
        if (protectedValue.Length > 0 && secret is null && Rules.RemoteUrl(url))
            return new SourceLink(true, "unreadable");
        if (!Configured(url, secret))
            return new SourceLink(false, null);
        return new SourceLink(true, fetch.Ok ? null : fetch.Problem ?? "unreachable");
    }

    private static bool Missing(LogRead read)
        => !read.Ok && read.Error == "Log file not found.";

    private static string? LogProblem(LogRead worker, LogRead hourly)
    {
        if (worker.Ok || hourly.Ok)
            return null;
        if (Missing(worker) && Missing(hourly))
            return null;
        if (worker.Unresponsive || hourly.Unresponsive)
            return "A push log is on a mount that did not answer. Copied and scan times may be missing until it recovers.";
        return "A push log could not be read. Copied and scan times may be missing.";
    }

    private static List<(string Key, string Relative)> DiskChecks(IReadOnlyList<ArrItem> items, IReadOnlyList<PushLine> lines)
    {
        var checks = new List<(string Key, string Relative)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string tag, string relative)
        {
            if (!FolderKey.IsSafe(relative))
                return;
            var folder = MediaLibrary.Folder(tag);
            var tail = folder + "/" + FolderKey.Normalize(relative);
            var key = FolderKey.DiskKey(tag, relative);
            if (!seen.Add(key))
                return;
            checks.Add((key, tail));
        }

        foreach (var item in items)
        {
            // The initial copy put whole folders on the UNAS before these push logs existed.
            // The folder being there is enough; a missing log line does not mean it is stuck.
            if (item.RelativeFolder.Length > 0)
                Add(item.LibraryTag, item.RelativeFolder);
            if (!string.IsNullOrEmpty(item.RelativeFile))
                Add(item.LibraryTag, item.RelativeFile);
        }

        foreach (var line in lines)
        {
            if (line.RelativeFolder.Length == 0)
                continue;
            Add(line.LibraryTag, line.RelativeFolder);
        }

        return checks;
    }
}
