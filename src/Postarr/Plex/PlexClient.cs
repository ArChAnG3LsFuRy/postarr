using System.Net.Http.Headers;
using System.Xml.Linq;
using Postarr.MediaServers;
using Postarr.Models;

namespace Postarr.Plex;

public class PlexSection
{
    public string Key   { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Type  { get; set; } = string.Empty;
}

public class PlexMediaItem
{
    public string    RatingKey         { get; set; } = string.Empty;
    public string    Title             { get; set; } = string.Empty;
    public int?      Year              { get; set; }
    public MediaType MediaType         { get; set; }
    public string?   TmdbId            { get; set; }
    public string?   TvdbId            { get; set; }
    public string?   ImdbId            { get; set; }
    public string?   VideoResolution   { get; set; }
    public string?   VideoDynamicRange { get; set; }
    public string?   AudioCodec        { get; set; }
    public string?   Edition           { get; set; }
    public string?   Studio            { get; set; }
    public string?   Network           { get; set; }
    public string?   ContentRating     { get; set; }
    public string?   ThumbPath         { get; set; }
    public string?   ArtPath           { get; set; }
    public DateTime? AddedAtUtc        { get; set; }
    public DateTime? LatestSeasonAddedUtc { get; set; }   // shows: newest (non-special) season's added date
    public int?      RuntimeMinutes    { get; set; }
    public int?      VersionCount      { get; set; }
    public string?   VideoSource       { get; set; }
    public int?      AudioLanguageCount    { get; set; }
    public int?      SubtitleLanguageCount { get; set; }
    // Ratings the server already holds (from the item-details fetch). ServerRatingsKnown = that fetch
    // succeeded for this item, so a null score really means "the server has none".
    public bool      ServerRatingsKnown    { get; set; }
    public double?   ServerImdbRating      { get; set; }
    public int?      ServerRottenTomatoes  { get; set; }
    public int?      ServerAudienceScore   { get; set; }
    public List<PlexSeason> Seasons    { get; set; } = new();
}

public class PlexCollectionItem
{
    public string    RatingKey    { get; set; } = string.Empty;
    public string    Title        { get; set; } = string.Empty;
    public int       ItemCount    { get; set; }
    public string?   ThumbPath    { get; set; }
    public string    SectionKey   { get; set; } = string.Empty;
    public MediaType MediaType    { get; set; }
    /// <summary>TMDB collection ID when the server already knows it (Jellyfin does; Plex doesn't).</summary>
    public string?   TmdbId       { get; set; }
}

public class PlexSeason
{
    public string RatingKey   { get; set; } = string.Empty;
    public int    SeasonNumber{ get; set; }
    public string Title       { get; set; } = string.Empty;
    public int?   LeafCount   { get; set; }  // episode count
    public string? ThumbPath  { get; set; }
    public DateTime? AddedAtUtc { get; set; }
}

/// <summary>An image the server already holds for an item (key, whether it's the selected one, source).</summary>
public record PlexPoster(string Key, bool Selected, string? Provider);

public class PlexClient : IMediaServerClient
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;
    private readonly string     _token;

    public PlexClient(HttpClient http, string baseUrl, string token)
    {
        _http    = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _token   = token;
    }

