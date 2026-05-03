using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Adp.Agent;

/// <summary>
/// The shape returned by <c>GET /.well-known/adp-manifest.json</c>. This is
/// the public discovery document for the agent — peers and registries fetch
/// it once to learn the agent's identity, its declared decision classes,
/// its authority weights, and its public signing key.
/// </summary>
public sealed record AgentManifest(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("complianceLevel")] int ComplianceLevel,
    [property: JsonPropertyName("decisionClasses")] ImmutableList<string> DecisionClasses,
    [property: JsonPropertyName("domainAuthorities")] ImmutableDictionary<string, DomainAuthority> DomainAuthorities,
    [property: JsonPropertyName("journalEndpoint")] string JournalEndpoint,
    [property: JsonPropertyName("publicKey")] string? PublicKey,
    [property: JsonPropertyName("trustLevel")] string TrustLevel
)
{
    /// <summary>
    /// Build a manifest from an <see cref="AgentConfig"/>. The runtime calls this at startup
    /// (and whenever the config is reloaded) to materialize the document served at
    /// <c>/.well-known/adp-manifest.json</c>.
    /// </summary>
    public static AgentManifest FromConfig(AgentConfig config) => new(
        AgentId: config.AgentId,
        Identity: $"did:web:{config.Domain}",
        ComplianceLevel: 3,
        DecisionClasses: config.DecisionClasses,
        DomainAuthorities: config.Authorities.ToImmutableDictionary(
            kv => kv.Key,
            kv => new DomainAuthority(
                Authority: kv.Value,
                Source: $"mcp-manifest:{config.AgentId}#authorities")),
        // Default: internal `Domain:Port` URL, which works for peer-to-peer
        // calls in the same network (loopback / hairpin). Override with
        // <c>AgentConfig.PublicJournalEndpoint</c> when the agent sits behind
        // a TLS-terminating proxy and external callers (e.g. the registry
        // audit) need the proxy URL.
        JournalEndpoint: config.PublicJournalEndpoint ?? $"http://{config.Domain}:{config.Port}/adj/v0",
        PublicKey: config.Auth?.PublicKey,
        TrustLevel: "open"
    );
}

public sealed record DomainAuthority(
    [property: JsonPropertyName("authority")] double Authority,
    [property: JsonPropertyName("source")] string Source
);
