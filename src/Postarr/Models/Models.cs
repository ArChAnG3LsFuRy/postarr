namespace Postarr.Models;

public enum MediaType { Movie, Show, Season }

public enum PosterSource { Tmdb, FanArt, Tvdb, Plex, Local, Generated, Kometa }

public enum ApplyMode { AutoApply, ReviewQueue }

// ─── Overlay types (each maps to a tickbox in the UI) ───────────────────────

/// <summary>
/// Stores the normalised position (0.0–1.0) of a single badge on the poster.
/// Using fractions means the position scales correctly to any poster resolution.
/// </summary>
public class BadgeXY
{
    public float X { get; set; } = 0.88f;  // fraction of poster width
    public float Y { get; set; } = 0.92f;  // fraction of poster height
}

public class OverlaySettings
{
    // ── Quality ──────────────────────────────────────────────────────────────
    public bool ResolutionEnabled       { get; set; } = true;
    public bool DynamicRangeEnabled     { get; set; } = true;
    public bool AudioCodecEnabled       { get; set; } = false;
    public bool ContentRatingEnabled    { get; set; } = false;
    public bool EditionEnabled          { get; set; } = false;
    public bool LanguageEnabled         { get; set; } = false;

    // ── Ratings ──────────────────────────────────────────────────────────────
    public bool ImdbRatingEnabled       { get; set; } = false;
    public bool RottenTomatoesEnabled   { get; set; } = false;
    public bool AudienceScoreEnabled    { get; set; } = false;

    // ── Awards ───────────────────────────────────────────────────────────────
    public bool OscarWinnerEnabled      { get; set; } = false;
    public bool OscarNomineeEnabled     { get; set; } = false;
    public bool EmmyWinnerEnabled       { get; set; } = false;

    // ── Streaming / Network / Studio ──────────────────────────────────────
    public bool StreamingServiceEnabled { get; set; } = false;
    public bool NetworkEnabled          { get; set; } = false;
    public bool StudioEnabled           { get; set; } = false;

    // ── TV-show status ────────────────────────────────────────────────────
    public bool ShowStatusEnabled       { get; set; } = false;
    public bool EpisodeCountEnabled     { get; set; } = false;

    // ── Discovery ─────────────────────────────────────────────────────────
    public bool TrendingEnabled         { get; set; } = false;
    public bool PopularEnabled          { get; set; } = false;
    // ── More ratings / library info ──────────────────────────────────────
    public bool MetacriticEnabled       { get; set; } = false;
    public bool NewBadgeEnabled         { get; set; } = false;
    public int  NewBadgeDays            { get; set; } = 14;    // NEW / NEW SEASON shows for this many days
    public bool VideoSourceEnabled      { get; set; } = false;
    public bool RuntimeEnabled          { get; set; } = false;
    public bool VersionsEnabled         { get; set; } = false;
    public bool AudioLanguagesEnabled   { get; set; } = false;  // dual / multi audio
    public bool SubtitleLanguagesEnabled{ get; set; } = false;  // dual / multi subtitles
    public bool LetterboxdEnabled       { get; set; } = false;  // MDBList
    public bool TraktEnabled            { get; set; } = false;  // MDBList
    public bool ImdbTop250Enabled       { get; set; } = false;  // corner ribbon, list via MDBList

    // ── Scope ───────────────────────────────────────────────────────────────
    // When true, the enabled badges above are also baked onto TV-show *season*
    // posters (using the parent show's metadata, matching how the season poster
    // is pushed to Plex). When false, season posters are applied clean, with no
    // overlays. Movie/show posters are unaffected by this flag.
    public bool SeasonPosterOverlaysEnabled { get; set; } = true;

