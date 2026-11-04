namespace Postarr.Auth;

/// <summary>
/// Intercepts every request.
///  • No login created yet (fresh install, or an upgrade that never set one): everything except the login page and
///    its assets is refused — browsers go to /login, which shows "Create your login"; API calls get 401 setupRequired.
///  • Login enabled and no valid session: browsers go to /login, API/SignalR calls get 401.
///  • Plex webhooks are let through when they carry the install's webhook token (Plex can't sign in).
/// </summary>
public class AuthMiddleware
{
    private readonly RequestDelegate _next;

    public AuthMiddleware(RequestDelegate next) => _next = next;

    private static bool IsStateChanging(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

    private static bool IsCrossOrigin(HttpRequest req)
    {
        var site = req.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0) return site is not ("same-origin" or "none");
        var origin = req.Headers.Origin.ToString();
        if (origin.Length == 0 || origin == "null") return origin == "null";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return true;
        var originHost = o.IsDefaultPort ? o.Host : $"{o.Host}:{o.Port}";
        var hosts = new[] { req.Host.Value, req.Headers["X-Forwarded-Host"].ToString() };
        return !hosts.Any(x => !string.IsNullOrEmpty(x) && string.Equals(x, originHost, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoginAsset(string path) =>
        path.StartsWith("/api/auth/") || path == "/login" || path == "/login.html" ||
        path == "/css/app.css" || path.StartsWith("/images/logo/") || path.StartsWith("/fonts/") ||
        path.StartsWith("/favicon") || path == "/placeholder-poster.svg";

    public async Task InvokeAsync(HttpContext ctx, AuthService auth)
    {
        var path = ctx.Request.Path.Value ?? "";

        // Browser hardening on every response: never inside another site's frame (clickjacking), never guess a file's
        // type from its content, and don't hand other sites this page's address.
        var h = ctx.Response.Headers;
        h["X-Frame-Options"]         = "DENY";
        h["Content-Security-Policy"] = "frame-ancestors 'none'";
        h["X-Content-Type-Options"]  = "nosniff";
        h["Referrer-Policy"]         = "same-origin";

        // Cross-site request forgery: a page on another site — including another port or subdomain of the same host,
        // which the SameSite cookie rule still counts as "same site" — must not be able to make the signed-in browser
        // change anything. Browsers say where a request came from (Sec-Fetch-Site, or Origin on older ones); only
        // Postarr's own pages may send changes. Tools with no browser headers (scripts, the Plex webhook) aren't affected.
        if (IsStateChanging(ctx.Request.Method) && path.StartsWith("/api/") && !path.StartsWith("/api/webhooks/") && IsCrossOrigin(ctx.Request))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "Blocked: this request came from another site." });
            return;
        }

        if (IsLoginAsset(path) || auth.IsValidWebhook(ctx))
        {
            await _next(ctx);
            return;
        }

        var setupRequired = auth.SetupRequired();
        if (setupRequired || !auth.ValidateSession(ctx))
        {
            if (path.StartsWith("/api/") || path.StartsWith("/scanhub"))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = setupRequired ? "Create a login first." : "Unauthorized", setupRequired });
            }
            else
            {
                ctx.Response.Redirect("/login");
            }
            return;
        }

        await _next(ctx);
    }
}
