using SkiaSharp;

namespace Postarr.Services;

/// <summary>
/// Downloads remote poster/background images, resizes them to a small thumbnail, and caches
/// the result on disk. Grid cards display ~180px-wide posters, but the source CDN images are
/// often 1000–2000px wide, so fetching and serving a resized, locally-cached copy makes the
/// grid load far faster (smaller payloads, served from localhost, cached across sessions).
/// </summary>
public class ImageCacheService
{
    private readonly IHttpClientFactory _http;
    private readonly string _cacheDir;

    // Only images from the metadata providers we actually pull artwork from are proxied.
    // Any other host is redirected to the original URL (never fetched server-side), which
    // keeps this from becoming an open SSRF proxy.
    private static readonly string[] AllowedHostSuffixes =
        { "fanart.tv", "themoviedb.org", "tmdb.org", "thetvdb.com" };

    public ImageCacheService(IHttpClientFactory http, string cacheDir)
    {
        _http = http;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
        && AllowedHostSuffixes.Any(s =>
            u.Host.Equals(s, StringComparison.OrdinalIgnoreCase) ||
            u.Host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));

    private string PathFor(string url, int width)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{url}|{width}")));
        return Path.Combine(_cacheDir, $"{hash}.jpg");
    }

    /// <summary>
    /// Returns the path to a cached JPEG thumbnail for <paramref name="url"/> at the given
    /// width, creating (fetching + resizing) it on first request. Returns null on failure so
    /// the caller can fall back to the original URL.
    /// </summary>
    public async Task<string?> GetThumbnailAsync(string url, int width, CancellationToken ct)
    {
        var path = PathFor(url, width);
        if (File.Exists(path)) return path;

        try
        {
            var bytes = await _http.CreateClient("imagedownload").GetByteArrayAsync(url, ct);
            using var src = SKBitmap.Decode(bytes);
            if (src == null || src.Width == 0) return null;

            int w = Math.Min(width, src.Width);   // never upscale past the source
            int h = (int)Math.Round((double)src.Height / src.Width * w);

            using var resized = src.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
            if (resized == null) return null;
            using var img  = SKImage.FromBitmap(resized);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 82);

            // Write to a temp file then move, so a concurrent request never reads a half-written file.
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, data.ToArray(), ct);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
