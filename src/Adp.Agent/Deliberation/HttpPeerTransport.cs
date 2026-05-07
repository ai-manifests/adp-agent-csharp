using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Acb.Manifest;
using Adj.Manifest;
using Adp.Manifest;
using CalibrationScore = Adp.Manifest.CalibrationScore;

namespace Adp.Agent.Deliberation;

/// <summary>
/// HTTP implementation of <see cref="IPeerTransport"/>. Sends peer calls
/// over plain HTTPS using the configured <see cref="HttpClient"/>, with
/// outbound auth headers resolved from <see cref="AuthConfig.PeerTokens"/>
/// via <see cref="PeerAuthHeaders"/>.
///
/// <para>
/// The transport keeps an internal URL → agentId map so that
/// <see cref="PeerAuthHeaders.GetPeerToken"/> can resolve the right
/// peer-token for each outgoing call. The map is populated automatically
/// as a side-effect of <see cref="FetchManifestAsync"/> and explicitly via
/// <see cref="RegisterAgent"/> — see <see cref="IPeerTransport"/> for the
/// rationale.
/// </para>
/// </summary>
public sealed class HttpPeerTransport : IPeerTransport
{
    /// <summary>
    /// Per-call timeout for slow peer responses (proposal requests block on
    /// the peer's evaluator, which may be a 5–30s LLM call). Without this
    /// an unresponsive peer hangs the deliberation indefinitely — matches
    /// the equivalent TS lib default.
    /// </summary>
    private static readonly TimeSpan ProposalTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Tighter timeout for fetches that should be near-instant (manifest,
    /// calibration, journal gossip). A peer that takes &gt;10s to serve a
    /// static .well-known doc is effectively dead.
    /// </summary>
    private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly AuthConfig? _auth;
    private readonly Dictionary<string, string> _peerAgentIds = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpPeerTransport(HttpClient http, AuthConfig? auth = null)
    {
        _http = http;
        _auth = auth;
    }

    public void RegisterAgent(string peerUrl, string agentId)
    {
        _peerAgentIds[peerUrl] = agentId;
    }

    /// <summary>Build a CancellationToken that times out after <paramref name="timeout"/> while honouring the caller's <paramref name="outer"/> cancellation.</summary>
    private static CancellationTokenSource WithTimeout(CancellationToken outer, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        return cts;
    }

    public async Task<AgentManifest> FetchManifestAsync(string peerUrl, CancellationToken ct = default)
    {
        using var timeoutCts = WithTimeout(ct, FastTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{peerUrl}/.well-known/adp-manifest.json");
        using var res = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Manifest fetch failed: {peerUrl} → {(int)res.StatusCode}");
        }
        var manifest = await res.Content.ReadFromJsonAsync<AgentManifest>(JsonOptions, timeoutCts.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Manifest from {peerUrl} parsed as null");
        // Populate the URL → agentId map as a side-effect, mirroring the TS
        // HttpTransport behavior. The same binding is established by
        // RegisterAgent for paths that bypass manifest discovery.
        _peerAgentIds[peerUrl] = manifest.AgentId;
        return manifest;
    }

    public async Task<CalibrationScore> FetchCalibrationAsync(
        string journalEndpoint, string agentId, string domain, CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = WithTimeout(ct, FastTimeout);
            var url = $"{journalEndpoint}/calibration?agent_id={Uri.EscapeDataString(agentId)}&domain={Uri.EscapeDataString(domain)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                var score = await res.Content.ReadFromJsonAsync<CalibrationScore>(JsonOptions, timeoutCts.Token).ConfigureAwait(false);
                if (score is not null) return score;
            }
        }
        catch
        {
            // fall through to neutral default
        }
        return new CalibrationScore(Value: 0.5, SampleSize: 0, Staleness: TimeSpan.Zero);
    }

    public async Task<PeerProposalResponse> RequestProposalAsync(
        string peerUrl, string deliberationId,
        ActionDescriptor action, ReversibilityTier tier, CancellationToken ct = default)
    {
        var body = new
        {
            deliberationId,
            action = new { kind = action.Kind, target = action.Target, parameters = action.Parameters },
            tier,
        };
        using var timeoutCts = WithTimeout(ct, ProposalTimeout);
        using var req = BuildRequest(HttpMethod.Post, $"{peerUrl}/api/propose", peerUrl, body);
        using var res = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Proposal request failed: {peerUrl} → {(int)res.StatusCode}");
        }
        var envelope = await res.Content.ReadFromJsonAsync<ProposalEnvelope>(JsonOptions, timeoutCts.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Proposal envelope from {peerUrl} parsed as null");
        return new PeerProposalResponse(envelope.Proposal, envelope.Signature);
    }

    public async Task<FalsificationResponse> SendFalsificationAsync(
        string peerUrl, string conditionId, int round, string evidenceAgentId, CancellationToken ct = default)
    {
        var body = new { conditionId, round, evidenceAgentId };
        using var timeoutCts = WithTimeout(ct, ProposalTimeout);
        using var req = BuildRequest(HttpMethod.Post, $"{peerUrl}/api/respond-falsification", peerUrl, body);
        using var res = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            // Per the spec, a non-responding peer's vote stands unchanged. We
            // treat a non-2xx as an implicit reject so the deliberation
            // continues rather than failing.
            return new FalsificationResponse(Action: "reject", Reason: $"peer returned {(int)res.StatusCode}");
        }
        var parsed = await res.Content.ReadFromJsonAsync<FalsificationResponse>(JsonOptions, timeoutCts.Token).ConfigureAwait(false);
        return parsed ?? new FalsificationResponse(Action: "reject", Reason: "unparseable response");
    }

    public async Task PushJournalEntriesAsync(
        string peerUrl, IReadOnlyList<JournalEntry> entries, CancellationToken ct = default)
    {
        using var timeoutCts = WithTimeout(ct, FastTimeout);
        using var req = BuildRequest(HttpMethod.Post, $"{peerUrl}/adj/v0/entries", peerUrl, entries);
        try
        {
            using var res = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            // Best-effort gossip — peers that reject the push (revoked,
            // suspended, validating) don't break the initiator's transcript.
        }
        catch
        {
            // swallow — same best-effort posture as the TS runtime
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, string peerUrl, object body)
    {
        var req = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        var agentId = _peerAgentIds.TryGetValue(peerUrl, out var id) ? id : "*";
        var headers = PeerAuthHeaders.Build(_auth, agentId);
        if (headers.TryGetValue("Authorization", out var auth))
        {
            req.Headers.TryAddWithoutValidation("Authorization", auth);
        }
        return req;
    }

    private sealed record ProposalEnvelope(Proposal Proposal, string? Signature);
}