    // ── Per-badge positions (normalised 0–1) ─────────────────────────────
    // Kometa default positions, researched from Kometa's overlay YAML defaults:
    // resolution=bottom-left, audio_codec=top-center, streaming/network=bottom-left,
    // ratings=bottom-center, ribbon(awards)=bottom-right, status=top-left,
    // episode_info=bottom-right. Badges sharing a corner are pre-spaced vertically.
    public BadgeXY ResolutionPos        { get; set; } = new() { X = 0.04f, Y = 0.05f };  // x = left edge (left-anchored)
    public BadgeXY DynamicRangePos      { get; set; } = new() { X = 0.18f, Y = 0.88f };
    public BadgeXY AudioCodecPos        { get; set; } = new() { X = 0.50f, Y = 0.06f };
    public BadgeXY ContentRatingPos     { get; set; } = new() { X = 0.92f, Y = 0.06f };
    public BadgeXY EditionPos           { get; set; } = new() { X = 0.50f, Y = 0.13f };
    public BadgeXY LanguagePos          { get; set; } = new() { X = 0.92f, Y = 0.13f };
    // Ratings stack down the left side (Kometa's default) — a single bottom row can't fit six score boxes.
    public BadgeXY ImdbRatingPos        { get; set; } = new() { X = 0.17f, Y = 0.30f };
    public BadgeXY RottenTomatoesPos    { get; set; } = new() { X = 0.17f, Y = 0.38f };
    public BadgeXY AudienceScorePos     { get; set; } = new() { X = 0.17f, Y = 0.46f };
    public BadgeXY OscarWinnerPos       { get; set; } = new() { X = 0.92f, Y = 0.95f };
    public BadgeXY OscarNomineePos      { get; set; } = new() { X = 0.92f, Y = 0.88f };
    public BadgeXY EmmyWinnerPos        { get; set; } = new() { X = 0.92f, Y = 0.81f };
    public BadgeXY StreamingServicePos  { get; set; } = new() { X = 0.18f, Y = 0.95f };
    public BadgeXY NetworkPos           { get; set; } = new() { X = 0.18f, Y = 0.88f };
    public BadgeXY StudioPos            { get; set; } = new() { X = 0.18f, Y = 0.81f };
    public BadgeXY ShowStatusPos        { get; set; } = new() { X = 0.18f, Y = 0.06f };
    public BadgeXY EpisodeCountPos      { get; set; } = new() { X = 0.92f, Y = 0.95f };
    public BadgeXY TrendingPos          { get; set; } = new() { X = 0.18f, Y = 0.13f };
    public BadgeXY PopularPos           { get; set; } = new() { X = 0.18f, Y = 0.20f };
    public BadgeXY MetacriticPos        { get; set; } = new() { X = 0.17f, Y = 0.54f };
    public BadgeXY NewBadgePos          { get; set; } = new() { X = 0.50f, Y = 0.21f };
    public BadgeXY VideoSourcePos       { get; set; } = new() { X = 0.83f, Y = 0.21f };
    public BadgeXY RuntimePos           { get; set; } = new() { X = 0.17f, Y = 0.21f };
    public BadgeXY VersionsPos          { get; set; } = new() { X = 0.83f, Y = 0.29f };
    public BadgeXY AudioLanguagesPos    { get; set; } = new() { X = 0.83f, Y = 0.37f };
    public BadgeXY SubtitleLanguagesPos { get; set; } = new() { X = 0.83f, Y = 0.45f };
    public BadgeXY LetterboxdPos        { get; set; } = new() { X = 0.17f, Y = 0.62f };
    public BadgeXY TraktPos             { get; set; } = new() { X = 0.17f, Y = 0.70f };
    public BadgeXY ImdbTop250Pos        { get; set; } = new() { X = 0.92f, Y = 0.95f };  // bottom-right sash, like the awards

