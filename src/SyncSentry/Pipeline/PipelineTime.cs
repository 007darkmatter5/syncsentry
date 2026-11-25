using System.Globalization;

namespace SyncSentry.Pipeline;

/// <summary>Push-log timestamps are local America/Chicago, not UTC.</summary>
public static class PipelineTime
{
    public static TimeZoneInfo Zone { get; } = Resolve();

    public static bool TryParseLocal(string text, out DateTimeOffset utc)
    {
        utc = default;
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;
        if (Zone.IsInvalidTime(local))
            local = local.AddHours(1);
        var converted = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
        utc = new DateTimeOffset(DateTime.SpecifyKind(converted, DateTimeKind.Utc));
        return true;
    }

    public static string FormatLocal(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTime(utc, Zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "America/Chicago", "Central Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
