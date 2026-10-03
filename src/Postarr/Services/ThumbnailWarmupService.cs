using Postarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Services;

/// <summary>
/// Pre-generates poster/background thumbnails in the background so the grids are fast on the
/// FIRST view, not just on repeat views. Without this, each thumbnail is created on demand the
/// first time it scrolls into view (a fetch from the source CDN + resize), so the very first
/// scroll-through would still wait. Runs shortly after startup and periodically afterwards to
/// pick up newly-scanned items; already-cached thumbnails are skipped instantly, so repeat
/// passes are cheap. Concurrency is capped so it stays gentle on the CDN and CPU.
/// </summary>
public class ThumbnailWarmupService : BackgroundService
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly ImageCacheService _cache;

    // Widths must match what the UI actually requests (see thumbUrl calls in app.js), so the
    // warmed files are the same cache entries the browser asks for.
    private const int PosterWidth       = 400;
    private const int SeasonPosterWidth = 300;
    private const int BackgroundWidth   = 600;
    // Kept low on purpose: each job decodes a full-res source image (a 2000×3000 poster is
    // ~24 MB decoded) before resizing, so a high fan-out — especially alongside a running
    // scan that is also doing SkiaSharp work — spikes memory. 2-at-a-time stays gentle.
    private const int MaxConcurrency    = 2;
    private const int BatchSize         = 100;

    public ThumbnailWarmupService(IDbContextFactory<PostarrDbContext> dbFactory, ImageCacheService cache)
    {
        _dbFactory = dbFactory;
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the app finish starting (and avoid competing with an initial scan) before warming.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await WarmAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { /* non-fatal — try again next cycle */ }

            // Re-run periodically to warm thumbnails for items added by later scans.
            try { await Task.Delay(TimeSpan.FromMinutes(15), ct); } catch { return; }
        }
    }

    private async Task WarmAsync(CancellationToken ct)
    {
        List<(string Url, int Width)> jobs;
        using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var posters     = await db.LibraryItems.Where(i => i.CurrentPosterUrl != null)
                                    .Select(i => i.CurrentPosterUrl!).ToListAsync(ct);
            var backgrounds = await db.LibraryItems.Where(i => i.CurrentBackgroundUrl != null)
                                    .Select(i => i.CurrentBackgroundUrl!).ToListAsync(ct);
            var seasons     = await db.Seasons.Where(s => s.CurrentPosterUrl != null)
                                    .Select(s => s.CurrentPosterUrl!).ToListAsync(ct);

            jobs = posters.Select(u => (u, PosterWidth))
                .Concat(seasons.Select(u => (u, SeasonPosterWidth)))
                .Concat(backgrounds.Select(u => (u, BackgroundWidth)))
                .Where(j => _cache.IsAllowed(j.Item1))
                .ToList();
        }

        // Process in small batches rather than spawning a task per job up front, so peak
        // memory stays bounded no matter how large the library is.
        using var gate = new SemaphoreSlim(MaxConcurrency);
        for (int i = 0; i < jobs.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = jobs.Skip(i).Take(BatchSize).Select(async job =>
            {
                await gate.WaitAsync(ct);
                try   { await _cache.GetThumbnailAsync(job.Url, job.Width, ct); } // no-op if already cached
                catch { /* non-fatal */ }
                finally { gate.Release(); }
            });
            await Task.WhenAll(batch);
        }
    }
}
