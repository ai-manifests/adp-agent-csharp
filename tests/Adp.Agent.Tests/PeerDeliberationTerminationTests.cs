using Adp.Agent.Deliberation;
using Adp.Manifest;
using Xunit;

namespace Adp.Agent.Tests;

/// <summary>
/// Regression coverage for ADP §7.2 / §7.3 terminal-state classification.
/// The runner must default to <c>Deadlocked</c> on non-convergence (atomic
/// actions are the common case); <c>PartialCommit</c> requires the caller
/// to opt in via the <c>HasReversibleSubset</c> callback in
/// <see cref="PeerDeliberationOptions"/>.
/// </summary>
public class PeerDeliberationTerminationTests
{
    [Fact]
    public void HasReversibleSubset_DefaultIsNull()
    {
        var opts = new PeerDeliberationOptions();
        Assert.Null(opts.HasReversibleSubset);
    }

    [Fact]
    public void HasReversibleSubset_CanBeProvided()
    {
        Func<Adj.Manifest.ActionDescriptor, TallyResult, bool> cb = (a, t) => false;
        var opts = new PeerDeliberationOptions(HasReversibleSubset: cb);
        Assert.NotNull(opts.HasReversibleSubset);

        var dummyAction = new Adj.Manifest.ActionDescriptor(
            Kind: "merge_pull_request",
            Target: "x/y#1",
            Parameters: null);
        var dummyTally = new TallyResult(
            ApproveWeight: 0, RejectWeight: 1, AbstainWeight: 0,
            TotalDeliberationWeight: 1, ApprovalFraction: 0,
            ParticipationFraction: 1, ThresholdMet: false,
            ParticipationFloorMet: true, DomainVetoesClear: true,
            Converged: false);
        Assert.False(opts.HasReversibleSubset!.Invoke(dummyAction, dummyTally));
    }

    [Fact]
    public void DeliberationOrchestrator_ReversibleSubsetFalse_ReturnsDeadlocked()
    {
        var orch = new DeliberationOrchestrator();
        var nonConverged = new TallyResult(
            ApproveWeight: 0.255, RejectWeight: 1.404, AbstainWeight: 0,
            TotalDeliberationWeight: 1.659, ApprovalFraction: 0.154,
            ParticipationFraction: 1, ThresholdMet: false,
            ParticipationFloorMet: true, DomainVetoesClear: true,
            Converged: false);
        Assert.Equal(TerminationState.Deadlocked,
            orch.DetermineTermination(nonConverged, hasReversibleSubset: false));
    }

    [Fact]
    public void DeliberationOrchestrator_ReversibleSubsetTrue_ReturnsPartialCommit()
    {
        var orch = new DeliberationOrchestrator();
        var nonConverged = new TallyResult(
            ApproveWeight: 0.255, RejectWeight: 1.404, AbstainWeight: 0,
            TotalDeliberationWeight: 1.659, ApprovalFraction: 0.154,
            ParticipationFraction: 1, ThresholdMet: false,
            ParticipationFloorMet: true, DomainVetoesClear: true,
            Converged: false);
        Assert.Equal(TerminationState.PartialCommit,
            orch.DetermineTermination(nonConverged, hasReversibleSubset: true));
    }
}
