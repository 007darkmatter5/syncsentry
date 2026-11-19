using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SyncSentry.Alerts;
using SyncSentry.Components;
using SyncSentry.Components.Account;
using SyncSentry.Data;
using SyncSentry.Demo;
using SyncSentry.Host;
using SyncSentry.Jobs;
using SyncSentry.Logging;
using SyncSentry.Outbox;
using SyncSentry.Rclone;
using SyncSentry.Security;
using SyncSentry.Services;
using SyncSentry.Setup;

if (IoCommands.TryHandle(args, out var exitCode))
    return exitCode;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options =>
{
    options.DetailedErrors = builder.Environment.IsDevelopment();
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
})
.AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = true;
});

var connectionString = AppPaths.ResolveConnectionString(builder.Configuration, builder.Environment.ContentRootPath);
var logDirectory = AppPaths.ResolveLogDirectory(builder.Configuration, builder.Environment.ContentRootPath, connectionString);
Directory.CreateDirectory(logDirectory);
builder.Logging.AddProvider(new ScrubbingLoggerProvider(Path.Combine(logDirectory, "syncsentry.log")));
var databasePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
if (!string.IsNullOrEmpty(databasePath) && databasePath != ":memory:" && Path.GetDirectoryName(databasePath) is { Length: > 0 } databaseFolder)
    Directory.CreateDirectory(databaseFolder);

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(30)));

builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 8;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();

var keysPath = AppPaths.ResolveKeysPath(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("SyncSentry")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddHttpClient("rclone", client => client.Timeout = TimeSpan.FromSeconds(100));
builder.Services.AddHttpClient("ntfy", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<StuckProcesses>();
builder.Services.AddSingleton<ITreeScanner, TreeScanner>();
builder.Services.AddSingleton<ILogReader, LogReader>();
builder.Services.AddSingleton<ITailscaleReader, TailscaleReader>();
builder.Services.AddSingleton<INtfyClient, NtfyClient>();
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddSingleton(new DemoInboxHolder(AppFlags.Demo(builder.Configuration)));
builder.Services.AddSingleton<LiveBoard>();
builder.Services.AddSingleton<LiveCopyBoard>();
builder.Services.AddSingleton<TransferLog>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton<RunNowService>();
builder.Services.AddSingleton<OutboxService>();

if (AppFlags.Demo(builder.Configuration))
{
    builder.Services.AddSingleton<IMountTable, DemoMountTable>();
    builder.Services.AddSingleton<IPathProbe, DemoProbe>();
    builder.Services.AddSingleton<IRcloneRc, DemoRclone>();
}
else
{
    builder.Services.AddSingleton<IMountTable, ProcMountTable>();
    builder.Services.AddSingleton<IPathProbe, PathProbeService>();
    builder.Services.AddSingleton<IRcloneRc, RcloneRcClient>();
}

builder.Services.AddScoped<MonitorTick>();
builder.Services.AddHostedService<MonitorService>();
builder.Services.AddHostedService<LiveCopyPoller>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddScoped<AdminAccountService>();
builder.Services.AddScoped<ThemePreferenceService>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();
await DatabaseStartup.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAdditionalIdentityEndpoints();
app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(report.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
            ? "Healthy"
            : "Unhealthy");
    }
});

await app.RunAsync();
return 0;

public partial class Program;
