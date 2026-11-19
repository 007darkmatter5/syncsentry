namespace SyncSentry.Text;

public static class TextFormat
{
    public static string Bytes(long? value)
    {
        if (value is null)
            return "—";
        return Bytes(value.Value);
    }

    public static string Bytes(long value)
    {
        if (value < 0)
            value = 0;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double number = value;
        var unit = 0;
        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }

        return unit == 0 ? $"{number:0} {units[unit]}" : $"{number:0.0} {units[unit]}";
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond))
            return "—";
        return Bytes((long)bytesPerSecond) + "/s";
    }

    public static string Eta(int? seconds)
    {
        if (seconds is null || seconds < 0)
            return "—";
        var value = TimeSpan.FromSeconds(seconds.Value);
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}d {value.Hours}h";
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        if (value.TotalMinutes >= 1)
            return $"{(int)value.TotalMinutes}m {value.Seconds}s";
        return $"{value.Seconds}s";
    }

    public static int Percent(long bytes, long total)
    {
        if (total <= 0)
            return 0;
        var pct = (int)Math.Round(bytes * 100.0 / total);
        return Math.Clamp(pct, 0, 100);
    }
}
