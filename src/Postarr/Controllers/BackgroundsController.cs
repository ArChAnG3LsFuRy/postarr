using Postarr.Data;
using Postarr.Models;
using Postarr.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Controllers;

[ApiController]
[Route("api/backgrounds")]
public class BackgroundsController : ControllerBase
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly MetadataClientFactory _clientFactory;
    private readonly PosterApplyService    _applyService;
    private readonly BackupService         _backup;

    public BackgroundsController(IDbContextFactory<PostarrDbContext> db,
        MetadataClientFactory cf, PosterApplyService svc, BackupService backup)
    { _dbFactory = db; _clientFactory = cf; _applyService = svc; _backup = backup; }

    [HttpGet("candidates/item/{id}")]
    public async Task<ActionResult> GetCandidates(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var item     = await db.LibraryItems.FindAsync(id);
        if (item == null) return NotFound();
        // Items do NOT pull existing Plex art (only collections do) — see PostersController.
        var agg  = _clientFactory.BuildPosterAggregator();
        var list = item.MediaType == MediaType.Movie
            ? await agg.GetMovieBackgroundCandidatesAsync(item.TmdbId, item.TvdbId)
            : await agg.GetShowBackgroundCandidatesAsync(item.TmdbId, item.TvdbId);
        return Ok(list);
    }

    public record ApplyRequest(string ImageUrl, PosterSource Source);

    [HttpPost("upload/item/{id}")]
    public async Task<ActionResult> UploadCustomBackground(int id, IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "No file provided." });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.LibraryItems.FindAsync(id);
        if (item == null) return NotFound();
        var plex = _clientFactory.BuildMediaServerClient();
        if (plex == null) return BadRequest(new { error = $"{_clientFactory.MediaServerName} not configured." });
        await plex.UploadBackgroundAsync(item.PlexRatingKey, bytes, file.ContentType);
        // Store a displayable URL for the art Plex now has selected ("custom" rendered as "Load failed").
        item.CurrentBackgroundUrl    = await _applyService.ResolveUploadedArtUrlAsync(item.PlexRatingKey, true) ?? "custom";
        item.CurrentBackgroundSource = "Local";
        item.BackgroundAppliedToPlex = true;
        await db.SaveChangesAsync();
        return Ok(new { status = "applied" });
    }

    [HttpPost("apply-current/item/{id}")]
    public async Task<ActionResult> ApplyCurrent(int id, [FromQuery] bool force = false)
    {
        try
        {
            var r = await _applyService.ApplyCurrentBackgroundAsync(id, force);
            return Ok(new { status = r });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("dismiss/item/{id}")]
    public async Task<ActionResult> Dismiss(int id)
    {
        await _applyService.DismissBackgroundAsync(id);
        return Ok(new { status = "dismissed" });
    }

    [HttpPost("apply/item/{id}")]
    public async Task<ActionResult> Apply(int id, [FromBody] ApplyRequest req)
    {
        var r = await _applyService.ApplyBackgroundAsync(id, req.ImageUrl, req.Source, "Manual selection");
        return Ok(new { status = r });
    }

    [HttpPost("restore/item/{id}")]
    public async Task<ActionResult> Restore(int id)
    {
        var ok = await _applyService.RestoreBackgroundAsync(id);
        return Ok(new { success = ok });
    }

    [HttpGet("backup-info/{id}")]
    public async Task<ActionResult> BackupInfo(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.LibraryItems.FindAsync(id);
        if (item == null) return NotFound();
        return Ok(_backup.GetBackupInfo(item.PlexRatingKey));
    }
}
