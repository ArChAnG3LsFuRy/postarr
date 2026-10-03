using Postarr.Data;
using Postarr.Models;
using Postarr.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Controllers;

/// <summary>
/// Collections (e.g. "The Dark Knight Collection") are a separate Plex entity from
/// individual movies/shows, with their own TMDB endpoint and image set. Collection
/// posters are applied as-is with no overlay badges, since a collection isn't a
/// single piece of media with a resolution/audio codec/etc.
/// </summary>
[ApiController]
[Route("api/collections")]
public class CollectionsController : ControllerBase
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly MetadataClientFactory _clientFactory;
    private readonly BackupService         _backup;
    private readonly ActivityLogger        _log;
    private readonly IHttpClientFactory    _http;
    private readonly CollectionArtService  _art;
    private readonly SettingsRepository    _settings;

    public CollectionsController(
        IDbContextFactory<PostarrDbContext> db, MetadataClientFactory cf,
        BackupService backup, ActivityLogger log, IHttpClientFactory http,
        CollectionArtService art, SettingsRepository settings)
    { _dbFactory = db; _clientFactory = cf; _backup = backup; _log = log; _http = http; _art = art; _settings = settings; }

    [HttpGet("candidates/{id}")]
    public async Task<ActionResult> GetCandidates(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var col      = await db.Collections.FindAsync(id);
        if (col == null) return NotFound();

        var result = new List<PosterCandidate>();

        // 0. A collection Postarr built: its own design, and Kometa's image when that's switched on.
        var managed = await db.ManagedCollections.FirstOrDefaultAsync(m => m.ServerCollectionId == col.PlexRatingKey);
        if (managed != null)
        {
            var own = CollectionArtService.GeneratedUrl(managed);
            result.Add(new PosterCandidate { Source = PosterSource.Generated, ImageUrl = own, ThumbnailUrl = own });
            if (_settings.Get().AutoCollections?.UseKometaImages == true)
            {
                try
                {
                    var k = await _art.KometaUrlAsync(managed, HttpContext.RequestAborted);
                    if (k != null) result.Add(new PosterCandidate { Source = PosterSource.Kometa, ImageUrl = k, ThumbnailUrl = k });
                }
                catch { /* non-fatal */ }
            }
        }

        // 1. Posters that already exist in Plex — including the user's own custom uploads, which
        //    the metadata-agent search never returns. Currently-selected one first.
        var plex = _clientFactory.BuildMediaServerClient();
        if (plex != null)
        {
            try
            {
                var posters = await plex.GetAvailablePostersAsync(col.PlexRatingKey);
                foreach (var p in posters.OrderByDescending(x => x.Selected).Take(12))
                {
                    var url = Plex.PlexClient.ImageProxyUrl(p.Key);
                    result.Add(new PosterCandidate { Source = PosterSource.Plex, ImageUrl = url, ThumbnailUrl = url });
                }
            }
            catch { /* non-fatal — still show TMDB results */ }
        }

        // 2. TMDB collection posters (the previous behaviour).
        if (!string.IsNullOrEmpty(col.TmdbId))
        {
            var tmdb = _clientFactory.BuildTmdbClient();
            if (tmdb != null)
            {
                try { result.AddRange(await tmdb.GetCollectionPostersAsync(col.TmdbId)); }
                catch { /* non-fatal */ }
            }
        }

        return Ok(result);
    }

    public record ApplyRequest(string ImageUrl, PosterSource Source);

    [HttpPost("apply/{id}")]
    public async Task<ActionResult> Apply(int id, [FromBody] ApplyRequest req)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var col      = await db.Collections.FindAsync(id);
        if (col == null) return NotFound();

        var plex = _clientFactory.BuildMediaServerClient();
        if (plex == null) return BadRequest(new { error = $"{_clientFactory.MediaServerName} is not configured." });

        // A Plex-sourced poster already lives in Plex (e.g. the user's own upload) — just select it.
        // No download, no re-upload, no overlay; Plex keeps all its other posters untouched.
        if (req.Source == PosterSource.Plex)
        {
            var key = Plex.PlexClient.PosterKeyFromRef(req.ImageUrl);
            if (key == null) return BadRequest(new { error = $"Invalid {_clientFactory.MediaServerName} poster reference." });
            await plex.SelectPosterAsync(col.PlexRatingKey, Plex.PlexClient.PosterSelectValue(key));
            col.CurrentPosterUrl    = req.ImageUrl.StartsWith("/api/images/plex") ? req.ImageUrl : Plex.PlexClient.ImageProxyUrl(key);
            col.CurrentPosterSource = PosterSource.Plex.ToString();
            col.PosterDismissed     = false;   // picking a poster un-dismisses it
            await db.SaveChangesAsync();
            await _log.LogAsync($"Collection poster set from existing {_clientFactory.MediaServerName} art: {col.Title}");
            return Ok(new { status = "applied" });
        }

        // Back up the existing collection poster before overwriting, same as items
        var itemDir = _backup.GetItemBackupDir(col.PlexRatingKey);
        await _backup.BackupPosterIfNeededAsync(col.PlexRatingKey, col.CurrentPosterUrl, itemDir);

        // Postarr's own design is drawn locally; anything else is downloaded.
        var bytes = await _art.BytesAsync(req.ImageUrl, HttpContext.RequestAborted);
        // No overlay rendering for collections — pushed as-is.
        await plex.UploadPosterAsync(col.PlexRatingKey, bytes, "image/jpeg");

        col.CurrentPosterUrl    = req.ImageUrl;
        col.CurrentPosterSource = req.Source.ToString();
        col.PosterDismissed     = false;   // picking a poster un-dismisses it
        await db.SaveChangesAsync();

        await _log.LogAsync($"Collection poster applied: {col.Title} ({req.Source})");
        return Ok(new { status = "applied" });
    }

    [HttpPost("upload/{id}")]
    public async Task<ActionResult> UploadCustomPoster(int id, IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "No file provided." });
        if (!file.ContentType.StartsWith("image/")) return BadRequest(new { error = "File must be an image." });

        using var db = await _dbFactory.CreateDbContextAsync();
        var col      = await db.Collections.FindAsync(id);
        if (col == null) return NotFound();

        var plex = _clientFactory.BuildMediaServerClient();
        if (plex == null) return BadRequest(new { error = $"{_clientFactory.MediaServerName} is not configured." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        // Collection posters push as-is — no overlay badges, same as picking one.
        await plex.UploadPosterAsync(col.PlexRatingKey, ms.ToArray(), file.ContentType);

        // Store the proxy URL of the poster Plex now has selected, so the card can display it
        // (storing "custom" rendered as "Load failed").
        var posters = await plex.GetAvailablePostersAsync(col.PlexRatingKey);
        var sel     = posters.FirstOrDefault(p => p.Selected) ?? posters.FirstOrDefault();
        col.CurrentPosterUrl    = sel != null ? Plex.PlexClient.ImageProxyUrl(sel.Key) : "custom";
        col.CurrentPosterSource = PosterSource.Local.ToString();
        col.PosterDismissed     = false;   // uploading a custom poster un-dismisses it
        await db.SaveChangesAsync();

        await _log.LogAsync($"Custom collection poster applied: {col.Title}");
        return Ok(new { status = "applied" });
    }

    [HttpPost("dismiss/{id}")]
    public async Task<ActionResult> Dismiss(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var col      = await db.Collections.FindAsync(id);
        if (col == null) return NotFound();
        col.CurrentPosterUrl    = null;
        col.CurrentPosterSource = null;
        col.PosterDismissed     = true;   // hides it from the grid; cleared when a poster is chosen
        await db.SaveChangesAsync();
        return Ok(new { status = "dismissed" });
    }

    [HttpPost("restore/{id}")]
    public async Task<ActionResult> Restore(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var col      = await db.Collections.FindAsync(id);
        if (col == null) return NotFound();

        var plex = _clientFactory.BuildMediaServerClient();
        if (plex == null) return BadRequest(new { error = $"{_clientFactory.MediaServerName} is not configured." });

        var ok = await _backup.RestorePosterAsync(col.PlexRatingKey, plex);
        if (ok) { col.CurrentPosterUrl = null; col.CurrentPosterSource = null; await db.SaveChangesAsync(); }
        return Ok(new { success = ok });
    }
}
