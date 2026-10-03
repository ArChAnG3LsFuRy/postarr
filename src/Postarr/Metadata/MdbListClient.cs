using System.Text.Json;

namespace Postarr.Metadata;

/// <summary>
/// Fetches ratings from MDBList (https://mdblist.com) — a free-API aggregator that carries
/// Rotten Tomatoes critic + audience scores for BOTH movies and TV shows. OMDb only returns RT
/// for movies, so MDBList is what lets TV shows get an RT score. This is the same source Kometa
/// uses for its `mdb_tomatoes` rating. A free API key is available at mdblist.com/preferences.
/// </summary>
public class MdbListClient
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private const string Base = "https://api.mdblist.com";

    public MdbListClient(HttpClient http, string apiKey) { _http = http; _apiKey = apiKey?.Trim() ?? ""; }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return false;
        try
        {
            // The /user endpoint validates the key: 200 + account JSON for a good key,
            // 401/403 + {"error":...} for a bad one. (The old /?apikey=&i= form is gone —
            // it now just returns a generic API landing blob for any input.)
            var resp = await _http.GetAsync($"{Base}/user?apikey={Uri.EscapeDataString(_apiKey)}", ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("error", out _);
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns Rotten Tomatoes critic % and audience % for an IMDb id, or a TMDB id + media type.
    /// Returns null if no id is available or the request fails.
    /// </summary>
    public async Task<(int? RottenTomatoes, int? Audience, int? Metacritic)?> GetRottenTomatoesAsync(
        string? imdbId, string? tmdbId, bool isMovie, CancellationToken ct = default)
    {
        // Current MDBList API is path-based: /{provider}/{mediatype}/{id}?apikey=…
        var mediaType = isMovie ? "movie" : "show";
        string? path = !string.IsNullOrWhiteSpace(imdbId) ? $"/imdb/{mediaType}/{imdbId}"
            : !string.IsNullOrWhiteSpace(tmdbId)           ? $"/tmdb/{mediaType}/{tmdbId}"
            : null;
        if (path == null) return null;

        try
        {
            var resp = await _http.GetAsync($"{Base}{path}?apikey={Uri.EscapeDataString(_apiKey)}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ratings", out var ratings)
                || ratings.ValueKind != JsonValueKind.Array)
                return null;

            // ratings: [{ "source": "tomatoes", "value": 94 }, { "source": "popcorn", "value": 85 }, ...]
            //
            // MDBList names the RT *audience* score "popcorn" (after the RT popcorn-bucket icon) —
            // NOT "tomatoesaudience". Matching only the latter meant Audience Score never populated
            // however valid the API key was. Keep the other spellings as tolerant fallbacks.
            int? rt = null, audience = null, metacritic = null;
            foreach (var r in ratings.EnumerateArray())
            {
                var source = r.TryGetProperty("source", out var sr) ? sr.GetString() : null;
                if (source == null || !r.TryGetProperty("value", out var vv)) continue;

                // Values are mixed: RT scores are ints (0-100) but imdb/letterboxd/trakt come back
                // as decimals (8.8). GetInt32() THROWS on those, which aborted the whole loop via the
                // outer catch and lost the RT scores entirely — so parse defensively and round.
                int? val = null;
                if (vv.ValueKind == JsonValueKind.Number)
                {
                    if (vv.TryGetInt32(out var iv))        val = iv;
                    else if (vv.TryGetDouble(out var dv))  val = (int)Math.Round(dv);
                }
                else if (vv.ValueKind == JsonValueKind.String
                         && int.TryParse(vv.GetString(), out var pv)) val = pv;

                if (val == null || val < 0) continue;
                source = source.ToLowerInvariant();
                if (source == "tomatoes")                                            rt = val;
                else if (source is "popcorn" or "tomatoesaudience" or "audience")     audience = val;
                else if (source == "metacritic")                                      metacritic = val;
            }
            return (rt, audience, metacritic);
        }
        catch { return null; }
    }

    /// <summary>Ratings for one title from a batch lookup. Letterboxd is out of 5; the rest are 0–100.</summary>
    public record BatchRatings(string? ImdbId, string? TmdbId, double? Letterboxd, int? Trakt,
                               int? RottenTomatoes, int? Audience, int? Metacritic);

    /// <summary>
    /// Looks up many titles at once — MDBList takes up to 200 ids per request and counts it as ONE request
    /// against the daily limit (1,000 on a free key), so a whole library costs about ten. <paramref name="provider"/>
    /// is "imdb" or "tmdb"; results are keyed by that same id. Titles MDBList doesn't know are simply absent.
    /// </summary>
    public async Task<Dictionary<string, BatchRatings>> GetBatchRatingsAsync(
        IEnumerable<string> ids, string provider, bool isMovie, CancellationToken ct = default)
    {
        var result = new Dictionary<string, BatchRatings>(StringComparer.OrdinalIgnoreCase);
        var all = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        var url = $"{Base}/{provider}/{(isMovie ? "movie" : "show")}?apikey={Uri.EscapeDataString(_apiKey)}";
        foreach (var chunk in all.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();
            using var body = new StringContent(JsonSerializer.Serialize(new { ids = chunk }),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) break;   // limit reached / bad key — keep what we have
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) break;

            foreach (var m in doc.RootElement.EnumerateArray())
            {
                string? imdb = null, tmdb = null;
                if (m.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Object)
                {
                    if (idsEl.TryGetProperty("imdb", out var i) && i.ValueKind == JsonValueKind.String) imdb = i.GetString();
                    if (idsEl.TryGetProperty("tmdb", out var t) && t.ValueKind == JsonValueKind.Number)  tmdb = t.GetRawText();
                }
                var key = provider == "imdb" ? imdb : tmdb;
                if (string.IsNullOrEmpty(key)) continue;

                // ratings: [{ "source": "letterboxd", "value": 9.2, "score": 92 }, …]. "score" is the 0–100
                // normalised form of every source, so Letterboxd's 4.6/5 arrives as 92.
                double? lb = null; int? trakt = null, rt = null, aud = null, mc = null;
                if (m.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
                    foreach (var r in ratings.EnumerateArray())
                    {
                        var source = r.TryGetProperty("source", out var s) ? s.GetString()?.ToLowerInvariant() : null;
                        if (!r.TryGetProperty("score", out var sc) || sc.ValueKind != JsonValueKind.Number
                            || !sc.TryGetDouble(out var score) || score <= 0) continue;
                        switch (source)
                        {
                            case "letterboxd": lb    = Math.Round(score / 20, 1);   break;
                            case "trakt":      trakt = (int)Math.Round(score);      break;
                            case "tomatoes":   rt    = (int)Math.Round(score);      break;
                            case "popcorn":    aud   = (int)Math.Round(score);      break;
                            case "metacritic": mc    = (int)Math.Round(score);      break;
                        }
                    }
                result[key!] = new BatchRatings(imdb, tmdb, lb, trakt, rt, aud, mc);
            }
        }
        return result;
    }

    /// <summary>
    /// IMDb id → rank for a public MDBList list, or null if it couldn't be read (so the caller keeps what it had
    /// rather than wiping every rank on a bad day).
    /// </summary>
    public async Task<Dictionary<string, int>?> GetListRanksAsync(int listId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{Base}/lists/{listId}/items?limit=1000&apikey={Uri.EscapeDataString(_apiKey)}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            // Items come back as an array, or grouped as { "movies": [...], "shows": [...] }.
            var items = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array) items.AddRange(doc.RootElement.EnumerateArray());
            else foreach (var group in new[] { "movies", "shows" })
                if (doc.RootElement.TryGetProperty(group, out var g) && g.ValueKind == JsonValueKind.Array)
                    items.AddRange(g.EnumerateArray());

            // Position in the list as returned (MDBList's own "rank" field counts in steps of 1000).
            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int n = 0; n < items.Count; n++)
                if (items[n].TryGetProperty("imdb_id", out var id) && id.GetString() is { Length: > 0 } imdb)
                    ranks.TryAdd(imdb, n + 1);
            return ranks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>
    /// The titles of an MDBList list, in the list's own order (movies and shows interleaved), or null if it couldn't
    /// be read. <paramref name="path"/> comes from <see cref="ListEntryParser.MdbListPath"/>.
    /// </summary>
    public async Task<List<ListEntry>?> GetListEntriesAsync(string path, CancellationToken ct = default)
    {
        var entries = new List<ListEntry>();
        try
        {
            for (int offset = 0; offset < 10000; offset += 1000)
            {
                var resp = await _http.GetAsync($"{Base}{path}/items?unified=true&limit=1000&offset={offset}&apikey={Uri.EscapeDataString(_apiKey)}", ct);
                if (!resp.IsSuccessStatusCode) return offset == 0 ? null : entries;
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var page = new List<JsonElement>();
                if (doc.RootElement.ValueKind == JsonValueKind.Array) page.AddRange(doc.RootElement.EnumerateArray());
                else foreach (var group in new[] { "movies", "shows" })
                    if (doc.RootElement.TryGetProperty(group, out var g) && g.ValueKind == JsonValueKind.Array)
                        page.AddRange(g.EnumerateArray());

                foreach (var it in page)
                {
                    string? imdb = it.TryGetProperty("imdb_id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    string? tmdb = null;
                    if (it.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object
                        && ids.TryGetProperty("tmdb", out var t) && t.ValueKind == JsonValueKind.Number) tmdb = t.GetRawText();
                    tmdb ??= it.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
                    var title = it.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                    int? year = it.TryGetProperty("release_year", out var ry) && ry.ValueKind == JsonValueKind.Number && ry.TryGetInt32(out var yv) ? yv : null;
                    var kind = it.TryGetProperty("mediatype", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString() : null;
                    if (imdb == null && tmdb == null && title == null) continue;
                    entries.Add(new ListEntry(imdb, tmdb, title, year, kind == null ? null : kind == "show"));
                }
                if (page.Count < 1000 && !(resp.Headers.TryGetValues("X-Has-More", out var more) && more.Contains("true"))) break;
            }
            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Public lists matching a name search (most popular first), or MDBList's top lists when query is empty. Null if it couldn't be read.</summary>
    public async Task<List<CommunityList>?> SearchListsAsync(string? query, int limit = 30, CancellationToken ct = default)
    {
        try
        {
            var q = (query ?? "").Trim();
            var url = q.Length == 0
                ? $"{Base}/lists/top?limit={limit}&apikey={Uri.EscapeDataString(_apiKey)}"
                : $"{Base}/lists/search?query={Uri.EscapeDataString(q)}&limit={limit}&apikey={Uri.EscapeDataString(_apiKey)}";
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                    : doc.RootElement.TryGetProperty("lists", out var l) && l.ValueKind == JsonValueKind.Array ? l : default;
            if (arr.ValueKind != JsonValueKind.Array) return null;

            string? Str(JsonElement e, params string[] names)
            {
                foreach (var n in names)
                    if (e.TryGetProperty(n, out var v))
                        if (v.ValueKind == JsonValueKind.String) return v.GetString();
                        else if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("name", out var nn) && nn.ValueKind == JsonValueKind.String) return nn.GetString();
                return null;
            }
            int Num(JsonElement e, params string[] names)
            {
                foreach (var n in names)
                    if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                return 0;
            }

            var result = new List<CommunityList>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Num(e, "id", "listid", "list_id");
                var name = Str(e, "name", "title");
                if (id == 0 || string.IsNullOrWhiteSpace(name)) continue;
                var user = Str(e, "user_name", "username", "user");
                var slug = Str(e, "slug");
                result.Add(new CommunityList(id, name!, user, slug, Num(e, "items", "item_count", "items_count", "count"),
                    Num(e, "likes", "like_count", "likes_count"), Str(e, "description"),
                    !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(slug) ? $"https://mdblist.com/lists/{user}/{slug}" : null));
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
