using Microsoft.Data.Sqlite;

namespace SyncSentry.Services;

public static class AppPaths
{
    public static string ResolveConnectionString(IConfiguration configuration, string contentRoot)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=Data/syncsentry.db";
        var sqlite = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(sqlite.DataSource) && sqlite.DataSource != ":memory:" && !Path.IsPathRooted(sqlite.DataSource))
        {
            sqlite.DataSource = Path.GetFullPath(Path.Combine(contentRoot, sqlite.DataSource));
            connectionString = sqlite.ToString();
        }

        return connectionString;
    }

    public static string ResolveLogDirectory(IConfiguration configuration, string contentRoot, string connectionString)
    {
        var configured = configuration["Logging:File:Path"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured));

        var dataFile = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var dataDir = string.IsNullOrWhiteSpace(dataFile)
            ? contentRoot
            : Path.GetDirectoryName(Path.GetFullPath(dataFile)) ?? contentRoot;
        var parent = Directory.GetParent(dataDir)?.FullName ?? dataDir;
        return Path.Combine(parent, "logs");
    }

    public static string ResolveKeysPath(IConfiguration configuration, string contentRoot)
    {
        var keysPath = configuration["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keysPath))
            keysPath = Path.Combine(contentRoot, "keys");
        else if (!Path.IsPathRooted(keysPath))
            keysPath = Path.Combine(contentRoot, keysPath);
        return Path.GetFullPath(keysPath);
    }
}
