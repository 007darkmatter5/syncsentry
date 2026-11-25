using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace SyncSentry.Pipeline;

public sealed record PlexHit(
    string Title,
    string Kind,
    string LibraryFolder,
    string RelativeFolder,
    IReadOnlyList<string> ExternalIds,
    DateTimeOffset? AddedUtc,
    int? Season = null,
    int? Episode = null,
    string? RatingKey = null,
    string? ShowTitle = null,
    int? Year = null);

public sealed record PlexQuery(
    string Key,
    string LibraryTag,
    string RelativeFolder,
    IReadOnlyList<string> Guids,
    int? Season,
    int? Episode);

public sealed record PlexFetch(bool Attempted, bool Ok, string? Problem, IReadOnlyList<PlexHit> Hits)
{
    public static PlexFetch NotConfigured { get; } = new(false, true, null, []);

    public static PlexFetch Failed(string problem) => new(true, false, problem, []);
}

public interface IPlexCatalog
{
    Task<PlexFetch> FetchAsync(string url, string token, CancellationToken cancellationToken);
}

public sealed class PlexCatalogClient(IHttpClientFactory http) : IPlexCatalog
{
    internal static int PageSize { get; set; } = 200;
    private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(4);

    private readonly object _gate = new();
    private (string Url, DateTimeOffset Until, IReadOnlyList<PlexHit> Hits)? _index;

    public async Task<PlexFetch> FetchAsync(string url, string token, CancellationToken cancellationToken)
    {
        if (!Rules.RemoteUrl(url) || string.IsNullOrWhiteSpace(token))
            return PlexFetch.NotConfigured;

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_index is { } cached && cached.Url == url && cached.Until > now)
                return new PlexFetch(true, true, null, cached.Hits);
        }

        try
        {
            var client = http.CreateClient("plex");
            var sectionsBody = await GetAsync(client, url, token, "/library/sections", cancellationToken);
            if (sectionsBody is null)
                return CachedOrFailed("refused");
            var hits = new List<PlexHit>();
            foreach (var section in PlexJson.Sections(sectionsBody))
            {
                if (section.Type is not ("movie" or "show" or "artist"))
                    continue;
                hits.AddRange(await PageSectionAsync(client, url, token, section, cancellationToken));
            }

            IReadOnlyList<PlexHit> indexed = hits;
            lock (_gate)
                _index = (url, now + IndexTtl, indexed);
            return new PlexFetch(true, true, null, indexed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return CachedOrFailed("unreachable");
        }
    }

    private async Task<IReadOnlyList<PlexHit>> PageSectionAsync(HttpClient client, string url, string token, PlexSection section, CancellationToken cancellationToken)
    {
        var hits = new List<PlexHit>();
        var pageSize = Math.Clamp(PageSize, 1, 500);
        var type = section.Type == "show" ? "&type=4" : section.Type == "artist" ? "&type=10" : "";
        for (var start = 0; start < 50000; start += pageSize)
        {
            var body = await GetAsync(
                client,
                url,
                token,
                "/library/sections/" + Uri.EscapeDataString(section.Key) + "/all?includeGuids=1" + type
                    + "&X-Plex-Container-Start=" + start.ToString(CultureInfo.InvariantCulture)
                    + "&X-Plex-Container-Size=" + pageSize.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
            if (body is null)
                break;
            var page = PlexDocument.Read(body, section.Title);
            hits.AddRange(page.Hits);
            if (!PlexDocument.MorePages(hits.Count, page.Hits.Count, pageSize, page.Total))
                break;
        }

        return hits;
    }

    private PlexFetch CachedOrFailed(string problem)
    {
        lock (_gate)
        {
            if (_index is { } cached)
                return new PlexFetch(true, true, null, cached.Hits);
        }

        return PlexFetch.Failed(problem);
    }

    private static async Task<string?> GetAsync(HttpClient client, string url, string token, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ArrHistoryClient.Combine(url, path));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "SyncSentry");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

public static class PlexJson
{
    public static IReadOnlyList<PlexSection> Sections(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!Container(document.RootElement, out var container))
            return [];
        if (!Prop(container, "Directory", out var directories) || directories.ValueKind != JsonValueKind.Array)
            return [];
        var sections = new List<PlexSection>();
        foreach (var item in directories.EnumerateArray())
        {
            var key = String(item, "key");
            var title = String(item, "title");
            var type = String(item, "type");
            if (key.Length == 0 || title.Length == 0)
                continue;
            sections.Add(new PlexSection(key, title, type));
        }

