using System.Globalization;
using System.Text.RegularExpressions;

namespace SyncSentry.Rclone;

public sealed record StatsLine(
    long Bytes,
    long TotalBytes,
    double Percent,
    double? SpeedBytesPerSecond,
    int? EtaSeconds,
    long? Files,
    long? TotalFiles,
    string Raw);

public sealed record LogSnapshot(
    DateTimeOffset? LastEventUtc,
    StatsLine? Stats,
    int ErrorCount,
    string? LastError,
    bool SawActivity);

public static partial class RcloneLogParser
{
    public static LogSnapshot Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new LogSnapshot(null, null, 0, null, false);

        DateTimeOffset? lastEvent = null;
        StatsLine? stats = null;
        long? files = null;
        long? totalFiles = null;
        var errors = 0;
        var summaryErrors = 0;
        string? lastError = null;
        var saw = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            saw = true;
            var match = LinePattern().Match(line);
            var level = match.Success ? match.Groups["level"].Value : "";
            var message = (match.Success ? match.Groups["msg"].Value : line).Trim();
            if (match.Success && DateTime.TryParseExact(
                    match.Groups["ts"].Value,
                    "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var stamp))
            {
                lastEvent = new DateTimeOffset(stamp);
            }

            if (level.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                errors++;
                lastError = TrimError(message);
            }

            if (TryFiles(message, out var parsedFiles, out var parsedTotal))
            {
                files = parsedFiles;
                totalFiles = parsedTotal;
                continue;
            }

            if (TryErrorSummary(message, out var summary))
            {
                summaryErrors = summary;
                continue;
            }

            if (TryStats(message, out var parsed))
            {
                stats = parsed with
                {
                    Files = files ?? parsed.Files,
                    TotalFiles = totalFiles ?? parsed.TotalFiles
                };
                if (parsed.Files is not null)
                    files = parsed.Files;
                if (parsed.TotalFiles is not null)
                    totalFiles = parsed.TotalFiles;
            }
        }

        if (stats is not null && (files is not null || totalFiles is not null))
            stats = stats with { Files = files ?? stats.Files, TotalFiles = totalFiles ?? stats.TotalFiles };

        return new LogSnapshot(lastEvent, stats, Math.Max(errors, summaryErrors), lastError, saw);
    }

    public static bool TryStats(string message, out StatsLine stats)
    {
        stats = null!;
        var text = message.Trim();
        if (text.StartsWith("Transferred:", StringComparison.OrdinalIgnoreCase))
            text = text["Transferred:".Length..].Trim();
        text = LeadingDate().Replace(text, "");
        var match = StatsPattern().Match(text);
        if (!match.Success)
            return false;
        if (!TrySize(match.Groups["done"].Value, out var done) || !TrySize(match.Groups["total"].Value, out var total))
            return false;
        var percent = match.Groups["pct"].Value == "-"
            ? 0
            : double.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture);
        TrySize(match.Groups["speed"].Value.Replace("/s", "", StringComparison.OrdinalIgnoreCase), out var speed);
        var eta = TryEta(match.Groups["eta"].Value);
        long? files = null;
        long? totalFiles = null;
        var extra = match.Groups["extra"].Value;
        var xfr = XfrPattern().Match(extra);
        if (xfr.Success)
        {
            files = long.Parse(xfr.Groups[1].Value, CultureInfo.InvariantCulture);
            totalFiles = long.Parse(xfr.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        stats = new StatsLine(done, total, percent, speed, eta, files, totalFiles, text);
        return true;
    }

    public static bool TrySize(string text, out long bytes)
    {
        bytes = 0;
        var match = SizePattern().Match(text.Trim());
        if (!match.Success)
            return false;
        var number = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups["u"].Value;
        var factor = unit.ToUpperInvariant() switch
        {
            "PIB" => 1024d * 1024 * 1024 * 1024 * 1024,
            "TIB" => 1024d * 1024 * 1024 * 1024,
            "GIB" => 1024d * 1024 * 1024,
            "MIB" => 1024d * 1024,
            "KIB" => 1024d,
            "PB" => 1e15,
            "TB" => 1e12,
            "GB" => 1e9,
            "MB" => 1e6,
            "KB" => 1e3,
            _ => 1d
        };
        bytes = (long)Math.Round(number * factor);
        return true;
    }

    public static int? TryEta(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "-" || text.Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;
        var match = EtaPattern().Match(text.Trim());
        if (!match.Success)
            return null;
        double seconds = 0;
        if (match.Groups["d"].Success)
            seconds += int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture) * 86400d;
        if (match.Groups["h"].Success)
            seconds += int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600d;
        if (match.Groups["m"].Success)
            seconds += int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60d;
        if (match.Groups["s"].Success)
            seconds += double.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (seconds > int.MaxValue)
            return null;
        return (int)Math.Round(seconds);
    }

    private static bool TryFiles(string message, out long files, out long total)
    {
        files = 0;
        total = 0;
        var match = FilesPattern().Match(message.Trim());
        if (!match.Success)
            return false;
        files = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        total = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryErrorSummary(string message, out int errors)
    {
        errors = 0;
        var match = ErrorsPattern().Match(message.Trim());
        if (!match.Success)
            return false;
        errors = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static string TrimError(string message)
    {
        var value = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length > 300)
            value = value[..300];
        return value;
    }

    [GeneratedRegex(@"^(?<ts>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2})\s+(?<level>DEBUG|INFO|NOTICE|WARNING|ERROR|CRITICAL)\s+:\s?(?<msg>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDate();

    [GeneratedRegex(@"^(?<done>[\d.]+\s*[KMGTPE]?i?B)\s*/\s*(?<total>[\d.]+\s*[KMGTPE]?i?B),\s*(?<pct>[\d.]+|-)%,\s*(?<speed>[\d.]+\s*[KMGTPE]?i?B/s),\s*ETA\s+(?<eta>\S+)(?<extra>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatsPattern();

    [GeneratedRegex(@"^(?<n>[\d.]+)\s*(?<u>[KMGTPE]?i?B)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"^(?:(?<d>\d+)d)?(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+(?:\.\d+)?)s)?$", RegexOptions.CultureInvariant)]
    private static partial Regex EtaPattern();

    [GeneratedRegex(@"xfr#(?<files>\d+)/(?<total>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XfrPattern();

    [GeneratedRegex(@"^Transferred:\s+(\d+)\s*/\s*(\d+)\s*,", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilesPattern();

    [GeneratedRegex(@"^Errors:\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorsPattern();
}

public static class LogRunAssessor
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Incomplete = "incomplete";
    public const string Idle = "idle";

    public static string Assess(LogSnapshot snapshot, DateTimeOffset? fileWrittenUtc, DateTimeOffset now, TimeSpan staleAfter)
    {
        var fresh = fileWrittenUtc is not null && now - fileWrittenUtc.Value <= staleAfter;
        if (snapshot.Stats is null && snapshot.ErrorCount == 0)
            return fresh && snapshot.SawActivity ? Running : Idle;
        if (fresh)
            return Running;
        if (snapshot.ErrorCount > 0)
            return Failed;
        if (snapshot.Stats is not null && snapshot.Stats.Percent >= 99.9)
            return Completed;
        if (snapshot.Stats is not null)
            return Incomplete;
        return Idle;
    }
}
