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
    DateTimeOffset? AddedUtc);

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
    public async Task<PlexFetch> FetchAsync(string url, string token, CancellationToken cancellationToken)
    {
        if (!Rules.RemoteUrl(url) || string.IsNullOrWhiteSpace(token))
            return PlexFetch.NotConfigured;

        try
        {
            var client = http.CreateClient("plex");
            var sectionsBody = await GetAsync(client, url, token, "/library/sections", cancellationToken);
            if (sectionsBody is null)
                return PlexFetch.Failed("refused");
            var hits = new List<PlexHit>();
            foreach (var section in PlexJson.Sections(sectionsBody))
            {
                if (section.Type is not ("movie" or "show" or "artist"))
                    continue;
                var recent = await GetAsync(
                    client,
                    url,
                    token,
                    "/library/sections/" + Uri.EscapeDataString(section.Key) + "/recentlyAdded?X-Plex-Container-Start=0&X-Plex-Container-Size=200",
                    cancellationToken);
                if (recent is null)
                    return PlexFetch.Failed("refused");
                hits.AddRange(PlexJson.Hits(recent, section.Title));
            }

            return new PlexFetch(true, true, null, hits);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return PlexFetch.Failed("unreachable");
        }
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
        DateTimeOffset? added = null;
        if (Prop(item, "addedAt", out var addedAt) && addedAt.TryGetInt64(out var unix) && unix > 0)
            added = DateTimeOffset.FromUnixTimeSeconds(unix);
        return new PlexHit(title, type, split.LibraryFolder, split.RelativeFolder, ids, added);
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