        return sections;
    }

    public static IReadOnlyList<PlexHit> Hits(string json, string sectionTitle)
        => PlexDocument.Read(json, sectionTitle).Hits;

    internal static PlexHit? ReadHit(JsonElement item, string sectionTitle)
    {
        var type = String(item, "type");
        if (type is not ("movie" or "episode" or "album" or "track" or "show"))
            return null;
        var file = FirstFile(item);
        var split = PlexPath.Split(file, sectionTitle);
        var title = TitleOf(item, type);
        var ids = ExternalIds(item);
        var showGuid = String(item, "grandparentGuid");
        if (showGuid.Length > 0)
            AddGuid(ids, showGuid);
        DateTimeOffset? added = null;
        if (Prop(item, "addedAt", out var addedAt) && addedAt.TryGetInt64(out var unix) && unix > 0)
            added = DateTimeOffset.FromUnixTimeSeconds(unix);
        var season = type == "episode" ? Int(item, "parentIndex") : null;
        var episode = type == "episode" ? Int(item, "index") : null;
        var rating = String(item, "ratingKey");
        var showTitle = type == "episode" ? String(item, "grandparentTitle") : null;
        var year = Int(item, "year") ?? (type == "episode" ? Int(item, "grandparentYear") : null);
        return new PlexHit(title, type, split.LibraryFolder, split.RelativeFolder, ids, added, season, episode, rating.Length == 0 ? null : rating, showTitle, year);
    }

    private static string TitleOf(JsonElement item, string type)
    {
        var title = String(item, "title");
        if (type == "episode")
        {
            var show = String(item, "grandparentTitle");
            var season = Int(item, "parentIndex");
            var episode = Int(item, "index");
            if (show.Length > 0 && season is not null && episode is not null)
                return show + " · S" + season.Value.ToString(CultureInfo.InvariantCulture) + "E" + episode.Value.ToString("00", CultureInfo.InvariantCulture);
            if (show.Length > 0)
                return show + " · " + title;
        }

        if (type is "album" or "track")
        {
            var artist = String(item, "parentTitle");
            if (type == "track")
                artist = String(item, "grandparentTitle");
            if (artist.Length > 0 && title.Length > 0)
                return artist + " · " + title;
        }

        var year = Int(item, "year");
        if (year is >= 1900 && title.Length > 0 && !title.Contains("(" + year.Value.ToString(CultureInfo.InvariantCulture) + ")", StringComparison.Ordinal))
            return title + " (" + year.Value.ToString(CultureInfo.InvariantCulture) + ")";
        return title;
    }

    private static List<string> ExternalIds(JsonElement item)
    {
        var ids = new List<string>();
        AddGuid(ids, String(item, "guid"));
        if (Prop(item, "Guid", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
                AddGuid(ids, String(entry, "id"));
        }

        return ids;
    }

    internal static void AddGuid(List<string> ids, string guid)
    {
        var colon = guid.IndexOf("://", StringComparison.Ordinal);
        if (colon <= 0 || colon + 3 >= guid.Length)
            return;
        var scheme = guid[..colon].ToLowerInvariant();
        var value = guid[(colon + 3)..].Trim();
        if (value.Length == 0)
            return;
        if (scheme is not ("imdb" or "tmdb" or "tvdb" or "mbid"))
            return;
        var id = scheme + ":" + value;
        if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            ids.Add(id);
    }

    private static string FirstFile(JsonElement item)
    {
        if (!Prop(item, "Media", out var media) || media.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var entry in media.EnumerateArray())
        {
            if (!Prop(entry, "Part", out var parts) || parts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in parts.EnumerateArray())
            {
                var file = String(part, "file");
                if (file.Length > 0)
                    return file;
            }
        }

        return "";
    }

    private static bool Container(JsonElement root, out JsonElement container)
    {
        if (root.ValueKind == JsonValueKind.Object && Prop(root, "MediaContainer", out container))
            return true;
        container = root;
        return root.ValueKind == JsonValueKind.Object;
    }

    private static bool Prop(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;
        if (name.Length > 0)
        {
            var camel = char.ToLowerInvariant(name[0]) + name[1..];
            if (!camel.Equals(name, StringComparison.Ordinal) && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(camel, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static string String(JsonElement element, string name)
    {
        if (!Prop(element, name, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
    }

    private static int? Int(JsonElement element, string name)
    {
        if (!Prop(element, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return null;
    }
}

public sealed record PlexSection(string Key, string Title, string Type);

public readonly record struct PlexPage(IReadOnlyList<PlexHit> Hits, int? Total);

public static class PlexDocument
{
    public static PlexPage Read(string body, string sectionTitle)
    {
        var text = body.Trim();
        if (text.StartsWith('<'))
            return ReadXml(text, sectionTitle);
        using var document = JsonDocument.Parse(text);
        if (!Container(document.RootElement, out var container))
            return new PlexPage([], null);
        var hits = new List<PlexHit>();
        if (Prop(container, "Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in metadata.EnumerateArray())
            {
                var hit = ReadHit(item, sectionTitle);
                if (hit is not null)
                    hits.Add(hit);
            }
        }

        int? total = Int(container, "totalSize") ?? Int(container, "size");
        return new PlexPage(hits, total);
    }

    public static bool MorePages(int fetched, int pageCount, int pageSize, int? total)
    {
        if (pageCount < pageSize)
            return false;
        if (total is int count && fetched >= count)
            return false;
        return pageCount > 0;
    }

    private static PlexPage ReadXml(string xml, string sectionTitle)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root;
        if (root is null)
            return new PlexPage([], null);
        var hits = new List<PlexHit>();
        foreach (var item in root.Elements())
        {
            if (item.Name.LocalName is not ("Video" or "Directory" or "Track"))
                continue;
            var type = Attr(item, "type");
            if (type is not ("movie" or "episode" or "album" or "track" or "show"))
                continue;
            var file = item.Elements("Media").Elements("Part").Select(part => Attr(part, "file")).FirstOrDefault(value => value.Length > 0) ?? "";
            var split = PlexPath.Split(file, sectionTitle);
            var title = Attr(item, "title");
            var show = Attr(item, "grandparentTitle");
            var year = IntAttr(item, "year") ?? IntAttr(item, "grandparentYear");
            if (type == "episode" && show.Length > 0)
            {
                var season = IntAttr(item, "parentIndex");
                var episode = IntAttr(item, "index");
                if (season is not null && episode is not null)
                    title = show + " · S" + season.Value.ToString(CultureInfo.InvariantCulture) + "E" + episode.Value.ToString("00", CultureInfo.InvariantCulture);
            }
            else if (year is >= 1900 && title.Length > 0 && !title.Contains("(" + year.Value.ToString(CultureInfo.InvariantCulture) + ")", StringComparison.Ordinal))
                title += " (" + year.Value.ToString(CultureInfo.InvariantCulture) + ")";
            var ids = item.Elements("Guid").Select(guid => Attr(guid, "id")).Where(id => id.Length > 0).ToList();
            var own = Attr(item, "guid");
            if (own.Length > 0)
                ids.Insert(0, own);
            var parsed = new List<string>();
            foreach (var id in ids)
                PlexJson.AddGuid(parsed, id);
            long addedUnix = 0;
            var addedText = Attr(item, "addedAt");
            DateTimeOffset? added = long.TryParse(addedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out addedUnix) && addedUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(addedUnix)
                : null;
            hits.Add(new PlexHit(
                title,
                type,
                split.LibraryFolder,
                split.RelativeFolder,
                parsed,
                added,
                type == "episode" ? IntAttr(item, "parentIndex") : null,
                type == "episode" ? IntAttr(item, "index") : null,
                Attr(item, "ratingKey") is { Length: > 0 } rating ? rating : null,
                type == "episode" ? show : null,
                year));
        }

        var total = IntAttr(root, "totalSize") ?? IntAttr(root, "size");
        return new PlexPage(hits, total);
    }

    private static string Attr(XElement element, string name)
        => element.Attribute(name)?.Value.Trim() ?? "";

    private static int? IntAttr(XElement element, string name)
        => int.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool Container(JsonElement root, out JsonElement container)
    {
        if (root.ValueKind == JsonValueKind.Object && Prop(root, "MediaContainer", out container))
            return true;
        container = root;
        return root.ValueKind == JsonValueKind.Object;
    }

    private static bool Prop(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;
        if (name.Length > 0)
        {
            var camel = char.ToLowerInvariant(name[0]) + name[1..];
            if (!camel.Equals(name, StringComparison.Ordinal) && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(camel, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static int? Int(JsonElement element, string name)
    {
        if (!Prop(element, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static PlexHit? ReadHit(JsonElement item, string sectionTitle)
        => PlexJson.ReadHit(item, sectionTitle);
}

public static class PlexMatch
{
    public static string GuidValue(string id)
    {
        var colon = id.IndexOf(':');
        if (colon <= 0)
            return id;
        return id[..colon] + "://" + id[(colon + 1)..];
    }

    public static string? ImdbInPath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(relative, @"\btt\d{5,}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? "imdb:" + match.Value : null;
    }

    public static IReadOnlyList<PlexQuery> From(IReadOnlyList<ArrItem> items, IReadOnlyList<PushFolder> folders)
    {
        var queries = new List<PlexQuery>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var guids = new List<string>();
            Add(guids, item.ExternalId);
            Add(guids, item.SeriesGuid);
            Add(guids, item.AlsoGuid);
            Add(guids, ImdbInPath(item.RelativeFolder));
            var key = item.LibraryTag + "|" + (item.Season?.ToString() ?? "") + "|" + (item.Episode?.ToString() ?? "") + "|"
                + string.Join(",", guids) + "|" + FolderKey.Normalize(item.RelativeFolder);
            if (guids.Count == 0 && item.RelativeFolder.Length == 0)
                continue;
            if (seen.Add(key))
                queries.Add(new PlexQuery(key, item.LibraryTag, item.RelativeFolder, guids, item.Season, item.Episode));
        }

        foreach (var folder in folders)
        {
            if (folder.RelativeFolder.Length == 0)
                continue;
            var imdb = ImdbInPath(folder.RelativeFolder);
            var guids = imdb is null ? Array.Empty<string>() : new[] { imdb };
            var key = folder.LibraryTag + "|||" + string.Join(",", guids) + "|" + FolderKey.Normalize(folder.RelativeFolder);
            if (seen.Add(key))
                queries.Add(new PlexQuery(key, folder.LibraryTag, folder.RelativeFolder, guids, null, null));
        }

        return queries;
    }

    public static string CacheKey(string url, PlexQuery query)
    {
        var show = query.LibraryTag == "TV"
            ? query.Guids.FirstOrDefault(id => id.StartsWith("tvdb:", StringComparison.OrdinalIgnoreCase))
                ?? query.Guids.FirstOrDefault(id => id.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
            : null;
        return show is null ? url + "|" + query.Key : url + "|show|" + show;
    }

    public static bool Covers(PlexQuery query, IReadOnlyList<PlexHit> hits)
    {
        if (query.Season is int season && query.Episode is int episode)
        {
            if (hits.Any(hit => hit.Kind == "episode" && hit.Season == season && hit.Episode == episode
                && (query.Guids.Count == 0 || hit.ExternalIds.Any(id => query.Guids.Contains(id, StringComparer.OrdinalIgnoreCase)))))
                return true;
        }

        foreach (var guid in query.Guids)
        {
            if (query.LibraryTag == "TV")
                continue;
            if (hits.Any(hit => hit.Kind is not ("episode" or "show") && hit.ExternalIds.Any(id => id.Equals(guid, StringComparison.OrdinalIgnoreCase))))
                return true;
        }

        if (query.RelativeFolder.Length == 0)
            return false;
        return hits.Any(hit =>
            hit.Kind != "show"
            && MediaLibrary.Same(hit.LibraryFolder, query.LibraryTag)
            && FolderKey.Matches(query.RelativeFolder, hit.RelativeFolder)
            && (query.Season is null || query.Episode is null || hit.Season is null || (hit.Season == query.Season && hit.Episode == query.Episode)));
    }

    private static void Add(List<string> guids, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && !guids.Contains(id, StringComparer.OrdinalIgnoreCase))
            guids.Add(id);
    }
}
