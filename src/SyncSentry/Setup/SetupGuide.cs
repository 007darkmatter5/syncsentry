using System.Reflection;
using System.Security.Cryptography;
using SyncSentry.Data;
using SyncSentry.Host;
using SyncSentry.Rclone;

namespace SyncSentry.Setup;

public sealed record PathCheck(string Path, string Title, bool Mapped, bool ReadOnly, string Fix)
{
    public bool Ok => Mapped && ReadOnly;
}

public sealed record LocationRow(string Kind, string Name, string Path, string Source, bool ReadOnly);

public sealed record EnvironmentReport(
    PathCheck UserShares,
    PathCheck Remotes,
    IReadOnlyList<LocationRow> Smb,
    IReadOnlyList<LocationRow> Pools,
    IReadOnlyList<LocationRow> Shares)
{
    public bool Ok => UserShares.Ok && Remotes.Ok;

    public IReadOnlyList<LocationRow> Choices { get; } = Shares.Concat(Pools).Concat(Smb).ToArray();
}

public sealed record HelperView(string Level, string Summary, string Detail, DateTimeOffset? WrittenUtc);

public sealed record SetupGap(string Level, string Text, string Href);

public sealed record ChecklistItem(string Name, string Level, string Detail, int Step);

public sealed class CopyDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string ScriptKey { get; set; } = "";
    public string IncludeRules { get; set; } = "";
    public string ExcludeRules { get; set; } = "";
    public string StartMode { get; set; } = "rc";
    public string Operation { get; set; } = "copy";
    public int? LogSourceId { get; set; }
    public bool Fresh { get; set; }
}

public static class SetupGuide
{
    public const string SuggestedRcUrl = "http://172.17.0.1:5572";
    public const string DefaultRcUser = "syncsentry";
    public const string DefaultAppdata = "/mnt/user/appdata/syncsentry";
    public const string DefaultNtfy = "https://ntfy.sh";
    public const string UserScriptsHelp = "https://forums.unraid.net/topic/48286-plugin-ca-user-scripts/";

    public static SetupState InitialState(bool hasUser, bool hasRcIdentity, bool hasHelperFile, DateTimeOffset now)
    {
        var state = new SetupState { UpdatedUtc = now };
        if (!hasUser)
            return state;
        state.LegacyUpgrade = true;
        state.Finished = true;
        state.RcloneSkipped = !hasRcIdentity;
        state.HelperSkipped = !hasHelperFile;
        return state;
    }

    public static bool HasRcIdentity(string? user, string? protectedPassword)
        => !string.IsNullOrWhiteSpace(user) || !string.IsNullOrEmpty(protectedPassword);

    public static EnvironmentReport Inspect(IEnumerable<MountEntry> mounts)
    {
        var table = mounts.ToArray();
        var smb = new List<LocationRow>();
        var pools = new List<LocationRow>();
        var shares = new List<LocationRow>();
        foreach (var entry in table)
        {
            var kind = KindOf(entry);
            if (kind is null)
                continue;
            var row = new LocationRow(kind, MountPlanner.NameFor(entry), MountInfo.Normalize(entry.MountPoint), entry.Source, entry.ReadOnly);
            switch (kind)
            {
                case "smb":
                    smb.Add(row);
                    break;
                case "pool":
                    pools.Add(row);
                    break;
                default:
                    shares.Add(row);
                    break;
            }
        }

        return new EnvironmentReport(
            CheckRoot(table, "/mnt/user", "User shares", "User shares"),
            CheckRoot(table, "/mnt/remotes", "Remote mounts", "Remote mounts"),
            smb,
            pools,
            shares);
    }

    public static string? KindOf(MountEntry entry)
    {
        var type = entry.FsType ?? "";
        if (type.Contains("cifs", StringComparison.OrdinalIgnoreCase)
            || type.Equals("smb", StringComparison.OrdinalIgnoreCase)
            || type.Equals("smb3", StringComparison.OrdinalIgnoreCase))
            return "smb";
        if (type.Contains("mergerfs", StringComparison.OrdinalIgnoreCase))
            return "pool";
        var point = MountInfo.Normalize(entry.MountPoint);
        if (type.Contains("shfs", StringComparison.OrdinalIgnoreCase) || point == "/mnt/user" || point.StartsWith("/mnt/user/", StringComparison.Ordinal))
            return "share";
        return null;
    }

