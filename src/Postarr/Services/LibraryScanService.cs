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

    // ── Libraries that no longer exist on the media server ──────────────────────────────────────────
    // A scan only prunes items inside libraries the server still lists, so an outage or a library that is
    // deleted and later restored (Plex rebuilt from a backup) can never wipe Postarr's records. The flip
    // side: items from a library that is really gone (e.g. a temporary library created during a recovery)
    // are never pruned. These are detected here and removed only when the user confirms (Health page).

    public record StaleLibrary(string SectionId, int Items, int Collections, List<string> SampleTitles, DateTime? NewestAddedUtc);

    /// <summary>Libraries Postarr has items for that the server no longer lists; null when the server can't be
    /// asked reliably (not configured, unreachable, or it listed no libraries at all).</summary>
    public async Task<List<StaleLibrary>?> GetStaleLibrariesAsync(CancellationToken ct = default)
    {
        var current = await CurrentSectionKeysAsync(ct);
        if (current == null) return null;

        var server = _clientFactory.MediaServerName;
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = await db.LibraryItems.AsNoTracking()
            .Where(i => i.ServerType == server && i.PlexLibrarySectionId != "")
            .Select(i => new { i.PlexLibrarySectionId, i.Title, i.AddedAtUtc })
            .ToListAsync(ct);
        var cols = await db.Collections.AsNoTracking()
            .Where(c => c.ServerType == server && c.PlexLibrarySectionId != "")
            .Select(c => c.PlexLibrarySectionId)
            .ToListAsync(ct);

        return items.Where(i => !current.Contains(i.PlexLibrarySectionId))
            .GroupBy(i => i.PlexLibrarySectionId)
            .Select(g => new StaleLibrary(g.Key, g.Count(), cols.Count(c => c == g.Key),
                g.OrderBy(i => i.Title).Select(i => i.Title).Take(8).ToList(),
                g.Max(i => i.AddedAtUtc)))
            .OrderBy(s => s.SectionId)
            .ToList();
    }

    /// <summary>Removes Postarr's records (items, their seasons, collections and stored original-poster backups) for
    /// a library the server no longer lists. Re-checked against a live listing, so a library that has come back
    /// is never touched. Nothing is changed on the media server itself.</summary>
    public async Task<(int Items, int Collections)?> RemoveStaleLibraryAsync(string sectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sectionId) || _isScanning) return null;
        var current = await CurrentSectionKeysAsync(ct);
        if (current == null || current.Contains(sectionId)) return null;

        var server = _clientFactory.MediaServerName;
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = await db.LibraryItems.Where(i => i.ServerType == server && i.PlexLibrarySectionId == sectionId).ToListAsync(ct);
        var cols  = await db.Collections.Where(c => c.ServerType == server && c.PlexLibrarySectionId == sectionId).ToListAsync(ct);
        if (items.Count == 0 && cols.Count == 0) return (0, 0);

        db.LibraryItems.RemoveRange(items);   // seasons cascade-delete with their show
        db.Collections.RemoveRange(cols);
        await db.SaveChangesAsync(ct);
        foreach (var i in items) _backup.DeleteItemBackup(i.PlexRatingKey);

        await _log.LogAsync($"Removed {items.Count} item(s) and {cols.Count} collection(s) from library {sectionId}, which no longer exists in " +
                            $"{server}: {string.Join(", ", items.Take(20).Select(i => i.Title))}");
        return (items.Count, cols.Count);
    }

    private async Task<HashSet<string>?> CurrentSectionKeysAsync(CancellationToken ct)
    {
        var client = _clientFactory.BuildMediaServerClient();
        if (client == null) return null;
        try
        {
            var sections = await client.GetLibrarySectionsAsync(ct);
            // An empty listing is far more likely a server still starting up than every library deleted.
            return sections.Count == 0 ? null : sections.Select(s => s.Key).ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

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

            // For recognising re-added titles: a record's key is gone when its library isn't listed any more, or
            // the library was listed with items and the key wasn't among them. A library that came back empty is
            // treated as unknown (more likely a hiccup than every item re-added), so nothing in it is "gone".
            var listedKeys     = allItems.Select(x => x.Item.RatingKey).ToHashSet();
            var listedSections = sections.Select(x => x.Key).ToHashSet();
            var filledSections = allItems.Select(x => x.Sec.Key).ToHashSet();
            Func<LibraryItem, Task<bool>>? isGone = allItems.Count == 0 ? null : r => Task.FromResult(
                !listedSections.Contains(r.PlexLibrarySectionId)
                || (filledSections.Contains(r.PlexLibrarySectionId) && !listedKeys.Contains(r.PlexRatingKey)));

            foreach (var (sec, plexItem) in allItems)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (isNew, isChanged, skipped) = await UpsertItemAsync(sec, plexItem, incrementalMode, ct, isGone);
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

            // Items from libraries the server no longer has are kept (see GetStaleLibrariesAsync) — say so.
            try
            {
                var stale = await GetStaleLibrariesAsync(ct);
                if (stale is { Count: > 0 })
                    await _log.LogAsync($"{stale.Sum(s => s.Items)} item(s) belong to {stale.Count} librar{(stale.Count == 1 ? "y" : "ies")} " +
                                        $"no longer in {_clientFactory.MediaServerName} — kept for now; remove them from the Health page.", "Warning");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* informational only */ }

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
        var listings = new Dictionary<string, HashSet<string>>();
        foreach (var sec in sections)
        {
            if (isShow != (sec.Type == "show")) continue;
            var items = await plex.GetSectionItemsAsync(sec, ct);
            if (items.Count > 0) listings[sec.Key] = items.Select(i => i.RatingKey).ToHashSet();
            var match = items.FirstOrDefault(i => i.RatingKey == ratingKey);
            if (match != null)
            {
                await AddItemDetailsAsync(plex, new[] { match }, ct);
                await UpsertItemAsync(sec, match, incrementalMode: false, ct, LiveGoneCheck(plex, sections, listings, ct));
                await _log.LogAsync($"Webhook scan: {match.Title}");
            }
        }
    }

    /// <summary>
    /// Gone-check for single-item updates (webhook / live connection), which don't list the whole server: a record's
    /// key is gone when its library is no longer listed, or that library's listing (fetched on demand, at most once,
    /// and only when a re-add candidate exists) doesn't contain it. Empty listings count as unknown, never gone.
    /// </summary>
    private static Func<LibraryItem, Task<bool>> LiveGoneCheck(IMediaServerClient client, List<PlexSection> sections,
                                                              Dictionary<string, HashSet<string>> listings, CancellationToken ct)
        => async r =>
        {
            if (sections.Count == 0) return false;
            var sec = sections.FirstOrDefault(x => x.Key == r.PlexLibrarySectionId);
            if (sec == null) return true;
            if (!listings.TryGetValue(sec.Key, out var keys))
            {
                keys = (await client.GetSectionItemsAsync(sec, ct)).Select(i => i.RatingKey).ToHashSet();
                listings[sec.Key] = keys;
            }
            return keys.Count > 0 && !keys.Contains(r.PlexRatingKey);
        };

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
                if (details.TryGetValue(item.RatingKey, out var d) && d.Unavailable)
                    item.DetailsUnavailable = true;
                else if (d != null)
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
        Func<LibraryItem, Task<bool>>? isGone = null;
        var client = _clientFactory.BuildMediaServerClient();
        if (client != null)
        {
            try { isGone = LiveGoneCheck(client, await client.GetLibrarySectionsAsync(ct), new(), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* no re-add matching this time */ }
        }
        await UpsertItemAsync(sec, item, incrementalMode: false, ct, isGone);
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
        PlexSection sec, PlexMediaItem plexItem, bool incrementalMode, CancellationToken ct,
        Func<LibraryItem, Task<bool>>? isGone = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.LibraryItems
            .Include(i => i.Seasons)
            .FirstOrDefaultAsync(i => i.PlexRatingKey == plexItem.RatingKey, ct);

        // A key Postarr has never seen may be a title the server re-added (library rebuilt, files moved or
        // replaced): carry the old record (chosen artwork, backups, settings) over instead of starting again.
        bool adopted = false, posterLost = false, backgroundLost = false;
        if (existing == null && isGone != null)
        {
            (existing, posterLost, backgroundLost) = await TryAdoptReAddedAsync(db, sec, plexItem, isGone, ct);
            adopted = existing != null;
        }

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
            // The server couldn't give this item's details this time: its dynamic range is unknown, not "SDR".
            if (plexItem.DetailsUnavailable && !string.IsNullOrEmpty(existing.VideoDynamicRange))
                plexItem.VideoDynamicRange = existing.VideoDynamicRange;
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
        // Keep the earliest added date: a title the server re-added (or a replaced file) isn't new content, so it
        // mustn't light up the NEW badge. Season dates can legitimately move forward (a new season), so for a
        // re-added show only dates clearly after the re-add count.
        existing.AddedAtUtc = existing.AddedAtUtc.HasValue && plexItem.AddedAtUtc.HasValue
            ? (plexItem.AddedAtUtc < existing.AddedAtUtc ? plexItem.AddedAtUtc : existing.AddedAtUtc)
            : plexItem.AddedAtUtc ?? existing.AddedAtUtc;
        if (plexItem.LatestSeasonAddedUtc.HasValue
            && !(existing.ReAddedUtc.HasValue && existing.LatestSeasonAddedUtc.HasValue
                 && plexItem.LatestSeasonAddedUtc <= existing.ReAddedUtc.Value.AddDays(1)))
            existing.LatestSeasonAddedUtc = plexItem.LatestSeasonAddedUtc;
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
        if (incrementalMode && !isNew && !adopted && !isChanged && !seasonsChanged && !string.IsNullOrEmpty(existing.CurrentPosterUrl))
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
        var reKeyedSeasons   = new List<SeasonItem>();
        var listedSeasonKeys = plexItem.Seasons.Select(p => p.RatingKey).ToHashSet();
        foreach (var ps in plexItem.Seasons)
        {
            var s = existing.Seasons.FirstOrDefault(x => x.PlexRatingKey == ps.RatingKey);
            // Same season under a new key (the show was re-added): keep its chosen poster and backup.
            var moved = s == null
                ? existing.Seasons.FirstOrDefault(x => x.SeasonNumber == ps.SeasonNumber && !listedSeasonKeys.Contains(x.PlexRatingKey))
                : null;
            if (moved != null)
            {
                _backup.MoveItemBackup(moved.PlexRatingKey, ps.RatingKey);
                moved.PlexRatingKey = ps.RatingKey;
                moved.Title = ps.Title; moved.EpisodeCount = ps.LeafCount;
                if (!IsReapplyable(moved.CurrentPosterUrl, moved.CurrentPosterSource))
                { moved.CurrentPosterUrl = null; moved.CurrentPosterSource = null; }
                else reKeyedSeasons.Add(moved);
                continue;
            }
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

        if (isNew)
        {
            await MaybeAutoSelectPosterAsync(existing.Id, "New item", ct);
            await MaybeAutoSelectBackgroundAsync(existing.Id, "New item", ct);
        }
        else if (isChanged)
            await OnQualityChangedAsync(existing, ct);

        if (adopted) await ReapplyCarriedOverAsync(existing, posterLost, backgroundLost, ct);
        if (reKeyedSeasons.Count > 0 && _settings.Get().AutoApplyOnScan)
            foreach (var rs in reKeyedSeasons)
            {
                try { await _applyService.ApplyCurrentSeasonPosterAsync(rs.Id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Apply All will retry */ }
            }

        // Auto-select season posters for new seasons or shows
        if (anyNewSeason || isNew)
            await MaybeAutoSelectSeasonPostersAsync(existing.Id, ct);

        if (newBadgeStale) await ReapplyForNewBadgeAsync(existing.Id, ct);
        return (isNew, isChanged, false);
    }

    // ── Re-added titles ────────────────────────────────────────────────────────────────────────────────
    // When a server re-adds a title under a new key (a library deleted and re-created, files moved, a file
    // replaced), the old record would otherwise be pruned, losing the chosen poster, background, season posters
    // and the backup of the original artwork, and the title would start again as "new". Instead, find the
    // record for the same title (same TMDB / TVDB / IMDb id and type) whose own key the server no longer has,
    // and move it to the new key.

    private async Task<(LibraryItem? Item, bool PosterLost, bool BackgroundLost)> TryAdoptReAddedAsync(
        PostarrDbContext db, PlexSection sec, PlexMediaItem plexItem, Func<LibraryItem, Task<bool>> isGone, CancellationToken ct)
    {
        string? tmdb = NullIfEmpty(plexItem.TmdbId), tvdb = NullIfEmpty(plexItem.TvdbId), imdb = NullIfEmpty(plexItem.ImdbId);
        if (tmdb == null && tvdb == null && imdb == null) return (null, false, false);
        try
        {
            var server = _clientFactory.MediaServerName;
            var candidates = await db.LibraryItems.Include(i => i.Seasons)
                .Where(i => i.ServerType == server && i.MediaType == plexItem.MediaType && i.PlexRatingKey != plexItem.RatingKey
                            && ((tmdb != null && i.TmdbId == tmdb) || (tvdb != null && i.TvdbId == tvdb) || (imdb != null && i.ImdbId == imdb)))
                .ToListAsync(ct);
            var gone = new List<LibraryItem>();
            foreach (var c in candidates) if (await isGone(c)) gone.Add(c);
            if (gone.Count == 0) return (null, false, false);

            // Several (e.g. a 4K and an HD library both rebuilt)? Only a same-library match is unambiguous.
            LibraryItem? pick = gone.Count == 1 ? gone[0] : null;
            if (pick == null)
            {
                var same = gone.Where(g => g.PlexLibrarySectionId == sec.Key).ToList();
                if (same.Count == 1) pick = same[0];
            }
            if (pick == null) return (null, false, false);

            var oldKey = pick.PlexRatingKey;
            _backup.MoveItemBackup(oldKey, plexItem.RatingKey);
            pick.PlexRatingKey        = plexItem.RatingKey;
            pick.PlexLibrarySectionId = sec.Key;
            pick.ReAddedUtc           = DateTime.UtcNow;
            pick.NewBadgeOnPoster     = false;
            // The server's copy has none of our artwork any more. Picks of the old server item's own images
            // (or uploads) went with it; everything else is pushed again.
            pick.PosterAppliedToPlex = false;
            bool posterLost = !string.IsNullOrEmpty(pick.CurrentPosterUrl) && !IsReapplyable(pick.CurrentPosterUrl, pick.CurrentPosterSource);
            if (posterLost) { pick.CurrentPosterUrl = null; pick.CurrentPosterSource = null; }
            pick.BackgroundAppliedToPlex = false;
            bool backgroundLost = !string.IsNullOrEmpty(pick.CurrentBackgroundUrl) && !IsReapplyable(pick.CurrentBackgroundUrl, pick.CurrentBackgroundSource);
            if (backgroundLost) { pick.CurrentBackgroundUrl = null; pick.CurrentBackgroundSource = null; }

            await _log.LogAsync($"Re-added in {server}: '{pick.Title}' (was {oldKey}, now {plexItem.RatingKey}) - kept its artwork choices and backups.");
            return (pick, posterLost, backgroundLost);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _log.LogAsync($"Couldn't check whether '{plexItem.Title}' was re-added: {ex.Message}", "Warning");
            return (null, false, false);
        }
    }

    // Artwork Postarr can push again from its source. A pick of the server's own image (or an uploaded file) can't
    // be: it lived on the old server item.
    private static bool IsReapplyable(string? url, string? source) =>
        !string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        && source != nameof(PosterSource.Plex) && source != nameof(PosterSource.Local);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private async Task ReapplyCarriedOverAsync(LibraryItem item, bool posterLost, bool backgroundLost, CancellationToken ct)
    {
        // A choice that couldn't come across gets the usual automatic pick; artwork that was never chosen stays unchosen.
        if (posterLost)     await MaybeAutoSelectPosterAsync(item.Id, "Re-added", ct);
        if (backgroundLost) await MaybeAutoSelectBackgroundAsync(item.Id, "Re-added", ct);
        if (!_settings.Get().AutoApplyOnScan) return;   // otherwise it shows as "not applied" for Apply All
        try { if (!string.IsNullOrEmpty(item.CurrentPosterUrl))     await _applyService.ApplyCurrentPosterAsync(item.Id, force: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        try { if (!string.IsNullOrEmpty(item.CurrentBackgroundUrl)) await _applyService.ApplyCurrentBackgroundAsync(item.Id, force: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    // A new file / quality only changes the badges, so the chosen poster stays: it's re-rendered and re-pushed
    // (right away with auto-apply, otherwise via Apply All). Only an item with no poster yet gets an automatic pick.
    private async Task OnQualityChangedAsync(LibraryItem item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.CurrentPosterUrl))
        {
            await MaybeAutoSelectPosterAsync(item.Id, "Quality changed", ct);
            return;
        }
        await _log.LogAsync($"Quality changed: {item.Title} ({item.VideoResolution} {item.VideoDynamicRange}) - keeping its poster, updating badges.");
        using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var row = await db.LibraryItems.FindAsync(new object[] { item.Id }, ct);
            if (row != null) { row.PosterAppliedToPlex = false; await db.SaveChangesAsync(ct); }
        }
        if (!_settings.Get().AutoApplyOnScan) return;
        try { await _applyService.ApplyCurrentPosterAsync(item.Id, force: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* stays pending; Apply All will retry */ }
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