    // ── Global style ──────────────────────────────────────────────────────
    public int    FontSizePx            { get; set; } = 28;
    // Badge image size as a percentage of poster width (10–50, default 22).
    // 22% matches the reference screenshot — roughly the same visual weight
    // as Kometa's default overlay sizing on a standard 1000px poster.
    public int    BadgeSizePct          { get; set; } = 22;
    // Opacity of the dark pill background behind badge images (0.0–1.0, default 0.78).
    // The badge PNG image is always drawn at full opacity on top — only the dark
    // background box behind it changes transparency.
    public float  PillOpacity           { get; set; } = 0.78f;
    public string AudioCodecStyle       { get; set; } = "standard";
    public string StreamingStyle        { get; set; } = "color";
    public string NetworkStyle          { get; set; } = "color";
    public string StudioStyle           { get; set; } = "standard";
    public string RibbonColour          { get; set; } = "black";
    public string FlagStyle             { get; set; } = "round";
    // "badge" = the RT icon + percentage pill (default). "ribbon" = the Certified Fresh corner
    // ribbon, which carries no score and so only appears when the title is Fresh.
    public string RottenTomatoesStyle   { get; set; } = "badge";
    // Same choice for the audience score; its ribbon art is the "Verified Hot" seal.
    public string AudienceScoreStyle    { get; set; } = "badge";
}

// ─── Media items ─────────────────────────────────────────────────────────────

public class LibraryItem
{
    public int    Id                        { get; set; }
    public string PlexRatingKey             { get; set; } = string.Empty;
    public string PlexLibrarySectionId      { get; set; } = string.Empty;
    /// <summary>Media server the item came from ("Plex"/"Jellyfin"/"Emby"); lists only show the active server's.</summary>
    public string ServerType                { get; set; } = "Plex";
    public MediaType MediaType              { get; set; }
    public string Title                     { get; set; } = string.Empty;
    public int?   Year                      { get; set; }

    // External IDs
    public string? TmdbId                  { get; set; }
    public string? TvdbId                  { get; set; }
    public string? ImdbId                  { get; set; }

    // Technical metadata (from Plex)
    public string? VideoResolution          { get; set; }
    public string? VideoDynamicRange        { get; set; }
    public string? AudioCodec              { get; set; }
    public string? Edition                 { get; set; }  // e.g. "Director's Cut", "Extended"
    public string? ContentRating           { get; set; }  // e.g. "PG", "MA15+"

    // Enriched metadata (from TMDB)
    public string? Studio                  { get; set; }
    public string? Network                 { get; set; }
    public string? StreamingService        { get; set; }  // e.g. "Netflix"
    public string? ShowStatus              { get; set; }  // "Returning Series", "Ended", etc.
    public int?    EpisodeCount            { get; set; }
    public double? ImdbRating              { get; set; }
    public int?    RottenTomatoesScore     { get; set; }
    public int?    AudienceScore           { get; set; }
    public bool    IsOscarWinner           { get; set; }
    public bool    IsOscarNominee          { get; set; }
    public bool    IsEmmyWinner            { get; set; }
    // Last time ratings/awards were pulled from OMDb/MDBList. MinValue = never. OMDb's free tier
    // allows ~1,000 calls a day, so refreshes work oldest-first from this and resume where they
    // stopped instead of re-spending the day's quota on the same titles every time.
    public DateTime RatingsCheckedUtc      { get; set; }
    public bool    IsTrending              { get; set; }
    public bool    IsPopular               { get; set; }
    public string? ContentLanguage         { get; set; }  // primary language
    public int?    MetacriticScore         { get; set; }  // 0–100, from OMDb (MDBList fallback)
    // From the media server's own file info — feed the NEW / runtime / source / versions / language badges.
    public DateTime? AddedAtUtc             { get; set; }  // when the server added the item
    public DateTime? LatestSeasonAddedUtc   { get; set; }  // shows: newest season's added date
    public int?    RuntimeMinutes          { get; set; }
    public int?    VersionCount            { get; set; }  // movies: versions (files) on the server
    public string? VideoSource             { get; set; }  // "REMUX", "BLU-RAY", "WEB", "HDTV", "DVD"
    public int?    AudioLanguageCount      { get; set; }
    public int?    SubtitleLanguageCount   { get; set; }
    // Whether the poster last pushed to the server carried the NEW badge. When that no longer matches
    // (the badge expired, or a new season arrived) the poster is stale and gets re-applied.
    public bool    NewBadgeOnPoster        { get; set; }
    // Which of ImdbRating / RottenTomatoesScore / AudienceScore came from the media server itself (Plex
    // holds all three), as RatingsEnricher.*FromServer flags. OMDb / MDBList leave those fields alone
    // and only fill the gaps, so the two sources don't overwrite each other on alternate refreshes.
    public int     ServerRatingFields      { get; set; }
    // From MDBList's batch lookup: Letterboxd average out of 5, Trakt %, and the item's place in IMDb's
    // Top 250 chart (null = not in it).
    public double? LetterboxdRating        { get; set; }
    public int?    TraktRating             { get; set; }
    public int?    ImdbTop250Rank          { get; set; }
    // From TMDB during the scan — feed the Genre and Franchise auto-collections.
    public string? Genres                  { get; set; }  // "Action|Science Fiction"
    public string? TmdbCollectionId        { get; set; }  // movies: the franchise TMDB groups it in
    public string? TmdbCollectionName      { get; set; }  // e.g. "Alien Collection"
    public string? Countries               { get; set; }  // "United States of America|United Kingdom"
    public string? Actors                  { get; set; }  // top-billed cast, "|"-separated
    public string? Directors               { get; set; }  // movies: director(s); shows: creator(s)
    // Which TMDB detail fields have been gathered: 0 = none, 1 = genres + franchise, 2 = + countries + credits.
    public int     TmdbDetailsVersion      { get; set; }