    public static string? GuessAppdata(IEnumerable<MountEntry> mounts)
    {
        MountEntry? config = null;
        foreach (var entry in mounts)
        {
            if (MountInfo.Normalize(entry.MountPoint) == "/config")
                config = entry;
        }

        if (config is null)
            return null;
        var root = (config.Root ?? "").Trim();
        if (root.Length < 2 || root == "/" || !root.Contains("appdata", StringComparison.OrdinalIgnoreCase))
            return null;
        if (root.IndexOfAny(['\'', '"', '`', '$', '\n', '\r']) >= 0)
            return null;
        if (root.StartsWith("/mnt/", StringComparison.Ordinal))
            return MountInfo.Normalize(root);
        var shfs = config.FsType.Contains("shfs", StringComparison.OrdinalIgnoreCase)
            || config.Source.Contains("shfs", StringComparison.OrdinalIgnoreCase);
        if (shfs && root.StartsWith('/'))
            return MountInfo.Normalize("/mnt/user" + root);
        if (root.StartsWith('/'))
            return MountInfo.Normalize(root);
        return null;
    }

    public static bool HostPath(string? path)
    {
        if (!Rules.AbsolutePath(path))
            return false;
        return path!.IndexOfAny(['\'', '"', '`', '$', '\n', '\r', ' ']) < 0;
    }

    public static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var chars = new char[24];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    public static string ListenAddress(string? url)
    {
        if (Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
            if (uri.Port > 0)
                port = uri.Port;
            return uri.Host + ":" + port;
        }

        return "172.17.0.1:5572";
    }

    public static string RcloneSnippet(string? user, string? password, string? url)
    {
        var name = string.IsNullOrWhiteSpace(user) ? DefaultRcUser : user.Trim();
        var secret = string.IsNullOrEmpty(password) ? "paste-the-password" : password.Replace("'", "'\\''", StringComparison.Ordinal);
        var listen = ListenAddress(string.IsNullOrWhiteSpace(url) ? SuggestedRcUrl : url);
        return "export RCLONE_RC_USER=" + name + "\n"
            + "export RCLONE_RC_PASS='" + secret + "'\n"
            + "--rc --rc-addr " + listen + " --rc-enable-metrics=false";
    }

    public static string VersionGuidance(RcAccess access, string? version) => access switch
    {
        RcAccess.Ok => "rclone answered" + (string.IsNullOrWhiteSpace(version) ? "." : " (" + version + ")."),
        RcAccess.Unauthorized => "rclone refused the username or password. The user and password in the flags have to match this page, and rclone has to be started again after you add them.",
        RcAccess.Unreachable => "Nothing answered at that address. On the default Docker bridge the host is 172.17.0.1. On a custom network, use this server's LAN address instead. Confirm rclone is running with --rc.",
        RcAccess.NotConfigured => "Enter the address first.",
        _ => "rclone answered in a way SyncSentry could not read. Check the address and try again."
    };

    public static string HelperScript(string? appdataHostPath)
    {
        var path = HostPath(appdataHostPath) ? appdataHostPath!.Trim() : DefaultAppdata;
        return ReadResource("SyncSentry.Setup.syncsentry-host.sh").Replace(DefaultAppdata, path, StringComparison.Ordinal);
    }

    public static string ExampleCopyScript()
        => ReadResource("SyncSentry.Setup.media-copy.txt");

    public static string AllowlistNote(string key)
        => "Create this file on the Unraid host and make it executable:\n"
            + "/boot/config/plugins/user.scripts/scripts/syncsentry-jobs/" + key + ".sh\n\n"
            + "The helper only runs that file. The line it looks for is:\n"
            + "scriptKey=" + key;

    public static HelperView DescribeHelper(bool exists, DateTimeOffset? writtenUtc, DateTimeOffset now, TailscaleView? view)
    {
        if (!exists)
            return new HelperView(
                "red",
                "The helper has not written a status file yet.",
                "SyncSentry reads /config/host/tailscale.json. That file shows up after the User Scripts job runs. Schedule it as * * * * * and check again in about a minute.",
                null);
        var age = writtenUtc is null ? TimeSpan.MaxValue : now - writtenUtc.Value;
        if (age > TimeSpan.FromMinutes(5))
            return new HelperView(
                "yellow",
                "The status file is there, but it is more than 5 minutes old.",
                "The schedule may have stopped. User Scripts should run it every minute.",
                writtenUtc);
        var routes = view?.AcceptRoutes switch
        {
            true => "on",
            false => "off",
            _ => "unknown"
        };
        var state = string.IsNullOrWhiteSpace(view?.Detail) ? "" : " Tailscale says " + view.Detail + ".";
        return new HelperView("green", "The helper is reporting. Accept-routes is " + routes + "." + state, "The file was updated " + (writtenUtc?.ToLocalTime().ToString("g") ?? "just now") + ".", writtenUtc);
    }

    public static string? ValidatePairs(IReadOnlyList<CopyDraft> drafts)
    {
        foreach (var draft in drafts)
        {
            var blank = string.IsNullOrWhiteSpace(draft.Name)
                && string.IsNullOrWhiteSpace(draft.Source)
                && string.IsNullOrWhiteSpace(draft.Destination)
                && string.IsNullOrWhiteSpace(draft.ScriptKey);
            if (blank)
                continue;
            if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 120)
                return "Each copy needs a short name, such as Movies.";
            if (!Rules.AbsolutePath(draft.Source) || !Rules.AbsolutePath(draft.Destination))
                return "Source and destination have to be full paths, like /mnt/user/data/media.";
            if (!string.IsNullOrWhiteSpace(draft.ScriptKey) && !Rules.ScriptKey(draft.ScriptKey.Trim()))
                return "A script name is lowercase letters, numbers, and dashes.";
        }

