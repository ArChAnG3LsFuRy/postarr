using Postarr.Metadata;
using Postarr.Models;

namespace Postarr.Services;

/// <summary>
/// Pulls the data behind the rating and award badges — IMDb rating, Rotten Tomatoes / audience
/// score, Metacritic, Oscar winner/nominee, Emmy winner — for one item. OMDb is the source (its Awards text is
/// the only awards data among Postarr's providers); MDBList fills RT/audience where OMDb has none
/// (TV shows). Shared by the library scan and <see cref="RatingsRefreshService"/> so both pull the
/// data identically. Scores the media server already holds (Plex) take priority — see
/// <see cref="ApplyServerRatings"/>. Only updates the stored data; nothing is uploaded to Plex.
/// </summary>
public static class RatingsEnricher
{
    // LibraryItem.ServerRatingFields flags: which scores the media server supplied.
    public const int ImdbFromServer = 1, RottenTomatoesFromServer = 2, AudienceFromServer = 4;

    /// <summary>
    /// Takes the scores the media server already holds (Plex's own IMDb / Rotten Tomatoes ratings, read
    /// during the scan) as the primary source: they are there immediately, need no API key and cost no
    /// OMDb quota. A score the server lacks keeps whatever OMDb / MDBList supplied — or will supply.
    /// </summary>
    public static void ApplyServerRatings(LibraryItem item, double? imdb, int? rottenTomatoes, int? audience)
    {
        int flags = 0;
        if (imdb.HasValue)           { item.ImdbRating          = imdb;           flags |= ImdbFromServer; }
        if (rottenTomatoes.HasValue) { item.RottenTomatoesScore = rottenTomatoes; flags |= RottenTomatoesFromServer; }
        if (audience.HasValue)       { item.AudienceScore       = audience;       flags |= AudienceFromServer; }
        item.ServerRatingFields = flags;
    }

    /// <summary>
    /// Returns true if a source answered (the item then counts as checked). When OMDb is unavailable
    /// — daily limit, outage, bad key — the item is left completely untouched, so it keeps its place
    /// at the front of the queue for the next refresh.
    /// </summary>
    public static async Task<bool> EnrichAsync(
        LibraryItem item, OmdbClient? omdb, MdbListClient? mdb, CancellationToken ct)
    {
        bool answered = false;

        if (omdb != null && !string.IsNullOrEmpty(item.ImdbId))
        {
            var r = await omdb.GetRatingsAsync(item.ImdbId, ct);
            if (omdb.LastStatus == OmdbStatus.Unavailable) return false;
            answered = true;
            if (r.HasValue)
            {
                // Scores the media server supplied win; OMDb only fills the rest.
                var fromServer = item.ServerRatingFields;
                if (r.Value.ImdbRating.HasValue && (fromServer & ImdbFromServer) == 0)
                    item.ImdbRating = r.Value.ImdbRating;
                if (r.Value.RottenTomatoes.HasValue && (fromServer & RottenTomatoesFromServer) == 0)
                    item.RottenTomatoesScore = r.Value.RottenTomatoes;
                if (r.Value.Audience.HasValue && (fromServer & AudienceFromServer) == 0)
                    item.AudienceScore = r.Value.Audience;
                if (r.Value.Metacritic.HasValue)     item.MetacriticScore     = r.Value.Metacritic;
                item.IsOscarWinner  = r.Value.OscarWinner;
                item.IsOscarNominee = r.Value.OscarNominee;
                item.IsEmmyWinner   = r.Value.EmmyWinner;
            }
        }

        // Rotten Tomatoes via MDBList — the source Kometa uses (`mdb_tomatoes`). OMDb only has RT for
        // movies, so this is what fills RT (and audience) for TV shows.
        if (mdb != null && (item.RottenTomatoesScore == null || item.AudienceScore == null || item.MetacriticScore == null))
        {
            try
            {
                var rt = await mdb.GetRottenTomatoesAsync(item.ImdbId, item.TmdbId, item.MediaType == MediaType.Movie, ct);
                answered = true;
                if (rt.HasValue)
                {
                    if (item.RottenTomatoesScore == null && rt.Value.RottenTomatoes.HasValue)
                        item.RottenTomatoesScore = rt.Value.RottenTomatoes;
                    if (item.AudienceScore == null && rt.Value.Audience.HasValue)
                        item.AudienceScore = rt.Value.Audience;
                    if (item.MetacriticScore == null && rt.Value.Metacritic.HasValue)
                        item.MetacriticScore = rt.Value.Metacritic;
                }
            }
            catch { /* non-fatal */ }
        }

        if (answered) item.RatingsCheckedUtc = DateTime.UtcNow;
        return answered;
    }