    // Current poster/art state
    public string? CurrentPosterUrl        { get; set; }
    public string? CurrentPosterSource     { get; set; }
    public bool    PosterAppliedToPlex     { get; set; } = false;  // true once pushed to Plex
    public string? CurrentBackgroundUrl    { get; set; }
    public string? CurrentBackgroundSource { get; set; }
    public bool    BackgroundAppliedToPlex { get; set; } = false;
    public bool    TextlessPreferred        { get; set; }

    // Set when the user explicitly dismisses a poster/background. Distinct from "no poster yet":
    // a dismissed item is hidden from the grid AND skipped by the scan's auto-propose, so it stays
    // gone instead of reappearing on the next scan. Choosing a new poster clears the flag.
    public bool    PosterDismissed          { get; set; }
    public bool    BackgroundDismissed      { get; set; }

    // Backup
    public string? OriginalPosterPath      { get; set; }  // local file path
    public string? OriginalBackgroundPath  { get; set; }

    public DateTime  LastSeenAtUtc             { get; set; }
    public DateTime? LastPosterAppliedUtc       { get; set; }
    public DateTime? LastBackgroundAppliedUtc   { get; set; }

    public List<SeasonItem> Seasons { get; set; } = new();
}

public class SeasonItem
{
    public int    Id                    { get; set; }
    public int    LibraryItemId         { get; set; }
    public string PlexRatingKey         { get; set; } = string.Empty;
    public int    SeasonNumber          { get; set; }
    public string Title                 { get; set; } = string.Empty;
    public int?   EpisodeCount         { get; set; }
    public bool   IsNew                { get; set; }  // "New Season" banner flag

    public string? CurrentPosterUrl    { get; set; }
    public string? CurrentPosterSource { get; set; }
    public bool    TextlessPreferred    { get; set; }
    public string? OriginalPosterPath  { get; set; }
    public DateTime? LastPosterAppliedUtc { get; set; }
}

// ─── Poster / background candidates ──────────────────────────────────────────