    private string Sign(string path) =>
        $"{_baseUrl}{path}{(path.Contains('?') ? "&" : "?")}X-Plex-Token={Uri.EscapeDataString(_token)}";

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try { return (await _http.GetAsync(Sign("/identity"), ct)).IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<List<PlexSection>> GetLibrarySectionsAsync(CancellationToken ct = default)
    {
        var xml = await GetXmlAsync("/library/sections", ct);
        var list = new List<PlexSection>();
        foreach (var d in xml.Descendants("Directory"))
        {
            var type = d.Attribute("type")?.Value ?? "";
            if (type != "movie" && type != "show") continue;
            list.Add(new PlexSection { Key = d.Attribute("key")?.Value ?? "", Title = d.Attribute("title")?.Value ?? "", Type = type });
        }
        return list;
    }

    public async Task<List<PlexMediaItem>> GetSectionItemsAsync(PlexSection section, CancellationToken ct = default)
    {
        var xml   = await GetXmlAsync($"/library/sections/{section.Key}/all?includeGuids=1&includeExtras=0", ct);
        var items = new List<PlexMediaItem>();
        var node  = section.Type == "movie" ? "Video" : "Directory";

        foreach (var el in xml.Descendants(node))
        {
            if (el.Attribute("ratingKey") == null) continue;
            var item = new PlexMediaItem
            {
                RatingKey  = el.Attribute("ratingKey")!.Value,
                Title      = el.Attribute("title")?.Value ?? "",
                Year       = int.TryParse(el.Attribute("year")?.Value, out var y) ? y : null,
                MediaType  = section.Type == "movie" ? MediaType.Movie : MediaType.Show,
                Studio     = el.Attribute("studio")?.Value,
                Network    = el.Attribute("network")?.Value,
                Edition    = el.Attribute("editionTitle")?.Value,
                ThumbPath  = el.Attribute("thumb")?.Value,
                ArtPath    = el.Attribute("art")?.Value,
                AddedAtUtc = FromUnix(el.Attribute("addedAt")?.Value),
            };
            ExtractGuids(el, item);
            ExtractMedia(el, item);
            if (section.Type == "movie")
            {
                // Each version of a movie is its own <Media>; the listing carries them, with each file path.
                var medias = el.Elements("Media").ToList();
                item.VersionCount = medias.Count > 0 ? medias.Count : null;
                if (long.TryParse(el.Attribute("duration")?.Value, out var ms) && ms > 0)
                    item.RuntimeMinutes = (int)Math.Round(ms / 60000.0);
                item.VideoSource = MediaFileInfo.DetectSource(medias.FirstOrDefault()?.Element("Part")?.Attribute("file")?.Value);
            }
            if (section.Type == "show")
            {
                item.Seasons = await GetShowSeasonsAsync(item.RatingKey, ct);
                item.LatestSeasonAddedUtc = item.Seasons.Where(s => s.SeasonNumber > 0).Max(s => s.AddedAtUtc);
            }
            items.Add(item);
        }
        return items;
    }

    private async Task<List<PlexSeason>> GetShowSeasonsAsync(string showKey, CancellationToken ct)
    {
        var xml     = await GetXmlAsync($"/library/metadata/{showKey}/children", ct);
        var seasons = new List<PlexSeason>();
        foreach (var n in xml.Descendants("Directory"))
        {
            if (!int.TryParse(n.Attribute("index")?.Value, out var num)) continue;
            seasons.Add(new PlexSeason
            {
                RatingKey    = n.Attribute("ratingKey")?.Value ?? "",
                SeasonNumber = num,
                Title        = n.Attribute("title")?.Value ?? $"Season {num}",
                LeafCount    = int.TryParse(n.Attribute("leafCount")?.Value, out var lc) ? lc : null,
                ThumbPath    = n.Attribute("thumb")?.Value,
                AddedAtUtc   = FromUnix(n.Attribute("addedAt")?.Value),
            });
        }
        return seasons;
    }

    public async Task<List<PlexCollectionItem>> GetCollectionsAsync(PlexSection section, CancellationToken ct = default)
    {
        var xml   = await GetXmlAsync($"/library/sections/{section.Key}/collections", ct);
        var items = new List<PlexCollectionItem>();
        foreach (var el in xml.Descendants("Directory"))
        {
            if (el.Attribute("ratingKey") == null) continue;
            items.Add(new PlexCollectionItem
            {
                RatingKey  = el.Attribute("ratingKey")!.Value,
                Title      = el.Attribute("title")?.Value ?? "",
                ItemCount  = int.TryParse(el.Attribute("childCount")?.Value, out var cc) ? cc : 0,
                ThumbPath  = el.Attribute("thumb")?.Value,
                SectionKey = section.Key,
                MediaType  = section.Type == "movie" ? Models.MediaType.Movie : Models.MediaType.Show,
            });
        }
        return items;
    }

    private static DateTime? FromUnix(string? seconds) =>
        long.TryParse(seconds, out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;

    private static void ExtractGuids(XElement el, PlexMediaItem item)
    {
        // 1. Modern <Guid id="tmdb://123"/> child elements
        foreach (var g in el.Elements("Guid"))
        {
            var id = g.Attribute("id")?.Value ?? "";
            if (id.StartsWith("tmdb://"))      item.TmdbId = id["tmdb://".Length..];
            else if (id.StartsWith("tvdb://")) item.TvdbId = id["tvdb://".Length..];
            else if (id.StartsWith("imdb://")) item.ImdbId = id["imdb://".Length..];
        }

        // 2. Legacy guid attribute e.g. com.plexapp.agents.themoviedb://603?lang=en
        var legacy = el.Attribute("guid")?.Value;
        if (legacy != null)
        {
            if (item.TmdbId == null) { var m = System.Text.RegularExpressions.Regex.Match(legacy, @"themoviedb://(\d+)"); if (m.Success) item.TmdbId = m.Groups[1].Value; }
            if (item.TvdbId == null) { var m = System.Text.RegularExpressions.Regex.Match(legacy, @"thetvdb://(\d+)");   if (m.Success) item.TvdbId = m.Groups[1].Value; }
            if (item.ImdbId == null) { var m = System.Text.RegularExpressions.Regex.Match(legacy, @"imdb://(tt\d+)");    if (m.Success) item.ImdbId = m.Groups[1].Value; }
        }

        // 3. Extract IDs from Plex folder/file name convention:
        //    e.g. "The Last Stand (2024) [imdb-tt15682498] [tmdb-1411773]"
        //    or   "House of the Dragon [tvdb-371572]"
        var folderName = el.Attribute("title")?.Value ?? "";
        if (item.ImdbId == null) { var m = System.Text.RegularExpressions.Regex.Match(folderName, @"\[imdb-(tt\d+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase); if (m.Success) item.ImdbId = m.Groups[1].Value; }
        if (item.TmdbId == null) { var m = System.Text.RegularExpressions.Regex.Match(folderName, @"\[tmdb-(\d+)\]",   System.Text.RegularExpressions.RegexOptions.IgnoreCase); if (m.Success) item.TmdbId = m.Groups[1].Value; }
        if (item.TvdbId == null) { var m = System.Text.RegularExpressions.Regex.Match(folderName, @"\[tvdb-(\d+)\]",   System.Text.RegularExpressions.RegexOptions.IgnoreCase); if (m.Success) item.TvdbId = m.Groups[1].Value; }
    }

    private static void ExtractMedia(XElement el, PlexMediaItem item)
    {
        item.ContentRating = el.Attribute("contentRating")?.Value;
        var media = el.Element("Media");
        if (media == null) return;

        // Plex's videoResolution attribute is always a simple value: "4k", "1080", "720", "576", "480", "sd"
        item.VideoResolution = media.Attribute("videoResolution")?.Value;
        item.AudioCodec      = media.Attribute("audioCodec")?.Value;

        // HDR variant detection: Plex's own videoDynamicRange attribute on <Media> reports
        // "SDR" or "HDR" only — it does NOT distinguish HDR10 vs HDR10+ vs Dolby Vision.
        // To get the real variant we have to inspect the <Part><Stream type="1"> (video stream)
        // for colorTrc (transfer function) and DOVIPresent (Dolby Vision flag).
        var videoStream = media.Element("Part")?.Elements("Stream")
            .FirstOrDefault(s => s.Attribute("streamType")?.Value == "1");

        bool isDoVi    = videoStream?.Attribute("DOVIPresent")?.Value == "1";
        var colorTrc   = videoStream?.Attribute("colorTrc")?.Value ?? "";
        var dovBlCompat= videoStream?.Attribute("DOVIBLCompatID")?.Value;

        // HDR10+ specifically signals via DOVIBLCompatID or a colorTrc of "smpte2084" combined
        // with HDR10+ metadata; Plex doesn't expose a dedicated "HDR10+" flag directly, so the
        // most reliable signal available via the API is colorTrc=smpte2084 (HDR10/PQ) vs hlg (HLG).
        bool isHdr10Plus = videoStream?.Attribute("DOVIPresent")?.Value == "0" &&
                            colorTrc.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) &&
                            videoStream?.Attribute("title")?.Value?.Contains("+") == true;

        string dynamicRange;
        if (isDoVi && (colorTrc.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) || isHdr10Plus))
            dynamicRange = "DV-HDR";       // Dolby Vision profile that also carries HDR10 base layer
        else if (isDoVi)
            dynamicRange = "DV";
        else if (isHdr10Plus)
            dynamicRange = "HDR10+";
        else if (colorTrc.Equals("smpte2084", StringComparison.OrdinalIgnoreCase))
            dynamicRange = "HDR";          // standard HDR10
        else if (colorTrc.Equals("hlg", StringComparison.OrdinalIgnoreCase))
            dynamicRange = "HDR";          // HLG treated as HDR for badge purposes
        else
        {
            // Fall back to Plex's own summary attribute if stream-level detail isn't available
            var vdr = media.Attribute("videoDynamicRange")?.Value;
            dynamicRange = string.IsNullOrEmpty(vdr) || vdr.Equals("SDR", StringComparison.OrdinalIgnoreCase)
                ? "SDR" : vdr;
        }

        item.VideoDynamicRange = dynamicRange;
    }

    // ── Show-level quality (derived from episodes) ────────────────────────────
    // A show's top-level Plex element carries no <Media>, so it has no resolution, dynamic
    // range or audio codec of its own. We aggregate its episodes (/allLeaves) and report the
    // most common value for each — the value that represents the bulk of the show —
    // tie-breaking resolution toward the higher one. Used so show and season posters can carry
    // resolution / audio-codec badges just like movies.
    public async Task<(string? Resolution, string? DynamicRange, string? AudioCodec)> GetShowAggregateQualityAsync(string showKey, CancellationToken ct = default)
    {
        var xml     = await GetXmlAsync($"/library/metadata/{showKey}/allLeaves", ct);
        var resList = new List<string>();
        var acList  = new List<string>();
        var epKeys  = new List<string>();
        foreach (var ep in xml.Descendants("Video"))
        {
            var tmp = new PlexMediaItem();
            ExtractMedia(ep, tmp);
            if (!string.IsNullOrEmpty(tmp.VideoResolution))   resList.Add(tmp.VideoResolution!);
            if (!string.IsNullOrEmpty(tmp.AudioCodec))        acList.Add(tmp.AudioCodec!);
            var k = ep.Attribute("ratingKey")?.Value;
            if (!string.IsNullOrEmpty(k)) epKeys.Add(k!);
        }
        // Dynamic range (HDR/DV) lives only in per-stream colour data, which /allLeaves omits —
        // it must be fetched from each episode's full metadata (batched by rating key).
        var drList = (await GetStreamDetailsAsync(epKeys, ct)).Values
            .Select(d => d.DynamicRange).Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToList();

        static int ResRank(string r) => r.ToLowerInvariant() switch
        {
            "4k" or "2160" or "2160p" or "uhd" => 5,
            "1080" or "1080p"                  => 4,
            "720"  or "720p"                   => 3,
            "576"  or "576p"                   => 2,
            "480"  or "480p"                   => 1,
            _                                   => 0
        };
        static string? Mode(List<string> xs) => xs.Count == 0 ? null
            : xs.GroupBy(x => x)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => ResRank(g.Key))
                .First().Key;

        return (Mode(resList), Mode(drList), Mode(acList));
    }

    // ── Stream-level dynamic range (HDR / Dolby Vision) ───────────────────────
    // Plex's bulk listing endpoints (/sections/../all, /allLeaves) do NOT include the
    // <Part><Stream> colour data, and many servers don't populate <Media videoDynamicRange>
    // either — so scanned items always look "SDR". The only reliable signal is the video
    // stream's colorTrc / DOVIPresent, which is present when you fetch an item's own metadata.
    // Plex accepts a comma-separated list of rating keys, so we fetch them in batches to keep
    // the request count low (e.g. ~32 requests for 1500+ movies). The same metadata carries each
    // stream's language, so the audio/subtitle language counts come from here too, at no extra cost —
    // and Plex's own IMDb / Rotten Tomatoes scores (<Rating> elements), for movies and shows alike.
    public async Task<Dictionary<string, StreamDetails>> GetStreamDetailsAsync(
        IEnumerable<string> ratingKeys, CancellationToken ct = default)
    {
        var result = new Dictionary<string, StreamDetails>();
        var keys   = ratingKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
        const int batchSize = 50;
        for (int i = 0; i < keys.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = keys.GetRange(i, Math.Min(batchSize, keys.Count - i));
            XElement xml;
            try { xml = await GetXmlAsync($"/library/metadata/{string.Join(",", slice)}", ct); }
            catch { continue; } // a bad key in the batch shouldn't sink the rest of the scan
            // Movies/episodes come back as <Video>, shows as <Directory> (no streams, but they do have ratings).
            foreach (var v in xml.Elements().Where(e => e.Name == "Video" || e.Name == "Directory"))
            {
                var key = v.Attribute("ratingKey")?.Value;
                if (string.IsNullOrEmpty(key)) continue;
                var tmp = new PlexMediaItem();
                ExtractMedia(v, tmp); // reads stream colorTrc / DOVIPresent → real DR
                // Languages from the first version's streams (streamType 2 = audio, 3 = subtitle).
                var streams = v.Element("Media")?.Element("Part")?.Elements("Stream").ToList() ?? new();
                static string? Lang(XElement s) =>
                    s.Attribute("languageCode")?.Value ?? s.Attribute("languageTag")?.Value ?? s.Attribute("language")?.Value;
                var audio = streams.Where(s => s.Attribute("streamType")?.Value == "2").Select(Lang);
                var subs  = streams.Where(s => s.Attribute("streamType")?.Value == "3").Select(Lang);
                var (imdb, rt, aud) = ParseRatings(v);
                result[key!] = new StreamDetails(
                    string.IsNullOrEmpty(tmp.VideoDynamicRange) ? null : tmp.VideoDynamicRange,
                    MediaFileInfo.CountLanguages(audio, streams.Count > 0),
                    MediaFileInfo.CountLanguages(subs,  streams.Count > 0),
                    imdb, rt, aud);
            }
        }
        return result;
    }

    // Plex's agents store several scores per item, e.g.
    //   <Rating image="imdb://image.rating" value="7.5" type="audience"/>
    //   <Rating image="rottentomatoes://image.rating.ripe" value="9.3" type="critic"/>      (93%)
    //   <Rating image="rottentomatoes://image.rating.upright" value="8.1" type="audience"/> (81%)
    // Rotten Tomatoes values are out of 10, so ×10 gives the percentage the badges show.
    private static (double? Imdb, int? RottenTomatoes, int? Audience) ParseRatings(XElement item)
    {
        double? imdb = null; int? rt = null, aud = null;
        foreach (var r in item.Elements("Rating"))
        {
            if (!double.TryParse(r.Attribute("value")?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0) continue;
            var image = r.Attribute("image")?.Value ?? "";
            var type  = r.Attribute("type")?.Value;
            if (image.StartsWith("imdb://"))                                   imdb = Math.Round(v, 1);
            else if (image.StartsWith("rottentomatoes://") && type == "critic")   rt  = (int)Math.Round(v * 10);
            else if (image.StartsWith("rottentomatoes://") && type == "audience") aud = (int)Math.Round(v * 10);
        }
        return (imdb, rt, aud);
    }

    // ── Upload ──────────────────────────────────────────────────────────────

    public Task UploadPosterAsync(string ratingKey, byte[] bytes, string contentType, CancellationToken ct = default) =>
        UploadImageAsync($"/library/metadata/{ratingKey}/posters", bytes, contentType, ct);

    public Task UploadBackgroundAsync(string ratingKey, byte[] bytes, string contentType, CancellationToken ct = default) =>
        UploadImageAsync($"/library/metadata/{ratingKey}/arts", bytes, contentType, ct);

    private async Task UploadImageAsync(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var resp = await _http.PostAsync(Sign(path), content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Surface Plex's own error text instead of a bare status code, so "Apply All" can tell
            // the user *why* an upload failed (e.g. an unreadable image or a stale ratingKey).
            var body = await resp.Content.ReadAsStringAsync(ct);
            var reason = string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body.Trim();
            throw new HttpRequestException($"Plex rejected the image upload ({(int)resp.StatusCode}): {reason}");
        }
    }

    // ── Existing Plex artwork (incl. the user's custom uploads) ───────────────

    /// <summary>
    /// Lists the posters Plex already holds for an item/collection — including any custom artwork
    /// the user uploaded in Plex, which the metadata-agent search never returns.
    /// </summary>
    public Task<List<PlexPoster>> GetAvailablePostersAsync(string ratingKey, CancellationToken ct = default)
        => GetAvailableImagesAsync(ratingKey, "posters", ct);

    /// <summary>Same as <see cref="GetAvailablePostersAsync"/> but for background art (Plex's /arts).</summary>
    public Task<List<PlexPoster>> GetAvailableArtsAsync(string ratingKey, CancellationToken ct = default)
        => GetAvailableImagesAsync(ratingKey, "arts", ct);

    private async Task<List<PlexPoster>> GetAvailableImagesAsync(string ratingKey, string kind, CancellationToken ct)
    {
        var xml  = await GetXmlAsync($"/library/metadata/{ratingKey}/{kind}", ct);
        var list = new List<PlexPoster>();
        foreach (var p in xml.Elements("Photo"))
        {
            var key = p.Attribute("key")?.Value;
            if (string.IsNullOrEmpty(key)) continue;
            // Skip Plex's auto-generated collection collage.
            if (key!.Contains("/composite/")) continue;
            var provider = p.Attribute("provider")?.Value;
            // Only the user's OWN uploads. Metadata-agent images (provider=tmdb/gracenote) are what
            // the search already returns, and many have remote https keys that can't be proxied
            // through Plex — including them just produced duplicates and broken tiles.
            if (!string.IsNullOrEmpty(provider) && !provider.Equals("local", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new PlexPoster(key, p.Attribute("selected")?.Value == "1", provider));
        }
        return list;
    }

    /// <summary>Selects an existing Plex poster (no upload — the image is already in Plex).</summary>
    public Task SelectPosterAsync(string ratingKey, string selectValue, CancellationToken ct = default)
        => SelectImageAsync(ratingKey, "poster", selectValue, ct);

    /// <summary>Selects an existing Plex background/art.</summary>
    public Task SelectArtAsync(string ratingKey, string selectValue, CancellationToken ct = default)
        => SelectImageAsync(ratingKey, "art", selectValue, ct);

    private async Task SelectImageAsync(string ratingKey, string kind, string selectValue, CancellationToken ct)
    {
        // NB: the value must be the poster's ratingKey (e.g. "upload://posters/xxx"), NOT the
        // "/library/.../file?url=..." key — see PosterSelectValue.
        var url = Sign($"/library/metadata/{ratingKey}/{kind}?url={Uri.EscapeDataString(selectValue)}");
        (await _http.PutAsync(url, null, ct)).EnsureSuccessStatusCode();
    }

    // The proxy URL that lets the browser display a Plex image (which needs the token).
    public static string ImageProxyUrl(string key) => $"/api/images/plex?path={Uri.EscapeDataString(key)}";

    // Recover the raw Plex key (/library/.../file?url=…) from a proxy URL or a raw key.
    public static string? PosterKeyFromRef(string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl)) return null;
        if (imageUrl!.StartsWith("/library/")) return imageUrl;
        var i = imageUrl.IndexOf("path=", StringComparison.Ordinal);
        if (i < 0) return null;
        var enc = imageUrl[(i + 5)..];
        var amp = enc.IndexOf('&'); if (amp >= 0) enc = enc[..amp];
        return Uri.UnescapeDataString(enc);
    }

    // The value the select PUT wants: the decoded url= param of the key (e.g. "upload://posters/xxx").
    public static string PosterSelectValue(string key)
    {
        var i = key.IndexOf("url=", StringComparison.Ordinal);
        if (i < 0) return key;
        var v = key[(i + 4)..];
        var amp = v.IndexOf('&'); if (amp >= 0) v = v[..amp];
        return Uri.UnescapeDataString(v);
    }

    /// <summary>Fetches an image straight from Plex (signed with the token) for the display proxy.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> FetchImageAsync(string plexPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plexPath) || !plexPath.StartsWith("/library/")) return null;
        var r = await _http.GetAsync(Sign(plexPath), ct);
        if (!r.IsSuccessStatusCode) return null;
        var bytes = await r.Content.ReadAsByteArrayAsync(ct);
        return (bytes, r.Content.Headers.ContentType?.ToString() ?? "image/jpeg");
    }

