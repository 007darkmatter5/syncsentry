namespace SyncSentry.Jobs;

public static class TriggerFiles
{
    public static string IdName(Guid jobId) => jobId.ToString("N");

    public static bool IsPending(string directory, Guid jobId)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;
        var name = IdName(jobId);
        var request = Path.Combine(directory, name + ".request");
        var ack = Path.Combine(directory, name + ".ack");
        var running = Path.Combine(directory, name + ".running");
        return File.Exists(running) || (File.Exists(request) && !File.Exists(ack));
    }

    public static (bool Ok, bool AlreadyPending, string? Error) Write(string directory, Guid jobId, string name, string scriptKey, DateTimeOffset now)
    {
        if (!Rules.ScriptKey(scriptKey))
            return (false, false, "Script key must be lowercase letters, numbers, and dashes.");
        if (!Rules.AbsolutePath(directory))
            return (false, false, "Trigger directory must be an absolute path.");

        Directory.CreateDirectory(directory);
        if (IsPending(directory, jobId))
            return (false, true, "That job is already waiting for the host script.");

        var id = IdName(jobId);
        var ack = Path.Combine(directory, id + ".ack");
        if (File.Exists(ack))
            File.Delete(ack);

        var safeName = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safeName.Length > 80)
            safeName = safeName[..80];
        var body = $"scriptKey={scriptKey}\njobId={id}\nname={safeName}\nrequestedUtc={now:O}\n";
        var temp = Path.Combine(directory, id + ".tmp");
        File.WriteAllText(temp, body);
        File.Move(temp, Path.Combine(directory, id + ".request"), overwrite: true);
        return (true, false, null);
    }

    public static string? ReadAck(string directory, Guid jobId)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        var path = Path.Combine(directory, IdName(jobId) + ".ack");
        if (!File.Exists(path))
            return null;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("error=", StringComparison.Ordinal))
                return Trim(trimmed["error=".Length..]);
            if (trimmed == "ok=true")
                return "The host script finished.";
        }

        return Trim(text);
    }

    private static string Trim(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean.Length > 200 ? clean[..200] : clean;
    }
}

public static class RunNowGuard
{
    public static (bool Allowed, string? Reason) Decide(bool enabled, bool alreadyRunning, bool triggerPending)
    {
        if (!enabled)
            return (false, "This job is disabled.");
        if (alreadyRunning)
            return (false, "That job is already running.");
        if (triggerPending)
            return (false, "A trigger is already waiting for the host script.");
        return (true, null);
    }
}
