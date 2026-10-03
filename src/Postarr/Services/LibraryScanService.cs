using Postarr.Data;
using Postarr.Hubs;
using Postarr.MediaServers;
using Postarr.Metadata;
using Postarr.Models;
using Postarr.Overlays;
using Postarr.Plex;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Services;

public class LibraryScanService
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly MetadataClientFactory _clientFactory;
    private readonly SettingsRepository    _settings;
    private readonly PosterApplyService    _applyService;
    private readonly BackupService         _backup;
    private readonly ActivityLogger        _log;
    private readonly IScanNotifier         _notifier;
    private readonly IServiceScopeFactory  _scopes;

    private static volatile bool _isScanning;
    private static readonly object _scanLock = new();
    private static CancellationTokenSource? _scanCts;

    // Upper bound on a single scan. A normal full scan of ~1800 items runs in ~20 minutes, so
    // anything past this is wedged and must be abandoned rather than blocking all later scans.
    private static readonly TimeSpan MaxScanDuration = TimeSpan.FromHours(4);

    public LibraryScanService(
        IDbContextFactory<PostarrDbContext> dbFactory, MetadataClientFactory clientFactory,
        SettingsRepository settings, PosterApplyService applyService,
        BackupService backup, ActivityLogger log, IScanNotifier notifier, IServiceScopeFactory scopes)
    {
        _dbFactory = dbFactory; _clientFactory = clientFactory;
        _settings  = settings;  _applyService  = applyService;
        _backup = backup; _log = log; _notifier = notifier; _scopes = scopes;
    }

    private async Task<bool> HasManagedCollectionsAsync()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var server = _clientFactory.MediaServerName;
        return await db.ManagedCollections.AnyAsync(m => m.ServerType == server);
    }

    public bool IsScanning => _isScanning;

    public void CancelScan()
    {
        _scanCts?.Cancel();
    }

    // forceFull=true ignores the "Incremental scan only" setting and re-enriches/re-searches every
    // item. The manual "Scan Library" button passes this so a user-triggered scan is always a real
    // full scan; only the automatic scheduled scan honours the incremental setting (to stay cheap).
    public async Task<string> RunFullScanAsync(bool forceFull = false, CancellationToken externalCt = default)
    {
        lock (_scanLock)
        {
            if (_isScanning) return "already-running";
            _isScanning = true;
            _scanCts    = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            // Watchdog: a scan that hangs (an API call that never returns, a stalled download)
            // would otherwise leave _isScanning stuck true and silently block EVERY future scan.
            // Self-cancel so the flag is always released and scanning recovers on its own.
            _scanCts.CancelAfter(MaxScanDuration);
        }

        var ct = _scanCts!.Token;

        try
        {
            var plex = _clientFactory.BuildMediaServerClient();
            if (plex == null)
            {
                await _log.LogAsync($"Scan skipped: {_clientFactory.MediaServerName} not configured.", "Warning");
                return "not-configured";
            }

            var settings = _settings.Get();
            var incrementalMode = !forceFull && settings.IncrementalScanOnly && settings.LastFullScanCompletedUtc.HasValue;

            var sections = await plex.GetLibrarySectionsAsync(ct);

            // Pre-count items for progress bar. Plex's API doesn't expose a cheap
            // "changed since" filter on this endpoint, so we still list every item —
            // but in incremental mode we skip the expensive enrichment/poster-search
            // work below for anything that's already known and unchanged.
            var allItems = new List<(PlexSection Sec, PlexMediaItem Item)>();
            foreach (var sec in sections)
                foreach (var item in await plex.GetSectionItemsAsync(sec, ct))
                    allItems.Add((sec, item));

            await AddItemDetailsAsync(plex, allItems.Select(x => x.Item), ct);

            await _log.LogAsync(incrementalMode
                ? $"Incremental scan started — checking {allItems.Count} items for new/changed content."
                : $"Full library scan started — {allItems.Count} items.");
            await _notifier.ScanStartedAsync(allItems.Count);

            int processed = 0, newCount = 0, updatedCount = 0, skippedCount = 0, errors = 0;

            foreach (var (sec, plexItem) in allItems)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (isNew, isChanged, skipped) = await UpsertItemAsync(sec, plexItem, incrementalMode, ct);
                    if (isNew) newCount++;
                    else if (isChanged) updatedCount++;
                    else if (skipped) skippedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors++;
                    await _log.LogAsync($"Error: {plexItem.Title}: {ex.Message}", "Error");
                }
                processed++;
                await _notifier.ItemScannedAsync(plexItem.Title, "scanned", processed, allItems.Count);
            }

            // ── Prune items no longer in Plex ─────────────────────────────────────
            // Plex assigns a NEW rating key when a file is replaced/re-downloaded, so the old
            // item would otherwise linger forever as a duplicate (old poster stays alongside the
            // new one). Remove DB items whose rating key wasn't seen this scan — but only within
            // sections that actually returned items, so a transient empty/failed listing can't
            // wipe the library. Seasons cascade-delete with their show.
            try
            {
                var seenKeys        = allItems.Select(x => x.Item.RatingKey).ToHashSet();
                var scannedSections = allItems.Select(x => x.Sec.Key).ToHashSet();
                var server          = _clientFactory.MediaServerName;   // never prune another server's items
                using var pruneDb   = await _dbFactory.CreateDbContextAsync(ct);
                var candidates = await pruneDb.LibraryItems
                    .Where(i => scannedSections.Contains(i.PlexLibrarySectionId) && i.ServerType == server)
                    .ToListAsync(ct);
                var orphans = candidates.Where(i => !seenKeys.Contains(i.PlexRatingKey)).ToList();
                if (orphans.Count > 0)
                {
                    pruneDb.LibraryItems.RemoveRange(orphans);
                    await pruneDb.SaveChangesAsync(ct);
                    // Also delete each removed item's stored original-poster backup so it doesn't
                    // linger as orphaned files once the media (and its Plex poster) are gone.
                    foreach (var o in orphans) _backup.DeleteItemBackup(o.PlexRatingKey);
                    await _log.LogAsync($"Removed {orphans.Count} item(s) no longer in {_clientFactory.MediaServerName}:{string.Join(", ", orphans.Take(20).Select(o => o.Title))}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _log.LogAsync($"Prune step failed (non-fatal): {ex.Message}", "Error");
            }

            // Scan collections
            foreach (var sec in sections)
            {
                ct.ThrowIfCancellationRequested();
                try { await ScanCollectionsAsync(plex, sec, ct); } catch { /* non-fatal */ }
            }

            await _log.LogAsync($"Scan complete — {newCount} new, {updatedCount} updated, {skippedCount} unchanged (skipped), {errors} errors.");
            await _notifier.ScanCompletedAsync(newCount, updatedCount, errors);

            // Record completion time so incremental mode has a baseline next run
            settings.LastFullScanCompletedUtc = DateTime.UtcNow;
            _settings.Save(settings);

            // Keep auto-collections current (charts move, new titles arrive). Runs in the background so the scan
            // finishes now; resolved lazily because the collection service itself uses this one.
            // Also runs when every set is off but collections remain, so switched-off sets get cleaned up.
            var auto = _settings.Get().AutoCollections;
            if (auto?.SyncAfterScan == true && (auto.Sets.Any(s => s.Enabled) || await HasManagedCollectionsAsync()))
                AutoCollectionService.StartInBackground(_scopes);

            return "completed";
        }
        catch (OperationCanceledException)
        {
            await _log.LogAsync("Scan cancelled by user.", "Warning");
            await _notifier.ScanCompletedAsync(0, 0, 0);
            return "cancelled";
        }
        catch (Exception ex)
        {
            await _log.LogAsync($"Scan failed: {ex.Message}", "Error");
            return "error";
        }
        finally
        {
            lock (_scanLock)
            {
                _isScanning = false;
                _scanCts?.Dispose();     // was leaked on every scan
                _scanCts = null;
            }
        }
    }

    public async Task ScanSingleItemAsync(string ratingKey, bool isShow, CancellationToken ct = default)
    {
        var plex = _clientFactory.BuildMediaServerClient();
        if (plex == null) return;
        var sections = await plex.GetLibrarySectionsAsync(ct);
        foreach (var sec in sections)
        {
            if (isShow != (sec.Type == "show")) continue;
            var items = await plex.GetSectionItemsAsync(sec, ct);
            var match = items.FirstOrDefault(i => i.RatingKey == ratingKey);
            if (match != null)
            {
                await AddItemDetailsAsync(plex, new[] { match }, ct);
                await UpsertItemAsync(sec, match, incrementalMode: false, ct);
                await _log.LogAsync($"Webhook scan: {match.Title}");
            }
        }
    }

    /// <summary>
    /// The section listing reports every movie as "SDR" because it omits per-stream colour data, and has no
    /// stream languages or rating details. Fill those in from each item's full metadata, batched by rating key
    /// (cheap: ~1 request per 50 items): real HDR/DV (movies — shows get theirs from episodes in
    /// UpsertItemAsync), dual/multi audio &amp; subtitle counts, and the IMDb / Rotten Tomatoes scores the
    /// server already holds. Non-fatal: on failure the items keep whatever the listing reported.
    /// </summary>
    private static async Task AddItemDetailsAsync(IMediaServerClient plex, IEnumerable<PlexMediaItem> items, CancellationToken ct)
    {
        var list = items.Where(i => !string.IsNullOrEmpty(i.RatingKey)).ToList();
        try
        {
            var details = await plex.GetStreamDetailsAsync(list.Select(i => i.RatingKey), ct);
            foreach (var item in list)
                if (details.TryGetValue(item.RatingKey, out var d))
                {
                    if (d.DynamicRange != null)      item.VideoDynamicRange     = d.DynamicRange;
                    if (d.AudioLanguages != null)    item.AudioLanguageCount    = d.AudioLanguages;
                    if (d.SubtitleLanguages != null) item.SubtitleLanguageCount = d.SubtitleLanguages;
                    item.ServerRatingsKnown   = true;
                    item.ServerImdbRating     = d.ImdbRating;
                    item.ServerRottenTomatoes = d.RottenTomatoes;
                    item.ServerAudienceScore  = d.AudienceScore;
                }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* non-fatal */ }
    }

    /// <summary>
    /// Adds/refreshes one item the server reported as new, when the caller already fetched it (Jellyfin
    /// live updates) — same processing as a scan, without listing whole libraries.
    /// </summary>
    public async Task ScanItemAsync(PlexSection sec, PlexMediaItem item, CancellationToken ct = default)
    {
        await UpsertItemAsync(sec, item, incrementalMode: false, ct);
        await _log.LogAsync($"Live update: {item.Title}");
    }

    /// <summary>Re-reads every section's collections (e.g. after auto-collections created or changed some).</summary>
    public async Task RefreshCollectionsAsync(CancellationToken ct = default)
    {
        var plex = _clientFactory.BuildMediaServerClient();
        if (plex == null) return;
        foreach (var sec in await plex.GetLibrarySectionsAsync(ct))
            await ScanCollectionsAsync(plex, sec, ct);
    }

    private async Task ScanCollectionsAsync(IMediaServerClient plex, PlexSection sec, CancellationToken ct)
    {
        var cols = await plex.GetCollectionsAsync(sec, ct);
        var tmdb = _clientFactory.BuildTmdbClient();
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Collections Postarr built: a franchise knows its TMDB id; the rest (genres, decades, charts…) have no
        // TMDB collection, and a title search would pin unrelated franchise art on them ("Action" → some film series).
        var managed = (await db.ManagedCollections.ToListAsync(ct))
            .GroupBy(m => m.ServerCollectionId).ToDictionary(g => g.Key, g => g.First().TmdbCollectionId);

        foreach (var col in cols)
        {
            if (managed.TryGetValue(col.RatingKey, out var managedTmdb)) col.TmdbId = managedTmdb ?? "";
            var existing = await db.Collections.FirstOrDefaultAsync(c => c.PlexRatingKey == col.RatingKey, ct);
            if (existing == null)
            {
                existing = new PlexCollection { PlexRatingKey = col.RatingKey, PlexLibrarySectionId = col.SectionKey, MediaType = col.MediaType };
                db.Collections.Add(existing);
            }
            existing.Title         = col.Title;
            existing.ItemCount     = col.ItemCount;
            existing.LastSeenAtUtc = DateTime.UtcNow;
            existing.ServerType    = _clientFactory.MediaServerName;

            // Use the server's own TMDB collection ID when it has one (exact), else search by title.
            if (string.IsNullOrEmpty(existing.TmdbId) && !string.IsNullOrEmpty(col.TmdbId))
                existing.TmdbId = col.TmdbId;

            // Resolve TMDB collection ID if not already known (never for Postarr's own non-franchise collections)
            if (string.IsNullOrEmpty(existing.TmdbId) && tmdb != null && !managed.ContainsKey(col.RatingKey))
            {
                try
                {
                    var tmdbId = await tmdb.SearchCollectionAsync(col.Title, ct);
                    if (tmdbId != null)
                    {
                        existing.TmdbId = tmdbId;
                        await _log.LogAsync($"Matched collection '{col.Title}' → TMDB collection {tmdbId}");
                    }
                }
                catch { /* non-fatal */ }
            }

            // Auto-propose a poster if none chosen yet and we have a TMDB ID.
            // Skip collections the user dismissed, or they'd reappear on the next scan.
            if (string.IsNullOrEmpty(existing.CurrentPosterUrl) && !existing.PosterDismissed
                && !string.IsNullOrEmpty(existing.TmdbId) && tmdb != null)
            {
                try
                {
                    var candidates = await tmdb.GetCollectionPostersAsync(existing.TmdbId, ct);
                    if (candidates.Count > 0)
                    {
                        existing.CurrentPosterUrl    = candidates[0].ImageUrl;
                        existing.CurrentPosterSource = PosterSource.Tmdb.ToString();
                        await _log.LogAsync($"Auto-proposed collection poster: {col.Title}");
                    }
                }
                catch { /* non-fatal */ }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<(bool IsNew, bool IsChanged, bool Skipped)> UpsertItemAsync(
        PlexSection sec, PlexMediaItem plexItem, bool incrementalMode, CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.LibraryItems
            .Include(i => i.Seasons)
            .FirstOrDefaultAsync(i => i.PlexRatingKey == plexItem.RatingKey, ct);

        bool isNew = existing == null, isChanged = false;

        if (existing == null)
        {
            existing = new LibraryItem { PlexRatingKey = plexItem.RatingKey, PlexLibrarySectionId = sec.Key, MediaType = plexItem.MediaType,
                                         ServerType = _clientFactory.MediaServerName };
            db.LibraryItems.Add(existing);
        }
        else
        {
            existing.ServerType = _clientFactory.MediaServerName;
            // Only movies expose top-level resolution here. For shows, plexItem's values
            // are always null (quality is derived from episodes further below), so a change
            // is never detected from this comparison — and comparing the show's *derived*
            // resolution against plexItem's null would spuriously flag every scan as changed.
            isChanged = plexItem.MediaType == MediaType.Movie
                && (existing.VideoResolution != plexItem.VideoResolution
                    || existing.VideoDynamicRange != plexItem.VideoDynamicRange);
        }

        // Cheap facts straight from the server listing, refreshed on every scan — even when the rest of
        // the item is skipped — so the NEW, runtime, source, versions and language badges stay current.
        existing.AddedAtUtc            = plexItem.AddedAtUtc ?? existing.AddedAtUtc;
        existing.LatestSeasonAddedUtc  = plexItem.LatestSeasonAddedUtc ?? existing.LatestSeasonAddedUtc;
        existing.RuntimeMinutes        = plexItem.RuntimeMinutes ?? existing.RuntimeMinutes;
        existing.VersionCount          = plexItem.VersionCount ?? existing.VersionCount;
        if (plexItem.MediaType == MediaType.Movie) existing.VideoSource = plexItem.VideoSource;
        existing.AudioLanguageCount    = plexItem.AudioLanguageCount ?? existing.AudioLanguageCount;
        existing.SubtitleLanguageCount = plexItem.SubtitleLanguageCount ?? existing.SubtitleLanguageCount;
        if (plexItem.ServerRatingsKnown) RatingsEnricher.ApplyServerRatings(existing,
            plexItem.ServerImdbRating, plexItem.ServerRottenTomatoes, plexItem.ServerAudienceScore);

        // NEW badge arrival / expiry: the poster on the server has (or lacks) a NEW badge from when it was
        // applied. When that no longer matches — the badge expired, or a new season just arrived — the
        // poster is stale: mark it for re-apply, and re-apply it right away when auto-apply is on.
        var ov = _settings.Get().Overlays;
        bool newBadgeStale = ov.NewBadgeEnabled && existing.PosterAppliedToPlex
            && (OverlayRenderer.NewBadgeLabel(existing, ov, DateTime.UtcNow) != null) != existing.NewBadgeOnPoster;
        if (newBadgeStale) existing.PosterAppliedToPlex = false;

        // A show that gained (or lost) a season must NOT be skipped in incremental mode, or new
        // seasons would never appear until a manual full scan. Shows never trip the `isChanged`
        // check above (their quality is null at the show level), so compare season counts directly
        // — the section listing already carries every season, so this is free.
        bool seasonsChanged = !isNew && plexItem.MediaType == MediaType.Show
            && existing.Seasons.Count != plexItem.Seasons.Count;

        // Incremental mode: if this item already exists, hasn't changed, and already has
        // a poster, skip all the expensive work below (TMDB enrichment, poster search).
        // Still touch LastSeenAtUtc so Plex-side deletions can be detected later.
        if (incrementalMode && !isNew && !isChanged && !seasonsChanged && !string.IsNullOrEmpty(existing.CurrentPosterUrl))
        {
            existing.LastSeenAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (newBadgeStale) await ReapplyForNewBadgeAsync(existing.Id, ct);
            return (false, false, true);
        }

        existing.Title             = plexItem.Title;
        existing.Year              = plexItem.Year;
        existing.TmdbId            = plexItem.TmdbId;
        existing.TvdbId            = plexItem.TvdbId;
        existing.ImdbId            = plexItem.ImdbId;
        existing.VideoResolution   = plexItem.VideoResolution;
        existing.VideoDynamicRange = plexItem.VideoDynamicRange;
        existing.AudioCodec        = plexItem.AudioCodec;

        // Shows have no top-level Media element, so resolution/dynamic range must be
        // aggregated from their episodes. This lets show posters — and season posters,
        // which reuse the parent show's metadata — carry a resolution badge. Non-fatal:
        // if the lookup fails the show simply renders without a resolution badge.
        if (plexItem.MediaType == MediaType.Show)
        {
            try
            {
                var plex = _clientFactory.BuildMediaServerClient();
                if (plex != null)
                {
                    var (res, dr, ac) = await plex.GetShowAggregateQualityAsync(plexItem.RatingKey, ct);
                    if (!string.IsNullOrEmpty(res)) existing.VideoResolution   = res;
                    if (!string.IsNullOrEmpty(dr))  existing.VideoDynamicRange = dr;
                    if (!string.IsNullOrEmpty(ac))  existing.AudioCodec        = ac;
                }
            }
            catch { /* non-fatal — show keeps whatever resolution it had (usually none) */ }
        }
        existing.Edition           = plexItem.Edition;
        existing.Studio            = plexItem.Studio;
        existing.Network           = plexItem.Network;
        existing.ContentRating     = plexItem.ContentRating;
        existing.LastSeenAtUtc     = DateTime.UtcNow;

        // Enrich from TMDB
        var tmdb = _clientFactory.BuildTmdbClient();
        if (tmdb != null)
        {
            try
            {
                TmdbEnrichedData? enriched = plexItem.MediaType == MediaType.Movie && plexItem.TmdbId != null
                    ? await tmdb.GetMovieEnrichedAsync(plexItem.TmdbId, ct)
                    : plexItem.TmdbId != null ? await tmdb.GetShowEnrichedAsync(plexItem.TmdbId, ct) : null;

                if (enriched != null)
                {
                    existing.StreamingService = enriched.StreamingService;
                    existing.ShowStatus       = enriched.Status;
                    existing.EpisodeCount     = enriched.EpisodeCount;
                    existing.ContentLanguage  = enriched.ContentLanguage;
                    existing.IsPopular        = enriched.IsPopular;
                    // For the Genre / Franchise auto-collections.
                    AutoCollectionService.StoreTmdbDetails(existing, enriched);
                    if (enriched.Network != null) existing.Network = enriched.Network;
                    if (enriched.Studio  != null) existing.Studio  = enriched.Studio;
                    // TMDB rating only as a placeholder until OMDb supplies the real IMDb rating — once an
                    // item has one, a later scan must not overwrite it with TMDB's.
                    if (enriched.TmdbRating.HasValue && existing.ImdbRating == null) existing.ImdbRating = enriched.TmdbRating;
                    if (plexItem.TmdbId  != null)
                        existing.IsTrending = await tmdb.IsTrendingAsync(plexItem.TmdbId,
                            plexItem.MediaType == MediaType.Movie ? "movie" : "tv", ct);

                    existing.ImdbId = enriched.ImdbId ?? existing.ImdbId;

                    // Ratings + Oscar/Emmy flags (OMDb, MDBList for TV RT). Only for titles never
                    // checked: OMDb's ~1,000/day cap used to be spent on the same first titles every
                    // full scan, leaving the rest of the library permanently badge-less.
                    // RatingsRefreshService keeps already-checked titles current, oldest first.
                    if (existing.RatingsCheckedUtc == DateTime.MinValue)
                    {
                        try
                        {
                            await RatingsEnricher.EnrichAsync(existing,
                                _clientFactory.BuildOmdbClient(), _clientFactory.BuildMdbListClient(), ct);
                        }
                        catch { /* non-fatal */ }
                    }
                }
            }
            catch { /* non-fatal enrichment failure */ }
        }

        // Upsert seasons
        bool anyNewSeason = false;
        foreach (var ps in plexItem.Seasons)
        {
            var s = existing.Seasons.FirstOrDefault(x => x.PlexRatingKey == ps.RatingKey);
            if (s == null)
            {
                existing.Seasons.Add(new SeasonItem { PlexRatingKey = ps.RatingKey, SeasonNumber = ps.SeasonNumber, Title = ps.Title, EpisodeCount = ps.LeafCount, IsNew = true });
                anyNewSeason = true;
            }
            else { s.Title = ps.Title; s.EpisodeCount = ps.LeafCount; }
        }
        // Drop seasons Plex no longer has — but only if the listing actually returned seasons, so a
        // transient empty/failed children fetch can't wipe a show's seasons.
        if (plexItem.Seasons.Count > 0)
        {
            var plexSeasonKeys = plexItem.Seasons.Select(p => p.RatingKey).ToHashSet();
            var goneSeasons    = existing.Seasons.Where(s => !plexSeasonKeys.Contains(s.PlexRatingKey)).ToList();
            foreach (var g in goneSeasons) existing.Seasons.Remove(g);
        }

        await db.SaveChangesAsync(ct);

        if (isNew || isChanged)
        {
            var reason = isNew ? "New item" : "Quality changed";
            await MaybeAutoSelectPosterAsync(existing.Id, reason, ct);
            await MaybeAutoSelectBackgroundAsync(existing.Id, reason, ct);
        }

        // Auto-select season posters for new seasons or shows
        if (anyNewSeason || isNew)
            await MaybeAutoSelectSeasonPostersAsync(existing.Id, ct);

        if (newBadgeStale) await ReapplyForNewBadgeAsync(existing.Id, ct);
        return (isNew, isChanged, false);
    }

    // With auto-apply on, a poster whose NEW badge just appeared/expired is re-pushed immediately;
    // otherwise it stays marked "not applied" and Apply All (or its card's Apply) picks it up.
    private async Task ReapplyForNewBadgeAsync(int id, CancellationToken ct)
    {
        if (!_settings.Get().AutoApplyOnScan) return;
        try { await _applyService.ApplyCurrentPosterAsync(id, force: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* stays pending; Apply All will retry */ }
    }

    private async Task MaybeAutoSelectSeasonPostersAsync(int libraryItemId, CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.Include(i => i.Seasons)
            .FirstOrDefaultAsync(i => i.Id == libraryItemId, ct);
        if (item == null) return;

        var agg    = _clientFactory.BuildPosterAggregator();
        var prefer = item.TextlessPreferred || _settings.Get().PreferTextlessPosters;
        var empty  = item.Seasons.Where(s => string.IsNullOrEmpty(s.CurrentPosterUrl)).ToList();
        var fromSet = await PickSeasonSetAsync(item, empty, prefer, ct);

        foreach (var season in empty)
        {
            try
            {
                PosterCandidate best;
                if (fromSet.TryGetValue(season.Id, out var setPoster)) best = setPoster;
                else
                {
                    var candidates = await agg.GetSeasonPosterCandidatesAsync(item.TmdbId, item.TvdbId, season.SeasonNumber, ct);
                    if (candidates.Count == 0) continue;
                    best = (prefer ? candidates.FirstOrDefault(c => c.IsTextless) : null) ?? candidates.First();
                }
                // Same rule as shows and movies: with "Auto-apply on scan" on, push it (badges included)
                // instead of only proposing it — seasons used to be proposal-only whatever the setting.
                if (_settings.Get().AutoApplyOnScan)
                    await _applyService.ApplySeasonPosterAsync(season.Id, best.ImageUrl, best.Source, "New season", ct);
                else
                    await _applyService.ProposeSeasonPosterAsync(season.Id, best.ImageUrl, best.Source, ct);
            }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>
    /// "Prefer matching season sets": season id → the FanArt.tv poster from one set for the seasons that need a poster.
    /// A show whose existing seasons already use a FanArt.tv set continues that set; otherwise the set covering the most
    /// of the empty seasons (at least two) is used. Seasons the set doesn't cover are left to the normal choice.
    /// </summary>
    private async Task<Dictionary<int, PosterCandidate>> PickSeasonSetAsync(LibraryItem item, List<SeasonItem> empty, bool preferTextless,
                                                                           CancellationToken ct)
    {
        var picks = new Dictionary<int, PosterCandidate>();
        if (!_settings.Get().PreferSeasonSets || empty.Count == 0 || string.IsNullOrEmpty(item.TvdbId)) return picks;
        var fanart = _clientFactory.BuildFanArtClient();
        if (fanart == null) return picks;
        try
        {
            var all = await fanart.GetAllSeasonPostersAsync(item.TvdbId, ct);
            if (all.Count == 0) return picks;

            (PosterCandidate Poster, int Season)? anchor = null;
            foreach (var s in item.Seasons.Where(s => !string.IsNullOrEmpty(s.CurrentPosterUrl)).OrderBy(s => s.SeasonNumber))
            {
                var hit = all.FirstOrDefault(x => x.Season == s.SeasonNumber
                                                  && SeasonSetMatcher.BareUrl(x.Poster.ImageUrl) == SeasonSetMatcher.BareUrl(s.CurrentPosterUrl));
                if (hit.Poster != null) { anchor = (hit.Poster, hit.Season); break; }
            }
            anchor ??= SeasonSetMatcher.BestSet(all, empty.Select(s => s.SeasonNumber).ToList(), preferTextless);
            if (anchor == null) return picks;

            foreach (var s in empty)
                if (SeasonSetMatcher.MatchFor(all, anchor.Value.Poster, anchor.Value.Season, s.SeasonNumber) is { } p)
                    picks[s.Id] = p;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* non-fatal: these seasons get the usual pick */ }
        return picks;
    }

    private async Task MaybeAutoSelectPosterAsync(int id, string reason, CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item     = await db.LibraryItems.FindAsync(new object[] { id }, ct);
        if (item == null) return;
        // Respect an explicit dismissal — otherwise the next scan would propose a new poster and
        // the item the user deliberately hid would reappear in the grid.
        if (item.PosterDismissed) return;

        var agg        = _clientFactory.BuildPosterAggregator();
        var candidates = item.MediaType == MediaType.Movie
            ? await agg.GetMoviePosterCandidatesAsync(item.TmdbId, item.TvdbId, ct)
            : await agg.GetShowPosterCandidatesAsync(item.TmdbId, item.TvdbId, ct);
        if (candidates.Count == 0) return;

        var prefer = item.TextlessPreferred || _settings.Get().PreferTextlessPosters;
        var best   = (prefer ? candidates.FirstOrDefault(c => c.IsTextless) : null) ?? candidates.First();

        if (_settings.Get().AutoApplyOnScan)
        {
            // Auto-apply tickbox is on: push straight to Plex with overlays rendered.
            await _applyService.ApplyPosterAsync(id, best.ImageUrl, best.Source, reason, ct);
        }
        else
        {
            // Default: propose only — shows on the card, user clicks Apply themselves.
            await _applyService.ProposePosterAsync(id, best.ImageUrl, best.Source, reason, ct);
        }
    }

    private async Task MaybeAutoSelectBackgroundAsync(int id, string reason, CancellationToken ct)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item     = await db.LibraryItems.FindAsync(new object[] { id }, ct);
        if (item == null || !string.IsNullOrEmpty(item.CurrentBackgroundUrl)) return;

        var agg        = _clientFactory.BuildPosterAggregator();
        var candidates = item.MediaType == MediaType.Movie
            ? await agg.GetMovieBackgroundCandidatesAsync(item.TmdbId, item.TvdbId, ct)
            : await agg.GetShowBackgroundCandidatesAsync(item.TmdbId, item.TvdbId, ct);
        if (candidates.Count == 0) return;

        var best = candidates.First();

        if (_settings.Get().AutoApplyOnScan)
            await _applyService.ApplyBackgroundAsync(id, best.ImageUrl, best.Source, reason, ct);
        else
            await _applyService.ProposeBackgroundAsync(id, best.ImageUrl, best.Source, ct);
    }
}