    // ── Download ────────────────────────────────────────────────────────────

    public async Task<byte[]?> DownloadCurrentPosterAsync(string? thumbPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(thumbPath)) return null;
        var r = await _http.GetAsync(Sign(thumbPath), ct);
        return r.IsSuccessStatusCode ? await r.Content.ReadAsByteArrayAsync(ct) : null;
    }

    public async Task<byte[]?> DownloadCurrentBackgroundAsync(string? artPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(artPath)) return null;
        var r = await _http.GetAsync(Sign(artPath), ct);
        return r.IsSuccessStatusCode ? await r.Content.ReadAsByteArrayAsync(ct) : null;
    }

    private async Task<XElement> GetXmlAsync(string path, CancellationToken ct)
    {
        var r = await _http.GetAsync(Sign(path), ct);
        r.EnsureSuccessStatusCode();
        return XElement.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    // ── Collections Postarr builds ───────────────────────────────────────────
    // The same calls python-plexapi (and so Kometa) makes. Items are referenced by a "server://" URI naming this
    // server's machine identifier; keys are sent in batches so the URL stays a sensible length.

    private const int CollectionBatch = 100;
    private string? _machineId;

    private async Task<string> ItemsUriAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        _machineId ??= (await GetXmlAsync("/", ct)).Attribute("machineIdentifier")?.Value
                       ?? throw new InvalidOperationException("Plex didn't report its machine identifier.");
        return Uri.EscapeDataString(
            $"server://{_machineId}/com.plexapp.plugins.library/library/metadata/{string.Join(",", keys)}");
    }

    private async Task<XElement?> SendXmlAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var req  = new HttpRequestMessage(method, Sign(path));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Plex refused {method} {path.Split('?')[0]} ({(int)resp.StatusCode}): {body.Trim()}");
        }
        var text = await resp.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : XElement.Parse(text);
    }

    public async Task<string> CreateCollectionAsync(PlexSection section, string title, IReadOnlyList<string> itemKeys, CancellationToken ct = default)
    {
        if (itemKeys.Count == 0) throw new ArgumentException("A collection needs at least one item.", nameof(itemKeys));
        var type = section.Type == "show" ? 2 : 1;
        var uri  = await ItemsUriAsync(itemKeys.Take(CollectionBatch), ct);
        var xml  = await SendXmlAsync(HttpMethod.Post,
            $"/library/collections?type={type}&title={Uri.EscapeDataString(title)}&smart=0&sectionId={section.Key}&uri={uri}", ct);
        var id = xml?.Descendants("Directory").FirstOrDefault()?.Attribute("ratingKey")?.Value
                 ?? throw new InvalidOperationException($"Plex created '{title}' but didn't return its id.");
        if (itemKeys.Count > CollectionBatch) await AddToCollectionAsync(id, itemKeys.Skip(CollectionBatch).ToList(), ct);
        return id;
    }

    public async Task<List<string>?> GetCollectionItemKeysAsync(string collectionId, CancellationToken ct = default)
    {
        var r = await _http.GetAsync(Sign($"/library/collections/{collectionId}/children"), ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        var xml = XElement.Parse(await r.Content.ReadAsStringAsync(ct));
        return xml.Elements().Select(e => e.Attribute("ratingKey")?.Value).OfType<string>().ToList();
    }

    public async Task AddToCollectionAsync(string collectionId, IReadOnlyList<string> itemKeys, CancellationToken ct = default)
    {
        foreach (var chunk in itemKeys.Chunk(CollectionBatch))
            await SendXmlAsync(HttpMethod.Put, $"/library/collections/{collectionId}/items?uri={await ItemsUriAsync(chunk, ct)}", ct);
    }

    public async Task RemoveFromCollectionAsync(string collectionId, IReadOnlyList<string> itemKeys, CancellationToken ct = default)
    {
        foreach (var key in itemKeys)   // Plex removes one item per call
            await SendXmlAsync(HttpMethod.Delete, $"/library/collections/{collectionId}/items/{key}", ct);
    }

    public async Task RenameCollectionAsync(PlexSection section, string collectionId, string title, CancellationToken ct = default)
    {
        // Type 18 = collection. Locking the title stops Plex's agents renaming it back.
        await SendXmlAsync(HttpMethod.Put,
            $"/library/sections/{section.Key}/all?type=18&id={collectionId}&title.value={Uri.EscapeDataString(title)}&title.locked=1", ct);
    }

    // collectionSort: 0 = release date, 1 = alphabetical, 2 = custom (drag-to-order). Custom order is then set by
    // moving each item after the one before it — the calls python-plexapi's sortUpdate/moveItem make.
    public async Task<bool> SetCollectionOrderAsync(PlexSection section, string collectionId, string mode, IReadOnlyList<string> orderedKeys, CancellationToken ct = default)
    {
        if (mode != "list")   // release date is also Plex's own default
        {
            await SendXmlAsync(HttpMethod.Put, $"/library/metadata/{collectionId}/prefs?collectionSort=0", ct);
            return true;
        }
        await SendXmlAsync(HttpMethod.Put, $"/library/metadata/{collectionId}/prefs?collectionSort=2", ct);
        string? previous = null;
        foreach (var key in orderedKeys)
        {
            await SendXmlAsync(HttpMethod.Put,
                $"/library/collections/{collectionId}/items/{key}/move" + (previous != null ? $"?after={previous}" : ""), ct);
            previous = key;
        }
        return true;
    }

    public async Task DeleteCollectionAsync(string collectionId, CancellationToken ct = default)
    {
        // Guard: only ever delete something Plex itself says is a collection.
        var r = await _http.GetAsync(Sign($"/library/metadata/{collectionId}"), ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return;   // already gone
        r.EnsureSuccessStatusCode();
        var type = XElement.Parse(await r.Content.ReadAsStringAsync(ct)).Elements().FirstOrDefault()?.Attribute("type")?.Value;
        if (type != "collection")
            throw new InvalidOperationException($"Refusing to delete Plex item {collectionId}: it is a '{type}', not a collection.");
        await SendXmlAsync(HttpMethod.Delete, $"/library/collections/{collectionId}", ct);
    }
}
