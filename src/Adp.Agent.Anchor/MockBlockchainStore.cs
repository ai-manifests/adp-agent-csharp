using System.Collections.Concurrent;

namespace Adp.Agent.Anchor;

/// <summary>
/// In-memory <see cref="IBlockchainCalibrationStore"/>. Suitable for unit
/// tests and for the scheduler's <c>target: mock</c> mode. Stores records
/// keyed by (agentId, domain) and returns synthetic "tx hashes" derived
/// from a counter.
/// </summary>
public sealed class MockBlockchainStore : IBlockchainCalibrationStore
{
    private readonly ConcurrentDictionary<(string Agent, string Domain), CalibrationRecord> _records = new();
    private int _txCounter;

    public Task<CalibrationRecord?> GetCalibrationAsync(string agentId, string domain, CancellationToken ct = default)
    {
        _records.TryGetValue((agentId, domain), out var record);
        return Task.FromResult(record);
    }

    public Task<string> PublishCalibrationAsync(CalibrationRecord record, CancellationToken ct = default)
    {
        _records[(record.AgentId, record.Domain)] = record;
        var n = Interlocked.Increment(ref _txCounter);
        var tx = $"0xmock{n:x16}";
        return Task.FromResult(tx);
    }

    /// <summary>Count of records currently in the store (for test assertions).</summary>
    public int Count => _records.Count;

    /// <summary>Count of publishes seen since construction (for test assertions).</summary>
    public int PublishCount => _txCounter;
}
