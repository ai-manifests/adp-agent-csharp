using System.Collections.Immutable;
using Acb.Manifest;
using Adj.Manifest;
using Adp.Manifest;
using AdpTerminationState = Adp.Manifest.TerminationState;
using AdjTerminationState = Adj.Manifest.TerminationState;
using AcbTerminationState = Acb.Manifest.TerminationState;
using AdjDeliberationConfig = Adj.Manifest.DeliberationConfig;
using AcbTally = Acb.Manifest.Tally;

namespace Adp.Agent.Deliberation;

/// <summary>
/// Optional run-time options for <see cref="PeerDeliberation.RunAsync"/>.
/// Mirrors the TS runtime's <c>DeliberationRunOptions</c>.
/// </summary>
// Optional pre-loaded historical deliberations for ACB habit memory.
// When null, callers that wired IRuntimeJournalStoreScannable can supply
// history at run time; otherwise habit discount is zero.
public sealed record PeerDeliberationOptions(
    BudgetCommitted? Budget = null,
    IReadOnlyList<HistoricalDeliberation>? HabitHistory = null
);

/// <summary>
/// Result envelope returned by <see cref="PeerDeliberation.RunAsync"/>.
/// </summary>
public sealed record PeerDeliberationResult(
    string DeliberationId,
    AdpTerminationState Status,
    int Rounds,
    ImmutableDictionary<string, double> Weights,
    ImmutableList<Adp.Manifest.TallyResult> Tallies,
    ImmutableList<ProposalSummary> Proposals,
    // Settlement record produced when an ACB budget is attached. ACB entries
    // have a different envelope from Adj entries, so they are returned out
    // of band rather than written to the Adj journal store. Callers wire
    // them to a separate ACB journal (or to a unified store) as needed.
    SettlementRecorded? Settlement,
    double? InitialDisagreementMagnitude
);

/// <summary>Per-agent summary in the deliberation result.</summary>
public sealed record ProposalSummary(
    string AgentId,
    Vote Vote,
    Vote CurrentVote,
    double Confidence
);

/// <summary>
/// Peer-to-peer deliberation state machine. Any agent with the
/// <c>Initiator</c> role can construct one to drive a deliberation: it
/// discovers peers, requests proposals, tallies, runs belief-update
/// rounds, and writes a complete journal trace.
///
/// <para>
/// This is the C# port of the TypeScript runtime's <c>PeerDeliberation</c>.
/// Method shapes are kept aligned with the TS contract; the math is
/// delegated to <see cref="DeliberationOrchestrator"/> /
/// <see cref="WeightingFunction"/> from <c>Adp.Manifest</c> and to
/// <see cref="PricingCalculator"/> / <see cref="SettlementCalculator"/> /
/// <see cref="HabitMemoryCalculator"/> from <c>Acb.Manifest</c>.
/// </para>
/// </summary>
public sealed class PeerDeliberation
{
    private readonly AgentConfig _self;
    private readonly IRuntimeJournalStore _journal;
    private readonly IReadOnlyList<PeerConfig> _peers;
    private readonly IPeerTransport _transport;
    private readonly DeliberationOrchestrator _orchestrator;

    private readonly Dictionary<string, AgentManifest> _manifests = new();
    private readonly Dictionary<string, string> _peerUrlMap = new();
    private readonly Dictionary<string, double> _weights = new();
    private readonly List<Adp.Manifest.Proposal> _proposals = new();
    private readonly List<Adp.Manifest.TallyResult> _tallies = new();
    private readonly List<JournalEntry> _journalEntries = new();
    private readonly ContributionTracker _contributionTracker = new();
    private int _rounds;

    public PeerDeliberation(
        AgentConfig self,
        IRuntimeJournalStore journal,
        IReadOnlyList<PeerConfig> peers,
        IPeerTransport transport,
        DeliberationOrchestrator? orchestrator = null)
    {
        _self = self;
        _journal = journal;
        _peers = peers;
        _transport = transport;
        _orchestrator = orchestrator ?? new DeliberationOrchestrator();
    }

