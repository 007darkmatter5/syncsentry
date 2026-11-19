namespace SyncSentry.Services;

public static class Auth
{
    public const string AdminRole = "Admin";
}

public static class AppFlags
{
    public static bool Demo(IConfiguration configuration)
        => string.Equals(configuration["SyncSentry:Demo"], "true", StringComparison.OrdinalIgnoreCase);

    public static bool Workers(IConfiguration configuration)
        => !string.Equals(configuration["SyncSentry:Monitor"], "false", StringComparison.OrdinalIgnoreCase);
}
