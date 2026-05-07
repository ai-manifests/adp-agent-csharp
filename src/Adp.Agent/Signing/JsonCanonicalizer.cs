using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Adp.Agent.Signing;

/// <summary>
/// Deterministic JSON canonicalization used for proposal signing and
/// cross-language signature verification.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is a simplified RFC 8785 (JCS) variant: objects get their
/// keys sorted alphabetically at every level of nesting, arrays keep their
/// insertion order, primitives serialize as standard compact JSON, and no
/// whitespace is emitted anywhere.
/// </para>
/// <para>
/// The output of this function MUST be bit-identical to the
/// <c>canonicalizeValue</c> function in the TypeScript reference
/// implementation at <c>@ai-manifests/adp-agent@^0.3.0</c>. Cross-language
/// golden-vector tests enforce this parity. If the two ever disagree on
/// a byte, this class is the one that needs to be fixed (the TypeScript
/// implementation is the normative reference for signature interop).
/// </para>
/// <para>
/// <b>Number formatting note.</b> ECMAScript produces lowercase <c>'e'</c>
/// in exponent notation; .NET's default <c>double.ToString()</c> produces
/// uppercase <c>'E'</c>. This class normalizes to lowercase. ADP data in
/// normal operation never reaches exponent notation (all values are
/// bounded probabilities or small integers), but the normalization
/// matters for the golden-vector edge-case tests.
/// </para>
/// </remarks>
public static class JsonCanonicalizer
{
    /// <summary>
    /// Canonicalize a proposal, stripping the top-level <c>signature</c>
    /// field before serialization. This is the entry point callers use
    /// before signing or verifying a proposal.
    /// </summary>
    public static string CanonicalizeProposal(JsonNode proposal)
    {
        if (proposal is not JsonObject obj)
            throw new ArgumentException("Proposal must be a JSON object.", nameof(proposal));

        // Create a shallow copy without the signature field at the top level.
        var filtered = new JsonObject();
        foreach (var kv in obj)
        {
            if (kv.Key == "signature") continue;
            filtered[kv.Key] = kv.Value?.DeepClone();
        }
        return CanonicalizeValue(filtered);
    }

    /// <summary>
    /// Recursive canonical serializer for any JSON value. Exposed for
    /// golden-vector tests; most callers use <see cref="CanonicalizeProposal"/>.
    /// </summary>
    public static string CanonicalizeValue(JsonNode? value)
    {
        var sb = new StringBuilder();
        WriteValue(value, sb);
        return sb.ToString();
    }

    private static void WriteValue(JsonNode? value, StringBuilder sb)
    {
        if (value is null)
        {
            sb.Append("null");
            return;
        }

        switch (value)
        {
            case JsonObject obj:
                WriteObject(obj, sb);
                return;
            case JsonArray arr:
                WriteArray(arr, sb);
                return;
            case JsonValue jv:
                WriteJsonValue(jv, sb);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JsonNode kind: {value.GetType().Name}");
        }
    }

    private static void WriteObject(JsonObject obj, StringBuilder sb)
    {
        sb.Append('{');
        // JS `Array.prototype.sort` on string keys uses UTF-16 code-unit order,
        // which is what StringComparer.Ordinal gives us in .NET.
        var keys = obj.Select(kv => kv.Key)
                      .OrderBy(k => k, StringComparer.Ordinal)
                      .ToArray();

        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0) sb.Append(',');
            WriteString(keys[i], sb);
            sb.Append(':');
            WriteValue(obj[keys[i]], sb);
        }
        sb.Append('}');
    }

    private static void WriteArray(JsonArray arr, StringBuilder sb)
    {
        sb.Append('[');
        for (var i = 0; i < arr.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteValue(arr[i], sb);
        }
        sb.Append(']');
    }

    private static void WriteJsonValue(JsonValue jv, StringBuilder sb)
    {
        var element = jv.GetValue<JsonElement>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                sb.Append("null");
                return;
            case JsonValueKind.True:
                sb.Append("true");
                return;
            case JsonValueKind.False:
                sb.Append("false");
                return;
            case JsonValueKind.String:
                WriteString(element.GetString() ?? string.Empty, sb);
                return;
            case JsonValueKind.Number:
                WriteNumber(element, sb);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JsonValue kind: {element.ValueKind}");
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        // Use System.Text.Json's UnsafeRelaxedJsonEscaping to match the TS
        // JSON.stringify behavior: escape control chars, quote, backslash,
        // but leave non-ASCII characters as-is. The default .NET JSON
        // encoder would escape every non-ASCII char to \u00XX, producing
        // different bytes than TS.
        var encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        var writerOptions = new JsonWriterOptions { Encoder = encoder };
        using var buf = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buf, writerOptions))
        {
            writer.WriteStringValue(s);
        }
        sb.Append(Encoding.UTF8.GetString(buf.ToArray()));
    }

    private static void WriteNumber(JsonElement element, StringBuilder sb)
    {
        // Use the raw JSON text of the number if it's already well-formed
        // (doesn't contain an uppercase 'E' exponent marker). Otherwise
        // re-serialize via ECMAScript-compatible formatting.
        var raw = element.GetRawText();

        // Normalize uppercase exponent to lowercase ('1.5E+10' -> '1.5e+10').
        // ECMAScript `Number.prototype.toString` always emits lowercase 'e'.
        if (raw.IndexOf('E') >= 0)
        {
            raw = raw.Replace('E', 'e');
        }

        // Strip leading '+' from exponents: JS emits '1e+21' but so does
        // .NET in this case, so no transformation needed. Kept as a note
        // in case an edge case surfaces.

        sb.Append(raw);
    }
}
