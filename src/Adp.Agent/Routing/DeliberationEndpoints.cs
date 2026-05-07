using System.Text.Json;
using Adj.Manifest;
using Adp.Agent.Deliberation;
using Adp.Manifest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Adp.Agent.Routing;

/// <summary>
/// Deliberation write endpoints: proposal emission, falsification response,
/// full distributed deliberation initiation, and outcome recording.
/// </summary>
public static class DeliberationEndpoints
{
    public static IEndpointRouteBuilder MapAdpDeliberationEndpoints(
        this IEndpointRouteBuilder app,
        AgentConfig config,
        IRuntimeJournalStore journal,
        RuntimeDeliberation runtime)
    {
        // POST /api/propose — the single-agent path. Runs the evaluator,
        // builds a proposal, signs it (if signing is configured), journals
        // ProposalEmitted, and returns the signed proposal.
        app.MapPost("/api/propose", async (HttpContext ctx, ProposeRequest body) =>
        {
            if (body is null || string.IsNullOrEmpty(body.DeliberationId) || body.Action is null)
                return Results.BadRequest(new { error = "missing deliberationId or action" });

            var action = new ActionDescriptor(
                Kind: body.Action.Kind,
                Target: body.Action.Target,
                Parameters: body.Action.Parameters);

            var tier = body.Tier ?? ReversibilityTier.PartiallyReversible;
            var decisionClass = body.DecisionClass
                ?? (config.DecisionClasses.Count > 0 ? config.DecisionClasses[0] : "default");

            var signed = await runtime.RunProposalAsync(
                body.DeliberationId, action, tier, decisionClass, ctx.RequestAborted);

            return Results.Json(new
            {
                proposal = signed.Proposal,
                signature = signed.Signature,
            }, JsonOptions);
        });

        // POST /api/respond-falsification — stub for v0.1.0.
        // The TS runtime implements this as part of the belief-update loop.
        // The C# port handles the single-round path only in v0.1.0; the
        // distributed path (including falsification responses) is the
        // headline follow-up for v0.2.0.
        app.MapPost("/api/respond-falsification", () =>
            Results.Json(new
            {
                error = "not_implemented",
                message = "Falsification response handling is not yet ported to C#. Tracked for v0.2.0.",
            }, statusCode: StatusCodes.Status501NotImplemented));

        // POST /api/deliberate — initiator endpoint. Same stub status as
        // respond-falsification. Adopters who need distributed deliberation
        // today should use the TypeScript runtime.
        app.MapPost("/api/deliberate", () =>
            Results.Json(new
            {
                error = "not_implemented",
                message = "Distributed deliberation initiation is not yet ported to C#. Tracked for v0.2.0. Until then, the C# runtime supports single-agent proposal emission via POST /api/propose.",
            }, statusCode: StatusCodes.Status501NotImplemented));

        // POST /api/record-outcome — CI reporter posts observed outcomes here.
        app.MapPost("/api/record-outcome", (RecordOutcomeRequest body) =>
        {
            if (body is null || string.IsNullOrEmpty(body.DeliberationId) || string.IsNullOrEmpty(body.ReporterId))
                return Results.BadRequest(new { error = "missing deliberationId or reporterId" });

            runtime.RecordOutcome(
                deliberationId: body.DeliberationId,
                success: body.Success,
                reporterId: body.ReporterId,
                reporterConfidence: body.ReporterConfidence,
                groundTruth: body.GroundTruth,
                evidenceRefs: body.EvidenceRefs ?? Array.Empty<string>(),
                outcomeClass: body.OutcomeClass ?? OutcomeClass.Binary);

            return Results.Ok(new { status = "recorded" });
        });

        return app;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record ProposeRequest(
    string DeliberationId,
    ActionRequest Action,
    ReversibilityTier? Tier,
    string? DecisionClass
);

public sealed record ActionRequest(
    string Kind,
    string Target,
    System.Collections.Immutable.ImmutableDictionary<string, string>? Parameters
);

public sealed record RecordOutcomeRequest(
    string DeliberationId,
    double Success,
    string ReporterId,
    double ReporterConfidence,
    bool GroundTruth,
    IReadOnlyList<string>? EvidenceRefs,
    OutcomeClass? OutcomeClass
);
