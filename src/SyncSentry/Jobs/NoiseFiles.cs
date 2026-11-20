namespace SyncSentry.Jobs;

public static class NoiseFiles
{
    public static bool IsJunk(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var normalized = path.Replace('\\', '/').Trim();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals(".stfolder", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".stignore", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".stversions", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var name = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalized;
        if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith(".rclone", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("~syncthing~", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && name.Contains("syncthing", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