public class PosterCandidate
{
    public PosterSource Source      { get; set; }
    public string ImageUrl          { get; set; } = string.Empty;
    public string ThumbnailUrl      { get; set; } = string.Empty;
    public string? Language         { get; set; }
    public bool   IsTextless        { get; set; }
    public int    Width             { get; set; }
    public int    Height            { get; set; }
    public double? VoteAverage      { get; set; }
    public int?   Likes             { get; set; }
    // FanArt.tv's image id. FanArt.tv doesn't say who uploaded an image, but a set is uploaded in one go, so the
    // posters of one set have neighbouring ids — that's how "match the other seasons" finds the rest of a set.
    public string? ExternalId       { get; set; }
    // Season picker only: how many of the show's OTHER seasons (in the library) this poster's FanArt.tv set has a
    // poster for, out of how many there are. Null = not part of a set (or not FanArt.tv).
    public int?   SetMatches        { get; set; }
    public int?   SetOtherSeasons   { get; set; }
}

public class BackgroundCandidate
{
    public PosterSource Source      { get; set; }
    public string ImageUrl          { get; set; } = string.Empty;
    public string ThumbnailUrl      { get; set; } = string.Empty;
    public string? Language         { get; set; }
    public int    Width             { get; set; }
    public int    Height            { get; set; }
    public double? VoteAverage      { get; set; }
    public int?   Likes             { get; set; }
}

// ─── Queue / activity ─────────────────────────────────────────────────────────

public enum ChangeType { Poster, Background }

public class PendingChange
{
    public int        Id                    { get; set; }
    public int?       LibraryItemId         { get; set; }
    public int?       SeasonItemId          { get; set; }
    public ChangeType ChangeType            { get; set; } = ChangeType.Poster;
    public string     TargetTitle           { get; set; } = string.Empty;
    public string     ProposedImageUrl      { get; set; } = string.Empty;
    public PosterSource ProposedSource      { get; set; }
    public string     Reason               { get; set; } = string.Empty;
    public DateTime   CreatedAtUtc         { get; set; } = DateTime.UtcNow;
}

public class ActivityLogEntry
{
    public int      Id              { get; set; }
    public DateTime TimestampUtc    { get; set; } = DateTime.UtcNow;
    public string   Level           { get; set; } = "Info";
    public string   Message         { get; set; } = string.Empty;
}

// ─── Settings ────────────────────────────────────────────────────────────────

public class AuthSettings
{
    public string Username         { get; set; } = "admin";
    public string PasswordHash     { get; set; } = string.Empty;  // BCrypt hash; empty = no auth
    public bool   AuthEnabled      { get; set; } = false;
    public int    SessionDaysValid { get; set; } = 30;
}

public class BackupSettings
{
    public bool   BackupEnabled     { get; set; } = true;
    public string BackupDirectory   { get; set; } = string.Empty;  // defaults to ProgramData\Postarr\Backups
}

public class AppSettings
{
    // Connection
    /// <summary>Which media server Postarr manages: "Plex" (default), "Jellyfin" or "Emby".</summary>
    public string MediaServerType   { get; set; } = "Plex";
    public string PlexBaseUrl       { get; set; } = string.Empty;
    public string PlexToken         { get; set; } = string.Empty;
    public string JellyfinBaseUrl   { get; set; } = string.Empty;
    public string JellyfinApiKey    { get; set; } = string.Empty;
    public string EmbyBaseUrl       { get; set; } = string.Empty;
    public string EmbyApiKey        { get; set; } = string.Empty;

    // API keys
    public string TmdbApiKey        { get; set; } = string.Empty;
    public string FanArtApiKey      { get; set; } = string.Empty;
    public string TvdbApiKey        { get; set; } = string.Empty;
    public string OmdbApiKey        { get; set; } = string.Empty;  // https://www.omdbapi.com/apikey.aspx (free)
    public string MdbListApiKey     { get; set; } = string.Empty;  // https://mdblist.com/preferences (free) — RT for TV
    public string TraktClientId     { get; set; } = string.Empty;  // https://trakt.tv/oauth/applications (free) — public lists only
    // Secret that lets Plex's webhook in while login is on (Plex can't sign in). Generated on first use; part of the
    // webhook URL shown in Settings.
    public string WebhookToken      { get; set; } = string.Empty;

