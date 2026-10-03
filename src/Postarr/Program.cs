using Postarr.Auth;
using Postarr.Data;
using Postarr.Hubs;
using Postarr.Overlays;
using Postarr.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Anchor the content root (and therefore wwwroot / static files) to the executable's
    // directory, not the current working directory. Critical when running as a Windows service,
    // where the working directory is C:\Windows\System32 and the app would otherwise 404 on the UI.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Host.UseWindowsService(options => { options.ServiceName = "Postarr"; });

// When running as an installed Windows Service, use ProgramData (writable by LocalSystem).
// When running directly with `dotnet run` or as a normal user process, use the app's own
// directory instead — ProgramData may not be writable by the current user account.
var isWindowsService = WindowsServiceHelpers.IsWindowsService();
// POSTARR_DATA_DIR lets a container (or any host) point the database, image cache and backups at a
// single mounted volume. Unset on a normal Windows install, so behaviour there is unchanged.
var dataDir = Environment.GetEnvironmentVariable("POSTARR_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataDir))
    dataDir = isWindowsService ? ResolveServiceDataDir() : Path.Combine(AppContext.BaseDirectory, "data");

// Renaming Curatarr → Postarr must not orphan an existing install's settings/database. The old
// %ProgramData%\Curatarr folder is MOVED to \Postarr on first run (falling back to using it in
// place if the move fails), so nothing is ever left behind or lost.
static string ResolveServiceDataDir()
{
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var current = Path.Combine(appData, "Postarr");
    var legacy  = Path.Combine(appData, "Curatarr");
    if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
    try { Directory.Move(legacy, current); return current; }
    catch { return legacy; }   // in use / permissions — keep working from the old folder
}

Directory.CreateDirectory(dataDir);
MigrateLegacyDatabase(dataDir);
Console.WriteLine($"[Postarr] Data directory: {dataDir}");
Console.WriteLine($"[Postarr] Running as Windows Service: {isWindowsService}");

// The database file was renamed curatarr.db → postarr.db. Without this the app would silently
// start on an empty database and the user's whole library would appear to vanish.
static void MigrateLegacyDatabase(string dir)
{
    var current = Path.Combine(dir, "postarr.db");
    var legacy  = Path.Combine(dir, "curatarr.db");
    if (File.Exists(current) || !File.Exists(legacy)) return;
    try
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })     // keep SQLite sidecars together
            if (File.Exists(legacy + suffix)) File.Move(legacy + suffix, current + suffix);
        Console.WriteLine("[Postarr] Migrated database curatarr.db → postarr.db");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Postarr] Database rename failed ({ex.Message}); continuing on the existing file.");
    }
}

builder.Services.AddDbContextFactory<PostarrDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataDir, File.Exists(Path.Combine(dataDir, "postarr.db")) || !File.Exists(Path.Combine(dataDir, "curatarr.db")) ? "postarr.db" : "curatarr.db")}"));

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // JS sends camelCase; C# models are PascalCase. Make deserialization
        // case-insensitive so [FromBody] binds correctly in both directions.
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });
builder.Services.AddSignalR();

builder.Services.AddHttpClient("plex",         c => { c.Timeout = TimeSpan.FromSeconds(30); c.DefaultRequestHeaders.UserAgent.ParseAdd("Postarr/1.0"); });
builder.Services.AddHttpClient("jellyfin",     c => { c.Timeout = TimeSpan.FromSeconds(60); c.DefaultRequestHeaders.UserAgent.ParseAdd("Postarr/1.0"); })
    .ConfigurePrimaryHttpMessageHandler(Postarr.Jellyfin.JellyfinClient.CreateHandler);
builder.Services.AddHttpClient("tmdb",         c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Postarr/1.0"); });
builder.Services.AddHttpClient("fanart",       c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Postarr/1.0"); });
builder.Services.AddHttpClient("tvdb",         c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Postarr/1.0"); });
builder.Services.AddHttpClient("omdb",         c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Postarr/1.0"); });
builder.Services.AddHttpClient("trakt",         c => { c.Timeout = TimeSpan.FromSeconds(20); });   // User-Agent is set per request in TraktClient
builder.Services.AddHttpClient("mdblist",       c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Postarr/1.0"); });
builder.Services.AddHttpClient("imagedownload",c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    // FanArt.tv's CDN (assets.fanart.tv) and some other image hosts will return
    // empty/blank responses to requests with no User-Agent header at all.
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Postarr/1.0");
});

builder.Services.AddSingleton(sp => new ImageCacheService(
    sp.GetRequiredService<IHttpClientFactory>(), Path.Combine(dataDir, "imagecache")));
builder.Services.AddSingleton(sp => new KometaImageCache(
    sp.GetRequiredService<IHttpClientFactory>(), Path.Combine(dataDir, "kometa-cache"), sp.GetRequiredService<ILogger<KometaImageCache>>()));
builder.Services.AddSingleton<SettingsRepository>();
builder.Services.AddSingleton<IScanNotifier, SignalRScanNotifier>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<MetadataClientFactory>();
builder.Services.AddScoped<ActivityLogger>();
builder.Services.AddScoped<OverlayRenderer>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<PosterApplyService>();
builder.Services.AddScoped<LibraryScanService>();
builder.Services.AddScoped<AutoCollectionService>();
builder.Services.AddScoped<CollectionArtService>();
builder.Services.AddHostedService<ScheduledScanHostedService>();
builder.Services.AddHostedService<EnrichmentRefreshService>();
// Single instance, also injected into SettingsController so switching a badge on can start a run.
builder.Services.AddSingleton<RatingsRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RatingsRefreshService>());
builder.Services.AddHostedService<ThumbnailWarmupService>();
builder.Services.AddHostedService<Postarr.Jellyfin.JellyfinLiveUpdateService>();   // idles unless Jellyfin is active

// "urls" picks up --urls and ASPNETCORE_URLS automatically; falls back to the default port.
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://0.0.0.0:5286");

var app = builder.Build();

// Smart DB initialisation: handles fresh installs, upgrades from EnsureCreated,
// and normal migration upgrades. Preserves all existing data.
await DatabaseInitialiser.InitialiseAsync(app.Services);

app.UseMiddleware<AuthMiddleware>();
// Errors meant for the user (InvalidOperationException: "not configured", "only downloads artwork from…") come back as
// a readable 400 with the message, not a bare 500 that the page can only report as "Failed … 500".
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (InvalidOperationException ex) when (!ctx.Response.HasStarted && ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/login", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "login.html"), "text/html; charset=utf-8"));
app.MapControllers();
app.MapHub<ScanHub>("/scanhub");
app.MapFallbackToFile("index.html");

app.Run();
