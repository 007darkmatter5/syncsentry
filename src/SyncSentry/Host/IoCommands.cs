using System.Text;
using System.Text.Json;
using SyncSentry.Outbox;

namespace SyncSentry.Host;

/// <summary>Child-process commands. These may block in the kernel. The parent always times out.</summary>
public static class IoCommands
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !args[0].StartsWith("--io-", StringComparison.Ordinal))
            return false;

        exitCode = args[0] switch
        {
            "--io-probe" => Probe(args),
            "--io-scan" => Scan(args),
            "--io-tail" => Tail(args),
            "--io-exists" => Exists(args),
            _ => 1
        };
        return true;
    }

    private static int Probe(string[] args)
    {
        if (args.Length < 2)
            return 1;
        var path = args[1];
        try
        {
            // DriveInfo.GetDrives() would stat every mount, including a frozen one. Only touch this path.
            var info = new DriveInfo(path);
            var total = info.TotalSize;
            var free = info.AvailableFreeSpace;
            var used = Math.Max(0, total - free);
            Console.WriteLine(JsonSerializer.Serialize(new ProbePayload(true, total, free, used, null)));
            return 0;
        }
        catch (Exception ex) when (ex is DriveNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new ProbePayload(false, null, null, null, ex.GetType().Name)));
            return 2;
        }
    }

    private static int Scan(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var maxFiles))
            return 1;
        var seconds = 90;
        if (args.Length >= 4 && int.TryParse(args[3], out var parsed))
            seconds = parsed;
        var result = TreeWalker.Walk(args[1], maxFiles, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300)));
        foreach (var file in result.Files)
            Console.WriteLine(JsonSerializer.Serialize(new ScanLine(file.RelativePath, file.Size, file.MtimeSeconds)));
        return result.Truncated ? 3 : 0;
    }

    private static int Exists(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            try
            {
                Console.WriteLine(File.Exists(args[i]) || Directory.Exists(args[i]) ? "yes" : "no");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("no");
            }
        }

        return 0;
    }

    private static int Tail(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var maxBytes))
            return 1;
        try
        {
            var (text, written) = LocalTail.Read(args[1], maxBytes);
            Console.Error.WriteLine("mtime:" + written.ToUnixTimeSeconds());
            Console.Write(text);
            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("missing");
            return 2;
        }
    }

    private sealed record ProbePayload(bool Ok, long? Total, long? Free, long? Used, string? Error);

    private sealed record ScanLine(string P, long S, long M);
}

public static class LocalTail
{
    public static (string Text, DateTimeOffset Written) Read(string path, int maxBytes)
    {
        var takeBytes = Math.Clamp(maxBytes, 1024, 1_048_576);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var take = (int)Math.Min(takeBytes, length);
        stream.Seek(length - take, SeekOrigin.Begin);
        var buffer = new byte[take];
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer);
        if (length > take)
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0 && newline < text.Length - 1)
                text = text[(newline + 1)..];
        }

        var written = File.GetLastWriteTimeUtc(path);
        return (text, new DateTimeOffset(DateTime.SpecifyKind(written, DateTimeKind.Utc)));
    }
}

public static class TreeWalker
{
    public static TreeWalk Walk(string root, int maxFiles, TimeSpan budget)
    {
        var files = new List<FileFingerprint>();
        var truncated = false;
        var started = DateTime.UtcNow;
        var mounts = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists("/proc/self/mountinfo"))
            {
                foreach (var mount in MountInfo.Parse(File.ReadAllText("/proc/self/mountinfo")))
                    mounts.Add(MountInfo.Normalize(mount.MountPoint));
            }
        }
        catch (IOException)
        {
        }

        var rootFull = Path.GetFullPath(root);
        WalkDir(rootFull, "", files, mounts, rootFull, maxFiles, started, budget, ref truncated);
        return new TreeWalk(files, truncated);
    }

    private static void WalkDir(
        string dir,
        string relative,
        List<FileFingerprint> files,
        HashSet<string> mounts,
        string rootFull,
        int maxFiles,
        DateTime started,
        TimeSpan budget,
        ref bool truncated)
    {
        if (truncated)
            return;
        if (files.Count >= maxFiles || DateTime.UtcNow - started > budget)
        {
            truncated = true;
            return;
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (files.Count >= maxFiles || DateTime.UtcNow - started > budget)
            {
                truncated = true;
                return;
            }

            string name;
            FileAttributes attributes;
            try
            {
                name = Path.GetFileName(entry);
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(name) || name is "." or "..")
                continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            var rel = string.IsNullOrEmpty(relative) ? name : relative + "/" + name;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                var full = MountInfo.Normalize(entry);
                if (!full.Equals(MountInfo.Normalize(rootFull), StringComparison.Ordinal) && mounts.Contains(full))
                    continue;
                if (rel.Count(character => character == '/') > 40)
                    continue;
                WalkDir(entry, rel, files, mounts, rootFull, maxFiles, started, budget, ref truncated);
                continue;
            }

            try
            {
                var info = new FileInfo(entry);
                var mtime = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
                files.Add(new FileFingerprint(rel, info.Length, mtime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

public sealed record TreeWalk(IReadOnlyList<FileFingerprint> Files, bool Truncated);
