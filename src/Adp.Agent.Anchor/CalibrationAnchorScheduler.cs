using Adp.Agent.CalibrationSnapshot;

namespace Adp.Agent.Anchor;

/// <summary>
/// Periodic publisher that commits the agent's current signed calibration
/// snapshot to a Neo3-compatible chain. Runs as an async timer task;
/// configured via <see cref="AgentConfig.CalibrationAnchor"/> and wired
/// via <see cref="AdpAgentHost.AfterStart"/> and
/// <see cref="AdpAgentHost.BeforeStop"/> lifecycle hooks.
/// </summary>
public sealed class CalibrationAnchorScheduler : IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly IRuntimeJournalStore _journal;
    private readonly IBlockchainCalibrationStore _store;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly List<AnchorStatusEntry> _status = new();
    private readonly object _statusLock = new();

    public CalibrationAnchorScheduler(
        AgentConfig config,
        IRuntimeJournalStore journal,
        IBlockchainCalibrationStore store,
        TimeSpan? interval = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        var configuredSeconds = config.CalibrationAnchor?.PublishIntervalSeconds ?? 3600;
        _interval = interval ?? TimeSpan.FromSeconds(Math.Max(30, configuredSeconds));
    }

    /// <summary>Start the periodic publish loop. Idempotent.</summary>
    public void Start()
    {
        if (_loopTask is not null) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Request a shutdown. Returns after the current publish cycle finishes.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { /* expected */ }
        }
        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }

    /// <summary>
    /// Publish every declared decision class once, immediately. Mirrors the
    /// TypeScript <c>publishNow</c> method — used by tests and by adopters
    /// who want an on-demand commit without waiting for the next timer tick.
    /// </summary>
    public async Task PublishNowAsync(CancellationToken ct = default)
    {
        if (_config.Auth?.PrivateKey is null)
        {
            RecordStatus(new AnchorStatusEntry(
                At: DateTimeOffset.UtcNow,
                Domain: "(none)",
                Success: false,
                Detail: "No signing key configured; skipping."));
            return;
        }

        foreach (var domain in _config.DecisionClasses)
        {
            try
            {
                var snapshot = SnapshotBuilder.Build(
                    _config.AgentId, domain, _journal, _config.Auth.PrivateKey);
                var record = new CalibrationRecord(
                    AgentId: _config.AgentId,
                    Domain: domain,
                    Value: snapshot.CalibrationValue,
                    SampleSize: snapshot.SampleSize,
                    Timestamp: DateTimeOffset.Parse(snapshot.ComputedAt).ToUnixTimeMilliseconds(),
                    JournalHash: snapshot.JournalHash);
                var txHash = await _store.PublishCalibrationAsync(record, ct);
                RecordStatus(new AnchorStatusEntry(
                    At: DateTimeOffset.UtcNow,
                    Domain: domain,
                    Success: true,
                    Detail: $"tx={txHash}"));
            }
            catch (Exception ex)
            {
                RecordStatus(new AnchorStatusEntry(
                    At: DateTimeOffset.UtcNow,
                    Domain: domain,
                    Success: false,
                    Detail: ex.Message));
                Console.Error.WriteLine(
                    $"[anchor] Failed to publish calibration for {_config.AgentId}/{domain}: {ex.Message}");
            }
        }
    }

    /// <summary>Snapshot of recent publish attempts for the status endpoint.</summary>
    public IReadOnlyList<AnchorStatusEntry> Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status.TakeLast(32).ToArray();
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Tick immediately, then on interval.
        while (!ct.IsCancellationRequested)
        {
            try { await PublishNowAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[anchor] Loop iteration failed: {ex.Message}");
            }

            try { await Task.Delay(_interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void RecordStatus(AnchorStatusEntry entry)
    {
        lock (_statusLock)
        {
            _status.Add(entry);
            if (_status.Count > 128)
            {
                _status.RemoveRange(0, _status.Count - 128);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>One entry in the scheduler's publish-status history.</summary>
public sealed record AnchorStatusEntry(
    DateTimeOffset At,
    string Domain,
    bool Success,
    string Detail
);
