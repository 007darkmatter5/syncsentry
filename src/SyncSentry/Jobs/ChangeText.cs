using SyncSentry.Host;

namespace SyncSentry.Jobs;

public sealed record ChangeLine(
    string Title,
    string File,
    string FullPath,
    string From,
    string To,
    string RouteTip,
    long Size,
    DateTimeOffset When,
    string Ago,
    string WhenText,
    string Result,
    string? Error,
    string PairKey);

public static class ChangeText
{
    public static (string Title, string File) Split(string? name)
    {
        var path = (name ?? "").Replace('\\', '/').Trim().Trim('/');
        if (path.Length == 0)
            return ("File", "");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var file = parts[^1];
        if (parts.Length == 1)
            return (file, "");
        return (parts[0], file);
    }

    public static string Place(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var full = MountInfo.Normalize(path);
        string location;
        string rest;
        if (full.StartsWith("/mnt/remotes/", StringComparison.Ordinal))
        {
            var tail = full["/mnt/remotes/".Length..];
            var slash = tail.IndexOf('/');
            location = slash < 0 ? tail : tail[..slash];
            rest = slash < 0 ? "" : tail[(slash + 1)..];
        }
        else if (full == "/mnt/user" || full.StartsWith("/mnt/user/", StringComparison.Ordinal))
        {
            location = "Unraid array";
            rest = full == "/mnt/user" ? "" : full["/mnt/user/".Length..];
        }
        else
        {
            var bits = full.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return bits.Length == 0 ? full : bits[^1];
        }

        var folder = rest.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(folder))
            return location;
        return location + "/" + folder;
    }

    public static string Result(bool ok, string? error)
    {
        if (ok)
            return "copied";
        if (!string.IsNullOrEmpty(error) && error.Contains("delet", StringComparison.OrdinalIgnoreCase))
            return "deleted";
        return "failed";
    }

    public static string ResultLabel(string result) => result switch
    {
        "copied" => "Copied",
        "deleted" => "Deleted",
        _ => "Failed"
    };

    public static ChangeLine Line(HistoryRow row, string? fallbackSource, string? fallbackDestination, DateTimeOffset now)
    {
        var source = string.IsNullOrWhiteSpace(row.SourcePath) ? fallbackSource ?? "" : row.SourcePath;
        var destination = string.IsNullOrWhiteSpace(row.DestinationPath) ? fallbackDestination ?? "" : row.DestinationPath;
        var (title, file) = Split(row.Name);
        var from = Place(source);
        var to = Place(destination);
        if (from.Length == 0)
            from = "Unknown place";
        if (to.Length == 0)
            to = "Unknown place";
        var key = string.IsNullOrWhiteSpace(row.SourcePath) ? "" : FolderBoards.KeyFor(row.SourcePath, row.DestinationPath);
        return new ChangeLine(
            title,
            file,
            (row.Name ?? "").Replace('\\', '/'),
            from,
            to,
            source + " → " + destination,
            row.Size,
            row.CompletedUtc,
            Ago(row.CompletedUtc, now),
            row.CompletedUtc.ToLocalTime().ToString("f"),
            Result(row.Ok, row.Error),
            row.Error,
            key);
    }

    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var delta = now - when;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;
        if (delta.TotalMinutes < 1)
            return "just now";
        if (delta.TotalHours < 1)
        {
            var minutes = Math.Max(1, (int)delta.TotalMinutes);
            return minutes == 1 ? "1 min ago" : minutes + " min ago";
        }

        if (delta.TotalDays < 1)
        {
            var hours = Math.Max(1, (int)delta.TotalHours);
            return hours == 1 ? "1 hr ago" : hours + " hr ago";
        }

        if (delta.TotalDays < 7)
        {
            var days = Math.Max(1, (int)delta.TotalDays);
            return days == 1 ? "1 day ago" : days + " days ago";
        }

        return when.ToLocalTime().ToString("g");
    }
}
