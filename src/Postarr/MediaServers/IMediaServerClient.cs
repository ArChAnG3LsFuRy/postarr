using Postarr.Plex;

namespace Postarr.MediaServers;

/// <summary>
/// Everything Postarr needs from a media server: read the library, read/write artwork, and report
/// quality info for badges. <see cref="PlexClient"/> is the original implementation; other servers
/// (Jellyfin, Emby) plug in behind this same contract so the scan/apply/backup code doesn't care
/// which one is configured.
///
/// Item/season/collection identifiers are the server's own keys (Plex rating keys today). The data
/// transfer types still carry their original Plex names; they are plain data and server-neutral.
/// </summary>
public interface IMediaServerClient
{
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    // ── Library ──────────────────────────────────────────────────────────────
    Task<List<PlexSection>>        GetLibrarySectionsAsync(CancellationToken ct = default);
    Task<List<PlexMediaItem>>      GetSectionItemsAsync(PlexSection section, CancellationToken ct = default);
    Task<List<PlexCollectionItem>> GetCollectionsAsync(PlexSection section, CancellationToken ct = default);

    // ── Quality (drives resolution / HDR / audio badges) ────────────────────
    Task<(string? Resolution, string? DynamicRange, string? AudioCodec)> GetShowAggregateQualityAsync(string showKey, CancellationToken ct = default);
    /// <summary>
    /// Item key → stream details not in the library listing: dynamic range ("SDR", "HDR", "HDR10+", "DV",
    /// "DV-HDR") and audio/subtitle language counts. Servers whose listing already carries these return
    /// an empty map (nothing to refine).
    /// </summary>
    Task<Dictionary<string, StreamDetails>> GetStreamDetailsAsync(IEnumerable<string> itemKeys, CancellationToken ct = default);

    // ── Write artwork ────────────────────────────────────────────────────────
    Task UploadPosterAsync(string itemKey, byte[] bytes, string contentType, CancellationToken ct = default);
    Task UploadBackgroundAsync(string itemKey, byte[] bytes, string contentType, CancellationToken ct = default);

    // ── Artwork already held by the server (e.g. the user's own uploads) ────
    Task<List<PlexPoster>> GetAvailablePostersAsync(string itemKey, CancellationToken ct = default);
    Task<List<PlexPoster>> GetAvailableArtsAsync(string itemKey, CancellationToken ct = default);
    Task SelectPosterAsync(string itemKey, string selectValue, CancellationToken ct = default);
    Task SelectArtAsync(string itemKey, string selectValue, CancellationToken ct = default);

    // ── Read artwork ─────────────────────────────────────────────────────────
    /// <summary>Fetches a server-hosted image for the browser display proxy.</summary>
    Task<(byte[] Bytes, string ContentType)?> FetchImageAsync(string imagePath, CancellationToken ct = default);
    Task<byte[]?> DownloadCurrentPosterAsync(string? thumbPath, CancellationToken ct = default);
    Task<byte[]?> DownloadCurrentBackgroundAsync(string? artPath, CancellationToken ct = default);

    // ── Collections Postarr builds (auto-collections) ───────────────────────
    /// <summary>Creates a regular (non-smart) collection in the section holding the given items; returns its id.</summary>
    Task<string> CreateCollectionAsync(PlexSection section, string title, IReadOnlyList<string> itemKeys, CancellationToken ct = default);
    /// <summary>Item keys currently in the collection, or null if the collection no longer exists.</summary>
    Task<List<string>?> GetCollectionItemKeysAsync(string collectionId, CancellationToken ct = default);
    Task AddToCollectionAsync(string collectionId, IReadOnlyList<string> itemKeys, CancellationToken ct = default);
    Task RemoveFromCollectionAsync(string collectionId, IReadOnlyList<string> itemKeys, CancellationToken ct = default);
    Task RenameCollectionAsync(PlexSection section, string collectionId, string title, CancellationToken ct = default);
    /// <summary>
    /// Sets how the collection is ordered. mode "list" = exactly <paramref name="orderedKeys"/> (a custom order),
    /// "release" = by release date, "default" = the server's own default. Returns false when the server can't
    /// do that (nothing is changed then) — Jellyfin/Emby have only release-date and A–Z, no custom order.
    /// </summary>
    Task<bool> SetCollectionOrderAsync(PlexSection section, string collectionId, string mode, IReadOnlyList<string> orderedKeys, CancellationToken ct = default);
    /// <summary>Deletes the collection itself (never its titles). Refuses anything that isn't a collection.</summary>
    Task DeleteCollectionAsync(string collectionId, CancellationToken ct = default);
}
