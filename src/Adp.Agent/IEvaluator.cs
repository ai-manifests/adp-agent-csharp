using Adp.Manifest;

namespace Adp.Agent;

/// <summary>
/// The hook adopters implement to produce votes. This is the one piece of
/// the runtime that is intentionally <em>not</em> shipped in the framework —
/// it encodes the agent's domain-specific decision logic (run tests, query
/// a database, call an LLM, inspect a binary, etc.) and must be provided by
/// the adopter.
/// </summary>
/// <remarks>
/// Register an implementation with the ASP.NET Core DI container before
/// starting the agent:
/// <code>
/// builder.Services.AddSingleton&lt;IEvaluator, MyEvaluator&gt;();
/// </code>
/// The runtime will call <see cref="EvaluateAsync"/> every time it receives
/// a proposal request on <c>POST /api/propose</c> and will emit whatever
/// vote and confidence the evaluator returns.
/// </remarks>
public interface IEvaluator
{
    /// <summary>
    /// Produce a vote for the given action. The runtime supplies the action
    /// descriptor, the reversibility tier, the decision class, and an optional
    /// cancellation token. The evaluator returns a vote, a confidence in [0, 1],
    /// and an optional human-readable rationale that will be recorded in the
    /// journal's <c>justification.rationale</c> field.
    /// </summary>
    ValueTask<EvaluationResult> EvaluateAsync(
        EvaluationRequest request,
        CancellationToken ct = default);
}

/// <summary>What the runtime hands to an evaluator on each proposal request.</summary>
public sealed record EvaluationRequest(
    string DeliberationId,
    ProposalAction Action,
    ReversibilityTier Tier,
    string DecisionClass
);

/// <summary>What an evaluator returns. The runtime packages this into a full <see cref="Proposal"/>.</summary>
public sealed record EvaluationResult(
    Vote Vote,
    double Confidence,
    string Rationale,
    IReadOnlyList<string>? EvidenceRefs = null
)
{
    /// <summary>A no-op approval at full default confidence — useful as a stub during development.</summary>
    public static EvaluationResult Approve(double confidence = 0.75, string rationale = "stub approval") =>
        new(Vote.Approve, confidence, rationale);

    /// <summary>A no-op rejection — useful for evaluators that refuse on principle.</summary>
    public static EvaluationResult Reject(double confidence, string rationale) =>
        new(Vote.Reject, confidence, rationale);

    /// <summary>Abstain when the evaluator has no opinion — e.g. decision class out of scope.</summary>
    public static EvaluationResult Abstain(string rationale) =>
        new(Vote.Abstain, 0.0, rationale);
}
