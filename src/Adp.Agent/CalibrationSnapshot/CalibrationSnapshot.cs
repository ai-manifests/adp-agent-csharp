using System.Text;
using System.Text.Json.Serialization;
using Adj.Manifest;
using Adp.Agent.Signing;

namespace Adp.Agent.CalibrationSnapshot;

/// <summary>
/// Signed calibration snapshot per ADJ §7.4. The always-on trust mechanism:
/// every agent publishes one of these per declared decision class at
/// <c>/.well-known/adp-calibration.json</c>, signed with its Ed25519 key,
/// and peers/registries verify against the public key in the agent's
/// manifest.
/// </summary>
public sealed record CalibrationSnapshotRecord(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("calibrationValue")] double CalibrationValue,
    [property: JsonPropertyName("sampleSize")] int SampleSize,
    [property: JsonPropertyName("journalHash")] string JournalHash,
    [property: JsonPropertyName("computedAt")] string ComputedAt,
    [property: JsonPropertyName("signature")] string Signature
);

/// <summary>
/// The envelope served at <c>/.well-known/adp-calibration.json</c> —
/// a list of per-domain signed snapshots plus metadata.
/// </summary>
public sealed record CalibrationSnapshotEnvelope(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("publishedAt")] string PublishedAt,
    [property: JsonPropertyName("snapshots")] IReadOnlyList<CalibrationSnapshotRecord> Snapshots
);

/// <summary>
/// Build, sign, and verify calibration snapshots.
/// </summary>
public static class SnapshotBuilder
{
    /// <summary>
    /// The canonical message format used for snapshot signing. Per ADJ §7.4
    /// and matching the TypeScript reference implementation, this is a
    /// pipe-delimited string:
    /// <code>
    /// agentId|domain|calibrationValue|sampleSize|journalHash|computedAt
    /// </code>
    /// where <c>calibrationValue</c> is formatted to exactly 4 decimal places
    /// to match Brier score precision and avoid cross-language number-format
    /// drift.
    /// </summary>
    public static string CanonicalSnapshotMessage(string agentId, string domain, double calibrationValue, int sampleSize, string journalHash, string computedAt)
    {
        var value = calibrationValue.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        return $"{agentId}|{domain}|{value}|{sampleSize}|{journalHash}|{computedAt}";
    }

    /// <summary>
    /// Sign an unsigned snapshot for a given agent.
    /// </summary>
    public static string SignSnapshot(
        string agentId,
        string domain,
        double calibrationValue,
        int sampleSize,
        string journalHash,
        string computedAt,
        string privateKeyHex)
    {
        var message = CanonicalSnapshotMessage(agentId, domain, calibrationValue, sampleSize, journalHash, computedAt);
        var bytes = Encoding.UTF8.GetBytes(message);
        return Ed25519Signer.SignCanonicalBytes(bytes, privateKeyHex);
    }

    /// <summary>
    /// Verify a signed snapshot against a public key. Returns false on any
    /// error (malformed key, malformed signature, verification failure).
    /// </summary>
    public static bool VerifySnapshot(string agentId, CalibrationSnapshotRecord snapshot, string publicKeyHex)
    {
        var message = CanonicalSnapshotMessage(
            agentId,
            snapshot.Domain,
            snapshot.CalibrationValue,
            snapshot.SampleSize,
            snapshot.JournalHash,
            snapshot.ComputedAt);
        var bytes = Encoding.UTF8.GetBytes(message);
        return Ed25519Signer.VerifyCanonicalBytes(bytes, snapshot.Signature, publicKeyHex);
    }

    /// <summary>
    /// Build one snapshot for a given (agent, domain) pair by querying the
    /// journal and running Brier scoring over the (confidence, outcome) pairs.
    /// </summary>
    public static CalibrationSnapshotRecord Build(
        string agentId,
        string domain,
        IRuntimeJournalStore journal,
        string privateKeyHex)
    {
        var score = journal.GetCalibration(agentId, domain);
        var journalHash = ComputeJournalHash(journal, domain);
        var computedAt = DateTimeOffset.UtcNow.ToString("o");

        var signature = SignSnapshot(
            agentId, domain, score.Value, score.SampleSize, journalHash, computedAt, privateKeyHex);

        return new CalibrationSnapshotRecord(
            Domain: domain,
            CalibrationValue: score.Value,
            SampleSize: score.SampleSize,
            JournalHash: journalHash,
            ComputedAt: computedAt,
            Signature: signature
        );
    }

    /// <summary>
    /// Build the full envelope — one signed snapshot per declared decision class.
    /// </summary>
    public static CalibrationSnapshotEnvelope BuildEnvelope(
        AgentConfig config,
        IRuntimeJournalStore journal)
    {
        if (config.Auth?.PrivateKey is null)
            throw new InvalidOperationException(
                "Agent has no signing key configured; cannot publish signed calibration snapshot.");

        var snapshots = new List<CalibrationSnapshotRecord>(config.DecisionClasses.Count);
        foreach (var domain in config.DecisionClasses)
        {
            snapshots.Add(Build(config.AgentId, domain, journal, config.Auth.PrivateKey));
        }

        return new CalibrationSnapshotEnvelope(
            AgentId: config.AgentId,
            PublishedAt: DateTimeOffset.UtcNow.ToString("o"),
            Snapshots: snapshots
        );
    }

    /// <summary>
    /// Compute a deterministic hash of the journal state used to produce a
    /// calibration value. A peer replaying the same journal must get the
    /// same value and the same hash; a mismatched hash indicates either
    /// journal tampering or a replay drift bug.
    /// </summary>
    private static string ComputeJournalHash(IRuntimeJournalStore journal, string domain)
    {
        // Hash the set of (proposal_id, confidence, outcome_success) tuples
        // that went into the Brier score for this domain. We rebuild the
        // tuples from the journal entries rather than trust any cached state.
        var relevant = new List<string>();
        foreach (var dlbId in journal.ListDeliberations())
        {
            var slice = journal.GetDeliberation(dlbId);
            if (slice.IsEmpty) continue;

            var proposals = slice.OfType<ProposalEmitted>()
                .Where(p => p.Proposal.Domain == domain && p.Proposal.CalibrationAtStake)
                .ToList();
            var outcome = slice.OfType<OutcomeObserved>()
                .OrderByDescending(o => o.Timestamp)
                .FirstOrDefault();

            foreach (var p in proposals)
            {
                var outcomeVal = outcome?.Success.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                relevant.Add($"{p.Proposal.ProposalId}|{p.Proposal.Confidence:F4}|{outcomeVal}");
            }
        }
        relevant.Sort(StringComparer.Ordinal);
        var joined = string.Join("\n", relevant);
        var sha = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(sha).ToLowerInvariant();
    }
}
