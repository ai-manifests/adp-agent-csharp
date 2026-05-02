using System.Collections.Immutable;
using Acb.Manifest;
using Adj.Manifest;
using Adp.Manifest;
using CalibrationScore = Adp.Manifest.CalibrationScore;

namespace Adp.Agent.Deliberation;

/// <summary>
/// Peer-to-peer transport used by <see cref="PeerDeliberation"/> to talk to
/// other agents. Implementations own the URL → agentId mapping that
/// outbound calls need to look up the right peer-token in
/// <see cref="AuthConfig.PeerTokens"/>.
///
/// <para>
/// The map is populated automatically as a side-effect of
/// <see cref="FetchManifestAsync"/>, and explicitly via
/// <see cref="RegisterAgent"/> for paths that don't go through manifest
/// fetch — most importantly the initiator's own self URL, since a
/// deliberation's initiator never fetches its own manifest. Without
/// <see cref="RegisterAgent"/>, the self URL stays unbound and
/// outgoing calls fall back to a wildcard token lookup, which produces
/// no <c>Authorization</c> header and a 401 from the agent's own
/// auth middleware.
/// </para>
/// <para>
/// This is the C# port of the TypeScript runtime's <c>PeerTransport</c>
/// interface. Method shapes are kept aligned with the TS contract so
/// adopters porting code between the runtimes don't need to relearn the
/// surface.
/// </para>
/// </summary>
public interface IPeerTransport
{
    /// <summary>
    /// Bind a URL to an agent id in the transport's internal map so
    /// subsequent outgoing calls to <paramref name="peerUrl"/> use the
    /// correct peer-token from <see cref="AuthConfig.PeerTokens"/>. Call
    /// this for any peer whose <see cref="FetchManifestAsync"/> is not
    /// invoked through the transport — most importantly the initiator's
    /// own self URL.
    /// </summary>
    void RegisterAgent(string peerUrl, string agentId);

    /// <summary>Fetch a peer's <c>/.well-known/adp-manifest.json</c>.</summary>
    Task<AgentManifest> FetchManifestAsync(string peerUrl, CancellationToken ct = default);

    /// <summary>
    /// Fetch a peer's calibration score for the given <paramref name="agentId"/>
    /// in <paramref name="domain"/> from its journal endpoint. Returns the
    /// neutral default <c>{0.5, 0, 0}</c> on failure.
    /// </summary>
    Task<CalibrationScore> FetchCalibrationAsync(
        string journalEndpoint, string agentId, string domain, CancellationToken ct = default);

    /// <summary>
    /// Ask <paramref name="peerUrl"/> for a proposal on <paramref name="action"/>
    /// at <paramref name="tier"/>. Returns the peer's signed proposal if
    /// signing is configured, otherwise the unsigned proposal.
    /// </summary>
    Task<PeerProposalResponse> RequestProposalAsync(
        string peerUrl, string deliberationId,
        ActionDescriptor action, ReversibilityTier tier, CancellationToken ct = default);

    /// <summary>
    /// Send a falsification request to the peer that owns the targeted
    /// dissent condition. The peer responds with one of
    /// <c>acknowledge</c>, <c>reject</c>, or <c>amend</c>.
    /// </summary>
    Task<FalsificationResponse> SendFalsificationAsync(
        string peerUrl, string conditionId, int round, string evidenceAgentId,
        CancellationToken ct = default);

    /// <summary>
    /// Push a batch of journal entries to a peer's <c>POST /adj/v0/entries</c>
    /// endpoint. Used to gossip the deliberation's full transcript at close.
    /// </summary>
    Task PushJournalEntriesAsync(
        string peerUrl, IReadOnlyList<JournalEntry> entries, CancellationToken ct = default);
}

/// <summary>
/// Envelope returned by <see cref="IPeerTransport.RequestProposalAsync"/>.
/// Wraps the rich <see cref="Proposal"/> with the optional Ed25519
/// signature so the caller can verify signature without re-fetching.
/// </summary>
public sealed record PeerProposalResponse(
    Proposal Proposal,
    string? Signature
);

/// <summary>
/// Response shape from a falsification request. <c>Action</c> is one of
/// <c>acknowledge</c>, <c>reject</c>, or <c>amend</c>. When <c>amend</c>,
/// <c>NewCondition</c> carries the narrowed condition string.
/// </summary>
public sealed record FalsificationResponse(
    string Action,
    string? Reason = null,
    string? NewCondition = null
);
