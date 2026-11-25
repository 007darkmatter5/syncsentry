using System.Globalization;
using System.Net;
using System.Text.Json;
using SyncSentry.Security;

namespace SyncSentry.Pipeline;

public sealed record ArrItem(
    string App,
    string Kind,
    string Identity,
    string Title,
    string LibraryTag,
    string LibraryFolder,
    string RelativeFolder,
    string? RelativeFile,
    string? ExternalId,
    DateTimeOffset? GrabbedUtc,
    DateTimeOffset? ImportedUtc,
    bool DownloadFailed,
    string? Failure,
    bool HasFile = false,
    bool Dismissed = false,
    bool? InQueue = null,
    string? DownloadId = null,
    int? Season = null,
    int? Episode = null,
    string? SeriesGuid = null,
    string? AlsoGuid = null,
    bool DownloadSettled = false,
    int? SeriesId = null,
    string? TitleSlug = null,
    int? MediaId = null,
    IReadOnlyList<ArrEvent>? Events = null);

public sealed record ArrEvent(
    string EventType,
    string Kind,
    DateTimeOffset When,
    string? DownloadId,
    string? Release,
    string? Quality,
    long? Bytes,
    string? Failure);

public sealed record ArrFetch(bool Attempted, bool Ok, string? Problem, IReadOnlyList<ArrItem> Items)
{
    public static ArrFetch NotConfigured { get; } = new(false, true, null, []);

    public static ArrFetch Failed(string problem) => new(true, false, problem, []);
}

