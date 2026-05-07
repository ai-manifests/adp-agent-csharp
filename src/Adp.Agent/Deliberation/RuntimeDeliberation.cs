using System.Collections.Immutable;
using Adj.Manifest;
using Adp.Manifest;
using Adp.Agent.Signing;
using AdjEntry = Adj.Manifest;
using AdpProposal = Adp.Manifest;

namespace Adp.Agent.Deliberation;

/// <summary>
/// Runtime glue between <see cref="IEvaluator"/>, <see cref="Adp.Manifest.Proposal"/>
/// construction, proposal signing, and journal persistence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope note:</b> The v0.1.0 C# runtime handles the single-agent
/// proposal path (evaluator → proposal → journal write) and leaves full
/// distributed deliberation — peer communication, belief-update rounds,
/// falsification handling, termination — as a follow-up. The HTTP routes
/// for the distributed path return 501 Not Implemented for now, with a
/// clear error message pointing at this file.
/// </para>
/// <para>
/// The TypeScript runtime at <c>@ai-manifests/adp-agent</c> does implement
/// the full distributed deliberation state machine, and cross-language
/// golden-vector tests only cover the parts that are implemented on both
/// sides. See the C# CHANGELOG for the feature parity matrix.
/// </para>
/// </remarks>
public sealed class RuntimeDeliberation
{
    private readonly AgentConfig _config;
    private readonly IRuntimeJournalStore _journal;
    private readonly IEvaluator _evaluator;

    public RuntimeDeliberation(AgentConfig config, IRuntimeJournalStore journal, IEvaluator evaluator)
    {
        _config = config;
        _journal = journal;
        _evaluator = evaluator;
    }

    /// <summary>
    /// Handle a <c>POST /api/propose</c> request. Runs the evaluator, builds
    /// a signed proposal, writes <see cref="AdjEntry.ProposalEmitted"/> to
    /// the journal, and returns the signed proposal.
    /// </summary>
    public async Task<SignedProposal> RunProposalAsync(
        string deliberationId,
        AdjEntry.ActionDescriptor action,
        ReversibilityTier tier,
        string decisionClass,
        CancellationToken ct = default)
    {
        // Step 1: run the evaluator
        var evalResult = await _evaluator.EvaluateAsync(
            new EvaluationRequest(deliberationId, BuildProposalAction(action), tier, decisionClass),
            ct);

        // Step 2: build the ADP Proposal (rich record)
        var proposalId = $"prp_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var proposal = new AdpProposal.Proposal(
            ProposalId: proposalId,
            DeliberationId: deliberationId,
            AgentId: _config.AgentId,
            Timestamp: now,
            Action: BuildProposalAction(action),
            Vote: evalResult.Vote,
            Confidence: evalResult.Confidence,
            DomainClaim: new DomainClaim(
                Domain: decisionClass,
                AuthoritySource: $"mcp-manifest:{_config.AgentId}#authorities"),
            ReversibilityTier: tier,
            BlastRadius: new BlastRadius(
                Scope: ImmutableList<string>.Empty,
                EstimatedUsersAffected: 0,
                RollbackCostSeconds: 0),
            Justification: new Justification(
                Summary: evalResult.Rationale,
                EvidenceRefs: (evalResult.EvidenceRefs ?? Array.Empty<string>()).ToImmutableList()),
            Stake: new Stake(
                DeclaredBy: _config.AgentId,
                Magnitude: _config.StakeMagnitude,
                CalibrationAtStake: true),
            DissentConditions: BuildDissentConditions(),
            Revisions: ImmutableList<VoteRevision>.Empty);

        // Step 3: sign (if a private key is configured)
        string? signature = null;
        if (_config.Auth?.PrivateKey is { Length: > 0 } pk)
        {
            signature = Ed25519Signer.SignProposal(proposal, pk);
        }

        // Step 4: write ProposalEmitted to the journal
        var entry = new AdjEntry.ProposalEmitted(
            EntryId: $"adj_{Guid.NewGuid():N}",
            DeliberationId: deliberationId,
            Timestamp: now,
            PriorEntryHash: null,
            Proposal: new AdjEntry.ProposalData(
                ProposalId: proposalId,
                AgentId: _config.AgentId,
                Vote: evalResult.Vote.ToString().ToLowerInvariant(),
                Confidence: evalResult.Confidence,
                Domain: decisionClass,
                CalibrationAtStake: true,
                DissentConditions: BuildConditionRecords()));
        _journal.Append(entry);

        return new SignedProposal(proposal, signature);
    }

    /// <summary>
    /// Handle a <c>POST /api/record-outcome</c> request. Writes an
    /// <see cref="AdjEntry.OutcomeObserved"/> entry to the journal for a
    /// previously closed deliberation. This is how the CI reporter closes
    /// the calibration loop: it observes a downstream outcome (build passed
    /// or failed, incident occurred or didn't) and reports it back.
    /// </summary>
    public void RecordOutcome(
        string deliberationId,
        double success,
        string reporterId,
        double reporterConfidence,
        bool groundTruth,
        IEnumerable<string> evidenceRefs,
        OutcomeClass outcomeClass = OutcomeClass.Binary)
    {
        var entry = new AdjEntry.OutcomeObserved(
            EntryId: $"adj_{Guid.NewGuid():N}",
            DeliberationId: deliberationId,
            Timestamp: DateTimeOffset.UtcNow,
            PriorEntryHash: null,
            ObservedAt: DateTimeOffset.UtcNow,
            OutcomeClass: outcomeClass,
            Success: success,
            EvidenceRefs: evidenceRefs.ToImmutableList(),
            ReporterId: reporterId,
            ReporterConfidence: reporterConfidence,
            GroundTruth: groundTruth,
            Supersedes: null);
        _journal.Append(entry);
    }

    private static AdpProposal.ProposalAction BuildProposalAction(AdjEntry.ActionDescriptor action) =>
        new(
            Kind: action.Kind,
            Target: action.Target,
            Parameters: action.Parameters ?? ImmutableDictionary<string, string>.Empty);

    private ImmutableList<DissentCondition> BuildDissentConditions()
    {
        var builder = ImmutableList.CreateBuilder<DissentCondition>();
        var i = 0;
        foreach (var text in _config.DissentConditions)
        {
            builder.Add(DissentCondition.Create($"dc_{_config.AgentId}_{i++:D3}", text));
        }
        return builder.ToImmutable();
    }

    private ImmutableList<AdjEntry.ConditionRecord> BuildConditionRecords()
    {
        var builder = ImmutableList.CreateBuilder<AdjEntry.ConditionRecord>();
        var i = 0;
        foreach (var text in _config.DissentConditions)
        {
            builder.Add(new AdjEntry.ConditionRecord(
                Id: $"dc_{_config.AgentId}_{i++:D3}",
                Condition: text,
                Status: "active",
                AmendmentCount: 0,
                TestedInRound: null));
        }
        return builder.ToImmutable();
    }
}

/// <summary>Return envelope for a signed proposal — the proposal plus its signature.</summary>
public sealed record SignedProposal(
    AdpProposal.Proposal Proposal,
    string? Signature
);
