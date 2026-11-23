using System.Globalization;
using System.Text.Json;

namespace SyncSentry.Customizations;

public sealed record CustomizationsSnapshot(
    string Path,
    bool Ok,
    string? Problem,
    int SchemaVersion,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? LastVerified,
    IReadOnlyList<string> Servers,
    IReadOnlyList<ChangeRecord> Changes,
    IReadOnlyList<PendingRecord> Pending,
    IReadOnlyList<ObservationRecord> Observations,
    IReadOnlyList<(string Label, string Value)> Counts,
    DateTimeOffset? ReadUtc)
{
    public static CustomizationsSnapshot Missing(string path)
        => Failed(path, "No customizations file is at this path yet.");

    public static CustomizationsSnapshot Failed(string path, string problem)
        => new(path, false, problem, 0, null, null, [], [], [], [], [], DateTimeOffset.UtcNow);
}

public sealed record ChangeRecord(
    string Id,
    string Server,
    string Category,
    string Title,
    string Summary,
    string Before,
    string After,
    string Why,
    string Date,
    string MadeBy,
    string MadeByConfidence,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Backups,
    string Undo,
    string Active,
    string ActiveDetail,
    string VerifiedAt,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Tags)
{
    public bool MustCheck => Tags.Any(tag => tag.Equals("must-check-before-work", StringComparison.OrdinalIgnoreCase));
}

public sealed record PendingRecord(string Id, string Server, string Title, string Detail, string Source);

public sealed record ObservationRecord(string Id, string Server, string Severity, string Title, string Detail);

public static class CustomizationsParser
{
    public static CustomizationsSnapshot Parse(string json, string path = "")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return CustomizationsSnapshot.Failed(path, "The customizations file is not a JSON object.");
            var root = doc.RootElement;
            return new CustomizationsSnapshot(
                path,
                true,
                null,
                Int(root, "schema_version"),
                Time(root, "generated_at"),
                Time(root, "last_verified"),
                Servers(root),
                Array(root, "changes").Select(Change).ToArray(),
                Array(root, "pending").Select(Pending).ToArray(),
                Array(root, "observations").Select(Observation).ToArray(),
                Counts(root),
                DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return CustomizationsSnapshot.Failed(path, "The customizations file is not valid JSON.");
        }
    }

    private static ChangeRecord Change(JsonElement item)
        => item.ValueKind != JsonValueKind.Object
            ? new("", "", "", "", "", "", "", "", "", "", "", [], [], "", "unknown", "", "", [], [])
            : new(
            Text(item, "id"),
            Text(item, "server"),
            Text(item, "category"),
            Text(item, "title"),
            Block(item, "summary"),
            Block(item, "before"),
            Block(item, "after"),
            Block(item, "why"),
            Text(item, "date"),
            Text(item, "made_by"),
            Text(item, "made_by_confidence"),
            Strings(item, "files"),
            Strings(item, "backups"),
            Block(item, "undo"),
            Active(item),
            Block(item, "active_detail"),
            Text(item, "verified_at"),
            Strings(item, "warnings"),
            Strings(item, "tags"));

    private static PendingRecord Pending(JsonElement item)
        => item.ValueKind != JsonValueKind.Object
            ? new("", "", "", "", "")
            : new(Text(item, "id"), Text(item, "server"), Text(item, "title"), Block(item, "detail"), Text(item, "source"));

    private static ObservationRecord Observation(JsonElement item)
        => item.ValueKind != JsonValueKind.Object
            ? new("", "", "", "", "")
            : new(Text(item, "id"), Text(item, "server"), Text(item, "severity"), Text(item, "title"), Block(item, "detail"));

    private static IReadOnlyList<string> Servers(JsonElement root)
    {
        var names = new List<string>();
        foreach (var item in Array(root, "servers"))
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name.Trim());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var label = FirstText(item, "name", "hostname", "host", "id", "server", "title");
            if (label.Length > 0)
                names.Add(label);
        }

        return names;
    }

    private static IReadOnlyList<(string Label, string Value)> Counts(JsonElement root)
    {
        if (!root.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Object)
            return [];
        var list = new List<(string, string)>();
        foreach (var property in counts.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "yes",
                JsonValueKind.False => "no",
                _ => property.Value.GetRawText()
            };
            list.Add((property.Name.Replace('_', ' '), value));
        }

        return list;
    }

    private static string Active(JsonElement item)
    {
        var value = Text(item, "active").Trim().ToLowerInvariant();
        return value is "yes" or "no" or "unknown" ? value : value.Length == 0 ? "unknown" : value;
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in value.EnumerateArray())
            yield return item;
    }

    private static IReadOnlyList<string> Strings(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return [value.GetString() ?? ""];
        if (value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray().Select(Textish).Where(text => text.Length > 0).ToArray();
    }

    private static string Block(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) ? Textish(value) : "";

    private static string Textish(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select(Textish).Where(text => text.Length > 0)),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            _ => FirstText(value, "path", "name", "text", "step") is { Length: > 0 } label ? label : value.GetRawText()
        };

    private static string Text(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? Textish(value) : "";

    private static string FirstText(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var text = Text(item, name).Trim();
            if (text.Length > 0)
                return text;
        }

        return "";
    }

    private static int Int(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static DateTimeOffset? Time(JsonElement item, string name)
    {
        var text = Text(item, name);
        if (text.Length == 0)
            return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
