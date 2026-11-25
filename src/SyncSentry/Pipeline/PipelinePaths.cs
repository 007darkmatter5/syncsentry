using System.Globalization;
using System.Text.RegularExpressions;

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
    private static readonly Regex SeasonFolder = new(@"^season\s+0*(?<n>[1-9]\d*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var text = path.Replace('\\', '/').Trim();
        while (text.Contains("//", StringComparison.Ordinal))
            text = text.Replace("//", "/", StringComparison.Ordinal);
        return text.Trim('/');
    }

    /// <summary>Season 04 and Season 4 are the same folder. Everything else is unchanged.</summary>
    public static string Canon(string? path)
    {
        var normal = Normalize(path);
        if (normal.Length == 0)
            return "";
        var parts = normal.Split('/');
        for (var i = 0; i < parts.Length; i++)
            parts[i] = CanonSegment(parts[i]);
        return string.Join('/', parts);
    }

    public static IReadOnlyList<string> SeasonSpellings(string? path)
    {
        var normal = Normalize(path);
        if (normal.Length == 0)
            return [];
        var found = new List<string> { normal };
        var parts = normal.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var match = SeasonFolder.Match(parts[i]);
            if (!match.Success || !int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                continue;
            foreach (var spelling in new[] { "Season " + number.ToString(CultureInfo.InvariantCulture), "Season " + number.ToString("00", CultureInfo.InvariantCulture) })
            {
                if (spelling.Equals(parts[i], StringComparison.OrdinalIgnoreCase))
                    continue;
                var copy = (string[])parts.Clone();
                copy[i] = spelling;
                var built = string.Join('/', copy);
                if (!found.Contains(built, StringComparer.OrdinalIgnoreCase))
                    found.Add(built);
            }
        }

        return found;
    }

    public static bool Matches(string? left, string? right)
    {
        var a = Canon(left);
        var b = Canon(right);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;
        return a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string DiskKey(string library, string relative)
        => MediaLibrary.Tag(library) + "|" + Canon(relative);

    private static string CanonSegment(string part)
    {
        var match = SeasonFolder.Match(part);
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return part;
        return "Season " + number.ToString(CultureInfo.InvariantCulture);
    }

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

/// <summary>Turns an *arr library file into the folder under the library root, keeping the real season directory name.</summary>
public static class LibraryPath
{
    public static string FolderFromFile(string? itemPath, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "";
        var file = filePath.Replace('\\', '/').Trim();
        var leaf = Path.GetFileName(file.TrimEnd('/'));
        var dir = PlexPath.IsMediaFile(leaf) ? Parent(file) : file.TrimEnd('/');
        if (dir.Length == 0)
            return "";
        if (string.IsNullOrWhiteSpace(itemPath))
            return "";
        var item = itemPath.Replace('\\', '/').Trim().TrimEnd('/');
        var root = Parent(item);
        if (root.Length > 0 && dir.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            return FolderKey.Normalize(dir[(root.Length + 1)..]);
        if (dir.Equals(item, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase))
        {
            var show = Path.GetFileName(item);
            var under = dir.Length > item.Length ? dir[(item.Length + 1)..] : "";
            return FolderKey.Normalize(under.Length == 0 ? show : show + "/" + under);
        }

        return "";
    }

    /// <summary>Last show/season or movie folder in a library file path, when the series root is not known.</summary>
    public static string TailFolder(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "";
        var file = filePath.Replace('\\', '/').Trim();
        var leaf = Path.GetFileName(file.TrimEnd('/'));
        var dir = PlexPath.IsMediaFile(leaf) ? Parent(file) : file.TrimEnd('/');
        if (dir.Length == 0)
            return "";
        var season = Path.GetFileName(dir);
        var show = Path.GetFileName(Parent(dir));
        if (show.Length > 0 && season.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
            return FolderKey.Normalize(show + "/" + season);
        return FolderKey.Normalize(season);
    }

    private static string Parent(string path)
    {
        var text = path.Replace('\\', '/').TrimEnd('/');
        var slash = text.LastIndexOf('/');
        return slash <= 0 ? "" : text[..slash];
    }
}
