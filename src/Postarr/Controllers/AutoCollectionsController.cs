using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Postarr.Data;
using Postarr.Metadata;
using Postarr.Models;
using Postarr.Services;

namespace Postarr.Controllers;

/// <summary>The Auto Collections page: the catalogue of sets, the user's choices, preview and sync.</summary>
[ApiController]
[Route("api/auto-collections")]
public class AutoCollectionsController : ControllerBase
{
    private readonly SettingsRepository    _repo;
    private readonly MetadataClientFactory _factory;
    private readonly RatingsRefreshService _ratings;
    private readonly IServiceScopeFactory  _scopes;
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;

    public AutoCollectionsController(SettingsRepository repo, MetadataClientFactory factory, RatingsRefreshService ratings,
                                     IServiceScopeFactory scopes, IDbContextFactory<PostarrDbContext> dbFactory)
    { _repo = repo; _factory = factory; _ratings = ratings; _scopes = scopes; _dbFactory = dbFactory; }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var s = _repo.Get();
        var cfg = s.AutoCollections ?? new AutoCollectionSettings();
        var server = _factory.MediaServerName;
        bool Has(string? req) => req switch
        {
            "mdblist" => !string.IsNullOrWhiteSpace(s.MdbListApiKey),
            "tmdb"    => !string.IsNullOrWhiteSpace(s.TmdbApiKey),
            "omdb"    => !string.IsNullOrWhiteSpace(s.OmdbApiKey),
            _         => true,
        };

        using var db = await _dbFactory.CreateDbContextAsync();
        var managed = await db.ManagedCollections.Where(m => m.ServerType == server)
            .GroupBy(m => m.SetKey).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var items = db.LibraryItems.Where(i => i.ServerType == server);
        var total = await items.CountAsync();
        var customMade = await db.ManagedCollections.Where(m => m.ServerType == server && m.SetKey == AutoCollectionService.CustomSetKey)
            .GroupBy(m => m.CollectionKey).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

