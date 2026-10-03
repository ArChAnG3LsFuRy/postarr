using Postarr.Auth;
using Postarr.Data;
using Postarr.Models;
using Postarr.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Postarr.Controllers;

// Note: No [ApiController] here — we handle body reading manually so model
// binding validation cannot auto-reject requests before our code runs.
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly SettingsRepository    _repo;
    private readonly MetadataClientFactory _factory;
    private readonly AuthService           _auth;
    private readonly RatingsRefreshService _ratingsRefresh;
    private const string Mask = "••••••••";
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        // Accept enum values as strings ("BottomRight") not just integers
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public SettingsController(SettingsRepository repo, MetadataClientFactory factory, AuthService auth,
                              RatingsRefreshService ratingsRefresh)
    { _repo = repo; _factory = factory; _auth = auth; _ratingsRefresh = ratingsRefresh; }

    // ── GET /api/settings ────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Get()
    {
        // Secrets are always masked. (There used to be a "?reveal=true" switch that returned them all in clear,
        // including the Plex token and the login's password hash — removed for the public release.)
        var s = _repo.Get();
        if (string.IsNullOrEmpty(s.WebhookToken))
        {
            s.WebhookToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _repo.Save(s);
        }
        return new JsonResult(MaskSecrets(s), _jsonOpts);
    }

    /// <summary>Version for Settings → About.</summary>
    [HttpGet("about")]
    public IActionResult About()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var ver = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                     .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
                  ?? asm?.GetName().Version?.ToString() ?? "unknown";
        return new JsonResult(new { version = ver.Split('+')[0] }, _jsonOpts);
    }

    // ── POST /api/settings ───────────────────────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> Save()
    {
        try
        {
            using var reader = new System.IO.StreamReader(Request.Body);
            var rawJson = await reader.ReadToEndAsync();

            // Log exactly what arrived so we can diagnose mismatches
            Console.WriteLine($"[Settings.Save] body length={rawJson.Length}");
            Console.WriteLine($"[Settings.Save] body={rawJson[..Math.Min(500, rawJson.Length)]}");

            if (string.IsNullOrWhiteSpace(rawJson))
                return new JsonResult(new { error = "Empty request body." }) { StatusCode = 400 };

            JsonDocument doc;
            try { doc = JsonDocument.Parse(rawJson); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings.Save] JSON parse error: {ex.Message}");
                return new JsonResult(new { error = $"Invalid JSON: {ex.Message}" }) { StatusCode = 400 };
            }

            var existing = _repo.Get();

            JsonElement settingsEl;
            if (doc.RootElement.TryGetProperty("settings", out var nested))
                settingsEl = nested;
            else
                settingsEl = doc.RootElement;

            AppSettings s;
            try
            {
                s = JsonSerializer.Deserialize<AppSettings>(settingsEl.GetRawText(), _jsonOpts)
                    ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings.Save] Deserialize error: {ex.Message}");
                // Partial failure — start from existing and patch in whatever top-level
                // scalar fields we can read directly from the JSON element
                s = existing;
                if (settingsEl.TryGetProperty("plexBaseUrl",  out var u))  s.PlexBaseUrl  = u.GetString() ?? s.PlexBaseUrl;
                if (settingsEl.TryGetProperty("plexToken",    out var pt)) s.PlexToken    = pt.GetString() ?? s.PlexToken;
                if (settingsEl.TryGetProperty("mediaServerType", out var mst)) s.MediaServerType = mst.GetString() ?? s.MediaServerType;
                if (settingsEl.TryGetProperty("jellyfinBaseUrl", out var ju))  s.JellyfinBaseUrl = ju.GetString()  ?? s.JellyfinBaseUrl;
                if (settingsEl.TryGetProperty("jellyfinApiKey",  out var jk))  s.JellyfinApiKey  = jk.GetString()  ?? s.JellyfinApiKey;
                if (settingsEl.TryGetProperty("embyBaseUrl",     out var eu))  s.EmbyBaseUrl     = eu.GetString()  ?? s.EmbyBaseUrl;
                if (settingsEl.TryGetProperty("embyApiKey",      out var ek))  s.EmbyApiKey      = ek.GetString()  ?? s.EmbyApiKey;
                if (settingsEl.TryGetProperty("tmdbApiKey",   out var tk)) s.TmdbApiKey   = tk.GetString() ?? s.TmdbApiKey;
                if (settingsEl.TryGetProperty("fanArtApiKey", out var fk)) s.FanArtApiKey = fk.GetString() ?? s.FanArtApiKey;
                if (settingsEl.TryGetProperty("tvdbApiKey",   out var vk)) s.TvdbApiKey   = vk.GetString() ?? s.TvdbApiKey;
                if (settingsEl.TryGetProperty("omdbApiKey",   out var ok)) s.OmdbApiKey   = ok.GetString() ?? s.OmdbApiKey;
                if (settingsEl.TryGetProperty("mdbListApiKey",out var mk)) s.MdbListApiKey= mk.GetString() ?? s.MdbListApiKey;
                if (settingsEl.TryGetProperty("traktClientId", out var tc)) s.TraktClientId = tc.GetString() ?? s.TraktClientId;
                if (settingsEl.TryGetProperty("applyMode",    out var am)) s.ApplyMode    = (Models.ApplyMode)am.GetInt32();
                if (settingsEl.TryGetProperty("preferTextlessPosters", out var tp)) s.PreferTextlessPosters = tp.GetBoolean();
                if (settingsEl.TryGetProperty("preferSeasonSets",      out var pss)) s.PreferSeasonSets     = pss.GetBoolean();
                if (settingsEl.TryGetProperty("scanIntervalMinutes",   out var si)) s.ScanIntervalMinutes   = si.GetInt32();
                if (settingsEl.TryGetProperty("webhookEnabled",        out var we)) s.WebhookEnabled        = we.GetBoolean();
                if (settingsEl.TryGetProperty("autoApplyOnScan",       out var aa)) s.AutoApplyOnScan       = aa.GetBoolean();
                if (settingsEl.TryGetProperty("incrementalScanOnly",   out var inc)) s.IncrementalScanOnly  = inc.GetBoolean();
                if (settingsEl.TryGetProperty("showDismissed",         out var sd)) s.ShowDismissed        = sd.GetBoolean();
                if (settingsEl.TryGetProperty("themePrimary",          out var tp2)) s.ThemePrimary        = tp2.GetString() ?? s.ThemePrimary;
                if (settingsEl.TryGetProperty("themeAccent",           out var ta2)) s.ThemeAccent         = ta2.GetString() ?? s.ThemeAccent;
            }

            if (string.IsNullOrEmpty(s.PlexToken)    || s.PlexToken    == Mask) s.PlexToken    = existing.PlexToken;
            if (string.IsNullOrEmpty(s.JellyfinApiKey) || s.JellyfinApiKey == Mask) s.JellyfinApiKey = existing.JellyfinApiKey;
            if (string.IsNullOrEmpty(s.EmbyApiKey)     || s.EmbyApiKey     == Mask) s.EmbyApiKey     = existing.EmbyApiKey;
            if (string.IsNullOrWhiteSpace(s.MediaServerType)) s.MediaServerType = existing.MediaServerType;
            if (string.IsNullOrEmpty(s.TmdbApiKey)   || s.TmdbApiKey   == Mask) s.TmdbApiKey   = existing.TmdbApiKey;
            if (string.IsNullOrEmpty(s.FanArtApiKey) || s.FanArtApiKey == Mask) s.FanArtApiKey = existing.FanArtApiKey;
            if (string.IsNullOrEmpty(s.TvdbApiKey)   || s.TvdbApiKey   == Mask) s.TvdbApiKey   = existing.TvdbApiKey;
            if (string.IsNullOrEmpty(s.OmdbApiKey)   || s.OmdbApiKey   == Mask) s.OmdbApiKey   = existing.OmdbApiKey;
            if (string.IsNullOrEmpty(s.MdbListApiKey)|| s.MdbListApiKey== Mask) s.MdbListApiKey= existing.MdbListApiKey;
            if (string.IsNullOrEmpty(s.TraktClientId)|| s.TraktClientId== Mask) s.TraktClientId= existing.TraktClientId;
            s.WebhookToken = existing.WebhookToken;   // never changed from the page

            string? newPassword = null;
            if (doc.RootElement.TryGetProperty("newPassword", out var np) &&
                np.ValueKind == JsonValueKind.String)
                newPassword = np.GetString();

            if (!string.IsNullOrWhiteSpace(newPassword) && newPassword.Length < 8)
                return new JsonResult(new { error = "Use a password of at least 8 characters." }) { StatusCode = 400 };

            s.Auth         ??= existing.Auth     ?? new AuthSettings();
            s.Overlays     ??= existing.Overlays ?? new OverlaySettings();
            s.Backup       ??= existing.Backup   ?? new BackupSettings();

            s.Auth.PasswordHash = !string.IsNullOrWhiteSpace(newPassword)
                ? _auth.HashPassword(newPassword)
                : existing.Auth?.PasswordHash ?? string.Empty;

            // Preserve server-managed fields the UI never posts back. collectSettings() doesn't send
            // lastFullScanCompletedUtc, so without this every settings save reset it to null — which
            // wiped the incremental-scan baseline and made every scheduled scan a FULL scan again.
            s.LastFullScanCompletedUtc ??= existing.LastFullScanCompletedUtc;
            // Auto-collection choices have their own page and endpoint; the settings form never carries them.
            s.AutoCollections = existing.AutoCollections ?? new AutoCollectionSettings();

            // Track when the overlay settings actually change, so already-applied posters only get
            // re-baked/re-uploaded once after a change (not on every Apply All → no poster pile-up).
            var overlaysChanged = JsonSerializer.Serialize(s.Overlays, _jsonOpts)
                               != JsonSerializer.Serialize(existing.Overlays, _jsonOpts);
            s.OverlaysLastChangedUtc = overlaysChanged ? DateTime.UtcNow : existing.OverlaysLastChangedUtc;

            _repo.Save(s);
            Console.WriteLine("[Settings.Save] saved OK");

            // A rating/award badge was just switched on → go and fetch its data now rather than at the
            // next scheduled run. Data only; posters on Plex change when the user presses Apply All.
            var ratingsFetchStarted = false;
            if (BadgeTurnedOn(existing.Overlays, s.Overlays))
            {
                _ratingsRefresh.RequestRun();
                ratingsFetchStarted = true;
            }
            return new JsonResult(new { status = "saved", ratingsFetchStarted }, _jsonOpts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings.Save] EXCEPTION: {ex}");
            return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    // True when any rating/award badge (the ones fed by OMDb/MDBList) went from off to on.
    private static bool BadgeTurnedOn(OverlaySettings? before, OverlaySettings? after)
    {
        if (after == null) return false;
        before ??= new OverlaySettings();
        return (after.ImdbRatingEnabled     && !before.ImdbRatingEnabled)
            || (after.RottenTomatoesEnabled && !before.RottenTomatoesEnabled)
            || (after.AudienceScoreEnabled  && !before.AudienceScoreEnabled)
            || (after.OscarWinnerEnabled    && !before.OscarWinnerEnabled)
            || (after.OscarNomineeEnabled   && !before.OscarNomineeEnabled)
            || (after.EmmyWinnerEnabled     && !before.EmmyWinnerEnabled)
            || (after.MetacriticEnabled     && !before.MetacriticEnabled)
            || (after.LetterboxdEnabled     && !before.LetterboxdEnabled)
            || (after.TraktEnabled          && !before.TraktEnabled)
            || (after.ImdbTop250Enabled     && !before.ImdbTop250Enabled);
    }

    // ── Connection tests ─────────────────────────────────────────────────────

    private string Resolve(string? submitted, string saved) =>
        string.IsNullOrWhiteSpace(submitted) || submitted == Mask ? saved : submitted;

    private async Task<(string? value, string? value2)> ReadTestBody()
    {
        using var reader = new System.IO.StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var doc = JsonDocument.Parse(raw);
        var v  = doc.RootElement.TryGetProperty("value",  out var vp)  ? vp.GetString()  : null;
        var v2 = doc.RootElement.TryGetProperty("value2", out var v2p) ? v2p.GetString() : null;
        return (v, v2);
    }

    [HttpPost("test/plex")]
    public async Task<IActionResult> TestPlex()
    {
        var (value, value2) = await ReadTestBody();
        var s      = _repo.Get();
        var url    = Resolve(value2, s.PlexBaseUrl);
        var token  = Resolve(value,  s.PlexToken);
        var client = new Plex.PlexClient(new HttpClient(), url, token);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    [HttpPost("test/jellyfin")]
    public async Task<IActionResult> TestJellyfin()
    {
        var (value, value2) = await ReadTestBody();
        var s      = _repo.Get();
        var url    = Resolve(value2, s.JellyfinBaseUrl);
        var key    = Resolve(value,  s.JellyfinApiKey);
        var client = new Jellyfin.JellyfinClient(new HttpClient(Jellyfin.JellyfinClient.CreateHandler()), url, key);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    [HttpPost("test/emby")]
    public async Task<IActionResult> TestEmby()
    {
        var (value, value2) = await ReadTestBody();
        var s      = _repo.Get();
        var url    = Resolve(value2, s.EmbyBaseUrl);
        var key    = Resolve(value,  s.EmbyApiKey);
        var client = new Jellyfin.JellyfinClient(new HttpClient(Jellyfin.JellyfinClient.CreateHandler()), url, key, emby: true);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    [HttpPost("test/tmdb")]
    public async Task<IActionResult> TestTmdb()
    {
        var (value, _) = await ReadTestBody();
        var key    = Resolve(value, _repo.Get().TmdbApiKey);
        var client = new Metadata.TmdbClient(new HttpClient(), key);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    [HttpPost("test/fanart")]
    public async Task<IActionResult> TestFanArt()
    {
        var (value, _) = await ReadTestBody();
        var key    = Resolve(value, _repo.Get().FanArtApiKey);
        var client = new Metadata.FanArtClient(new HttpClient(), key);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    [HttpPost("test/tvdb")]
    public async Task<IActionResult> TestTvdb()
    {
        var (value, _) = await ReadTestBody();
        var key    = Resolve(value, _repo.Get().TvdbApiKey);
        var client = new Metadata.TvdbClient(new HttpClient(), key);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    [HttpPost("test/omdb")]
    public async Task<IActionResult> TestOmdb()
    {
        var (value, _) = await ReadTestBody();
        var key    = Resolve(value, _repo.Get().OmdbApiKey);
        var client = new Metadata.OmdbClient(new HttpClient(), key);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    [HttpPost("test/mdblist")]
    public async Task<IActionResult> TestMdbList()
    {
        var (value, _) = await ReadTestBody();
        var key    = Resolve(value, _repo.Get().MdbListApiKey);
        var client = new Metadata.MdbListClient(new HttpClient(), key);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    [HttpPost("test/trakt")]
    public async Task<IActionResult> TestTrakt()
    {
        var (value, _) = await ReadTestBody();
        var key    = Resolve(value, _repo.Get().TraktClientId);
        var client = new Metadata.TraktClient(new HttpClient(), key);
        return new JsonResult(new { success = await client.TestConnectionAsync() }, _jsonOpts);
    }

    // ── Scan status ───────────────────────────────────────────────────────────

    [HttpGet("scan-status")]
    public IActionResult ScanStatus([FromServices] LibraryScanService scan) =>
        new JsonResult(new { isScanning = scan.IsScanning }, _jsonOpts);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AppSettings MaskSecrets(AppSettings s) => new()
    {
        MediaServerType       = s.MediaServerType,
        PlexBaseUrl           = s.PlexBaseUrl,
        PlexToken             = string.IsNullOrEmpty(s.PlexToken)    ? "" : Mask,
        JellyfinBaseUrl       = s.JellyfinBaseUrl,
        JellyfinApiKey        = string.IsNullOrEmpty(s.JellyfinApiKey) ? "" : Mask,
        EmbyBaseUrl           = s.EmbyBaseUrl,
        EmbyApiKey            = string.IsNullOrEmpty(s.EmbyApiKey)     ? "" : Mask,
        TmdbApiKey            = string.IsNullOrEmpty(s.TmdbApiKey)   ? "" : Mask,
        FanArtApiKey          = string.IsNullOrEmpty(s.FanArtApiKey) ? "" : Mask,
        TvdbApiKey            = string.IsNullOrEmpty(s.TvdbApiKey)   ? "" : Mask,
        OmdbApiKey            = string.IsNullOrEmpty(s.OmdbApiKey)   ? "" : Mask,
        MdbListApiKey         = string.IsNullOrEmpty(s.MdbListApiKey)? "" : Mask,
        TraktClientId         = string.IsNullOrEmpty(s.TraktClientId)? "" : Mask,
        WebhookToken          = s.WebhookToken,   // shown so the user can copy the webhook URL into Plex
        ApplyMode             = s.ApplyMode,
        PreferTextlessPosters = s.PreferTextlessPosters,
        PreferSeasonSets      = s.PreferSeasonSets,
        ScanIntervalMinutes   = s.ScanIntervalMinutes,
        WebhookEnabled        = s.WebhookEnabled,
        AutoApplyOnScan       = s.AutoApplyOnScan,
        IncrementalScanOnly   = s.IncrementalScanOnly,
        ShowDismissed         = s.ShowDismissed,
        ThemePrimary          = s.ThemePrimary,
        ThemeAccent           = s.ThemeAccent,
        // NB: this rebuilds AppSettings field-by-field, so anything added to the model must be
        // copied here too — otherwise GET silently returns the default and the UI can't read it.
        LastFullScanCompletedUtc = s.LastFullScanCompletedUtc,
        OverlaysLastChangedUtc   = s.OverlaysLastChangedUtc,
        Overlays = s.Overlays ?? new OverlaySettings(),
        Auth     = new AuthSettings
        {
            AuthEnabled      = s.Auth?.AuthEnabled      ?? false,
            Username         = s.Auth?.Username         ?? "admin",
            PasswordHash     = "",
            SessionDaysValid = s.Auth?.SessionDaysValid  ?? 30,
        },
        Backup = s.Backup ?? new BackupSettings(),
        AutoCollections = s.AutoCollections ?? new AutoCollectionSettings(),
    };
}