    // Behaviour
    public ApplyMode ApplyMode                  { get; set; } = ApplyMode.AutoApply;
    public bool      PreferTextlessPosters      { get; set; } = false;
    // When the scan picks season posters for a new show (or a new season), prefer one matching FanArt.tv set for
    // all of them — and continue the set a show already uses. See SeasonSetMatcher.
    public bool      PreferSeasonSets           { get; set; } = false;
    public int       ScanIntervalMinutes        { get; set; } = 60;
    public bool      WebhookEnabled             { get; set; } = true;
    // When true, a poster/background found during scan is pushed straight to Plex
    // (with overlays rendered). When false, it's only proposed on the card —
    // you click Apply yourself before anything changes in Plex.
    public bool      AutoApplyOnScan            { get; set; } = false;
    // When true, a full scan only inspects items added/changed since the last
    // scan completed (using LastSeenAtUtc), skipping the rest of the library
    // for a much faster run. First scan after enabling is always a full scan.
    // On by default: the first run is always a full scan (see LastFullScanCompletedUtc), and
    // every scan after that only does the expensive enrichment/poster work for new or changed
    // items instead of re-scanning the whole library each time.
    public bool      IncrementalScanOnly        { get; set; } = true;
    // When true, items/collections you dismissed stay visible in the grids (marked as dismissed)
    // instead of being hidden, so an accidental dismiss can be undone. Deliberately NOT part of
    // Overlays — anything in there bumps OverlaysLastChangedUtc and would re-upload every poster.
    public bool      ShowDismissed              { get; set; } = false;
    // UI theme colours, sampled from the Postarr logo by default. These drive the interface accent
    // AND the in-app logo re-tint. Purely cosmetic (top-level, not in Overlays, so they never touch
    // OverlaysLastChangedUtc / trigger re-uploads).
    public string    ThemePrimary               { get; set; } = "#10bec9";  // teal
    public string    ThemeAccent                { get; set; } = "#f2586e";  // coral
    public DateTime? LastFullScanCompletedUtc   { get; set; }
    // Bumped whenever overlay settings change. Lets apply skip re-uploading a poster that's already
    // on Plex and still reflects the current overlay settings — so repeated "Apply All" clicks don't
    // pile up duplicate posters in Plex (which has no API to delete uploaded posters).
    public DateTime? OverlaysLastChangedUtc     { get; set; }

    // Feature groups
    public OverlaySettings Overlays     { get; set; } = new();
    public AuthSettings    Auth         { get; set; } = new();
    public BackupSettings  Backup       { get; set; } = new();
    // Saved only through /api/auto-collections — the general settings save keeps whatever is stored.
    public AutoCollectionSettings AutoCollections { get; set; } = new();
}

/// <summary>
/// A Plex collection (movie or TV) — these are managed separately from
/// individual items and get their own poster picker.
/// </summary>
/// <summary>Which auto-collection sets are switched on, and their options.</summary>
public class AutoCollectionSettings
{
    public List<AutoCollectionSetConfig> Sets { get; set; } = new();
    // Re-sync every enabled set at the end of each library scan (charts move, new titles arrive).
    public bool SyncAfterScan { get; set; } = true;
    // Poster for new collections: Kometa's default image when one exists (fetched from Kometa's GitHub),
    // instead of Postarr's own design. Off by default — Kometa's artwork carries no licence.
    public bool UseKometaImages { get; set; }
    // Kometa-style "custom collections": the user's own lists (an MDBList / TMDB list, or titles typed in order).
    public List<CustomCollectionConfig> Custom { get; set; } = new();
}

