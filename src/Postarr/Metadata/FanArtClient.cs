using System.Text.Json;
using Postarr.Models;

namespace Postarr.Metadata;

public class FanArtClient
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private readonly ILogger?   _logger;
    private const string Base = "https://webservice.fanart.tv/v3";

    public FanArtClient(HttpClient http, string apiKey, ILogger? logger = null)
    { _http = http; _apiKey = apiKey; _logger = logger; }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{Base}/movies/603?api_key={_apiKey}", ct);
            return resp.StatusCode != System.Net.HttpStatusCode.Unauthorized
                && resp.StatusCode != System.Net.HttpStatusCode.Forbidden;
        }
        catch { return false; }
    }

    // ── Posters ──────────────────────────────────────────────────────────────

    public Task<List<PosterCandidate>> GetMoviePostersAsync(string tmdbId, CancellationToken ct = default) =>
        GetPosterArrayAsync($"{Base}/movies/{tmdbId}?api_key={_apiKey}", "movieposter", ct);

    public Task<List<PosterCandidate>> GetShowPostersAsync(string tvdbId, CancellationToken ct = default) =>
        GetPosterArrayAsync($"{Base}/tv/{tvdbId}?api_key={_apiKey}", "tvposter", ct);

    public async Task<List<PosterCandidate>> GetSeasonPostersAsync(string tvdbId, int season, CancellationToken ct = default)
    {
        try
        {
            var url  = $"{Base}/tv/{tvdbId}?api_key={_apiKey}";
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("FanArt season lookup failed for tvdb {TvdbId}: HTTP {Status}", tvdbId, (int)resp.StatusCode);
                return new();
            }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var list = new List<PosterCandidate>();
            if (!doc.RootElement.TryGetProperty("seasonposter", out var arr)) return list;
            foreach (var p in arr.EnumerateArray())
                if (p.TryGetProperty("season", out var s) && s.GetString() == season.ToString())
                    list.Add(BuildPosterCandidate(p));
            return list.OrderByDescending(p => p.Likes ?? 0).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FanArt season poster fetch threw for tvdb {TvdbId}", tvdbId);
            return new();
        }
    }

    /// <summary>Every season poster FanArt.tv has for a show (one request), with its season number.</summary>
    public async Task<List<(int Season, PosterCandidate Poster)>> GetAllSeasonPostersAsync(string tvdbId, CancellationToken ct = default)
    {
        var list = new List<(int, PosterCandidate)>();
        try
        {
            var resp = await _http.GetAsync($"{Base}/tv/{tvdbId}?api_key={_apiKey}", ct);
            if (!resp.IsSuccessStatusCode) return list;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("seasonposter", out var arr)) return list;
            foreach (var p in arr.EnumerateArray())
                if (p.TryGetProperty("season", out var s) && int.TryParse(s.GetString(), out var n))
                    list.Add((n, BuildPosterCandidate(p)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "FanArt season posters fetch threw for tvdb {TvdbId}", tvdbId);
        }
        return list;
    }

    // ── Backgrounds ─────────────────────────────────────────────────────────

    public Task<List<BackgroundCandidate>> GetMovieBackgroundsAsync(string tmdbId, CancellationToken ct = default) =>
        GetBackgroundArrayAsync($"{Base}/movies/{tmdbId}?api_key={_apiKey}", "moviebackground", ct);

    public Task<List<BackgroundCandidate>> GetShowBackgroundsAsync(string tvdbId, CancellationToken ct = default) =>
        GetBackgroundArrayAsync($"{Base}/tv/{tvdbId}?api_key={_apiKey}", "showbackground", ct);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<List<PosterCandidate>> GetPosterArrayAsync(string url, string key, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Don't swallow this silently — a 401/403 here means the API key is
                // wrong/expired, a 404 means FanArt has no entry for this TMDB/TVDB id at all.
                _logger?.LogWarning("FanArt request failed ({Key}): HTTP {Status} for {Url}",
                    key, (int)resp.StatusCode, url.Split('?')[0]);
                return new();
            }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var list = new List<PosterCandidate>();
            if (!doc.RootElement.TryGetProperty(key, out var arr))
            {
                // Entry exists on FanArt but has no posters of this type uploaded yet —
                // this is normal/expected for less popular titles, not an error.
                return list;
            }
            foreach (var p in arr.EnumerateArray()) list.Add(BuildPosterCandidate(p));
            return list.OrderByDescending(p => p.Likes ?? 0).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FanArt poster fetch threw for {Url}", url.Split('?')[0]);
            return new();
        }
    }

    private async Task<List<BackgroundCandidate>> GetBackgroundArrayAsync(string url, string key, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("FanArt background request failed ({Key}): HTTP {Status} for {Url}",
                    key, (int)resp.StatusCode, url.Split('?')[0]);
                return new();
            }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var list = new List<BackgroundCandidate>();
            if (!doc.RootElement.TryGetProperty(key, out var arr)) return list;
            foreach (var p in arr.EnumerateArray())
            {
                var imgUrl = p.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(imgUrl)) continue;
                imgUrl = ForceHttps(imgUrl);
                var likes = p.TryGetProperty("likes", out var lk) && int.TryParse(lk.GetString(), out var lv) ? lv : 0;
                list.Add(new BackgroundCandidate
                {
                    Source       = PosterSource.FanArt,
                    ImageUrl     = imgUrl,
                    ThumbnailUrl = imgUrl,
                    Likes        = likes
                });
            }
            return list.OrderByDescending(p => p.Likes ?? 0).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FanArt background fetch threw for {Url}", url.Split('?')[0]);
            return new();
        }
    }

    private static PosterCandidate BuildPosterCandidate(JsonElement p)
    {
        var url  = p.TryGetProperty("url",  out var u) ? u.GetString() ?? "" : "";
        url      = ForceHttps(url);
        var lang = p.TryGetProperty("lang", out var l) ? l.GetString()        : null;
        var likes= p.TryGetProperty("likes",out var lk) && int.TryParse(lk.GetString(), out var lv) ? lv : 0;
        return new PosterCandidate
        {
            Source       = PosterSource.FanArt,
            ImageUrl     = url,
            ThumbnailUrl = url,
            Language     = lang,
            IsTextless   = lang == "00" || string.IsNullOrEmpty(lang),
            Likes        = likes,
            ExternalId   = p.TryGetProperty("id", out var id) ? id.GetString() : null
        };
    }

    // FanArt.tv's API still returns all asset URLs as plain http://, which can be
    // silently blocked as mixed content when Postarr's UI is served over https,
    // and some CDNs/proxies handle http differently from https for hot-linking.
    // Their CDN (assets.fanart.tv) fully supports https, so this is a safe upgrade.
    private static string ForceHttps(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["http://".Length..]
            : url;
}
