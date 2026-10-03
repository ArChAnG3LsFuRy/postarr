using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Postarr.Data;
using Postarr.MediaServers;
using Postarr.Models;
using SkiaSharp;

namespace Postarr.Services;

/// <summary>
/// Posters for the collections Postarr builds. Each one gets artwork as soon as it exists, from (in order):
/// Kometa's default image when the user has opted in and one exists; TMDB's own poster for a franchise; otherwise
/// Postarr's own design, drawn here. A poster the user picks themselves is never replaced.
/// </summary>
public class CollectionArtService
{
    // Bump when the drawn design changes, so existing generated posters are redrawn and re-uploaded.
    private const int DesignVersion = 2;
    public  const string GeneratedPrefix = "/api/auto-collections/poster/";

    private static readonly ConcurrentDictionary<string, byte[]> _rendered = new();

    private readonly IWebHostEnvironment _env;
    private readonly IHttpClientFactory  _http;
    private readonly MetadataClientFactory _factory;
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly SettingsRepository  _settings;
    private readonly ILogger<CollectionArtService> _logger;
    private readonly KometaImageCache _kometa;

    public CollectionArtService(IWebHostEnvironment env, IHttpClientFactory http, MetadataClientFactory factory,
        IDbContextFactory<PostarrDbContext> dbFactory, SettingsRepository settings, ILogger<CollectionArtService> logger,
        KometaImageCache kometa)
    { _env = env; _http = http; _factory = factory; _dbFactory = dbFactory; _settings = settings; _logger = logger; _kometa = kometa; }

    // ── What the poster says ─────────────────────────────────────────────────

    /// <summary>Small label above, the big text, an optional caption below, and the colour.</summary>
    public record ArtText(string Label, string Main, string? Caption, float Hue, bool VariedHue);

    public static ArtText Describe(ManagedCollection m)
    {
        var title = m.Title;
        string? caption = null;
        // Jellyfin/Emby titles carry " (Movies)" / " (TV)" / " (Library)" to tell twins apart.
        var paren = title.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && title.EndsWith(')'))
        {
            var inner = title[(paren + 2)..^1];
            caption = inner switch { "TV" => "TV SHOWS", _ => inner.ToUpperInvariant() };
            title = title[..paren];
        }
        string Strip(string prefix) => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? title[prefix.Length..] : title;

