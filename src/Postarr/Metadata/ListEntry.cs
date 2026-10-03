using System.Text.RegularExpressions;

namespace Postarr.Metadata;

/// <summary>One title in an external or typed list, in list order. Any of the identifiers may be missing.</summary>
public record ListEntry(string? ImdbId, string? TmdbId, string? Title, int? Year, bool? IsShow = null);

/// <summary>A public list from a community source (MDBList / Trakt) as shown in the "browse community lists" picker.</summary>
public record CommunityList(int Id, string Name, string? UserName, string? Slug, int Items, int Likes, string? Description, string? Url);

public static class ListEntryParser
{
    /// <summary>
    /// Titles typed in by the user, one per line: "Iron Man (2008)", "tmdb:1726" or "imdb:tt0371746".
    /// Blank lines and lines starting with # are ignored. List order is kept.
    /// </summary>
    public static List<ListEntry> ParseTitles(string? text)
    {
        var result = new List<ListEntry>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            line = Regex.Replace(line, @"^\s*\d+[\.\)]\s+", "");   // "12. Iron Man" / "12) Iron Man" numbering

            var imdb = Regex.Match(line, @"^imdb\s*[:=]\s*(tt\d+)\s*(#.*)?$", RegexOptions.IgnoreCase);
            if (imdb.Success) { var (t, y0) = FromComment(imdb.Groups[2].Value); result.Add(new ListEntry(imdb.Groups[1].Value, null, t, y0)); continue; }
            var tmdb = Regex.Match(line, @"^tmdb\s*[:=]\s*(\d+)\s*(#.*)?$", RegexOptions.IgnoreCase);
            if (tmdb.Success) { var (t, y0) = FromComment(tmdb.Groups[2].Value); result.Add(new ListEntry(null, tmdb.Groups[1].Value, t, y0)); continue; }
            var bare = Regex.Match(line, @"^(tt\d{6,})$");
            if (bare.Success) { result.Add(new ListEntry(bare.Groups[1].Value, null, null, null)); continue; }

            int? year = null;
            var y = Regex.Match(line, @"\s*[\(\[](\d{4})[\)\]]\s*$");
            if (y.Success) { year = int.Parse(y.Groups[1].Value); line = line[..y.Index].Trim(); }
            if (line.Length > 0) result.Add(new ListEntry(null, null, line, year));
        }
        return result;
    }

    /// <summary>The "# Title (Year)" note after an id: used to name the entry (and as a fallback match if the id isn't found).</summary>
    private static (string? Title, int? Year) FromComment(string comment)
    {
        var c = comment.TrimStart('#', ' ', '	').Trim();
        if (c.Length == 0) return (null, null);
        var y = Regex.Match(c, @"\s*[\(\[](\d{4})[\)\]]\s*$");
        return y.Success ? (c[..y.Index].Trim(), int.Parse(y.Groups[1].Value)) : (c, null);
    }

    /// <summary>
    /// Turns what the user pasted for an MDBList list into an API path: a numeric id, or
    /// https://mdblist.com/lists/{user}/{list}. Null if it isn't recognisable.
    /// </summary>
    public static string? MdbListPath(string? listRef)
    {
        var r = (listRef ?? "").Trim();
        if (r.Length == 0) return null;
        if (Regex.IsMatch(r, @"^\d+$")) return $"/lists/{r}";
        var m = Regex.Match(r, @"mdblist\.com/lists/([^/?#\s]+)/([^/?#\s]+)", RegexOptions.IgnoreCase);
        if (m.Success && !m.Groups[1].Value.Equals("official", StringComparison.OrdinalIgnoreCase))
            return $"/lists/{Uri.EscapeDataString(m.Groups[1].Value)}/{Uri.EscapeDataString(m.Groups[2].Value)}";
        var short2 = Regex.Match(r, @"^([\w\-\.]+)/([\w\-\.]+)$");   // "username/list-name"
        return short2.Success ? $"/lists/{Uri.EscapeDataString(short2.Groups[1].Value)}/{Uri.EscapeDataString(short2.Groups[2].Value)}" : null;
    }

    /// <summary>
    /// A Trakt list as an API path: https://trakt.tv/users/{user}/lists/{list}, "user/list", or a numeric list id.
    /// Null if it isn't recognisable.
    /// </summary>
    public static string? TraktPath(string? listRef)
    {
        var r = (listRef ?? "").Trim();
        if (r.Length == 0) return null;
        var m = Regex.Match(r, @"trakt\.tv/users/([^/?#\s]+)/lists/([^/?#\s]+)", RegexOptions.IgnoreCase);
        if (m.Success) return $"/users/{Uri.EscapeDataString(m.Groups[1].Value)}/lists/{Uri.EscapeDataString(m.Groups[2].Value)}";
        var byId = Regex.Match(r, @"^(?:https?://(?:www\.)?trakt\.tv/lists/)?(\d+)(?:[/?#].*)?$", RegexOptions.IgnoreCase);
        if (byId.Success) return $"/lists/{byId.Groups[1].Value}";
        var pair = Regex.Match(r, @"^([\w\-\.]+)/([\w\-\.]+)$");
        return pair.Success ? $"/users/{Uri.EscapeDataString(pair.Groups[1].Value)}/lists/{Uri.EscapeDataString(pair.Groups[2].Value)}" : null;
    }

    /// <summary>The list id from https://www.themoviedb.org/list/{id}[-name], a bare id, or null.</summary>
    public static string? TmdbListId(string? listRef)
    {
        var r = (listRef ?? "").Trim();
        var m = Regex.Match(r, @"themoviedb\.org/list/([0-9a-zA-Z]+)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(r, @"^[0-9a-zA-Z]{1,40}$") ? r : null;
    }
}
