using System.Collections.Immutable;
using Acb.Manifest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Adp.Agent.Routing;

/// <summary>
/// ACB (Agent Cognitive Budget) endpoints. Currently only
/// <c>POST /api/budget</c> — materializes a <see cref="BudgetCommitted"/>
/// entry from the agent's <see cref="AcbDefaultsConfig"/>.
/// </summary>
public static class AcbEndpoints
{
    public static IEndpointRouteBuilder MapAcbEndpoints(
        this IEndpointRouteBuilder app,
        AgentConfig config,
        IRuntimeJournalStore journal)
    {
        app.MapPost("/api/budget", (BudgetRequest body) =>
        {
            if (config.Acb is null)
            {
                return Results.Json(
                    new { error = "acb_not_configured" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (body is null || string.IsNullOrEmpty(body.DeliberationId))
                return Results.BadRequest(new { error = "missing deliberationId" });

            var amount = body.AmountTotal ?? config.Acb.DefaultAmountTotal;
            var now = DateTimeOffset.UtcNow;
            var budget = new BudgetCommitted(
                EntryId: $"acb_{Guid.NewGuid():N}",
                DeliberationId: body.DeliberationId,
                Timestamp: now,
                PriorEntryHash: null,
                BudgetId: $"bgt_{Guid.NewGuid():N}",
                BudgetAuthority: config.Acb.BudgetAuthority,
                PostedAt: now,
                Denomination: config.Acb.Denomination,
                AmountTotal: amount,
                Pricing: config.Acb.Pricing,
                Settlement: config.Acb.Settlement,
                Constraints: config.Acb.Constraints ?? new BudgetConstraints(
                    MaxParticipants: 8, MaxRounds: 4, Irrevocable: false),
                Signature: "unsigned-v0"  // TODO: wire up ACB entry signing
            );

            // The ACB store is separate from the ADJ journal store — the ref
            // lib's Acb.Manifest.IBudgetStore is a distinct interface. For
            // v0.1.0 we return the budget record without persisting it to a
            // backing store; adopters who want durable budget tracking will
            // wire an IBudgetStore directly to this endpoint. Tracked for
            // v0.2.0.
            return Results.Json(new { budget }, statusCode: StatusCodes.Status200OK);
        });

        return app;
    }
}

public sealed record BudgetRequest(
    string DeliberationId,
    double? AmountTotal
);
