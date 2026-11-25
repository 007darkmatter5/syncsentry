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

            return new ArrFetch(true, true, null, ArrHistoryParser.Merge(items));
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

        return Merge(events);
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
            map[key] = current with
            {
                Title = title,
                RelativeFolder = relative ?? "",
                RelativeFile = file,
                ExternalId = external,
                GrabbedUtc = grabbed,
                ImportedUtc = imported,
                DownloadFailed = failed,
                Failure = failure
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
            title = seriesTitle.Length > 0 && season is int seasonNumber && number is int episodeNumber
                ? seriesTitle + " · S" + seasonNumber + "E" + episodeNumber.ToString("00", CultureInfo.InvariantCulture)
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
        }

        if (kind != "import")
        {
            relative = "";
            relativeFile = null;
        }

        if (title.Length == 0)
            title = source.Length > 0 ? source : libraryTag;
        var failure = kind == "fail" ? FailureText(data) : null;
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
            kind == "grab" || kind == "fail" ? when : null,
            kind == "import" ? when : null,
            kind == "fail",
            failure);
    }

    private static string Classify(string eventType)
    {
        if (eventType.Equals("grabbed", StringComparison.OrdinalIgnoreCase))
            return "grab";
        if (eventType.Contains("import", StringComparison.OrdinalIgnoreCase))
            return "import";
        if (eventType.Contains("fail", StringComparison.OrdinalIgnoreCase))
            return "fail";
        return "";
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