        return null;
    }

    public static void ApplyPair(SyncJobDefinition job, CopyDraft draft)
    {
        job.Name = draft.Name.Trim();
        job.Source = draft.Source.Trim();
        job.Destination = draft.Destination.Trim();
        job.ScriptKey = draft.ScriptKey.Trim();
        job.Enabled = true;
        if (string.IsNullOrWhiteSpace(job.Operation))
            job.Operation = "copy";
        if (Rules.ScriptKey(job.ScriptKey))
            job.StartMode = "trigger";
    }

    public static IReadOnlyList<SetupGap> Gaps(SetupState state, EnvironmentReport environment, bool hasRcIdentity, HelperView helper, int pairs)
    {
        var gaps = new List<SetupGap>();
        if (!environment.Ok)
            gaps.Add(new SetupGap("red", "A folder SyncSentry needs is missing or not read-only.", "/setup?step=2"));
        if (state.Finished && !state.LegacyUpgrade && !state.RcloneSkipped && !hasRcIdentity)
            gaps.Add(new SetupGap("yellow", "rclone is not connected yet. Live progress stays on the log until it is.", "/setup?step=3"));
        if (!state.HelperSkipped && helper.Level != "green")
            gaps.Add(new SetupGap(helper.Level == "red" ? "red" : "yellow", "The host helper is not reporting.", "/setup?step=4"));
        if (state.Finished && pairs == 0)
            gaps.Add(new SetupGap("yellow", "No copies are set up yet, so the dashboard has nothing to follow.", "/setup?step=5"));
        return gaps;
    }

    public static IReadOnlyList<ChecklistItem> Checklist(
        SetupState state,
        bool hasAccount,
        EnvironmentReport environment,
        bool hasRcIdentity,
        HelperView helper,
        int pairs,
        bool hasTopic,
        string bandwidthMode)
    {
        var rclone = state.RcloneSkipped
            ? new ChecklistItem("rclone", "green", "Using the log only.", 3)
            : hasRcIdentity
                ? new ChecklistItem("rclone", "green", "A username or password is saved.", 3)
                : new ChecklistItem("rclone", "yellow", "Not connected.", 3);
        var helperItem = state.HelperSkipped
            ? new ChecklistItem("Helper script", "green", "Marked done from an existing install that had no helper file.", 4)
            : new ChecklistItem("Helper script", helper.Level, helper.Summary, 4);
        return
        [
            new ChecklistItem("Admin account", hasAccount ? "green" : "red", hasAccount ? "An admin account exists." : "No admin account yet.", 1),
            new ChecklistItem("Folders", environment.Ok ? "green" : "red", environment.Ok ? "User shares and remote mounts are read-only." : "A required folder is missing or writable.", 2),
            rclone,
            helperItem,
            new ChecklistItem("Copies", pairs > 0 ? "green" : "yellow", pairs > 0 ? pairs + " saved." : "None yet.", 5),
            new ChecklistItem("Alerts", hasTopic ? "green" : "yellow", hasTopic ? "A topic is saved." : "No topic yet. Alerts stay off.", 6),
            new ChecklistItem("Speed limit", "green", bandwidthMode == "off" ? "Off. This is optional." : "Set to " + bandwidthMode + ".", 7),
            new ChecklistItem("Finished", state.Finished ? "green" : "yellow", state.Finished ? (state.LegacyUpgrade ? "Already configured before this wizard. Settings were left as they were." : "Setup is finished.") : "The wizard has not been finished.", 8)
        ];
    }

    public static string FormRcUrl(string? savedUrl, bool hasIdentity)
    {
        if (!hasIdentity && (string.IsNullOrWhiteSpace(savedUrl) || savedUrl.Trim() == "http://127.0.0.1:5572"))
            return SuggestedRcUrl;
        return savedUrl?.Trim() ?? "";
    }

    private static PathCheck CheckRoot(IReadOnlyList<MountEntry> table, string path, string title, string templateName)
    {
        var exact = MountInfo.FindExact(table, path);
        if (exact is null)
            return new PathCheck(path, title, false, false,
                "This path is not mounted inside the container. In the Unraid template, add a path named " + templateName
                + ": container " + path + ", host " + path + ", access mode Read only, propagation rslave. Then restart the container.");
        if (!exact.ReadOnly)
            return new PathCheck(path, title, true, false,
                "This path is mounted, but the container is allowed to write to it. Edit " + templateName
                + " in the template and set the access mode to Read only, then restart the container.");
        return new PathCheck(path, title, true, true, "");
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
