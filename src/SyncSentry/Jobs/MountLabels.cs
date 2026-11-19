namespace SyncSentry.Jobs;

/// <summary>Turns a filesystem path into the name a person uses for that disk.</summary>
public static class MountLabels
{
    public static string Label(string? path, IReadOnlyList<MountView> mounts)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Unknown place";

        var full = Host.MountInfo.Normalize(path);
        MountView? best = null;
        var bestLength = -1;
        foreach (var mount in mounts)
        {
            var point = Host.MountInfo.Normalize(mount.Path);
            var matches = full == point || full.StartsWith(point + "/", StringComparison.Ordinal);
            if (!matches || point.Length <= bestLength)
                continue;
            best = mount;
            bestLength = point.Length;
        }

        if (full == "/mnt/user" || full.StartsWith("/mnt/user/", StringComparison.Ordinal))
            return "Unraid array (user share)";

        if (best is not null && IsRemote(best.FsType, best.Path))
            return Leaf(best.Path) + " (remote SMB)";

        if (full.StartsWith("/mnt/remotes/", StringComparison.Ordinal))
        {
            var rest = full["/mnt/remotes/".Length..];
            var slash = rest.IndexOf('/');
            var name = slash < 0 ? rest : rest[..slash];
            return name + " (remote SMB)";
        }

        if (best is not null && !string.IsNullOrWhiteSpace(best.Name))
            return best.Name;

        return full;
    }

    private static bool IsRemote(string fsType, string path)
    {
        var type = fsType.ToLowerInvariant();
        return type is "cifs" or "smb" or "smb3" || path.StartsWith("/mnt/remotes/", StringComparison.Ordinal);
    }

    private static string Leaf(string path)
    {
        var full = path.TrimEnd('/');
        var slash = full.LastIndexOf('/');
        return slash < 0 ? full : full[(slash + 1)..];
    }
}
