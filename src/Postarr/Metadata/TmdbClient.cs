using System.Text.Json;
using Postarr.Models;

namespace Postarr.Metadata;

public class TmdbEnrichedData
{
    public double? ImdbRating          { get; set; }  // from OMDB API
    public double? TmdbRating          { get; set; }  // from TMDB vote_average (0-10)
    public int?    RottenTomatoesScore { get; set; }  // from OMDB API (critics %)
    public int?    AudienceScore       { get; set; }  // from OMDB API (audience %)
    public string? ImdbId              { get; set; }  // IMDB ID for OMDB lookups
    public string? Status              { get; set; }  // "Returning Series", "Ended", etc.
    public int?    EpisodeCount        { get; set; }
    public string? StreamingService    { get; set; }
    public bool    IsPopular           { get; set; }
    public bool    IsTrending          { get; set; }
    public string? ContentLanguage     { get; set; }
    public string? Studio              { get; set; }
    public string? Network             { get; set; }
    public List<string> Genres         { get; set; } = new();
    public string? CollectionId        { get; set; }  // movies: "belongs_to_collection" (the franchise)
    public string? CollectionName      { get; set; }
    public List<string> Countries      { get; set; } = new();   // production countries, by name
    public List<string> Actors         { get; set; } = new();   // top-billed cast
    public List<string> Directors      { get; set; } = new();   // movies: directors; shows: creators
}