    /// <summary>True when any badge that draws on the per-title OMDb lookup is switched on.</summary>
    public static bool AnyBadgeNeedsRatings(OverlaySettings? o) => o != null &&
        (o.ImdbRatingEnabled || o.RottenTomatoesEnabled || o.AudienceScoreEnabled || o.MetacriticEnabled
         || o.OscarWinnerEnabled || o.OscarNomineeEnabled || o.EmmyWinnerEnabled);

    /// <summary>True when any badge fed only by MDBList's batch lookup is switched on (no OMDb involved).</summary>
    public static bool AnyBadgeNeedsMdbListBatch(OverlaySettings? o) => o != null &&
        (o.LetterboxdEnabled || o.TraktEnabled || o.ImdbTop250Enabled);

    // MDBList list mirroring IMDb's official Top 250 chart (same order as imdb.com/chart/top; checked 2026-09-29
    // against the most-liked alternative, 249/250 identical). Kometa scrapes IMDb for this; MDBList avoids that.
    public const int ImdbTop250ListId = 34725;

    /// <summary>
    /// Letterboxd, Trakt and IMDb Top 250 for every given item via MDBList batch lookups (~1 request per 200
    /// titles, plus 1 for the Top 250 list). Also fills Rotten Tomatoes / audience / Metacritic where nothing
    /// else has supplied them. Returns how many items gained or changed a value.
    /// </summary>
    public static async Task<int> EnrichBatchAsync(IReadOnlyList<LibraryItem> items, MdbListClient mdb, CancellationToken ct)
    {
        int changed = 0;
        foreach (var isMovie in new[] { true, false })
        {
            var group = items.Where(i => (i.MediaType == MediaType.Movie) == isMovie).ToList();
            if (group.Count == 0) continue;
            // IMDb id where known (the reliable match), TMDB id for the rest.
            var byImdb = await mdb.GetBatchRatingsAsync(group.Where(i => !string.IsNullOrEmpty(i.ImdbId)).Select(i => i.ImdbId!), "imdb", isMovie, ct);
            var noImdb = group.Where(i => string.IsNullOrEmpty(i.ImdbId) && !string.IsNullOrEmpty(i.TmdbId)).ToList();
            var byTmdb = noImdb.Count > 0
                ? await mdb.GetBatchRatingsAsync(noImdb.Select(i => i.TmdbId!), "tmdb", isMovie, ct)
                : new Dictionary<string, MdbListClient.BatchRatings>();

            foreach (var item in group)
            {
                MdbListClient.BatchRatings? r = null;
                if (!string.IsNullOrEmpty(item.ImdbId)) byImdb.TryGetValue(item.ImdbId!, out r);
                else if (!string.IsNullOrEmpty(item.TmdbId)) byTmdb.TryGetValue(item.TmdbId!, out r);
                if (r == null) continue;

                var before = (item.LetterboxdRating, item.TraktRating, item.RottenTomatoesScore, item.AudienceScore, item.MetacriticScore);
                if (r.Letterboxd.HasValue) item.LetterboxdRating = r.Letterboxd;
                if (r.Trakt.HasValue)      item.TraktRating      = r.Trakt;
                item.RottenTomatoesScore ??= r.RottenTomatoes;
                item.AudienceScore       ??= r.Audience;
                item.MetacriticScore     ??= r.Metacritic;
                if (before != (item.LetterboxdRating, item.TraktRating, item.RottenTomatoesScore, item.AudienceScore, item.MetacriticScore))
                    changed++;
            }
        }

        // Top 250 membership (movies). A failed or suspiciously short read keeps the previous ranks.
        var ranks = await mdb.GetListRanksAsync(ImdbTop250ListId, ct);
        if (ranks != null && ranks.Count >= 200)
            foreach (var item in items)
            {
                int? rank = item.MediaType == MediaType.Movie && !string.IsNullOrEmpty(item.ImdbId)
                            && ranks.TryGetValue(item.ImdbId!, out var rk) ? rk : null;
                if (rank != item.ImdbTop250Rank) { item.ImdbTop250Rank = rank; changed++; }
            }
        return changed;
    }
}
