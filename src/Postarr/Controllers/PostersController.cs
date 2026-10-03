using Postarr.Data;
using Postarr.Models;
using Postarr.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Controllers;

[ApiController]
[Route("api/posters")]
public class PostersController : ControllerBase
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly MetadataClientFactory _clientFactory;
    private readonly PosterApplyService    _applyService;

    public PostersController(IDbContextFactory<PostarrDbContext> db,
        MetadataClientFactory cf, PosterApplyService svc)
    { _dbFactory = db; _clientFactory = cf; _applyService = svc; }

    // ── Candidates ────────────────────────────────────────────────────────────

    [HttpGet("candidates/item/{id}")]
    public async Task<ActionResult> GetItemCandidates(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var item     = await db.LibraryItems.FindAsync(id);
        if (item == null) return NotFound();
        // Note: individual items do NOT pull existing Plex posters (only collections do). Items
        // accumulate Postarr's own badged re-uploads in Plex, which cluttered the picker and, when
        // picked, applied as-is with no resolution badge. So item pickers show search results only.
        var agg  = _clientFactory.BuildPosterAggregator();
        var list = item.MediaType == MediaType.Movie
            ? await agg.GetMoviePosterCandidatesAsync(item.TmdbId, item.TvdbId)
            : await agg.GetShowPosterCandidatesAsync(item.TmdbId, item.TvdbId);
        return Ok(list);
    }

    [HttpGet("candidates/season/{seasonId}")]
    public async Task<ActionResult> GetSeasonCandidates(int seasonId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var season   = await db.Seasons.FindAsync(seasonId);
        if (season == null) return NotFound();
        var parent = await db.LibraryItems.FindAsync(season.LibraryItemId);
        if (parent == null) return NotFound();
        var list = await _clientFactory.BuildPosterAggregator()
            .GetSeasonPosterCandidatesAsync(parent.TmdbId, parent.TvdbId, season.SeasonNumber);

        // Mark the FanArt.tv posters that are part of a set, with how many of this show's other seasons it covers.
        var fanart = _clientFactory.BuildFanArtClient();
        if (fanart != null && !string.IsNullOrEmpty(parent.TvdbId) && list.Any(c => c.Source == PosterSource.FanArt))
        {
            var others = await db.Seasons.Where(x => x.LibraryItemId == parent.Id && x.Id != seasonId)
                                         .Select(x => x.SeasonNumber).ToListAsync();
            if (others.Count > 0)
            {
                var all = await fanart.GetAllSeasonPostersAsync(parent.TvdbId);
                foreach (var c in list.Where(c => c.Source == PosterSource.FanArt && c.ExternalId != null))
                {
                    var n = others.Count(o => SeasonSetMatcher.MatchFor(all, c, season.SeasonNumber, o) != null);
                    if (n > 0) { c.SetMatches = n; c.SetOtherSeasons = others.Count; }
                }
            }
        }
        return Ok(list);
    }

    /// <summary>
    /// "Match the other seasons": for each other season of the show, the FanArt.tv poster from the same set as the one
    /// just chosen. FanArt.tv doesn't record uploaders in its API, but a set is uploaded in one go, so its posters have
    /// neighbouring ids (checked on The Simpsons, Game of Thrones, Breaking Bad, Stranger Things): same language, nearest
    /// id, and close enough to have been part of the same upload. Seasons with no such poster are listed as missing.
    /// Only returns the matches — the page applies them one by one, so no single request runs long.
    /// </summary>
    [HttpGet("season-set/{seasonId}")]
    public async Task<ActionResult> GetSeasonSet(int seasonId, [FromQuery] string imageUrl, CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var season = await db.Seasons.FindAsync(new object[] { seasonId }, ct);
        if (season == null) return NotFound();
        var show = await db.LibraryItems.FindAsync(new object[] { season.LibraryItemId }, ct);
        if (show == null) return NotFound();
        if (string.IsNullOrEmpty(show.TvdbId)) return BadRequest(new { error = "This show has no TVDB id, which FanArt.tv needs." });
        var fanart = _clientFactory.BuildFanArtClient();
        if (fanart == null) return BadRequest(new { error = "Needs a FanArt.tv key (Settings)." });

        var all = await fanart.GetAllSeasonPostersAsync(show.TvdbId, ct);
        var chosen = all.FirstOrDefault(x => SeasonSetMatcher.BareUrl(x.Poster.ImageUrl) == SeasonSetMatcher.BareUrl(imageUrl)).Poster;
        if (chosen == null || !int.TryParse(chosen.ExternalId, out _))
            return BadRequest(new { error = "Only FanArt.tv posters can be matched to a set." });

        var others = await db.Seasons.Where(s => s.LibraryItemId == show.Id && s.Id != seasonId)
                                     .OrderBy(s => s.SeasonNumber).ToListAsync(ct);
        var matches = new List<object>(); var missing = new List<int>();
        foreach (var s in others)
        {
            var best = SeasonSetMatcher.MatchFor(all, chosen, season.SeasonNumber, s.SeasonNumber);
            if (best != null)
                matches.Add(new { seasonId = s.Id, seasonNumber = s.SeasonNumber, imageUrl = best.ImageUrl, source = best.Source });
            else missing.Add(s.SeasonNumber);
        }
        return Ok(new { matches, missing });
    }

    // ── Custom poster upload (file upload → apply directly to Plex) ──────────

    [HttpPost("upload/item/{id}")]
    public async Task<ActionResult> UploadCustomPoster(int id, IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "No file provided." });
        if (!file.ContentType.StartsWith("image/")) return BadRequest(new { error = "File must be an image." });

        using var db   = await _dbFactory.CreateDbContextAsync();
        var item       = await db.LibraryItems.FindAsync(id);
        if (item == null) return NotFound();

        using var ms   = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes      = ms.ToArray();

        // Apply custom upload directly to Plex with overlays rendered on top
        var result = await _applyService.ApplyPosterAsync(id, null, PosterSource.Local, "Custom upload", bytes: bytes);
        return Ok(new { status = result });
    }

    [HttpPost("upload/season/{seasonId}")]
    public async Task<ActionResult> UploadCustomSeasonPoster(int seasonId, IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "No file provided." });
        if (!file.ContentType.StartsWith("image/")) return BadRequest(new { error = "File must be an image." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        // Same path as a picked season poster (overlays honour the season-overlay setting).
        var result = await _applyService.ApplySeasonPosterAsync(
            seasonId, null, PosterSource.Local, "Custom upload", bytes: ms.ToArray());
        return Ok(new { status = result });
    }

    // ── Apply chosen poster (from picker) ─────────────────────────────────────

    public record ApplyRequest(string ImageUrl, PosterSource Source);

    [HttpPost("apply/item/{id}")]
    public async Task<ActionResult> ApplyToItem(int id, [FromBody] ApplyRequest req)
    {
        var r = await _applyService.ApplyPosterAsync(id, req.ImageUrl, req.Source, "Manual selection");
        return Ok(new { status = r });
    }

    [HttpPost("apply/season/{seasonId}")]
    public async Task<ActionResult> ApplyToSeason(int seasonId, [FromBody] ApplyRequest req)
    {
        var r = await _applyService.ApplySeasonPosterAsync(seasonId, req.ImageUrl, req.Source, "Manual selection");
        return Ok(new { status = r });
    }

    // ── Apply current proposed poster to Plex ─────────────────────────────────

    // Apply All's season step: re-applies a season's current poster so it gets the badges too.
    [HttpPost("apply-current/season/{seasonId}")]
    public async Task<ActionResult> ApplyCurrentSeason(int seasonId)
    {
        try
        {
            var r = await _applyService.ApplyCurrentSeasonPosterAsync(seasonId);
            return Ok(new { status = r });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("apply-current/item/{id}")]
    public async Task<ActionResult> ApplyCurrent(int id, [FromQuery] bool force = false)
    {
        // Return a readable reason instead of an unhandled 500 so "Apply All" can report which
        // items failed and why. force=true (Apply All) re-pushes even already-applied posters.
        try
        {
            var r = await _applyService.ApplyCurrentPosterAsync(id, force);
            return Ok(new { status = r });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ── Dismiss proposed poster ───────────────────────────────────────────────

    [HttpPost("dismiss/item/{id}")]
    public async Task<ActionResult> Dismiss(int id)
    {
        await _applyService.DismissPosterAsync(id);
        return Ok(new { status = "dismissed" });
    }

    [HttpPost("dismiss/season/{seasonId}")]
    public async Task<ActionResult> DismissSeason(int seasonId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var season = await db.Seasons.FindAsync(seasonId);
        if (season == null) return NotFound();
        season.CurrentPosterUrl    = null;
        season.CurrentPosterSource = null;
        await db.SaveChangesAsync();
        return Ok(new { status = "dismissed" });
    }

    // ── Restore backup ────────────────────────────────────────────────────────

    [HttpPost("restore/item/{id}")]
    public async Task<ActionResult> RestorePoster(int id)
    {
        var ok = await _applyService.RestorePosterAsync(id);
        return Ok(new { success = ok });
    }

    // ── Textless preference ───────────────────────────────────────────────────

    public record TextlessRequest(bool TextlessPreferred);

    [HttpPost("item/{id}/textless-preference")]
    public async Task<ActionResult> SetTextless(int id, [FromBody] TextlessRequest req)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.LibraryItems.FindAsync(id);
        if (item == null) return NotFound();
        item.TextlessPreferred = req.TextlessPreferred;
        await db.SaveChangesAsync();
        return Ok();
    }
}
