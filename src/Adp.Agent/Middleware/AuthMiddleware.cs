using Microsoft.AspNetCore.Http;

namespace Adp.Agent.Middleware;

/// <summary>
/// Bearer token authentication middleware for ADP inbound admin endpoints.
/// Matches the TypeScript runtime's middleware semantics: unprotected
/// endpoints (manifest, calibration snapshot, health, ADJ read-only queries)
/// pass through unchanged; protected endpoints (<c>POST /api/*</c>) require
/// an <c>Authorization: Bearer &lt;token&gt;</c> header that matches
/// <see cref="AuthConfig.BearerToken"/>.
/// </summary>
public sealed class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthConfig? _auth;

    // Endpoints that require bearer-token auth.
    private static readonly string[] ProtectedPrefixes =
    {
        "/api/propose",
        "/api/respond-falsification",
        "/api/deliberate",
        "/api/record-outcome",
        "/api/budget",
        "/api/anchor/",
    };

    public AuthMiddleware(RequestDelegate next, AgentConfig config)
    {
        _next = next;
        _auth = config.Auth;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!RequiresAuth(ctx.Request.Path))
        {
            await _next(ctx);
            return;
        }

        if (_auth is null || string.IsNullOrEmpty(_auth.BearerToken))
        {
            // Auth not configured — protected endpoints are effectively disabled.
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "auth_not_configured",
                message = "This endpoint requires authentication, but the agent has no bearer token configured.",
            });
            return;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "missing_bearer_token",
                message = "Missing 'Authorization: Bearer <token>' header.",
            });
            return;
        }

        var provided = header.Substring("Bearer ".Length).Trim();
        if (!ConstantTimeEquals(provided, _auth.BearerToken))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "invalid_bearer_token",
            });
            return;
        }

        await _next(ctx);
    }

    private static bool RequiresAuth(PathString path)
    {
        if (!path.HasValue) return false;
        var p = path.Value!;
        foreach (var prefix in ProtectedPrefixes)
        {
            if (p.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Constant-time string equality. Prevents timing attacks on the bearer
    /// token comparison.
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var result = 0;
        for (var i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }
        return result == 0;
    }
}
