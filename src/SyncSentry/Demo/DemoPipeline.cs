using SyncSentry.Pipeline;

namespace SyncSentry.Demo;

/// <summary>Sample movies, episodes, and push-log lines so the Pipeline page can be opened with no API keys.</summary>
public static class DemoPipeline
{
    public const string Witches = "The Witches of Eastwick (1987) (tt0094332)";
    public const string Oak = "The Curse of Oak Island (2014) (tt3455408)";
    public const string Holdovers = "The Holdovers (2023)";
    public const string Bogus = "Bogus Movie (2024)";
    public const string Album = "Khruangbin/A La Sala";

    public static string WorkerLog => Path.Combine(DemoFiles.Root, "unas-push-worker.log");

    public static string HourlyLog => Path.Combine(DemoFiles.Root, "unas-push-hourly.log");

    public static string UnasRoot => Path.Combine(DemoFiles.Root, "unas");

    public static void Write(DateTimeOffset now)
    {
        var witchesCopy = now.AddHours(-5).AddMinutes(-40);
        var oakHourly = now.AddMinutes(-30);
        var oakCopy = now.AddMinutes(-25);
        var bogusFail = now.AddHours(-2).AddMinutes(-30);
        var musicCopy = now.AddHours(-1);
        Directory.CreateDirectory(UnasRoot);
        Touch(Path.Combine(UnasRoot, "Movies", Witches, "The Witches of Eastwick (1987).mkv"));
        Touch(Path.Combine(UnasRoot, "TV Shows", Oak, "Season 14", "The Curse of Oak Island S14E05.mkv"));
        Touch(Path.Combine(UnasRoot, "Music", "Khruangbin", "A La Sala", "track.flac"));

        File.WriteAllText(WorkerLog, string.Join('\n',
        [
            Line(witchesCopy, "OK copied: [Movies] " + Witches + " (9378868615 bytes)"),
            Line(witchesCopy, "PLEX scan asked (1 new file(s)): [Movies] M:\\Movies\\" + Witches + " via http://192.168.1.16:32400"),
            Line(witchesCopy, "run done [Movies] ok=1 failed=0"),
            Line(oakCopy, "OK copied: [TV] " + Oak + "/Season 14 (5990488193 bytes)"),
            Line(oakCopy, "PLEX scan asked (1 new file(s)): [TV] M:\\TV Shows\\" + Oak + "\\Season 14 via http://192.168.1.16:32400"),
            Line(oakCopy, "run done [TV] ok=1 failed=0"),
            Line(bogusFail, "could not copy [Movies] " + Bogus + ": permission denied"),
            Line(bogusFail, "run done [Movies] ok=0 failed=1"),
            Line(musicCopy, "OK copied: [Music] " + Album + " (2400000000 bytes)"),
            Line(musicCopy, "run done [Music] ok=1 failed=0"),
            ""
        ]));
        File.WriteAllText(HourlyLog, Line(oakHourly, "OK [TV] " + Oak + "/Season 14") + "\n");
    }

    public static IReadOnlyList<ArrItem> Items(DateTimeOffset now)
    {
        var witchesGrab = now.AddHours(-6);
        var witchesImport = now.AddHours(-5).AddMinutes(-50);
        var holdGrab = now.AddHours(-5);
        var holdImport = now.AddHours(-3);
        var oakGrab = now.AddMinutes(-50);
        var oakImport = now.AddMinutes(-40);
        var nextGrab = now.AddMinutes(-12);
        var bogusGrab = now.AddHours(-3).AddMinutes(-20);
        var bogusImport = now.AddHours(-3);
        return
        [
            WithUpgrade(Movie("witches", "The Witches of Eastwick (1987)", Witches, "imdb:tt0094332", witchesGrab, witchesImport, false, null), now.AddHours(-1)),
            Movie("holdovers", Holdovers, Holdovers, "imdb:tt14849194", holdGrab, holdImport, false, null),
            Movie("bogus", Bogus, Bogus, null, bogusGrab, bogusImport, false, null),
            Episode("oak-5", "The Curse of Oak Island (2014) · S14E05", Oak + "/Season 14", Oak + "/Season 14/The Curse of Oak Island S14E05.mkv", oakGrab, oakImport),
            Episode("oak-6", "The Curse of Oak Island (2014) · S14E06", "", null, nextGrab, null)
        ];
    }

    public static IReadOnlyList<PlexHit> Plex(DateTimeOffset now)
    {
        return
        [
            new PlexHit(
                "The Witches of Eastwick (1987)",
                "movie",
                "Movies",
                Witches,
                ["imdb:tt0094332"],
                now.AddHours(-5).AddMinutes(-30))
        ];
    }

    private static ArrItem Movie(string id, string title, string folder, string? external, DateTimeOffset grabbed, DateTimeOffset? imported, bool failed, string? failure)
    {
        var events = new List<ArrEvent>
        {
            new("grabbed", "grab", grabbed, "dl-" + id, title + ".1080p", "Bluray-1080p", 9_378_868_615, null)
        };
        if (imported is not null)
            events.Add(new ArrEvent("downloadFolderImported", "import", imported.Value, "dl-" + id, title + ".1080p", "Bluray-1080p", 9_378_868_615, null));
        if (failed)
            events.Add(new ArrEvent("downloadFailed", "fail", grabbed, "dl-" + id, title, null, null, failure));
        return new ArrItem(
            "Radarr", "movie", "movie:" + id, title, "Movies", "Movies",
            imported is null ? "" : folder,
            imported is null ? null : folder + "/" + title + ".mkv",
            external, grabbed, imported, failed, failure,
            HasFile: imported is not null,
            DownloadId: "dl-" + id,
            TitleSlug: Slug(title),
            MediaId: 1,
            Events: events);
    }

    private static ArrItem WithUpgrade(ArrItem movie, DateTimeOffset when)
    {
        var events = (movie.Events ?? []).ToList();
        events.Add(new ArrEvent("downloadFailed", "fail", when, "dl-" + movie.Identity + "-upgrade", movie.Title + ".2160p", "Remux-2160p", null, "SAB: Aborted, not complete"));
        return movie with
        {
            DownloadFailed = true,
            Failure = "SAB: Aborted, not complete",
            Events = events
        };
    }

    private static ArrItem Episode(string id, string title, string folder, string? file, DateTimeOffset grabbed, DateTimeOffset? imported)
    {
        var events = new List<ArrEvent>
        {
            new("grabbed", "grab", grabbed, "dl-" + id, title, "HDTV-1080p", 1_200_000_000, null)
        };
        if (imported is not null)
            events.Add(new ArrEvent("downloadFolderImported", "import", imported.Value, "dl-" + id, title, "HDTV-1080p", 1_200_000_000, null));
        return new ArrItem(
            "Sonarr", "episode", "episode:" + id, title, "TV", "TV Shows",
            imported is null ? "" : folder, imported is null ? null : file, null, grabbed, imported, false, null,
            HasFile: imported is not null,
            DownloadId: "dl-" + id,
            Season: 14,
            Episode: id.EndsWith('6') ? 6 : 5,
            SeriesGuid: "tvdb:269613",
            TitleSlug: "the-curse-of-oak-island",
            MediaId: 14,
            Events: events);
    }

    private static string Slug(string title)
    {
        var text = title.ToLowerInvariant();
        var chars = text.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Line(DateTimeOffset utc, string text)
        => PipelineTime.FormatLocal(utc) + " " + text;

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
            File.WriteAllBytes(path, [0]);
    }
}
