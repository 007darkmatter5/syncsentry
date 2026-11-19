using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SyncSentry.Tests;

public class WebTests : IClassFixture<SyncSentryApp>
{
    private readonly SyncSentryApp _app;

    public WebTests(SyncSentryApp app) => _app = app;

    [Fact]
    public async Task Health_and_login_page_are_anonymous()
    {
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", (await health.Content.ReadAsStringAsync()).Trim());

        var login = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync();
        Assert.Contains("Log in", html);
        Assert.Contains("blazor.web.js", html);
        Assert.Contains("Create the admin account", html);
    }
}

public sealed class SyncSentryApp : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ss-web-" + Guid.NewGuid().ToString("N"));

    public SyncSentryApp()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "Data Source=" + Path.Combine(_root, "syncsentry.db"));
        Environment.SetEnvironmentVariable("DataProtection__KeysPath", Path.Combine(_root, "keys"));
        Environment.SetEnvironmentVariable("Logging__File__Path", Path.Combine(_root, "logs"));
        Environment.SetEnvironmentVariable("SyncSentry__Demo", "false");
        Environment.SetEnvironmentVariable("SyncSentry__Monitor", "false");
    }

    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
        Environment.SetEnvironmentVariable("DataProtection__KeysPath", null);
        Environment.SetEnvironmentVariable("Logging__File__Path", null);
        Environment.SetEnvironmentVariable("SyncSentry__Demo", null);
        Environment.SetEnvironmentVariable("SyncSentry__Monitor", null);
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}