    public async Task<PeerDeliberationResult> RunAsync(
        ActionDescriptor action,
        Adp.Manifest.ReversibilityTier tier = Adp.Manifest.ReversibilityTier.PartiallyReversible,
        PeerDeliberationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new PeerDeliberationOptions();
        var dlbId = $"dlb_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        // 1. Discover peers — fetchManifest also populates the transport's URL→agentId map
        foreach (var peer in _peers)
        {
            var manifest = await _transport.FetchManifestAsync(peer.Url, ct).ConfigureAwait(false);
            _manifests[manifest.AgentId] = manifest;
            _peerUrlMap[manifest.AgentId] = peer.Url;
        }

        // Self-manifest. The initiator never fetches its own manifest, so
        // RegisterAgent is the only path that binds the self URL to the self
        // agent id in the transport. Without this, outgoing self-proposal and
        // self-journal calls fall back to wildcard '*' peer-token lookup,
        // which produces no Authorization header, which makes the agent's
        // own auth middleware reject the call with 401.
        var selfUrl = $"http://{_self.Domain}:{_self.Port}";
        _peerUrlMap[_self.AgentId] = selfUrl;
        _transport.RegisterAgent(selfUrl, _self.AgentId);

        var participants = _manifests.Keys.Append(_self.AgentId).ToImmutableList();

        // ACB budget commit precedes deliberation_opened in the hash chain.
        // We do NOT append BudgetCommitted to the Adj journal store
        // (different envelope); it returns in the result for ACB-aware
        // callers to persist to a separate store.
        if (options.Budget is not null)
        {
            var budget = options.Budget;
            if (budget.Constraints?.MaxParticipants is { } maxP && participants.Count > maxP)
            {
                throw new InvalidOperationException(
                    $"Budget {budget.BudgetId} maxParticipants={maxP} exceeded by deliberation with {participants.Count} participants");
            }
        }

        // Journal: deliberation_opened
        _journalEntries.Add(new DeliberationOpened(
            EntryId: NewEntryId(),
            DeliberationId: dlbId,
            Timestamp: now,
            PriorEntryHash: null,
            DecisionClass: _self.DecisionClasses.Count > 0 ? _self.DecisionClasses[0] : "default",
            Action: action,
            Participants: participants,
            Config: new AdjDeliberationConfig(MaxRounds: 3, ParticipationFloor: 0.50)));

        // 2. Request proposals from peers
        foreach (var (agentId, manifest) in _manifests)
        {
            var resp = await _transport.RequestProposalAsync(_peerUrlMap[agentId], dlbId, action, tier, ct).ConfigureAwait(false);
            _proposals.Add(resp.Proposal);
            _contributionTracker.RecordProposal(agentId);

            var domain = manifest.DomainAuthorities.Keys.FirstOrDefault() ?? _self.DecisionClasses[0];
            var authority = manifest.DomainAuthorities.TryGetValue(domain, out var auth) ? auth.Authority : 0.5;
            var cal = await _transport.FetchCalibrationAsync(manifest.JournalEndpoint, agentId, domain, ct).ConfigureAwait(false);
            _weights[agentId] = WeightingFunction.ComputeWeight(authority, cal, domain, resp.Proposal.Stake.Magnitude);

            _journalEntries.Add(BuildProposalEmitted(dlbId, resp.Proposal, domain));
        }

        // Self-proposal — same path as peers, exercises the auth round-trip
        var selfResp = await _transport.RequestProposalAsync(selfUrl, dlbId, action, tier, ct).ConfigureAwait(false);
        _proposals.Add(selfResp.Proposal);
        _contributionTracker.RecordProposal(_self.AgentId);

        var selfDomain = _self.DecisionClasses.Count > 0 ? _self.DecisionClasses[0] : "default";
        var selfAuthority = _self.Authorities.TryGetValue(selfDomain, out var sa) ? sa : 0.5;
        var selfJournalEndpoint = $"http://{_self.Domain}:{_self.Port}/adj/v0";
        var selfCal = await _transport.FetchCalibrationAsync(selfJournalEndpoint, _self.AgentId, selfDomain, ct).ConfigureAwait(false);
        _weights[_self.AgentId] = WeightingFunction.ComputeWeight(selfAuthority, selfCal, selfDomain, selfResp.Proposal.Stake.Magnitude);
        _journalEntries.Add(BuildProposalEmitted(dlbId, selfResp.Proposal, selfDomain));

        // 3. Round 0 tally
        var proposalsByAgent = _proposals.ToImmutableDictionary(p => p.AgentId);
        var weightsImm = _weights.ToImmutableDictionary();
        var tally = _orchestrator.Tally(proposalsByAgent, weightsImm, tier);
        _tallies.Add(tally);
        var initialTally = tally;
        var initialMagnitude = PricingCalculator.ComputeDisagreementMagnitude(
            new AcbTally(tally.ApproveWeight, tally.RejectWeight, tally.AbstainWeight));

        // 4. Belief-update rounds
        var maxRounds = options.Budget?.Constraints?.MaxRounds ?? 3;
        for (var round = 1; round <= maxRounds && !tally.Converged; round++)
        {
            _rounds = round;
            var revised = false;

            var rejecters = _proposals.Where(p => p.CurrentVote == Vote.Reject).ToList();
            var approvers = _proposals.Where(p => p.CurrentVote == Vote.Approve).ToList();

            Adp.Manifest.Proposal? evidenceAgent = approvers.Count == 0
                ? null
                : approvers.Aggregate((best, p) =>
                    (_weights.TryGetValue(p.AgentId, out var pw) ? pw : 0.0) >
                    (_weights.TryGetValue(best.AgentId, out var bw) ? bw : 0.0) ? p : best);

            for (var ri = 0; ri < rejecters.Count; ri++)
            {
                var rejecter = rejecters[ri];
                var active = rejecter.DissentConditions.Where(dc => dc.Status == DissentConditionStatus.Active).ToList();
                var allFalsified = active.Count > 0;

                foreach (var condition in active)
                {
                    if (evidenceAgent is null) { allFalsified = false; continue; }

                    _journalEntries.Add(BuildRoundEvent(
                        dlbId, round, EventKind.FalsificationEvidence,
                        evidenceAgent.AgentId, rejecter.AgentId, condition.Id));
                    _contributionTracker.RecordFalsificationEvidence(
                        evidenceAgent.AgentId, rejecter.AgentId, condition.Id);

                    var response = await _transport.SendFalsificationAsync(
                        _peerUrlMap[rejecter.AgentId], condition.Id, round, evidenceAgent.AgentId, ct).ConfigureAwait(false);

                    var responseKind = response.Action switch
                    {
                        "acknowledge" => EventKind.Acknowledge,
                        "reject" => EventKind.Reject,
                        "amend" => EventKind.Amend,
                        _ => EventKind.Reject,
                    };
                    _journalEntries.Add(BuildRoundEvent(
                        dlbId, round, responseKind, rejecter.AgentId, null, condition.Id));

                    if (response.Action == "acknowledge")
                    {
                        var idx = _proposals.FindIndex(p => p.AgentId == rejecter.AgentId);
                        if (idx >= 0)
                        {
                            _proposals[idx] = _proposals[idx].WithDissentCondition(condition.Id, dc => dc with
                            {
                                Status = DissentConditionStatus.Falsified,
                                TestedInRound = round,
                                TestedBy = evidenceAgent.AgentId,
                            });
                            rejecter = _proposals[idx];
                        }
                        _contributionTracker.RecordAcknowledgement(
                            evidenceAgent.AgentId, rejecter.AgentId, condition.Id);
                    }
                    else
                    {
                        allFalsified = false;
                    }
                }

                if (allFalsified)
                {
                    var idx = _proposals.FindIndex(p => p.AgentId == rejecter.AgentId);
                    if (idx >= 0)
                    {
                        _proposals[idx] = _proposals[idx].Revise(
                            round, Vote.Abstain, null,
                            $"All dissent conditions falsified in round {round}.");
                    }
                    revised = true;

                    _journalEntries.Add(BuildRoundEvent(
                        dlbId, round, EventKind.Revise, rejecter.AgentId, null, null));
                }
            }

            if (!revised) break;

            proposalsByAgent = _proposals.ToImmutableDictionary(p => p.AgentId);
            tally = _orchestrator.Tally(proposalsByAgent, weightsImm, tier);
            _tallies.Add(tally);
        }

        // 5. Close
        var status = _orchestrator.DetermineTermination(tally, hasReversibleSubset: true);
        _journalEntries.Add(new DeliberationClosed(
            EntryId: NewEntryId(),
            DeliberationId: dlbId,
            Timestamp: DateTimeOffset.UtcNow,
            PriorEntryHash: null,
            Termination: ToAdjTermination(status),
            RoundCount: _rounds,
            Tier: TierToString(tier),
            FinalTally: BuildTallyRecord(tally, tier),
            Weights: weightsImm,
            CommittedAction: status == AdpTerminationState.Deadlocked ? null : action));

        // 5.5 ACB settlement (immediate-mode here; deferred/two_phase wait for outcome)
        SettlementRecorded? settlementEntry = null;
        if (options.Budget is not null)
        {
            var budget = options.Budget;
            var routine = PricingCalculator.SelectRoutine(
                budget.Pricing,
                new AcbTally(initialTally.ApproveWeight, initialTally.RejectWeight, initialTally.AbstainWeight),
                _rounds,
                ToAcbTermination(status));
            var unlockTriggered = routine == Routine.Expensive;

            // Habit history: caller-supplied via options, otherwise scan the
            // local journal if it implements the scannable capability.
            var history = options.HabitHistory ?? FindHabitHistory(action, dlbId);
            var habitDiscount = HabitMemoryCalculator.ComputeHabitDiscount(history);

            var drawTotal = PricingCalculator.ComputeDraw(
                budget.Pricing, routine, _proposals.Count, _rounds, habitDiscount);

            var threshold = DeliberationOrchestrator.GetThreshold(tier);
            var loadBearing = ContributionTracker.ComputeLoadBearingAgents(
                tally, weightsImm, threshold, _proposals);
            var brierDeltas = new Dictionary<string, double>(); // immediate mode
            var contributions = _contributionTracker.Build(loadBearing, brierDeltas);

            settlementEntry = SettlementCalculator.BuildSettlementRecord(new SettlementInputs(
                EntryId: NewEntryId(),
                DeliberationId: dlbId,
                Timestamp: DateTimeOffset.UtcNow,
                PriorEntryHash: null,
                BudgetId: budget.BudgetId,
                AmountTotal: budget.AmountTotal,
                DrawTotal: drawTotal,
                Settlement: budget.Settlement,
                Contributions: contributions,
                SubstrateReports: ImmutableList<SubstrateReport>.Empty,
                HabitDiscountApplied: habitDiscount,
                UnlockTriggered: unlockTriggered,
                DisagreementMagnitudeInitial: initialMagnitude,
                OutcomeReferenced: null,
                Signature: "self"));
        }

        // 6. Persist + gossip — write Adj entries to local journal first, then push to peers + self
        foreach (var entry in _journalEntries)
        {
            _journal.Append(entry);
        }
        var allUrls = _peers.Select(p => p.Url).Append(selfUrl).ToList();
        foreach (var url in allUrls)
        {
            await _transport.PushJournalEntriesAsync(url, _journalEntries, ct).ConfigureAwait(false);
        }

        return new PeerDeliberationResult(
            DeliberationId: dlbId,
            Status: status,
            Rounds: _rounds,
            Weights: weightsImm,
            Tallies: _tallies.ToImmutableList(),
            Proposals: _proposals.Select(p => new ProposalSummary(
                AgentId: p.AgentId,
                Vote: p.Vote,
                CurrentVote: p.CurrentVote,
                Confidence: p.Confidence
            )).ToImmutableList(),
            Settlement: settlementEntry,
            InitialDisagreementMagnitude: initialMagnitude);
    }

