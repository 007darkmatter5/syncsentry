using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace SyncSentry.Security;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedPayload);
}

/// <summary>Encrypts rclone and ntfy secrets with this install's data-protection keys.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "SyncSentry.Secret.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedPayload)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedPayload);
        return _protector.Unprotect(protectedPayload);
    }
}

public static partial class SecretRedactor
{
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var value = UrlUserInfo().Replace(text, "$1");
        value = Bearer().Replace(value, "Bearer [redacted]");
        value = NamedSecret().Replace(value, "$1=[redacted]");
        return value;
    }

    public static string ForDisplay(string? text, int maxLength = 300)
    {
        var value = Scrub(text).Replace("\r", " ").Replace("\n", " ").Trim();
        if (value.Length > maxLength)
            value = value[..maxLength].TrimEnd() + "…";
        return value;
    }

    [GeneratedRegex(@"(https?://)[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)\b(password|passwd|token|secret|rc-pass|authorization)\b\s*[:=]\s*\S+")]
    private static partial Regex NamedSecret();
}

public static class Crypto
{
    public static bool IsProtectionFailure(Exception exception) => exception is CryptographicException;
}

/// <summary>Environment variables that must not be copied into helper processes.</summary>
public static class SecretEnvironment
{
    public static readonly string[] Keys =
    [
        "SyncSentry__Rclone__RcPassword",
        "SyncSentry__Ntfy__Token"
    ];

    public static void RemoveFrom(IDictionary<string, string?> environment)
    {
        foreach (var key in Keys)
            environment.Remove(key);
    }
}
