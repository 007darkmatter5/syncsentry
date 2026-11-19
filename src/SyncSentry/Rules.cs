using System.Text.RegularExpressions;

namespace SyncSentry;

public static partial class Rules
{
    public static bool AbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500)
            return false;
        if (!path.StartsWith('/'))
            return false;
        foreach (var character in path)
        {
            if (char.IsControl(character))
                return false;
        }

        return true;
    }

    public static bool ScriptKey(string? key)
        => !string.IsNullOrWhiteSpace(key) && ScriptKeyPattern().IsMatch(key);

    public static bool FilterRule(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule) || rule.Length > 200)
            return false;
        foreach (var character in rule)
        {
            if (char.IsControl(character))
                return false;
        }

        return true;
    }

    public static IReadOnlyList<string> SplitRules(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(FilterRule)
            .Take(50)
            .ToArray();
    }

    public static bool RcUrl(string? url) => RemoteUrl(url);

    public static bool NtfyServer(string? url) => RemoteUrl(url);

    public static bool NtfyTopic(string? topic)
        => !string.IsNullOrWhiteSpace(topic) && TopicPattern().IsMatch(topic);

    public static bool TrySplitRemote(string value, out string fs, out string remote)
    {
        fs = "";
        remote = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var index = value.IndexOf(':');
        if (index <= 0)
            return false;
        var name = value[..index];
        if (!RemoteNamePattern().IsMatch(name))
            return false;
        fs = name + ":";
        remote = value[(index + 1)..].TrimStart('/');
        return remote.IndexOf('\n') < 0 && remote.Length <= 500;
    }

    public static bool RemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 300)
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        return true;
    }

    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,40}$")]
    private static partial Regex ScriptKeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.+-]{1,64}$")]
    private static partial Regex RemoteNamePattern();
}
