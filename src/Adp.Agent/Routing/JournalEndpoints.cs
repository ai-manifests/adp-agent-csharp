using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Adp.Agent.Routing;

/// <summary>
/// ADJ §7.1 query contract — the read-only surface peers and registries use
/// to spot-check journals, recompute calibration independently, and crawl
/// for batch verification.
/// </summary>
public static class JournalEndpoints
{
    public static IEndpointRouteBuilder MapAdjQueryEndpoints(
        this IEndpointRouteBuilder app,
        IRuntimeJournalStore journal)
    {
        // GET /adj/v0/calibration?agentId=&domain=
        app.MapGet("/adj/v0/calibration", (string agentId, string domain) =>
        {
            var score = journal.GetCalibration(agentId, domain);
            return Results.Json(score, JsonOptions);
        });

        // GET /adj/v0/deliberation/{id}
        app.MapGet("/adj/v0/deliberation/{id}", (string id) =>
        {
            var entries = journal.GetDeliberation(id);
            if (entries.IsEmpty)
                return Results.NotFound(new { error = "deliberation_not_found", deliberationId = id });
            return Results.Json(new
            {
                deliberationId = id,
                entries,
            }, JsonOptions);
        });

        // GET /adj/v0/deliberations?since=ISO8601&limit=100
        app.MapGet("/adj/v0/deliberations", (string? since, int? limit) =>
        {
            var sinceDt = ParseSince(since);
            var lim = limit is > 0 and <= 10_000 ? limit.Value : 100;
            var slices = journal.ListDeliberationsSince(sinceDt, lim);
            return Results.Json(new { deliberations = slices }, JsonOptions);
        });

        // GET /adj/v0/outcome/{id}
        app.MapGet("/adj/v0/outcome/{id}", (string id) =>
        {
            var outcome = journal.GetOutcome(id);
            if (outcome is null)
                return Results.NotFound(new { error = "outcome_not_found", deliberationId = id });
            return Results.Json(outcome, JsonOptions);
        });

        // GET /adj/v0/entries?since=ISO8601
        app.MapGet("/adj/v0/entries", (string? since) =>
        {
            var sinceDt = ParseSince(since);
            var entries = journal.GetAllEntriesSince(sinceDt);
            return Results.Json(new { entries }, JsonOptions);
        });

        return app;
    }

    private static DateTimeOffset ParseSince(string? since)
    {
        if (string.IsNullOrWhiteSpace(since)) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTimeOffset.MinValue;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