public interface IArrHistorySource
{
    Task<ArrFetch> FetchAsync(string app, string url, string apiKey, DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed class ArrHistoryClient(IHttpClientFactory http) : IArrHistorySource
{
    private static readonly TimeSpan PathTtl = TimeSpan.FromMinutes(5);
    private readonly object _pathsGate = new();
    private readonly Dictionary<int, (DateTimeOffset Until, string Files, string? Series)> _paths = new();
    public async Task<ArrFetch> FetchAsync(string app, string url, string apiKey, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (!Rules.RemoteUrl(url) || string.IsNullOrWhiteSpace(apiKey))
            return ArrFetch.NotConfigured;

        try
        {
            var client = http.CreateClient("arr");
            var items = new List<ArrItem>();
            for (var page = 1; page <= 5; page++)
            {
                var body = await GetHistoryAsync(client, app, url, apiKey, page, cancellationToken);
                if (body is null)
                    return ArrFetch.Failed("refused");
                var pageItems = ArrHistoryParser.Read(app, body);
                var records = ArrHistoryParser.RecordCount(body);
                if (records == 0 && page == 1)
                    return new ArrFetch(true, true, null, []);
                items.AddRange(pageItems);
                var oldest = pageItems.Count == 0
                    ? DateTimeOffset.MinValue
                    : pageItems.Min(item => item.GrabbedUtc ?? item.ImportedUtc ?? DateTimeOffset.MaxValue);
                if (records < 100 || oldest < since)
                    break;
            }

            var linked = ArrHistoryParser.LinkDownloads(ArrHistoryParser.Merge(items));
            var queue = await GetQueueKeysAsync(client, app, url, apiKey, cancellationToken);
            var queued = ArrHistoryParser.ApplyQueue(linked, queue);
            if (app.Equals("Sonarr", StringComparison.OrdinalIgnoreCase))
                queued = await FillSonarrPathsAsync(client, url, apiKey, queued, cancellationToken);
            return new ArrFetch(true, true, null, queued);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return ArrFetch.Failed("unreachable");
        }
    }

    private async Task<IReadOnlyList<ArrItem>> FillSonarrPathsAsync(HttpClient client, string url, string apiKey, IReadOnlyList<ArrItem> items, CancellationToken cancellationToken)
    {
        var needed = items.Where(item => item.HasFile && item.RelativeFolder.Length == 0 && item.SeriesId is > 0)
            .Select(item => item.SeriesId!.Value)
            .Distinct()
            .ToList();
        if (needed.Count == 0)
            return items;
        var filled = items;
        foreach (var seriesId in needed)
        {
            var (files, series) = await EpisodePathsAsync(client, url, apiKey, seriesId, cancellationToken);
            if (files is null && series is null)
                continue;
            try
            {
                filled = ArrHistoryParser.ApplyLibraryPaths(filled, seriesId, files ?? "[]", series);
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return filled;
    }

    private async Task<(string? Files, string? Series)> EpisodePathsAsync(HttpClient client, string url, string apiKey, int seriesId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_pathsGate)
        {
            if (_paths.TryGetValue(seriesId, out var cached) && cached.Until > now)
                return (cached.Files, cached.Series);
        }

        try
        {
            var files = await GetBodyAsync(client, url, apiKey, "/api/v3/episodefile?seriesId=" + seriesId.ToString(CultureInfo.InvariantCulture), cancellationToken);
            var series = await GetBodyAsync(client, url, apiKey, "/api/v3/series/" + seriesId.ToString(CultureInfo.InvariantCulture), cancellationToken);
            if (files is null && series is null)
                return (null, null);
            lock (_pathsGate)
                _paths[seriesId] = (now + PathTtl, files ?? "[]", series);
            return (files ?? "[]", series);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, null);
        }
    }

    private static async Task<string?> GetBodyAsync(HttpClient client, string url, string apiKey, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, url, apiKey, path, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task<string?> GetHistoryAsync(HttpClient client, string app, string url, string apiKey, int page, CancellationToken cancellationToken)
    {
        var withIncludes = HistoryPath(app, page, include: true);
        var response = await SendAsync(client, url, apiKey, withIncludes, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            response.Dispose();
            response = await SendAsync(client, url, apiKey, HistoryPath(app, page, include: false), cancellationToken);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, string apiKey, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Combine(url, path));
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return await client.SendAsync(request, cancellationToken);
    }

    private static string HistoryPath(string app, int page, bool include)
    {
        var api = app.Equals("Lidarr", StringComparison.OrdinalIgnoreCase) ? "v1" : "v3";
        var extra = "";
        if (include)
        {
            extra = app switch
            {
                "Sonarr" => "&includeSeries=true&includeEpisode=true",
                "Radarr" => "&includeMovie=true",
                "Lidarr" => "&includeArtist=true&includeAlbum=true",
                _ => ""
            };
        }

        return $"/api/{api}/history?page={page}&pageSize=100&sortKey=date&sortDirection=descending{extra}";
    }

    private static async Task<IReadOnlySet<string>?> GetQueueKeysAsync(HttpClient client, string app, string url, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var api = app.Equals("Lidarr", StringComparison.OrdinalIgnoreCase) ? "v1" : "v3";
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var page = 1; page <= 3; page++)
            {
                using var response = await SendAsync(client, url, apiKey, $"/api/{api}/queue?page={page}&pageSize=100", cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return null;
                if (!response.IsSuccessStatusCode)
                    return page == 1 ? null : keys;
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var count = ArrHistoryParser.ReadQueue(body, keys);
                if (count < 100)
                    break;
            }

            return keys;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    internal static Uri Combine(string url, string path)
    {
        var root = url.TrimEnd('/') + "/";
        return new Uri(new Uri(root, UriKind.Absolute), path.TrimStart('/'));
    }
}

public static class ArrHistoryParser
{
    public static int RecordCount(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var records))
            root = records;
        return root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() : 0;
    }

    public static IReadOnlyList<ArrItem> Read(string app, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var records))
            root = records;
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        var events = new List<ArrItem>();
        foreach (var record in root.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
                continue;
            var item = ReadRecord(app, record);
            if (item is not null)
                events.Add(item);
        }

        return LinkDownloads(Merge(events));
    }

