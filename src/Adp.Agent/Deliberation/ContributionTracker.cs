using System.Collections.Immutable;
using Acb.Manifest;
using Adp.Manifest;

namespace Adp.Agent.Deliberation;

/// <summary>
/// Per-deliberation contribution tracker. Records which agents proposed,
/// who acknowledged whose falsifications, and any dissent-quality flags;
/// at close, builds the per-agent <see cref="ParticipantContribution"/>
/// list the <see cref="SettlementCalculator"/> consumes for the
/// <c>default-v0</c> distribution.
///
/// <para>
/// Mirrors the TypeScript runtime's <c>ContributionTracker</c> in
/// <c>acb.ts</c>. The methods are called by <see cref="PeerDeliberation"/>
/// at well-defined points in the state machine; everything in here is
/// runtime-mutable state, intentionally separate from the immutable
/// <c>Acb.Manifest</c> records.
/// </para>
/// </summary>
public sealed class ContributionTracker
{
    private readonly HashSet<string> _participants = new();
    private readonly Dictionary<string, int> _acknowledged = new();
    private readonly HashSet<string> _flagged = new();

    /// <summary>Mark <paramref name="agentId"/> as having submitted a proposal.</summary>
    public void RecordProposal(string agentId)
    {
        _participants.Add(agentId);
    }

    /// <summary>
    /// Record a falsification event from <paramref name="evidenceAgentId"/>
    /// targeting <paramref name="targetAgentId"/>'s condition. The runner
    /// calls this for every outgoing falsification regardless of the peer's
    /// response, but only acknowledged falsifications count toward the
    /// bonus (see <see cref="RecordAcknowledgement"/>).
    /// </summary>
    public void RecordFalsificationEvidence(
        string evidenceAgentId, string targetAgentId, string conditionId)
    {
        // No-op — counted only when acknowledged.
        _ = evidenceAgentId;
        _ = targetAgentId;
        _ = conditionId;
    }

    /// <summary>
    /// Record that the targeted agent acknowledged the falsification raised
    /// by <paramref name="evidenceAgentId"/>. ACB §6.2 — only acknowledged
    /// falsifications count toward the falsification bonus, to discourage
    /// spam.
    /// </summary>
    public void RecordAcknowledgement(string evidenceAgentId, string targetAgentId, string conditionId)
    {
        _ = targetAgentId;
        _ = conditionId;
        _acknowledged[evidenceAgentId] = _acknowledged.TryGetValue(evidenceAgentId, out var n) ? n + 1 : 1;
    }

    /// <summary>Flag an agent's contribution as low-quality. Triggers the dissent-quality penalty.</summary>
    public void FlagDissentQuality(string agentId)
    {
        _flagged.Add(agentId);
    }

    /// <summary>
    /// Build the final per-agent contribution list. <paramref name="loadBearingAgents"/>
    /// is the set whose votes were load-bearing (their removal would have
    /// changed the termination state); the runner computes this by replay
    /// after the final tally. <paramref name="brierDeltas"/> carries
    /// <c>(confidence − outcome)²</c> per-agent when the outcome is known
    /// at settlement time; pass an empty map for immediate-mode settlement.
    /// </summary>
    public ImmutableList<ParticipantContribution> Build(
        IReadOnlyCollection<string> loadBearingAgents,
        IReadOnlyDictionary<string, double> brierDeltas)
    {
        var builder = ImmutableList.CreateBuilder<ParticipantContribution>();
        foreach (var agentId in _participants)
        {
            builder.Add(new ParticipantContribution(
                AgentId: agentId,
                Participated: true,
                AcknowledgedFalsifications: _acknowledged.TryGetValue(agentId, out var c) ? c : 0,
                LoadBearing: loadBearingAgents.Contains(agentId),
                OutcomeBrierDelta: brierDeltas.TryGetValue(agentId, out var d) ? d : null,
                DissentQualityFlagged: _flagged.Contains(agentId)
            ));
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Counterfactual load-bearing computation: an agent's vote is load-bearing
    /// if removing their weight would have dropped approval fraction below
    /// the convergence threshold. Only computed for agents whose final vote
    /// was <c>approve</c> — the load-bearing direction in a converged
    /// deliberation. Mirrors the TS <c>computeLoadBearingAgents</c> helper.
    /// </summary>
    public static ImmutableHashSet<string> ComputeLoadBearingAgents(
        Adp.Manifest.TallyResult finalTally,
        IReadOnlyDictionary<string, double> weights,
        double threshold,
        IReadOnlyList<Proposal> proposals)
    {
        if (!finalTally.ThresholdMet) return ImmutableHashSet<string>.Empty;

        var builder = ImmutableHashSet.CreateBuilder<string>();
        foreach (var p in proposals)
        {
            var current = p.Revisions.Count > 0
                ? p.Revisions[^1].NewVote
                : p.Vote;
            if (current != Vote.Approve) continue;

            var w = weights.TryGetValue(p.AgentId, out var x) ? x : 0.0;
            if (w == 0) continue;

            var newApprove = finalTally.ApproveWeight - w;
            var newNonAbstaining = newApprove + finalTally.RejectWeight;
            var newApprovalFraction = newNonAbstaining > 0 ? newApprove / newNonAbstaining : 0;
            if (newApprovalFraction < threshold) builder.Add(p.AgentId);
        }
        return builder.ToImmutable();
    }
}
