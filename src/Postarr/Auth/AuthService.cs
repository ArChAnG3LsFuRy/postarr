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
        return ValidateToken(token);
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
        var key = System.Text.Encoding.UTF8.GetBytes(_settings.Get().Auth.PasswordHash + "postarr_secret");
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    // ── Password-guessing protection: 5 wrong passwords from one address in 15 minutes locks it out for 5 ──
    private static readonly ConcurrentDictionary<string, (int Fails, DateTime First, DateTime LockedUntil)> _attempts = new();
    private const int MaxFails = 5;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15), LockFor = TimeSpan.FromMinutes(5);

    public static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

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
