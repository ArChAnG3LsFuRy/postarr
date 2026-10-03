using Postarr.Auth;
using Postarr.Data;
using Microsoft.AspNetCore.Mvc;

namespace Postarr.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly SettingsRepository _settings;

    public AuthController(AuthService auth, SettingsRepository settings)
    {
        _auth = auth;
        _settings = settings;
    }

    public record LoginRequest(string Username, string Password);

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        if (_auth.SetupRequired()) return BadRequest(new { error = "Create your login first.", setupRequired = true });
        var settings = _settings.Get();
        if (!settings.Auth.AuthEnabled) return Ok(new { success = true, authEnabled = false });

        var client = AuthService.ClientKey(HttpContext);
        var wait = _auth.LockedSeconds(client);
        if (wait > 0) return StatusCode(429, new { error = $"Too many wrong passwords. Try again in {(wait + 59) / 60} minute(s)." });

        if (req.Username != settings.Auth.Username || !_auth.ValidatePassword(req.Password))
        {
            _auth.RecordFailure(client);
            return Unauthorized(new { error = "Invalid username or password." });
        }
        _auth.RecordSuccess(client);
        IssueSession(req.Username, settings.Auth.SessionDaysValid);
        return Ok(new { success = true });
    }

    /// <summary>First run: create the login. Only works while no login exists.</summary>
    [HttpPost("setup")]
    public IActionResult Setup([FromBody] LoginRequest req)
    {
        if (!_auth.SetupRequired()) return BadRequest(new { error = "A login already exists — sign in instead." });
        var username = (req.Username ?? "").Trim();
        if (username.Length == 0 || username.Length > 64) return BadRequest(new { error = "Choose a username." });
        if ((req.Password ?? "").Length < 8) return BadRequest(new { error = "Use a password of at least 8 characters." });

        var s = _settings.Get();
        s.Auth.Username     = username;
        s.Auth.PasswordHash = _auth.HashPassword(req.Password!);
        s.Auth.AuthEnabled  = true;
        _settings.Save(s);
        IssueSession(username, s.Auth.SessionDaysValid);
        return Ok(new { success = true });
    }

    private void IssueSession(string username, int days)
    {
        // Secure when the browser reached us over HTTPS — directly, or through a reverse proxy / Cloudflare tunnel.
        var https = Request.IsHttps || string.Equals(Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
        Response.Cookies.Append("postarr_session", _auth.CreateSessionToken(username), new CookieOptions
        {
            HttpOnly = true,
            Secure   = https,
            SameSite = SameSiteMode.Lax,
            Expires  = DateTimeOffset.UtcNow.AddDays(days),
            Path     = "/"
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("postarr_session");
        return Ok();
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        authEnabled   = _auth.IsAuthEnabled(),
        authenticated = _auth.ValidateSession(HttpContext),
        setupRequired = _auth.SetupRequired(),
    });
}