/// <summary>One user-defined collection: where its titles come from, which libraries, and how it's ordered.</summary>
public class CustomCollectionConfig
{
    public string Id      { get; set; } = Guid.NewGuid().ToString("N");   // stable — becomes ManagedCollection.CollectionKey
    public string Name    { get; set; } = string.Empty;
    public bool   Enabled { get; set; } = true;
    // "mdblist" = an MDBList list, "trakt" = a Trakt list, "tmdb" = a TMDB list (URL or id), "titles" = typed in below.
    public string Source  { get; set; } = "titles";
    public string ListRef { get; set; } = string.Empty;
    // One per line, in the order wanted: "Iron Man (2008)", "tmdb:1726" or "imdb:tt0371746". Lines starting # are notes.
    public string Titles  { get; set; } = string.Empty;
    public bool   Movies  { get; set; } = true;
    public bool   Shows   { get; set; }
    // "list" = the list's own order (needs the server to support a custom order), "release" = oldest first,
    // "default" = leave the server's own order (A–Z).
    public string Order   { get; set; } = "list";
}

public class AutoCollectionSetConfig
{
    public string Key      { get; set; } = string.Empty;   // catalogue key, e.g. "content.genre"
    public bool   Enabled  { get; set; }
    public int?   MinItems { get; set; }                    // null = the catalogue's default
    // Picking individual collections in a set that makes many (genres, studios, franchises…), by collection key.
    // OnlyListed = false: make every one except those in Keys (new ones join automatically).
    // OnlyListed = true:  make only those in Keys (new ones stay out until ticked).
    public bool         OnlyListed { get; set; }
    public List<string> Keys       { get; set; } = new();
    // Kometa's "addons": fold one value into another (e.g. TMDB's TV genre "Sci-Fi & Fantasy" → "Science
    // Fiction"). Collection key → the name of the collection it joins.
    public Dictionary<string, string> Merges { get; set; } = new();
    // Kometa's "title_format": "{name}" is the value, e.g. "{name} Movies". Null = the set's default.
    public string? TitleFormat { get; set; }
}

/// <summary>
/// A collection Postarr created on the media server for an auto-collection set. Postarr only ever changes or
/// deletes collections listed here — never ones the user made.
/// </summary>
public class ManagedCollection
{
    public int       Id                   { get; set; }
    public string    ServerType           { get; set; } = "Plex";
    public string    SectionKey           { get; set; } = string.Empty;
    public MediaType MediaType            { get; set; }
    public string    SetKey               { get; set; } = string.Empty;   // catalogue set, e.g. "time.decade"
    public string    CollectionKey        { get; set; } = string.Empty;   // unique within the set, e.g. "1990"
    public string    Title                { get; set; } = string.Empty;
    public string    ServerCollectionId   { get; set; } = string.Empty;   // Plex rating key / Jellyfin item id
    public string?   TmdbCollectionId     { get; set; }                   // franchises: lets the poster picker find art
    public int       ItemCount            { get; set; }
    public DateTime  LastSyncedUtc        { get; set; }
    // The poster URL Postarr itself last put on this collection. If the collection's current poster is
    // anything else, the user chose it — and automatic artwork leaves it alone from then on.
    public string?   AutoPosterUrl        { get; set; }
    // Hash of the item order Postarr last applied to this collection (custom collections with a set order only).
    public string?   OrderHash            { get; set; }
}

public class PlexCollection
{
    public int    Id                    { get; set; }
    public string PlexRatingKey         { get; set; } = string.Empty;
    public string PlexLibrarySectionId  { get; set; } = string.Empty;
    /// <summary>Media server the collection came from ("Plex"/"Jellyfin"/"Emby"); lists only show the active server's.</summary>
    public string ServerType            { get; set; } = "Plex";
    public MediaType MediaType          { get; set; }  // Movie or Show
    public string Title                 { get; set; } = string.Empty;
    public int    ItemCount             { get; set; }
    public string? CurrentPosterUrl    { get; set; }
    public string? CurrentPosterSource { get; set; }
    public string? TmdbId              { get; set; }
    public DateTime LastSeenAtUtc      { get; set; }

    // Same contract as LibraryItem.PosterDismissed: an explicitly dismissed collection is hidden
    // from the grid and not re-proposed by scans. Cleared when a poster is chosen again.
    public bool   PosterDismissed      { get; set; }
}
