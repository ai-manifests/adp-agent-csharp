namespace Adp.Agent.Anchor;

/// <summary>
/// Pluggable interface for committing calibration records to a blockchain
/// anchor and reading them back. Implementations: <see cref="MockBlockchainStore"/>
/// (in-memory, for tests) and <see cref="Neo3BlockchainStore"/> (Neo3 RPC client).
/// Adopters with a non-Neo3 chain implement this interface themselves.
/// </summary>
public interface IBlockchainCalibrationStore
{
    /// <summary>Read a calibration record for an (agent, domain) pair. Null if none exists.</summary>
    Task<CalibrationRecord?> GetCalibrationAsync(string agentId, string domain, CancellationToken ct = default);

    /// <summary>Publish a calibration record to the chain. Returns the transaction hash.</summary>
    Task<string> PublishCalibrationAsync(CalibrationRecord record, CancellationToken ct = default);
}

/// <summary>
/// The shape of a calibration record as it lives on-chain. Scaled to an
/// integer in [0, 10000] for 4-decimal precision before serialization.
/// </summary>
public sealed record CalibrationRecord(
    string AgentId,
    string Domain,
    double Value,
    int SampleSize,
    long Timestamp,
    string JournalHash
);