        return m.SetKey switch
        {
            var k when k.StartsWith("chart.") => new ArtText("CHART", title, caption, 215, false),
            "award.best_picture"   => new ArtText("ACADEMY AWARDS", title, caption, 44, false),
            "award.oscar_winners"  => new ArtText("ACADEMY AWARDS", title, caption, 44, false),
            "award.oscar_nominees" => new ArtText("ACADEMY AWARDS", title, caption, 44, false),
            "award.emmy_winners"   => new ArtText("TELEVISION ACADEMY", title, caption, 44, false),
            "content.genre"        => new ArtText("GENRE", title, caption, 0, true),
            "content.franchise"    => new ArtText("FRANCHISE", title.Replace(" Collection", "").Trim(), caption, 0, true),
            "rating.content_rating"=> new ArtText("RATED", Strip("Rated "), caption, 176, false),
            "media.resolution"     => new ArtText("RESOLUTION", title, caption, 255, false),
            "production.studio"    => new ArtText("STUDIO", title, caption, 0, true),
            "production.network"   => new ArtText("NETWORK", title, caption, 0, true),
            "production.streaming" => new ArtText("STREAMING", title, caption, 0, true),
            "media.edition"        => new ArtText("EDITION", title, caption, 290, false),
            "location.country"     => new ArtText("COUNTRY", title, caption, 0, true),
            "location.language"    => new ArtText("LANGUAGE", title, caption, 0, true),
            "people.actor"         => new ArtText("STARRING", title, caption, 0, true),
            "people.director"      => new ArtText(m.MediaType == MediaType.Show ? "CREATED BY" : "DIRECTED BY", title, caption, 0, true),
            "time.year"            => new ArtText("YEAR", title, caption, 140, false),
            "time.decade"          => new ArtText("DECADE", title, caption, 112, false),
            "time.best_of_year"    => new ArtText("BEST OF", Strip("Best of "), caption, 28, false),
            "custom"               => new ArtText("COLLECTION", title, caption, 200, false),
            _                      => new ArtText("", title, caption, 220, false),
        };
    }

    public static string GeneratedUrl(ManagedCollection m)
    {
        var d = Describe(m);
        // A stable hash (string.GetHashCode is randomised per process), so the URL only changes with the design.
        var v = Fnv($"{DesignVersion}|{m.SetKey}|{d.Label}|{d.Main}|{d.Caption}");
        return $"{GeneratedPrefix}{m.Id}?v={v:x8}";
    }

    private static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s) { h ^= ch; h *= 16777619; }
        return h;
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    public byte[] Render(ManagedCollection m)
    {
        var key = GeneratedUrl(m);
        return _rendered.GetOrAdd(key, _ => Draw(Describe(m)));
    }

    private byte[] Draw(ArtText t)
    {
        const int W = 1000, H = 1500;
        float hue = t.VariedHue ? Fnv(t.Main.ToLowerInvariant()) % 360 : t.Hue;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;

        // Background: a lit centre falling off to near-black edges, then a darker foot — the Kometa look,
        // with Postarr's own type.
        using (var bg = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(new SKPoint(W / 2f, H * 0.40f), H * 0.78f,
                new[] { SKColor.FromHsl(hue, 72, 44), SKColor.FromHsl(hue, 68, 27), SKColor.FromHsl(hue, 60, 8) },
                new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp)
        }) c.DrawRect(0, 0, W, H, bg);
        using (var foot = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, H * 0.62f), new SKPoint(0, H),
                new[] { SKColors.Transparent, new SKColor(0, 0, 0, 150) }, SKShaderTileMode.Clamp)
        }) c.DrawRect(0, 0, W, H, foot);

        var face = Typeface();
        // Big text: as large as fits, wrapped onto up to three lines. Names go in capitals; values whose case
        // means something ("1080p", "2020s", "MA 15+") keep it.
        var keepCase = t.Label is "DECADE" or "RESOLUTION" or "RATED" or "BEST OF" or "YEAR";
        var (lines, size) = Fit(keepCase ? t.Main : t.Main.ToUpperInvariant(), face, maxWidth: 820, maxLines: 3, from: 200, to: 56);
        using var big = new SKPaint { Typeface = face, TextSize = size, Color = SKColors.White, IsAntialias = true, FakeBoldText = true };
        float lineH = size * 1.06f, blockH = lineH * lines.Count;
        float top = H * 0.46f - blockH / 2f;
        for (int i = 0; i < lines.Count; i++)
        {
            float w = big.MeasureText(lines[i]);
            float baseline = top + lineH * i + size * 0.80f;
            c.DrawText(lines[i], (W - w) / 2f, baseline, big);
        }

        using var small = new SKPaint { Typeface = face, TextSize = 38, Color = new SKColor(255, 255, 255, 200), IsAntialias = true };
        if (t.Label.Length > 0) Spaced(c, t.Label, W / 2f, top - 62, small, 9);
        // Accent rule under the title, then the caption (e.g. "TV SHOWS").
        using (var rule = new SKPaint { Color = new SKColor(255, 255, 255, 150), IsAntialias = true })
            c.DrawRoundRect(new SKRect(W / 2f - 70, top + blockH + 44, W / 2f + 70, top + blockH + 50), 3, 3, rule);
        if (!string.IsNullOrEmpty(t.Caption)) Spaced(c, t.Caption!, W / 2f, top + blockH + 120, small, 8);

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    // Greedy word wrap at decreasing sizes until it fits the width in at most maxLines lines.
    private static (List<string> Lines, float Size) Fit(string text, SKTypeface face, float maxWidth, int maxLines, float from, float to)
    {
        using var p = new SKPaint { Typeface = face, FakeBoldText = true };
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (float size = from; size >= to; size -= 6)
        {
            p.TextSize = size;
            var lines = new List<string>();
            var cur = "";
            foreach (var w in words)
            {
                var trial = cur.Length == 0 ? w : cur + " " + w;
                if (p.MeasureText(trial) <= maxWidth || cur.Length == 0) cur = trial;
                else { lines.Add(cur); cur = w; }
            }
            if (cur.Length > 0) lines.Add(cur);
            if (lines.Count <= maxLines && lines.All(l => p.MeasureText(l) <= maxWidth)) return (lines, size);
        }
        return (new List<string> { text }, to);
    }

    // Letter-spaced, centred text (Skia has no tracking setting).
    private static void Spaced(SKCanvas c, string s, float cx, float baseline, SKPaint p, float tracking)
    {
        var widths = s.Select(ch => p.MeasureText(ch.ToString())).ToList();
        float total = widths.Sum() + tracking * (s.Length - 1);
        float x = cx - total / 2f;
        for (int i = 0; i < s.Length; i++) { c.DrawText(s[i].ToString(), x, baseline, p); x += widths[i] + tracking; }
    }

    private static SKTypeface? _face;
    private SKTypeface Typeface()
    {
        if (_face != null) return _face;
        foreach (var root in new[] { _env.WebRootPath, Path.Combine(_env.ContentRootPath ?? "", "wwwroot"),
                                     Path.Combine(AppContext.BaseDirectory, "wwwroot") })
        {
            var f = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "fonts", "Inter-Medium.ttf");
            if (f != null && File.Exists(f)) return _face = SKTypeface.FromFile(f);
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)   // running from source
        {
            var f = Path.Combine(dir.FullName, "wwwroot", "fonts", "Inter-Medium.ttf");
            if (File.Exists(f)) return _face = SKTypeface.FromFile(f);
        }
        return _face = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
    }

    // ── Kometa's default images (opt-in) ─────────────────────────────────────

    /// <summary>The raw GitHub URL of Kometa's image for this collection, or null if it has none.</summary>
    public async Task<string?> KometaUrlAsync(ManagedCollection m, CancellationToken ct) => (await KometaLookupAsync(m, ct)).Url;

    /// <summary>
    /// Kometa's image for this collection — taken from the local cache, or downloaded into it the first time. Unreachable
    /// is true when GitHub couldn't be asked (rather than the image not existing), so the caller can keep what it has.
    /// </summary>
    public async Task<(string? Url, bool Unreachable)> KometaLookupAsync(ManagedCollection m, CancellationToken ct)
    {
        var d = Describe(m);
        var main = d.Main;
        var isShow = m.MediaType == MediaType.Show;
        var candidates = m.SetKey switch
        {
            "chart.imdb_top250"       => new[] { "chart/color/IMDb Top 250.jpg" },
            "chart.letterboxd_top250" => new[] { "chart/color/Letterboxd Top 250.jpg" },
            "chart.tmdb_trending"     => new[] { "chart/color/TMDb Trending.jpg" },
            "chart.tmdb_popular"      => new[] { "chart/color/TMDb Popular.jpg" },
            "chart.tmdb_top_rated"    => new[] { "chart/color/TMDb Top Rated.jpg" },
            "award.best_picture"      => new[] { "chart/color/Oscar Best Picture Winners.jpg" },
            "content.genre"           => new[] { $"genre/{main}.jpg" },
            "content.franchise"       => new[] { $"franchise/{main}.jpg" },
            "rating.content_rating"   => RatingFiles(main),
            "media.resolution"        => main switch { "4K" => new[] { "resolution/4k.jpg" }, "1080p" => new[] { "resolution/1080.jpg" },
                                                       "720p" => new[] { "resolution/720.jpg" }, _ => new[] { "resolution/480.jpg" } },
            "production.studio"       => new[] { $"studio/{main}.jpg" },
            "production.network"      => new[] { $"network/color/{main}.jpg" },
            "production.streaming"    => new[] { $"streaming/color/{main}_{(isShow ? "shows" : "movies")}.jpg", $"streaming/color/{main}.jpg" },
            "time.decade"             => new[] { $"decade/{main.TrimEnd('s')}.jpg" },
            "time.year"               => new[] { $"year/{main}.jpg" },
            "location.country"        => new[] { $"country/color/{main}.jpg" },
            "location.language"       => new[] { $"country/color/{main}.jpg" },   // Kometa keeps language art alongside countries
            "time.best_of_year"       => new[] { $"year/best/{main}.jpg" },
            _                         => Array.Empty<string>(),
        };
        bool unreachable = false;
        foreach (var path in candidates)
        {
            var r = await _kometa.EnsureAsync(path, ct);
            if (r == KometaImageCache.Lookup.Found) return (KometaImageCache.UrlFor(path), false);
            if (r == KometaImageCache.Lookup.Unreachable) unreachable = true;
        }
        return (null, unreachable);
    }

    private static string[] RatingFiles(string rating)
    {
        var r = rating.Replace(" ", "");
        if (r.Equals("NotRated", StringComparison.OrdinalIgnoreCase) || r.Equals("Unrated", StringComparison.OrdinalIgnoreCase)) r = "NR";
        return new[] { "us", "au", "uk", "nz", "de" }.Select(c => $"content_rating/{c}/{r}.jpg").ToArray();
    }

    // ── Putting art on the collections ───────────────────────────────────────

    /// <summary>Image bytes for a poster URL Postarr may use: its own design (drawn locally) or a web image.</summary>
    public async Task<byte[]> BytesAsync(string url, CancellationToken ct)
    {
        if (url.StartsWith(GeneratedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = int.Parse(url[GeneratedPrefix.Length..].Split('?')[0]);
            using var db = await _dbFactory.CreateDbContextAsync(ct);
            var m = await db.ManagedCollections.FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new InvalidOperationException("That collection is no longer managed by Postarr.");
            return Render(m);
        }
        if (KometaImageCache.IsKometaUrl(url))
            return await _kometa.ReadAsync(url, ct) ?? throw new InvalidOperationException("Kometa's image isn't available right now.");
        return await _http.CreateClient("imagedownload").GetByteArrayAsync(url, ct);
    }

    /// <summary>
    /// Gives every Postarr-made collection a poster, and keeps it current if the design or the Kometa setting
    /// changes — but only while the poster is still the one Postarr put there. Returns how many were updated.
    /// </summary>
    public async Task<int> ApplyAutoArtAsync(IMediaServerClient client, CancellationToken ct)
    {
        var cfg    = _settings.Get().AutoCollections ?? new AutoCollectionSettings();
        var server = _factory.MediaServerName;
        var tmdb   = _factory.BuildTmdbClient();
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var managed = await db.ManagedCollections.Where(m => m.ServerType == server).ToListAsync(ct);
        var cols    = await db.Collections.Where(c => c.ServerType == server).ToListAsync(ct);
        int updated = 0;

        foreach (var m in managed)
        {
            var col = cols.FirstOrDefault(c => c.PlexRatingKey == m.ServerCollectionId);
            if (col == null || col.PosterDismissed) continue;
            // The user picked this poster (in Postarr, or it was there before Postarr made art) → hands off.
            bool untouched = m.AutoPosterUrl == null
                ? col.CurrentPosterUrl == null || col.CurrentPosterSource == nameof(PosterSource.Tmdb)
                : col.CurrentPosterUrl == m.AutoPosterUrl;
            if (!untouched) continue;

            try
            {
                string? url = null; var source = PosterSource.Generated;
                if (m.SetKey == "content.franchise" && !string.IsNullOrEmpty(m.TmdbCollectionId) && tmdb != null)
                {
                    url = col.CurrentPosterSource == nameof(PosterSource.Tmdb) ? col.CurrentPosterUrl : null;
                    url ??= (await tmdb.GetCollectionPostersAsync(m.TmdbCollectionId, ct)).FirstOrDefault()?.ImageUrl;
                    if (url != null) source = PosterSource.Tmdb;
                }
                if (url == null && cfg.UseKometaImages)
                {
                    var (k, unreachable) = await KometaLookupAsync(m, ct);
                    if (k != null) { url = k; source = PosterSource.Kometa; }
                    // GitHub is down: keep the Kometa poster that's already there rather than swapping in Postarr's own design.
                    else if (unreachable && KometaImageCache.IsKometaUrl(m.AutoPosterUrl)) continue;
                }
                url ??= GeneratedUrl(m);

                if (url == m.AutoPosterUrl && col.CurrentPosterUrl == url) continue;   // already there
                await client.UploadPosterAsync(m.ServerCollectionId, await BytesAsync(url, ct), "image/jpeg", ct);
                col.CurrentPosterUrl = url; col.CurrentPosterSource = source.ToString();
                m.AutoPosterUrl = url;
                await db.SaveChangesAsync(ct);
                updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Poster for collection {Title} failed", m.Title);
            }
        }
        return updated;
    }
}
