using Postarr.Models;

namespace Postarr.Metadata;

public class PosterAggregator
{
    private readonly TmdbClient?   _tmdb;
    private readonly FanArtClient? _fanArt;
    private readonly TvdbClient?   _tvdb;

    public PosterAggregator(TmdbClient? tmdb, FanArtClient? fanArt, TvdbClient? tvdb)
    { _tmdb = tmdb; _fanArt = fanArt; _tvdb = tvdb; }

    public async Task<List<PosterCandidate>> GetMoviePosterCandidatesAsync(
        string? tmdbId, string? tvdbId, CancellationToken ct = default)
    {
        var all = new List<PosterCandidate>();
        if (_tmdb   != null && tmdbId != null) all.AddRange(await Safe(() => _tmdb.GetMoviePostersAsync(tmdbId, ct)));
        if (_fanArt != null && tmdbId != null) all.AddRange(await Safe(() => _fanArt.GetMoviePostersAsync(tmdbId, ct)));
        if (_tvdb   != null && tvdbId != null) all.AddRange(await Safe(() => _tvdb.GetMoviePostersAsync(tvdbId, ct)));
        return Rank(all);
    }

    public async Task<List<PosterCandidate>> GetShowPosterCandidatesAsync(
        string? tmdbId, string? tvdbId, CancellationToken ct = default)
    {
        var all = new List<PosterCandidate>();
        if (_tmdb   != null && tmdbId != null) all.AddRange(await Safe(() => _tmdb.GetShowPostersAsync(tmdbId, ct)));
        if (_fanArt != null && tvdbId != null) all.AddRange(await Safe(() => _fanArt.GetShowPostersAsync(tvdbId, ct)));
        if (_tvdb   != null && tvdbId != null) all.AddRange(await Safe(() => _tvdb.GetShowPostersAsync(tvdbId, ct)));
        return Rank(all);
    }

    public async Task<List<PosterCandidate>> GetSeasonPosterCandidatesAsync(
        string? tmdbId, string? tvdbId, int season, CancellationToken ct = default)
    {
        var all = new List<PosterCandidate>();
        if (_tmdb   != null && tmdbId != null) all.AddRange(await Safe(() => _tmdb.GetSeasonPostersAsync(tmdbId, season, ct)));
        if (_fanArt != null && tvdbId != null) all.AddRange(await Safe(() => _fanArt.GetSeasonPostersAsync(tvdbId, season, ct)));
        if (_tvdb   != null && tvdbId != null)
        {
            var seasonId = await SafeVal(() => _tvdb.GetSeasonIdAsync(tvdbId, season, ct));
            if (seasonId != null) all.AddRange(await Safe(() => _tvdb.GetSeasonPostersAsync(seasonId, ct)));
        }
        return Rank(all);
    }

    // ── Backgrounds ─────────────────────────────────────────────────────────

    public async Task<List<BackgroundCandidate>> GetMovieBackgroundCandidatesAsync(
        string? tmdbId, string? tvdbId, CancellationToken ct = default)
    {
        var all = new List<BackgroundCandidate>();
        if (_tmdb   != null && tmdbId != null) all.AddRange(await SafeBg(() => _tmdb.GetMovieBackgroundsAsync(tmdbId, ct)));
        if (_fanArt != null && tmdbId != null) all.AddRange(await SafeBg(() => _fanArt.GetMovieBackgroundsAsync(tmdbId, ct)));
        if (_tvdb   != null && tvdbId != null) all.AddRange(await SafeBg(() => _tvdb.GetMovieBackgroundsAsync(tvdbId, ct)));
        return all.OrderByDescending(b => b.Likes ?? 0).ThenByDescending(b => b.VoteAverage ?? 0).ToList();
    }

    public async Task<List<BackgroundCandidate>> GetShowBackgroundCandidatesAsync(
        string? tmdbId, string? tvdbId, CancellationToken ct = default)
    {
        var all = new List<BackgroundCandidate>();
        if (_tmdb   != null && tmdbId != null) all.AddRange(await SafeBg(() => _tmdb.GetShowBackgroundsAsync(tmdbId, ct)));
        if (_fanArt != null && tvdbId != null) all.AddRange(await SafeBg(() => _fanArt.GetShowBackgroundsAsync(tvdbId, ct)));
        if (_tvdb   != null && tvdbId != null) all.AddRange(await SafeBg(() => _tvdb.GetShowBackgroundsAsync(tvdbId, ct)));
        return all.OrderByDescending(b => b.Likes ?? 0).ThenByDescending(b => b.VoteAverage ?? 0).ToList();
    }

    private static List<PosterCandidate> Rank(List<PosterCandidate> list) =>
        list.OrderByDescending(c => c.IsTextless).ThenByDescending(c => c.Likes ?? 0)
            .ThenByDescending(c => c.VoteAverage ?? 0).ToList();

    private static async Task<List<PosterCandidate>> Safe(Func<Task<List<PosterCandidate>>> f)
    { try { return await f(); } catch { return new(); } }

    private static async Task<List<BackgroundCandidate>> SafeBg(Func<Task<List<BackgroundCandidate>>> f)
    { try { return await f(); } catch { return new(); } }

    private static async Task<string?> SafeVal(Func<Task<string?>> f)
    { try { return await f(); } catch { return null; } }
}
