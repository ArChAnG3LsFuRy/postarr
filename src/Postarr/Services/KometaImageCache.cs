using System.Collections.Concurrent;

namespace Postarr.Services;

/// <summary>
/// Local copies of the Kometa default images Postarr has used, kept in the data folder (kometa-cache/).
///
/// Kometa's artwork has no licence, so Postarr never ships it: each install downloads only the images it actually
/// uses, from Kometa's public GitHub repo, the first time it needs them — exactly as before. From then on the local
/// copy is used, so a file Kometa later moves or deletes, or a GitHub outage, can't change a collection's poster.
/// </summary>
public class KometaImageCache
{
    public const string RawBase = "https://raw.githubusercontent.com/Kometa-Team/Default-Images/master/";

    public enum Lookup { Found, Missing, Unreachable }

    private readonly IHttpClientFactory _http;
    private readonly string _dir;
    private readonly ILogger<KometaImageCache> _logger;
    // Paths GitHub said don't exist (404), and when — asked again after a day in case Kometa adds them.
    private readonly ConcurrentDictionary<string, DateTime> _missing = new();
    private static readonly TimeSpan MissingRecheck = TimeSpan.FromDays(1);

    public KometaImageCache(IHttpClientFactory http, string dir, ILogger<KometaImageCache> logger)
    { _http = http; _dir = dir; _logger = logger; }

    /// <summary>The raw GitHub URL for a path in Kometa's repo — still what Postarr records as the poster's identity.</summary>
    public static string UrlFor(string path) => RawBase + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    public static bool IsKometaUrl(string? url) => url != null && url.StartsWith(RawBase, StringComparison.OrdinalIgnoreCase);

    private static string? PathFromUrl(string url) =>
        IsKometaUrl(url) ? string.Join("/", url[RawBase.Length..].Split('/').Select(Uri.UnescapeDataString)) : null;

    private string? LocalPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        return Path.Combine(new[] { _dir }.Concat(parts).ToArray());
    }

    /// <summary>
    /// Makes sure the image is on disk: uses the local copy, otherwise downloads it once. Missing = GitHub says it
    /// doesn't exist; Unreachable = GitHub couldn't be asked (outage, timeout) — callers keep what they have then.
    /// </summary>
    public async Task<Lookup> EnsureAsync(string path, CancellationToken ct)
    {
        var local = LocalPath(path);
        if (local == null) return Lookup.Missing;
        if (File.Exists(local)) return Lookup.Found;
        if (_missing.TryGetValue(path, out var when) && DateTime.UtcNow - when < MissingRecheck) return Lookup.Missing;
        try
        {
            using var resp = await _http.CreateClient("imagedownload").GetAsync(UrlFor(path), ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { _missing[path] = DateTime.UtcNow; return Lookup.Missing; }
            if (!resp.IsSuccessStatusCode) return Lookup.Unreachable;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 1024) return Lookup.Unreachable;   // an error page, not an image
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var tmp = local + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, local, overwrite: true);
            _missing.TryRemove(path, out _);
            return Lookup.Found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Kometa image {Path} couldn't be fetched", path);
            return Lookup.Unreachable;
        }
    }

    /// <summary>The image bytes for a Kometa URL (from the local copy, downloading it first if needed), or null.</summary>
    public async Task<byte[]?> ReadAsync(string url, CancellationToken ct)
    {
        var path = PathFromUrl(url);
        if (path == null || await EnsureAsync(path, ct) != Lookup.Found) return null;
        return await File.ReadAllBytesAsync(LocalPath(path)!, ct);
    }
}
