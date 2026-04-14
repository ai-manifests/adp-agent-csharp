using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Adp.Manifest;
using NSec.Cryptography;

namespace Adp.Agent.Signing;

/// <summary>
/// Ed25519 proposal signing and verification, wire-compatible with the
/// TypeScript reference implementation in <c>@ai-manifests/adp-agent</c>.
/// </summary>
/// <remarks>
/// <para>
/// Keys are 32-byte Ed25519 raw private/public keys represented as 64-char
/// lowercase hex strings in all public APIs — matching how the TS runtime
/// stores and serializes them.
/// </para>
/// <para>
/// The signing algorithm is: canonicalize the proposal to a string (see
/// <see cref="JsonCanonicalizer.CanonicalizeProposal"/>), UTF-8 encode the
/// string, Ed25519-sign the bytes, return the 64-byte signature as a
/// 128-char lowercase hex string.
/// </para>
/// </remarks>
public static class Ed25519Signer
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;
    private static readonly KeyBlobFormat PrivateKeyFormat = KeyBlobFormat.RawPrivateKey;
    private static readonly KeyBlobFormat PublicKeyFormat = KeyBlobFormat.RawPublicKey;

    /// <summary>
    /// Generate a fresh Ed25519 key pair. Both keys are returned as 64-char
    /// lowercase hex strings.
    /// </summary>
    public static (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        var creationParams = new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        };
        using var key = Key.Create(Algorithm, creationParams);
        var privateBytes = key.Export(PrivateKeyFormat);
        var publicBytes = key.PublicKey.Export(PublicKeyFormat);
        return (
            PublicKey: Convert.ToHexString(publicBytes).ToLowerInvariant(),
            PrivateKey: Convert.ToHexString(privateBytes).ToLowerInvariant()
        );
    }

    /// <summary>
    /// Derive the public key from a private key. Useful when the config
    /// only carries the private key and the manifest needs the public half.
    /// </summary>
    public static string GetPublicKey(string privateKeyHex)
    {
        var privateBytes = Convert.FromHexString(privateKeyHex);
        using var key = Key.Import(Algorithm, privateBytes, PrivateKeyFormat);
        var publicBytes = key.PublicKey.Export(PublicKeyFormat);
        return Convert.ToHexString(publicBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Canonicalize and sign a proposal. Returns a 128-char lowercase hex
    /// signature. The proposal must be JSON-serializable via
    /// <see cref="System.Text.Json.JsonSerializer"/> using the runtime's
    /// <see cref="ProposalSerializerOptions"/>.
    /// </summary>
    public static string SignProposal(Proposal proposal, string privateKeyHex)
    {
        var canonical = CanonicalizeProposalToString(proposal);
        return SignCanonicalBytes(Encoding.UTF8.GetBytes(canonical), privateKeyHex);
    }

    /// <summary>
    /// Verify a proposal's signature against a public key. Returns false
    /// on any error (malformed key, malformed signature, verification
    /// failure) to match the forgiving behavior of the TS reference.
    /// </summary>
    public static bool VerifyProposal(Proposal proposal, string signatureHex, string publicKeyHex)
    {
        try
        {
            var canonical = CanonicalizeProposalToString(proposal);
            var message = Encoding.UTF8.GetBytes(canonical);
            var signature = Convert.FromHexString(signatureHex);
            var publicBytes = Convert.FromHexString(publicKeyHex);
            var publicKey = PublicKey.Import(Algorithm, publicBytes, PublicKeyFormat);
            return Algorithm.Verify(publicKey, message, signature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lower-level helper: sign an already-canonicalized byte sequence.
    /// Exposed for golden-vector tests that verify the canonicalize step
    /// independently of the sign step.
    /// </summary>
    public static string SignCanonicalBytes(byte[] message, string privateKeyHex)
    {
        var privateBytes = Convert.FromHexString(privateKeyHex);
        using var key = Key.Import(Algorithm, privateBytes, PrivateKeyFormat);
        var signature = Algorithm.Sign(key, message);
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    /// <summary>
    /// Lower-level helper: verify a signature against an already-canonicalized
    /// byte sequence. Exposed for golden-vector tests.
    /// </summary>
    public static bool VerifyCanonicalBytes(byte[] message, string signatureHex, string publicKeyHex)
    {
        try
        {
            var signature = Convert.FromHexString(signatureHex);
            var publicBytes = Convert.FromHexString(publicKeyHex);
            var publicKey = PublicKey.Import(Algorithm, publicBytes, PublicKeyFormat);
            return Algorithm.Verify(publicKey, message, signature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Canonicalize a typed proposal. Internally: serialize to a JsonNode,
    /// pass through <see cref="JsonCanonicalizer.CanonicalizeProposal"/>.
    /// </summary>
    public static string CanonicalizeProposalToString(Proposal proposal)
    {
        var node = JsonSerializer.SerializeToNode(proposal, ProposalSerializerOptions)
            ?? throw new InvalidOperationException("Proposal serialized to null.");
        return JsonCanonicalizer.CanonicalizeProposal(node);
    }

    /// <summary>
    /// JSON serializer options used for proposals. camelCase property names,
    /// enums as lowercase strings, no indentation. Matches the TypeScript
    /// wire format.
    /// </summary>
    public static readonly JsonSerializerOptions ProposalSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
        },
    };
}
