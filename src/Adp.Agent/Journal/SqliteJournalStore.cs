using System.Collections.Immutable;
using Adj.Manifest;
using Adp.Agent.Serialization;
using Microsoft.Data.Sqlite;

namespace Adp.Agent.Journal;

/// <summary>
/// SQLite-backed journal store. Implements <see cref="IRuntimeJournalStore"/>
/// against a single database file at <c>{journalDir}/journal.db</c>, with
/// one row per entry and indexes on <c>deliberation_id</c> and
/// <c>timestamp</c> for fast queries.
/// </summary>
/// <remarks>
/// <para>
/// Schema:
/// <code>
///   CREATE TABLE entries (
///     entry_id TEXT PRIMARY KEY,
///     deliberation_id TEXT NOT NULL,
///     entry_type TEXT NOT NULL,
///     timestamp TEXT NOT NULL,
///     json_payload TEXT NOT NULL
///   );
///   CREATE INDEX idx_entries_deliberation ON entries (deliberation_id, timestamp);
///   CREATE INDEX idx_entries_timestamp ON entries (timestamp);
/// </code>
/// </para>
/// <para>
/// Concurrency: a single <see cref="SqliteConnection"/> is held open for
/// the store's lifetime with WAL mode enabled. All writes acquire a
/// process-local lock; reads are satisfied by the same connection under
/// the same lock (SQLite's thread-safety model makes this the simplest
/// correct option).
/// </para>
/// </remarks>
public sealed class SqliteJournalStore : IRuntimeJournalStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();
    private bool _disposed;

    public SqliteJournalStore(string journalDir)
    {
        Directory.CreateDirectory(journalDir);
        var dbPath = Path.Combine(journalDir, "journal.db");
        _connection = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS entries (
                entry_id TEXT PRIMARY KEY,
                deliberation_id TEXT NOT NULL,
                entry_type TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                json_payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_entries_deliberation
                ON entries (deliberation_id, timestamp);
            CREATE INDEX IF NOT EXISTS idx_entries_timestamp
                ON entries (timestamp);
        """;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Append(JournalEntry entry)
    {
        lock (_lock)
        {
            InsertOne(entry);
        }
    }

    /// <inheritdoc />
    public void AppendBatch(IEnumerable<JournalEntry> entries)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var entry in entries)
            {
                InsertOne(entry, tx);
            }
            tx.Commit();
        }
    }

    private void InsertOne(JournalEntry entry, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO entries (entry_id, deliberation_id, entry_type, timestamp, json_payload)
            VALUES ($entry_id, $deliberation_id, $entry_type, $timestamp, $json_payload);
        """;
        cmd.Parameters.AddWithValue("$entry_id", entry.EntryId);
        cmd.Parameters.AddWithValue("$deliberation_id", entry.DeliberationId);
        cmd.Parameters.AddWithValue("$entry_type", entry.EntryType.ToString());
        cmd.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("$json_payload", JournalEntrySerializer.Serialize(entry));
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public ImmutableList<JournalEntry> GetDeliberation(string deliberationId)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT json_payload FROM entries
                WHERE deliberation_id = $deliberation_id
                ORDER BY timestamp ASC;
            """;
            cmd.Parameters.AddWithValue("$deliberation_id", deliberationId);

            var builder = ImmutableList.CreateBuilder<JournalEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                builder.Add(JournalEntrySerializer.Deserialize(reader.GetString(0)));
            }
            return builder.ToImmutable();
        }
    }

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
        // Replay the full journal to build scoring pairs. For large journals
        // this should be materialized via a view / trigger that maintains
        // per-agent pair aggregates, but for v0.1.0 a full replay is fine.
        lock (_lock)
        {
            var pairs = new List<ScoringPair>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT json_payload FROM entries ORDER BY deliberation_id, timestamp;";

            var byDlb = new Dictionary<string, (List<ProposalEmitted> Proposals, OutcomeObserved? Outcome)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var entry = JournalEntrySerializer.Deserialize(reader.GetString(0));
                if (!byDlb.TryGetValue(entry.DeliberationId, out var slot))
                {
                    slot = (new List<ProposalEmitted>(), null);
                }
                if (entry is ProposalEmitted p
                    && p.Proposal.AgentId == agentId
                    && p.Proposal.Domain == domain
                    && p.Proposal.CalibrationAtStake)
                {
                    slot.Proposals.Add(p);
                }
                else if (entry is OutcomeObserved o)
                {
                    if (slot.Outcome is null || o.Timestamp > slot.Outcome.Timestamp)
                        slot = (slot.Proposals, o);
                }
                byDlb[entry.DeliberationId] = slot;
            }

            foreach (var (_, slot) in byDlb)
            {
                if (slot.Outcome is null) continue;
                foreach (var p in slot.Proposals)
                {
                    pairs.Add(new ScoringPair(
                        Confidence: p.Proposal.Confidence,
                        Outcome: slot.Outcome.OutcomeValue,
                        Timestamp: slot.Outcome.ObservedAt));
                }
            }

            return pairs.Count == 0
                ? BrierScorer.GetDefault()
                : BrierScorer.Compute(pairs, DateTimeOffset.UtcNow);
        }
    }

    /// <inheritdoc />
    public ConditionQualityMetrics GetConditionTrace(string agentId, TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT json_payload FROM entries
                WHERE entry_type = 'ProposalEmitted' AND timestamp >= $cutoff
                ORDER BY timestamp ASC;
            """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("o"));

            var conditions = new List<ConditionRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var entry = JournalEntrySerializer.Deserialize(reader.GetString(0));
                if (entry is ProposalEmitted p && p.Proposal.AgentId == agentId)
                {
                    conditions.AddRange(p.Proposal.DissentConditions);
                }
            }
            return ConditionQualityScorer.Compute(conditions);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListDeliberations()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT deliberation_id FROM entries
                GROUP BY deliberation_id
                ORDER BY MIN(timestamp) ASC;
            """;
            var ids = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
            return ids;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DeliberationSlice> ListDeliberationsSince(DateTimeOffset since, int limit)
    {
        lock (_lock)
        {
            var ids = new List<string>();
            using (var idCmd = _connection.CreateCommand())
            {
                idCmd.CommandText = """
                    SELECT deliberation_id FROM entries
                    GROUP BY deliberation_id
                    HAVING MIN(timestamp) >= $since
                    ORDER BY MIN(timestamp) ASC
                    LIMIT $limit;
                """;
                idCmd.Parameters.AddWithValue("$since", since.ToString("o"));
                idCmd.Parameters.AddWithValue("$limit", limit);
                using var reader = idCmd.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetString(0));
            }

            var result = new List<DeliberationSlice>(ids.Count);
            foreach (var id in ids)
            {
                result.Add(new DeliberationSlice(id, GetDeliberation(id)));
            }
            return result;
        }
    }

    /// <inheritdoc />
    public ImmutableList<JournalEntry> GetAllEntriesSince(DateTimeOffset since)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT json_payload FROM entries
                WHERE timestamp >= $since
                ORDER BY timestamp ASC;
            """;
            cmd.Parameters.AddWithValue("$since", since.ToString("o"));

            var builder = ImmutableList.CreateBuilder<JournalEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                builder.Add(JournalEntrySerializer.Deserialize(reader.GetString(0)));
            }
            return builder.ToImmutable();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}
