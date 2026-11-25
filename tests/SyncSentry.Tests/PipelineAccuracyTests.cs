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
    public void A_file_already_in_plex_stays_in_plex_when_a_later_upgrade_fails()
    {
        var movie = new ArrItem(
            "Radarr", "movie", "movie:42", "Batman Begins (2005)", "Movies", "Movies",
            "Batman Begins (2005)", null, "imdb:tt0372784", Now.AddHours(-5), null, true, "upgrade rejected",
            HasFile: true);
        var hit = new PlexHit("Batman Begins", "movie", "Movies", "Batman Begins (2005)", ["imdb:tt0372784"], Now.AddHours(-1), Year: 2005);
        var sources = new PipelineSources(new SourceLink(false, null), new SourceLink(true, null), new SourceLink(false, null), new SourceLink(true, null));
        var shown = Assert.Single(PipelineBuilder.Build(new PipelineInput(Now, 7, [], [movie], [hit], sources, new HashSet<string>(), false, false)).Items);
        Assert.Equal("in-plex", shown.Badge);
        Assert.Equal("Upgrade download failed.", shown.Problem);
        Assert.Equal("done", shown.Stages.Single(stage => stage.Name == "Downloaded").State);

        var alreadyImported = movie with { ImportedUtc = Now.AddMinutes(-20) };
        var kept = Assert.Single(PipelineBuilder.Build(new PipelineInput(Now, 7, [], [alreadyImported], [hit], sources, new HashSet<string>(), false, false)).Items);
        Assert.Equal("in-plex", kept.Badge);
        Assert.Equal("Upgrade download failed.", kept.Problem);

        var missed = movie with { Identity = "movie:99", Title = "Not In Library (2026)", RelativeFolder = "Not In Library (2026)", ExternalId = "imdb:tt0000001" };
        var failed = Assert.Single(PipelineBuilder.Build(new PipelineInput(Now, 7, [], [missed], [hit], sources, new HashSet<string>(), false, false)).Items);
        Assert.Equal("failed", failed.Badge);
    }

    [Fact]
    public void Sonarr_episode_files_supply_the_season_folder_when_history_has_no_path()
    {
        var json = """
            {"records":[
              {"sourceTitle":"The.Chosen.S04E01","date":"2026-11-25T12:00:00Z","eventType":"grabbed","seriesId":12,"episodeId":70,
               "episode":{"id":70,"seasonNumber":4,"episodeNumber":1,"hasFile":true},
               "series":{"id":12,"title":"The Chosen","year":2019,"path":"/mnt/user/data/media/TV Shows/The Chosen (2019) (tt5622316)"}}
            ]}
            """;
        var grabbed = Assert.Single(ArrHistoryParser.Read("Sonarr", json));
        Assert.Equal("", grabbed.RelativeFolder);
        Assert.Equal(12, grabbed.SeriesId);
        var files = """
            [{"seriesId":12,"seasonNumber":4,"path":"/mnt/user/data/media/TV Shows/The Chosen (2019) (tt5622316)/Season 04/The Chosen - S04E01.mkv","episodeIds":[70,71]}]
            """;
        var fromFile = Assert.Single(ArrHistoryParser.ApplyLibraryPaths(ArrHistoryParser.Read("Sonarr", json), 12, files, null));
        Assert.Equal("The Chosen (2019) (tt5622316)/Season 04", fromFile.RelativeFolder);

        var seriesOnly = """
            {"path":"/mnt/user/data/media/TV Shows/The Chosen (2019) (tt5622316)","seasons":[{"seasonNumber":4,"seasonFolder":"Season 04"}]}
            """;
        var fromSeries = Assert.Single(ArrHistoryParser.ApplyLibraryPaths(ArrHistoryParser.Read("Sonarr", json), 12, "[]", seriesOnly));
        Assert.Equal("The Chosen (2019) (tt5622316)/Season 04", fromSeries.RelativeFolder);
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
    public async Task Plex_index_is_paged_and_then_cached()
    {
        var previous = PlexCatalogClient.PageSize;
        PlexCatalogClient.PageSize = 1;
        try
        {
            var handler = new ScriptedPlex();
            var catalog = new PlexCatalogClient(new OneClient(handler));
            var first = await catalog.FetchAsync("http://plex.local:32400", "token-value", CancellationToken.None);
            Assert.True(first.Ok);
            Assert.Contains(first.Hits, hit => hit.ExternalIds.Contains("imdb:tt0120689") && hit.RelativeFolder.Contains("The Green Mile", StringComparison.Ordinal));
            Assert.Contains(first.Hits, hit => hit.Title.Contains("No Escape", StringComparison.Ordinal));
            Assert.Contains(handler.Paths, path => path.Contains("X-Plex-Container-Start=0", StringComparison.Ordinal) && path.Contains("includeGuids=1", StringComparison.Ordinal));
            Assert.Contains(handler.Paths, path => path.Contains("X-Plex-Container-Start=1", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Paths, path => path.Contains("guid=", StringComparison.Ordinal));
            var calls = handler.Calls;
            var second = await catalog.FetchAsync("http://plex.local:32400", "token-value", CancellationToken.None);
            Assert.Equal(calls, handler.Calls);
            Assert.Equal(first.Hits.Count, second.Hits.Count);
        }
        finally
        {
            PlexCatalogClient.PageSize = previous;
        }
    }

    [Fact]
    public void Plex_xml_index_reads_guids_paths_and_episode_numbers()
    {
        var xml = """
            <MediaContainer size="2" totalSize="2">
              <Video ratingKey="10" type="movie" title="The Manhattan Project" year="1986" addedAt="1795608000">
                <Guid id="imdb://tt0091472"/>
                <Guid id="tmdb://12345"/>
                <Media><Part file="M:\Movies\The Manhattan Project (1986)\movie.mkv"/></Media>
              </Video>
              <Video ratingKey="11" type="episode" title="The Chosen One" parentIndex="4" index="1" grandparentTitle="The Chosen" year="2019" addedAt="1795608000">
                <Guid id="tvdb://999"/>
                <Media><Part file="M:\TV Shows\The Chosen (2019) (tt5622316)\Season 04\episode.mkv"/></Media>
              </Video>
            </MediaContainer>
            """;
        var page = PlexDocument.Read(xml, "TV Shows");
        Assert.Equal(2, page.Hits.Count);
        var movie = page.Hits[0];
        Assert.Contains("imdb:tt0091472", movie.ExternalIds);
        Assert.Contains("tmdb:12345", movie.ExternalIds);
        Assert.Equal(1986, movie.Year);
        Assert.Equal("The Manhattan Project (1986)", movie.RelativeFolder);
        var episode = page.Hits[1];
        Assert.Equal(4, episode.Season);
        Assert.Equal(1, episode.Episode);
        Assert.Equal("The Chosen", episode.ShowTitle);
        Assert.Equal("The Chosen (2019) (tt5622316)/Season 04", episode.RelativeFolder);
        Assert.True(PlexDocument.MorePages(1, 1, 1, 2));
        Assert.False(PlexDocument.MorePages(2, 1, 1, 2));
    }

    [Fact]
    public void Season_folders_come_from_the_arr_file_and_match_zero_padding()
    {
        var series = "/mnt/user/data/media/TV Shows/The Chosen (2019) (tt5622316)";
        var file = series + "/Season 04/The Chosen - S04E01.mkv";
        Assert.Equal("The Chosen (2019) (tt5622316)/Season 04", LibraryPath.FolderFromFile(series, file));
        Assert.True(FolderKey.Matches("The Chosen (2019) (tt5622316)/Season 4", "The Chosen (2019) (tt5622316)/Season 04"));
        Assert.Equal(FolderKey.DiskKey("TV", "The Chosen (2019) (tt5622316)/Season 4"), FolderKey.DiskKey("TV", "The Chosen (2019) (tt5622316)/Season 04"));

        var json = """
            {"records":[
              {"sourceTitle":"The.Chosen.S04E01","date":"2026-11-25T12:00:00Z","eventType":"downloadFolderImported","episodeId":70,
               "episode":{"id":70,"seasonNumber":4,"episodeNumber":1,"hasFile":true},
               "episodeFile":{"path":"/mnt/user/data/media/TV Shows/The Chosen (2019) (tt5622316)/Season 04/The Chosen - S04E01.mkv"},
               "series":{"title":"The Chosen","year":2019,"tvdbId":362156,"imdbId":"tt5622316","path":"/mnt/user/data/media/TV Shows/The Chosen (2019) (tt5622316)"},
               "data":{"droppedPath":"/downloads/The.Chosen.S04E01.mkv","importedPath":"/mnt/user/data/media/TV Shows/The Chosen (2019) (tt5622316)/Season 04/The Chosen - S04E01.mkv"}}
            ]}
            """;
        var item = Assert.Single(ArrHistoryParser.Read("Sonarr", json));
        Assert.Equal("The Chosen (2019) (tt5622316)/Season 04", item.RelativeFolder);
        Assert.DoesNotContain("Season 4/", item.RelativeFolder + "/", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_padded_season_folder_counts_as_copied()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-season-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "TV Shows", "The Chosen (2019) (tt5622316)", "Season 04"));
        var checker = new FolderExists(new QuietMounts(), new SyncSentry.Host.StuckProcesses(), TimeProvider.System);
        var key = FolderKey.DiskKey("TV", "The Chosen (2019) (tt5622316)/Season 4");
        var present = await checker.PresentAsync(root, [(key, "TV Shows/The Chosen (2019) (tt5622316)/Season 4")], CancellationToken.None);
        Assert.Contains(key, present);
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
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

    private sealed class QuietMounts : SyncSentry.Host.IMountTable
    {
        public IReadOnlyList<SyncSentry.Host.MountEntry> Read() => [];
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
            string body;
            if (path.Contains("/library/sections", StringComparison.Ordinal) && !path.Contains("/all", StringComparison.Ordinal))
                body = """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Movies"}]}}""";
            else if (path.Contains("X-Plex-Container-Start=0", StringComparison.Ordinal))
                body = """{"MediaContainer":{"size":1,"totalSize":2,"Metadata":[{"ratingKey":"10","type":"movie","title":"The Green Mile","year":1999,"addedAt":1795608000,"Guid":[{"id":"imdb://tt0120689"},{"id":"tmdb://497"}],"Media":[{"Part":[{"file":"M:\\Movies\\The Green Mile (1999) (tt0120689)\\The Green Mile.mkv"}]}]}]}}""";
            else if (path.Contains("X-Plex-Container-Start=1", StringComparison.Ordinal))
                body = """{"MediaContainer":{"size":1,"totalSize":2,"Metadata":[{"ratingKey":"11","type":"movie","title":"No Escape","year":1994,"addedAt":1795608000,"Guid":[{"id":"imdb://tt0110678"}],"Media":[{"Part":[{"file":"M:\\Movies\\No Escape (1994) (tt0110678)\\No Escape.mkv"}]}]}]}}""";
            else
                body = """{"MediaContainer":{"size":0,"totalSize":0,"Metadata":[]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
