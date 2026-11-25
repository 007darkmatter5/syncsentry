using Microsoft.EntityFrameworkCore;
using SyncSentry.Alerts;
using SyncSentry.Data;
using SyncSentry.Pipeline;
using SyncSentry.Security;
using SyncSentry.Services;

namespace SyncSentry.Tests;

public class PipelineLogTests
{
    private const string Sample = """
        2026-11-25 07:42:37 OK copied: [Movies] The Witches of Eastwick (1987) (tt0094332) (9378868615 bytes)
        2026-11-25 07:42:37 run done [Movies] ok=1 failed=0
        2026-11-25 08:25:19 PLEX scan asked (1 new file(s)): [TV] M:\TV Shows\The Curse of Oak Island (2014) (tt3455408)\Season 14 via http://192.168.1.16:32400
        2026-11-25 08:25:19 OK copied: [TV] The Curse of Oak Island (2014) (tt3455408)/Season 14 (5990488193 bytes)
        2026-11-25 08:17:41 OK [TV] The Curse of Oak Island (2014) (tt3455408)/Season 14
        """;

    [Fact]
    public void Sample_lines_become_a_copied_movie_and_a_scanned_season()
    {
        var lines = UnasPushLog.Parse(Sample);
        var movie = Assert.Single(lines, line => line.Kind == PushLineKind.Copied && line.LibraryTag == "Movies");
        Assert.Equal("The Witches of Eastwick (1987) (tt0094332)", movie.RelativeFolder);
        Assert.Equal("Movies", movie.LibraryFolder);
        Assert.Equal(9378868615L, movie.Bytes);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 13, 42, 37, TimeSpan.Zero), movie.Timestamp);

        var summary = Assert.Single(lines, line => line.Kind == PushLineKind.RunSummary);
        Assert.Equal("Movies", summary.LibraryTag);
        Assert.Equal(1, summary.OkCount);
        Assert.Equal(0, summary.FailedCount);

        var scan = Assert.Single(lines, line => line.Kind == PushLineKind.ScanRequested);
        Assert.Equal("TV", scan.LibraryTag);
        Assert.Equal("TV Shows", scan.LibraryFolder);
        Assert.Equal("The Curse of Oak Island (2014) (tt3455408)/Season 14", scan.RelativeFolder);
        Assert.Equal(1, scan.NewFiles);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 14, 25, 19, TimeSpan.Zero), scan.Timestamp);
        Assert.DoesNotContain("32400", scan.RelativeFolder, StringComparison.Ordinal);

        var copied = Assert.Single(lines, line => line.Kind == PushLineKind.Copied && line.LibraryTag == "TV");
        Assert.Equal("The Curse of Oak Island (2014) (tt3455408)/Season 14", copied.RelativeFolder);
        Assert.Equal(5990488193L, copied.Bytes);

        var synced = Assert.Single(lines, line => line.Kind == PushLineKind.AlreadySynced);
        Assert.Equal(copied.RelativeFolder, synced.RelativeFolder);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 14, 17, 41, TimeSpan.Zero), synced.Timestamp);

        var folders = UnasPushLog.Fold(lines);
        var witches = Assert.Single(folders, folder => folder.LibraryTag == "Movies");
        Assert.False(witches.Failed);
        Assert.Equal(movie.Timestamp, witches.CopiedUtc);
        Assert.Equal(9378868615L, witches.Bytes);

        var oak = Assert.Single(folders, folder => folder.LibraryTag == "TV");
        Assert.False(oak.Failed);
        Assert.Equal(synced.Timestamp, oak.CopiedUtc);
        Assert.Equal(scan.Timestamp, oak.ScanUtc);
        Assert.Equal(5990488193L, oak.Bytes);
    }

    [Fact]
    public void A_line_that_is_not_ok_is_a_possible_failure_for_that_folder()
    {
        var text = """
            2026-11-25 09:01:02 start [Movies]
            2026-11-25 09:01:05 could not copy [Movies] Some Movie (2020): permission denied
            2026-11-25 09:01:05 run done [Movies] ok=0 failed=1
            """;
        var lines = UnasPushLog.Parse(text);
        Assert.Equal(2, lines.Count(line => line.Kind == PushLineKind.PossibleFailure));
        var folder = Assert.Single(UnasPushLog.Fold(lines));
        Assert.Equal("Some Movie (2020)", folder.RelativeFolder);
        Assert.True(folder.Failed);
        Assert.Contains("permission denied", folder.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_successful_run_is_not_failed_by_a_harmless_extra_line()
    {
        var text = """
            2026-11-25 09:03:00 checking share
            2026-11-25 09:03:01 OK copied: [Movies] Good Movie (1999) (10 bytes)
            2026-11-25 09:03:01 run done [Movies] ok=1 failed=0
            """;
        var lines = UnasPushLog.Parse(text);
        Assert.Contains(lines, line => line.Kind == PushLineKind.PossibleFailure);
        var folder = Assert.Single(UnasPushLog.Fold(lines));
        Assert.False(folder.Failed);
        Assert.Equal("Good Movie (1999)", folder.RelativeFolder);
        Assert.Equal(10L, folder.Bytes);
    }

    [Fact]
    public void Imported_more_than_two_hours_ago_and_not_copied_is_stuck()
    {
        var now = new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);
        var imported = now.AddHours(-3);
        var input = new PipelineInput(
            now,
            7,
            [],
            [
                new ArrItem("Radarr", "movie", "holdovers", "The Holdovers (2023)", "Movies", "Movies", "The Holdovers (2023)", null, "imdb:tt14849194", imported.AddMinutes(-20), imported, false, null)
            ],
            [],
            Configured,
            new HashSet<string>(),
            false,
            false);
        var item = Assert.Single(PipelineBuilder.Build(input).Items);
        Assert.Equal("stuck", item.Badge);
        Assert.Contains("not copied", item.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("stuck", item.Stages.Single(stage => stage.Name == "Copied").State);
        Assert.Equal("done", item.Stages.Single(stage => stage.Name == "Imported").State);
    }

    [Fact]
    public void An_unconfigured_app_leaves_its_stage_unknown()
    {
        var now = new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);
        var lines = UnasPushLog.Parse(Sample);
        var input = new PipelineInput(now, 7, lines, [], [], None, new HashSet<string>(), false, false);
        var snapshot = PipelineBuilder.Build(input);
        Assert.Contains(snapshot.Hints, hint => hint.Settings && hint.Text.Contains("Sonarr", StringComparison.Ordinal));
        Assert.Contains(snapshot.Hints, hint => hint.Settings && hint.Text.Contains("Plex", StringComparison.Ordinal));
        var oak = Assert.Single(snapshot.Items, item => item.Title.Contains("Oak Island", StringComparison.Ordinal));
        Assert.Equal("unknown", oak.Stages.Single(stage => stage.Name == "Downloaded").State);
        Assert.Equal("unknown", oak.Stages.Single(stage => stage.Name == "Imported").State);
        Assert.Equal("done", oak.Stages.Single(stage => stage.Name == "Copied").State);
        Assert.Equal("done", oak.Stages.Single(stage => stage.Name == "Scan asked").State);
        Assert.Equal("unknown", oak.Stages.Single(stage => stage.Name == "In Plex").State);
    }

    [Fact]
    public void A_radarr_movie_picks_up_the_copy_from_the_sample_log()
    {
        var copiedAt = new DateTimeOffset(2026, 11, 25, 13, 42, 37, TimeSpan.Zero);
        var now = copiedAt.AddMinutes(20);
        var movie = new ArrItem(
            "Radarr",
            "movie",
            "witches",
            "The Witches of Eastwick (1987)",
            "Movies",
            "Movies",
            "The Witches of Eastwick (1987) (tt0094332)",
            null,
            "imdb:tt0094332",
            copiedAt.AddMinutes(-10),
            copiedAt.AddMinutes(-5),
            false,
            null);
        var plex = new PlexHit("The Witches of Eastwick (1987)", "movie", "Movies", movie.RelativeFolder, ["imdb:tt0094332"], copiedAt.AddMinutes(8));
        var input = new PipelineInput(now, 7, UnasPushLog.Parse(Sample), [movie], [plex], Configured, new HashSet<string>(), false, false);
        var item = Assert.Single(PipelineBuilder.Build(input).Items, row => row.Title.Contains("Witches", StringComparison.Ordinal));
        Assert.Equal("in-plex", item.Badge);
        Assert.Equal("done", item.Stages.Single(stage => stage.Name == "Copied").State);
        Assert.Equal(copiedAt, item.Stages.Single(stage => stage.Name == "Copied").At);
        Assert.Equal("done", item.Stages.Single(stage => stage.Name == "In Plex").State);
    }

    [Fact]
    public void Sonarr_history_groups_a_grab_and_an_import_without_using_the_show_id_as_the_episode()
    {
        var json = """
            {"records":[
              {"sourceTitle":"Oak.S14E05","date":"2026-11-25T14:00:00Z","eventType":"grabbed","seriesId":5,"episodeId":9,
               "series":{"title":"The Curse of Oak Island","year":2014,"path":"/tv/The Curse of Oak Island (2014) (tt3455408)","imdbId":"tt3455408"},
               "episode":{"id":9,"seasonNumber":14,"episodeNumber":5,"title":"Dig"}},
              {"sourceTitle":"Oak.S14E05","date":"2026-11-25T14:10:00Z","eventType":"downloadFolderImported","seriesId":5,"episodeId":9,
               "series":{"title":"The Curse of Oak Island","year":2014,"path":"/tv/The Curse of Oak Island (2014) (tt3455408)"},
               "episode":{"id":9,"seasonNumber":14,"episodeNumber":5,"title":"Dig"},
               "data":{"importedPath":"/tv/The Curse of Oak Island (2014) (tt3455408)/Season 14/Oak.mkv"}}
            ]}
            """;
        var item = Assert.Single(ArrHistoryParser.Read("Sonarr", json));
        Assert.Equal("The Curse of Oak Island (2014) · S14E05", item.Title);
        Assert.Equal("The Curse of Oak Island (2014) (tt3455408)/Season 14", item.RelativeFolder);
        Assert.Equal("The Curse of Oak Island (2014) (tt3455408)/Season 14/Oak.mkv", item.RelativeFile);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T14:00:00Z"), item.GrabbedUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T14:10:00Z"), item.ImportedUtc);
        Assert.Null(item.ExternalId);
    }

    [Fact]
    public void Plex_json_maps_a_windows_path_onto_the_library_folder()
    {
        var json = """
            {"MediaContainer":{"Metadata":[
              {"type":"movie","title":"The Witches of Eastwick","year":1987,"addedAt":1764070957,
               "Guid":[{"id":"imdb://tt0094332"}],
               "Media":[{"Part":[{"file":"M:\\Movies\\The Witches of Eastwick (1987) (tt0094332)\\The Witches of Eastwick (1987).mkv"}]}]}
            ]}}
            """;
        var hit = Assert.Single(PlexJson.Hits(json, "Movies"));
        Assert.Equal("Movies", hit.LibraryFolder);
        Assert.Equal("The Witches of Eastwick (1987) (tt0094332)", hit.RelativeFolder);
        Assert.Contains("imdb:tt0094332", hit.ExternalIds);
        Assert.Equal("The Witches of Eastwick (1987)", hit.Title);
    }

    [Fact]
    public async Task Pipeline_settings_store_the_key_and_do_not_return_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "t.db")).Options;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.Settings.Add(new InstanceSetting { UpdatedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var store = new SettingsStore(new Factory(options), new PlainProtector(), new QuietNtfy(), TimeProvider.System);
        var error = await store.SavePipelineAsync(new PipelineEdit
        {
            SonarrUrl = "http://sonarr:8989",
            NewSonarrApiKey = "sonarr-api-key-value",
            RadarrUrl = "http://radarr:7878",
            NewRadarrApiKey = "radarr-api-key-value",
            PlexUrl = "http://192.168.1.16:32400",
            NewPlexToken = "plex-token-value"
        });
        Assert.Null(error);

        var loaded = await store.LoadPipelineAsync();
        Assert.Equal("http://sonarr:8989", loaded.SonarrUrl);
        Assert.True(loaded.HasSonarrApiKey);
        Assert.Equal("", loaded.NewSonarrApiKey);
        Assert.True(loaded.HasRadarrApiKey);
        Assert.Equal("", loaded.NewRadarrApiKey);
        Assert.False(loaded.HasLidarrApiKey);
        Assert.True(loaded.HasPlexToken);
        Assert.Equal("", loaded.NewPlexToken);
        Assert.DoesNotContain("sonarr-api-key-value", System.Text.Json.JsonSerializer.Serialize(loaded), StringComparison.Ordinal);

        await using var check = new ApplicationDbContext(options);
        var saved = await check.Settings.SingleAsync();
        Assert.Equal("enc:sonarr-api-key-value", saved.SonarrApiKeyProtected);
        Assert.Equal("enc:plex-token-value", saved.PlexTokenProtected);
        Assert.DoesNotContain("sonarr-api-key-value", saved.SonarrUrl, StringComparison.Ordinal);

        var kept = await store.SavePipelineAsync(new PipelineEdit
        {
            SonarrUrl = "http://sonarr:8989",
            HasSonarrApiKey = true,
            RadarrUrl = "http://radarr:7878",
            HasRadarrApiKey = true,
            PlexUrl = "http://192.168.1.16:32400",
            HasPlexToken = true
        });
        Assert.Null(kept);
        await using var again = new ApplicationDbContext(options);
        Assert.Equal("enc:sonarr-api-key-value", (await again.Settings.SingleAsync()).SonarrApiKeyProtected);
    }

    private static PipelineSources Configured { get; } = new(
        new SourceLink(true, null),
        new SourceLink(true, null),
        new SourceLink(true, null),
        new SourceLink(true, null));

    private static PipelineSources None { get; } = new(
        new SourceLink(false, null),
        new SourceLink(false, null),
        new SourceLink(false, null),
        new SourceLink(false, null));

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string protectedPayload) => protectedPayload["enc:".Length..];
    }

    private sealed class QuietNtfy : INtfyClient
    {
        public Task<NtfySendResult> SendAsync(NtfyTarget target, string title, string body, CancellationToken cancellationToken)
            => Task.FromResult(new NtfySendResult(true, "ok"));
    }
}