public class TmdbClient
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private const string Base  = "https://api.themoviedb.org/3";
    private const string ImgBase = "https://image.tmdb.org/t/p";

    public TmdbClient(HttpClient http, string apiKey)
    { _http = http; _apiKey = apiKey; }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try { return (await _http.GetAsync($"{Base}/configuration?api_key={_apiKey}", ct)).IsSuccessStatusCode; }
        catch { return false; }
    }

    // ── Posters ──────────────────────────────────────────────────────────────

    public Task<List<PosterCandidate>> GetMoviePostersAsync(string id, CancellationToken ct = default) =>
        GetPostersAsync($"{Base}/movie/{id}/images?api_key={_apiKey}", ct);

    public Task<List<PosterCandidate>> GetShowPostersAsync(string id, CancellationToken ct = default) =>
        GetPostersAsync($"{Base}/tv/{id}/images?api_key={_apiKey}", ct);

    public Task<List<PosterCandidate>> GetSeasonPostersAsync(string id, int season, CancellationToken ct = default) =>
        GetPostersAsync($"{Base}/tv/{id}/season/{season}/images?api_key={_apiKey}", ct);

    // TMDB collections (movie franchises like "The Dark Knight Collection") have their
    // own endpoint and image set, separate from individual movie/show images.
    public Task<List<PosterCandidate>> GetCollectionPostersAsync(string collectionId, CancellationToken ct = default) =>
        GetPostersAsync($"{Base}/collection/{collectionId}/images?api_key={_apiKey}", ct);

    /// <summary>
    /// Searches TMDB for a collection by name and returns the first matching TMDB collection ID.
    /// Used to resolve the TMDB ID for Plex collections that don't carry one natively.
    /// </summary>
    public async Task<string?> SearchCollectionAsync(string title, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(title);
            var resp    = await _http.GetAsync($"{Base}/search/collection?api_key={_apiKey}&query={encoded}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return null;
            // Return the first result's ID — the title search is usually accurate for
            // franchise collections like "The Dark Knight Collection"
            var first = results[0];
            return first.TryGetProperty("id", out var id) ? id.GetInt32().ToString() : null;
        }
        catch { return null; }
    }

    // ── Backgrounds ─────────────────────────────────────────────────────────

    public Task<List<BackgroundCandidate>> GetMovieBackgroundsAsync(string id, CancellationToken ct = default) =>
        GetBackgroundsAsync($"{Base}/movie/{id}/images?api_key={_apiKey}", ct);

    public Task<List<BackgroundCandidate>> GetShowBackgroundsAsync(string id, CancellationToken ct = default) =>
        GetBackgroundsAsync($"{Base}/tv/{id}/images?api_key={_apiKey}", ct);

    // ── Enriched metadata ────────────────────────────────────────────────────

    public async Task<TmdbEnrichedData?> GetMovieEnrichedAsync(string tmdbId, CancellationToken ct = default)
    {
        try
        {
            // append_to_response pulls watch providers + external IDs + ratings in one call
            var resp = await _http.GetAsync(
                $"{Base}/movie/{tmdbId}?api_key={_apiKey}&append_to_response=watch/providers,external_ids,release_dates,credits", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;

            // TMDB vote_average (0-10 scale)
            double? tmdbRating = root.TryGetProperty("vote_average", out var va) && va.GetDouble() > 0
                ? Math.Round(va.GetDouble(), 1) : null;

            // External IMDb ID for OMDB lookups
            string? imdbId = null;
            if (root.TryGetProperty("external_ids", out var extIds))
                imdbId = extIds.TryGetProperty("imdb_id", out var iid) ? iid.GetString() : null;

            string? collectionId = null, collectionName = null;
            if (root.TryGetProperty("belongs_to_collection", out var btc) && btc.ValueKind == JsonValueKind.Object)
            {
                if (btc.TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.Number) collectionId = cid.GetRawText();
                collectionName = btc.TryGetProperty("name", out var cn) ? cn.GetString() : null;
            }

            return new TmdbEnrichedData
            {
                Status           = root.TryGetProperty("status", out var st)   ? st.GetString()     : null,
                ContentLanguage  = root.TryGetProperty("original_language", out var lang) ? lang.GetString() : null,
                IsPopular        = root.TryGetProperty("popularity", out var pop) && pop.GetDouble() > 50,
                Studio           = GetFirstProductionCompany(root),
                StreamingService = GetWatchProvider(root),
                TmdbRating       = tmdbRating,
                ImdbId           = imdbId,
                Genres           = GetGenres(root),
                CollectionId     = collectionId,
                CollectionName   = collectionName,
                Countries        = Names(root, "production_countries"),
                Actors           = TopCast(root),
                Directors        = root.TryGetProperty("credits", out var cr) && cr.TryGetProperty("crew", out var crew)
                                   ? crew.EnumerateArray().Where(p => p.TryGetProperty("job", out var j) && j.GetString() == "Director")
                                         .Select(p => p.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().Distinct().Take(3).ToList()
                                   : new(),
            };
        }
        catch { return null; }
    }

    public async Task<TmdbEnrichedData?> GetShowEnrichedAsync(string tmdbId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync(
                $"{Base}/tv/{tmdbId}?api_key={_apiKey}&append_to_response=watch/providers,external_ids,credits", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;

            var episodeCount = 0;
            if (root.TryGetProperty("seasons", out var seasons))
                foreach (var season in seasons.EnumerateArray())
                    if (season.TryGetProperty("episode_count", out var ec))
                        episodeCount += ec.GetInt32();

            double? tmdbRating = root.TryGetProperty("vote_average", out var va) && va.GetDouble() > 0
                ? Math.Round(va.GetDouble(), 1) : null;

            string? imdbId = null;
            if (root.TryGetProperty("external_ids", out var extIds))
                imdbId = extIds.TryGetProperty("imdb_id", out var iid) ? iid.GetString() : null;

            return new TmdbEnrichedData
            {
                Status           = root.TryGetProperty("status", out var st)   ? st.GetString()     : null,
                ContentLanguage  = root.TryGetProperty("original_language", out var lang) ? lang.GetString() : null,
                EpisodeCount     = episodeCount > 0 ? episodeCount : null,
                Network          = GetFirstNetwork(root),
                IsPopular        = root.TryGetProperty("popularity", out var pop) && pop.GetDouble() > 30,
                StreamingService = GetWatchProvider(root),
                TmdbRating       = tmdbRating,
                ImdbId           = imdbId,
                Genres           = GetGenres(root),
                Countries        = Names(root, "production_countries"),
                Actors           = TopCast(root),
                Directors        = Names(root, "created_by").Take(3).ToList(),
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// TMDB ids on one of TMDB's charts, in chart order: "trending" (this week), "popular" or "top_rated".
    /// 20 per page. Null if TMDB couldn't be read, so a sync can leave the collection as it was.
    /// </summary>
    public async Task<List<string>?> GetChartIdsAsync(string chart, bool isMovie, int pages, CancellationToken ct = default)
    {
        var type = isMovie ? "movie" : "tv";
        var path = chart == "trending" ? $"/trending/{type}/week" : $"/{type}/{chart}";
        var ids  = new List<string>();
        try
        {
            for (int page = 1; page <= pages; page++)
            {
                var resp = await _http.GetAsync($"{Base}{path}?api_key={_apiKey}&page={page}", ct);
                if (!resp.IsSuccessStatusCode) return page == 1 ? null : ids;
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("results", out var results)) break;
                foreach (var r in results.EnumerateArray())
                    if (r.TryGetProperty("id", out var id)) ids.Add(id.GetRawText());
                if (doc.RootElement.TryGetProperty("total_pages", out var tp) && tp.GetInt32() <= page) break;
            }
            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ids.Count > 0 ? ids : null; }
    }

    public async Task<bool> IsTrendingAsync(string tmdbId, string mediaType, CancellationToken ct = default)
    {
        try
        {
            var type = mediaType == "movie" ? "movie" : "tv";
            var resp = await _http.GetAsync($"{Base}/trending/{type}/week?api_key={_apiKey}", ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return false;
            foreach (var r in results.EnumerateArray())
                if (r.TryGetProperty("id", out var id) && id.GetInt32().ToString() == tmdbId) return true;
            return false;
        }
        catch { return false; }
    }

    public async Task<string?> FindTmdbIdByTvdbIdAsync(string tvdbId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{Base}/find/{tvdbId}?api_key={_apiKey}&external_source=tvdb_id", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("tv_results", out var tv) && tv.GetArrayLength() > 0)
                return tv[0].GetProperty("id").GetInt32().ToString();
            return null;
        }
        catch { return null; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? GetFirstProductionCompany(JsonElement root)
    {
        if (root.TryGetProperty("production_companies", out var cos) && cos.GetArrayLength() > 0)
            return cos[0].TryGetProperty("name", out var n) ? n.GetString() : null;
        return null;
    }

    // "name" of each object in an array property (production_countries, created_by…).
    private static List<string> Names(JsonElement root, string prop)
    {
        var list = new List<string>();
        if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
                if (x.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) list.Add(name);
        return list;
    }

    // The first five billed cast members (credits.cast is in billing order).
    private static List<string> TopCast(JsonElement root) =>
        root.TryGetProperty("credits", out var cr) && cr.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array
            ? cast.EnumerateArray().Take(5).Select(p => p.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().ToList()
            : new();

    private static List<string> GetGenres(JsonElement root)
    {
        var list = new List<string>();
        if (root.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
            foreach (var g in gs.EnumerateArray())
                if (g.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) list.Add(name);
        return list;
    }

    private static string? GetFirstNetwork(JsonElement root)
    {
        if (root.TryGetProperty("networks", out var nets) && nets.GetArrayLength() > 0)
            return nets[0].TryGetProperty("name", out var n) ? n.GetString() : null;
        return null;
    }

    private static string? GetWatchProvider(JsonElement root)
    {
        // TMDB watch/providers structure: results -> US -> flatrate[0].provider_name
        try
        {
            if (root.TryGetProperty("watch/providers", out var wp) &&
                wp.TryGetProperty("results", out var results) &&
                results.TryGetProperty("US", out var us) &&
                us.TryGetProperty("flatrate", out var flat) &&
                flat.GetArrayLength() > 0)
                return flat[0].GetProperty("provider_name").GetString();
        }
        catch { }
        return null;
    }

    private async Task<List<PosterCandidate>> GetPostersAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var list = new List<PosterCandidate>();
            if (!doc.RootElement.TryGetProperty("posters", out var posters)) return list;
            foreach (var p in posters.EnumerateArray())
            {
                var fp   = p.GetProperty("file_path").GetString();
                if (fp == null) continue;
                string? lang = p.TryGetProperty("iso_639_1", out var lp) && lp.ValueKind == JsonValueKind.String
                    ? lp.GetString() : null;
                list.Add(new PosterCandidate
                {
                    Source       = PosterSource.Tmdb,
                    ImageUrl     = $"{ImgBase}/original{fp}",
                    ThumbnailUrl = $"{ImgBase}/w300{fp}",
                    Language     = lang,
                    IsTextless   = lang == null,
                    Width        = p.TryGetProperty("width",        out var w)  ? w.GetInt32()  : 0,
                    Height       = p.TryGetProperty("height",       out var h)  ? h.GetInt32()  : 0,
                    VoteAverage  = p.TryGetProperty("vote_average", out var va) ? va.GetDouble(): null
                });
            }
            return list.OrderByDescending(p => p.IsTextless).ThenByDescending(p => p.VoteAverage ?? 0).ToList();
        }
        catch { return new(); }
    }

    private async Task<List<BackgroundCandidate>> GetBackgroundsAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var list = new List<BackgroundCandidate>();
            var key  = doc.RootElement.TryGetProperty("backdrops", out _) ? "backdrops" : "stills";
            if (!doc.RootElement.TryGetProperty(key, out var backdrops)) return list;
            foreach (var p in backdrops.EnumerateArray())
            {
                var fp = p.TryGetProperty("file_path", out var f) ? f.GetString() : null;
                if (fp == null) continue;
                list.Add(new BackgroundCandidate
                {
                    Source       = PosterSource.Tmdb,
                    ImageUrl     = $"{ImgBase}/original{fp}",
                    ThumbnailUrl = $"{ImgBase}/w500{fp}",
                    Width        = p.TryGetProperty("width",        out var w)  ? w.GetInt32()  : 0,
                    Height       = p.TryGetProperty("height",       out var h)  ? h.GetInt32()  : 0,
                    VoteAverage  = p.TryGetProperty("vote_average", out var va) ? va.GetDouble(): null
                });
            }
            return list.OrderByDescending(p => p.VoteAverage ?? 0).ToList();
        }
        catch { return new(); }
    }

    /// <summary>The titles of a public TMDB list, in list order, or null if it couldn't be read.</summary>
    public async Task<List<ListEntry>?> GetListEntriesAsync(string listId, CancellationToken ct = default)
    {
        var entries = new List<ListEntry>();
        try
        {
            for (int page = 1; page <= 20; page++)
            {
                var resp = await _http.GetAsync($"{Base}/list/{Uri.EscapeDataString(listId)}?api_key={_apiKey}&page={page}", ct);
                if (!resp.IsSuccessStatusCode) return page == 1 ? null : entries;
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return page == 1 ? null : entries;
                foreach (var it in items.EnumerateArray())
                {
                    var kind = it.TryGetProperty("media_type", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString() : null;
                    if (kind is not ("movie" or "tv")) continue;   // people etc.
                    var tmdb  = it.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
                    var title = it.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()
                              : it.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var date  = (kind == "movie" ? "release_date" : "first_air_date");
                    int? year = it.TryGetProperty(date, out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: >= 4 } ds
                                && int.TryParse(ds[..4], out var yv) ? yv : null;
                    entries.Add(new ListEntry(null, tmdb, title, year, kind == "tv"));
                }
                if (!doc.RootElement.TryGetProperty("total_pages", out var tp) || page >= tp.GetInt32()) break;
            }
            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
