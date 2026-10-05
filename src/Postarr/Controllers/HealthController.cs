using Postarr.Data;
using Postarr.Models;
using Postarr.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Controllers;

/// <summary>
/// Powers the "Health" view: a single snapshot of what needs attention — provider connectivity,
/// scan status, and the library items missing artwork, an applied poster, an external ID, or
/// ratings (the usual reasons a badge or poster never shows up). Read-only and computed on demand.
/// </summary>
[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly SettingsRepository    _settings;
    private readonly MetadataClientFactory _clients;
    private readonly LibraryScanService    _scan;

    public HealthController(
        IDbContextFactory<PostarrDbContext> dbFactory, SettingsRepository settings,
        MetadataClientFactory clients, LibraryScanService scan)
    { _dbFactory = dbFactory; _settings = settings; _clients = clients; _scan = scan; }

    public record ServiceStatus(string Name, bool Configured, bool? Ok);
    public record IssueItem(int Id, string Title, int? Year, string MediaType);
    public record IssueGroup(int Total, List<IssueItem> Items);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] bool quick = false)
    {
        var s = _settings.Get();

        // quick=true (used for the sidebar badge on boot) skips the live provider tests — those are
        // ~6 network round-trips and the badge only needs the DB issue counts.
        ServiceStatus[] services;
        if (quick)
        {
            services = Array.Empty<ServiceStatus>();
        }
        else
        {
            // Provider connectivity — run every test in parallel so the page loads in one round-trip's
            // worth of latency rather than the sum. A provider with no key is reported as
            // not-configured (Ok = null) instead of failed, so the UI can grey it out, not flag red.
            async Task<ServiceStatus> Check(string name, bool configured, Func<Task<bool>>? test)
            {
                if (!configured || test == null) return new ServiceStatus(name, configured, null);
                try { return new ServiceStatus(name, true, await test()); }
                catch { return new ServiceStatus(name, true, false); }
            }

            var plex    = _clients.BuildMediaServerClient();
            var tmdb    = _clients.BuildTmdbClient();
            var fanart  = _clients.BuildFanArtClient();
            var tvdb    = _clients.BuildTvdbClient();
            var omdb    = _clients.BuildOmdbClient();
            var mdblist = _clients.BuildMdbListClient();
            var trakt   = _clients.BuildTraktClient();

            services = await Task.WhenAll(
                Check(_clients.MediaServerName, plex != null, plex == null ? null : () => plex.TestConnectionAsync()),
                Check("TMDb",     tmdb    != null, tmdb    == null ? null : () => tmdb.TestConnectionAsync()),
                Check("FanArt.tv",fanart  != null, fanart  == null ? null : () => fanart.TestConnectionAsync()),
                Check("TVDB",     tvdb    != null, tvdb    == null ? null : () => tvdb.TestConnectionAsync()),
                Check("OMDb",     omdb    != null, omdb    == null ? null : () => omdb.TestConnectionAsync()),
                Check("MDBList",  mdblist != null, mdblist == null ? null : () => mdblist.TestConnectionAsync()),
                Check("Trakt",    trakt   != null, trakt   == null ? null : () => trakt.TestConnectionAsync()));
        }

        using var db = await _dbFactory.CreateDbContextAsync();
        var server = MetadataClientFactory.ServerKey(s);   // only the active media server's items count
        var items = await db.LibraryItems.AsNoTracking()
            .Where(i => i.ServerType == server)
            .Select(i => new
            {
                i.Id, i.Title, i.Year, i.MediaType,
                i.CurrentPosterUrl, i.PosterAppliedToPlex, i.PosterDismissed,
                i.TmdbId, i.TvdbId,
                i.ImdbRating, i.RottenTomatoesScore, i.AudienceScore,
            })
            .ToListAsync();

        static string MT(MediaType t) => t == MediaType.Movie ? "movie" : "show";

        // A dismissed item is a deliberate "leave it blank", so it isn't flagged as a problem.
        var active = items.Where(i => !i.PosterDismissed).ToList();

        IssueGroup Group(IEnumerable<dynamic> src)
        {
            var list = src.ToList();
            return new IssueGroup(
                list.Count,
                list.Take(200)
                    .Select(i => new IssueItem((int)i.Id, (string)i.Title, (int?)i.Year, MT((MediaType)i.MediaType)))
                    .ToList());
        }

        var noPoster   = Group(active.Where(i => string.IsNullOrEmpty(i.CurrentPosterUrl)));
        var notApplied = Group(active.Where(i => !string.IsNullOrEmpty(i.CurrentPosterUrl) && !i.PosterAppliedToPlex));
        var noId       = Group(active.Where(i => string.IsNullOrEmpty(i.TmdbId) && string.IsNullOrEmpty(i.TvdbId)));
        var noRatings  = Group(active.Where(i =>
            i.ImdbRating == null && i.RottenTomatoesScore == null && i.AudienceScore == null));

        // Libraries Postarr still has items for but the server no longer lists (live lookup, so full view only).
        var staleLibraries = quick ? null : await _scan.GetStaleLibrariesAsync();

        return Ok(new
        {
            services,
            staleLibraries = staleLibraries ?? new List<LibraryScanService.StaleLibrary>(),
            scan = new
            {
                isScanning     = _scan.IsScanning,
                lastFullScanUtc = s.LastFullScanCompletedUtc,
            },
            counts = new
            {
                movies      = items.Count(i => i.MediaType == MediaType.Movie),
                shows       = items.Count(i => i.MediaType == MediaType.Show),
                collections = await db.Collections.CountAsync(c => c.ServerType == server),
            },
            issues = new { noPoster, notApplied, noId, noRatings },
        });
    }

    /// <summary>Forget a library the media server no longer has (Health page → Remove). Postarr's records only;
    /// nothing on the server changes. Refused if the library is listed again or a scan is running.</summary>
    [HttpPost("stale-libraries/{sectionId}/remove")]
    public async Task<IActionResult> RemoveStaleLibrary(string sectionId)
    {
        if (_scan.IsScanning) return Conflict(new { error = "A scan is running — try again when it has finished." });
        var r = await _scan.RemoveStaleLibraryAsync(sectionId);
        if (r == null)
            return BadRequest(new { error = $"Library {sectionId} is still on {_clients.MediaServerName}, or it couldn't be checked right now — nothing was removed." });
        return Ok(new { items = r.Value.Items, collections = r.Value.Collections });
    }
}