    public static int ReadQueue(string json, ISet<string> keys)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var records))
            root = records;
        if (root.ValueKind != JsonValueKind.Array)
            return 0;
        var count = 0;
        foreach (var record in root.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
                continue;
            count++;
            AddKey(keys, "episode:", Int(record, "episodeId"));
            AddKey(keys, "movie:", Int(record, "movieId"));
            AddKey(keys, "album:", Int(record, "albumId"));
            var download = First(record, "downloadId");
            if (download.Length == 0 && record.TryGetProperty("data", out var data))
                download = First(data, "downloadId");
            if (download.Length > 0)
                keys.Add("download:" + download);
        }

        return count;
    }

    public static IReadOnlyList<ArrItem> ApplyLibraryPaths(IReadOnlyList<ArrItem> items, int seriesId, string episodeFilesJson, string? seriesJson)
    {
        var files = ReadEpisodeFiles(episodeFilesJson);
        var seriesPath = "";
        var seasonFolders = new Dictionary<int, string>();
        if (!string.IsNullOrWhiteSpace(seriesJson))
        {
            using var document = JsonDocument.Parse(seriesJson);
            var root = document.RootElement;
            seriesPath = First(root, "path");
            if (root.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
            {
                foreach (var season in seasons.EnumerateArray())
                {
                    var number = Int(season, "seasonNumber");
                    var folder = First(season, "seasonFolder");
                    if (number is int seasonNumber && folder.Length > 0)
                        seasonFolders[seasonNumber] = folder;
                }
            }
        }

        return items.Select(item => FillPath(item, seriesId, seriesPath, files, seasonFolders)).ToList();
    }

    public static IReadOnlyList<ArrItem> ApplyQueue(IReadOnlyList<ArrItem> items, IReadOnlySet<string>? keys)
    {
        if (keys is null)
            return items;
        return items.Select(item => item with { InQueue = InQueue(item, keys) }).ToList();
    }

    public static IReadOnlyList<ArrItem> LinkDownloads(IReadOnlyList<ArrItem> items)
    {
        var list = items.ToList();
        var drop = new HashSet<int>();
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item.ImportedUtc is not null || item.GrabbedUtc is null)
                continue;
            var importAt = -1;
            for (var j = 0; j < list.Count; j++)
            {
                if (i == j || list[j].ImportedUtc is null)
                    continue;
                if (SameStory(item, list[j]))
                {
                    importAt = j;
                    break;
                }
            }

            if (importAt >= 0)
            {
                var import = list[importAt];
                list[importAt] = import with
                {
                    GrabbedUtc = Earlier(import.GrabbedUtc, item.GrabbedUtc),
                    HasFile = import.HasFile || item.HasFile,
                    DownloadId = import.DownloadId ?? item.DownloadId,
                    SeriesGuid = import.SeriesGuid ?? item.SeriesGuid,
                    AlsoGuid = import.AlsoGuid ?? item.AlsoGuid,
                    ExternalId = import.ExternalId ?? item.ExternalId,
                    Season = import.Season ?? item.Season,
                    Episode = import.Episode ?? item.Episode,
                    SeriesId = import.SeriesId ?? item.SeriesId,
                    TitleSlug = import.TitleSlug ?? item.TitleSlug,
                    MediaId = import.MediaId ?? item.MediaId,
                    Events = JoinEvents(import.Events, item.Events)
                };
                drop.Add(i);
                continue;
            }

            if (!string.IsNullOrEmpty(item.DownloadId)
                && list.Any(other => other.ImportedUtc is not null && string.Equals(other.DownloadId, item.DownloadId, StringComparison.OrdinalIgnoreCase)))
                list[i] = item with { DownloadSettled = true };
        }

        var linked = new List<ArrItem>();
        for (var i = 0; i < list.Count; i++)
        {
            if (!drop.Contains(i))
                linked.Add(list[i]);
        }

        return linked;
    }

    public static IReadOnlyList<ArrItem> Merge(IReadOnlyList<ArrItem> events)
    {
        var map = new Dictionary<string, ArrItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events.OrderBy(item => item.GrabbedUtc ?? item.ImportedUtc ?? DateTimeOffset.MinValue))
        {
            var key = item.App + "|" + item.Identity;
            if (!map.TryGetValue(key, out var current))
            {
                map[key] = item;
                continue;
            }

            var grabbed = Earlier(current.GrabbedUtc, item.GrabbedUtc);
            var imported = Earlier(current.ImportedUtc, item.ImportedUtc);
            var failedAt = Later(current.DownloadFailed ? current.GrabbedUtc ?? current.ImportedUtc : null, item.DownloadFailed ? item.GrabbedUtc ?? item.ImportedUtc : null);
            var failed = failedAt is not null && (imported is null || failedAt > imported);
            var relative = Prefer(current.RelativeFolder, item.RelativeFolder);
            var file = Prefer(current.RelativeFile, item.RelativeFile);
            var external = current.ExternalId ?? item.ExternalId;
            var failure = failed ? (item.Failure ?? current.Failure) : null;
            var title = current.Title.Length >= item.Title.Length ? current.Title : item.Title;
            var dismissed = (current.Dismissed || item.Dismissed) && imported is null;
            map[key] = Fold(current, item, title, relative ?? "", file, external, grabbed, imported, failed, failure, dismissed);
        }

        return map.Values.ToList();
    }

    public static ArrItem Combine(IReadOnlyList<ArrItem> items)
    {
        var current = items[0];
        for (var i = 1; i < items.Count; i++)
        {
            var item = items[i];
            var grabbed = Earlier(current.GrabbedUtc, item.GrabbedUtc);
            var imported = Earlier(current.ImportedUtc, item.ImportedUtc);
            var failedAt = Later(current.DownloadFailed ? current.GrabbedUtc ?? current.ImportedUtc : null, item.DownloadFailed ? item.GrabbedUtc ?? item.ImportedUtc : null);
            var failed = failedAt is not null && (imported is null || failedAt > imported);
            var title = current.Title.Length >= item.Title.Length ? current.Title : item.Title;
            current = Fold(
                current,
                item,
                title,
                Prefer(current.RelativeFolder, item.RelativeFolder) ?? "",
                Prefer(current.RelativeFile, item.RelativeFile),
                current.ExternalId ?? item.ExternalId,
                grabbed,
                imported,
                failed,
                failed ? item.Failure ?? current.Failure : null,
                (current.Dismissed || item.Dismissed) && imported is null);
        }

        return current;
    }

    private static ArrItem Fold(
        ArrItem current,
        ArrItem item,
        string title,
        string relative,
        string? file,
        string? external,
        DateTimeOffset? grabbed,
        DateTimeOffset? imported,
        bool failed,
        string? failure,
        bool dismissed)
    {
        var identity = current.Identity.StartsWith("title:", StringComparison.OrdinalIgnoreCase)
            && !item.Identity.StartsWith("title:", StringComparison.OrdinalIgnoreCase)
            ? item.Identity
            : current.Identity;
        return current with
        {
            Identity = identity,
            Title = title,
            RelativeFolder = relative,
            RelativeFile = file,
            ExternalId = external,
            GrabbedUtc = grabbed,
            ImportedUtc = imported,
            DownloadFailed = failed,
            Failure = failure,
            HasFile = current.HasFile || item.HasFile,
            Dismissed = dismissed,
            DownloadId = current.DownloadId ?? item.DownloadId,
            Season = current.Season ?? item.Season,
            Episode = current.Episode ?? item.Episode,
            SeriesGuid = current.SeriesGuid ?? item.SeriesGuid,
            AlsoGuid = current.AlsoGuid ?? item.AlsoGuid,
            DownloadSettled = current.DownloadSettled || item.DownloadSettled,
            SeriesId = current.SeriesId ?? item.SeriesId,
            TitleSlug = current.TitleSlug ?? item.TitleSlug,
            MediaId = current.MediaId ?? item.MediaId,
            Events = JoinEvents(current.Events, item.Events)
        };
    }

    private static IReadOnlyList<ArrEvent>? JoinEvents(IReadOnlyList<ArrEvent>? left, IReadOnlyList<ArrEvent>? right)
    {
        if (left is null || left.Count == 0)
            return right;
        if (right is null || right.Count == 0)
            return left;
        return left.Concat(right).OrderBy(item => item.When).ThenBy(item => item.EventType, StringComparer.Ordinal).ToList();
    }

    private static ArrItem? ReadRecord(string app, JsonElement record)
    {
        var kind = Classify(String(record, "eventType"));
        if (kind.Length == 0)
            return null;
        if (!TryDate(record, out var when))
            return null;

        var source = String(record, "sourceTitle");
        var data = record.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement
            : default;
        var importedPath = data.ValueKind == JsonValueKind.Object ? First(data, "importedPath") : "";
        string identity;
        string title;
        string libraryTag;
        string relative;
        string? relativeFile;
        string? external;
        var hasFile = false;
        string? seriesGuid = null;
        string? alsoGuid = null;
        int? seasonNumber = null;
        int? episodeNumber = null;
        int? seriesId = null;
        string? titleSlug = null;
        int? mediaId = null;

        if (app.Equals("Radarr", StringComparison.OrdinalIgnoreCase))
        {
            var movie = Object(record, "movie");
            identity = FirstId("movie:", Int(movie, "id"), Int(record, "movieId"), Int(movie, "tmdbId"))
                ?? TextId("imdb:", First(movie, "imdbId"))
                ?? "title:" + source;
            title = WithYear(First(movie, "title"), Int(movie, "year"));
            if (title.Length == 0)
                title = source;
            libraryTag = "Movies";
            var moviePath = First(movie, "path");
            var movieFile = First(Object(record, "movieFile"), "path");
            var movieLibraryFile = movieFile.Length > 0 ? movieFile : kind == "import" ? importedPath : "";
            relative = LibraryPath.FolderFromFile(moviePath, movieLibraryFile);
            if (relative.Length == 0)
                relative = FolderName(moviePath);
            if (relative.Length == 0)
                relative = FolderName(Parent(movieLibraryFile));
            relativeFile = FileTail(relative, movieLibraryFile);
            external = ExternalMovie(movie);
            hasFile = Bool(movie, "hasFile") || HasObject(record, "movieFile");
            titleSlug = Blank(First(movie, "titleSlug"));
            mediaId = Int(movie, "id") ?? Int(record, "movieId");
        }
        else if (app.Equals("Lidarr", StringComparison.OrdinalIgnoreCase))
        {
            var artist = Object(record, "artist");
            var album = Object(record, "album");
            var artistName = First(artist, "artistName", "title");
            var albumTitle = First(album, "title");
            identity = FirstId("album:", Int(album, "id"), Int(record, "albumId"))
                ?? TextId("mbid:", First(album, "foreignAlbumId"))
                ?? (artistName.Length > 0 && albumTitle.Length > 0 ? "album:" + artistName + "/" + albumTitle : "title:" + source);
            title = artistName.Length > 0 && albumTitle.Length > 0 ? artistName + " · " + albumTitle : source;
            libraryTag = "Music";
            var artistFolder = FolderName(First(artist, "path"));
            var albumFolder = FolderName(Parent(importedPath));
            if (artistFolder.Length == 0)
                artistFolder = FolderName(Parent(Parent(importedPath)));
            relative = Join(artistFolder, albumFolder);
            relativeFile = FileTail(relative, importedPath);
            var mbid = First(album, "foreignAlbumId");
            external = mbid.Length > 0 ? "mbid:" + mbid : null;
            hasFile = Bool(album, "hasFile") || HasObject(record, "albumFile");
            titleSlug = Blank(First(artist, "titleSlug"));
            mediaId = Int(album, "id") ?? Int(record, "albumId");
        }
        else
        {
            var series = Object(record, "series");
            var episode = Object(record, "episode");
            var season = Int(episode, "seasonNumber");
            var number = Int(episode, "episodeNumber");
            identity = FirstId("episode:", Int(episode, "id"), Int(record, "episodeId"))
                ?? (Int(series, "id") is int showId && season is int seasonNo && number is int episodeNo
                    ? "episode:" + showId + ":" + seasonNo + ":" + episodeNo
                    : null)
                ?? (Int(record, "seriesId") is int seriesKey && season is int s && number is int e
                    ? "episode:" + seriesKey + ":" + s + ":" + e
                    : "title:" + source);
            var seriesTitle = WithYear(First(series, "title"), Int(series, "year"));
            title = seriesTitle.Length > 0 && season is int shownSeason && number is int shownEpisode
                ? seriesTitle + " · S" + shownSeason + "E" + shownEpisode.ToString("00", CultureInfo.InvariantCulture)
                : source;
            libraryTag = "TV";
            seriesId = Int(series, "id") ?? Int(record, "seriesId");
            titleSlug = Blank(First(series, "titleSlug"));
            mediaId = seriesId;
            var seriesPath = First(series, "path");
            var showFolder = FolderName(seriesPath);
            var episodeFilePath = First(Object(record, "episodeFile"), "path");
            var libraryFile = episodeFilePath;
            if (libraryFile.Length == 0 && kind == "import")
                libraryFile = importedPath;
            relative = LibraryPath.FolderFromFile(seriesPath, libraryFile);
            relativeFile = relative.Length == 0 ? null : FileTail(relative, libraryFile);
            if (relative.Length == 0 && kind == "import")
            {
                var seasonFolder = FolderName(Parent(importedPath));
                if (showFolder.Length == 0)
                    showFolder = FolderName(Parent(Parent(importedPath)));
                if (seasonFolder.Length == 0 && season is int seasonValue)
                    seasonFolder = "Season " + seasonValue.ToString(CultureInfo.InvariantCulture);
                relative = Join(showFolder, seasonFolder);
                relativeFile = FileTail(relative, importedPath);
            }
            external = null;
            var tvdb = Int(series, "tvdbId");
            var imdb = First(series, "imdbId");
            seriesGuid = tvdb is > 0 ? "tvdb:" + tvdb.Value.ToString(CultureInfo.InvariantCulture) : null;
            alsoGuid = imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? "imdb:" + imdb : null;
            if (seriesGuid is null && alsoGuid is not null)
            {
                seriesGuid = alsoGuid;
                alsoGuid = null;
            }

            seasonNumber = season;
            episodeNumber = number;
            hasFile = Bool(episode, "hasFile") || episodeFilePath.Length > 0;
        }

        if (kind != "import" && !hasFile)
        {
            relative = "";
            relativeFile = null;
        }

        if (title.Length == 0)
            title = source.Length > 0 ? source : libraryTag;
        var failure = kind == "fail" ? FailureText(data) : null;
        var downloadId = First(record, "downloadId");
        if (downloadId.Length == 0)
            downloadId = First(data, "downloadId");
        return new ArrItem(
            AppName(app),
            KindName(app),
            identity,
            title,
            libraryTag,
            MediaLibrary.Folder(libraryTag),
            relative,
            relativeFile,
            external,
            kind == "grab" || kind == "fail" || kind == "dismiss" ? when : null,
            kind == "import" ? when : null,
            kind == "fail",
            failure,
            hasFile,
            kind == "dismiss",
            null,
            downloadId.Length == 0 ? null : downloadId,
            seasonNumber,
            episodeNumber,
            seriesGuid,
            alsoGuid,
            false,
            seriesId,
            titleSlug,
            mediaId,
            [
                new ArrEvent(
                    String(record, "eventType"),
                    kind,
                    when,
                    downloadId.Length == 0 ? null : downloadId,
                    Blank(source),
                    Blank(QualityName(record)),
                    data.ValueKind == JsonValueKind.Object ? SizeOf(data) : null,
                    failure)
            ]);
    }

    private static string? Blank(string text) => text.Length == 0 ? null : text;

    private static string QualityName(JsonElement record)
    {
        if (!record.TryGetProperty("quality", out var quality) || quality.ValueKind != JsonValueKind.Object)
            return "";
        if (quality.TryGetProperty("quality", out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            var name = First(inner, "name");
            if (name.Length > 0)
                return name;
        }

        return First(quality, "name");
    }

    private static long? SizeOf(JsonElement data)
    {
        foreach (var name in new[] { "size", "fileSize" })
        {
            if (!data.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static string Classify(string eventType)
    {
        if (eventType.Equals("grabbed", StringComparison.OrdinalIgnoreCase))
            return "grab";
        if (eventType.Contains("import", StringComparison.OrdinalIgnoreCase))
            return "import";
        if (eventType.Contains("fail", StringComparison.OrdinalIgnoreCase))
            return "fail";
        if (eventType.Contains("ignore", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("blocklist", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("replaced", StringComparison.OrdinalIgnoreCase))
            return "dismiss";
        return "";
    }

    private static bool SameStory(ArrItem grab, ArrItem import)
    {
        if (!SameSlot(grab, import))
            return false;
        if (!string.IsNullOrEmpty(grab.DownloadId) && string.Equals(grab.DownloadId, import.DownloadId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(grab.SeriesGuid) && GuidEquals(grab.SeriesGuid, import.SeriesGuid))
            return true;
        if (!string.IsNullOrEmpty(grab.AlsoGuid) && (GuidEquals(grab.AlsoGuid, import.AlsoGuid) || GuidEquals(grab.AlsoGuid, import.SeriesGuid)))
            return true;
        return string.Equals(grab.Title, import.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameSlot(ArrItem left, ArrItem right)
    {
        if (string.Equals(left.Identity, right.Identity, StringComparison.OrdinalIgnoreCase))
            return true;
        if (left.Season is int season && right.Season == season && left.Episode is int episode && right.Episode == episode)
            return left.LibraryTag == right.LibraryTag;
        return false;
    }

    private static bool GuidEquals(string? left, string? right)
        => !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static ArrItem FillPath(ArrItem item, int seriesId, string seriesPath, IReadOnlyList<SonarrFile> files, IReadOnlyDictionary<int, string> seasonFolders)
    {
        if (item.SeriesId != seriesId || item.RelativeFolder.Length > 0 || !item.HasFile)
            return item;
        var episodeId = EpisodeId(item);
        SonarrFile? file = null;
        if (episodeId is int id)
            file = files.FirstOrDefault(candidate => candidate.EpisodeIds.Contains(id));
        if (file is null && item.Season is int season)
            file = files.FirstOrDefault(candidate => candidate.Season == season);
        var folder = LibraryPath.FolderFromFile(seriesPath, file?.Path);
        if (folder.Length == 0)
            folder = LibraryPath.TailFolder(file?.Path);
        if (folder.Length == 0 && seriesPath.Length > 0 && item.Season is int seasonNumber)
        {
            var seasonName = seasonFolders.TryGetValue(seasonNumber, out var named)
                ? named
                : "Season " + seasonNumber.ToString("00", CultureInfo.InvariantCulture);
            folder = FolderName(seriesPath) + "/" + seasonName;
        }

        if (folder.Length == 0)
            return item;
        var relativeFile = file is null ? item.RelativeFile : FileTail(folder, file.Path) ?? item.RelativeFile;
        return item with { RelativeFolder = folder, RelativeFile = relativeFile };
    }

    private static int? EpisodeId(ArrItem item)
    {
        const string prefix = "episode:";
        if (!item.Identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = item.Identity[prefix.Length..];
        if (rest.Contains(':'))
            return null;
        return int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static IReadOnlyList<SonarrFile> ReadEpisodeFiles(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var records))
            root = records;
        if (root.ValueKind != JsonValueKind.Array)
            return [];
        var files = new List<SonarrFile>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var ids = new List<int>();
            if (item.TryGetProperty("episodeIds", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Number && entry.TryGetInt32(out var id))
                        ids.Add(id);
                }
            }

            var single = Int(item, "episodeId");
            if (single is int one && !ids.Contains(one))
                ids.Add(one);
            files.Add(new SonarrFile(ids, Int(item, "seasonNumber"), First(item, "path")));
        }

        return files;
    }

    private sealed record SonarrFile(IReadOnlyList<int> EpisodeIds, int? Season, string Path);

    private static bool InQueue(ArrItem item, IReadOnlySet<string> keys)
    {
        if (!string.IsNullOrEmpty(item.DownloadId) && keys.Contains("download:" + item.DownloadId))
            return true;
        return keys.Contains(item.Identity);
    }

    private static void AddKey(ISet<string> keys, string prefix, int? id)
    {
        if (id is > 0)
            keys.Add(prefix + id.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static bool HasObject(JsonElement record, string name)
        => record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;

    private static bool Bool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && value.GetString() is "true");
    }

    private static string? ExternalMovie(JsonElement movie)
    {
        var imdb = First(movie, "imdbId");
        if (imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            return "imdb:" + imdb;
        if (Int(movie, "tmdbId") is int tmdb)
            return "tmdb:" + tmdb.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static string FailureText(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return "The download failed.";
        var text = First(data, "message", "reason", "status");
        if (text.Length == 0)
            return "The download failed.";
        text = SecretRedactor.ForDisplay(text, 180);
        return text.Length == 0 ? "The download failed." : text;
    }

    private static string? FileTail(string relativeFolder, string importedPath)
    {
        if (relativeFolder.Length == 0 || importedPath.Length == 0 || !PlexPath.IsMediaFile(importedPath))
            return null;
        var name = Path.GetFileName(importedPath.Replace('\\', '/'));
        if (string.IsNullOrEmpty(name))
            return null;
        return FolderKey.Normalize(relativeFolder + "/" + name);
    }

    private static string Join(string parent, string child)
    {
        if (parent.Length == 0)
            return child;
        if (child.Length == 0 || child.Equals(parent, StringComparison.OrdinalIgnoreCase))
            return parent;
        return parent + "/" + child;
    }

    private static string FolderName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var trimmed = path.Replace('\\', '/').Trim().TrimEnd('/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? "" : name;
    }

    private static string Parent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var parent = Path.GetDirectoryName(trimmed);
        return parent?.Replace('\\', '/') ?? "";
    }

    private static string WithYear(string title, int? year)
    {
        if (title.Length == 0)
            return "";
        if (year is null or < 1900)
            return title;
        var suffix = "(" + year.Value.ToString(CultureInfo.InvariantCulture) + ")";
        return title.Contains(suffix, StringComparison.Ordinal) ? title : title + " " + suffix;
    }

    private static string AppName(string app)
        => app.Equals("Radarr", StringComparison.OrdinalIgnoreCase) ? "Radarr"
            : app.Equals("Lidarr", StringComparison.OrdinalIgnoreCase) ? "Lidarr"
            : "Sonarr";

    private static string KindName(string app)
        => app.Equals("Radarr", StringComparison.OrdinalIgnoreCase) ? "movie"
            : app.Equals("Lidarr", StringComparison.OrdinalIgnoreCase) ? "album"
            : "episode";

    private static string? FirstId(string prefix, params int?[] ids)
    {
        foreach (var id in ids)
        {
            if (id is > 0)
                return prefix + id.Value.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string? TextId(string prefix, string value)
        => string.IsNullOrWhiteSpace(value) ? null : prefix + value.Trim();

    private static string? Prefer(string? current, string? next)
        => string.IsNullOrEmpty(current) ? next : current;

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return left < right ? left : right;
    }

    private static DateTimeOffset? Later(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return left > right ? left : right;
    }

    private static bool TryDate(JsonElement record, out DateTimeOffset when)
    {
        when = default;
        var text = String(record, "date");
        return text.Length > 0 && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when);
    }

    private static JsonElement Object(JsonElement record, string name)
        => record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string String(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
    }

    private static string First(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = String(element, name);
            if (value.Length > 0)
                return value;
        }

        return "";
    }

    private static int? Int(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
