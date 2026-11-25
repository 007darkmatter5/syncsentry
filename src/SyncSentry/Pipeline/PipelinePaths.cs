namespace SyncSentry.Pipeline;

public static class PipelinePaths
{
    public const string WorkerLog = "/logs/mediamanny/unas-push-worker.log";
    public const string HourlyLog = "/logs/mediamanny/unas-push-hourly.log";
    public const string UnasRoot = "/mnt/remotes/UNASPRO_Media";
    public const string DefaultPlexUrl = "http://192.168.1.16:32400";
    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(2);
    public const int DefaultWindowDays = 7;
    public const int MaxWindowDays = 30;
}

/// <summary>Library tags used by the push logs, and the folder names Plex and the UNAS share use.</summary>
public static class MediaLibrary
{
    public static string Tag(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var text = name.Trim();
        if (text.Equals("TV", StringComparison.OrdinalIgnoreCase)
            || text.Equals("TV Shows", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Television", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Shows", StringComparison.OrdinalIgnoreCase))
            return "TV";
        if (text.Equals("Movie", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Movies", StringComparison.OrdinalIgnoreCase))
            return "Movies";
        if (text.Equals("Music", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Audio", StringComparison.OrdinalIgnoreCase))
            return "Music";
        return text;
    }

    public static string Folder(string tag) => tag switch
    {
        "TV" => "TV Shows",
        "Movies" => "Movies",
        "Music" => "Music",
        _ => tag
    };

    public static string App(string tag) => tag switch
    {
        "TV" => "Sonarr",
        "Movies" => "Radarr",
        "Music" => "Lidarr",
        _ => ""
    };

    public static bool IsKnown(string? name)
    {
        var tag = Tag(name);
        return tag is "TV" or "Movies" or "Music";
    }

    public static bool Same(string? libraryOrTag, string? other)
    {
        var left = Tag(libraryOrTag);
        return left.Length > 0 && left.Equals(Tag(other), StringComparison.OrdinalIgnoreCase);
    }
}

public static class FolderKey
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var text = path.Replace('\\', '/').Trim();
        while (text.Contains("//", StringComparison.Ordinal))
            text = text.Replace("//", "/", StringComparison.Ordinal);
        return text.Trim('/');
    }

    public static bool Matches(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;
        return a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string DiskKey(string library, string relative)
        => MediaLibrary.Tag(library) + "|" + Normalize(relative);

    public static bool IsSafe(string? relative)
    {
        var text = Normalize(relative);
        if (text.Length is 0 or > 400)
            return false;
        if (text.Contains("..", StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal))
            return false;
        foreach (var character in text)
        {
            if (char.IsControl(character))
                return false;
        }

        return true;
    }
}
