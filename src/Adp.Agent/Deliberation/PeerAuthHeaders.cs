using System.Collections.Immutable;

namespace Adp.Agent.Deliberation;

/// <summary>
/// Resolves outgoing peer-to-peer Authorization headers from an
/// <see cref="AuthConfig"/>. The lookup table maps a peer agent id to that
/// peer's expected bearer token; transports use it to send the correct
/// token when calling each peer's protected endpoints.
///
/// This is the C# equivalent of the TypeScript runtime's
/// <c>middleware/auth.authHeaders</c> helper, kept separate from the
/// inbound <see cref="Middleware.AuthMiddleware"/> because the two are
/// different concerns: the middleware enforces inbound auth using the
/// agent's own bearer; this helper builds outbound headers using a
/// per-peer token map.
/// </summary>
public static class PeerAuthHeaders
{
    /// <summary>
    /// Returns the bearer token to use when calling
    /// <paramref name="peerAgentId"/>'s protected endpoints. Falls back to a
    /// wildcard <c>"*"</c> entry if no peer-specific token is configured;
    /// returns <c>null</c> when no auth is configured at all.
    /// </summary>
    public static string? GetPeerToken(AuthConfig? auth, string peerAgentId)
    {
        if (auth is null || auth.PeerTokens.IsEmpty) return null;
        if (auth.PeerTokens.TryGetValue(peerAgentId, out var direct))
            return direct;
        if (auth.PeerTokens.TryGetValue("*", out var wildcard))
            return wildcard;
        return null;
    }

    /// <summary>
    /// Builds the outgoing header dictionary for a peer call. Always
    /// includes <c>Content-Type: application/json</c>; conditionally adds
    /// <c>Authorization: Bearer &lt;token&gt;</c> when a peer token is
    /// resolved.
    /// </summary>
    public static ImmutableDictionary<string, string> Build(AuthConfig? auth, string peerAgentId)
    {
        var token = GetPeerToken(auth, peerAgentId);
        var builder = ImmutableDictionary.CreateBuilder<string, string>();
        builder["Content-Type"] = "application/json";
        if (!string.IsNullOrEmpty(token))
        {
            builder["Authorization"] = $"Bearer {token}";
        }
        return builder.ToImmutable();
    }
}