        return Ok(new
        {
            server,
            syncAfterScan = cfg.SyncAfterScan,
            useKometaImages = cfg.UseKometaImages,
            libraryTitles = total,
            custom = cfg.Custom.Select(c => new
            {
                c.Id, c.Name, c.Enabled, c.Source, c.ListRef, c.Titles, c.Movies, c.Shows, c.Order,
                managedCount = customMade.GetValueOrDefault(c.Id),
            }),
            hasMdbList = !string.IsNullOrWhiteSpace(s.MdbListApiKey), hasTrakt = !string.IsNullOrWhiteSpace(s.TraktClientId), hasTmdb = !string.IsNullOrWhiteSpace(s.TmdbApiKey),
            customOrderSupported = server is "Plex" or "Jellyfin",
            awardDataChecked = await items.CountAsync(i => i.RatingsCheckedUtc > DateTime.MinValue),
            sets = AutoCollectionService.Catalogue.Select(d =>
            {
                var c = cfg.Sets.FirstOrDefault(x => x.Key == d.Key);
                return new
                {
                    d.Key, d.Group, d.Name, d.Description, d.Movies, d.Shows, d.DefaultMinItems, d.Requires,
                    requirementMet = Has(d.Requires),
                    dynamic  = AutoCollectionService.IsDynamic(d.Key),
                    enabled  = c?.Enabled ?? false,
                    minItems = c?.MinItems,
                    onlyListed = c?.OnlyListed ?? false,
                    // Original Language starts with English left out (it would hold almost everything); only
                    // until the user has made their own choice for that set.
                    keys     = d.Key == "location.language" && (c == null || (!c.Enabled && !c.OnlyListed && c.Keys.Count == 0))
                               ? new List<string> { "english" } : c?.Keys ?? new List<string>(),
                    merges   = c?.Merges ?? new Dictionary<string, string>(),
                    titleFormat = c?.TitleFormat,
                    defaultTitleFormat = AutoCollectionService.DefaultFormat(d.Key, "x"),
                    managedCount = managed.GetValueOrDefault(d.Key),
                };
            }),
        });
    }

    public record SetChoice(string Key, bool Enabled, int? MinItems, bool OnlyListed = false, List<string>? Keys = null,
                            Dictionary<string, string>? Merges = null, string? TitleFormat = null);
    public record ConfigBody(List<SetChoice> Sets, bool SyncAfterScan, bool UseKometaImages);

    [HttpPost("config")]
    public IActionResult SaveConfig([FromBody] ConfigBody body)
    {
        var s = _repo.Get();
        var before = s.AutoCollections ?? new AutoCollectionSettings();
        s.AutoCollections = new AutoCollectionSettings
        {
            SyncAfterScan = body.SyncAfterScan,
            UseKometaImages = body.UseKometaImages,
            Custom = before.Custom,   // edited only through /custom
            Sets = body.Sets.Where(x => AutoCollectionService.Def(x.Key) != null)
                            .Select(x => new AutoCollectionSetConfig
                            {
                                Key = x.Key, Enabled = x.Enabled,
                                MinItems = x.MinItems is > 0 and < 10000 ? x.MinItems : null,
                                OnlyListed = x.OnlyListed && AutoCollectionService.IsDynamic(x.Key),
                                Keys = AutoCollectionService.IsDynamic(x.Key)
                                    ? (x.Keys ?? new()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).ToList()
                                    : new(),
                                Merges = AutoCollectionService.IsDynamic(x.Key) && x.Merges != null
                                    ? x.Merges.Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                                              .Take(2000).ToDictionary(kv => kv.Key, kv => kv.Value.Trim())
                                    : new(),
                                // A format must contain {name}; anything else falls back to the set's default.
                                TitleFormat = x.TitleFormat?.Contains("{name}") == true && x.TitleFormat.Length <= 120 ? x.TitleFormat.Trim() : null,
                            }).ToList(),
        };
        _repo.Save(s);
        // An award collection needs OMDb's award data — fetch it now rather than at the next scheduled refresh.
        if (AutoCollectionService.AwardSetsEnabled(s.AutoCollections) && !AutoCollectionService.AwardSetsEnabled(before))
            _ratings.RequestRun();
        return Ok(new { status = "saved" });
    }

    /// <summary>Every collection a dynamic set could make, for its "Choose…" list.</summary>
    public record CustomBody(string? Id, string? Name, bool Enabled, string? Source, string? ListRef, string? Titles,
                             bool Movies, bool Shows, string? Order);

    private static CustomCollectionConfig? Clean(CustomBody b)
    {
        var name = (b.Name ?? "").Trim();
        if (name.Length == 0) return null;
        return new CustomCollectionConfig
        {
            Id      = string.IsNullOrWhiteSpace(b.Id) || b.Id.Length > 40 ? Guid.NewGuid().ToString("N") : b.Id.Trim(),
            Name    = name.Length > 100 ? name[..100] : name,
            Enabled = b.Enabled,
            Source  = b.Source is "mdblist" or "trakt" or "tmdb" or "titles" ? b.Source : "titles",
            ListRef = (b.ListRef ?? "").Trim() is { Length: > 500 } r ? r[..500] : (b.ListRef ?? "").Trim(),
            Titles  = (b.Titles ?? "").Length > 40000 ? b.Titles![..40000] : b.Titles ?? "",
            Movies  = b.Movies || !b.Shows,   // at least one
            Shows   = b.Shows,
            Order   = b.Order is "list" or "release" or "default" ? b.Order : "list",
        };
    }

    /// <summary>Replaces the user's custom collections (the sets' own choices are saved by /config).</summary>
    [HttpPost("custom")]
    public IActionResult SaveCustom([FromBody] List<CustomBody> body)
    {
        var s = _repo.Get();
        s.AutoCollections ??= new AutoCollectionSettings();
        s.AutoCollections.Custom = body.Select(Clean).OfType<CustomCollectionConfig>().Take(200).ToList();
        _repo.Save(s);
        return Ok(new { status = "saved", custom = s.AutoCollections.Custom });
    }

    /// <summary>What a custom collection would hold — reads the list and matches it to the library, changes nothing.</summary>
    [HttpPost("custom/check")]
    public async Task<IActionResult> CheckCustom([FromBody] CustomBody body, CancellationToken ct)
    {
        var c = Clean(body with { Name = string.IsNullOrWhiteSpace(body.Name) ? "check" : body.Name });
        using var scope = _scopes.CreateScope();
        return Ok(await scope.ServiceProvider.GetRequiredService<AutoCollectionService>().TestCustomAsync(c!, ct));
    }

    /// <summary>Public lists for the "browse community lists" picker (MDBList or Trakt): a name search, or the popular ones when empty.</summary>
    [HttpGet("community-lists")]
    public async Task<IActionResult> CommunityLists([FromQuery] string? query, [FromQuery] string? source, CancellationToken ct)
    {
        List<CommunityList>? lists;
        if (source == "trakt")
        {
            var trakt = _factory.BuildTraktClient();
            if (trakt == null) return BadRequest(new { error = "Add a Trakt Client ID in Settings to browse Trakt lists." });
            lists = await trakt.SearchListsAsync(query, 30, ct);
            return lists == null ? StatusCode(502, new { error = "Trakt didn't answer — check the Client ID, or try again in a moment." }) : Ok(lists);
        }
        var mdb = _factory.BuildMdbListClient();
        if (mdb == null) return BadRequest(new { error = "Add an MDBList key in Settings to browse community lists." });
        lists = await mdb.SearchListsAsync(query, 30, ct);
        return lists == null ? StatusCode(502, new { error = "MDBList didn't answer — try again in a moment." }) : Ok(lists);
    }

    /// <summary>Copies a list's titles into editable text (the "copy instead of follow" option).</summary>
    [HttpPost("custom/import")]
    public async Task<IActionResult> ImportCustom([FromBody] CustomBody body, CancellationToken ct)
    {
        var c = Clean(body with { Name = string.IsNullOrWhiteSpace(body.Name) ? "import" : body.Name });
        using var scope = _scopes.CreateScope();
        var (text, count, error) = await scope.ServiceProvider.GetRequiredService<AutoCollectionService>().ImportTitlesAsync(c!, ct);
        return error != null ? BadRequest(new { error }) : Ok(new { titles = text, count });
    }

    [HttpGet("values/{setKey}")]
    public async Task<IActionResult> Values(string setKey, [FromQuery] int? minItems, CancellationToken ct)
    {
        if (!AutoCollectionService.IsDynamic(setKey)) return BadRequest(new { error = "That set makes a single collection." });
        using var scope = _scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AutoCollectionService>();
        // First time on a big library: fetching TMDB details takes minutes, so do it in the background and let the page poll.
        if (AutoCollectionService.NeedsTmdbDetails(setKey) && (AutoCollectionService.BackfillRunning || await svc.PendingTmdbDetailsAsync(ct) > 0))
        {
            AutoCollectionService.StartBackfillInBackground(_scopes);
            return Accepted(new { preparing = true, progress = AutoCollectionService.BackfillProgress });
        }
        return Ok(await svc.ValuesAsync(setKey, minItems, ct));
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        if (AutoCollectionService.IsRunning) return Conflict(new { error = "A collection sync is already running." });
        using var scope = _scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AutoCollectionService>();
        var needs = (_repo.Get().AutoCollections?.Sets ?? new()).Any(s => s.Enabled && AutoCollectionService.NeedsTmdbDetails(s.Key));
        if (needs && (AutoCollectionService.BackfillRunning || await svc.PendingTmdbDetailsAsync(ct) > 0))
        {
            AutoCollectionService.StartBackfillInBackground(_scopes);
            return Accepted(new { preparing = true, progress = AutoCollectionService.BackfillProgress });
        }
        var (rows, notes) = await svc.PreviewAsync(ct);
        return Ok(new { rows, notes });
    }

    [HttpPost("sync")]
    public IActionResult Sync()
    {
        if (AutoCollectionService.IsRunning) return Conflict(new { error = "A collection sync is already running." });
        AutoCollectionService.StartInBackground(_scopes);
        return Accepted(new { status = "started" });
    }

    /// <summary>Postarr's own poster design for one of its collections (the ?v= in the URL busts caches).</summary>
    [HttpGet("poster/{id:int}")]
    public async Task<IActionResult> Poster(int id, [FromServices] CollectionArtService art)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var m = await db.ManagedCollections.FirstOrDefaultAsync(x => x.Id == id);
        if (m == null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return File(art.Render(m), "image/jpeg");
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        isRunning  = AutoCollectionService.IsRunning,
        progress   = AutoCollectionService.Progress,
        lastRunUtc = AutoCollectionService.LastRunUtc,
        rows       = AutoCollectionService.LastRows,
        errors     = AutoCollectionService.LastErrors,
    });
}
