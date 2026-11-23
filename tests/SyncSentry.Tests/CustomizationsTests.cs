using SyncSentry.Customizations;
using SyncSentry.Host;

namespace SyncSentry.Tests;

public class CustomizationsTests
{
    [Fact]
    public void Sample_file_keeps_servers_that_are_strings_or_objects_and_ignores_extra_fields()
    {
        var file = CustomizationsParser.Parse(CustomizationsSample.Json, "/customizations/unraid-customizations.json");
        Assert.True(file.Ok);
        Assert.Equal(1, file.SchemaVersion);
        Assert.Equal(["UnraidHive", "nas-unraid"], file.Servers);
        Assert.Contains(file.Counts, item => item.Label == "must check" && item.Value == "1");
        var must = Assert.Single(file.Changes, item => item.Id == "H-04");
        Assert.True(must.MustCheck);
        Assert.Contains("Remove the exclude lines", must.Undo);
        Assert.Equal("owner", must.MadeBy);
        Assert.Equal("high", must.MadeByConfidence);
        Assert.Equal("yes", must.Active);
    }

    [Fact]
    public void Invalid_json_is_an_empty_problem_not_a_crash()
    {
        var file = CustomizationsParser.Parse("{ not json", "/customizations/unraid-customizations.json");
        Assert.False(file.Ok);
        Assert.Empty(file.Changes);
        Assert.Contains("not valid JSON", file.Problem);
    }

    [Fact]
    public void Reader_reports_a_missing_file_and_reloads_when_it_appears()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "unraid-customizations.json");
        var reader = new CustomizationsReader(new Config(path), new EmptyMounts());
        var missing = reader.Load();
        Assert.False(missing.Ok);
        Assert.Contains("No customizations file", missing.Problem);

        File.WriteAllText(path, CustomizationsSample.Json);
        var loaded = reader.Load();
        Assert.True(loaded.Ok);
        Assert.Equal(3, loaded.Changes.Count);
        Assert.Contains(loaded.Changes, item => item.MustCheck);
    }

    private sealed class Config(string path) : Microsoft.Extensions.Configuration.IConfiguration
    {
        public string? this[string key]
        {
            get => key == "SyncSentry:Customizations:Path" ? path : null;
            set { }
        }

        public IEnumerable<Microsoft.Extensions.Configuration.IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
        public Microsoft.Extensions.Configuration.IConfigurationSection GetSection(string key) => new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build().GetSection(key);
    }

    private sealed class EmptyMounts : IMountTable
    {
        public IReadOnlyList<MountEntry> Read() => [];
    }
}
