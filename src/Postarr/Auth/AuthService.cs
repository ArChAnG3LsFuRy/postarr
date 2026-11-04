using System.Collections.Concurrent;
using System.Security.Cryptography;
using Postarr.Data;
using BC = BCrypt.Net.BCrypt;

namespace Postarr.Auth;

public class AuthService
{
    private readonly SettingsRepository _settings;

    public AuthService(SettingsRepository settings) => _settings = settings;

    public bool IsAuthEnabled() => _settings.Get().Auth.AuthEnabled;

    /// <summary>No login has been created yet — Postarr refuses everything until one is (first-run setup).</summary>
    public bool SetupRequired() => string.IsNullOrEmpty(_settings.Get().Auth.PasswordHash);

    public bool ValidatePassword(string password)
    {
        var auth = _settings.Get().Auth;
        if (string.IsNullOrEmpty(auth.PasswordHash)) return false;
        try { return BC.Verify(password, auth.PasswordHash); }
        catch { return false; }
    }

    public string HashPassword(string password) => BC.HashPassword(password, workFactor: 11);

    public bool ValidateSession(HttpContext ctx)
    {
        if (SetupRequired()) return false;
        if (!IsAuthEnabled()) return true;
        var token = ctx.Request.Cookies["postarr_session"];
        if (string.IsNullOrEmpty(token)) return false;
        // Simple HMAC-signed session token: base64(username:expiry).signature
        return ValidateToken(token) && !IsRevoked(token);
    }

    // ── Signing key ──────────────────────────────────────────────────────────────────────────────────────────
    // Sessions are signed with a random secret kept in its own file ({data}/session.key), plus the password hash so a
    // password change still signs everyone out. It used to be the hash alone (+ a fixed word that's public in the
    // source), so anyone holding a copy of the database — a downloaded backup, say — could mint a valid login.
    public static string? DataDir { get; set; }
    private static byte[]? _secret;
    private static readonly object _secretLock = new();

