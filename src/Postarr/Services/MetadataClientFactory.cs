using Postarr.Data;
using Postarr.Jellyfin;
using Postarr.MediaServers;
using Postarr.Models;
using Postarr.Metadata;
using Postarr.Plex;

namespace Postarr.Services;

public class MetadataClientFactory
{
    private readonly SettingsRepository  _settings;
    private readonly IHttpClientFactory  _http;
    private readonly ILogger<MetadataClientFactory> _logger;

    public MetadataClientFactory(SettingsRepository settings, IHttpClientFactory http, ILogger<MetadataClientFactory> logger)
    { _settings = settings; _http = http; _logger = logger; }

    /// <summary>
    /// The active server's canonical name ("Plex", "Jellyfin" or "Emby"). Used both for display and as the
    /// ServerType stamped on library items/collections, so lists only show the active server's.
    /// </summary>
    public static string ServerKey(AppSettings s) =>
        string.Equals(s.MediaServerType, "Jellyfin", StringComparison.OrdinalIgnoreCase) ? "Jellyfin"
      : string.Equals(s.MediaServerType, "Emby",     StringComparison.OrdinalIgnoreCase) ? "Emby"
      : "Plex";

    /// <summary>Display name of the configured media server ("Plex", "Jellyfin" or "Emby").</summary>
    public string MediaServerName => ServerKey(_settings.Get());

    /// <summary>The configured media server, or null if it isn't set up yet.</summary>
    public IMediaServerClient? BuildMediaServerClient()
    {
        var s = _settings.Get();
        switch (ServerKey(s))
        {
            case "Jellyfin":
                if (string.IsNullOrWhiteSpace(s.JellyfinBaseUrl) || string.IsNullOrWhiteSpace(s.JellyfinApiKey)) return null;
                return new JellyfinClient(_http.CreateClient("jellyfin"), s.JellyfinBaseUrl, s.JellyfinApiKey);
            case "Emby":   // Emby's API is Jellyfin's ancestor; the same client runs in Emby mode
                if (string.IsNullOrWhiteSpace(s.EmbyBaseUrl) || string.IsNullOrWhiteSpace(s.EmbyApiKey)) return null;
                return new JellyfinClient(_http.CreateClient("jellyfin"), s.EmbyBaseUrl, s.EmbyApiKey, emby: true);
        }
        if (string.IsNullOrWhiteSpace(s.PlexBaseUrl) || string.IsNullOrWhiteSpace(s.PlexToken)) return null;
        return new PlexClient(_http.CreateClient("plex"), s.PlexBaseUrl, s.PlexToken);
    }

    public TmdbClient?   BuildTmdbClient()   { var k = _settings.Get().TmdbApiKey;   return string.IsNullOrWhiteSpace(k) ? null : new TmdbClient(_http.CreateClient("tmdb"), k); }
    public FanArtClient? BuildFanArtClient() { var k = _settings.Get().FanArtApiKey; return string.IsNullOrWhiteSpace(k) ? null : new FanArtClient(_http.CreateClient("fanart"), k, _logger); }
    public TvdbClient?   BuildTvdbClient()   { var k = _settings.Get().TvdbApiKey;   return string.IsNullOrWhiteSpace(k) ? null : new TvdbClient(_http.CreateClient("tvdb"), k); }
    public OmdbClient?   BuildOmdbClient()   { var k = _settings.Get().OmdbApiKey;   return string.IsNullOrWhiteSpace(k) ? null : new OmdbClient(_http.CreateClient("omdb"), k); }
    public TraktClient?  BuildTraktClient()  { var k = _settings.Get().TraktClientId; return string.IsNullOrWhiteSpace(k) ? null : new TraktClient(_http.CreateClient("trakt"), k); }
    public MdbListClient? BuildMdbListClient() { var k = _settings.Get().MdbListApiKey; return string.IsNullOrWhiteSpace(k) ? null : new MdbListClient(_http.CreateClient("mdblist"), k); }

    public PosterAggregator BuildPosterAggregator() =>
        new PosterAggregator(BuildTmdbClient(), BuildFanArtClient(), BuildTvdbClient());
}
