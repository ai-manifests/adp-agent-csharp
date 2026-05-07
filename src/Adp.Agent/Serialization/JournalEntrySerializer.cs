using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Adj.Manifest;

namespace Adp.Agent.Serialization;

/// <summary>
/// Polymorphic serializer for <see cref="JournalEntry"/> records. Handles
/// round-tripping the five concrete entry types through a JSON line. Uses
/// an explicit discriminator line format rather than System.Text.Json's
/// built-in polymorphism so it doesn't require modifying the ref lib
/// types (which live in a separate package).
/// </summary>
public static class JournalEntrySerializer
{
    /// <summary>
    /// JSON options used for entry serialization. camelCase, enums-as-strings,
    /// ignore nulls. Matches the TypeScript runtime's wire format.
    /// </summary>
    public static readonly JsonSerializerOptions EntryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
        },
    };

    /// <summary>
    /// Serialize a journal entry to a single-line JSON string suitable for
    /// storage in a JSONL file. The discriminator is <c>entryType</c> and
    /// is included by virtue of the abstract record field being serialized.
    /// </summary>
    public static string Serialize(JournalEntry entry)
    {
        // System.Text.Json won't serialize derived-type properties off an
        // abstract base reference, so switch on the concrete type and
        // serialize each explicitly. Adding a new entry type is a deliberate
        // compile-break here, which is desirable.
        return entry switch
        {
            DeliberationOpened o => JsonSerializer.Serialize(o, EntryJsonOptions),
            ProposalEmitted p => JsonSerializer.Serialize(p, EntryJsonOptions),
            RoundEvent r => JsonSerializer.Serialize(r, EntryJsonOptions),
            DeliberationClosed c => JsonSerializer.Serialize(c, EntryJsonOptions),
            OutcomeObserved o => JsonSerializer.Serialize(o, EntryJsonOptions),
            _ => throw new InvalidOperationException(
                $"Unknown journal entry type: {entry.GetType().Name}"),
        };
    }

    /// <summary>
    /// Parse a single JSONL line into a journal entry. Reads the
    /// <c>entryType</c> discriminator field and dispatches to the matching
    /// concrete record type.
    /// </summary>
    public static JournalEntry Deserialize(string jsonLine)
    {
        var node = JsonNode.Parse(jsonLine)
            ?? throw new InvalidOperationException("Null JSON node.");
        var obj = node.AsObject();
        if (!obj.TryGetPropertyValue("entryType", out var typeNode) || typeNode is null)
            throw new InvalidOperationException("Journal entry missing 'entryType' discriminator.");

        var entryType = typeNode.GetValue<string>();
        return entryType switch
        {
            "deliberation_opened" => obj.Deserialize<DeliberationOpened>(EntryJsonOptions)!,
            "proposal_emitted" => obj.Deserialize<ProposalEmitted>(EntryJsonOptions)!,
            "round_event" => obj.Deserialize<RoundEvent>(EntryJsonOptions)!,
            "deliberation_closed" => obj.Deserialize<DeliberationClosed>(EntryJsonOptions)!,
            "outcome_observed" => obj.Deserialize<OutcomeObserved>(EntryJsonOptions)!,
            _ => throw new InvalidOperationException(
                $"Unknown journal entryType discriminator: '{entryType}'"),
        };
    }
}
