using Adj.Manifest;
using Adp.Agent.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Adp.Agent.Middleware;

/// <summary>
/// Validates any journal entry that would be written by the agent itself
/// against the schema invariants required by ADJ §3 / §7.4 / §8.1 before
/// the write is accepted. This runs <em>inside</em> the runtime — it's not
/// a request-body validator on inbound HTTP traffic, it's a guard that
/// <see cref="Deliberation.RuntimeDeliberation"/> invokes on every
/// <see cref="IRuntimeJournalStore.Append"/> call to catch runtime bugs
/// before corrupt entries land on disk.
/// </summary>
/// <remarks>
/// The TypeScript runtime implements this as an Express middleware that
/// validates the JSON body of inbound write endpoints. The C# port exposes
/// the same validation as a static helper that the routing layer and the
/// deliberation state machine both call — middleware style in TypeScript,
/// direct invocation style in C#. The observable behavior (bad entries
/// get rejected with a specific error string) is identical.
/// </remarks>
public static class JournalEntryValidator
{
    /// <summary>
    /// Validate a single entry. Returns an error message if the entry is
    /// invalid, null if it's acceptable. Adopters typically call this from
    /// their custom journal-writing code if they bypass the runtime's
    /// built-in append path.
    /// </summary>
    public static string? Validate(JournalEntry entry)
    {
        if (string.IsNullOrEmpty(entry.EntryId))
            return "entryId is required";
        if (string.IsNullOrEmpty(entry.DeliberationId))
            return "deliberationId is required";
        if (entry.Timestamp == default)
            return "timestamp is required";

        return entry switch
        {
            DeliberationOpened o => ValidateOpened(o),
            ProposalEmitted p => ValidateProposal(p),
            RoundEvent r => ValidateRound(r),
            DeliberationClosed c => ValidateClosed(c),
            OutcomeObserved o => ValidateOutcome(o),
            _ => $"unknown journal entry type: {entry.GetType().Name}",
        };
    }

    /// <summary>
    /// Convenience wrapper: validate, throw on error. Used by the runtime's
    /// default write path where violations are programmer errors.
    /// </summary>
    public static void ValidateOrThrow(JournalEntry entry)
    {
        var error = Validate(entry);
        if (error is not null)
        {
            throw new InvalidOperationException(
                $"Journal entry validation failed ({entry.GetType().Name}): {error}");
        }
    }

    private static string? ValidateOpened(DeliberationOpened o)
    {
        if (string.IsNullOrEmpty(o.DecisionClass))
            return "deliberation_opened.decisionClass is required";
        if (o.Action is null)
            return "deliberation_opened.action is required";
        if (string.IsNullOrEmpty(o.Action.Kind))
            return "deliberation_opened.action.kind is required";
        if (o.Participants.Count == 0)
            return "deliberation_opened.participants cannot be empty";
        return null;
    }

    private static string? ValidateProposal(ProposalEmitted p)
    {
        var pd = p.Proposal;
        if (string.IsNullOrEmpty(pd.ProposalId))
            return "proposal_emitted.proposal.proposalId is required";
        if (string.IsNullOrEmpty(pd.AgentId))
            return "proposal_emitted.proposal.agentId is required";
        if (pd.Confidence < 0 || pd.Confidence > 1)
            return $"proposal_emitted.proposal.confidence must be in [0,1] (got {pd.Confidence})";
        if (string.IsNullOrEmpty(pd.Domain))
            return "proposal_emitted.proposal.domain is required";
        if (string.IsNullOrEmpty(pd.Vote))
            return "proposal_emitted.proposal.vote is required";
        return null;
    }

    private static string? ValidateRound(RoundEvent r)
    {
        if (r.Round < 0)
            return $"round_event.round must be non-negative (got {r.Round})";
        if (string.IsNullOrEmpty(r.AgentId))
            return "round_event.agentId is required";
        return null;
    }

    private static string? ValidateClosed(DeliberationClosed c)
    {
        if (c.FinalTally is null)
            return "deliberation_closed.finalTally is required";
        if (c.RoundCount < 0)
            return $"deliberation_closed.roundCount must be non-negative (got {c.RoundCount})";
        if (string.IsNullOrEmpty(c.Tier))
            return "deliberation_closed.tier is required";
        return null;
    }

    private static string? ValidateOutcome(OutcomeObserved o)
    {
        if (o.Success < 0 || o.Success > 1)
            return $"outcome_observed.success must be in [0,1] (got {o.Success})";
        if (string.IsNullOrEmpty(o.ReporterId))
            return "outcome_observed.reporterId is required";
        if (o.ReporterConfidence < 0 || o.ReporterConfidence > 1)
            return $"outcome_observed.reporterConfidence must be in [0,1] (got {o.ReporterConfidence})";
        return null;
    }
}

/// <summary>
/// Extension hooks for registering the Adp.Agent middleware pipeline.
/// Called from <see cref="AdpAgentHost"/> during startup; adopters can
/// also call these directly if they want to mount Adp.Agent routes into
/// an existing ASP.NET Core application.
/// </summary>
public static class MiddlewareExtensions
{
    /// <summary>
    /// Register the bearer-token auth middleware. No-op if
    /// <see cref="AgentConfig.Auth"/> is null.
    /// </summary>
    public static IApplicationBuilder UseAdpAuth(this IApplicationBuilder app, AgentConfig config) =>
        app.UseMiddleware<AuthMiddleware>(config);

    /// <summary>
    /// Register the fixed-window rate limiter.
    /// </summary>
    public static IApplicationBuilder UseAdpRateLimit(
        this IApplicationBuilder app,
        int maxRequestsPerWindow = 120,
        TimeSpan? window = null) =>
        app.UseMiddleware<RateLimitMiddleware>(maxRequestsPerWindow, window ?? TimeSpan.FromMinutes(1));
}
