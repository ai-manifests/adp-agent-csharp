using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Adp.Agent.Mcp;

/// <summary>
/// MCP (Model Context Protocol) tool server wiring for the C# runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Status: stub in v0.1.0.</b> The TypeScript runtime exposes six MCP
/// tools over SSE at <c>/mcp</c> — <c>adp_propose</c>, <c>adp_falsify</c>,
/// <c>adp_deliberate</c>, <c>adj_calibration</c>, <c>adj_journal</c>,
/// <c>adj_outcome</c> — so peers can call each other's ADP/ADJ operations
/// as MCP tool calls instead of HTTP POSTs. The C# port is tracked for
/// v0.2.0 along with distributed deliberation.
/// </para>
/// <para>
/// The official <c>ModelContextProtocol</c> NuGet package (v1.2.0) has
/// stabilized since this repo started, but wiring a full SSE tool server
/// on top of it is a ~300-line job that's not in scope for the v0.1.0
/// release. The stub below reserves the <c>/mcp</c> route prefix with a
/// 501 response so adopters who probe for MCP capability get a clear
/// error rather than a 404.
/// </para>
/// </remarks>
public static class AdpMcpServer
{
    public static IEndpointRouteBuilder MapAdpMcpEndpoints(
        this IEndpointRouteBuilder app,
        AgentConfig config,
        IRuntimeJournalStore journal)
    {
        app.MapGet("/mcp", () =>
            Microsoft.AspNetCore.Http.Results.Json(new
            {
                error = "not_implemented",
                message = "MCP tool server is not yet ported to the C# runtime (v0.2.0). Use the TypeScript runtime @ai-manifests/adp-agent if you need MCP tool integration today.",
            }, statusCode: Microsoft.AspNetCore.Http.StatusCodes.Status501NotImplemented));

        return app;
    }
}
