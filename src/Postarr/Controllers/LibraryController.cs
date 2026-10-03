using Postarr.Data;
using Postarr.Models;
using Postarr.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Controllers;

[ApiController]
[Route("api/library")]
public class LibraryController : ControllerBase
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly LibraryScanService _scanService;
    private readonly SettingsRepository _settings;

    public LibraryController(IDbContextFactory<PostarrDbContext> db, LibraryScanService scan, SettingsRepository settings)
    { _dbFactory = db; _scanService = scan; _settings = settings; }

    // Only the active media server's items are listed. Switching server type in Settings hides the
    // other server's items (nothing is deleted) and switching back brings them straight back.
    private string ActiveServer => MetadataClientFactory.ServerKey(_settings.Get());

    [HttpGet("movies")]
    public async Task<ActionResult> GetMovies()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var server = ActiveServer;
        return Ok(await db.LibraryItems.Where(i => i.MediaType == MediaType.Movie && i.ServerType == server).OrderBy(i => i.Title).ToListAsync());
    }

    [HttpGet("shows")]
    public async Task<ActionResult> GetShows()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var server = ActiveServer;
        return Ok(await db.LibraryItems.Include(i => i.Seasons).Where(i => i.MediaType == MediaType.Show && i.ServerType == server).OrderBy(i => i.Title).ToListAsync());
    }

    [HttpGet("collections/movies")]
    public async Task<ActionResult> GetMovieCollections()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var server = ActiveServer;
        return Ok(await db.Collections.Where(c => c.MediaType == MediaType.Movie && c.ServerType == server).OrderBy(c => c.Title).ToListAsync());
    }

    [HttpGet("collections/shows")]
    public async Task<ActionResult> GetShowCollections()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var server = ActiveServer;
        return Ok(await db.Collections.Where(c => c.MediaType == MediaType.Show && c.ServerType == server).OrderBy(c => c.Title).ToListAsync());
    }

    [HttpGet("item/{id}")]
    public async Task<ActionResult> GetItem(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.LibraryItems.Include(i => i.Seasons).FirstOrDefaultAsync(i => i.Id == id);
        return item == null ? NotFound() : Ok(item);
    }

    [HttpGet("scan/status")]
    public ActionResult ScanStatus() => Ok(new { isScanning = _scanService.IsScanning });

    [HttpPost("scan")]
    public ActionResult TriggerScan()
    {
        // Tell the client the truth: if a scan is already running (e.g. the one that auto-starts
        // on boot), clicking Scan is a no-op — previously it still returned "scan-started", so the
        // button looked broken ("said scan started but nothing happened"). Errors from the
        // fire-and-forget task were also swallowed; log them so a failed scan isn't invisible.
        if (_scanService.IsScanning)
            return Ok(new { status = "already-running" });

        // The manual button is always a real full scan (re-enrich + re-search every item), even when
        // "Incremental scan only" is ticked for the background scheduled scans.
        _ = Task.Run(async () =>
        {
            try { await _scanService.RunFullScanAsync(forceFull: true); }
            catch (Exception ex) { Console.WriteLine($"[Scan] Unhandled scan failure: {ex}"); }
        });
        return Accepted(new { status = "scan-started" });
    }

    [HttpPost("scan/cancel")]
    public ActionResult CancelScan()
    {
        _scanService.CancelScan();
        return Ok(new { status = "cancelling" });
    }

    [HttpGet("activity")]
    public async Task<ActionResult> GetActivity([FromQuery] int take = 100)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return Ok(await db.ActivityLog.OrderByDescending(e => e.TimestampUtc).Take(take).ToListAsync());
    }

    [HttpGet("pending")]
    public async Task<ActionResult> GetPendingChanges()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return Ok(await db.PendingChanges.OrderByDescending(p => p.CreatedAtUtc).ToListAsync());
    }
}
