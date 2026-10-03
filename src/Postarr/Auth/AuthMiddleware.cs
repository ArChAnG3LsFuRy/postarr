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

    private static bool IsLoginAsset(string path) =>
        path.StartsWith("/api/auth/") || path == "/login" || path == "/login.html" ||
        path == "/css/app.css" || path.StartsWith("/images/logo/") || path.StartsWith("/fonts/") ||
        path.StartsWith("/favicon") || path == "/placeholder-poster.svg";

    public async Task InvokeAsync(HttpContext ctx, AuthService auth)
    {
        var path = ctx.Request.Path.Value ?? "";

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
