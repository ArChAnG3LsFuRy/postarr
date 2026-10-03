using Postarr.Models;

namespace Postarr.Services;

/// <summary>
/// FanArt.tv season-poster sets. FanArt.tv's API doesn't name uploaders, but a set is uploaded in one go, so its posters
/// have neighbouring ids: same language, nearest id, close enough to have been in the same upload (the window widens
/// with the season gap, and when two languages were uploaded together). Checked on The Simpsons, Game of Thrones,
/// Breaking Bad and Stranger Things. Shared by the season picker's "Set" tags, "Match the other seasons", and the
/// scan's "Prefer matching season sets" — so all three always agree.
/// </summary>
public static class SeasonSetMatcher
{
    public static string Lang(PosterCandidate p) => string.IsNullOrEmpty(p.Language) ? "00" : p.Language;

    public static string BareUrl(string? u) => (u ?? "").Replace("https://", "").Replace("http://", "").TrimEnd('/');

    /// <summary>The poster from the same set as <paramref name="chosen"/> for another season, or null.</summary>
    public static PosterCandidate? MatchFor(List<(int Season, PosterCandidate Poster)> all, PosterCandidate chosen,
                                            int chosenSeason, int targetSeason)
    {
        if (targetSeason == chosenSeason) return chosen;
        if (!int.TryParse(chosen.ExternalId, out var chosenId)) return null;
        var window = 25 + 10 * Math.Abs(targetSeason - chosenSeason);
        return all.Where(x => x.Season == targetSeason && Lang(x.Poster) == Lang(chosen))
                  .Select(x => (x.Poster, Gap: int.TryParse(x.Poster.ExternalId, out var id) ? Math.Abs(id - chosenId) : int.MaxValue))
                  .Where(x => x.Gap <= window)
                  .OrderBy(x => x.Gap).Select(x => x.Poster).FirstOrDefault();
    }

    /// <summary>
    /// The set that covers the most of <paramref name="seasons"/> (at least two), as an anchor poster + its season.
    /// Ties go to textless (when preferred), then English/textless over other languages, then FanArt.tv likes.
    /// </summary>
    public static (PosterCandidate Poster, int Season)? BestSet(List<(int Season, PosterCandidate Poster)> all,
                                                                IReadOnlyCollection<int> seasons, bool preferTextless)
    {
        (PosterCandidate Poster, int Season)? best = null;
        (int Cover, int Textless, int Lang, int Likes) bestScore = (0, 0, 0, 0);
        foreach (var (season, poster) in all.Where(x => seasons.Contains(x.Season) && int.TryParse(x.Poster.ExternalId, out _)))
        {
            var cover = seasons.Count(s => MatchFor(all, poster, season, s) != null);
            if (cover < 2) continue;
            var score = (cover, preferTextless && Lang(poster) == "00" ? 1 : 0, Lang(poster) is "en" or "00" ? 1 : 0, poster.Likes ?? 0);
            if (best == null || score.CompareTo(bestScore) > 0) { best = (poster, season); bestScore = score; }
        }
        return best;
    }
}
