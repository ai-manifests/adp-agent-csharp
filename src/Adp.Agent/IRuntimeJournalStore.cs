using System.Collections.Immutable;
using Adj.Manifest;

namespace Adp.Agent;

/// <summary>
/// Runtime journal store. Extends the ADJ §7 read-only query interface
/// <see cref="IJournalStore"/> with the write operations and batch-query
/// operations the agent runtime needs.
/// </summary>
/// <remarks>
/// The <c>Adj.Manifest</c> package defines <see cref="IJournalStore"/> as
/// query-only because the spec's §7 query contract is what peers and
/// registries need — they don't need to write into the journal, only read.
/// The runtime, on the other hand, needs to append entries as deliberation
/// progresses, and it needs batch-query methods for the signed calibration
/// snapshot and the anchor scheduler.
/// </remarks>
public interface IRuntimeJournalStore : IJournalStore
{
    /// <summary>Append a single journal entry. Must be durable before returning.</summary>
    void Append(JournalEntry entry);

    /// <summary>
    /// Append a batch of entries atomically. If the backend can't guarantee atomicity,
    /// it must at minimum append in order and roll back partially-written batches on error.
    /// </summary>
    void AppendBatch(IEnumerable<JournalEntry> entries);

    /// <summary>List all deliberation IDs the store knows about, sorted by first-entry timestamp.</summary>
    IReadOnlyList<string> ListDeliberations();

    /// <summary>
    /// List full deliberation records opened on or after <paramref name="since"/>,
    /// capped at <paramref name="limit"/> records. Used by the ADJ §7.1 batch query
    /// endpoint and the calibration snapshot builder.
    /// </summary>
    IReadOnlyList<DeliberationSlice> ListDeliberationsSince(DateTimeOffset since, int limit);

    /// <summary>
    /// Return all entries across all deliberations written on or after
    /// <paramref name="since"/>, in timestamp order. Used by the anchor scheduler
    /// when it needs to replay the entire journal to build snapshots for every
    /// declared decision class.
    /// </summary>
    ImmutableList<JournalEntry> GetAllEntriesSince(DateTimeOffset since);
}

/// <summary>A full deliberation record: the ID plus every entry for it, in timestamp order.</summary>
public sealed record DeliberationSlice(
    string DeliberationId,
    ImmutableList<JournalEntry> Entries
);
