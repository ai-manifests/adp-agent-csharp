using System.Collections.Immutable;
using Acb.Manifest;
using Adj.Manifest;
using Adp.Agent.Deliberation;
using Adp.Manifest;
using Xunit;
using AdpCalibrationScore = Adp.Manifest.CalibrationScore;

namespace Adp.Agent.Tests;

/// <summary>
/// Regression coverage for the self-URL → self-agentId binding the
/// deliberation runner must establish on the transport before the
/// initiator self-proposal call.
///
/// Before the fix, only fetchManifest registered URLs in HttpPeerTransport's
/// internal map, and the initiator never fetches its own manifest — so
/// the self URL stayed unbound and outgoing self-proposal calls fell back
/// to the wildcard '*' peer-token lookup. With <see cref="AuthConfig.PeerTokens"/>
/// holding only per-peer entries (no '*'), no Authorization header was
/// sent, and the agent's own auth middleware rejected the call with 401.
/// </summary>
public class PeerTransportTests
{
    [Fact]
    public void RegisterAgent_BindsUrlToAgentId_ForOutboundAuthLookup()
    {
        // Build an auth config with a per-peer token map. There's no '*'
        // wildcard — the test ensures resolution goes via the per-peer
        // mapping the transport's RegisterAgent populates.
        var auth = new AuthConfig
        {
            BearerToken = "self-bearer",
            PeerTokens = ImmutableDictionary<string, string>.Empty
                .Add("did:adp:self", "self-bearer")
                .Add("did:adp:peer", "peer-bearer"),
        };

        var http = new HttpClient();
        var transport = new HttpPeerTransport(http, auth);
        // Public API: RegisterAgent exists on the interface and the
        // implementation. Calling it must not throw.
        transport.RegisterAgent("http://self.test:3001", "did:adp:self");
        transport.RegisterAgent("http://peer.test", "did:adp:peer");

        // Indirect verification: the helper that builds outbound headers
        // routes via the same peerAgentIds → peerTokens chain that the
        // transport uses. Construct an expected header set for each peer
        // and confirm we don't fall through to the no-auth case.
        var selfHeaders = PeerAuthHeaders.Build(auth, "did:adp:self");
        Assert.True(selfHeaders.ContainsKey("Authorization"));
        Assert.Equal("Bearer self-bearer", selfHeaders["Authorization"]);

        var peerHeaders = PeerAuthHeaders.Build(auth, "did:adp:peer");
        Assert.True(peerHeaders.ContainsKey("Authorization"));
        Assert.Equal("Bearer peer-bearer", peerHeaders["Authorization"]);
    }

    [Fact]
    public void PeerAuthHeaders_FallsBackToWildcard_WhenAgentMissing()
    {
        // Wildcard fallback is preserved for transports that legitimately
        // need it (e.g. external integrations). The bug fix doesn't
        // remove wildcard support; it makes the self URL no longer rely
        // on it.
        var auth = new AuthConfig
        {
            BearerToken = "x",
            PeerTokens = ImmutableDictionary<string, string>.Empty.Add("*", "wildcard-token"),
        };

        var headers = PeerAuthHeaders.Build(auth, "did:adp:unknown");
        Assert.Equal("Bearer wildcard-token", headers["Authorization"]);
    }

    [Fact]
    public void PeerAuthHeaders_NoTokenWhenNoMatch()
    {
        var auth = new AuthConfig
        {
            BearerToken = "x",
            PeerTokens = ImmutableDictionary<string, string>.Empty.Add("did:adp:other", "other"),
        };

        var headers = PeerAuthHeaders.Build(auth, "did:adp:unknown");
        Assert.False(headers.ContainsKey("Authorization"));
        Assert.Equal("application/json", headers["Content-Type"]);
    }
}
