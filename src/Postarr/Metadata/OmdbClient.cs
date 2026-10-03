using System.Text.Json;

namespace Postarr.Metadata;

/// <summary>
/// Fetches ratings from the OMDB API (https://www.omdbapi.com).
/// Free API key available at https://www.omdbapi.com/apikey.aspx
/// Returns IMDb rating, Rotten Tomatoes critics %, and Metacritic score.
/// TMDB does not provide RT scores — OMDB is the standard source for these.
/// </summary>
public enum OmdbStatus { Ok, NotFound, Unavailable }

public class OmdbClient
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private const string Base = "https://www.omdbapi.com";

    // OMDb's free key allows ~1,000 requests/day. Once it answers "Request limit reached!" every
    // further call is wasted, so all clients (one is built per item) stop until the next UTC day.
    private static DateTime _blockedUntilUtc = DateTime.MinValue;
    public static bool IsLimited => DateTime.UtcNow < _blockedUntilUtc;

    /// <summary>Outcome of the last GetRatingsAsync: Unavailable = limit/outage/bad key (retry later),
    /// NotFound = OMDb has no such title (nothing to retry).</summary>
    public OmdbStatus LastStatus { get; private set; } = OmdbStatus.Unavailable;

    public OmdbClient(HttpClient http, string apiKey) { _http = http; _apiKey = apiKey; }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{Base}/?i=tt0111161&apikey={_apiKey}", ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("Response", out var r) && r.GetString() == "True";
        }
        catch { return false; }
    }

    /// <summary>
    /// Fetches IMDb rating, RT critics %, and audience score for a given IMDb ID (e.g. tt0111161).
    /// Returns null if the IMDb ID is empty or the request fails.
    /// </summary>
    public async Task<(double? ImdbRating, int? RottenTomatoes, int? Audience, int? Metacritic,
                       bool OscarWinner, bool OscarNominee, bool EmmyWinner)?> GetRatingsAsync(
        string imdbId, CancellationToken ct = default)
    {
        LastStatus = OmdbStatus.Unavailable;
        if (string.IsNullOrWhiteSpace(imdbId) || IsLimited) return null;
        try
        {
            // OMDb answers the daily-limit and bad-key cases with HTTP 401 + a JSON "Error", so the body
            // is read regardless of status to tell those apart from a genuinely unknown title.
            var resp = await _http.GetAsync($"{Base}/?i={imdbId}&tomatoes=true&apikey={_apiKey}", ct);
            if ((int)resp.StatusCode >= 500) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Response", out var r) || r.GetString() != "True")
            {
                var error = root.TryGetProperty("Error", out var e) ? e.GetString() ?? "" : "";
                if (error.Contains("limit", StringComparison.OrdinalIgnoreCase))
                    _blockedUntilUtc = DateTime.UtcNow.Date.AddDays(1);          // resume next UTC day
                else if (error.Contains("key", StringComparison.OrdinalIgnoreCase))
                    _blockedUntilUtc = DateTime.UtcNow.AddHours(1);              // bad/inactive key
                else if (resp.IsSuccessStatusCode)
                    LastStatus = OmdbStatus.NotFound;                            // "Incorrect IMDb ID." etc.
                return null;
            }
            LastStatus = OmdbStatus.Ok;

            // IMDb rating — "7.9" string
            double? imdbRating = null;
            if (root.TryGetProperty("imdbRating", out var ir) && ir.GetString() is { } irs && irs != "N/A")
                if (double.TryParse(irs, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                    imdbRating = Math.Round(d, 1);

            // Rotten Tomatoes from Ratings array: [{"Source":"Rotten Tomatoes","Value":"94%"}]
            int? rt = null, audience = null;
            if (root.TryGetProperty("Ratings", out var ratings))
            {
                foreach (var rating in ratings.EnumerateArray())
                {
                    var source = rating.TryGetProperty("Source", out var s) ? s.GetString() : null;
                    var value  = rating.TryGetProperty("Value",  out var v) ? v.GetString() : null;
                    if (value == null || value == "N/A") continue;
                    if (source == "Rotten Tomatoes" && value.EndsWith("%"))
                        if (int.TryParse(value.TrimEnd('%'), out var rtv)) rt = rtv;
                }
            }

            // Metacritic: the "Metascore" field ("90"), or the Ratings entry ("90/100") as a fallback.
            int? metacritic = null;
            if (root.TryGetProperty("Metascore", out var ms) && ms.GetString() is { } mss && int.TryParse(mss, out var mv))
                metacritic = mv;
            else if (root.TryGetProperty("Ratings", out var rr))
                foreach (var rating in rr.EnumerateArray())
                    if (rating.TryGetProperty("Source", out var src) && src.GetString() == "Metacritic"
                        && rating.TryGetProperty("Value", out var val) && val.GetString() is { } vs
                        && int.TryParse(vs.Split('/')[0], out var mv2))
                        metacritic = mv2;

            // OMDB also returns tomatoUserMeter for audience score (requires tomatoes=true)
            if (root.TryGetProperty("tomatoUserMeter", out var tum) && tum.GetString() is { } tums && tums != "N/A")
                if (int.TryParse(tums, out var av)) audience = av;

            // Awards is a free-text summary, e.g. "Won 7 Oscars. 21 wins & 31 nominations total."
            // or "Nominated for 3 Oscars." / "Won 2 Primetime Emmys." — the only awards source we
            // have, and what the Oscar/Emmy badges need (they were never populated before).
            bool oscarWin = false, oscarNom = false, emmyWin = false;
            if (root.TryGetProperty("Awards", out var aw) && aw.GetString() is { } awards && awards != "N/A")
            {
                oscarWin = System.Text.RegularExpressions.Regex.IsMatch(awards, @"won\s+\d+\s+oscar",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                oscarNom = System.Text.RegularExpressions.Regex.IsMatch(awards, @"nominated\s+for\s+\d+\s+oscar",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                emmyWin  = System.Text.RegularExpressions.Regex.IsMatch(awards, @"won\s+\d+\s+(primetime\s+)?emmy",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return (imdbRating, rt, audience, metacritic, oscarWin, oscarNom, emmyWin);
        }
        catch { return null; }
    }
}
