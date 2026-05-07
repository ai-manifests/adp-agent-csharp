using System.Collections.Concurrent;
using System.Collections.Immutable;
using Adj.Manifest;
using Adp.Agent.Serialization;

namespace Adp.Agent.Journal;

/// <summary>
/// JSONL-backed journal store. One file per deliberation, written append-only
/// under the configured journal directory. Implements
/// <see cref="IRuntimeJournalStore"/>. Zero-dependency default backend.
/// </summary>
/// <remarks>
/// <para>
/// File layout:
/// <code>
///   {journalDir}/{deliberationId}.jsonl
/// </code>
/// Each line is a single serialized <see cref="JournalEntry"/>. Entries are
/// never overwritten; all mutations are appends. On startup, the store
/// scans the directory and builds an in-memory index of deliberations for
/// fast queries.
/// </para>
/// <para>
/// Concurrency: writes are serialized by a per-deliberation lock. Reads
/// are lock-free on the immutable index snapshot; the snapshot is rebuilt
/// on every append.
/// </para>
/// </remarks>
public sealed class JsonlJournalStore : IRuntimeJournalStore
{
    private readonly string _root;
    private readonly object _writeLock = new();

    // Cached in-memory index, rebuilt on every append. Keyed by deliberation ID,
    // values are the entries for that deliberation in timestamp order.
    private ImmutableDictionary<string, ImmutableList<JournalEntry>> _index =
        ImmutableDictionary<string, ImmutableList<JournalEntry>>.Empty;

    public JsonlJournalStore(string journalDir)
    {
        _root = Path.GetFullPath(journalDir);
        Directory.CreateDirectory(_root);
        _index = LoadFromDisk();
    }

    /// <inheritdoc />
    public void Append(JournalEntry entry)
    {
        lock (_writeLock)
        {
            var path = PathFor(entry.DeliberationId);
            File.AppendAllLines(path, new[] { JournalEntrySerializer.Serialize(entry) });

            var existing = _index.TryGetValue(entry.DeliberationId, out var list) ? list : ImmutableList<JournalEntry>.Empty;
            _index = _index.SetItem(entry.DeliberationId, existing.Add(entry));
        }
    }

    /// <inheritdoc />
    public void AppendBatch(IEnumerable<JournalEntry> entries)
    {
        lock (_writeLock)
        {
            var grouped = entries.GroupBy(e => e.DeliberationId);
            var builder = _index.ToBuilder();
            foreach (var group in grouped)
            {
                var path = PathFor(group.Key);
                var lines = group.Select(JournalEntrySerializer.Serialize).ToArray();
                File.AppendAllLines(path, lines);

                var existing = builder.TryGetValue(group.Key, out var list) ? list : ImmutableList<JournalEntry>.Empty;
                builder[group.Key] = existing.AddRange(group);
            }
            _index = builder.ToImmutable();
        }
    }

    /// <inheritdoc />
    public ImmutableList<JournalEntry> GetDeliberation(string deliberationId) =>
        _index.TryGetValue(deliberationId, out var list) ? list : ImmutableList<JournalEntry>.Empty;

    /// <inheritdoc />
    public OutcomeObserved? GetOutcome(string deliberationId)
    {
        var entries = GetDeliberation(deliberationId);
        return entries
            .OfType<OutcomeObserved>()
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public CalibrationScore GetCalibration(string agentId, string domain)
    {
        var pairs = new List<ScoringPair>();
        foreach (var (_, entries) in _index)
        {
            var proposals = entries.OfType<ProposalEmitted>()
                .Where(p => p.Proposal.AgentId == agentId
                         && p.Proposal.Domain == domain
                         && p.Proposal.CalibrationAtStake)
                .ToList();

            var outcome = entries.OfType<OutcomeObserved>()
                .OrderByDescending(o => o.Timestamp)
                .FirstOrDefault();

            if (outcome is null) continue;

            foreach (var p in proposals)
            {
                pairs.Add(new ScoringPair(
                    Confidence: p.Proposal.Confidence,
                    Outcome: outcome.OutcomeValue,
                    Timestamp: outcome.ObservedAt));
            }
        }

        return pairs.Count == 0
            ? BrierScorer.GetDefault()
            : BrierScorer.Compute(pairs, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public ConditionQualityMetrics GetConditionTrace(string agentId, TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var conditions = _index.Values
            .SelectMany(entries => entries)
            .OfType<ProposalEmitted>()
            .Where(p => p.Proposal.AgentId == agentId && p.Timestamp >= cutoff)
            .SelectMany(p => p.Proposal.DissentConditions)
            .ToList();
        return ConditionQualityScorer.Compute(conditions);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListDeliberations() =>
        _index
            .OrderBy(kv => kv.Value.Count > 0 ? kv.Value[0].Timestamp : DateTimeOffset.MinValue)
            .Select(kv => kv.Key)
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<DeliberationSlice> ListDeliberationsSince(DateTimeOffset since, int limit)
    {
        return _index
            .Where(kv => kv.Value.Count > 0 && kv.Value[0].Timestamp >= since)
            .OrderBy(kv => kv.Value[0].Timestamp)
            .Take(limit)
            .Select(kv => new DeliberationSlice(kv.Key, kv.Value))
            .ToList();
    }

    /// <inheritdoc />
    public ImmutableList<JournalEntry> GetAllEntriesSince(DateTimeOffset since)
    {
        return _index.Values
            .SelectMany(entries => entries)
            .Where(e => e.Timestamp >= since)
            .OrderBy(e => e.Timestamp)
            .ToImmutableList();
    }

    private string PathFor(string deliberationId)
    {
        // Defensive: reject path-traversal characters.
        foreach (var c in new[] { '/', '\\', '.', ':' })
        {
            if (deliberationId.Contains(c))
                throw new ArgumentException(
                    $"Illegal character '{c}' in deliberationId '{deliberationId}'.",
                    nameof(deliberationId));
        }
        return Path.Combine(_root, $"{deliberationId}.jsonl");
    }

    private ImmutableDictionary<string, ImmutableList<JournalEntry>> LoadFromDisk()
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableList<JournalEntry>>();
        foreach (var file in Directory.EnumerateFiles(_root, "*.jsonl"))
        {
            var deliberationId = Path.GetFileNameWithoutExtension(file);
            var entries = ImmutableList.CreateBuilder<JournalEntry>();
            foreach (var line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                entries.Add(JournalEntrySerializer.Deserialize(line));
            }
            // Sort by timestamp to tolerate out-of-order writes.
            var sorted = entries.ToImmutable().Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            builder[deliberationId] = sorted;
        }
        return builder.ToImmutable();
    }
}
