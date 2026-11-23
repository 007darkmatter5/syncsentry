using SyncSentry.Host;
using SyncSentry.Services;

namespace SyncSentry.Customizations;

public static class CustomizationsPaths
{
    public const string DefaultPath = "/customizations/unraid-customizations.json";

    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration["SyncSentry:Customizations:Path"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        if (AppFlags.Demo(configuration))
            return Path.Combine(Demo.DemoFiles.Root, "unraid-customizations.json");
        return DefaultPath;
    }
}

public sealed class CustomizationsReader(IConfiguration configuration, IMountTable mounts)
{
    private readonly object _gate = new();
    private CustomizationsSnapshot? _cached;
    private long _token;

    public CustomizationsSnapshot Load(bool force = false)
    {
        var path = CustomizationsPaths.Resolve(configuration);
        if (!Rules.AbsolutePath(path))
            return CustomizationsSnapshot.Failed(path, "Set the customizations path to a full path inside the container.");

        var table = mounts.Read();
        if (MountRisk.PathNeedsIsolation(path, MountInfo.FindCovering(table, path)))
            return CustomizationsSnapshot.Failed(path, "That path is on a network or FUSE mount. Mount the folder at /customizations so this page cannot stall on a frozen share.");

        try
        {
            if (!File.Exists(path))
                return Remember(CustomizationsSnapshot.Missing(path), 0);
            var info = new FileInfo(path);
            var token = info.LastWriteTimeUtc.Ticks ^ info.Length;
            lock (_gate)
            {
                if (!force && _cached is { Ok: true } && _token == token)
                    return _cached;
            }

            if (info.Length > 2_000_000)
                return Remember(CustomizationsSnapshot.Failed(path, "The customizations file is larger than 2 MB."), token);
            var text = File.ReadAllText(path);
            return Remember(CustomizationsParser.Parse(text, path), token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CustomizationsSnapshot.Failed(path, "The customizations file could not be read.");
        }
    }

    private CustomizationsSnapshot Remember(CustomizationsSnapshot snapshot, long token)
    {
        lock (_gate)
        {
            _cached = snapshot;
            _token = token;
        }

        return snapshot;
    }
}