    // ---------- Helpers ----------

    private static string NewEntryId() => $"adj_{Guid.NewGuid():N}";

    private static string TierToString(Adp.Manifest.ReversibilityTier tier) => tier switch
    {
        Adp.Manifest.ReversibilityTier.Reversible => "reversible",
        Adp.Manifest.ReversibilityTier.PartiallyReversible => "partially_reversible",
        Adp.Manifest.ReversibilityTier.Irreversible => "irreversible",
        _ => "partially_reversible",
    };

    private static AdjTerminationState ToAdjTermination(AdpTerminationState s) => s switch
    {
        AdpTerminationState.Converged => AdjTerminationState.Converged,
        AdpTerminationState.PartialCommit => AdjTerminationState.PartialCommit,
        AdpTerminationState.Deadlocked => AdjTerminationState.Deadlocked,
        _ => AdjTerminationState.Deadlocked,
    };

    private static AcbTerminationState ToAcbTermination(AdpTerminationState s) => s switch
    {
        AdpTerminationState.Converged => AcbTerminationState.Converged,
        AdpTerminationState.PartialCommit => AcbTerminationState.PartialCommit,
        AdpTerminationState.Deadlocked => AcbTerminationState.Deadlocked,
        _ => AcbTerminationState.Deadlocked,
    };

