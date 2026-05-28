using Microsoft.AspNetCore.Http;
using NINA.Polaris.Services.Auth;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// AUTH-1: HTTP surface for the local-auth feature.
///
/// All routes under /api/auth/* are exempted from AuthMiddleware so
/// the frontend can hit /status before deciding whether to show the
/// first-run wizard, the login overlay, or the app itself.
///
/// Endpoints also set / clear a same-origin HttpOnly cookie so the
/// embedded iframes (phd2-gui, indi-web, sky) carry auth automatically
/// without needing JS to intercept their requests.
/// </summary>
public static class AuthEndpoints {
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app) {
        var g = app.MapGroup("/api/auth");

        // GET /api/auth/status
        // Public. Tells the frontend which boot path to take:
        //   configured=false       -> show wizard
        //   enabled=false          -> let app load with no login
        //   authenticated=false    -> show login overlay
        //   authenticated=true     -> app ready
        g.MapGet("/status", (HttpContext ctx, AuthService auth) => {
            var token = ExtractToken(ctx);
            return Results.Ok(auth.GetStatus(token));
        });

        // POST /api/auth/setup { password }
        // First-run only; rejects when a password is already set.
        g.MapPost("/setup", (HttpContext ctx, AuthService auth, SetupRequest req) => {
            if (auth.IsConfigured)
                return Results.Conflict(new { error = "already configured" });
            try {
                var token = auth.SetInitialPassword(req.Password ?? "");
                if (token == null)
                    return Results.BadRequest(new { error = "setup failed" });
                SetSessionCookie(ctx, token);
                return Results.Ok(new { token });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/auth/login { password }
        // Returns the session token + sets the cookie. Rate-limited
        // per IP; lockout response includes a Retry-After header.
        g.MapPost("/login", (HttpContext ctx, AuthService auth, LoginRequest req) => {
            if (!auth.IsConfigured)
                return Results.BadRequest(new { error = "not configured" });
            var token = auth.Login(req.Password ?? "",
                ctx.Connection.RemoteIpAddress);
            if (token == null)
                return Results.Json(new { error = "invalid password" },
                    statusCode: 401);
            SetSessionCookie(ctx, token);
            return Results.Ok(new { token });
        });

        // POST /api/auth/logout
        // Invalidates the presented session and clears the cookie.
        g.MapPost("/logout", (HttpContext ctx, AuthService auth) => {
            var token = ExtractToken(ctx);
            if (!string.IsNullOrEmpty(token)) auth.Logout(token);
            ClearSessionCookie(ctx);
            return Results.Ok(new { ok = true });
        });

        // POST /api/auth/change-password { current, new }
        // Authenticated. Invalidates every other session.
        g.MapPost("/change-password",
                (HttpContext ctx, AuthService auth, ChangePasswordRequest req) => {
            var current = ExtractToken(ctx);
            if (string.IsNullOrEmpty(current) || !auth.ValidateToken(current))
                return Results.Json(new { error = "not authenticated" },
                    statusCode: 401);
            try {
                var newToken = auth.ChangePassword(
                    req.Current ?? "", req.New ?? "", current);
                if (newToken == null)
                    return Results.Json(new { error = "current password invalid" },
                        statusCode: 401);
                SetSessionCookie(ctx, newToken);
                return Results.Ok(new { ok = true });
            } catch (ArgumentException ex) {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/auth/disable { password }
        // Persists AuthEnabled=false on the active profile. Requires
        // current password so a stolen session token alone cannot
        // disable auth.
        g.MapPost("/disable", (AuthService auth, DisableEnableRequest req) => {
            if (!auth.SetEnabled(req.Password ?? "", false))
                return Results.Json(new { error = "invalid password" },
                    statusCode: 401);
            return Results.Ok(new { ok = true, enabled = false });
        });

        // POST /api/auth/enable { password }
        // Same as /disable but flips it back on. Useful when the
        // operator toggles auth off temporarily on a closed LAN and
        // wants to reactivate before going somewhere public.
        g.MapPost("/enable", (AuthService auth, DisableEnableRequest req) => {
            if (!auth.SetEnabled(req.Password ?? "", true))
                return Results.Json(new { error = "invalid password" },
                    statusCode: 401);
            return Results.Ok(new { ok = true, enabled = true });
        });
    }

    // ----- helpers ---------------------------------------------------

    /// <summary>Token lookup order matches AuthMiddleware:
    /// Authorization header > ?token= query > polaris_session cookie.
    /// Kept here too so endpoints can read the caller's session
    /// without depending on the middleware having stashed it.</summary>
    internal static string? ExtractToken(HttpContext ctx) {
        var hdr = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(hdr) &&
                hdr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
            return hdr["Bearer ".Length..].Trim();
        }
        var q = ctx.Request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(q)) return q;
        if (ctx.Request.Cookies.TryGetValue(AuthService.CookieName, out var c))
            return c;
        return null;
    }

    private static void SetSessionCookie(HttpContext ctx, string token) {
        // SameSite=Strict so cross-site requests never carry the
        // cookie; Polaris is single-origin only. HttpOnly so JS can't
        // read it (the bearer token in localStorage handles the JS
        // path; the cookie is only for iframe + img/a navigation).
        // Secure flag is auto-set when the request came in via HTTPS.
        ctx.Response.Cookies.Append(AuthService.CookieName, token,
            new CookieOptions {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                // No Max-Age/Expires: cookie dies when the browser
                // closes, mirroring sessionStorage behaviour on the
                // JS side.
            });
    }

    private static void ClearSessionCookie(HttpContext ctx) {
        ctx.Response.Cookies.Delete(AuthService.CookieName,
            new CookieOptions { Path = "/" });
    }

    public record SetupRequest(string? Password);
    public record LoginRequest(string? Password);
    public record ChangePasswordRequest(string? Current, string? New);
    public record DisableEnableRequest(string? Password);
}
