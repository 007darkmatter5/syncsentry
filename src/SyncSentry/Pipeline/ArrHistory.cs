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
    bool DownloadSettled = false);

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
            return new ArrFetch(true, true, null, ArrHistoryParser.ApplyQueue(linked, queue));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return ArrFetch.Failed("unreachable");
        }
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
                    Episode = import.Episode ?? item.Episode
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
            map[key] = current with
            {
                Title = title,
                RelativeFolder = relative ?? "",
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
                DownloadSettled = current.DownloadSettled || item.DownloadSettled
            };
        }

        return map.Values.ToList();
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
        var importedPath = data.ValueKind == JsonValueKind.Object ? First(data, "importedPath", "path", "droppedPath") : "";
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
            relative = FolderName(First(movie, "path"));
            if (relative.Length == 0)
                relative = FolderName(Parent(importedPath));
            relativeFile = FileTail(relative, importedPath);
            external = ExternalMovie(movie);
            hasFile = Bool(movie, "hasFile") || HasObject(record, "movieFile");
            var movieFile = First(Object(record, "movieFile"), "path");
            if (relative.Length == 0 && movieFile.Length > 0)
                relative = FolderName(Parent(movieFile));
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
        }
        else
        {
            var series = Object(record, "series");
            var episode = Object(record, "episode");
            var season = Int(episode, "seasonNumber");
            var number = Int(episode, "episodeNumber");
            identity = FirstId("episode:", Int(episode, "id"), Int(record, "episodeId"))
                ?? (Int(series, "id") is int seriesId && season is int seasonNo && number is int episodeNo
                    ? "episode:" + seriesId + ":" + seasonNo + ":" + episodeNo
                    : null)
                ?? (Int(record, "seriesId") is int seriesKey && season is int s && number is int e
                    ? "episode:" + seriesKey + ":" + s + ":" + e
                    : "title:" + source);
            var seriesTitle = WithYear(First(series, "title"), Int(series, "year"));
            title = seriesTitle.Length > 0 && season is int shownSeason && number is int shownEpisode
                ? seriesTitle + " · S" + shownSeason + "E" + shownEpisode.ToString("00", CultureInfo.InvariantCulture)
                : source;
            libraryTag = "TV";
            var showFolder = FolderName(First(series, "path"));
            var seasonFolder = FolderName(Parent(importedPath));
            if (showFolder.Length == 0)
                showFolder = FolderName(Parent(Parent(importedPath)));
            if (seasonFolder.Length == 0 && season is int seasonValue && kind == "import")
                seasonFolder = "Season " + seasonValue.ToString(CultureInfo.InvariantCulture);
            relative = kind == "import" ? Join(showFolder, seasonFolder) : "";
            relativeFile = kind == "import" ? FileTail(relative, importedPath) : null;
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
            hasFile = Bool(episode, "hasFile") || HasObject(record, "episodeFile");
            var episodeFile = First(Object(record, "episodeFile"), "path");
            if (relative.Length == 0 && episodeFile.Length > 0)
            {
                var fileFolder = FolderName(Parent(episodeFile));
                var show = showFolder.Length > 0 ? showFolder : FolderName(Parent(Parent(episodeFile)));
                relative = Join(show, fileFolder);
                relativeFile = FileTail(relative, episodeFile);
            }
            else if (relative.Length == 0 && hasFile && showFolder.Length > 0 && seasonNumber is int hasSeason)
                relative = showFolder + "/Season " + hasSeason.ToString(CultureInfo.InvariantCulture);
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
            alsoGuid);
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