    private static ProposalEmitted BuildProposalEmitted(string dlbId, Adp.Manifest.Proposal proposal, string domain) =>
        new(
            EntryId: NewEntryId(),
            DeliberationId: dlbId,
            Timestamp: DateTimeOffset.UtcNow,
            PriorEntryHash: null,
            Proposal: new ProposalData(
                ProposalId: proposal.ProposalId,
                AgentId: proposal.AgentId,
                Vote: proposal.Vote.ToString().ToLowerInvariant(),
                Confidence: proposal.Confidence,
                Domain: domain,
                CalibrationAtStake: proposal.Stake.CalibrationAtStake,
                DissentConditions: proposal.DissentConditions.Select(dc => new ConditionRecord(
                    Id: dc.Id,
                    Condition: dc.Condition,
                    Status: dc.Status.ToString().ToLowerInvariant(),
                    AmendmentCount: dc.Amendments.Count,
                    TestedInRound: dc.TestedInRound)).ToImmutableList()));

    private static RoundEvent BuildRoundEvent(
        string dlbId, int round, EventKind kind,
        string agentId, string? targetAgentId, string? targetConditionId) =>
        new(
            EntryId: NewEntryId(),
            DeliberationId: dlbId,
            Timestamp: DateTimeOffset.UtcNow,
            PriorEntryHash: null,
            Round: round,
            EventKind: kind,
            AgentId: agentId,
            TargetAgentId: targetAgentId,
            TargetConditionId: targetConditionId,
            Payload: ImmutableDictionary<string, object>.Empty);

