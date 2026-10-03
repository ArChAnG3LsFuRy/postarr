using System.Net.Http.Headers;
using System.Text.Json;
using Postarr.MediaServers;
using Postarr.Models;
using Postarr.Plex;

namespace Postarr.Jellyfin;

/// <summary>
/// Jellyfin implementation of <see cref="IMediaServerClient"/>. Item keys are Jellyfin item IDs
/// (stored in the same PlexRatingKey columns Plex keys use). Quality info is translated into the
/// same vocabulary PlexClient produces ("4k"/"1080"…, "DV-HDR"/"HDR10+"/"HDR"…, "truehd atmos"…)
/// so the overlay renderer draws identical badges without knowing which server is behind it.
///
/// Covers library, seasons, quality, collections (BoxSets), poster/background upload, and the server's
/// current artwork. Live change notifications come from <see cref="JellyfinLiveUpdateService"/>.
/// </summary>
public class JellyfinClient : IMediaServerClient
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;
    private readonly string     _apiKey;
    private readonly bool       _emby;
    private string?             _userQuery;   // "&userId=…" once resolved ("" if it couldn't be)

    private string ServerName => _emby ? "Emby" : "Jellyfin";

    /// <param name="emby">Emby mode: Emby's API is Jellyfin's ancestor, so the same calls work once
    /// they go under Emby's /emby prefix with its X-Emby-Token header.</param>
    public JellyfinClient(HttpClient http, string baseUrl, string apiKey, bool emby = false)
    {
        _http    = http;
        _emby    = emby;
        _baseUrl = baseUrl.TrimEnd('/');
        if (emby && !_baseUrl.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) _baseUrl += "/emby";
        _apiKey  = apiKey;
    }

    /// <summary>
    /// HTTP handler that connects over IPv4 before IPv6. "localhost" resolves to ::1 first, Jellyfin
    /// listens on IPv4 only by default, and Windows takes ~2 s to report a refused connection — so every
    /// new connection to http://localhost:8096 stalled 2 s before falling back (measured: 2.05 s vs 0.02 s
    /// for 127.0.0.1). Trying IPv4 first removes that; IPv6-only hosts still work via the fallback.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new() { ConnectCallback = ConnectIpv4FirstAsync };

    private static async ValueTask<Stream> ConnectIpv4FirstAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        Exception? last = null;
        foreach (var ip in addresses.OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 1 : 0))
        {
            var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream,
                                                       System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new System.Net.IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) { socket.Dispose(); last = ex; }
        }
        throw last ?? new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
    }

    private HttpRequestMessage Req(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, _baseUrl + path);
        // The Authorization header is Jellyfin's current scheme; the legacy X-Emby-Token header and
        // api_key query parameter are disabled by default in newer releases. Header, not query, also
        // keeps the key out of URLs (and therefore out of logs).
        r.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{_apiKey}\"");
        if (_emby) r.Headers.TryAddWithoutValidation("X-Emby-Token", _apiKey);
        return r;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var req  = Req(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    // /Items is scoped by user on older servers, so pass an admin user's ID when one is available.
    // An API key is admin-level, so /Users is readable; if it isn't, carry on without the parameter.
    private async Task<string> UserQueryAsync(CancellationToken ct)
    {
        if (_userQuery != null) return _userQuery;
        try
        {
            // Emby has no plain GET /Users; it's /Users/Query, which wraps the list in "Items".
            using var doc = await GetJsonAsync(_emby ? "/Users/Query" : "/Users", ct);
            string? pick = null;
            foreach (var u in _emby ? Items(doc) : doc.RootElement.EnumerateArray())
            {
                var id    = Str(u, "Id");
                var admin = u.TryGetProperty("Policy", out var pol) && pol.TryGetProperty("IsAdministrator", out var a)
                            && a.ValueKind == JsonValueKind.True;
                if (id == null) continue;
                pick ??= id;
                if (admin) { pick = id; break; }
            }
            _userQuery = pick == null ? "" : $"&userId={pick}";
        }
        catch { _userQuery = ""; }
        return _userQuery;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // /System/Info (unlike /System/Info/Public) requires a valid key, so this checks both.
            using var req = Req(HttpMethod.Get, "/System/Info");
            return (await _http.SendAsync(req, ct)).IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Library ──────────────────────────────────────────────────────────────

    public async Task<List<PlexSection>> GetLibrarySectionsAsync(CancellationToken ct = default)
    {
        // Jellyfin: GET /Library/VirtualFolders (a bare array, id in "ItemId"). Emby has no GET there; its
        // /Library/MediaFolders returns the same library folders as items (id in "Id").
        using var doc = await GetJsonAsync(_emby ? "/Library/MediaFolders" : "/Library/VirtualFolders", ct);
        var list = new List<PlexSection>();
        foreach (var f in _emby ? Items(doc) : doc.RootElement.EnumerateArray())
        {
            var type = Str(f, "CollectionType") switch { "movies" => "movie", "tvshows" => "show", _ => null };
            var id   = Str(f, _emby ? "Id" : "ItemId");
            if (type == null || string.IsNullOrEmpty(id)) continue;   // music, books, mixed… aren't posters we manage
            list.Add(new PlexSection { Key = id!, Title = Str(f, "Name") ?? "", Type = type });
        }
        return list;
    }

    // Extra fields for movie/show listings. Emby leaves out even the year and age rating unless asked; Jellyfin
    // always sends those, and its Fields list is a fixed set that doesn't name them, so they're only added for Emby.
    private string ItemFields => "ProviderIds,MediaSources,Studios,DateCreated" + (_emby ? ",ProductionYear,OfficialRating" : "");

    public async Task<List<PlexMediaItem>> GetSectionItemsAsync(PlexSection section, CancellationToken ct = default)
    {
        var isShow = section.Type == "show";
        var path   = $"/Items?ParentId={section.Key}&Recursive=true&IncludeItemTypes={(isShow ? "Series" : "Movie")}"
                   + $"&Fields={ItemFields}{await UserQueryAsync(ct)}";
        using var doc = await GetJsonAsync(path, ct);
        var items = new List<PlexMediaItem>();
        foreach (var e in Items(doc))
        {
            var item = ParseItem(e, isShow);
            if (string.IsNullOrEmpty(item.RatingKey)) continue;
            if (isShow) { item.Seasons = await GetSeasonsAsync(item.RatingKey, ct); item.LatestSeasonAddedUtc = LatestSeason(item.Seasons); }
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Turns the item IDs from a "library changed" notification into the movies/shows to refresh, each
    /// with the library it belongs to. Episodes and seasons map to their show (so new seasons get picked
    /// up); anything outside a movie/show library is dropped. Fetches only these items, not whole libraries.
    /// </summary>
    public async Task<List<(PlexSection Section, PlexMediaItem Item)>> ResolveAddedAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var user    = await UserQueryAsync(ct);
        var targets = new Dictionary<string, bool>();   // item id → is show
        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            using var doc = await GetJsonAsync($"/Items?Ids={string.Join(",", chunk)}{user}", ct);
            foreach (var e in Items(doc))
            {
                var id = Str(e, "Id");
                switch (Str(e, "Type"))
                {
                    case "Movie":             if (id != null) targets[id] = false; break;
                    case "Series":            if (id != null) targets[id] = true;  break;
                    case "Season" or "Episode": var sid = Str(e, "SeriesId"); if (sid != null) targets[sid] = true; break;
                }
            }
        }
        if (targets.Count == 0) return new();

        var sections = await GetLibrarySectionsAsync(ct);
        var result   = new List<(PlexSection, PlexMediaItem)>();
        foreach (var (id, isShow) in targets)
        {
            // The library an item lives in is one of its ancestors (the library's root folder).
            PlexSection? sec = null;
            using (var anc = await GetJsonAsync($"/Items/{id}/Ancestors?{user.TrimStart('&')}", ct))
                foreach (var a in anc.RootElement.EnumerateArray())
                {
                    var aid = Str(a, "Id");
                    sec = sections.FirstOrDefault(s => s.Key == aid && s.Type == (isShow ? "show" : "movie"));
                    if (sec != null) break;
                }
            if (sec == null) continue;

            using var doc = await GetJsonAsync($"/Items?Ids={id}&Fields={ItemFields}{user}", ct);
            var e = Items(doc).FirstOrDefault();
            if (e.ValueKind != JsonValueKind.Object) continue;
            var item = ParseItem(e, isShow);
            if (isShow) { item.Seasons = await GetSeasonsAsync(id, ct); item.LatestSeasonAddedUtc = LatestSeason(item.Seasons); }
            result.Add((sec, item));
        }
        return result;
    }

    /// <summary>The auth header with a client identity, which a live-update WebSocket session needs.</summary>
    public static string SessionAuthHeader(string apiKey) =>
        $"MediaBrowser Client=\"Postarr\", Device=\"Postarr\", DeviceId=\"postarr-live-updates\", Version=\"1.0\", Token=\"{apiKey}\"";

    private async Task<List<PlexSeason>> GetSeasonsAsync(string seriesId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/Shows/{seriesId}/Seasons?Fields=ChildCount,DateCreated{await UserQueryAsync(ct)}", ct);
        var seasons = new List<PlexSeason>();
        foreach (var s in Items(doc))
        {
            var num = Int(s, "IndexNumber");
            var id  = Str(s, "Id");
            if (num == null || id == null) continue;
            seasons.Add(new PlexSeason
            {
                RatingKey    = id,
                SeasonNumber = num.Value,
                Title        = Str(s, "Name") ?? $"Season {num}",
                LeafCount    = Int(s, "ChildCount"),
                ThumbPath    = HasPrimary(s) ? $"/Items/{id}/Images/Primary" : null,
                AddedAtUtc   = Date(s, "DateCreated"),
            });
        }
        return seasons;
    }

    // ── Collections ──────────────────────────────────────────────────────────
    // Jellyfin collections ("BoxSets") aren't tied to a library and can mix movies and shows, but the scan
    // asks per library section. So fetch every BoxSet once per scan, classify it by its contents (mostly
    // series → show collection, else movie collection), and give each to the first section of that type —
    // otherwise a collection would be listed once per library or bounce between them.
    private List<(PlexCollectionItem Item, bool IsShow)>? _boxSets;
    private readonly HashSet<string> _boxSetsClaimed = new();

    public async Task<List<PlexCollectionItem>> GetCollectionsAsync(PlexSection section, CancellationToken ct = default)
    {
        _boxSets ??= await LoadBoxSetsAsync(ct);
        var isShow = section.Type == "show";
        var result = new List<PlexCollectionItem>();
        foreach (var (item, boxIsShow) in _boxSets)
        {
            if (boxIsShow != isShow || !_boxSetsClaimed.Add(item.RatingKey)) continue;
            item.SectionKey = section.Key;
            item.MediaType  = isShow ? MediaType.Show : MediaType.Movie;
            result.Add(item);
        }
        return result;
    }

    private async Task<List<(PlexCollectionItem Item, bool IsShow)>> LoadBoxSetsAsync(CancellationToken ct)
    {
        var user = await UserQueryAsync(ct);
        using var doc = await GetJsonAsync($"/Items?IncludeItemTypes=BoxSet&Recursive=true&Fields=ProviderIds{user}", ct);
        var list = new List<(PlexCollectionItem, bool)>();
        foreach (var b in Items(doc))
        {
            var id = Str(b, "Id");
            if (id == null) continue;
            int movies = 0, series = 0;
            using (var kids = await GetJsonAsync($"/Items?ParentId={id}{user}", ct))
                foreach (var k in Items(kids))
                    switch (Str(k, "Type")) { case "Movie": movies++; break; case "Series": series++; break; }
            if (movies + series == 0) continue;   // empty, or holds things we don't manage (music, books…)

            list.Add((new PlexCollectionItem
            {
                RatingKey = id,
                Title     = Str(b, "Name") ?? "",
                ItemCount = movies + series,
                ThumbPath = HasPrimary(b) ? $"/Items/{id}/Images/Primary" : null,
                TmdbId    = ProviderId(b, "tmdb"),
            }, series > movies));
        }
        return list;
    }

    // ── Collections Postarr builds (BoxSets) ─────────────────────────────────
    // Same endpoints on Jellyfin and Emby (Emby's under /emby, added by the base URL). Ids go in batches so the
    // query string stays a sensible length.

    private const int CollectionBatch = 100;

    private async Task SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var req  = Req(method, path);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{ServerName} refused {method} {path.Split('?')[0]} ({(int)resp.StatusCode}): {body.Trim()}");
        }
    }

    public async Task<string> CreateCollectionAsync(PlexSection section, string title, IReadOnlyList<string> itemKeys, CancellationToken ct = default)
    {
        if (itemKeys.Count == 0) throw new ArgumentException("A collection needs at least one item.", nameof(itemKeys));
        using var req = Req(HttpMethod.Post,
            $"/Collections?Name={Uri.EscapeDataString(title)}&Ids={string.Join(",", itemKeys.Take(CollectionBatch))}&IsLocked=false");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{ServerName} refused to create '{title}' ({(int)resp.StatusCode}): {body.Trim()}");
        using var doc = JsonDocument.Parse(body);
        var id = Str(doc.RootElement, "Id") ?? throw new InvalidOperationException($"{ServerName} created '{title}' but didn't return its id.");
        if (itemKeys.Count > CollectionBatch) await AddToCollectionAsync(id, itemKeys.Skip(CollectionBatch).ToList(), ct);
        _boxSets = null;   // the cached BoxSet list is now out of date
        return id;
    }

    /// <summary>
    /// The Type of one item ("BoxSet", "Movie"…), or null if it doesn't exist. Emby hides BoxSets from a
    /// user-scoped /Items?Ids= query (it returns nothing), so ask without the user first, then with it.
    /// </summary>
    private async Task<string?> ItemTypeAsync(string id, CancellationToken ct)
    {
        foreach (var user in new[] { "", await UserQueryAsync(ct) }.Distinct())
        {
            using var doc = await GetJsonAsync($"/Items?Ids={id}{user}", ct);
            var item = Items(doc).FirstOrDefault();
            if (item.ValueKind == JsonValueKind.Object) return Str(item, "Type") ?? "";
        }
        return null;
    }

    public async Task<List<string>?> GetCollectionItemKeysAsync(string collectionId, CancellationToken ct = default)
    {
        if (await ItemTypeAsync(collectionId, ct) == null) return null;   // deleted on the server
        using var kids = await GetJsonAsync($"/Items?ParentId={collectionId}{await UserQueryAsync(ct)}", ct);
        return Items(kids).Select(k => Str(k, "Id")).OfType<string>().ToList();
    }

    public async Task AddToCollectionAsync(string collectionId, IReadOnlyList<string> itemKeys, CancellationToken ct = default)
    {
        foreach (var chunk in itemKeys.Chunk(CollectionBatch))
            await SendAsync(HttpMethod.Post, $"/Collections/{collectionId}/Items?Ids={string.Join(",", chunk)}", ct);
    }

    public async Task RemoveFromCollectionAsync(string collectionId, IReadOnlyList<string> itemKeys, CancellationToken ct = default)
    {
        foreach (var chunk in itemKeys.Chunk(CollectionBatch))
            await SendAsync(HttpMethod.Delete, $"/Collections/{collectionId}/Items?Ids={string.Join(",", chunk)}", ct);
    }

    public async Task RenameCollectionAsync(PlexSection section, string collectionId, string title, CancellationToken ct = default)
    {
        // No rename call: fetch the item's full record, change Name, post the record back (what the web UI does).
        var user = await UserQueryAsync(ct);
        var uid  = user.StartsWith("&userId=") ? user["&userId=".Length..] : null;
        var path = _emby && uid != null ? $"/Users/{uid}/Items/{collectionId}" : $"/Items/{collectionId}{(uid != null ? $"?userId={uid}" : "")}";
        using var get  = Req(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(get, ct);
        resp.EnsureSuccessStatusCode();
        var node = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
                   ?? throw new InvalidOperationException($"{ServerName} returned no data for collection {collectionId}.");
        if (node["Type"]?.GetValue<string>() != "BoxSet")
            throw new InvalidOperationException($"Refusing to rename {ServerName} item {collectionId}: it is not a collection.");
        node["Name"] = title;
        using var post = Req(HttpMethod.Post, $"/Items/{collectionId}");
        post.Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var done = await _http.SendAsync(post, ct);
        if (!done.IsSuccessStatusCode)
            throw new HttpRequestException($"{ServerName} refused the rename ({(int)done.StatusCode}): {(await done.Content.ReadAsStringAsync(ct)).Trim()}");
        _boxSets = null;
    }

    /// <summary>
    /// Reads (or, with write, sets) a BoxSet's DisplayOrder and returns what it was. Jellyfin knows "PremiereDate",
    /// "SortName" and "Default" (= the order the items were added in); Emby only PremiereDate and SortName. Nothing is
    /// posted when it already has the value.
    /// </summary>
    public async Task<string> DisplayOrderAsync(string collectionId, string? newValue, bool write, CancellationToken ct)
    {
        var user = await UserQueryAsync(ct);
        var uid  = user.StartsWith("&userId=") ? user["&userId=".Length..] : null;
        var path = _emby && uid != null ? $"/Users/{uid}/Items/{collectionId}" : $"/Items/{collectionId}{(uid != null ? $"?userId={uid}" : "")}";
        using var get  = Req(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(get, ct);
        resp.EnsureSuccessStatusCode();
        var node = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
                   ?? throw new InvalidOperationException($"{ServerName} returned no data for collection {collectionId}.");
        if (node["Type"]?.GetValue<string>() != "BoxSet")
            throw new InvalidOperationException($"Refusing to change {ServerName} item {collectionId}: it is not a collection.");
        var old = node["DisplayOrder"]?.GetValue<string>() ?? "";
        if (!write || old == newValue) return old;
        node["DisplayOrder"] = newValue;
        using var post = Req(HttpMethod.Post, $"/Items/{collectionId}");
        post.Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var done = await _http.SendAsync(post, ct);
        if (!done.IsSuccessStatusCode)
            throw new HttpRequestException($"{ServerName} refused the display order change ({(int)done.StatusCode}): {(await done.Content.ReadAsStringAsync(ct)).Trim()}");
        _boxSets = null;
        return old;
    }

    // Jellyfin: DisplayOrder "Default" shows a collection in the order its items were added (confirmed 2026-09-30), so a
    // custom order = that setting + the items added in the wanted order. Emby ignores it (its BoxSets only sort by release
    // date or A–Z), so a custom "list" order isn't possible there. "release" and "default" both mean release date.
    public async Task<bool> SetCollectionOrderAsync(PlexSection section, string collectionId, string mode, IReadOnlyList<string> orderedKeys, CancellationToken ct = default)
    {
        if (mode != "list") { await DisplayOrderAsync(collectionId, "PremiereDate", true, ct); return true; }
        if (_emby) return false;

        await DisplayOrderAsync(collectionId, "Default", true, ct);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var have = await GetCollectionItemKeysAsync(collectionId, ct) ?? new List<string>();
            if (have.SequenceEqual(orderedKeys)) return true;
            if (have.Count > 0) await RemoveFromCollectionAsync(collectionId, have, ct);
            await AddToCollectionAsync(collectionId, orderedKeys, ct);   // in the wanted order
            await Task.Delay(300, ct);
        }
        return (await GetCollectionItemKeysAsync(collectionId, ct) ?? new List<string>()).SequenceEqual(orderedKeys);
    }

    public async Task DeleteCollectionAsync(string collectionId, CancellationToken ct = default)
    {
        // Guard: DELETE /Items/{id} removes ANY item (a movie included), so confirm it's a BoxSet first.
        var type = await ItemTypeAsync(collectionId, ct);
        if (type == null) return;   // already gone
        if (type != "BoxSet")
            throw new InvalidOperationException($"Refusing to delete {ServerName} item {collectionId}: it is a '{type}', not a collection.");
        await SendAsync(HttpMethod.Delete, $"/Items/{collectionId}", ct);
        _boxSets = null;
    }

    // ── Quality ──────────────────────────────────────────────────────────────

    public async Task<(string? Resolution, string? DynamicRange, string? AudioCodec)> GetShowAggregateQualityAsync(string showKey, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"/Items?ParentId={showKey}&Recursive=true&IncludeItemTypes=Episode&Fields=MediaSources{await UserQueryAsync(ct)}", ct);
        var res = new List<string>(); var dr = new List<string>(); var ac = new List<string>();
        foreach (var ep in Items(doc))
        {
            var tmp = new PlexMediaItem();
            ExtractMedia(ep, tmp);
            if (!string.IsNullOrEmpty(tmp.VideoResolution))   res.Add(tmp.VideoResolution!);
            if (!string.IsNullOrEmpty(tmp.VideoDynamicRange)) dr.Add(tmp.VideoDynamicRange!);
            if (!string.IsNullOrEmpty(tmp.AudioCodec))        ac.Add(tmp.AudioCodec!);
        }

        // Same rule as PlexClient: the most common value across episodes, ties toward higher resolution.
        static int ResRank(string r) => r switch { "4k" => 5, "1080" => 4, "720" => 3, "576" => 2, "480" => 1, _ => 0 };
        static string? Mode(List<string> xs) => xs.Count == 0 ? null
            : xs.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenByDescending(g => ResRank(g.Key)).First().Key;
        return (Mode(res), Mode(dr), Mode(ac));
    }

    // Jellyfin's library listing already carries full stream data (unlike Plex's), so dynamic range and
    // language counts are set during GetSectionItemsAsync and there's nothing to refine.
    public Task<Dictionary<string, StreamDetails>> GetStreamDetailsAsync(IEnumerable<string> itemKeys, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, StreamDetails>());

    // ── Write artwork ────────────────────────────────────────────────────────

    public Task UploadPosterAsync(string itemKey, byte[] bytes, string contentType, CancellationToken ct = default)
        => UploadImageAsync($"/Items/{itemKey}/Images/Primary", bytes, contentType, ct);

    // Jellyfin keeps a *list* of backgrounds and an upload is added to it (even to /Backdrop/0), in an order
    // it re-sorts itself — and its web app can rotate through all of them. So the chosen background never
    // showed and they piled up (measured: 10 → 11 per apply, new one landing 4th). Replace instead: remove
    // the item's existing backgrounds, then upload, so the chosen one is the only one. Jellyfin can re-fetch
    // its own via Refresh metadata → Replace existing images.
    public async Task UploadBackgroundAsync(string itemKey, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        int count = 0;
        using (var doc = await GetJsonAsync($"/Items?Ids={itemKey}{await UserQueryAsync(ct)}", ct))
        {
            var item = Items(doc).FirstOrDefault();
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("BackdropImageTags", out var bt)
                && bt.ValueKind == JsonValueKind.Array)
                count = bt.GetArrayLength();
        }
        for (var i = count - 1; i >= 0; i--)   // highest index first, so the remaining positions don't shift
        {
            using var del  = Req(HttpMethod.Delete, $"/Items/{itemKey}/Images/Backdrop/{i}");
            using var resp = await _http.SendAsync(del, ct);   // best effort: a failed delete still gets the upload
        }
        await UploadImageAsync($"/Items/{itemKey}/Images/Backdrop", bytes, contentType, ct);
    }

    private async Task UploadImageAsync(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Post, path);
        // Jellyfin's image upload takes the file base64-encoded in the body, typed as the image's MIME.
        req.Content = new StringContent(Convert.ToBase64String(bytes));
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body   = await resp.Content.ReadAsStringAsync(ct);
            var reason = string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body.Trim();
            throw new HttpRequestException($"{ServerName} rejected the image upload ({(int)resp.StatusCode}): {reason}");
        }
    }

    // ── Artwork already on the server ────────────────────────────────────────
    // Jellyfin has no Plex-style list of alternate uploads, so this reports just the item's current
    // image as the selected one. That's what lets a custom upload display on its card afterwards.

    public Task<List<PlexPoster>> GetAvailablePostersAsync(string itemKey, CancellationToken ct = default)
        => CurrentImageAsync(itemKey, backdrop: false, ct);

    public Task<List<PlexPoster>> GetAvailableArtsAsync(string itemKey, CancellationToken ct = default)
        => CurrentImageAsync(itemKey, backdrop: true, ct);

    private async Task<List<PlexPoster>> CurrentImageAsync(string itemKey, bool backdrop, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/Items?Ids={itemKey}{await UserQueryAsync(ct)}", ct);
        var item = Items(doc).FirstOrDefault();
        if (item.ValueKind != JsonValueKind.Object) return new();

        string? tag = null;
        if (backdrop)
        {
            if (item.TryGetProperty("BackdropImageTags", out var bt) && bt.ValueKind == JsonValueKind.Array && bt.GetArrayLength() > 0)
                tag = bt[0].GetString();
        }
        else if (item.TryGetProperty("ImageTags", out var it) && it.TryGetProperty("Primary", out var p))
            tag = p.GetString();
        if (string.IsNullOrEmpty(tag)) return new();

        // The tag changes whenever the image does, so it doubles as a cache-buster for the proxy URL.
        var key = backdrop ? $"/Items/{itemKey}/Images/Backdrop/0?tag={tag}" : $"/Items/{itemKey}/Images/Primary?tag={tag}";
        return new() { new PlexPoster(key, true, "local") };
    }

    public Task SelectPosterAsync(string itemKey, string selectValue, CancellationToken ct = default)
        => SelectImageAsync(itemKey, selectValue, backdrop: false, ct);

    public Task SelectArtAsync(string itemKey, string selectValue, CancellationToken ct = default)
        => SelectImageAsync(itemKey, selectValue, backdrop: true, ct);

    // Plex keeps every uploaded poster and switches between them; Jellyfin holds one per slot. So choosing
    // an image the server already has means: nothing to do if it's this item's current one, otherwise copy
    // it onto the item (fetch + upload), which is how Jellyfin sets artwork.
    private async Task SelectImageAsync(string itemKey, string imagePath, bool backdrop, CancellationToken ct)
    {
        var own = backdrop ? $"/Items/{itemKey}/Images/Backdrop" : $"/Items/{itemKey}/Images/Primary";
        if (imagePath.StartsWith(own, StringComparison.OrdinalIgnoreCase)) return;
        var img = await FetchImageAsync(imagePath, ct)
                  ?? throw new InvalidOperationException($"Couldn't read that image from {ServerName}.");
        if (backdrop) await UploadBackgroundAsync(itemKey, img.Bytes, img.ContentType, ct);
        else          await UploadPosterAsync(itemKey, img.Bytes, img.ContentType, ct);
    }

    // ── Read artwork ─────────────────────────────────────────────────────────

    /// <summary>Only Jellyfin image paths are fetched, so the proxy can't be pointed at other endpoints.</summary>
    public static bool IsImagePath(string? path) =>
        !string.IsNullOrEmpty(path) && path!.StartsWith("/Items/") && path.Contains("/Images/") && !path.Contains("..");

    public async Task<(byte[] Bytes, string ContentType)?> FetchImageAsync(string imagePath, CancellationToken ct = default)
    {
        if (!IsImagePath(imagePath)) return null;
        using var req = Req(HttpMethod.Get, imagePath);
        using var r   = await _http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode) return null;
        return (await r.Content.ReadAsByteArrayAsync(ct), r.Content.Headers.ContentType?.ToString() ?? "image/jpeg");
    }

    public async Task<byte[]?> DownloadCurrentPosterAsync(string? thumbPath, CancellationToken ct = default)
        => thumbPath == null ? null : (await FetchImageAsync(thumbPath, ct))?.Bytes;

    public async Task<byte[]?> DownloadCurrentBackgroundAsync(string? artPath, CancellationToken ct = default)
        => artPath == null ? null : (await FetchImageAsync(artPath, ct))?.Bytes;

    // ── Parsing ──────────────────────────────────────────────────────────────

    private static IEnumerable<JsonElement> Items(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("Items", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static PlexMediaItem ParseItem(JsonElement e, bool isShow)
    {
        var id   = Str(e, "Id") ?? "";
        var item = new PlexMediaItem
        {
            RatingKey     = id,
            Title         = Str(e, "Name") ?? "",
            Year          = Int(e, "ProductionYear"),
            MediaType     = isShow ? MediaType.Show : MediaType.Movie,
            ContentRating = Str(e, "OfficialRating"),
            ThumbPath     = HasPrimary(e) ? $"/Items/{id}/Images/Primary" : null,
            ArtPath       = e.TryGetProperty("BackdropImageTags", out var bt) && bt.ValueKind == JsonValueKind.Array
                            && bt.GetArrayLength() > 0 ? $"/Items/{id}/Images/Backdrop/0" : null,
            AddedAtUtc    = Date(e, "DateCreated"),
        };

        if (!isShow)
        {
            // Alternate versions of a movie are grouped as extra MediaSources.
            var sourcesOk = e.TryGetProperty("MediaSources", out var ms) && ms.ValueKind == JsonValueKind.Array;
            item.VersionCount = sourcesOk && ms.GetArrayLength() > 0 ? ms.GetArrayLength() : null;
            var ticks = Long(e, "RunTimeTicks");                                // 10,000,000 ticks per second
            if (ticks > 0) item.RuntimeMinutes = (int)Math.Round(ticks.Value / 600_000_000.0);
            item.VideoSource = MediaFileInfo.DetectSource(
                (sourcesOk && ms.GetArrayLength() > 0 ? Str(ms[0], "Path") : null) ?? Str(e, "Path"));
        }

        // Jellyfin lists a series' network under Studios, so it maps to Network for shows.
        string? studio = null;
        if (e.TryGetProperty("Studios", out var st) && st.ValueKind == JsonValueKind.Array && st.GetArrayLength() > 0)
            studio = Str(st[0], "Name");
        if (isShow) item.Network = studio; else item.Studio = studio;

        if (e.TryGetProperty("ProviderIds", out var pids) && pids.ValueKind == JsonValueKind.Object)
            foreach (var p in pids.EnumerateObject())
            {
                var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                if (string.IsNullOrWhiteSpace(v)) continue;
                switch (p.Name.ToLowerInvariant())
                {
                    case "tmdb": item.TmdbId = v; break;
                    case "tvdb": item.TvdbId = v; break;
                    case "imdb": item.ImdbId = v; break;
                }
            }

        ExtractMedia(e, item);
        return item;
    }

    private static void ExtractMedia(JsonElement e, PlexMediaItem target)
    {
        if (!e.TryGetProperty("MediaSources", out var sources) || sources.ValueKind != JsonValueKind.Array
            || sources.GetArrayLength() == 0) return;
        if (!sources[0].TryGetProperty("MediaStreams", out var streams) || streams.ValueKind != JsonValueKind.Array) return;

        JsonElement? video = null, audio = null, firstAudio = null;
        var audioLangs = new List<string?>(); var subLangs = new List<string?>();
        foreach (var s in streams.EnumerateArray())
        {
            var type = Str(s, "Type");
            if (type == "Video") video ??= s;
            else if (type == "Subtitle") subLangs.Add(Str(s, "Language"));
            else if (type == "Audio")
            {
                audioLangs.Add(Str(s, "Language"));
                firstAudio ??= s;
                if (audio == null && s.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True) audio = s;
            }
        }
        audio ??= firstAudio;

        if (video is { } v)
        {
            target.VideoResolution   = MapResolution(Int(v, "Width"), Int(v, "Height"));
            target.VideoDynamicRange = MapDynamicRange(Str(v, "VideoRangeType"), Str(v, "VideoRange"), Str(v, "ColorTransfer"),
                                                       Str(v, "ExtendedVideoType"), Str(v, "DisplayTitle"));
        }
        if (audio is { } a) target.AudioCodec = MapAudioCodec(Str(a, "Codec"), Str(a, "Profile"));
        var anyStreams = streams.GetArrayLength() > 0;
        target.AudioLanguageCount    = MediaFileInfo.CountLanguages(audioLangs, anyStreams);
        target.SubtitleLanguageCount = MediaFileInfo.CountLanguages(subLangs,   anyStreams);
    }

    private static DateTime? LatestSeason(List<PlexSeason> seasons) =>
        seasons.Where(x => x.SeasonNumber > 0).Max(x => x.AddedAtUtc);

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } v && DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    // Width first so cropped scope films (1920×800) still count as 1080.
    private static string? MapResolution(int? width, int? height)
    {
        if (width == null && height == null) return null;
        int w = width ?? 0, h = height ?? 0;
        if (w >= 3200 || h >= 2000) return "4k";
        if (w >= 1800 || h >= 1000) return "1080";
        if (w >= 1200 || h >= 700)  return "720";
        if (h >= 560)               return "576";
        if (h >= 400)               return "480";
        return "sd";
    }

    // Jellyfin's VideoRangeType values → the same strings PlexClient emits.
    private static string MapDynamicRange(string? rangeType, string? range, string? colorTransfer,
                                          string? extendedType, string? displayTitle)
    {
        // Emby has no VideoRangeType; it reports ExtendedVideoType ("DolbyVision", "Hdr10Plus", "Hdr10",
        // "HyperLogGamma"…) and a display-style VideoRange ("HDR 10", "Dolby Vision"…). Same rules as below.
        if (string.IsNullOrEmpty(rangeType))
        {
            bool pq = string.Equals(colorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase);
            var e = $"{extendedType} {range} {displayTitle}".ToUpperInvariant().Replace(" ", "");
            if (e.Contains("DOLBYVISION") || e.Contains("DOVI")) return pq ? "DV-HDR" : "DV";
            if (e.Contains("HDR10+") || e.Contains("HDR10PLUS")) return "HDR10+";
            if (e.Contains("HDR") || e.Contains("HLG") || e.Contains("HYPERLOGGAMMA") || pq) return "HDR";
            return "SDR";
        }

        var t = rangeType.ToUpperInvariant();
        // DOVIInvalid = Dolby Vision metadata Jellyfin can't use, so badge it by its base layer instead.
        if (t.StartsWith("DOVI") && t != "DOVIINVALID")
        {
            // Same rule as PlexClient: Dolby Vision on a PQ (HDR10) base layer is "DV-HDR"; on HLG/SDR it's
            // "DV". The transfer function is the direct signal — e.g. profile 7.6 reports as "DOVIWithEL",
            // whose name doesn't say HDR10 even though its base layer is. Fall back to the type name.
            bool pqBase = string.Equals(colorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
                       || t.Contains("HDR10") || t == "DOVIWITHEL";
            return pqBase ? "DV-HDR" : "DV";
        }
        if (t == "HDR10PLUS")          return "HDR10+";
        if (t is "HDR10" or "HLG")     return "HDR";
        if (t == "SDR")                return "SDR";
        return string.Equals(range, "HDR", StringComparison.OrdinalIgnoreCase) ? "HDR" : "SDR";
    }

    // The renderer matches on substrings ("dts"+"ma", "truehd"+"atmos", "dtsx"…), so fold the profile in
    // only where it distinguishes a badge; plain codecs ("aac", "ac3") must stay exact.
    private static string? MapAudioCodec(string? codec, string? profile)
    {
        if (string.IsNullOrEmpty(codec)) return null;
        var c = codec!.ToLowerInvariant();
        var p = (profile ?? "").ToLowerInvariant();
        if (c.StartsWith("dts"))                                   return $"dts {p}".Replace(":", "").Trim();   // "DTS:X" → dtsx
        if ((c is "truehd" or "eac3") && p.Contains("atmos"))      return $"{c} atmos";
        return c;
    }

    private static string? ProviderId(JsonElement e, string provider)
    {
        if (!e.TryGetProperty("ProviderIds", out var pids) || pids.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in pids.EnumerateObject())
            if (p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                return p.Value.GetString();
        return null;
    }

    private static bool HasPrimary(JsonElement e) =>
        e.TryGetProperty("ImageTags", out var it) && it.ValueKind == JsonValueKind.Object && it.TryGetProperty("Primary", out _);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
