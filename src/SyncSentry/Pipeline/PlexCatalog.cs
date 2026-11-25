using System.Globalization;
using System.Net;
using System.Text.Json;

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
    string? RatingKey = null);

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
    Task<PlexFetch> FetchAsync(string url, string token, IReadOnlyList<PlexQuery> queries, CancellationToken cancellationToken);
}

public sealed class PlexCatalogClient(IHttpClientFactory http) : IPlexCatalog
{
    private static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SectionTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FoundTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(8);
    private const int LookupBudget = 24;

    private readonly object _gate = new();
    private (string Url, DateTimeOffset Until, IReadOnlyList<PlexSection> Sections)? _sections;
    private (string Url, DateTimeOffset Until, IReadOnlyList<PlexHit> Hits)? _recent;
    private readonly Dictionary<string, (DateTimeOffset Until, IReadOnlyList<PlexHit> Hits)> _lookups = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PlexFetch> FetchAsync(string url, string token, IReadOnlyList<PlexQuery> queries, CancellationToken cancellationToken)
    {
        if (!Rules.RemoteUrl(url) || string.IsNullOrWhiteSpace(token))
            return PlexFetch.NotConfigured;

        try
        {
            var client = http.CreateClient("plex");
            var now = DateTimeOffset.UtcNow;
            var sections = CachedSections(url, now);
            if (sections is null)
            {
                var sectionsBody = await GetAsync(client, url, token, "/library/sections", cancellationToken);
                if (sectionsBody is null)
                    return PlexFetch.Failed("refused");
                sections = PlexJson.Sections(sectionsBody);
                lock (_gate)
                    _sections = (url, now + SectionTtl, sections);
            }

            var cachedRecent = CachedRecent(url, now);
            var hits = new List<PlexHit>();
            if (cachedRecent is not null)
                hits.AddRange(cachedRecent);
            else
            {
                foreach (var section in sections)
                {
                    if (section.Type is not ("movie" or "show" or "artist"))
                        continue;
                    var recent = await GetAsync(
                        client,
                        url,
                        token,
                        "/library/sections/" + Uri.EscapeDataString(section.Key) + "/recentlyAdded?includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=100",
                        cancellationToken);
                    if (recent is null)
                        return PlexFetch.Failed("refused");
                    hits.AddRange(PlexJson.Hits(recent, section.Title));
                }

                lock (_gate)
                    _recent = (url, now + RecentTtl, hits.ToList());
            }

            var budget = LookupBudget;
            foreach (var query in queries)
            {
                if (PlexMatch.Covers(query, hits))
                    continue;
                var cacheKey = PlexMatch.CacheKey(url, query);
                if (TryCachedLookup(cacheKey, now, out var cached))
                {
                    hits.AddRange(cached);
                    continue;
                }

                if (budget <= 0)
                    continue;
                budget--;
                IReadOnlyList<PlexHit> found;
                try
                {
                    found = await LookupAsync(client, url, token, sections, query, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
                {
                    found = [];
                }

                lock (_gate)
                    _lookups[cacheKey] = (now + (found.Count > 0 ? FoundTtl : MissTtl), found);
                hits.AddRange(found);
            }

            return new PlexFetch(true, true, null, hits);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return PlexFetch.Failed("unreachable");
        }
    }

    private async Task<IReadOnlyList<PlexHit>> LookupAsync(
        HttpClient client,
        string url,
        string token,
        IReadOnlyList<PlexSection> sections,
        PlexQuery query,
        CancellationToken cancellationToken)
    {
        var section = SectionFor(sections, query.LibraryTag);
        if (section is null)
            return [];
        var guid = query.Guids.FirstOrDefault();
        if (guid is not null)
        {
            var type = query.LibraryTag == "TV" ? "&type=2" : query.LibraryTag == "Music" ? "&type=9" : "";
            var body = await GetAsync(
                client,
                url,
                token,
                "/library/sections/" + Uri.EscapeDataString(section.Key) + "/all?includeGuids=1" + type + "&guid=" + Uri.EscapeDataString(PlexMatch.GuidValue(guid)),
                cancellationToken);
            if (body is null)
                return [];
            var found = PlexJson.Hits(body, section.Title);
            if (query.LibraryTag != "TV")
                return found;
            var show = found.FirstOrDefault(hit => hit.Kind == "show" && !string.IsNullOrEmpty(hit.RatingKey));
            if (show is null)
                return found;
            var leaves = await GetAsync(
                client,
                url,
                token,
                "/library/metadata/" + Uri.EscapeDataString(show.RatingKey!) + "/allLeaves?includeGuids=1",
                cancellationToken);
            if (leaves is null)
                return found;
            var episodes = PlexJson.Hits(leaves, section.Title)
                .Select(hit => hit with { ExternalIds = hit.ExternalIds.Concat(query.Guids).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() })
                .ToList();
            return episodes.Count > 0 ? episodes : found;
        }

        var title = query.RelativeFolder.Split('/', 2)[0];
        if (title.Length == 0)
            return [];
        var leaf = query.LibraryTag == "TV" ? "4" : query.LibraryTag == "Music" ? "10" : "1";
        var searched = await GetAsync(
            client,
            url,
            token,
            "/library/sections/" + Uri.EscapeDataString(section.Key) + "/all?includeGuids=1&type=" + leaf + "&title=" + Uri.EscapeDataString(title) + "&X-Plex-Container-Size=8",
            cancellationToken);
        return searched is null ? [] : PlexJson.Hits(searched, section.Title);
    }

    private IReadOnlyList<PlexSection>? CachedSections(string url, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_sections is { } cached && cached.Url == url && cached.Until > now)
                return cached.Sections;
        }

        return null;
    }

    private IReadOnlyList<PlexHit>? CachedRecent(string url, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_recent is { } cached && cached.Url == url && cached.Until > now)
                return cached.Hits;
        }

        return null;
    }

    private bool TryCachedLookup(string key, DateTimeOffset now, out IReadOnlyList<PlexHit> hits)
    {
        lock (_gate)
        {
            if (_lookups.TryGetValue(key, out var cached) && cached.Until > now)
            {
                hits = cached.Hits;
                return true;
            }
        }

        hits = [];
        return false;
    }

    private static PlexSection? SectionFor(IReadOnlyList<PlexSection> sections, string libraryTag)
    {
        var type = libraryTag switch
        {
            "Movies" => "movie",
            "TV" => "show",
            "Music" => "artist",
            _ => ""
        };
        if (type.Length == 0)
            return null;
        var folder = MediaLibrary.Folder(libraryTag);
        return sections.FirstOrDefault(section => section.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && section.Title.Equals(folder, StringComparison.OrdinalIgnoreCase))
            ?? sections.FirstOrDefault(section => section.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
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
    {
        using var document = JsonDocument.Parse(json);
        if (!Container(document.RootElement, out var container))
            return [];
        if (!Prop(container, "Metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Array)
            return [];
        var hits = new List<PlexHit>();
        foreach (var item in metadata.EnumerateArray())
        {
            var hit = ReadHit(item, sectionTitle);
            if (hit is not null)
                hits.Add(hit);
        }

        return hits;
    }

    private static PlexHit? ReadHit(JsonElement item, string sectionTitle)
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
        return new PlexHit(title, type, split.LibraryFolder, split.RelativeFolder, ids, added, season, episode, rating.Length == 0 ? null : rating);
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

    private static void AddGuid(List<string> ids, string guid)
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
