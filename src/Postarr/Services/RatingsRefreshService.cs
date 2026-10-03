using Postarr.Data;
using Postarr.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Services;

/// <summary>
/// Keeps the data behind the rating and award badges (IMDb, Rotten Tomatoes, audience, Oscar,
/// Emmy, Metacritic) current across the whole library, within OMDb's ~1,000-requests/day free limit —
/// plus Letterboxd / Trakt / IMDb Top 250 from MDBList's batch lookups (a handful of requests per run).
///
/// Titles are worked oldest-checked first and each run stops cleanly when OMDb reports its daily
/// limit, so the next run resumes where this one stopped — every title gets its turn, rather than
/// the same first ~1,000 being re-fetched forever. Runs every few hours, and immediately when a
/// rating/award badge is switched on in Settings (<see cref="RequestRun"/>).
///
/// Only updates Postarr's stored data. Nothing is uploaded to Plex — "Apply All to Plex" re-renders
/// posters with whatever data is current.
/// </summary>
public class RatingsRefreshService : BackgroundService
{
    // A title is re-checked at most this often. Awards/ratings change slowly, and this keeps daily
    // usage well under OMDb's cap so scans still have room for newly added titles.
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(3);
    private static readonly TimeSpan RunEvery     = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RatingsRefreshService> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public RatingsRefreshService(IServiceScopeFactory scopeFactory, ILogger<RatingsRefreshService> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    /// <summary>Start a refresh now instead of waiting for the next scheduled run.</summary>
    public void RequestRun()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { /* a run is already queued */ }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await WaitAsync(TimeSpan.FromMinutes(5), ct);   // let startup (and any initial scan) settle
        while (!ct.IsCancellationRequested)
        {
            try { await RunAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Ratings & awards refresh failed."); }
            await WaitAsync(RunEvery, ct);
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await _wake.WaitAsync(delay, ct); } catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsRepository>().Get();
        var factory   = scope.ServiceProvider.GetRequiredService<MetadataClientFactory>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PostarrDbContext>>();
        var log       = scope.ServiceProvider.GetRequiredService<ActivityLogger>();
        var server    = factory.MediaServerName;   // don't spend API quota on the inactive server's items
        var mdb       = factory.BuildMdbListClient();

        // Letterboxd / Trakt / IMDb Top 250: MDBList batch lookups for the whole library — about ten requests
        // however big it is, so simply redone every run. Independent of OMDb and its daily limit.
        if (mdb != null && RatingsEnricher.AnyBadgeNeedsMdbListBatch(settings.Overlays))
        {
            try
            {
                using var bdb = await dbFactory.CreateDbContextAsync(ct);
                var all = await bdb.LibraryItems
                    .Where(i => i.ServerType == server && (i.ImdbId != null || i.TmdbId != null))
                    .ToListAsync(ct);
                var changed = await RatingsEnricher.EnrichBatchAsync(all, mdb, ct);
                await bdb.SaveChangesAsync(ct);
                if (changed > 0)
                    await log.LogAsync($"Letterboxd / Trakt / IMDb Top 250 updated for {changed} titles (MDBList). " +
                                       "Use Apply All to put the updated badges on your posters.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "MDBList batch refresh failed.");
            }
        }

        // Nothing would use the OMDb data unless a rating/award badge — or an award collection — is switched on.
        if (!RatingsEnricher.AnyBadgeNeedsRatings(settings.Overlays)
            && !AutoCollectionService.AwardSetsEnabled(settings.AutoCollections)) return;

        var omdb = factory.BuildOmdbClient();
        if (omdb == null && mdb == null) return;
        if (omdb != null && OmdbClient.IsLimited) return;   // still inside today's limit — try next run

        using var db  = await dbFactory.CreateDbContextAsync(ct);

        var dueBefore = DateTime.UtcNow - RecheckAfter;
        var items = await db.LibraryItems
            .Where(i => i.RatingsCheckedUtc < dueBefore && (i.ImdbId != null || i.TmdbId != null) && i.ServerType == server)
            .OrderBy(i => i.RatingsCheckedUtc)
            .ThenBy(i => i.RottenTomatoesScore != null)   // never-checked titles with no data yet go first
            .ThenBy(i => i.Id)
            .ToListAsync(ct);
        if (items.Count == 0) return;

        await log.LogAsync($"Ratings & awards refresh started — {items.Count} titles due.");
        int done = 0, awardChanges = 0;
        bool hitLimit = false;

        for (int n = 0; n < items.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            if (omdb != null && OmdbClient.IsLimited) { hitLimit = true; break; }

            var item   = items[n];
            var before = (item.IsOscarWinner, item.IsOscarNominee, item.IsEmmyWinner);
            try
            {
                if (await RatingsEnricher.EnrichAsync(item, omdb, mdb, ct)) done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* skip this title */ }
            if (before != (item.IsOscarWinner, item.IsOscarNominee, item.IsEmmyWinner)) awardChanges++;

            if (n % 50 == 49) await db.SaveChangesAsync(ct);   // keep progress if the service stops mid-run
            await Task.Delay(150, ct);                          // be polite to the APIs
        }
        await db.SaveChangesAsync(ct);

        var remaining = items.Count - done;
        await log.LogAsync(hitLimit
            ? $"Ratings & awards refresh paused — OMDb daily limit reached. {done} titles updated " +
              $"({awardChanges} award changes), {remaining} still to do; resumes automatically after the limit resets."
            : $"Ratings & awards refresh complete — {done} titles updated ({awardChanges} award changes). " +
              "Use Apply All to put the updated badges on your posters.");
    }
}
