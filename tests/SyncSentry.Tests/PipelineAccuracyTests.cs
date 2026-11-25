using System.Net;
using SyncSentry.Pipeline;

namespace SyncSentry.Tests;

public class PipelineAccuracyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_grab_imported_later_under_another_episode_id_is_not_stuck()
    {
        var json = """
            {"records":[
              {"sourceTitle":"Oak.S14E05","date":"2026-11-25T12:00:00Z","eventType":"grabbed","downloadId":"dl-1","episodeId":1,
               "episode":{"id":1,"seasonNumber":14,"episodeNumber":5,"hasFile":false},
               "series":{"title":"The Curse of Oak Island","year":2014,"tvdbId":3455408,"imdbId":"tt3455408","path":"/tv/The Curse of Oak Island (2014) (tt3455408)"}},
              {"sourceTitle":"Oak.S14E05","date":"2026-11-25T15:00:00Z","eventType":"downloadFolderImported","downloadId":"dl-1","episodeId":99,
               "episode":{"id":99,"seasonNumber":14,"episodeNumber":5,"hasFile":true},
               "series":{"title":"The Curse of Oak Island","year":2014,"tvdbId":3455408,"path":"/tv/The Curse of Oak Island (2014) (tt3455408)"},
               "data":{"importedPath":"/tv/The Curse of Oak Island (2014) (tt3455408)/Season 14/ep.mkv"}}
            ]}
            """;
        var item = Assert.Single(ArrHistoryParser.Read("Sonarr", json));
        Assert.NotNull(item.ImportedUtc);
        Assert.Equal(14, item.Season);
        Assert.Equal(5, item.Episode);
        Assert.Equal("tvdb:3455408", item.SeriesGuid);
        var shown = Show(item with { InQueue = false }, DateTimeOffset.Parse("2026-11-25T15:20:00Z"));
        Assert.NotEqual("stuck", shown.Badge);
        Assert.Equal("done", shown.Stages.Single(stage => stage.Name == "Imported").State);
    }

    [Fact]
    public void A_grab_for_an_episode_that_already_has_a_file_is_not_stuck()
    {
        var json = """
            {"records":[
              {"sourceTitle":"Show.S01E01","date":"2026-11-25T10:00:00Z","eventType":"grabbed","downloadId":"old",
               "episode":{"id":4,"seasonNumber":1,"episodeNumber":1,"hasFile":true},
               "episodeFile":{"path":"/tv/Show/Season 1/ep.mkv"},
               "series":{"title":"Show","year":2014,"tvdbId":9,"path":"/tv/Show"}}
            ]}
            """;
        var item = Assert.Single(ArrHistoryParser.Read("Sonarr", json));
        Assert.True(item.HasFile);
        Assert.Null(item.ImportedUtc);
        Assert.Contains("Show/Season 1", item.RelativeFolder, StringComparison.Ordinal);
        var shown = Show(item with { InQueue = false });
        Assert.NotEqual("stuck", shown.Badge);
        Assert.Equal("done", shown.Stages.Single(stage => stage.Name == "Imported").State);
    }

    [Fact]
    public void An_ignored_or_blocklisted_grab_is_not_stuck()
    {
        var json = """
            {"records":[
              {"sourceTitle":"Show.S01E02","date":"2026-11-25T08:00:00Z","eventType":"grabbed","downloadId":"gone","episodeId":8,
               "episode":{"id":8,"seasonNumber":1,"episodeNumber":2,"hasFile":false},
               "series":{"title":"Show","tvdbId":9,"path":"/tv/Show"}},
              {"sourceTitle":"Show.S01E02","date":"2026-11-25T09:00:00Z","eventType":"downloadIgnored","downloadId":"gone","episodeId":8,
               "episode":{"id":8,"seasonNumber":1,"episodeNumber":2,"hasFile":false},
               "series":{"title":"Show","tvdbId":9,"path":"/tv/Show"}}
            ]}
            """;
        var item = Assert.Single(ArrHistoryParser.Read("Sonarr", json));
        Assert.True(item.Dismissed);
        var shown = Show(item with { InQueue = false });
        Assert.NotEqual("stuck", shown.Badge);
        Assert.Equal("pending", shown.Stages.Single(stage => stage.Name == "Imported").State);
    }

    [Fact]
    public void Only_a_grab_that_is_still_in_the_queue_is_stuck()
    {
        var grab = new ArrItem(
            "Sonarr", "episode", "episode:3", "Show (2014) · S02E03", "TV", "TV Shows", "", null, null,
            Now.AddHours(-4), null, false, null, false, false, true, "dl", 2, 3, "tvdb:9");
        var waiting = Show(grab);
        Assert.Equal("stuck", waiting.Badge);
        Assert.Contains("not imported", waiting.Problem, StringComparison.OrdinalIgnoreCase);

        var left = Show(grab with { InQueue = false });
        Assert.NotEqual("stuck", left.Badge);
        Assert.Equal("pending", left.Stages.Single(stage => stage.Name == "Imported").State);

        var unknown = Show(grab with { InQueue = null });
        Assert.NotEqual("stuck", unknown.Badge);
    }

    [Fact]
    public void Queue_records_mark_the_matching_download()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = ArrHistoryParser.ReadQueue("""{"records":[{"episodeId":3,"downloadId":"dl-9"},{"movieId":4}]}""", keys);
        Assert.Equal(2, count);
        var queued = Assert.Single(ArrHistoryParser.ApplyQueue(
            [new ArrItem("Sonarr", "episode", "episode:3", "Show", "TV", "TV Shows", "", null, null, Now, null, false, null, DownloadId: "other")],
            keys));
        Assert.True(queued.InQueue);
        var elsewhere = Assert.Single(ArrHistoryParser.ApplyQueue(
            [new ArrItem("Radarr", "movie", "movie:8", "Movie", "Movies", "Movies", "", null, null, Now, null, false, null, DownloadId: "nope")],
            keys));
        Assert.False(elsewhere.InQueue);
        Assert.Null(Assert.Single(ArrHistoryParser.ApplyQueue(
            [new ArrItem("Sonarr", "episode", "episode:3", "Show", "TV", "TV Shows", "", null, null, Now, null, false, null)],
            null)).InQueue);
    }

    [Fact]
    public void Plex_guid_and_path_lookup_confirm_a_movie_and_an_episode()
    {
        var movieJson = """
            {"MediaContainer":{"Metadata":[
              {"ratingKey":"10","type":"movie","title":"The Witches of Eastwick","year":1987,"addedAt":1795608000,
               "Guid":[{"id":"imdb://tt0094332"}],
               "Media":[{"Part":[{"file":"M:\\Movies\\The Witches of Eastwick (1987) (tt0094332)\\movie.mkv"}]}]}
            ]}}
            """;
        var movie = Assert.Single(PlexJson.Hits(movieJson, "Movies"));
        Assert.Equal("Movies", movie.LibraryFolder);
        Assert.Equal("The Witches of Eastwick (1987) (tt0094332)", movie.RelativeFolder);
        Assert.Contains("imdb:tt0094332", movie.ExternalIds);

        var episodeJson = """
            {"MediaContainer":{"Metadata":[
              {"ratingKey":"55","type":"episode","title":"Dig","parentIndex":14,"index":5,"addedAt":1795608000,
               "grandparentTitle":"The Curse of Oak Island",
               "Media":[{"Part":[{"file":"M:\\TV Shows\\The Curse of Oak Island (2014) (tt3455408)\\Season 14\\ep.mkv"}]}]}
            ]}}
            """;
        var episode = Assert.Single(PlexJson.Hits(episodeJson, "TV Shows"));
        Assert.Equal(14, episode.Season);
        Assert.Equal(5, episode.Episode);
        Assert.Equal("The Curse of Oak Island (2014) (tt3455408)/Season 14", episode.RelativeFolder);

        var hits = new List<PlexHit> { movie, episode with { ExternalIds = episode.ExternalIds.Append("tvdb:3455408").ToArray() } };
        Assert.Equal("done", InPlex("Movies", movie.RelativeFolder, "imdb:tt0094332", null, null, null, null, hits));
        Assert.Equal("done", InPlex("TV", "different/folder", null, "tvdb:3455408", null, 14, 5, hits));
        Assert.Equal("pending", InPlex("TV", "different/folder", null, "tvdb:3455408", null, 14, 6, hits));
        Assert.Equal("imdb:tt3455408", PlexMatch.ImdbInPath("Show (2014) (tt3455408)/Season 1"));
    }

    [Fact]
    public async Task Plex_lookups_are_cached_so_the_same_show_is_not_asked_again()
    {
        var handler = new ScriptedPlex();
        var catalog = new PlexCatalogClient(new OneClient(handler));
        var query = new PlexQuery("movie|tt0094332", "Movies", "The Witches of Eastwick (1987) (tt0094332)", ["imdb:tt0094332"], null, null);
        var first = await catalog.FetchAsync("http://plex.local:32400", "token-value", [query], CancellationToken.None);
        Assert.True(first.Ok);
        Assert.Contains(first.Hits, hit => hit.ExternalIds.Contains("imdb:tt0094332"));
        var calls = handler.Calls;
        Assert.True(calls >= 2);
        var second = await catalog.FetchAsync("http://plex.local:32400", "token-value", [query], CancellationToken.None);
        Assert.True(second.Ok);
        Assert.Equal(calls, handler.Calls);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("token-value", StringComparison.Ordinal));
    }

    private static PipelineItem Show(ArrItem item, DateTimeOffset? now = null)
    {
        var sources = new PipelineSources(new SourceLink(true, null), new SourceLink(true, null), new SourceLink(false, null), new SourceLink(false, null));
        return Assert.Single(PipelineBuilder.Build(new PipelineInput(now ?? Now, 7, [], [item], [], sources, new HashSet<string>(), false, false)).Items);
    }

    private static string InPlex(string library, string relative, string? externalId, string? series, string? also, int? season, int? episode, IReadOnlyList<PlexHit> hits)
    {
        var item = new ArrItem("x", library == "TV" ? "episode" : "movie", "id", "title", library, MediaLibrary.Folder(library), relative, null, externalId, null, null, false, null, Season: season, Episode: episode, SeriesGuid: series, AlsoGuid: also);
        var sources = new PipelineSources(new SourceLink(true, null), new SourceLink(true, null), new SourceLink(true, null), new SourceLink(true, null));
        var shown = Assert.Single(PipelineBuilder.Build(new PipelineInput(Now, 7, [], [item], hits, sources, new HashSet<string>(), true, true)).Items);
        return shown.Stages.Single(stage => stage.Name == "In Plex").State;
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class ScriptedPlex : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var path = request.RequestUri?.PathAndQuery ?? "";
            Paths.Add(path);
            var body = path.Contains("/library/sections", StringComparison.Ordinal) && !path.Contains("/all", StringComparison.Ordinal) && !path.Contains("recentlyAdded", StringComparison.Ordinal)
                ? """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies"}]}}"""
                : path.Contains("guid=", StringComparison.Ordinal)
                    ? """{"MediaContainer":{"Metadata":[{"ratingKey":"10","type":"movie","title":"The Witches of Eastwick","year":1987,"addedAt":1795608000,"Guid":[{"id":"imdb://tt0094332"}],"Media":[{"Part":[{"file":"M:\\Movies\\The Witches of Eastwick (1987) (tt0094332)\\movie.mkv"}]}]}]}}"""
                    : """{"MediaContainer":{"Metadata":[]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
