using Postarr.Services;
using Microsoft.AspNetCore.Mvc;

namespace Postarr.Controllers;

[ApiController]
[Route("api/images")]
public class ImagesController : ControllerBase
{
    private readonly ImageCacheService     _cache;
    private readonly MetadataClientFactory _clients;
    public ImagesController(ImageCacheService cache, MetadataClientFactory clients)
    { _cache = cache; _clients = clients; }

    /// <summary>
    /// Proxies an image from the configured media server (signed with its token/key, which the browser
    /// doesn't have). Used to display the server's existing artwork. The path is restricted to image
    /// paths — Plex's /library/… or Jellyfin's /Items/{id}/Images/… — so the credential can't be used
    /// to fetch anything else. (The route keeps its original "plex" name so stored URLs keep working.)
    /// </summary>
    [HttpGet("plex")]
    public async Task<IActionResult> Plex([FromQuery] string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("..")) return BadRequest();
        if (!path.StartsWith("/library/") && !Jellyfin.JellyfinClient.IsImagePath(path)) return BadRequest();
        var plex = _clients.BuildMediaServerClient();
        if (plex == null) return NotFound();
        var img = await plex.FetchImageAsync(path, ct);
        if (img == null) return NotFound();
        // Raster images only: an SVG or HTML answer would otherwise run as a page on Postarr's own address.
        if (!RasterTypes.Contains(img.Value.ContentType.Split(';')[0].Trim().ToLowerInvariant())) return StatusCode(415);
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(img.Value.Bytes, img.Value.ContentType);
    }

    /// <summary>
    /// Returns a small, disk-cached JPEG thumbnail of a remote poster/background so grid cards
    /// load quickly. Falls back to a redirect to the original URL when the host isn't proxied
    /// or the fetch/resize fails, so images always display either way.
    /// </summary>
    [HttpGet("thumb")]
    public async Task<IActionResult> Thumb([FromQuery] string url, [FromQuery] int w = 400, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return BadRequest();
        // Not proxied → let the browser load it directly, but only same-site paths or known image hosts
        // (an open redirect to any address could be used in phishing links that look like they're from here).
        if (!_cache.IsAllowed(url)) return SafeRedirect(url);

        w = Math.Clamp(w, 64, 1000);
        var path = await _cache.GetThumbnailAsync(url, w, ct);
        if (path == null) return SafeRedirect(url);         // fetch/resize failed → original still shows

        // Thumbnail for a given (url,width) never changes, so let the browser cache it hard.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return PhysicalFile(path, "image/jpeg");
    }

    private static readonly HashSet<string> RasterTypes = new() { "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif", "image/bmp", "image/avif" };

    // A path on this site: one leading "/", then no second slash or backslash, and no whitespace or control characters
    // anywhere (browsers drop tabs and newlines, so "/<tab>/evil.example" would become "//evil.example").
    private static readonly System.Text.RegularExpressions.Regex SameSitePath =
        new("^/(?![/\\\\])[^\\s\\\\\\x00-\\x1f]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private IActionResult SafeRedirect(string url)
    {
        var sameSite = SameSitePath.IsMatch(url);
        return sameSite || _cache.IsAllowed(url) || Services.KometaImageCache.IsKometaUrl(url) ? Redirect(url) : NotFound();
    }
}
