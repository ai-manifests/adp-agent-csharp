using System.Text.Json;
using Adp.Agent.CalibrationSnapshot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Adp.Agent.Routing;

/// <summary>
/// Unauthenticated discovery endpoints: health check, agent manifest, and
/// signed calibration snapshot. These are the public identity surface
/// every peer and registry hits first.
/// </summary>
public static class ManifestEndpoints
{
    public static IEndpointRouteBuilder MapAdpManifestEndpoints(
        this IEndpointRouteBuilder app,
        AgentConfig config,
        IRuntimeJournalStore journal)
    {
        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            agentId = config.AgentId,
        }));

        // ADP manifest — spec §5 discovery doc.
        app.MapGet("/.well-known/adp-manifest.json", () =>
        {
            var manifest = AgentManifest.FromConfig(config);
            return Results.Json(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
            });
        });

        // Signed calibration snapshot — ADJ §7.4. Returns an error shape
        // rather than 404 when signing isn't configured so that verifiers
        // can distinguish "this agent has no key" from "this endpoint
        // doesn't exist."
        app.MapGet("/.well-known/adp-calibration.json", () =>
        {
            if (config.Auth?.PrivateKey is null)
            {
                return Results.Json(
                    new { error = "Agent has no signing key configured; cannot publish signed calibration" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                var envelope = SnapshotBuilder.BuildEnvelope(config, journal);
                return Results.Json(envelope, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = $"failed to build calibration snapshot: {ex.Message}" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        return app;
    }
}
