using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace Adp.Agent.Middleware;

/// <summary>
/// Simple fixed-window rate limiter keyed by client IP. Enforces a
/// per-window request cap on inbound endpoints to blunt basic flooding
/// attacks. Intentionally coarse — real load shedding belongs in the
/// reverse proxy. This exists to match the TypeScript runtime's default
/// middleware and to give adopters a single-line opt-in via
/// <see cref="MiddlewareExtensions.UseAdpRateLimit"/>.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int _maxRequestsPerWindow;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    public RateLimitMiddleware(RequestDelegate next, int maxRequestsPerWindow = 120, TimeSpan? window = null)
    {
        _next = next;
        _maxRequestsPerWindow = maxRequestsPerWindow;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var key = GetClientKey(ctx);
        var now = DateTimeOffset.UtcNow;

        var win = _windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) =>
            {
                if (now - existing.Start >= _window)
                    return new Window(now, 1);
                return existing with { Count = existing.Count + 1 };
            });

        if (win.Count > _maxRequestsPerWindow)
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = ((int)(_window - (now - win.Start)).TotalSeconds).ToString();
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "rate_limit_exceeded",
                retryAfterSeconds = (_window - (now - win.Start)).TotalSeconds,
            });
            return;
        }

        await _next(ctx);
    }

    private static string GetClientKey(HttpContext ctx)
    {
        // Prefer X-Forwarded-For if present (typical behind a reverse proxy).
        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd))
        {
            var first = fwd.ToString().Split(',', 2, StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(first)) return first;
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
