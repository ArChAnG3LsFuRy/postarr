using Microsoft.EntityFrameworkCore;
using Postarr.Data;
using Postarr.MediaServers;
using Postarr.Metadata;
using Postarr.Models;
using Postarr.Plex;

namespace Postarr.Services;

/// <summary>One ready-made collection set the user can tick (Kometa's "defaults", Postarr's subset).</summary>
public record CollectionSetDef(
    string Key, string Group, string Name, string Description,
    bool Movies, bool Shows, int DefaultMinItems, string? Requires = null);

/// <summary>A row of a sync plan — what will happen (or happened) to one collection.</summary>
public record CollectionPlanRow(
    string SetKey, string SetName, string Title, string Section, string Action,
    int Count, int Adds, int Removes, string? Note = null);

/// <summary>
/// Kometa-style auto-collections: builds collections on the media server from charts, awards and the library's
/// own data, and keeps them in sync. Only collections recorded in <see cref="ManagedCollection"/> (i.e. created
/// here) are ever changed or deleted; a user's own collections are never touched, and a title clash with one of
/// them is skipped rather than duplicated.
/// </summary>
public class AutoCollectionService
{
    // ── Catalogue ────────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<CollectionSetDef> Catalogue = new List<CollectionSetDef>
    {
        new("chart.imdb_top250",       "Charts", "IMDb Top 250",       "Films in IMDb's Top 250 chart",                       true,  false, 1, "mdblist"),
        new("chart.letterboxd_top250", "Charts", "Letterboxd Top 250", "Letterboxd's highest-rated films",                    true,  false, 1, "mdblist"),
        new("chart.tmdb_trending",     "Charts", "Trending",           "TMDB's trending titles this week (top 40)",           true,  true,  1, "tmdb"),
        new("chart.tmdb_popular",      "Charts", "Popular",            "TMDB's most popular titles (top 100)",                true,  true,  1, "tmdb"),
        new("chart.tmdb_top_rated",    "Charts", "Top Rated",          "TMDB's top-rated titles (top 200)",                   true,  true,  1, "tmdb"),

        new("award.best_picture",      "Awards", "Best Picture Winners","Every Academy Award Best Picture winner you have",    true,  false, 1, "mdblist"),
        new("award.oscar_winners",     "Awards", "Oscar Winners",      "Films that won at least one Oscar",                   true,  false, 1, "omdb"),
        new("award.oscar_nominees",    "Awards", "Oscar Nominees",     "Films nominated for an Oscar that didn't win one",     true,  false, 1, "omdb"),
        new("award.emmy_winners",      "Awards", "Emmy Winners",       "Shows that won a Primetime Emmy",                     false, true,  1, "omdb"),

        new("content.genre",           "Content", "Genres",            "One collection per genre — Action, Comedy, Drama…",   true,  true,  3, "tmdb"),
        new("content.franchise",       "Content", "Franchises",        "Film series from TMDB — Alien, Mission: Impossible…",  true,  false, 2, "tmdb"),

        new("rating.content_rating",   "Content Rating", "Content Ratings", "One per rating — G, PG, M, MA15+, R…",           true,  true,  3),

        new("media.resolution",        "Media", "Resolution",          "4K, 1080p, 720p and SD",                              true,  true,  1),
        new("media.edition",           "Media", "Editions",            "Director's Cut, Extended, IMAX… (Plex editions)",     true,  false, 2),

        new("location.country",        "Location", "Countries",        "Where it was made — United Kingdom, South Korea, Japan…", true, true, 3, "tmdb"),
        new("location.language",       "Location", "Original Language","French, Korean, Japanese… (English left out by default)", true, true, 3),

        new("people.actor",            "People", "Actors",             "One per actor with enough films or shows in your library (top-billed cast)", true, true, 6, "tmdb"),
        new("people.director",         "People", "Directors",          "Films by each director (shows: their creators)",      true,  true,  4, "tmdb"),

        new("production.studio",       "Production", "Studios",        "Pixar, Marvel Studios, A24…",                          true,  true,  5, "tmdb"),
        new("production.network",      "Production", "Networks",       "HBO, AMC, BBC One…",                                  false, true,  3, "tmdb"),
        new("production.streaming",    "Production", "Streaming Services", "Netflix, Disney+, Max, Prime Video, Hulu, Apple TV+, Paramount+, Peacock (where TMDB lists it streaming in the US)", true, true, 3, "tmdb"),

        new("time.decade",             "Time", "Decades",              "1980s, 1990s, 2000s…",                                true,  true,  3),
        new("time.year",               "Time", "Years",                "One per release year — 2019, 2020…",                  true,  true,  3),
        new("time.best_of_year",       "Time", "Best of the Year",     "\"Best of 2019\": the year's 10 highest-rated titles (years with at least the minimum number of rated titles)", true, true, 10),
    };

    // MDBList lists behind the list-based sets (checked 2026-09-29: the most-liked public mirrors).
    private static readonly Dictionary<string, int> MdbLists = new()
    {
        ["chart.imdb_top250"]       = RatingsEnricher.ImdbTop250ListId,   // same order as imdb.com/chart/top
        ["chart.letterboxd_top250"] = 21825,
        ["award.best_picture"]      = 110942,
    };

    private static readonly Dictionary<string, (string Chart, int Pages)> TmdbCharts = new()
    {
        ["chart.tmdb_trending"]  = ("trending",  2),
        ["chart.tmdb_popular"]   = ("popular",   5),
        ["chart.tmdb_top_rated"] = ("top_rated", 10),
    };

    public static CollectionSetDef? Def(string key) => Catalogue.FirstOrDefault(d => d.Key == key);

    public static bool AwardSetsEnabled(AutoCollectionSettings? a) =>
        a?.Sets.Any(s => s.Enabled && s.Key is "award.oscar_winners" or "award.oscar_nominees" or "award.emmy_winners") == true;

    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly MetadataClientFactory _factory;
    private readonly SettingsRepository    _settings;
    private readonly ActivityLogger        _log;
    private readonly IServiceProvider      _services;
    private readonly ILogger<AutoCollectionService> _logger;

    public AutoCollectionService(IDbContextFactory<PostarrDbContext> dbFactory, MetadataClientFactory factory,
        SettingsRepository settings, ActivityLogger log, IServiceProvider services, ILogger<AutoCollectionService> logger)
    { _dbFactory = dbFactory; _factory = factory; _settings = settings; _log = log; _services = services; _logger = logger; }

