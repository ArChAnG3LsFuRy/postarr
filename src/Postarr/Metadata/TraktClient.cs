using System.Text.Json;

namespace Postarr.Metadata;

/// <summary>
/// Reads PUBLIC Trakt lists (https://trakt.tv) — search, popular lists, and a list's titles in list order.
/// Public data needs only a free "Client ID" from trakt.tv/oauth/applications: no login, no OAuth, and nothing is
/// ever written to the user's Trakt account.
/// </summary>
public class TraktClient
{
    private readonly HttpClient _http;
    private readonly string     _clientId;
    private const string Base = "https://api.trakt.tv";

    public TraktClient(HttpClient http, string clientId) { _http = http; _clientId = clientId?.Trim() ?? ""; }

    private HttpRequestMessage Req(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
        // Trakt sits behind Cloudflare, which rejects requests with no User-Agent (a bare HttpClient — like the Settings
        // "Test" button used — got its HTML block page, so even a correct key looked like "could not connect").
        req.Headers.TryAddWithoutValidation("User-Agent", "Postarr/1.0");
        req.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        req.Headers.TryAddWithoutValidation("trakt-api-key", _clientId);
        return req;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        if (_clientId.Length == 0) return false;
        try
        {
            // Any public endpoint: a good Client ID answers 200, a bad one 403.
            using var resp = await _http.SendAsync(Req("/movies/trending?limit=1"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Public lists matching a name search, or Trakt's popular lists when the query is empty. Null if it couldn't be read.</summary>
    public async Task<List<CommunityList>?> SearchListsAsync(string? query, int limit = 30, CancellationToken ct = default)
    {
        try
        {
            var q = (query ?? "").Trim();
            var path = q.Length == 0 ? $"/lists/popular?limit={limit}" : $"/search/list?query={Uri.EscapeDataString(q)}&limit={limit}";
            using var resp = await _http.SendAsync(Req(path), ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var result = new List<CommunityList>();
            foreach (var wrapper in doc.RootElement.EnumerateArray())
            {
                // Search and popular both wrap each list as { …, "list": { … } }.
                var l = wrapper.ValueKind == JsonValueKind.Object && wrapper.TryGetProperty("list", out var inner) ? inner : wrapper;
                if (l.ValueKind != JsonValueKind.Object) continue;
                if (l.TryGetProperty("privacy", out var pv) && pv.ValueKind == JsonValueKind.String && pv.GetString() != "public") continue;

                var name = Str(l, "name");
                var ids  = l.TryGetProperty("ids", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                var slug = ids.ValueKind == JsonValueKind.Object ? Str(ids, "slug") : null;
                int id   = ids.ValueKind == JsonValueKind.Object && ids.TryGetProperty("trakt", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                string? user = null;
                if (l.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
                    user = u.TryGetProperty("ids", out var uids) && uids.ValueKind == JsonValueKind.Object ? Str(uids, "slug") ?? Str(u, "username") : Str(u, "username");
                if (id == 0 || string.IsNullOrWhiteSpace(name)) continue;

                result.Add(new CommunityList(id, name!, user, slug,
                    Num(l, "item_count"), Num(l, "likes") is > 0 and var lk ? lk : Num(wrapper, "like_count"), Str(l, "description"),
                    !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(slug) ? $"https://trakt.tv/users/{user}/lists/{slug}" : null));
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>
    /// A list's movies and shows, in the list's own order. <paramref name="path"/> comes from
    /// <see cref="ListEntryParser.TraktPath"/> (e.g. "/users/name/lists/slug" or "/lists/123"). Null if it couldn't be read.
    /// </summary>
    /// <summary>How many entries of the last list read were seasons/episodes/people — not movies or shows, so left out.</summary>
    public int SkippedItems { get; private set; }

    public async Task<List<ListEntry>?> GetListEntriesAsync(string path, CancellationToken ct = default)
    {
        SkippedItems = 0;
        var rows = new List<(int Rank, int Seq, ListEntry Entry)>();
        try
        {
            for (int page = 1; page <= 20; page++)
            {
                using var resp = await _http.SendAsync(Req($"{path}/items?page={page}&limit=1000"), ct);
                if (!resp.IsSuccessStatusCode) return page == 1 ? null : Ordered(rows);
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return page == 1 ? null : Ordered(rows);

                foreach (var it in doc.RootElement.EnumerateArray())
                {
                    var type = Str(it, "type");
                    if (type is not ("movie" or "show")) { SkippedItems++; continue; }   // seasons, episodes, people
                    if (!it.TryGetProperty(type, out var m) || m.ValueKind != JsonValueKind.Object) continue;
                    var ids  = m.TryGetProperty("ids", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                    var imdb = ids.ValueKind == JsonValueKind.Object ? Str(ids, "imdb") : null;
                    var tmdb = ids.ValueKind == JsonValueKind.Object && ids.TryGetProperty("tmdb", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetRawText() : null;
                    var title = Str(m, "title");
                    int? year = m.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yv) ? yv : null;
                    if (imdb == null && tmdb == null && title == null) continue;
                    rows.Add((Num(it, "rank"), rows.Count, new ListEntry(imdb, tmdb, title, year, type == "show")));
                }
                var pages = resp.Headers.TryGetValues("X-Pagination-Page-Count", out var pc) && int.TryParse(pc.FirstOrDefault(), out var n) ? n : 1;
                if (page >= pages) break;
            }
            return Ordered(rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    // Trakt returns items by rank already; sorting again (stably) just makes that certain.
    private static List<ListEntry> Ordered(List<(int Rank, int Seq, ListEntry Entry)> rows) =>
        rows.OrderBy(r => r.Rank == 0 ? int.MaxValue : r.Rank).ThenBy(r => r.Seq).Select(r => r.Entry).ToList();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
