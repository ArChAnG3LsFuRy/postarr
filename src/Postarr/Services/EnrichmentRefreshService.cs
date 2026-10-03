using Postarr.Data;
using Postarr.Models;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Services;

/// <summary>
/// Runs nightly to refresh time-sensitive metadata from TMDB:
/// - Show status (Returning / Ended / Cancelled)
/// - Trending (changes weekly)
/// - Popular (changes frequently)
/// - Streaming service (can change)
///
/// This runs separately from the full library scan so badges stay
/// current without re-scanning the entire Plex library.
/// </summary>
public class EnrichmentRefreshService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EnrichmentRefreshService> _logger;

    public EnrichmentRefreshService(IServiceScopeFactory scopeFactory,
        ILogger<EnrichmentRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait 5 minutes after startup before first run
        await Task.Delay(TimeSpan.FromMinutes(5), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await RefreshAllAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Enrichment refresh failed."); }

            // Run once every 24 hours
            await Task.Delay(TimeSpan.FromHours(24), ct);
        }
    }

    private async Task RefreshAllAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var dbFactory    = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PostarrDbContext>>();
        var clientFactory= scope.ServiceProvider.GetRequiredService<MetadataClientFactory>();
        var log          = scope.ServiceProvider.GetRequiredService<ActivityLogger>();

        var tmdb = clientFactory.BuildTmdbClient();
        if (tmdb == null) return;

        using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = clientFactory.MediaServerName;   // skip items that belong to the inactive server
        var items = await db.LibraryItems
            .Where(i => i.TmdbId != null && i.ServerType == server)
            .ToListAsync(ct);

        await log.LogAsync($"Enrichment refresh started — {items.Count} items.");
        int updated = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var mediaType = item.MediaType == MediaType.Movie ? "movie" : "tv";

                // Refresh trending (TMDB trending list changes weekly)
                item.IsTrending = await tmdb.IsTrendingAsync(item.TmdbId!, mediaType, ct);

                // Refresh enriched data (status, streaming, popular)
                var enriched = item.MediaType == MediaType.Movie
                    ? await tmdb.GetMovieEnrichedAsync(item.TmdbId!, ct)
                    : await tmdb.GetShowEnrichedAsync(item.TmdbId!, ct);

                if (enriched != null)
                {
                    if (enriched.Status          != null) item.ShowStatus       = enriched.Status;
                    if (enriched.StreamingService != null) item.StreamingService = enriched.StreamingService;
                    item.IsPopular = enriched.IsPopular;
                    if (enriched.EpisodeCount.HasValue) item.EpisodeCount = enriched.EpisodeCount;
                }

                updated++;

                // Small delay between calls to avoid hammering the API
                await Task.Delay(200, ct);
            }
            catch { /* skip individual failures */ }
        }

        await db.SaveChangesAsync(ct);
        await log.LogAsync($"Enrichment refresh complete — {updated} items updated.");
    }
}
