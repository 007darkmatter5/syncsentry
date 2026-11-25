namespace SyncSentry.Pipeline;

/// <summary>Plex on this network stores Windows paths such as <c>M:\TV Shows\Show\Season 1\file.mkv</c>.</summary>
public static class PlexPath
{
    public static (string LibraryFolder, string RelativeFolder) Split(string? path, string? sectionTitle = null)
    {
        var text = (path ?? "").Replace('\\', '/').Trim();
        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
            text = text[2..];
        text = text.Trim().Trim('/');
        if (text.Length == 0)
            return (sectionTitle?.Trim() ?? "", "");

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && IsMediaFile(parts[^1]))
            parts.RemoveAt(parts.Count - 1);
        if (parts.Count == 0)
            return (sectionTitle?.Trim() ?? "", "");

        var section = sectionTitle?.Trim() ?? "";
        if (MediaLibrary.IsKnown(parts[0]) || (section.Length > 0 && parts[0].Equals(section, StringComparison.OrdinalIgnoreCase)))
        {
            var library = parts[0];
            parts.RemoveAt(0);
            return (library, string.Join('/', parts));
        }

        if (section.Length > 0)
            return (section, string.Join('/', parts));
        return (parts[0], string.Join('/', parts.Skip(1)));
    }

    public static bool IsMediaFile(string name)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length is < 2 or > 5)
            return false;
        for (var i = 1; i < extension.Length; i++)
        {
            if (!char.IsLetter(extension[i]))
                return false;
        }

        return extension.ToLowerInvariant() switch
        {
            ".mkv" or ".mp4" or ".avi" or ".m4v" or ".wmv" or ".ts" or ".mpg" or ".mpeg"
                or ".flac" or ".mp3" or ".m4a" or ".aac" or ".ogg" or ".wav" or ".alac" => true,
            _ => false
        };
    }
}