    private static byte[] Secret()
    {
        if (_secret != null) return _secret;
        lock (_secretLock)
        {
            if (_secret != null) return _secret;
            var dir  = DataDir ?? AppContext.BaseDirectory;
            var path = Path.Combine(dir, "session.key");
            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllBytes(path);
                    if (existing.Length >= 32) return _secret = existing;
                }
                var fresh = RandomNumberGenerator.GetBytes(32);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, fresh);
                return _secret = fresh;
            }
            catch
            {
                // Read-only location: sessions last until Postarr restarts, which is still safe.
                return _secret = RandomNumberGenerator.GetBytes(32);
            }
        }
    }

    // ── Sign out ────────────────────────────────────────────────────────────────────────────────────────────
    // A session cookie is self-contained, so "Sign out" used to leave a copied cookie working until it expired. The
    // signed-out token's signature is now remembered (in {data}/revoked-sessions.txt) until its own expiry.
    private static readonly object _revokeLock = new();
    private static Dictionary<string, long>? _revoked;

    private static string RevokedPath => Path.Combine(DataDir ?? AppContext.BaseDirectory, "revoked-sessions.txt");

    private static Dictionary<string, long> Revoked()
    {
        if (_revoked != null) return _revoked;
        var d = new Dictionary<string, long>();
        try
        {
            if (File.Exists(RevokedPath))
                foreach (var line in File.ReadAllLines(RevokedPath))
                {
                    var p = line.Split(' ');
                    if (p.Length == 2 && long.TryParse(p[1], out var exp)) d[p[0]] = exp;
                }
        }
        catch { /* start empty */ }
        return _revoked = d;
    }

    private static bool IsRevoked(string token)
    {
        var sig = token.Split('.').LastOrDefault() ?? "";
        lock (_revokeLock) return Revoked().ContainsKey(sig);
    }

    public void Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token) || !ValidateToken(token)) return;
        var parts = token.Split('.');
        long exp;
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            exp = long.Parse(decoded[(decoded.LastIndexOf(':') + 1)..]);
        }
        catch { return; }
        lock (_revokeLock)
        {
            var d = Revoked();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var k in d.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) d.Remove(k);
            d[parts[1]] = exp;
            try { File.WriteAllLines(RevokedPath, d.Select(kv => $"{kv.Key} {kv.Value}")); } catch { /* kept in memory */ }
        }
    }

    /// <summary>A Plex webhook call carrying this install's webhook token (Plex can't sign in, so it gets a token).</summary>
    public bool IsValidWebhook(HttpContext ctx)
    {
        if (!(ctx.Request.Path.Value ?? "").StartsWith("/api/webhooks/")) return false;
        var expected = _settings.Get().WebhookToken;
        var given    = ctx.Request.Query["token"].ToString();
        return !string.IsNullOrEmpty(expected) && FixedEquals(given, expected);
    }

    public string CreateSessionToken(string username)
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(_settings.Get().Auth.SessionDaysValid).ToUnixTimeSeconds();
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{expiry}"));
        return $"{payload}.{Sign(payload)}";
    }

    private bool ValidateToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        if (!FixedEquals(Sign(parts[0]), parts[1])) return false;
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            var colonIdx = decoded.LastIndexOf(':');
            var expiry = long.Parse(decoded[(colonIdx + 1)..]);
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry;
        }
        catch { return false; }
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    private string Sign(string payload)
    {
        // Renaming this salt invalidates session cookies issued before the rename, so anyone
        // logged in at upgrade time is asked to sign in once more. Passwords are unaffected.
        var key = Secret().Concat(System.Text.Encoding.UTF8.GetBytes(_settings.Get().Auth.PasswordHash ?? "")).ToArray();
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    // ── Password-guessing protection: 5 wrong passwords from one address in 15 minutes locks it out for 5 ──
    private static readonly ConcurrentDictionary<string, (int Fails, DateTime First, DateTime LockedUntil)> _attempts = new();
    private const int MaxFails = 5;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15), LockFor = TimeSpan.FromMinutes(5);

    // Behind a reverse proxy or tunnel on the same machine (Cloudflare's cloudflared, nginx, Caddy…) every request
    // arrives from 127.0.0.1, so one person's wrong guesses used to lock everyone out. Trust the proxy's "real client"
    // header only when the connection itself comes from this machine — anyone else could simply invent one.
    public static string ClientKey(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip != null && System.Net.IPAddress.IsLoopback(ip))
        {
            var fwd = ctx.Request.Headers["CF-Connecting-IP"].ToString();
            // X-Forwarded-For: the LAST entry is the one our proxy added; earlier ones come from the client and can be
            // anything (sending a new made-up address each time would dodge the lockout).
            if (string.IsNullOrWhiteSpace(fwd)) fwd = ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',')[^1];
            if (!string.IsNullOrWhiteSpace(fwd)) return "fwd:" + fwd.Trim();
        }
        return ip?.ToString() ?? "unknown";
    }

    /// <summary>Seconds until this address may try again, or 0.</summary>
    public int LockedSeconds(string client)
    {
        if (_attempts.TryGetValue(client, out var a) && a.LockedUntil > DateTime.UtcNow)
            return (int)Math.Ceiling((a.LockedUntil - DateTime.UtcNow).TotalSeconds);
        return 0;
    }

    public void RecordFailure(string client)
    {
        var now = DateTime.UtcNow;
        _attempts.AddOrUpdate(client, _ => (1, now, DateTime.MinValue), (_, a) =>
        {
            if (now - a.First > FailWindow) return (1, now, DateTime.MinValue);
            var fails = a.Fails + 1;
            return (fails, a.First, fails >= MaxFails ? now + LockFor : a.LockedUntil);
        });
    }

    public void RecordSuccess(string client) => _attempts.TryRemove(client, out _);
}
