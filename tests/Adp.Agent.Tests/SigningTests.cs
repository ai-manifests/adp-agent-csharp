using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Adp.Agent.Signing;
using Adp.Manifest;
using Xunit;

namespace Adp.Agent.Tests;

public class SigningTests
{
    private static Proposal BuildProposal() => new(
        ProposalId: "prp_test_001",
        DeliberationId: "dlb_test_001",
        AgentId: "did:adp:test-agent",
        Timestamp: DateTimeOffset.Parse("2026-04-11T14:32:09.221Z"),
        Action: new ProposalAction(
            Kind: "merge_pull_request",
            Target: "github.com/acme/api#4471",
            Parameters: ImmutableDictionary<string, string>.Empty),
        Vote: Vote.Approve,
        Confidence: 0.86,
        DomainClaim: new DomainClaim(
            Domain: "code.correctness",
            AuthoritySource: "test"),
        ReversibilityTier: ReversibilityTier.PartiallyReversible,
        BlastRadius: new BlastRadius(
            Scope: ImmutableList.Create("service:api"),
            EstimatedUsersAffected: 12000,
            RollbackCostSeconds: 90),
        Justification: new Justification(
            Summary: "All tests pass",
            EvidenceRefs: ImmutableList<string>.Empty),
        Stake: new Stake(
            DeclaredBy: "self",
            Magnitude: StakeMagnitude.High,
            CalibrationAtStake: true),
        DissentConditions: ImmutableList<DissentCondition>.Empty,
        Revisions: ImmutableList<VoteRevision>.Empty);

    [Fact]
    public void GenerateKeyPair_produces_valid_hex_keys()
    {
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        Assert.Equal(64, publicKey.Length);   // 32 bytes hex
        Assert.Equal(64, privateKey.Length);  // 32 bytes hex
        Assert.Matches("^[0-9a-f]{64}$", publicKey);
        Assert.Matches("^[0-9a-f]{64}$", privateKey);
    }

    [Fact]
    public void Canonicalize_is_deterministic_across_key_ordering()
    {
        var proposal = BuildProposal();
        var canonical1 = Ed25519Signer.CanonicalizeProposalToString(proposal);
        var canonical2 = Ed25519Signer.CanonicalizeProposalToString(proposal);
        Assert.Equal(canonical1, canonical2);
    }

    [Fact]
    public void CanonicalizeValue_sorts_object_keys_recursively()
    {
        // Build two objects that are semantically equal but have keys in
        // different insertion order. The canonical form must match byte-for-byte.
        var a = JsonNode.Parse("""{"b":2,"a":{"y":20,"x":10},"c":[1,2]}""")!;
        var b = JsonNode.Parse("""{"a":{"x":10,"y":20},"c":[1,2],"b":2}""")!;
        Assert.Equal(
            JsonCanonicalizer.CanonicalizeValue(a),
            JsonCanonicalizer.CanonicalizeValue(b));
        // And the output should have sorted keys at every level:
        var canonical = JsonCanonicalizer.CanonicalizeValue(a);
        Assert.Equal("""{"a":{"x":10,"y":20},"b":2,"c":[1,2]}""", canonical);
    }

    [Fact]
    public void SignAndVerify_roundtrip_succeeds()
    {
        var proposal = BuildProposal();
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();

        var signature = Ed25519Signer.SignProposal(proposal, privateKey);
        Assert.NotEmpty(signature);
        Assert.Equal(128, signature.Length);  // 64 bytes hex

        var valid = Ed25519Signer.VerifyProposal(proposal, signature, publicKey);
        Assert.True(valid);
    }

    [Fact]
    public void Verify_rejects_tampered_top_level_confidence()
    {
        var proposal = BuildProposal();
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var signature = Ed25519Signer.SignProposal(proposal, privateKey);

        var tampered = proposal with { Confidence = 0.99 };
        Assert.False(Ed25519Signer.VerifyProposal(tampered, signature, publicKey));
    }

    [Fact]
    public void Verify_rejects_tampered_nested_justification()
    {
        // The key regression test. Under the old TS canonicalize (which used
        // JSON.stringify's replacer-array filter), mutating justification.rationale
        // did NOT invalidate the signature — the nested fields were silently
        // dropped from the canonical form. The v0.3.0 fix recursively canonicalizes
        // nested objects, closing that integrity hole. This test enforces the fix.
        var proposal = BuildProposal();
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var signature = Ed25519Signer.SignProposal(proposal, privateKey);

        var tampered = proposal with
        {
            Justification = new Justification(
                Summary: "Tests fail, refusing to merge",
                EvidenceRefs: ImmutableList<string>.Empty),
        };
        Assert.False(Ed25519Signer.VerifyProposal(tampered, signature, publicKey));
    }

    [Fact]
    public void Verify_rejects_wrong_public_key()
    {
        var proposal = BuildProposal();
        var (_, privateKey1) = Ed25519Signer.GenerateKeyPair();
        var (publicKey2, _) = Ed25519Signer.GenerateKeyPair();

        var signature = Ed25519Signer.SignProposal(proposal, privateKey1);
        Assert.False(Ed25519Signer.VerifyProposal(proposal, signature, publicKey2));
    }

    [Fact]
    public void Canonicalize_excludes_signature_field_when_present()
    {
        // Build a JSON node that has a top-level 'signature' field, confirm
        // it's stripped from the canonical form.
        var withSig = JsonNode.Parse("""
            {"agentId":"x","confidence":0.5,"signature":"abc123"}
        """)!;
        var canonical = JsonCanonicalizer.CanonicalizeProposal(withSig);
        Assert.DoesNotContain("signature", canonical);
        Assert.DoesNotContain("abc123", canonical);
        Assert.Equal("""{"agentId":"x","confidence":0.5}""", canonical);
    }
}
