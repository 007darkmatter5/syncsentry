using SyncSentry.Text;

namespace SyncSentry.Jobs;

public static class BandwidthPlan
{
    public static string DesiredRate(string? mode, double customMegabytes, double scheduleMegabytes, int startHour, int endHour, DateTimeOffset localNow)
    {
        var kind = (mode ?? "off").Trim().ToLowerInvariant();
        if (kind is "" or "off")
            return "off";
        if (kind == "custom")
            return ToRate(customMegabytes);
        if (kind != "schedule")
            return "off";

        startHour = Math.Clamp(startHour, 0, 23);
        endHour = Math.Clamp(endHour, 0, 24);
        var hour = localNow.Hour;
        var inside = startHour == endHour
            ? false
            : startHour < endHour
                ? hour >= startHour && hour < endHour
                : hour >= startHour || hour < endHour;
        return inside ? ToRate(scheduleMegabytes) : "off";
    }

    public static string ToRate(double megabytes)
    {
        if (megabytes <= 0 || double.IsNaN(megabytes))
            return "off";
        var whole = (int)Math.Round(megabytes);
        if (Math.Abs(megabytes - whole) < 0.05 && whole > 0)
            return whole + "M";
        var kibibytes = (int)Math.Max(1, Math.Round(megabytes * 1024));
        return kibibytes + "k";
    }

    public static string Display(string? rate, long bytesPerSecond)
    {
        if (string.IsNullOrWhiteSpace(rate) || rate.Equals("off", StringComparison.OrdinalIgnoreCase) || bytesPerSecond < 0 && rate.Equals("off", StringComparison.OrdinalIgnoreCase))
            return "Off";
        if (bytesPerSecond > 0)
            return TextFormat.Speed(bytesPerSecond);
        return rate;
    }

    public static bool IsNewSession(double? previousElapsed, double? currentElapsed)
        => previousElapsed is > 5 && currentElapsed is not null && currentElapsed.Value + 2 < previousElapsed.Value;
}
