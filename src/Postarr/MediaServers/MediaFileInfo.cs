using System.Text.RegularExpressions;

namespace Postarr.MediaServers;

/// <summary>
/// Per-item details from a server's full item metadata (Plex needs a second fetch for these): stream facts,
/// plus the IMDb / Rotten Tomatoes scores the server already holds (Plex only; null when it has none).
/// </summary>
public record StreamDetails(string? DynamicRange, int? AudioLanguages, int? SubtitleLanguages,
                            double? ImdbRating = null, int? RottenTomatoes = null, int? AudienceScore = null);

/// <summary>
/// Server-neutral helpers for the file-derived badges, so Plex, Jellyfin and Emby classify identically.
/// </summary>
public static class MediaFileInfo
{
    // Checked in order: a "BluRay REMUX" is a REMUX, a "BDRemux" too.
    private static readonly (string Label, Regex Pattern)[] Sources =
    {
        ("REMUX",   new Regex(@"remux",                                                RegexOptions.IgnoreCase)),
        ("BLU-RAY", new Regex(@"blu-?ray|\bbd(rip|25|50|66|100)?\b|\bbrrip\b|\buhd[ ._-]?bd", RegexOptions.IgnoreCase)),
        // Plain "web" must look like a release tag, or "Charlotte's Web.mkv" would count as a WEB release.
        ("WEB",     new Regex(@"\bweb[ ._-]?(dl|rip)\b|\d{3,4}p[ ._-]web\b|\bweb[ ._-](h\.?26[45]|x26[45]|hevc|avc)\b|\b(amzn|dsnp|hmax|atvp|pcok|pmtp)\b",
                              RegexOptions.IgnoreCase)),
        ("HDTV",    new Regex(@"\b(hdtv|pdtv|dsr|tvrip)\b",                            RegexOptions.IgnoreCase)),
        ("DVD",     new Regex(@"\bdvd(rip|5|9|r)?\b",                                  RegexOptions.IgnoreCase)),
    };

    /// <summary>"REMUX", "BLU-RAY", "WEB", "HDTV" or "DVD" from the file/folder name; null when it doesn't say.</summary>
    public static string? DetectSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Only the file name and its folder — not the whole library path, which could contain anything.
        var name = Path.GetFileName(path) + " " + Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        foreach (var (label, rx) in Sources)
            if (rx.IsMatch(name)) return label;
        return null;
    }

    /// <summary>Number of distinct, known languages (blank / "und" / "unknown" ignored); null if there were no streams.</summary>
    public static int? CountLanguages(IEnumerable<string?> languages, bool anyStreams)
    {
        if (!anyStreams) return null;
        return languages
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!.Trim().ToLowerInvariant())
            .Where(l => l is not ("und" or "unk" or "unknown" or "zxx" or "mis"))
            .Distinct()
            .Count();
    }
}
