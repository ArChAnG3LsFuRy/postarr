using Postarr.Models;

namespace Postarr.Services;

/// <summary>
/// Where Postarr may download artwork from. Image URLs reach the server from the browser (the poster picker sends the
/// one you clicked), so without this check anyone who can use Postarr could make it fetch any address — including
/// devices on the local network (server-side request forgery). Only the artwork providers Postarr uses, Kometa's
/// GitHub images and the configured media servers are allowed.
/// </summary>
public static class ImageSources
{
    private static readonly string[] HostSuffixes = { "themoviedb.org", "tmdb.org", "fanart.tv", "thetvdb.com" };
    private static readonly string[] ExactHosts   = { "raw.githubusercontent.com" };

    public static bool IsAllowedRemote(string? url, AppSettings settings)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            return false;
        var host = u.Host;
        if (HostSuffixes.Any(s => host.Equals(s, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (ExactHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)))
            return true;
        // The user's own media servers (their images are fetched with the server's address, e.g. Jellyfin item images).
        foreach (var server in new[] { settings.PlexBaseUrl, settings.JellyfinBaseUrl, settings.EmbyBaseUrl })
            if (Uri.TryCreate(server, UriKind.Absolute, out var s) && s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == u.Port)
                return true;
        return false;
    }

    /// <summary>Throws a readable error when an artwork URL isn't from an allowed source.</summary>
    public static void EnsureAllowed(string? url, AppSettings settings)
    {
        if (!IsAllowedRemote(url, settings))
            throw new InvalidOperationException(
                "Postarr only downloads artwork from TMDB, FanArt.tv, TheTVDB, Kometa's GitHub images or your media server.");
    }
}