    private static TallyRecord BuildTallyRecord(Adp.Manifest.TallyResult tally, Adp.Manifest.ReversibilityTier tier) =>
        new(
            ApproveWeight: tally.ApproveWeight,
            RejectWeight: tally.RejectWeight,
            AbstainWeight: tally.AbstainWeight,
            TotalWeight: tally.TotalDeliberationWeight,
            ApprovalFraction: tally.ApprovalFraction,
            ParticipationFraction: tally.ParticipationFraction,
            Threshold: DeliberationOrchestrator.GetThreshold(tier));

    /// <summary>
    /// Find similar prior deliberations from the local journal. Default
    /// strategy: match on <c>action.kind</c> with structural similarity on
    /// <c>action.target</c>. Mirrors the TS runtime's <c>findHabitHistory</c>.
    /// </summary>
    private IReadOnlyList<HistoricalDeliberation> FindHabitHistory(ActionDescriptor action, string excludeDlbId)
    {
        if (_journal is not IRuntimeJournalStoreScannable scannable)
            return Array.Empty<HistoricalDeliberation>();

        var entries = scannable.GetAllEntries().Where(e => e.DeliberationId != excludeDlbId).ToList();
        var closedByDlb = new Dictionary<string, DeliberationClosed>();
        var outcomeByDlb = new Dictionary<string, OutcomeObserved>();
        foreach (var e in entries)
        {
            if (e is DeliberationClosed dc) closedByDlb[e.DeliberationId] = dc;
            else if (e is OutcomeObserved oo)
            {
                if (!outcomeByDlb.TryGetValue(e.DeliberationId, out var existing) || oo.Timestamp > existing.Timestamp)
                    outcomeByDlb[e.DeliberationId] = oo;
            }
        }

        var history = new List<HistoricalDeliberation>();
        foreach (var (dlbId, closed) in closedByDlb)
        {
            if (closed.CommittedAction is null) continue;
            var committed = closed.CommittedAction;
            double similarity = 0;
            if (committed.Kind == action.Kind)
            {
                similarity = 0.5;
                if (committed.Target == action.Target) similarity = 1.0;
                else if (committed.Target.Split('/')[0] == action.Target.Split('/')[0]) similarity = 0.85;
            }
            if (similarity == 0) continue;

            var success = outcomeByDlb.TryGetValue(dlbId, out var outcome) && outcome.Success >= 0.5;
            history.Add(new HistoricalDeliberation(Similarity: similarity, SuccessfulOutcome: success));
        }
        return history;
    }
}

/// <summary>
/// Optional capability marker for journal stores that can yield every
/// entry across deliberations (used for ACB habit-memory lookups). Stores
/// that don't expose a global scan don't need to implement this; the
/// deliberation runner falls back to "no history" — habit memory then
/// produces no discount, which is the spec-correct behavior for an empty
/// history.
/// </summary>
public interface IRuntimeJournalStoreScannable : IRuntimeJournalStore
{
    IEnumerable<JournalEntry> GetAllEntries();
}
