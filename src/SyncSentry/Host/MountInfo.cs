using System.Text.RegularExpressions;

namespace SyncSentry.Host;

public sealed record MountEntry(
    string MountPoint,
    string FsType,
    string Source,
    string Options = "",
    string Root = "/",
    bool ReadOnly = false);

public static partial class MountInfo
{
    public static IReadOnlyList<MountEntry> Parse(string text)
    {
        var list = new List<MountEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var entry = ParseLine(line);
            if (entry is not null)
                list.Add(entry);
        }

        return list;
    }

    public static MountEntry? ParseLine(string line)
    {
        var separator = line.IndexOf(" - ", StringComparison.Ordinal);
        if (separator < 0)
            return null;

        var left = SplitFields(line[..separator]);
        var right = SplitFields(line[(separator + 3)..]);
        if (left.Count < 6 || right.Count < 2)
            return null;

        var mountPoint = left[4];
        if (mountPoint.Length == 0 || mountPoint[0] != '/')
            return null;

        var options = left[5];
        var root = left[3];
        return new MountEntry(mountPoint, right[0], SanitizeSource(right[1]), options, root, OptionsAreReadOnly(options));
    }

    /// <summary>
    /// Longest mount point that contains <paramref name="path"/>, ignoring the root mount.
    /// The root mount matches everything and would hide an unmounted path.
    /// </summary>
    public static MountEntry? FindCovering(IEnumerable<MountEntry> mounts, string path)
    {
        var full = Normalize(path);
        MountEntry? best = null;
        foreach (var mount in mounts)
        {
            var point = Normalize(mount.MountPoint);
            if (point == "/")
                continue;
            if (full != point && !full.StartsWith(point.EndsWith('/') ? point : point + "/", StringComparison.Ordinal))
                continue;
            if (best is null || point.Length > Normalize(best.MountPoint).Length)
                best = mount;
        }

        return best;
    }

    public static MountEntry? FindExact(IEnumerable<MountEntry> mounts, string path)
    {
        var full = Normalize(path);
        foreach (var mount in mounts)
        {
            if (Normalize(mount.MountPoint) == full)
                return mount;
        }

        return null;
    }

    public static IEnumerable<MountEntry> Discover(IEnumerable<MountEntry> mounts)
    {
        foreach (var mount in mounts)
        {
            if (IsDiscoveredType(mount.FsType))
                yield return mount;
        }
    }

    public static bool OptionsAreReadOnly(string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
            return false;
        foreach (var token in options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("ro", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsDiscoveredType(string? fsType)
    {
        if (string.IsNullOrWhiteSpace(fsType))
            return false;
        return fsType.Equals("cifs", StringComparison.OrdinalIgnoreCase)
            || fsType.Equals("smb3", StringComparison.OrdinalIgnoreCase)
            || fsType.Equals("smb", StringComparison.OrdinalIgnoreCase)
            || fsType.Equals("fuse.mergerfs", StringComparison.OrdinalIgnoreCase)
            || fsType.Equals("mergerfs", StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeSource(string source)
    {
        if (string.IsNullOrEmpty(source))
            return "";

        var value = source;
        var scheme = "";
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            scheme = "//";
            value = value[2..];
        }

        var at = value.LastIndexOf('@');
        var slash = value.IndexOf('/');
        if (at > 0 && (slash < 0 || at < slash))
            value = value[(at + 1)..];

        value = PasswordField().Replace(value, "");
        return scheme + value;
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        var value = path.Trim().Replace('\\', '/');
        if (!value.StartsWith('/'))
            value = "/" + value;
        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);
        if (value.Length > 1 && value.EndsWith('/'))
            value = value.TrimEnd('/');
        return value;
    }

    private static List<string> SplitFields(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToList();

    public static string Unescape(string value)
        => value.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"(?i)(,|^)(password|username|pass|user)=[^,]*")]
    private static partial Regex PasswordField();
}

public static class MountRisk
{
    public static bool IsRisky(string? fsType)
    {
        if (string.IsNullOrWhiteSpace(fsType))
            return true;

        return fsType.ToLowerInvariant() switch
        {
            "ext4" or "ext3" or "ext2" or "xfs" or "btrfs" or "zfs" or "tmpfs" or "overlay"
                or "proc" or "sysfs" or "cgroup" or "cgroup2" or "devtmpfs" or "squashfs" => false,
            _ => true
        };
    }

    /// <summary>
    /// Paths under /mnt, and anything on a network or FUSE filesystem, are read from a child
    /// process. A stat on a frozen hard CIFS mount blocks in the kernel and cannot be cancelled
    /// on the calling thread.
    /// </summary>
    public static bool PathNeedsIsolation(string path, MountEntry? covering)
    {
        if (covering is not null && IsRisky(covering.FsType))
            return true;

        var full = MountInfo.Normalize(path);
        return full == "/mnt" || full.StartsWith("/mnt/", StringComparison.Ordinal);
    }
}

public sealed record MountPlan(
    string Name,
    string Path,
    bool Mounted,
    string FsType,
    string Source,
    bool Configured);

public static class MountPlanner
{
    public static IReadOnlyList<MountPlan> Plan(
        IEnumerable<(string Name, string Path)> configured,
        IReadOnlyList<MountEntry> table)
    {
        var list = new List<MountPlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in configured)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
                continue;
            var path = MountInfo.Normalize(item.Path);
            if (!seen.Add(path))
                continue;
            var exact = MountInfo.FindExact(table, path);
            list.Add(new MountPlan(
                string.IsNullOrWhiteSpace(item.Name) ? path : item.Name.Trim(),
                path,
                exact is not null,
                exact?.FsType ?? "",
                exact is null ? "" : exact.Source,
                true));
        }

        foreach (var found in MountInfo.Discover(table))
        {
            var path = MountInfo.Normalize(found.MountPoint);
            if (!seen.Add(path))
                continue;
            list.Add(new MountPlan(NameFor(found), path, true, found.FsType, found.Source, false));
        }

        return list;
    }

    public static string NameFor(MountEntry entry)
    {
        var leaf = entry.MountPoint.TrimEnd('/');
        var slash = leaf.LastIndexOf('/');
        var name = slash >= 0 ? leaf[(slash + 1)..] : leaf;
        if (string.IsNullOrWhiteSpace(name))
            name = entry.MountPoint;
        if (entry.FsType.Contains("mergerfs", StringComparison.OrdinalIgnoreCase))
            return name + " pool";
        return name;
    }
}
