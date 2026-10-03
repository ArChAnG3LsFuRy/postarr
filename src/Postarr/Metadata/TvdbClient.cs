using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Postarr.Models;

namespace Postarr.Metadata;

public class TvdbClient
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private const string Base = "https://api4.thetvdb.com/v4";
    private string?  _token;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public TvdbClient(HttpClient http, string apiKey) { _http = http; _apiKey = apiKey; }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try { return await EnsureLoggedInAsync(ct); }
        catch { return false; }
    }

    private async Task<bool> EnsureLoggedInAsync(CancellationToken ct)
    {
        if (_token != null && DateTime.UtcNow < _tokenExpiry) return true;
        using var content = new StringContent(JsonSerializer.Serialize(new { apikey = _apiKey }), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{Base}/login", content, ct);
        if (!resp.IsSuccessStatusCode) return false;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("token", out var tp)) return false;
        _token = tp.GetString();
        _tokenExpiry = DateTime.UtcNow.AddHours(1);
        return _token != null;
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        if (!await EnsureLoggedInAsync(ct)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    // ── Posters ──────────────────────────────────────────────────────────────

    public async Task<List<PosterCandidate>> GetShowPostersAsync(string id, CancellationToken ct = default)
    {
        var doc = await GetAsync($"/series/{id}/artworks?type=2", ct);
        return doc == null ? new() : ParsePosters(doc);
    }

    public async Task<List<PosterCandidate>> GetMoviePostersAsync(string id, CancellationToken ct = default)
    {
        var doc = await GetAsync($"/movies/{id}/artworks?type=14", ct);
        return doc == null ? new() : ParsePosters(doc);
    }

    public async Task<List<PosterCandidate>> GetSeasonPostersAsync(string seasonId, CancellationToken ct = default)
    {
        var doc = await GetAsync($"/seasons/{seasonId}/extended", ct);
        if (doc == null) return new();
        var list = new List<PosterCandidate>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("artwork", out var art))
            foreach (var a in art.EnumerateArray())
                if (a.TryGetProperty("type", out var t) && t.GetInt32() == 7)
                    list.Add(BuildPosterCandidate(a));
        return list;
    }

    // ── Backgrounds ─────────────────────────────────────────────────────────

    public async Task<List<BackgroundCandidate>> GetShowBackgroundsAsync(string id, CancellationToken ct = default)
    {
        var doc = await GetAsync($"/series/{id}/artworks?type=3", ct); // type 3 = background/fanart
        return doc == null ? new() : ParseBackgrounds(doc);
    }

    public async Task<List<BackgroundCandidate>> GetMovieBackgroundsAsync(string id, CancellationToken ct = default)
    {
        var doc = await GetAsync($"/movies/{id}/artworks?type=15", ct); // type 15 = movie background
        return doc == null ? new() : ParseBackgrounds(doc);
    }

    // ── Season ID lookup ─────────────────────────────────────────────────────

    public async Task<string?> GetSeasonIdAsync(string showId, int number, CancellationToken ct = default)
    {
        var doc = await GetAsync($"/series/{showId}/extended", ct);
        if (doc == null) return null;
        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("seasons", out var seasons))
            foreach (var s in seasons.EnumerateArray())
            {
                var num  = s.TryGetProperty("number", out var n) ? n.GetInt32() : -1;
                var type = s.TryGetProperty("type", out var t) && t.TryGetProperty("type", out var tt) ? tt.GetString() : null;
                if (num == number && type == "official")
                    return s.GetProperty("id").GetInt32().ToString();
            }
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<PosterCandidate> ParsePosters(JsonDocument doc)
    {
        var list = new List<PosterCandidate>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        foreach (var a in data.EnumerateArray()) list.Add(BuildPosterCandidate(a));
        return list.OrderByDescending(p => p.VoteAverage ?? 0).ToList();
    }

    private static List<BackgroundCandidate> ParseBackgrounds(JsonDocument doc)
    {
        var list = new List<BackgroundCandidate>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        foreach (var a in data.EnumerateArray())
        {
            var url   = a.TryGetProperty("image",     out var img)  ? img.GetString() ?? ""  : "";
            var thumb = a.TryGetProperty("thumbnail", out var th)   ? th.GetString()  ?? url : url;
            var score = a.TryGetProperty("score",     out var sc)   ? sc.GetDouble()          : (double?)null;
            list.Add(new BackgroundCandidate { Source = PosterSource.Tvdb, ImageUrl = url, ThumbnailUrl = thumb, VoteAverage = score });
        }
        return list.OrderByDescending(p => p.VoteAverage ?? 0).ToList();
    }

    private static PosterCandidate BuildPosterCandidate(JsonElement a)
    {
        var url   = a.TryGetProperty("image",     out var img) ? img.GetString() ?? "" : "";
        var thumb = a.TryGetProperty("thumbnail", out var th)  ? th.GetString()  ?? url : url;
        var lang  = a.TryGetProperty("language",  out var l)   ? l.GetString()          : null;
        var score = a.TryGetProperty("score",     out var sc)  ? sc.GetDouble()          : (double?)null;
        return new PosterCandidate { Source = PosterSource.Tvdb, ImageUrl = url, ThumbnailUrl = thumb,
            Language = lang, IsTextless = string.IsNullOrEmpty(lang), VoteAverage = score };
    }
}
