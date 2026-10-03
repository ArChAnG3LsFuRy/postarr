using Postarr.Data;
using Postarr.Hubs;
using Postarr.MediaServers;
using Postarr.Models;
using Postarr.Overlays;
using Postarr.Plex;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Services;

/// <summary>
/// Handles the two-stage poster workflow:
///   1. Propose  — scan finds the best poster, stores URL in DB, shows on card (no Plex push yet)
///   2. Apply    — user clicks Apply, image is downloaded, overlays rendered, pushed to Plex
///
/// Change — user picks a different poster from the picker (proposes the new URL)
/// Dismiss — user clears the proposed poster without applying
/// </summary>
public class PosterApplyService
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly MetadataClientFactory  _clientFactory;
    private readonly SettingsRepository     _settings;
    private readonly OverlayRenderer        _overlay;
    private readonly ActivityLogger         _log;
    private readonly BackupService          _backup;
    private readonly IScanNotifier          _notifier;
    private readonly IHttpClientFactory     _http;

    public PosterApplyService(
        IDbContextFactory<PostarrDbContext> dbFactory, MetadataClientFactory clientFactory,
        SettingsRepository settings, OverlayRenderer overlay, ActivityLogger log,
        BackupService backup, IScanNotifier notifier, IHttpClientFactory http)
    {
        _dbFactory = dbFactory; _clientFactory = clientFactory;
        _settings  = settings;  _overlay       = overlay;
        _log = log; _backup = backup; _notifier = notifier; _http = http;
    }

    // ── Propose (called by scan — stores URL, no Plex push) ──────────────────

    public async Task ProposePosterAsync(int libraryItemId, string imageUrl, PosterSource source, string reason, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct);
        if (item == null) return;
        item.CurrentPosterUrl    = imageUrl;
        item.CurrentPosterSource = source.ToString();
        item.PosterAppliedToPlex = false;
        item.PosterDismissed     = false;   // picking a poster un-dismisses it
        await db.SaveChangesAsync(ct);
        await _notifier.PosterAppliedAsync(item.Title, source.ToString(), queued: false);
    }

    public async Task ProposeSeasonPosterAsync(int seasonId, string imageUrl, PosterSource source, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var season = await db.Seasons.FindAsync(new object[] { seasonId }, ct);
        if (season == null) return;
        season.CurrentPosterUrl    = imageUrl;
        season.CurrentPosterSource = source.ToString();
        await db.SaveChangesAsync(ct);
    }

    public async Task ProposeBackgroundAsync(int libraryItemId, string imageUrl, PosterSource source, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct);
        if (item == null) return;
        item.CurrentBackgroundUrl     = imageUrl;
        item.CurrentBackgroundSource  = source.ToString();
        item.BackgroundAppliedToPlex  = false;
        item.BackgroundDismissed      = false;   // picking a background un-dismisses it
        await db.SaveChangesAsync(ct);
        await _notifier.BackgroundAppliedAsync(item.Title, source.ToString(), queued: false);
    }

    // ── Apply (called by user — downloads, overlays, pushes to Plex) ─────────

    public async Task<string> ApplyPosterAsync(int libraryItemId, string? imageUrl, PosterSource source, string reason, CancellationToken ct = default, byte[]? bytes = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct)
            ?? throw new InvalidOperationException("Item not found.");

        // A Plex-sourced poster is already in Plex (e.g. the user's own upload) — just select it,
        // as-is: no download, no overlay badges, and Plex keeps its other posters.
        if (source == PosterSource.Plex && imageUrl != null)
        {
            await SelectPlexPosterAsync(item.PlexRatingKey, imageUrl, ct);
        }
        else
        {
            await PushPosterAsync(item, imageUrl, ct, bytes: bytes);
        }
        item.CurrentPosterUrl    = imageUrl ?? await ResolveUploadedArtUrlAsync(item.PlexRatingKey, false, ct) ?? "custom";
        item.CurrentPosterSource = source.ToString();
        item.PosterAppliedToPlex = true;
        item.PosterDismissed     = false;   // picking a poster un-dismisses it
        item.LastPosterAppliedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await _log.LogAsync($"Poster applied to {_clientFactory.MediaServerName}: {item.Title} ({source})");
        await _notifier.PosterAppliedAsync(item.Title, source.ToString(), queued: false);
        return "applied";
    }

    /// <summary>
    /// After a custom upload there's no public URL for the image, and storing the literal "custom"
    /// made the card render as "Load failed". Ask Plex which poster/art it now has selected and
    /// return its proxy URL so the card can actually display the uploaded image.
    /// </summary>
    public async Task<string?> ResolveUploadedArtUrlAsync(string ratingKey, bool isBackground, CancellationToken ct = default)
    {
        try
        {
            var plex = _clientFactory.BuildMediaServerClient();
            if (plex == null) return null;
            var list = isBackground
                ? await plex.GetAvailableArtsAsync(ratingKey, ct)
                : await plex.GetAvailablePostersAsync(ratingKey, ct);
            var sel = list.FirstOrDefault(p => p.Selected) ?? list.FirstOrDefault();
            return sel == null ? null : Postarr.Plex.PlexClient.ImageProxyUrl(sel.Key);
        }
        catch { return null; }
    }

    // Selects an existing Plex poster by reference (proxy URL or raw key), no re-upload/overlay.
    private async Task SelectPlexPosterAsync(string ratingKey, string imageUrl, CancellationToken ct)
    {
        var plex = _clientFactory.BuildMediaServerClient() ?? throw new InvalidOperationException($"{_clientFactory.MediaServerName} not configured.");
        var key  = Postarr.Plex.PlexClient.PosterKeyFromRef(imageUrl);
        if (key != null) await plex.SelectPosterAsync(ratingKey, Postarr.Plex.PlexClient.PosterSelectValue(key), ct);
    }

    // Selects an existing Plex background/art by reference (proxy URL or raw key).
    private static async Task SelectPlexArtAsync(IMediaServerClient plex, string ratingKey, string? imageUrl, CancellationToken ct)
    {
        var key = Postarr.Plex.PlexClient.PosterKeyFromRef(imageUrl);
        if (key != null) await plex.SelectArtAsync(ratingKey, Postarr.Plex.PlexClient.PosterSelectValue(key), ct);
    }

    public async Task<string> ApplySeasonPosterAsync(int seasonId, string? imageUrl, PosterSource source, string reason, CancellationToken ct = default, byte[]? bytes = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var season = await db.Seasons.FindAsync(new object[] { seasonId }, ct)
            ?? throw new InvalidOperationException("Season not found.");
        var parent = await db.LibraryItems.FindAsync(new object[] { season.LibraryItemId }, ct);
        var title  = $"{parent?.Title} – {season.Title}";

        // Season posters only get overlays when the season-overlay scope is enabled.
        // When off, push the artwork clean (still backed up first inside PushPosterAsync).
        var applyOverlays = _settings.Get().Overlays.SeasonPosterOverlaysEnabled;
        if (parent != null) await PushPosterAsync(parent, imageUrl, ct, season.PlexRatingKey, bytes: bytes, applyOverlays: applyOverlays);
        season.CurrentPosterUrl    = imageUrl ?? await ResolveUploadedArtUrlAsync(season.PlexRatingKey, false, ct) ?? "custom";
        season.CurrentPosterSource = source.ToString();
        season.LastPosterAppliedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await _log.LogAsync($"Season poster applied: {title}");
        return "applied";
    }

    /// <summary>
    /// Re-applies a season's current poster — badges included when the season-overlay setting is on.
    /// Used by Apply All so season posters get badges (and pick up badge-setting changes) like shows do.
    /// A custom upload has no source URL to re-render from, so it's left exactly as it is.
    /// </summary>
    public async Task<string> ApplyCurrentSeasonPosterAsync(int seasonId, CancellationToken ct = default)
    {
        string? url; PosterSource source;
        using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var season = await db.Seasons.FindAsync(new object[] { seasonId }, ct)
                ?? throw new InvalidOperationException("Season not found.");
            url    = season.CurrentPosterUrl;
            source = Enum.TryParse<PosterSource>(season.CurrentPosterSource, out var s) ? s : PosterSource.Tmdb;
        }
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "unchanged";
        return await ApplySeasonPosterAsync(seasonId, url, source, "Apply All", ct);
    }

    // ── Apply current proposed poster (user clicks Apply on card) ─────────────

    public async Task<string> ApplyCurrentPosterAsync(int libraryItemId, bool force = false, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct)
            ?? throw new InvalidOperationException("Item not found.");

        if (string.IsNullOrEmpty(item.CurrentPosterUrl))
            throw new InvalidOperationException("No poster proposed for this item.");

        // Already on Plex and still current (poster unchanged, overlay settings unchanged since it
        // was applied)? Skip — re-uploading the identical poster just piles up duplicates in Plex.
        // `force` (the "Apply All" button) overrides this so the user can always re-push everything.
        var overlaysChanged = _settings.Get().OverlaysLastChangedUtc ?? DateTime.MinValue;
        if (!force && item.PosterAppliedToPlex && item.LastPosterAppliedUtc.HasValue
            && item.LastPosterAppliedUtc.Value >= overlaysChanged)
            return "unchanged";

        var source = Enum.TryParse<PosterSource>(item.CurrentPosterSource, out var s) ? s : PosterSource.Tmdb;
        if (source == PosterSource.Plex)
            await SelectPlexPosterAsync(item.PlexRatingKey, item.CurrentPosterUrl, ct);
        else
            await PushPosterAsync(item, item.CurrentPosterUrl, ct);
        item.PosterAppliedToPlex  = true;
        item.LastPosterAppliedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await _log.LogAsync($"Poster pushed to {_clientFactory.MediaServerName}: {item.Title}");
        return "applied";
    }

    public async Task<string> ApplyCurrentBackgroundAsync(int libraryItemId, bool force = false, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct)
            ?? throw new InvalidOperationException("Item not found.");

        if (string.IsNullOrEmpty(item.CurrentBackgroundUrl))
            throw new InvalidOperationException("No background proposed for this item.");

        // Backgrounds carry no overlays, so an already-applied one never needs re-uploading —
        // skip to avoid piling up duplicate art in Plex. `force` (Apply All) overrides this.
        if (!force && item.BackgroundAppliedToPlex) return "unchanged";

        var plex  = _clientFactory.BuildMediaServerClient() ?? throw new InvalidOperationException($"{_clientFactory.MediaServerName} not configured.");
        var source = Enum.TryParse<PosterSource>(item.CurrentBackgroundSource, out var s) ? s : PosterSource.Tmdb;
        if (source == PosterSource.Plex)
        {
            await SelectPlexArtAsync(plex, item.PlexRatingKey, item.CurrentBackgroundUrl, ct);
        }
        else
        {
            var itemDir = _backup.GetItemBackupDir(item.PlexRatingKey);
            await _backup.BackupBackgroundIfNeededAsync(item.CurrentBackgroundUrl, itemDir, ct);
            ImageSources.EnsureAllowed(item.CurrentBackgroundUrl, _settings.Get());
            var bytes = await _http.CreateClient("imagedownload").GetByteArrayAsync(item.CurrentBackgroundUrl, ct);
            await plex.UploadBackgroundAsync(item.PlexRatingKey, bytes, "image/jpeg", ct);
        }
        item.BackgroundAppliedToPlex   = true;
        item.LastBackgroundAppliedUtc  = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await _log.LogAsync($"Background pushed to {_clientFactory.MediaServerName}: {item.Title}");
        return "applied";
    }

    // ── Dismiss (clears proposed poster without applying) ─────────────────────

    public async Task DismissPosterAsync(int libraryItemId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct);
        if (item == null) return;
        item.CurrentPosterUrl    = null;
        item.CurrentPosterSource = null;
        item.PosterAppliedToPlex = false;
        item.PosterDismissed     = true;   // hides it from the grid and stops scans re-proposing
        await db.SaveChangesAsync(ct);
        await _log.LogAsync($"Poster proposal dismissed: {item.Title}");
    }

    public async Task DismissBackgroundAsync(int libraryItemId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct);
        if (item == null) return;
        item.CurrentBackgroundUrl    = null;
        item.CurrentBackgroundSource = null;
        item.BackgroundAppliedToPlex = false;
        item.BackgroundDismissed     = true;
        await db.SaveChangesAsync(ct);
    }

    // ── Background apply ──────────────────────────────────────────────────────

    public async Task<string> ApplyBackgroundAsync(int libraryItemId, string imageUrl, PosterSource source, string reason, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct)
            ?? throw new InvalidOperationException("Item not found.");

        var plex    = _clientFactory.BuildMediaServerClient() ?? throw new InvalidOperationException($"{_clientFactory.MediaServerName} not configured.");

        // A Plex-sourced background already lives in Plex — just select it, no download/re-upload.
        if (source == PosterSource.Plex)
        {
            await SelectPlexArtAsync(plex, item.PlexRatingKey, imageUrl, ct);
        }
        else
        {
            var itemDir = _backup.GetItemBackupDir(item.PlexRatingKey);
            await _backup.BackupBackgroundIfNeededAsync(item.CurrentBackgroundUrl, itemDir, ct);
            ImageSources.EnsureAllowed(imageUrl, _settings.Get());
            var bytes = await _http.CreateClient("imagedownload").GetByteArrayAsync(imageUrl, ct);
            await plex.UploadBackgroundAsync(item.PlexRatingKey, bytes, "image/jpeg", ct);
        }

        item.CurrentBackgroundUrl     = imageUrl;
        item.CurrentBackgroundSource  = source.ToString();
        item.BackgroundAppliedToPlex  = true;
        item.BackgroundDismissed      = false;   // picking a background un-dismisses it
        item.LastBackgroundAppliedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await _log.LogAsync($"Background applied: {item.Title} ({source})");
        await _notifier.BackgroundAppliedAsync(item.Title, source.ToString(), queued: false);
        return "applied";
    }

    // ── Restore backup ────────────────────────────────────────────────────────

    public async Task<bool> RestorePosterAsync(int libraryItemId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct);
        if (item == null) return false;
        var plex = _clientFactory.BuildMediaServerClient() ?? throw new InvalidOperationException($"{_clientFactory.MediaServerName} not configured.");
        var ok = await _backup.RestorePosterAsync(item.PlexRatingKey, plex, ct);
        if (ok) { item.CurrentPosterUrl = null; item.CurrentPosterSource = null; item.PosterAppliedToPlex = false; await db.SaveChangesAsync(ct); }
        await _log.LogAsync(ok ? $"Poster restored: {item.Title}" : $"No backup found: {item.Title}", ok ? "Info" : "Warning");
        return ok;
    }

    public async Task<bool> RestoreBackgroundAsync(int libraryItemId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.LibraryItems.FindAsync(new object[] { libraryItemId }, ct);
        if (item == null) return false;
        var plex = _clientFactory.BuildMediaServerClient() ?? throw new InvalidOperationException($"{_clientFactory.MediaServerName} not configured.");
        var ok = await _backup.RestoreBackgroundAsync(item.PlexRatingKey, plex, ct);
        if (ok) { item.CurrentBackgroundUrl = null; item.CurrentBackgroundSource = null; item.BackgroundAppliedToPlex = false; await db.SaveChangesAsync(ct); }
        return ok;
    }

    // ── Internal push ─────────────────────────────────────────────────────────

    private async Task PushPosterAsync(LibraryItem context, string? imageUrl, CancellationToken ct, string? overrideKey = null, byte[]? bytes = null, bool applyOverlays = true)
    {
        var plex    = _clientFactory.BuildMediaServerClient() ?? throw new InvalidOperationException($"{_clientFactory.MediaServerName} not configured.");
        var key     = overrideKey ?? context.PlexRatingKey;
        var itemDir = _backup.GetItemBackupDir(key);
        await _backup.BackupPosterIfNeededAsync(key, context.CurrentPosterUrl, itemDir, ct);

        // Use provided bytes (custom upload) or download from URL
        if (bytes == null) ImageSources.EnsureAllowed(imageUrl, _settings.Get());
        var rawBytes = bytes ?? await _http.CreateClient("imagedownload").GetByteArrayAsync(imageUrl!, ct);
        var isSeason = overrideKey != null;
        var final    = applyOverlays
            ? _overlay.ApplyOverlays(rawBytes, context, _settings.Get().Overlays, isSeason)  // returns JPEG bytes
            : rawBytes;
        // Remember whether this poster carries the NEW badge, so the scan can tell when it has expired
        // (or a new season arrived) and the poster needs re-applying. Only the item's own poster counts.
        if (!isSeason)
            context.NewBadgeOnPoster = applyOverlays
                && Overlays.OverlayRenderer.NewBadgeLabel(context, _settings.Get().Overlays, DateTime.UtcNow) != null;
        // ApplyOverlays emits JPEG; when applyOverlays is false these are the raw downloaded bytes
        // (JPEG for every source we pull from). image/jpeg is correct for both.
        await plex.UploadPosterAsync(key, final, "image/jpeg", ct);
    }
}
