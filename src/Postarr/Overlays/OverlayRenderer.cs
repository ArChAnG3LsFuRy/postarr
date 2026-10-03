using Microsoft.AspNetCore.Hosting;
using SkiaSharp;
using Postarr.Models;
using System.Text.RegularExpressions;

namespace Postarr.Overlays;

/// <summary>
/// Renders overlay badges at independently-positioned locations on the poster.
/// Each badge type has its own X/Y stored as a fraction of poster size (0–1),
/// so positions set in the live preview scale correctly to any poster resolution.
/// </summary>
public class OverlayRenderer
{
    private readonly IWebHostEnvironment _env;
    public OverlayRenderer(IWebHostEnvironment env) => _env = env;

    // Plex displays posters in a 2:3 frame. Most sources (FanArt.tv in particular) serve
    // 1000x1426, which is wider than 2:3 — Plex then crops ~24px off each side, dragging
    // left/right-anchored badges to the edge and slicing them. Normalising to 2:3 BEFORE drawing
    // means what we render is exactly what Plex shows.
    private const float PosterAspect = 2f / 3f;

    /// <param name="isSeason">A season poster (drawn with its show's data): skips the show-level NEW badge,
    /// which would otherwise print "NEW SEASON" on every season of the show.</param>
    public byte[] ApplyOverlays(byte[] sourceBytes, LibraryItem item, OverlaySettings s, bool isSeason = false)
    {
        using var decoded = SKBitmap.Decode(sourceBytes)
            ?? throw new InvalidOperationException("Cannot decode source image.");
        using var bmp = NormaliseToPosterAspect(decoded);

        using var surface = SKSurface.Create(new SKImageInfo(bmp.Width, bmp.Height));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(bmp, 0, 0);

        foreach (var badge in BuildBadges(item, s, bmp.Width, bmp.Height, isSeason))
            DrawBadge(canvas, badge, bmp.Width, bmp.Height, s);

        using var img  = surface.Snapshot();
        // JPEG, not PNG: a full-res 2000×3000 poster re-encoded as lossless PNG is ~10 MB, and Plex
        // returns HTTP 500 on uploads that large. The same poster as JPEG q90 is ~1.5 MB, visually
        // indistinguishable (posters are photographic; badge edges survive q90 fine), and Plex stores
        // posters as JPEG internally anyway. Keep this in sync with the content type at the call site.
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Centre-crops the artwork to a 2:3 poster ratio (no-op when it already is). Returns the
    /// original bitmap when no crop is needed so the caller can dispose either safely.
    /// </summary>
    private static SKBitmap NormaliseToPosterAspect(SKBitmap src)
    {
        float aspect = (float)src.Width / src.Height;
        if (Math.Abs(aspect - PosterAspect) < 0.005f) return src.Copy();

        int cropW, cropH;
        if (aspect > PosterAspect) { cropH = src.Height; cropW = (int)MathF.Round(cropH * PosterAspect); }
        else                       { cropW = src.Width;  cropH = (int)MathF.Round(cropW / PosterAspect); }
        cropW = Math.Min(cropW, src.Width);
        cropH = Math.Min(cropH, src.Height);

        var rect = new SKRectI(
            (src.Width  - cropW) / 2,
            (src.Height - cropH) / 2,
            (src.Width  - cropW) / 2 + cropW,
            (src.Height - cropH) / 2 + cropH);

        var dst = new SKBitmap(cropW, cropH);
        return src.ExtractSubset(dst, rect) ? dst : src.Copy();
    }

    // ── Badge descriptor ──────────────────────────────────────────────────────
    private record RenderedBadge(
        string? ImagePath,
        string? Text,
        SKColor TextBg,
        float NormX,    // 0–1 fraction of poster width  (centre of badge)
        float NormY,    // 0–1 fraction of poster height (centre of badge)
        bool HasPill = false,   // resolution badge — sized by height, not width
        bool IsRibbon = false   // corner sash (RT / awards) — drawn without the dark box
    );

    // Uniform badge-box height as a fraction of poster height, scaled by BadgeSizePct — mirrors
    // --card-box-height client-side. Every badge shares this height; width hugs the content.
    private const float BoxHeightFactor = 0.30f;

    /// <summary>
    /// wwwroot on disk. IWebHostEnvironment.WebRootPath is NULL whenever the content root has no
    /// physical wwwroot beside it (which happens when ContentRootPath is pinned to the binary
    /// directory, as it is so the Windows service can find its files). That made every overlay
    /// render throw ArgumentNullException → HTTP 500 on apply, so fall back sensibly.
    /// </summary>
    private string WebRoot()
    {
        if (!string.IsNullOrEmpty(_env.WebRootPath) && Directory.Exists(_env.WebRootPath)) return _env.WebRootPath;
        foreach (var baseDir in new[] { _env.ContentRootPath, AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            var candidate = Path.Combine(baseDir, "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
        }
        // Running from source (`dotnet run`): the content root is bin\…\net8.0, which has no wwwroot copy
        // (the browser is served the project's via static web assets), so the badge images and font
        // weren't found and every badge fell back to plain text. Walk up to the project's own wwwroot.
        // Installed/Docker builds always hit one of the checks above first.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot");
            if (Directory.Exists(Path.Combine(candidate, "images"))) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "wwwroot");
    }

    /// <summary>
    /// "NEW" when the item was added within the configured window, "NEW SEASON" when a show's newest season
    /// was, otherwise null. Shared with the scan, which re-applies posters whose NEW state has changed.
    /// </summary>
    public static string? NewBadgeLabel(LibraryItem item, OverlaySettings s, DateTime nowUtc)
    {
        if (!s.NewBadgeEnabled) return null;
        var since = nowUtc.AddDays(-Math.Clamp(s.NewBadgeDays, 1, 365));
        if (item.AddedAtUtc >= since) return "NEW";
        if (item.MediaType == MediaType.Show && item.LatestSeasonAddedUtc >= since) return "NEW SEASON";
        return null;
    }

    private static string FormatRuntime(int minutes) =>
        minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";

    private List<RenderedBadge> BuildBadges(LibraryItem item, OverlaySettings s, int w, int h, bool isSeason = false)
    {
        var list = new List<RenderedBadge>();
        string root = Path.Combine(WebRoot(), "images");

        void Add(bool enabled, string? imgPath, string? text, SKColor bg, BadgeXY pos, bool hasPill = false, bool isRibbon = false)
        {
            if (!enabled) return;
            if (imgPath != null || text != null)
                list.Add(new RenderedBadge(imgPath, text, bg, pos.X, pos.Y, hasPill, isRibbon));
        }

        // ── Resolution — ONLY badge that gets the dark pill background ──────
        if (s.ResolutionEnabled && !string.IsNullOrEmpty(item.VideoResolution))
        {
            var imgPath = ResolutionImage(root, item.VideoResolution!, item.VideoDynamicRange);
            var label   = imgPath == null ? KometaResLabel(item.VideoResolution!, item.VideoDynamicRange) : null;
            Add(true, imgPath, label, ResTextColour(item.VideoResolution!, item.VideoDynamicRange), s.ResolutionPos, hasPill: true);
        }

        // ── Dynamic range — shown whenever enabled (previously suppressed while the
        //    resolution badge was on, so switching it on appeared to do nothing) ────
        if (s.DynamicRangeEnabled
            && !string.IsNullOrEmpty(item.VideoDynamicRange)
            && item.VideoDynamicRange!.ToUpper() != "SDR")
        {
            var imgPath = DynamicRangeImage(root, item.VideoDynamicRange!);
            Add(true, imgPath, imgPath == null ? item.VideoDynamicRange!.ToUpper() : null,
                new SKColor(110, 35, 155, 220), s.DynamicRangePos);
        }

        // ── Audio codec ────────────────────────────────────────────────────
        if (s.AudioCodecEnabled && !string.IsNullOrEmpty(item.AudioCodec))
        {
            var imgPath = AudioCodecImage(root, item.AudioCodec!, s.AudioCodecStyle);
            Add(true, imgPath, imgPath == null ? item.AudioCodec!.ToUpper() : null,
                new SKColor(25, 75, 155, 220), s.AudioCodecPos);
        }

        // ── Content rating ─────────────────────────────────────────────────
        // The image set only covers AU movie ratings; TV ratings (TV-MA, TV-14, …) and any
        // unmapped value fall back to a text pill so the rating still shows.
        if (s.ContentRatingEnabled && !string.IsNullOrEmpty(item.ContentRating))
        {
            var imgPath = ContentRatingImage(root, item.ContentRating!);
            Add(true, imgPath, imgPath == null ? item.ContentRating : null,
                new SKColor(20, 20, 20, 220), s.ContentRatingPos);
        }

        // ── Edition ────────────────────────────────────────────────────────
        if (s.EditionEnabled && !string.IsNullOrEmpty(item.Edition))
        {
            var imgPath = EditionImage(root, item.Edition!);
            Add(true, imgPath, imgPath == null ? item.Edition : null,
                new SKColor(20, 20, 20, 220), s.EditionPos);
        }

        // ── Streaming service ──────────────────────────────────────────────
        string? streamingImg = null;
        bool streamingShown = false;
        if (s.StreamingServiceEnabled && !string.IsNullOrEmpty(item.StreamingService))
        {
            streamingImg = StreamingImage(root, item.StreamingService!, s.StreamingStyle);
            Add(true, streamingImg, streamingImg == null ? item.StreamingService : null,
                StreamingColour(item.StreamingService!), s.StreamingServicePos);
            streamingShown = true;
        }

        // ── Network ────────────────────────────────────────────────────────
        // Streaming originals (Netflix, Apple TV, Disney+, …) have the same network and streaming
        // service, which drew the same logo twice. If the streaming badge already shows it, skip this.
        if (s.NetworkEnabled && !string.IsNullOrEmpty(item.Network))
        {
            var imgPath = NetworkImage(root, item.Network!, s.NetworkStyle);
            if (!(streamingShown && SameService(item.StreamingService!, streamingImg, item.Network!, imgPath)))
                Add(true, imgPath, imgPath == null ? item.Network : null,
                    new SKColor(20, 20, 20, 220), s.NetworkPos);
        }

        // ── Studio ─────────────────────────────────────────────────────────
        if (s.StudioEnabled && !string.IsNullOrEmpty(item.Studio))
        {
            var imgPath = StudioImage(root, item.Studio!, s.StudioStyle);
            Add(true, imgPath, imgPath == null ? item.Studio : null,
                new SKColor(20, 20, 20, 220), s.StudioPos);
        }

        // ── Awards (corner ribbons) ────────────────────────────────────────
        if (s.OscarWinnerEnabled && item.IsOscarWinner)
            Add(true, RibbonImage(root, "oscars", s.RibbonColour), null, SKColor.Empty, s.OscarWinnerPos, isRibbon: true);
        else if (s.OscarNomineeEnabled && item.IsOscarNominee)
            Add(true, null, "Oscar Nominee", new SKColor(175, 130, 20, 220), s.OscarNomineePos);

        if (s.EmmyWinnerEnabled && item.IsEmmyWinner)
            Add(true, RibbonImage(root, "emmys", s.RibbonColour), null, SKColor.Empty, s.EmmyWinnerPos, isRibbon: true);

        // IMDb Top 250 — Kometa's corner sash. Added after the awards, so an Oscar winner in the same corner
        // keeps the Oscar ribbon (see "one ribbon per corner" below).
        if (s.ImdbTop250Enabled && item.ImdbTop250Rank.HasValue)
            Add(true, RibbonImage(root, "imdb", s.RibbonColour), null, SKColor.Empty, s.ImdbTop250Pos, isRibbon: true);

        // ── Ratings ────────────────────────────────────────────────────────
        if (s.ImdbRatingEnabled && item.ImdbRating.HasValue)
        {
            var imgPath = RatingImage(root, "IMDb");
            Add(true, imgPath, imgPath == null ? $"IMDb {item.ImdbRating:F1}" : $"{item.ImdbRating:F1}",
                new SKColor(175, 130, 20, 220), s.ImdbRatingPos);
        }

        // Rotten Tomatoes → either the icon + score pill, or the Certified Fresh corner ribbon.
        if (s.RottenTomatoesEnabled && item.RottenTomatoesScore.HasValue)
        {
            var fresh = item.RottenTomatoesScore >= 60;
            if (s.RottenTomatoesStyle == "ribbon")
            {
                // The ribbon art is the "Certified Fresh" seal — it carries no percentage, so it
                // only makes sense on a Fresh title. A Rotten one simply gets no ribbon, the same
                // way the award ribbons only appear when the award was actually won.
                if (fresh)
                {
                    // The sash is drawn as a bottom-right triangle, so it only reads correctly in
                    // that corner. If the position is still the factory rating-row default, pin it
                    // there like the award ribbons; a position the user has moved is left alone.
                    var pos = IsDefaultRtPos(s.RottenTomatoesPos)
                        ? new BadgeXY { X = 0.92f, Y = 0.95f }
                        : s.RottenTomatoesPos;
                    Add(true, RibbonImage(root, "rotten", s.RibbonColour), null, SKColor.Empty,
                        pos, isRibbon: true);
                }
            }
            else
            {
                Add(true, RatingImage(root, fresh ? "RT-Crit-Fresh" : "RT-Crit-Rotten"),
                    $"{item.RottenTomatoesScore}%", new SKColor(200, 50, 20, 220), s.RottenTomatoesPos);
            }
        }

        if (s.AudienceScoreEnabled && item.AudienceScore.HasValue)
        {
            var fresh = item.AudienceScore >= 60;
            if (s.AudienceScoreStyle == "ribbon")
            {
                // "Verified Hot" seal — same rules as the RT ribbon: no score on the art, so it
                // only shows for a fresh audience, and it pins to the corner it's drawn for.
                if (fresh)
                {
                    var pos = IsDefaultAudiencePos(s.AudienceScorePos)
                        ? new BadgeXY { X = 0.92f, Y = 0.95f }
                        : s.AudienceScorePos;
                    Add(true, RibbonImage(root, "rottenverified", s.RibbonColour), null, SKColor.Empty,
                        pos, isRibbon: true);
                }
            }
            else
            {
                Add(true, RatingImage(root, fresh ? "RT-Aud-Fresh" : "RT-Aud-Rotten"),
                    $"{item.AudienceScore}%", new SKColor(200, 90, 20, 220), s.AudienceScorePos);
            }
        }

        // ── Language ───────────────────────────────────────────────────────
        if (s.LanguageEnabled && !string.IsNullOrEmpty(item.ContentLanguage))
        {
            var imgPath = FlagImage(root, item.ContentLanguage!, s.FlagStyle);
            Add(true, imgPath, imgPath == null ? item.ContentLanguage!.ToUpper() : null,
                new SKColor(20, 20, 20, 220), s.LanguagePos);
        }

        // ── TV status ──────────────────────────────────────────────────────
        if (s.ShowStatusEnabled && !string.IsNullOrEmpty(item.ShowStatus))
        {
            var (lbl, col) = StatusBadge(item.ShowStatus!);
            Add(true, null, lbl, col, s.ShowStatusPos);
        }

        if (s.EpisodeCountEnabled && item.EpisodeCount.HasValue)
            Add(true, null, $"{item.EpisodeCount} Episodes", new SKColor(20, 20, 20, 220), s.EpisodeCountPos);

        if (s.TrendingEnabled && item.IsTrending)
            Add(true, null, "Trending", new SKColor(200, 90, 20, 220), s.TrendingPos);

        if (s.PopularEnabled && item.IsPopular)
            Add(true, null, "Popular", new SKColor(25, 75, 155, 220), s.PopularPos);

        // ── More ratings / library info ────────────────────────────────────
        if (s.MetacriticEnabled && item.MetacriticScore.HasValue)
        {
            var imgPath = RatingImage(root, "Metacritic");
            Add(true, imgPath, imgPath == null ? $"Metacritic {item.MetacriticScore}" : $"{item.MetacriticScore}",
                new SKColor(40, 40, 40, 220), s.MetacriticPos);
        }

        if (s.LetterboxdEnabled && item.LetterboxdRating.HasValue)
        {
            var imgPath = RatingImage(root, "Letterboxd");
            var score   = item.LetterboxdRating.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            Add(true, imgPath, imgPath == null ? $"Letterboxd {score}" : score, new SKColor(40, 40, 40, 220), s.LetterboxdPos);
        }

        if (s.TraktEnabled && item.TraktRating.HasValue)
        {
            var imgPath = RatingImage(root, "Trakt");
            Add(true, imgPath, imgPath == null ? $"Trakt {item.TraktRating}%" : $"{item.TraktRating}%",
                new SKColor(40, 40, 40, 220), s.TraktPos);
        }

        if (!isSeason && NewBadgeLabel(item, s, DateTime.UtcNow) is { } newLabel)
            Add(true, null, newLabel, new SKColor(22, 150, 90, 225), s.NewBadgePos);

        if (s.VideoSourceEnabled && !string.IsNullOrEmpty(item.VideoSource))
            Add(true, null, item.VideoSource, new SKColor(20, 20, 20, 220), s.VideoSourcePos);

        if (s.RuntimeEnabled && item.MediaType == MediaType.Movie && item.RuntimeMinutes > 0)
            Add(true, null, FormatRuntime(item.RuntimeMinutes!.Value), new SKColor(20, 20, 20, 220), s.RuntimePos);

        if (s.VersionsEnabled && item.VersionCount >= 2)
        {
            var imgPath = RootImage(root, "versions.png");
            Add(true, imgPath, imgPath == null ? $"{item.VersionCount} Versions" : null, new SKColor(20, 20, 20, 220), s.VersionsPos);
        }

        if (s.AudioLanguagesEnabled && item.AudioLanguageCount >= 2)
        {
            var multi   = item.AudioLanguageCount >= 3;
            var imgPath = RootImage(root, multi ? "multi_audio.png" : "dual_audio.png");
            Add(true, imgPath, imgPath == null ? (multi ? "Multi Audio" : "Dual Audio") : null, new SKColor(20, 20, 20, 220), s.AudioLanguagesPos);
        }

        if (s.SubtitleLanguagesEnabled && item.SubtitleLanguageCount >= 2)
        {
            var multi   = item.SubtitleLanguageCount >= 3;
            var imgPath = RootImage(root, multi ? "multi_subs.png" : "dual_subs.png");
            Add(true, imgPath, imgPath == null ? (multi ? "Multi Subs" : "Dual Subs") : null, new SKColor(20, 20, 20, 220), s.SubtitleLanguagesPos);
        }

        // ── One ribbon per corner ──────────────────────────────────────────
        // Ribbons are corner sashes, so two in the same spot draw on top of each other (e.g. an Oscar winner
        // with the Certified Fresh ribbon). Like Kometa, keep only the highest-priority one there: the order
        // they were added above is the priority (awards first, then ratings). A ribbon the user has moved to a
        // different spot is left alone.
        var keptRibbons = new List<RenderedBadge>();
        list.RemoveAll(b =>
        {
            if (!b.IsRibbon) return false;
            if (keptRibbons.Any(k => Math.Abs(k.NormX - b.NormX) < 0.12f && Math.Abs(k.NormY - b.NormY) < 0.12f)) return true;
            keptRibbons.Add(b);
            return false;
        });

        return list;
    }

    // ── Draw a single badge at its absolute position ──────────────────────────
    // Kometa-flavoured backdrop box: uniform HEIGHT for every badge (width hugs the content),
    // semi-transparent black, rounded, black border. Ribbons are drawn as bare corner sashes.
    private void DrawBadge(SKCanvas canvas, RenderedBadge badge, int w, int h, OverlaySettings s)
    {
        float cx = badge.NormX * w;
        float cy = badge.NormY * h;

        float boxH   = Math.Max(h * (s.BadgeSizePct / 100f) * BoxHeightFactor, 14f);
        // Font size comes from Settings > Text Badge Font Size, scaled from a 1500px-tall
        // reference poster so it renders consistently at any artwork size. It used to be derived
        // from the box height (boxH * 0.5), which meant the setting did nothing.
        float fontSz = Math.Max(h / 1500f * s.FontSizePx, 8f);
        boxH = Math.Max(boxH, fontSz * 1.55f);      // keep the bubble tall enough for the text
        float radius = Math.Max(boxH * 0.22f, 4f);
        float padX   = boxH * 0.18f;
        byte  alpha  = (byte)(s.PillOpacity * 255f);

        using var textPaint = new SKPaint
        {
            Color = SKColors.White, IsAntialias = true, TextSize = fontSz,
            Typeface = BadgeTypeface(),             // Kometa's Inter-Medium
            TextAlign = SKTextAlign.Left,
        };

        // Anchor by zone so a badge keeps a consistent edge as its text length changes and never
        // runs off the poster: left side anchors its left edge and expands right, right side
        // anchors its right edge and expands left, the middle stays centred.
        float AnchorX(float boxW)
        {
            float frac = cx / w;
            float x = frac < 0.45f ? cx
                    : frac > 0.55f ? cx - boxW
                    : cx - boxW / 2f;
            return Math.Clamp(x, 0, Math.Max(0, w - boxW));
        }
        using var boxFill   = new SKPaint { Color = new SKColor(0, 0, 0, alpha), IsAntialias = true };

        if (badge.ImagePath != null)
        {
            try
            {
                using var bmp = SKBitmap.Decode(badge.ImagePath);
                if (bmp == null) return;

                // Corner ribbon — bare sash, sized by width.
                if (badge.IsRibbon)
                {
                    float rw = w * (s.BadgeSizePct / 100f);
                    float rh = (float)bmp.Height / bmp.Width * rw;
                    float rx = Math.Clamp(cx - rw / 2f, 0, w - rw);
                    float ry = Math.Clamp(cy - rh / 2f, 0, h - rh);
                    canvas.DrawBitmap(bmp, new SKRect(rx, ry, rx + rw, ry + rh));
                    return;
                }

                // Resolution PNGs carry differing transparent margins, so 4K/DV variants sat
                // further left than 1080P. Measure the opaque bounds and draw only that region so
                // every variant's visible artwork starts at the same left edge.
                var (opaqueL, opaqueR) = OpaqueXBounds(bmp, badge.ImagePath!);
                float srcW = opaqueR - opaqueL + 1;

                float ih = boxH * (badge.HasPill ? 0.48f : 0.70f);   // resolution a touch shorter
                float iw = srcW / bmp.Height * ih;
                float scoreW = badge.Text != null ? textPaint.MeasureText(badge.Text) + boxH * 0.32f : 0f;
                float boxW = iw + scoreW + padX * 2;   // box hugs content
                float bx = AnchorX(boxW);
                float by = Math.Clamp(cy - boxH / 2f, 0, h - boxH);
                var rect = new SKRoundRect(new SKRect(bx, by, bx + boxW, by + boxH), radius);
                canvas.DrawRoundRect(rect, boxFill);

                canvas.DrawBitmap(bmp,
                    new SKRect(opaqueL, 0, opaqueR + 1, bmp.Height),
                    new SKRect(bx + padX, by + (boxH - ih) / 2f, bx + padX + iw, by + (boxH + ih) / 2f));

                if (badge.Text != null && !badge.HasPill)
                {
                    float ty = by + boxH / 2f - (textPaint.FontMetrics.Ascent + textPaint.FontMetrics.Descent) / 2f;
                    canvas.DrawText(badge.Text, bx + padX + iw + boxH * 0.15f, ty, textPaint);
                }
            }
            catch { }
        }
        else if (badge.Text != null)
        {
            // Text-only badge in the same uniform dark box. Resolution (HasPill) is left-anchored
            // like the image variants — otherwise an image-less resolution (e.g. SD, which has no
            // sd.png) would centre on its left position and drift off the left border.
            float tw   = textPaint.MeasureText(badge.Text);
            float boxW = tw + padX * 2;
            float bx = AnchorX(boxW);
            float by = Math.Clamp(cy - boxH / 2f, 0, h - boxH);
            var rect = new SKRoundRect(new SKRect(bx, by, bx + boxW, by + boxH), radius);
            canvas.DrawRoundRect(rect, boxFill);
            float ty = by + boxH / 2f - (textPaint.FontMetrics.Ascent + textPaint.FontMetrics.Descent) / 2f;
            canvas.DrawText(badge.Text, bx + padX, ty, textPaint);
        }
    }

    // Kometa's default overlay font, bundled at wwwroot/fonts so text badges match Kometa's look.
    // Falls back to a system face if the file is missing.
    private static SKTypeface? _badgeTypeface;
    private SKTypeface BadgeTypeface()
    {
        if (_badgeTypeface != null) return _badgeTypeface;
        var p = Path.Combine(WebRoot(), "fonts", "Inter-Medium.ttf");
        _badgeTypeface = (File.Exists(p) ? SKTypeface.FromFile(p) : null)
            ?? SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        return _badgeTypeface;
    }

    // Leftmost/rightmost opaque columns of a badge image, cached per file. Used to normalise the
    // uneven transparent margins baked into the resolution PNGs.
    private static readonly Dictionary<string, (int L, int R)> _opaqueBounds = new();
    private static (int L, int R) OpaqueXBounds(SKBitmap bmp, string key)
    {
        lock (_opaqueBounds)
        {
            if (_opaqueBounds.TryGetValue(key, out var cached)) return cached;
            int left = bmp.Width, right = -1;
            for (int x = 0; x < bmp.Width; x++)
                for (int y = 0; y < bmp.Height; y++)
                    if (bmp.GetPixel(x, y).Alpha > 8)
                    {
                        if (x < left) left = x;
                        if (x > right) right = x;
                        break;
                    }
            if (right < left) { left = 0; right = bmp.Width - 1; }   // fully transparent → use all
            _opaqueBounds[key] = (left, right);
            return (left, right);
        }
    }

    // ── Image lookups ─────────────────────────────────────────────────────────
    private static string? ResolutionImage(string root, string res, string? dynRange)
        => Img(root, "resolution", BuildResKey(res, dynRange) + ".png");

    private static string BuildResKey(string res, string? dynRange)
    {
        var r   = res.ToLower().Trim();
        var dr  = (dynRange ?? "").ToUpper();
        bool isDV   = dr.Contains("DV");
        bool isPlus = dr.Contains("+") || dr.Contains("PLUS");
        bool isHDR  = dr.Contains("HDR");

        string rk = r switch
        {
            "4k" or "2160" or "2160p" or "uhd" => "4k",
            "1080" or "1080p" => "1080p",
            "720"  or "720p"  => "720p",
            "576"  or "576p"  => "576p",
            "480"  or "480p"  => "480p",
            _ => r
        };

        // Matches actual filenames in images/resolution/: {res}{suffix}.png
        // Suffixes available: (none), dv, hdr, plus, dvhdr, dvhdrplus
        string sf = (isDV && isHDR && isPlus) ? "dvhdrplus"
            : (isDV && isHDR)  ? "dvhdr"
            : isDV             ? "dv"
            : (isHDR && isPlus)? "plus"   // HDR10+ — Kometa images file this under "plus", not hdr
            : isHDR            ? "hdr"
            : isPlus           ? "plus"
            : "";

        return sf.Length > 0 ? rk + sf : rk;
    }

    private static string? DynamicRangeImage(string root, string dynRange)
    {
        var dr = dynRange.ToUpper();
        string key = dr.Contains("DV") && dr.Contains("HDR") ? "dvhdr" : dr.Contains("DV") ? "dv" : "hdr";
        return Img(root, "resolution", key + ".png");
    }

    private static string? AudioCodecImage(string root, string codec, string style)
    {
        var folder = style == "compact" ? "compact" : "standard";
        string? file = codec.ToLower() switch
        {
            var c when c.Contains("truehd") && c.Contains("atmos") => "truehd_atmos.png",
            var c when c.Contains("truehd")  => "truehd.png",
            var c when c.Contains("atmos")   => "dolby_atmos.png",
            "eac3" or "e-ac3" or "dd+"       => "plus.png",
            "ac3"  or "dd"                   => "digital.png",
            var c when c.Contains("dtsx")    => "dtsx.png",
            var c when c.Contains("dtses")   => "dtses.png",
            var c when c.Contains("dts-es")  => "dtses.png",
            var c when c.Contains("dts") && c.Contains("ma")  => "ma.png",
            var c when c.Contains("dts") && c.Contains("hra") => "hra.png",
            var c when c.Contains("dts")     => "dts.png",
            "aac"  => "aac.png",  "flac" => "flac.png",
            "mp3"  => "mp3.png",  "opus" => "opus.png",
            "pcm" or "lpcm" => "pcm.png",
            _      => null
        };
        return file == null ? null : Img(root, $"audio_codec/{folder}", file);
    }

    private static string? ContentRatingImage(string root, string rating)
    {
        var key = rating.ToLowerInvariant().Trim().Replace(" ", "");
        if (key.StartsWith("us/")) key = key[3..];
        string? file = key switch
        {
            // USA movie (MPAA)
            "g" => "usg.png", "pg" => "uspg.png", "pg-13" or "pg13" => "uspg-13.png",
            "r" => "usr.png", "nc-17" or "nc17" => "usnc-17.png",
            "nr" or "unrated" or "notrated" => "usnr.png",
            // USA TV (US TV Parental Guidelines) — TV-Y7 uses the TV-Y badge
            "tv-y" or "tvy" or "tv-y7" or "tvy7" => "ustv-y.png",
            "tv-g" or "tvg" => "ustv-g.png",
            "tv-pg" or "tvpg" => "ustv-pg.png",
            "tv-14" or "tv14" => "ustv-14.png",
            "tv-ma" or "tvma" => "ustv-ma.png",
            _ => null
        };
        return file == null ? null : Img(root, "cr", file);
    }

    private static string? EditionImage(string root, string edition)
    {
        string? file = edition.ToLower().Replace(" ", "").Replace("'", "") switch
        {
            var e when e.Contains("director")   => "directors.png",
            var e when e.Contains("extended")   => "extended.png",
            var e when e.Contains("unrated")    => "unrated.png",
            var e when e.Contains("theatrical") => "theatrical.png",
            var e when e.Contains("imax")       => "imax.png",
            var e when e.Contains("ultimate")   => "ultimate.png",
            var e when e.Contains("criterion")  => "criterion.png",
            var e when e.Contains("collector")  => "collector.png",
            var e when e.Contains("remaster")   => "remastered.png",
            var e when e.Contains("definitive") => "definitive.png",
            var e when e.Contains("special")    => "special.png",
            var e when e.Contains("anniversary")=> "anniversary.png",
            var e when e.Contains("alternate")  => "alternate.png",
            var e when e.Contains("final")      => "final.png",
            var e when e.Contains("diamond")    => "diamond.png",
            var e when e.Contains("platinum")   => "platinum.png",
            _ => null
        };
        return file == null ? null : Img(root, "edition", file);
    }

    /// <summary>
    /// Plex's provider names rarely match our asset filenames — it appends reseller/tier suffixes
    /// ("HBO Max Amazon Channel", "Peacock Premium") and spells "+" as "Plus" ("Disney Plus" vs
    /// Disney+.png). Yields candidate filenames best-first so a logo is used whenever one exists
    /// instead of falling back to text.
    /// </summary>
    internal static IEnumerable<string> ProviderCandidates(string raw)
    {
        var n = (raw ?? "").Trim();
        n = Regex.Replace(n, @"\s+(amazon|apple\s*tv|roku|google\s*play)\s+channel\s*$", "", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"\s+(premium|essential|basic|standard|with\s+ads)\s*$", "", RegexOptions.IgnoreCase).Trim();
        if (n.Length == 0) yield break;

        yield return n;                                                   // "Prime Video"
        var plus = Regex.Replace(n, @"\s*plus\b", "+", RegexOptions.IgnoreCase).Trim();
        if (!plus.Equals(n, StringComparison.OrdinalIgnoreCase)) yield return plus;   // "Disney Plus" → "Disney+"
        yield return n + "+";                                             // "Apple TV" → "Apple TV+"
        yield return n.Replace(" ", "");                                  // "AppleTV"

        var key = n.ToLowerInvariant();
        if (key.Contains("prime video") || key.StartsWith("amazon")) { yield return "Prime Video"; yield return "Amazon"; }
        if (key.StartsWith("disney"))    { yield return "Disney+";   yield return "Disney"; }
        if (key.StartsWith("apple"))     { yield return "Apple TV+"; yield return "AppleTV"; }
        if (key.StartsWith("hbo") || key.Contains("max")) { yield return "Max"; yield return "HBO"; }
        if (key.StartsWith("paramount")) yield return "Paramount+";
        if (key.StartsWith("peacock"))   yield return "Peacock";
        if (key.StartsWith("discovery")) yield return "discovery+";

        var first = n.Split(' ')[0];
        if (!first.Equals(n, StringComparison.OrdinalIgnoreCase)) yield return first;
    }

    private static string? FindProviderImage(string root, string folder, string raw)
    {
        foreach (var cand in ProviderCandidates(raw))
        {
            var p = Path.Combine(root, folder, cand + ".png");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// True when a network and a streaming service are the same brand — by name ("Apple TV" vs
    /// "Apple TV+", "Amazon Prime Video" vs "Prime Video") or by the logo each one resolves to.
    /// </summary>
    internal static bool SameService(string streaming, string? streamingImg, string network, string? networkImg)
    {
        if (ServiceKey(streaming) == ServiceKey(network)) return true;
        return streamingImg != null && networkImg != null
            && ServiceKey(Path.GetFileNameWithoutExtension(streamingImg)) == ServiceKey(Path.GetFileNameWithoutExtension(networkImg));
    }

    // Strict brand key: "Disney Plus" = "Disney+", but "Disney Channel" ≠ "Disney+" and
    // "Paramount Network" ≠ "Paramount+" (different brands, both badges should show).
    private static string ServiceKey(string name)
    {
        var n = ProviderCandidates(name).FirstOrDefault() ?? name;   // reseller/tier suffixes stripped
        n = Regex.Replace(n, @"\s*\bplus\b", "+", RegexOptions.IgnoreCase);
        n = Regex.Replace(n.ToLowerInvariant(), @"[^a-z0-9+]", "");
        return n switch
        {
            "appletv+" => "appletv",   // Apple TV+ was renamed Apple TV (2025); TMDB carries both spellings
            "amazon" or "amazonprime" or "amazonprimevideo" => "primevideo",
            _ => n,
        };
    }

    private static string? StreamingImage(string root, string service, string style) =>
        FindProviderImage(root, $"streaming/{(style == "white" ? "white" : "color")}", service);

    private static string? NetworkImage(string root, string network, string style) =>
        FindProviderImage(root, $"network/{(style == "white" ? "white" : "color")}", network);

    private static string? StudioImage(string root, string studio, string style) =>
        FindProviderImage(root, $"studio/{(style == "bigger" ? "bigger" : "standard")}", studio);

    // Match the factory values of RottenTomatoesPos / AudienceScorePos (the bottom rating row).
    // Any factory position the RT / audience badge has had: the current left-column stack, and the older
    // bottom-row defaults (server and browser used slightly different ones) that saved settings may still hold.
    private static bool IsDefaultRtPos(BadgeXY p)
        => IsAt(p, 0.17f, 0.38f) || IsAt(p, 0.42f, 0.95f) || IsAt(p, 0.44f, 0.94f);

    private static bool IsDefaultAudiencePos(BadgeXY p)
        => IsAt(p, 0.17f, 0.46f) || IsAt(p, 0.52f, 0.95f) || IsAt(p, 0.58f, 0.94f);

    private static bool IsAt(BadgeXY p, float x, float y) => Math.Abs(p.X - x) < 0.005f && Math.Abs(p.Y - y) < 0.005f;

    private static string? RibbonImage(string root, string award, string colour)
    {
        var folder = colour is "gray" or "red" or "yellow" ? colour : "black";
        return Img(root, $"ribbon/{folder}", award + ".png");
    }

    private static string? RatingImage(string root, string name) => Img(root, "rating", name + ".png");

    // Badge art that sits in the image set's top folder (versions, dual/multi audio & subtitles).
    private static string? RootImage(string root, string file)
    { var p = Path.Combine(root, file); return File.Exists(p) ? p : null; }

    private static string? FlagImage(string root, string language, string style)
        => Img(root, $"flag/{(style == "square" ? "square" : "round")}", language.ToLower() + ".png");

    private static string? Img(string root, string sub, string file)
    { var p = Path.Combine(root, sub, file); return File.Exists(p) ? p : null; }

    private static string KometaResLabel(string res, string? dynRange)
    {
        bool is4k = res.ToLower() is "4k" or "2160" or "uhd";
        bool is1080 = res is "1080"; bool is720 = res is "720";
        bool isDV = (dynRange??"").ToUpper().Contains("DV");
        bool isHDR= (dynRange??"").ToUpper().Contains("HDR");
        if (is4k   && isDV && isHDR) return "4K-DV-HDR"; if (is4k && isDV) return "4K-DV";
        if (is4k   && isHDR)         return "4K-HDR";     if (is4k)         return "4K";
        if (is1080 && isDV)           return "1080P-DV";   if (is1080&& isHDR) return "1080P-HDR";
        if (is1080)                   return "1080P";
        if (is720  && isHDR)          return "720P-HDR";   if (is720)        return "720P";
        return res.ToUpper();
    }

    private static SKColor ResTextColour(string res, string? dynRange)
    {
        bool is4k = res.ToLower() is "4k" or "2160" or "uhd";
        bool isHDR = (dynRange??"").ToUpper().Contains("HDR");
        bool isDV  = (dynRange??"").ToUpper().Contains("DV");
        if (is4k) return new SKColor(30, 100, 55, 220);
        if (isDV) return new SKColor(110, 35, 155, 220);
        if (isHDR)return new SKColor(25, 75, 155, 220);
        return new SKColor(20, 20, 20, 220);
    }

    private static SKColor StreamingColour(string s) => s.ToLower() switch
    {
        var x when x.Contains("netflix")   => new SKColor(229,  9,  20, 220),
        var x when x.Contains("disney")    => new SKColor( 17, 60, 166, 220),
        var x when x.Contains("hbo")       => new SKColor( 90, 30, 140, 220),
        var x when x.Contains("apple")     => new SKColor( 50, 50,  50, 220),
        var x when x.Contains("prime")     => new SKColor(  0,168, 225, 220),
        var x when x.Contains("hulu")      => new SKColor( 28,231, 131, 220),
        _ => new SKColor(20, 130, 130, 220)
    };

    private static (string L, SKColor C) StatusBadge(string status) => status.ToLower() switch
    {
        var s when s.Contains("return")     => ("Returning",     new SKColor(30, 100,  55, 220)),
        var s when s.Contains("ended")      => ("Ended",         new SKColor(20,  20,  20, 220)),
        var s when s.Contains("cancel")     => ("Cancelled",     new SKColor(185, 30,  30, 220)),
        var s when s.Contains("production") => ("In Production", new SKColor(25,  75, 155, 220)),
        _                                    => (status,          new SKColor(20,  20,  20, 220))
    };
}
