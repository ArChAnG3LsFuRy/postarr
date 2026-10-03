using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Postarr.Data;
using Postarr.Services;

namespace Postarr.Jellyfin;

/// <summary>
/// Jellyfin's (and Emby's) equivalent of Plex's "library.new" webhook, with nothing to install on the
/// server side: while one of them is the active server and instant updates are on, keep its WebSocket open
/// and add newly-added movies/shows as soon as the server has finished adding them.
///
/// Only "added" items are acted on. Postarr's own poster uploads show up as "updated" items, so reacting
/// to updates would loop. Additions are batched until Jellyfin has been quiet for a minute: it adds
/// items in bursts and fetches their metadata (the TMDB IDs Postarr needs) shortly afterwards.
/// </summary>
public class JellyfinLiveUpdateService : BackgroundService
{
    private static readonly TimeSpan SettleDelay   = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryDelay    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettingsCheck = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SettingsRepository   _settings;
    private readonly ILogger<JellyfinLiveUpdateService> _logger;

    private readonly HashSet<string> _pending = new();
    private DateTime _lastAddedUtc;
    private string?  _announcedFor;   // server "connected" was last logged for — once per server, not per reconnect

    public JellyfinLiveUpdateService(IServiceScopeFactory scopeFactory, SettingsRepository settings,
                                     ILogger<JellyfinLiveUpdateService> logger)
    { _scopeFactory = scopeFactory; _settings = settings; _logger = logger; }

    // Everything that decides whether/where to connect; any change drops the connection so it's rebuilt.
    private record Connection(string Server, string BaseUrl, string ApiKey);

    private static Connection? Target(Models.AppSettings s)
    {
        if (!s.WebhookEnabled) return null;
        var (url, key) = MetadataClientFactory.ServerKey(s) switch
        {
            "Jellyfin" => (s.JellyfinBaseUrl, s.JellyfinApiKey),
            "Emby"     => (s.EmbyBaseUrl,     s.EmbyApiKey),
            _          => ("", ""),
        };
        return string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)
            ? null : new Connection(MetadataClientFactory.ServerKey(s), url, key);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _ = ProcessLoopAsync(stop);
        while (!stop.IsCancellationRequested)
        {
            var target = Target(_settings.Get());
            if (target == null) { _announcedFor = null; await Delay(SettingsCheck, stop); continue; }
            try
            {
                await ListenAsync(target, stop);
                await Delay(TimeSpan.FromSeconds(5), stop);   // closed normally — brief pause before reconnecting
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning("Jellyfin live updates disconnected: {Message}", ex.Message);
                await Delay(RetryDelay, stop);
            }
        }
    }

    private async Task ListenAsync(Connection target, CancellationToken stop)
    {
        // Jellyfin: /socket with the auth header. Emby: /embywebsocket at the server root (not under /emby),
        // which takes the key and a device id in the query string.
        var serverRoot = target.BaseUrl.TrimEnd('/');
        if (target.Server == "Emby" && serverRoot.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) serverRoot = serverRoot[..^5];
        var baseUri = new Uri(serverRoot + "/");
        var path    = target.Server == "Emby"
            ? $"embywebsocket?api_key={Uri.EscapeDataString(target.ApiKey)}&deviceId=postarr-live-updates"
            : "socket";
        var wsUri   = new UriBuilder(new Uri(baseUri, path)) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws" }.Uri;

        using var conn = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var ws   = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", JellyfinClient.SessionAuthHeader(target.ApiKey));
        using var invoker = new HttpMessageInvoker(JellyfinClient.CreateHandler());   // IPv4-first, like the API client
        await ws.ConnectAsync(wsUri, invoker, conn.Token);

        _logger.LogInformation("{Server} live updates connected.", target.Server);
        if (_announcedFor != target.Server)
        {
            _announcedFor = target.Server;
            await LogActivityAsync($"{target.Server} instant updates connected — new movies and shows will be added automatically.");
        }

        // Drop the connection when the server type, address, key or toggle changes, so it's rebuilt (or stops).
        _ = Task.Run(async () =>
        {
            while (!conn.IsCancellationRequested)
            {
                await Delay(SettingsCheck, conn.Token);
                if (Target(_settings.Get()) != target) conn.Cancel();
            }
        });

        Task? keepAlive = null;
        var buffer = new byte[16 * 1024];
        using var msg = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !conn.IsCancellationRequested)
            {
                msg.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, conn.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    msg.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                using var doc = JsonDocument.Parse(msg.ToArray());
                var root = doc.RootElement;
                var type = root.TryGetProperty("MessageType", out var mt) ? mt.GetString() : null;

                if (type == "ForceKeepAlive" && keepAlive == null)
                {
                    // Jellyfin drops sockets that stay silent; it says how long it waits, so ping at half that.
                    var seconds = root.TryGetProperty("Data", out var d) && d.TryGetInt32(out var n) && n > 0 ? n : 60;
                    keepAlive = KeepAliveAsync(ws, TimeSpan.FromSeconds(Math.Max(5, seconds / 2)), conn.Token);
                }
                else if (type == "LibraryChanged" && root.TryGetProperty("Data", out var data)
                         && data.TryGetProperty("ItemsAdded", out var added) && added.ValueKind == JsonValueKind.Array)
                {
                    lock (_pending)
                    {
                        foreach (var id in added.EnumerateArray())
                            if (id.GetString() is { Length: > 0 } s2) _pending.Add(s2);
                        if (added.GetArrayLength() > 0) _lastAddedUtc = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // Settings changed — fall through and let ExecuteAsync reconnect with the new values (or idle).
        }
        finally
        {
            conn.Cancel();
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
        }
    }

    private static async Task KeepAliveAsync(ClientWebSocket ws, TimeSpan every, CancellationToken ct)
    {
        var ping = Encoding.UTF8.GetBytes("{\"MessageType\":\"KeepAlive\"}");
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Delay(every, ct);
            try { await ws.SendAsync(ping, WebSocketMessageType.Text, true, ct); } catch { return; }
        }
    }

    // Once additions have settled, fetch just those items and run them through the normal scan path.
    private async Task ProcessLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Delay(TimeSpan.FromSeconds(5), stop);
            List<string> batch;
            lock (_pending)
            {
                if (_pending.Count == 0 || DateTime.UtcNow - _lastAddedUtc < SettleDelay) continue;
                batch = _pending.ToList();
                _pending.Clear();
            }
            _logger.LogInformation("Media server reported {Count} newly added item(s); refreshing them.", batch.Count);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<MetadataClientFactory>();
                if (factory.BuildMediaServerClient() is not JellyfinClient jellyfin) continue;   // server changed meanwhile
                var scan = scope.ServiceProvider.GetRequiredService<LibraryScanService>();
                foreach (var (section, item) in await jellyfin.ResolveAddedAsync(batch, stop))
                    await scan.ScanItemAsync(section, item, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning("Jellyfin live update processing failed: {Message}", ex.Message); }
        }
    }

    private async Task LogActivityAsync(string message)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ActivityLogger>().LogAsync(message);
        }
        catch { /* activity log is best-effort */ }
    }

    private static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); } catch (OperationCanceledException) { }
    }
}