    // ── Run state (one sync at a time). Static: the service itself is per-request/scope. ─────────────
    private static readonly SemaphoreSlim _gate = new(1, 1);
    public static bool    IsRunning    { get; private set; }
    public static string? Progress     { get; private set; }
    public static DateTime? LastRunUtc { get; private set; }
    public static List<CollectionPlanRow> LastRows { get; private set; } = new();
    public static List<string> LastErrors { get; private set; } = new();

    /// <summary>Runs a sync in the background in its own service scope (the caller's request may end first).</summary>
    public static void StartInBackground(IServiceScopeFactory scopes) => _ = Task.Run(async () =>
    {
        using var scope = scopes.CreateScope();
        try { await scope.ServiceProvider.GetRequiredService<AutoCollectionService>().SyncAsync(CancellationToken.None); }
        catch (Exception ex) { scope.ServiceProvider.GetRequiredService<ILogger<AutoCollectionService>>().LogError(ex, "Auto-collection sync crashed"); }
    });

    private record Desired(string SetKey, string CollectionKey, string Title, PlexSection Section,
                           List<string> ItemKeys, string? TmdbCollectionId, string OrderMode = "default")
    {
        public bool Ordered => OrderMode != "default";
    }

    public const string CustomSetKey = "custom";
    private static string SetName(string key) => key == CustomSetKey ? "Custom" : Def(key)?.Name ?? key;
    private static string OrderHashOf(IEnumerable<string> keys) => Convert.ToHexString(
        System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(",", keys))));

    private sealed class Plan
    {
        public List<(Desired D, ManagedCollection? M, List<string>? Current, string Action, string? Note)> Rows { get; } = new();
        public List<ManagedCollection> Deletes { get; } = new();
        public List<string> Notes { get; } = new();
    }

    /// <summary>Works out what a sync would do, without changing anything on the server.</summary>
    public async Task<(List<CollectionPlanRow> Rows, List<string> Notes)> PreviewAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IsRunning = true;
            var plan = await BuildPlanAsync(ct);
            return (ToRows(plan), plan.Notes);
        }
        finally { IsRunning = false; Progress = null; _gate.Release(); }
    }

    /// <summary>Builds the plan and carries it out. Returns false if a sync was already running.</summary>
    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return false;
        var errors = new List<string>();
        try
        {
            IsRunning = true;
            var plan   = await BuildPlanAsync(ct);
            var client = _factory.BuildMediaServerClient() ?? throw new InvalidOperationException("No media server configured.");
            var server = _factory.MediaServerName;
            using var db = await _dbFactory.CreateDbContextAsync(ct);
            int created = 0, updated = 0, deleted = 0, step = 0, total = plan.Rows.Count(r => r.Action is "create" or "update") + plan.Deletes.Count;

            foreach (var (d, m, current, action, _) in plan.Rows)
            {
                if (action is not ("create" or "update")) continue;
                Progress = $"{++step}/{total} · {d.Title}";
                try
                {
                    if (action == "create")
                    {
                        var id = await client.CreateCollectionAsync(d.Section, d.Title, d.ItemKeys, ct);
                        // Jellyfin can create the collection yet silently drop the titles passed with it when several
                        // are made in a row (seen 2026-09-29: 2 of 10 came out empty). Check, and add what's missing.
                        await Task.Delay(300, ct);
                        var got = (await client.GetCollectionItemKeysAsync(id, ct))?.ToHashSet() ?? new HashSet<string>();
                        var missing = d.ItemKeys.Where(k => !got.Contains(k)).ToList();
                        if (missing.Count > 0) await client.AddToCollectionAsync(id, missing, ct);
                        var row = m != null ? await db.ManagedCollections.FirstAsync(x => x.Id == m.Id, ct) : new ManagedCollection();
                        row.ServerType = server; row.SectionKey = d.Section.Key;
                        row.MediaType = d.Section.Type == "show" ? MediaType.Show : MediaType.Movie;
                        row.SetKey = d.SetKey; row.CollectionKey = d.CollectionKey; row.Title = d.Title;
                        row.ServerCollectionId = id; row.TmdbCollectionId = d.TmdbCollectionId;
                        row.ItemCount = d.ItemKeys.Count; row.LastSyncedUtc = DateTime.UtcNow;
                        row.OrderHash = await ApplyOrderAsync(client, d, id, ct);
                        if (m == null) db.ManagedCollections.Add(row);
                        created++;
                    }
                    else
                    {
                        var want = d.ItemKeys.ToHashSet();
                        var have = current!.ToHashSet();
                        var add  = d.ItemKeys.Where(k => !have.Contains(k)).ToList();
                        var rem  = current!.Where(k => !want.Contains(k)).ToList();
                        if (add.Count > 0) await client.AddToCollectionAsync(m!.ServerCollectionId, add, ct);
                        if (rem.Count > 0) await client.RemoveFromCollectionAsync(m!.ServerCollectionId, rem, ct);
                        if (!string.Equals(m!.Title, d.Title, StringComparison.Ordinal))
                            await client.RenameCollectionAsync(d.Section, m.ServerCollectionId, d.Title, ct);
                        var row = await db.ManagedCollections.FirstAsync(x => x.Id == m!.Id, ct);
                        row.Title = d.Title; row.ItemCount = d.ItemKeys.Count; row.LastSyncedUtc = DateTime.UtcNow;
                        if (d.Ordered ? row.OrderHash != OrderHashOf(d.ItemKeys) : row.OrderHash != null)
                            row.OrderHash = await ApplyOrderAsync(client, d, m!.ServerCollectionId, ct);
                        updated++;
                    }
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{d.Title}: {ex.Message}");
                    _logger.LogWarning(ex, "Auto-collection {Title} failed", d.Title);
                }
            }

            foreach (var m in plan.Deletes)
            {
                Progress = $"{++step}/{total} · removing {m.Title}";
                try
                {
                    await client.DeleteCollectionAsync(m.ServerCollectionId, ct);
                    db.ManagedCollections.Remove(await db.ManagedCollections.FirstAsync(x => x.Id == m.Id, ct));
                    var listed = await db.Collections.FirstOrDefaultAsync(c => c.PlexRatingKey == m.ServerCollectionId, ct);
                    if (listed != null) db.Collections.Remove(listed);
                    await db.SaveChangesAsync(ct);
                    deleted++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{m.Title}: {ex.Message}");
                }
            }

            // Show the new collections on Postarr's collection pages (and pick up franchise art) straight away.
            if (created + updated + deleted > 0)
            {
                Progress = "Refreshing collection list…";
                try { await _services.GetRequiredService<LibraryScanService>().RefreshCollectionsAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Collection refresh failed"); }
            }

            // Posters: new collections get one straight away; existing ones follow design / Kometa-setting changes.
            int posters = 0;
            Progress = "Adding posters…";
            try { posters = await _services.GetRequiredService<CollectionArtService>().ApplyAutoArtAsync(client, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Collection posters failed"); }

            LastRows = ToRows(plan); LastErrors = errors; LastRunUtc = DateTime.UtcNow;
            await _log.LogAsync($"Auto-collections synced on {server} — {created} created, {updated} updated, {deleted} removed, {posters} posters" +
                                (errors.Count > 0 ? $", {errors.Count} failed ({string.Join("; ", errors.Take(3))})" : "") + ".",
                                errors.Count > 0 ? "Warning" : "Info");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrors = errors.Append(ex.Message).ToList(); LastRunUtc = DateTime.UtcNow;
            await _log.LogAsync($"Auto-collection sync failed: {ex.Message}", "Error");
            return true;
        }
        finally { IsRunning = false; Progress = null; _gate.Release(); }
    }

    private static List<CollectionPlanRow> ToRows(Plan plan)
    {
        var rows = plan.Rows.Select(r =>
        {
            int adds = 0, removes = 0;
            if (r.Action == "update" && r.Current != null)
            {
                var have = r.Current.ToHashSet(); var want = r.D.ItemKeys.ToHashSet();
                adds = r.D.ItemKeys.Count(k => !have.Contains(k)); removes = r.Current.Count(k => !want.Contains(k));
            }
            return new CollectionPlanRow(r.D.SetKey, SetName(r.D.SetKey), r.D.Title, r.D.Section.Title,
                                         r.Action, r.D.ItemKeys.Count, adds, removes, r.Note);
        }).ToList();
        rows.AddRange(plan.Deletes.Select(m => new CollectionPlanRow(m.SetKey, SetName(m.SetKey), m.Title, "",
            "delete", m.ItemCount, 0, 0, "Set switched off, or no longer has enough titles")));
        return rows;
    }

    // ── Planning ─────────────────────────────────────────────────────────────

    private async Task<Plan> BuildPlanAsync(CancellationToken ct)
    {
        var settings = _settings.Get();
        var cfg      = settings.AutoCollections ?? new AutoCollectionSettings();
        var client   = _factory.BuildMediaServerClient() ?? throw new InvalidOperationException("No media server configured.");
        var server   = _factory.MediaServerName;
        var plan     = new Plan();
        _listCache.Clear(); _chartCache.Clear();   // charts move — always fetch fresh for each run

        Progress = "Reading libraries…";
        var sections = await client.GetLibrarySectionsAsync(ct);
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items    = await db.LibraryItems.Where(i => i.ServerType == server).ToListAsync(ct);
        var managed  = await db.ManagedCollections.Where(m => m.ServerType == server).ToListAsync(ct);
        var enabled  = cfg.Sets.Where(s => s.Enabled && Def(s.Key) != null).ToList();

        // Genre / franchise / country / cast data comes from TMDB during scans; fill any gaps first (once per title).
        if (enabled.Any(s => NeedsTmdbDetails(s.Key)))
            await BackfillTmdbAsync(items, db, plan, ct);

        Progress = "Working out collections…";
        var failedSets = new HashSet<string>();
        var desired    = new List<Desired>();
        foreach (var set in enabled)
        {
            var def = Def(set.Key)!;
            var min = Math.Max(1, set.MinItems ?? def.DefaultMinItems);
            foreach (var sec in sections)
            {
                var isShow = sec.Type == "show";
                if (isShow ? !def.Shows : !def.Movies) continue;
                var secItems = items.Where(i => i.PlexLibrarySectionId == sec.Key).ToList();
                if (secItems.Count == 0) continue;
                var groups = await GroupAsync(def, secItems, isShow, min, ct);
                if (groups == null) { failedSets.Add(def.Key); plan.Notes.Add($"{def.Name}: couldn't read its source list right now — left as it was."); continue; }
                // The user's picks (by the original values), then merges, then the name format.
                groups = Merge(groups.Where(g => Picked(set, g.Key)).ToList(), set);
                // "Best of" applies its minimum to the year (checked in GroupAsync); each collection holds 10.
                var need = def.Key == "time.best_of_year" ? 1 : min;
                foreach (var g in groups.Where(g => g.Items.Count >= need))
                    desired.Add(new Desired(def.Key, g.Key, FormatTitle(def, set, g.Title), sec,
                        g.Items.Select(i => i.PlexRatingKey).Distinct().ToList(), g.TmdbCollectionId));
            }
        }

        await PlanCustomAsync(cfg, sections, items, desired, failedSets, plan, ct);

        // Jellyfin / Emby show every collection in one place, so the same name for a movie set and a show set
        // (e.g. two "Trending" collections) would be ambiguous there. Plex keeps them in separate libraries.
        if (server != "Plex")
            foreach (var clash in desired.GroupBy(d => d.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
                foreach (var d in clash.ToList())
                {
                    var suffix = clash.Select(x => x.Section.Type).Distinct().Count() > 1
                        ? (d.Section.Type == "show" ? " (TV)" : " (Movies)") : $" ({d.Section.Title})";
                    desired[desired.IndexOf(d)] = d with { Title = d.Title + suffix };
                }

        // Collections already on the server that Postarr didn't make — never duplicated, never touched.
        Progress = "Checking existing collections…";
        var managedIds = managed.Select(m => m.ServerCollectionId).ToHashSet();
        var userTitles = new Dictionary<string, HashSet<string>>();   // section key → titles
        foreach (var sec in sections)
        {
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var c in await client.GetCollectionsAsync(sec, ct))
                    if (!managedIds.Contains(c.RatingKey)) titles.Add(c.Title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Listing collections failed"); }
            userTitles[sec.Key] = titles;
        }
        var allUserTitles = userTitles.Values.SelectMany(t => t).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // section|title already claimed this run
        var matched = new HashSet<int>();
        foreach (var d in desired)
        {
            var m = managed.FirstOrDefault(x => x.SetKey == d.SetKey && x.CollectionKey == d.CollectionKey && x.SectionKey == d.Section.Key);
            if (m != null) matched.Add(m.Id);

            if (!used.Add($"{d.Section.Key}|{d.Title}"))
            { plan.Rows.Add((d, m, null, "skip", "Another set already makes a collection with this name")); continue; }

            if (m != null)
            {
                Progress = $"Comparing {d.Title}…";
                List<string>? current = null;
                try { current = await client.GetCollectionItemKeysAsync(m.ServerCollectionId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { plan.Rows.Add((d, m, null, "skip", $"Couldn't read it: {ex.Message}")); continue; }
                if (current == null) { plan.Rows.Add((d, m, null, "create", "Was deleted on the server — will be recreated")); continue; }
                var same    = current.ToHashSet().SetEquals(d.ItemKeys);
                var renamed = !string.Equals(m.Title, d.Title, StringComparison.Ordinal);
                // A collection with a set order is also "changed" when the order Postarr last applied differs
                // (or when the order was switched off and the server should go back to its own).
                var orderDrift = d.Ordered ? m.OrderHash != OrderHashOf(d.ItemKeys) : m.OrderHash != null;
                plan.Rows.Add((d, m, current, same && !renamed && !orderDrift ? "unchanged" : "update",
                               renamed ? $"Renamed from “{m.Title}”" : orderDrift && same ? (d.Ordered ? "Order changed" : "Back to the server's own order") : null));
                continue;
            }

            var clashes = server == "Plex" ? userTitles.GetValueOrDefault(d.Section.Key)?.Contains(d.Title) == true
                                           : allUserTitles.Contains(d.Title);
            if (clashes) { plan.Rows.Add((d, null, null, "skip", "You already have a collection with this name")); continue; }
            plan.Rows.Add((d, null, null, "create", null));
        }

        // Managed collections nothing wants any more → delete. Not when their source just failed to load.
        foreach (var m in managed.Where(m => !matched.Contains(m.Id) && !failedSets.Contains(m.SetKey) && !failedSets.Contains($"{m.SetKey}|{m.CollectionKey}")))
            plan.Deletes.Add(m);

        return plan;
    }

    private record Group(string Key, string Title, List<LibraryItem> Items, string? TmdbCollectionId = null);

    /// <summary>
    /// Kometa's "addons": values the user folded into another join that collection (it may exist in this library
    /// or only be made by the merge, e.g. TV's "Sci-Fi &amp; Fantasy" → "Science Fiction").
    /// </summary>
    private static List<Group> Merge(List<Group> groups, AutoCollectionSetConfig set)
    {
        if (set.Merges.Count == 0) return groups;
        var byTitle = groups.GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result  = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var target = g;
            if (set.Merges.TryGetValue(g.Key, out var into) && !string.IsNullOrWhiteSpace(into)
                && !into.Equals(g.Title, StringComparison.OrdinalIgnoreCase))
                target = byTitle.TryGetValue(into, out var t) ? t : new Group(Slug(into), into.Trim(), new(), g.TmdbCollectionId);
            result[target.Key] = result.TryGetValue(target.Key, out var have)
                ? have with { Items = have.Items.Concat(g.Items).Distinct().ToList() }
                : target with { Items = (target == g ? g.Items : target.Items.Concat(g.Items)).Distinct().ToList() };
        }
        return result.Values.ToList();
    }

    public static string DefaultFormat(string setKey, string name) => setKey switch
    {
        "rating.content_rating" when !(name.StartsWith("Not ", StringComparison.OrdinalIgnoreCase)
                                       || name.Equals("Unrated", StringComparison.OrdinalIgnoreCase)) => "Rated {name}",
        "time.best_of_year" => "Best of {name}",
        _ => "{name}",
    };

    /// <summary>Kometa's "title_format": the user's pattern with {name} replaced, or the set's default.</summary>
    private static string FormatTitle(CollectionSetDef def, AutoCollectionSetConfig set, string name)
    {
        var fmt = set.TitleFormat;
        if (string.IsNullOrWhiteSpace(fmt) || !fmt.Contains("{name}")) fmt = DefaultFormat(def.Key, name);
        var title = fmt.Replace("{name}", name).Trim();
        return title.Length > 0 ? title : name;
    }

    /// <summary>Whether the user's per-collection picks allow this one.</summary>
    private static bool Picked(AutoCollectionSetConfig set, string key) =>
        set.OnlyListed ? set.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)
                       : !set.Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets that make one collection per value found in the library — the ones with a "Choose…" list.</summary>
    public static bool IsDynamic(string setKey) => setKey is "content.genre" or "content.franchise" or "rating.content_rating"
        or "media.resolution" or "media.edition" or "production.studio" or "production.network" or "production.streaming"
        or "location.country" or "location.language" or "people.actor" or "people.director"
        or "time.decade" or "time.year" or "time.best_of_year";

    public record ValueRow(string Key, string Title, int Count, bool MeetsMinimum);

    /// <summary>Every collection a dynamic set could make from the library (all libraries combined), for the picker.</summary>
    public async Task<List<ValueRow>> ValuesAsync(string setKey, int? minItems, CancellationToken ct)
    {
        var def = Def(setKey) ?? throw new ArgumentException("Unknown set.");
        var client = _factory.BuildMediaServerClient() ?? throw new InvalidOperationException("No media server configured.");
        var server = _factory.MediaServerName;
        var sections = await client.GetLibrarySectionsAsync(ct);
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = await db.LibraryItems.Where(i => i.ServerType == server).ToListAsync(ct);
        if (NeedsTmdbDetails(setKey)) await BackfillTmdbAsync(items, db, new Plan(), ct);

        var min = Math.Max(1, minItems ?? def.DefaultMinItems);
        var all = new List<(string Key, string Title, int Count)>();
        foreach (var sec in sections)
        {
            var isShow = sec.Type == "show";
            if (isShow ? !def.Shows : !def.Movies) continue;
            var groups = await GroupAsync(def, items.Where(i => i.PlexLibrarySectionId == sec.Key).ToList(), isShow, min, ct);
            if (groups != null) all.AddRange(groups.Select(g => (g.Key, g.Title, g.Items.Count)));
        }
        // The same value across libraries (e.g. "Drama" for movies and TV) is one choice.
        return all.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                  .Select(g => new ValueRow(g.Key, g.First().Title, g.Sum(x => x.Count),
                                            setKey == "time.best_of_year" || g.Max(x => x.Count) >= min))
                  .OrderByDescending(v => v.Count).ThenBy(v => v.Title).ToList();
    }

    /// <summary>The collections one set makes from one section's titles; null if its source couldn't be read.</summary>
    private async Task<List<Group>?> GroupAsync(CollectionSetDef def, List<LibraryItem> items, bool isShow, int min, CancellationToken ct)
    {
        switch (def.Key)
        {
            case "chart.imdb_top250":
            case "chart.letterboxd_top250":
            case "award.best_picture":
            {
                var mdb = _factory.BuildMdbListClient();
                if (mdb == null) return null;
                var ranks = await ListRanksAsync(mdb, MdbLists[def.Key], ct);
                if (ranks == null) return null;
                var hits = items.Where(i => i.ImdbId != null && ranks.ContainsKey(i.ImdbId)).OrderBy(i => ranks[i.ImdbId!]).ToList();
                return new() { new Group("all", def.Name, hits) };
            }
            case "chart.tmdb_trending":
            case "chart.tmdb_popular":
            case "chart.tmdb_top_rated":
            {
                var tmdb = _factory.BuildTmdbClient();
                if (tmdb == null) return null;
                var (chart, pages) = TmdbCharts[def.Key];
                var ids = await TmdbChartAsync(tmdb, chart, !isShow, pages, ct);
                if (ids == null) return null;
                var order = ids.Select((id, n) => (id, n)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().n);
                var hits = items.Where(i => i.TmdbId != null && order.ContainsKey(i.TmdbId)).OrderBy(i => order[i.TmdbId!]).ToList();
                return new() { new Group("all", def.Name, hits) };
            }
            case "award.oscar_winners":  return new() { new Group("all", def.Name, items.Where(i => i.IsOscarWinner).ToList()) };
            case "award.oscar_nominees": return new() { new Group("all", def.Name, items.Where(i => i.IsOscarNominee && !i.IsOscarWinner).ToList()) };
            case "award.emmy_winners":   return new() { new Group("all", def.Name, items.Where(i => i.IsEmmyWinner).ToList()) };

            case "content.genre":
                return items.SelectMany(i => (i.Genres ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                              .Select(g => (Genre: g, Item: i)))
                            .GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
                            .Select(g => new Group(Slug(g.Key), g.First().Genre, g.Select(x => x.Item).ToList())).ToList();

            case "content.franchise":
                return items.Where(i => !string.IsNullOrEmpty(i.TmdbCollectionId))
                            .GroupBy(i => i.TmdbCollectionId!)
                            .Select(g => new Group(g.Key, g.First().TmdbCollectionName ?? $"Collection {g.Key}",
                                                   g.OrderBy(i => i.Year ?? 9999).ToList(), g.Key)).ToList();

            case "rating.content_rating":
                return By(items, i => RatingLabel(i.ContentRating), r => r);   // "Rated …" comes from the title format

            case "media.resolution":
                return By(items, i => ResolutionLabel(i.VideoResolution), r => r);

            case "production.studio":    return By(items, i => i.Studio?.Trim(), s => s);
            case "production.network":   return By(items, i => i.Network?.Trim(), s => s);
            case "production.streaming": return By(items, i => StreamingLabel(i.StreamingService), s => s);

            case "time.decade":
                return By(items, i => i.Year is > 1880 and < 2200 ? $"{i.Year / 10 * 10}s" : null, d => d)
                       .OrderBy(g => g.Key).ToList();

            case "time.year":
                return By(items, i => i.Year is > 1880 and < 2200 ? i.Year.ToString() : null, y => y);

            case "media.edition":
                return By(items, i => string.IsNullOrWhiteSpace(i.Edition) ? null : i.Edition.Trim(), e => e);

            case "location.country":  return ByMany(items, i => i.Countries);
            case "people.actor":      return ByMany(items, i => i.Actors);
            case "people.director":   return ByMany(items, i => i.Directors);

            case "location.language":
                // English is most libraries' default, so a collection of it would just be "almost everything";
                // it's in the "Choose…" list, unticked, for anyone who does want it.
                return By(items, i => LanguageName(i.ContentLanguage), l => l);

            case "time.best_of_year":
                return items.Where(i => i.Year != null && i.ImdbRating != null)
                            .GroupBy(i => i.Year!.Value)
                            .Where(g => g.Count() >= min)
                            .Select(g => new Group(g.Key.ToString(), g.Key.ToString(),   // "Best of …" is the title format
                                g.OrderByDescending(i => i.ImdbRating).ThenBy(i => i.Title).Take(10).ToList()))
                            .ToList();
        }
        return new();
    }

    private static List<Group> By(List<LibraryItem> items, Func<LibraryItem, string?> key, Func<string, string> title) =>
        items.Select(i => (K: key(i), Item: i)).Where(x => !string.IsNullOrEmpty(x.K))
             .GroupBy(x => x.K!, StringComparer.OrdinalIgnoreCase)
             .Select(g => new Group(Slug(g.Key), title(g.First().K!), g.Select(x => x.Item).ToList())).ToList();

    // Multi-valued fields ("A|B|C"): each value makes its own collection.
    private static List<Group> ByMany(List<LibraryItem> items, Func<LibraryItem, string?> field) =>
        items.SelectMany(i => (field(i) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Distinct(StringComparer.OrdinalIgnoreCase).Select(v => (V: v, Item: i)))
             .GroupBy(x => x.V, StringComparer.OrdinalIgnoreCase)
             .Select(g => new Group(Slug(g.Key), g.First().V, g.Select(x => x.Item).ToList())).ToList();

    // ISO 639-1 → English name for the languages libraries commonly hold; anything else shows its code.
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["fr"] = "French", ["de"] = "German", ["es"] = "Spanish", ["it"] = "Italian", ["pt"] = "Portuguese",
        ["ja"] = "Japanese", ["ko"] = "Korean", ["zh"] = "Chinese", ["cn"] = "Cantonese", ["hi"] = "Hindi", ["ta"] = "Tamil",
        ["te"] = "Telugu", ["ml"] = "Malayalam", ["bn"] = "Bengali", ["ru"] = "Russian", ["sv"] = "Swedish", ["da"] = "Danish",
        ["no"] = "Norwegian", ["nb"] = "Norwegian", ["fi"] = "Finnish", ["nl"] = "Dutch", ["pl"] = "Polish", ["tr"] = "Turkish",
        ["th"] = "Thai", ["id"] = "Indonesian", ["tl"] = "Filipino", ["vi"] = "Vietnamese", ["ar"] = "Arabic", ["he"] = "Hebrew",
        ["fa"] = "Persian", ["el"] = "Greek", ["cs"] = "Czech", ["hu"] = "Hungarian", ["ro"] = "Romanian", ["uk"] = "Ukrainian",
        ["is"] = "Icelandic", ["ga"] = "Irish", ["xx"] = "No Language",
    };

    private static string? LanguageName(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : Languages.TryGetValue(code.Trim(), out var n) ? n : code.Trim().ToUpperInvariant();

    private static string Slug(string s) => s.Trim().ToLowerInvariant();

    // Servers prefix ratings with the rating country: Jellyfin "AU-MA 15+", Plex "au/M". The collection is just
    // "Rated MA 15+". Only real country codes are stripped — "PG-13", "TV-MA" and "NC-17" start with two letters
    // and a dash too.
    private static readonly HashSet<string> RatingCountries = new(StringComparer.OrdinalIgnoreCase)
        { "AU", "NZ", "GB", "UK", "US", "CA", "IE", "DE", "FR", "NL", "BE", "ES", "IT", "PT", "BR", "MX", "JP", "KR",
          "IN", "SE", "NO", "DK", "FI", "CH", "AT", "PL", "RU", "ZA", "SG", "HK", "TW", "PH", "MY" };

    private static string? RatingLabel(string? rating)
    {
        var r = rating?.Trim();
        if (string.IsNullOrEmpty(r)) return null;
        if (r.Length > 3 && (r[2] == '-' || r[2] == '/') && RatingCountries.Contains(r[..2])) r = r[3..].Trim();
        return r.Length == 0 ? null : r;
    }

    // TMDB names every storefront and add-on channel ("HBO Max Amazon Channel", "Philo", "fuboTV"…). Like Kometa,
    // only the major services get a collection, under their everyday names; the rest are left out.
    private static string? StreamingLabel(string? provider)
    {
        var p = provider?.Trim().ToLowerInvariant() ?? "";
        if (p.Length == 0 || p.Contains("channel") && !p.StartsWith("netflix")) return null;   // add-on channels
        if (p.StartsWith("netflix"))                              return "Netflix";
        if (p.StartsWith("disney"))                               return "Disney+";
        if (p.StartsWith("hbo max") || p == "max" || p.StartsWith("max "))  return "Max";
        if (p.StartsWith("amazon prime") || p.StartsWith("prime video"))     return "Prime Video";
        if (p.StartsWith("hulu"))                                 return "Hulu";
        if (p.StartsWith("apple tv"))                             return "Apple TV+";
        if (p.StartsWith("paramount"))                            return "Paramount+";
        if (p.StartsWith("peacock"))                              return "Peacock";
        return null;
    }

    private static string? ResolutionLabel(string? res) => res?.Trim().ToLowerInvariant() switch
    {
        "4k" or "2160" or "2160p" or "uhd" => "4K",
        "1080" or "1080p"                  => "1080p",
        "720" or "720p"                    => "720p",
        null or ""                         => null,
        _                                  => "SD",
    };

    // External lists are fetched once per plan even when several sections use them.
    private readonly Dictionary<int, Dictionary<string, int>?> _listCache = new();
    private readonly Dictionary<string, List<string>?> _chartCache = new();

    private async Task<Dictionary<string, int>?> ListRanksAsync(MdbListClient mdb, int listId, CancellationToken ct)
    {
        if (!_listCache.TryGetValue(listId, out var r))
        {
            r = await mdb.GetListRanksAsync(listId, ct);
            if (r != null && r.Count < 10) r = null;   // a near-empty answer means the list is broken — don't act on it
            _listCache[listId] = r;
        }
        return r;
    }

    private async Task<List<string>?> TmdbChartAsync(TmdbClient tmdb, string chart, bool isMovie, int pages, CancellationToken ct)
    {
        var key = $"{chart}|{isMovie}";
        if (!_chartCache.TryGetValue(key, out var ids)) _chartCache[key] = ids = await tmdb.GetChartIdsAsync(chart, isMovie, pages, ct);
        return ids;
    }

    public const int CurrentTmdbDetails = 2;

    /// <summary>The TMDB details the dynamic sets use (genres, franchise, countries, cast, directors).</summary>
    public static void StoreTmdbDetails(LibraryItem i, TmdbEnrichedData e)
    {
        i.Genres    = string.Join("|", e.Genres);      // "" = TMDB has none (so it isn't asked again)
        i.Countries = string.Join("|", e.Countries);
        i.Actors    = string.Join("|", e.Actors);
        i.Directors = string.Join("|", e.Directors);
        if (i.MediaType == MediaType.Movie) { i.TmdbCollectionId = e.CollectionId; i.TmdbCollectionName = e.CollectionName; }
        i.TmdbDetailsVersion = CurrentTmdbDetails;
    }

    public static bool NeedsTmdbDetails(string setKey) => setKey is "content.genre" or "content.franchise"
        or "location.country" or "people.actor" or "people.director";

    /// <summary>
    /// Titles scanned before these details were collected don't have them yet: fetch them from TMDB now, once
    /// per title (TmdbDetailsVersion records what's been gathered).
    /// </summary>
    private async Task BackfillTmdbAsync(List<LibraryItem> items, PostarrDbContext db, Plan plan, CancellationToken ct)
    {
        var tmdb = _factory.BuildTmdbClient();
        var todo = items.Where(i => i.TmdbDetailsVersion < CurrentTmdbDetails && !string.IsNullOrEmpty(i.TmdbId)
                                    && !_noTmdbDetails.ContainsKey(i.Id)).ToList();
        if (tmdb == null || todo.Count == 0) return;
        int done = 0, missed = 0;
        using var throttle = new SemaphoreSlim(6);
        // Saved in chunks so a stopped or interrupted run keeps what it already fetched (a big library takes minutes).
        foreach (var chunk in todo.Chunk(60))
        {
            await Task.WhenAll(chunk.Select(async i =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    var e = i.MediaType == MediaType.Movie ? await tmdb.GetMovieEnrichedAsync(i.TmdbId!, ct)
                                                           : await tmdb.GetShowEnrichedAsync(i.TmdbId!, ct);
                    if (e != null) StoreTmdbDetails(i, e);
                    else { _noTmdbDetails[i.Id] = 0; Interlocked.Increment(ref missed); }   // TMDB has nothing for it — don't ask again this session
                }
                finally
                {
                    Progress = BackfillProgress = $"Getting genre, franchise, country & cast data from TMDB ({Interlocked.Increment(ref done)}/{todo.Count}, first time only)…";
                    throttle.Release();
                }
            }));
            await db.SaveChangesAsync(ct);
        }
        plan.Notes.Add($"Fetched genre, franchise, country and cast data from TMDB for {todo.Count - missed} titles (one-off)."
                       + (missed > 0 ? $" TMDB had nothing for {missed} others (bad or missing ID) — they're left out of these sets." : ""));
    }

    // Titles TMDB returned nothing for (404, bad ID, rate limit). Without this they stay "pending" forever and the
    // pending check would restart the backfill endlessly. Cleared on restart, so they get another try then.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _noTmdbDetails = new();

    // ── Background TMDB backfill for the "Choose…" lists and Preview: those are plain HTTP requests, and a first-time
    //    fetch on a big library outlasts a proxy's timeout (Cloudflare 524), so the UI starts it here and polls. ─────
    private static int _backfillBusy;
    public static bool    BackfillRunning  => Volatile.Read(ref _backfillBusy) == 1;
    public static string? BackfillProgress { get; private set; }

    /// <summary>Titles still missing the TMDB details the dynamic sets use (0 when TMDB isn't configured).</summary>
    public async Task<int> PendingTmdbDetailsAsync(CancellationToken ct)
    {
        if (_factory.BuildTmdbClient() == null) return 0;
        var server = _factory.MediaServerName;
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = await db.LibraryItems.Where(i => i.ServerType == server && i.TmdbDetailsVersion < CurrentTmdbDetails
                                                   && i.TmdbId != null && i.TmdbId != "").Select(i => i.Id).ToListAsync(ct);
        return ids.Count(id => !_noTmdbDetails.ContainsKey(id));
    }

    public static void StartBackfillInBackground(IServiceScopeFactory scopes)
    {
        if (Interlocked.CompareExchange(ref _backfillBusy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            try
            {
                var svc = scope.ServiceProvider.GetRequiredService<AutoCollectionService>();
                var server = svc._factory.MediaServerName;
                using var db = await svc._dbFactory.CreateDbContextAsync();
                var items = await db.LibraryItems.Where(i => i.ServerType == server).ToListAsync();
                await svc.BackfillTmdbAsync(items, db, new Plan(), CancellationToken.None);
            }
            catch (Exception ex) { scope.ServiceProvider.GetRequiredService<ILogger<AutoCollectionService>>().LogError(ex, "TMDB details backfill crashed"); }
            finally { BackfillProgress = null; if (!IsRunning) Progress = null; Volatile.Write(ref _backfillBusy, 0); }
        });
    }

    // ── Custom collections (the user's own lists) ────────────────────────────
    // "Marvel movies in story order": a collection whose titles come from an MDBList / TMDB list or a typed list,
    // matched to what's in the library, optionally kept in the list's own order on the server.

    private readonly Dictionary<string, (List<ListEntry>? Entries, string? Error)> _entryCache = new();
    private readonly Dictionary<string, string> _entryNotes = new();   // list key → e.g. "12 episodes/seasons were skipped"
    private string? EntryNote(CustomCollectionConfig c) => _entryNotes.GetValueOrDefault($"{c.Source}|{c.ListRef.Trim()}");

    private async Task<(List<ListEntry>? Entries, string? Error)> EntriesAsync(CustomCollectionConfig c, CancellationToken ct)
    {
        if (c.Source == "titles")
        {
            var t = ListEntryParser.ParseTitles(c.Titles);
            return t.Count == 0 ? (null, "The list is empty.") : (t, null);
        }
        var key = $"{c.Source}|{c.ListRef.Trim()}";
        if (_entryCache.TryGetValue(key, out var hit)) return hit;
        (List<ListEntry>?, string?) r;
        if (c.Source == "mdblist")
        {
            var path = ListEntryParser.MdbListPath(c.ListRef);
            var mdb  = _factory.BuildMdbListClient();
            if (path == null) r = (null, "Not an MDBList list address — use a link like https://mdblist.com/lists/username/list-name.");
            else if (mdb == null) r = (null, "Needs an MDBList key (Settings).");
            else
            {
                var l = await mdb.GetListEntriesAsync(path, ct);
                r = l == null ? (null, "Couldn't read that list from MDBList (is it public?).")
                  : l.Count == 0 ? (null, "That list is empty.") : (l, null);
            }
        }
        else if (c.Source == "trakt")
        {
            var path  = ListEntryParser.TraktPath(c.ListRef);
            var trakt = _factory.BuildTraktClient();
            if (path == null) r = (null, "Not a Trakt list address — use a link like https://trakt.tv/users/username/lists/list-name.");
            else if (trakt == null) r = (null, "Needs a Trakt Client ID (Settings).");
            else
            {
                var l = await trakt.GetListEntriesAsync(path, ct);
                if (trakt.SkippedItems > 0)
                    _entryNotes[key] = $"{trakt.SkippedItems} entries in that list are episodes or seasons, which a movie/show collection can't hold — skipped";
                r = l == null ? (null, "Couldn't read that list from Trakt (is it public?).")
                  : l.Count == 0 ? (null, "That list is empty.") : (l, null);
            }
        }
        else if (c.Source == "tmdb")
        {
            var id   = ListEntryParser.TmdbListId(c.ListRef);
            var tmdb = _factory.BuildTmdbClient();
            if (id == null) r = (null, "Not a TMDB list address — use a link like https://www.themoviedb.org/list/8136.");
            else if (tmdb == null) r = (null, "Needs a TMDB key (Settings).");
            else
            {
                var l = await tmdb.GetListEntriesAsync(id, ct);
                r = l == null ? (null, "Couldn't read that list from TMDB (is it public?).")
                  : l.Count == 0 ? (null, "That list is empty.") : (l, null);
            }
        }
        else r = (null, "Unknown list type.");
        return _entryCache[key] = r;
    }

    private static string NormTitle(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch == '&') sb.Append("and");
            else if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        var n = sb.ToString();
        return n.StartsWith("the") && n.Length > 5 ? n[3..] : n;
    }

    /// <summary>The library items in one section that the list names, in list order; matchedEntries collects which entries hit.</summary>
    private static List<LibraryItem> MatchItems(List<ListEntry> entries, List<LibraryItem> secItems, bool isShow, HashSet<int> matchedEntries)
    {
        var byImdb  = secItems.Where(i => !string.IsNullOrEmpty(i.ImdbId)).GroupBy(i => i.ImdbId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byTmdb  = secItems.Where(i => !string.IsNullOrEmpty(i.TmdbId)).GroupBy(i => i.TmdbId!).ToDictionary(g => g.Key, g => g.First());
        var byTitle = secItems.GroupBy(i => NormTitle(i.Title)).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<LibraryItem>(); var seen = new HashSet<int>();
        for (int n = 0; n < entries.Count; n++)
        {
            var e = entries[n];
            if (e.IsShow.HasValue && e.IsShow != isShow) continue;
            LibraryItem? hit = null;
            if (e.ImdbId != null && byImdb.TryGetValue(e.ImdbId, out var a)) hit = a;
            else if (e.TmdbId != null && byTmdb.TryGetValue(e.TmdbId, out var b)) hit = b;
            else if (e.Title != null && byTitle.TryGetValue(NormTitle(e.Title), out var cands))
                hit = cands.FirstOrDefault(i => e.Year == null || i.Year == null || Math.Abs(i.Year.Value - e.Year.Value) <= 1);
            if (hit == null) continue;
            matchedEntries.Add(n);
            if (seen.Add(hit.Id)) result.Add(hit);
        }
        return result;
    }

    public record CustomResolved(List<(PlexSection Sec, List<LibraryItem> Items)> PerSection, int Total, List<string> Missing, string? Error);

    private async Task<CustomResolved> ResolveCustomAsync(CustomCollectionConfig c, List<PlexSection> sections, List<LibraryItem> items, CancellationToken ct)
    {
        var (entries, error) = await EntriesAsync(c, ct);
        if (entries == null) return new(new(), 0, new(), error);
        var perSection = new List<(PlexSection, List<LibraryItem>)>();
        var matched = new HashSet<int>();
        foreach (var sec in sections)
        {
            var isShow = sec.Type == "show";
            if (isShow ? !c.Shows : !c.Movies) continue;
            var hits = MatchItems(entries, items.Where(i => i.PlexLibrarySectionId == sec.Key).ToList(), isShow, matched);
            if (c.Order == "release") hits = hits.OrderBy(i => i.Year ?? 9999).ToList();   // stable: same year keeps list order
            if (hits.Count > 0) perSection.Add((sec, hits));
        }
        bool Applies(ListEntry e) => e.IsShow == null || (e.IsShow == true ? c.Shows : c.Movies);
        var total   = entries.Count(Applies);
        var missing = entries.Select((e, n) => (e, n)).Where(x => Applies(x.e) && !matched.Contains(x.n))
                             .Select(x => x.e.Title != null ? (x.e.Year != null ? $"{x.e.Title} ({x.e.Year})" : x.e.Title)
                                          : x.e.ImdbId != null ? $"imdb:{x.e.ImdbId}" : $"tmdb:{x.e.TmdbId}").ToList();
        return new(perSection, total, missing, null);
    }

    private async Task PlanCustomAsync(AutoCollectionSettings cfg, List<PlexSection> sections, List<LibraryItem> items,
        List<Desired> desired, HashSet<string> failedSets, Plan plan, CancellationToken ct)
    {
        foreach (var c in cfg.Custom.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Name)))
        {
            Progress = $"Reading list for {c.Name}…";
            var r = await ResolveCustomAsync(c, sections, items, ct);
            if (r.Error != null)
            {
                failedSets.Add($"{CustomSetKey}|{c.Id}");   // keep whatever is already on the server
                plan.Notes.Add($"{c.Name}: {r.Error} — left as it was.");
                continue;
            }
            var found = r.Total - r.Missing.Count;
            plan.Notes.Add($"{c.Name}: {found} of {r.Total} titles are in your library" +
                           (r.Missing.Count > 0 ? $" (not found: {string.Join(", ", r.Missing.Take(6))}{(r.Missing.Count > 6 ? $" and {r.Missing.Count - 6} more" : "")})." : ".") +
                           (EntryNote(c) is { } skipNote ? $" Note: {skipNote}." : ""));
            foreach (var (sec, hits) in r.PerSection)
                desired.Add(new Desired(CustomSetKey, c.Id, c.Name.Trim(), sec,
                    hits.Select(i => i.PlexRatingKey).Distinct().ToList(), null, c.Order));
        }
    }

    /// <summary>
    /// A list's titles as editable text for the "typed titles" box, in list order — "imdb:tt… # Title (Year)" where the list
    /// carries an IMDb id (exact matching), otherwise "Title (Year)". Every title is included, whether or not you own it.
    /// </summary>
    public async Task<(string? Text, int Count, string? Error)> ImportTitlesAsync(CustomCollectionConfig c, CancellationToken ct)
    {
        var (entries, error) = await EntriesAsync(c, ct);
        if (entries == null) return (null, 0, error);
        static string Label(ListEntry e) => e.Title == null ? "" : e.Year != null ? $"{e.Title} ({e.Year})" : e.Title;
        var lines = entries.Select(e =>
            e.ImdbId != null ? (e.Title != null ? $"imdb:{e.ImdbId} # {Label(e)}" : $"imdb:{e.ImdbId}")
          : e.Title != null  ? Label(e)
          : e.TmdbId != null ? $"tmdb:{e.TmdbId}" : null).OfType<string>().ToList();
        return (string.Join("\n", lines), lines.Count, null);
    }

    /// <summary>For the editor's "Check list" button: what a custom collection would hold, without changing anything.</summary>
    public async Task<object> TestCustomAsync(CustomCollectionConfig c, CancellationToken ct)
    {
        var client = _factory.BuildMediaServerClient() ?? throw new InvalidOperationException("No media server configured.");
        var server = _factory.MediaServerName;
        var sections = await client.GetLibrarySectionsAsync(ct);
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = await db.LibraryItems.Where(i => i.ServerType == server).ToListAsync(ct);
        var r = await ResolveCustomAsync(c, sections, items, ct);
        return new
        {
            error = r.Error, total = r.Total, found = r.Total - r.Missing.Count, missing = r.Missing, note = EntryNote(c),
            matched = r.PerSection.SelectMany(p => p.Items.Select(i => new { i.Title, i.Year })).DistinctBy(x => (x.Title, x.Year)).ToList(),
        };
    }

    /// <summary>
    /// Puts a collection into the list's order on the server (or back to the server's own when the collection
    /// isn't ordered any more). Returns the hash to remember, or null when there is no set order.
    /// </summary>
    private async Task<string?> ApplyOrderAsync(IMediaServerClient client, Desired d, string collectionId, CancellationToken ct)
    {
        await client.SetCollectionOrderAsync(d.Section, collectionId, d.OrderMode, d.ItemKeys, ct);
        return d.Ordered ? OrderHashOf(d.ItemKeys) : null;
    }
}
