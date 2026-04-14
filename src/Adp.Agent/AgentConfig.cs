using System.Collections.Immutable;
using Adp.Manifest;

namespace Adp.Agent;

/// <summary>
/// Runtime configuration for an ADP agent host. Typically deserialized from
/// an <c>agent.config.json</c> file at startup. Every field except
/// <see cref="Auth"/>, <see cref="Peers"/>, <see cref="Acb"/>, and
/// <see cref="CalibrationAnchor"/> is required.
/// </summary>
public sealed record AgentConfig
{
    /// <summary>The DID of this agent, e.g. <c>did:adp:test-runner-v1</c>.</summary>
    public required string AgentId { get; init; }

    /// <summary>HTTP port the agent listens on.</summary>
    public required int Port { get; init; }

    /// <summary>Public domain the agent serves at — used in manifest and DID resolution.</summary>
    public required string Domain { get; init; }

    /// <summary>Decision classes this agent has authority over, e.g. <c>code.correctness</c>.</summary>
    public required ImmutableList<string> DecisionClasses { get; init; }

    /// <summary>Per-class authority weights in [0, 1], as declared in the manifest.</summary>
    public required ImmutableDictionary<string, double> Authorities { get; init; }

    /// <summary>Stake magnitude this agent commits by default on new proposals.</summary>
    public required StakeMagnitude StakeMagnitude { get; init; }

    /// <summary>Default vote used by the fallback evaluator if none is configured.</summary>
    public required Vote DefaultVote { get; init; }

    /// <summary>Default confidence used by the fallback evaluator if none is configured.</summary>
    public required double DefaultConfidence { get; init; }

    /// <summary>Default dissent conditions declared on each proposal this agent emits.</summary>
    public required ImmutableList<string> DissentConditions { get; init; }

    /// <summary>
    /// Structured falsification responses — maps a condition kind to a response recipe
    /// the runtime uses when it's asked to respond to a falsification pointing at one
    /// of this agent's dissent conditions.
    /// </summary>
    public ImmutableDictionary<string, string> FalsificationResponses { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>Directory where the JSONL journal lives (or the SQLite file's parent dir).</summary>
    public required string JournalDir { get; init; }

    /// <summary>Which journal backend to use. Defaults to <see cref="JournalBackend.Jsonl"/>.</summary>
    public JournalBackend JournalBackend { get; init; } = JournalBackend.Jsonl;

    /// <summary>Optional list of peer agents this agent can initiate deliberations with.</summary>
    public ImmutableList<PeerConfig> Peers { get; init; } = ImmutableList<PeerConfig>.Empty;

    /// <summary>Bearer token + signing key configuration for proposal signing and inbound auth.</summary>
    public AuthConfig? Auth { get; init; }

    /// <summary>ACB defaults — pricing, settlement, budget authority.</summary>
    public AcbDefaultsConfig? Acb { get; init; }

    /// <summary>Optional Neo3 chain anchor configuration for signed calibration snapshots.</summary>
    public CalibrationAnchorConfig? CalibrationAnchor { get; init; }

    /// <summary>Evaluator configuration — shell command, static vote, or custom plugin.</summary>
    public EvaluatorConfig? Evaluator { get; init; }

    /// <summary>Whether this agent can initiate deliberations via <c>POST /api/deliberate</c>.</summary>
    public bool Initiator { get; init; } = false;
}

/// <summary>Journal backend selector.</summary>
public enum JournalBackend
{
    /// <summary>Append-only JSONL files, one per deliberation. Zero-dependency default.</summary>
    Jsonl = 0,

    /// <summary>SQLite database with indexed entry access. Requires Microsoft.Data.Sqlite.</summary>
    Sqlite = 1,
}

/// <summary>Configuration for a peer agent this agent knows about.</summary>
public sealed record PeerConfig(
    string AgentId,
    string Url,
    PeerTransport Transport = PeerTransport.Http
);

/// <summary>How to talk to a peer.</summary>
public enum PeerTransport
{
    /// <summary>Direct HTTP over the peer's <c>/api/*</c> routes.</summary>
    Http = 0,

    /// <summary>MCP tool calls over SSE.</summary>
    Mcp = 1,
}

/// <summary>Runtime auth config — bearer tokens and Ed25519 signing keys.</summary>
public sealed record AuthConfig
{
    /// <summary>Required bearer token for inbound admin operations (propose, deliberate, record-outcome).</summary>
    public required string BearerToken { get; init; }

    /// <summary>Optional per-peer bearer tokens, keyed by peer agent ID. Used for signed peer-to-peer calls.</summary>
    public ImmutableDictionary<string, string> PeerTokens { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>Ed25519 private key (hex) used to sign this agent's proposals and calibration snapshots.</summary>
    public string? PrivateKey { get; init; }

    /// <summary>Ed25519 public key (hex) served in the manifest. Derived from <see cref="PrivateKey"/> if omitted.</summary>
    public string? PublicKey { get; init; }
}

/// <summary>
/// ACB (Agent Cognitive Budget) defaults the agent applies when no caller-supplied
/// budget is provided on a deliberation.
/// </summary>
public sealed record AcbDefaultsConfig
{
    public required string BudgetAuthority { get; init; }
    public required Acb.Manifest.Denomination Denomination { get; init; }
    public required double DefaultAmountTotal { get; init; }
    public required Acb.Manifest.PricingProfile Pricing { get; init; }
    public required Acb.Manifest.SettlementProfileConfig Settlement { get; init; }
    public Acb.Manifest.BudgetConstraints? Constraints { get; init; }
}

/// <summary>
/// Optional Neo3 calibration anchor configuration. When <see cref="Enabled"/> is
/// true and the runtime has <c>Adp.Agent.Anchor</c> referenced, a scheduler
/// periodically commits signed snapshots to the configured chain.
/// </summary>
public sealed record CalibrationAnchorConfig
{
    public bool Enabled { get; init; } = false;

    /// <summary><c>mock</c>, <c>neo-express</c>, <c>neo-custom</c>, <c>neo-testnet</c>, or <c>neo-mainnet</c>.</summary>
    public string Target { get; init; } = "mock";

    public string? RpcUrl { get; init; }
    public string? ContractHash { get; init; }
    public string? PrivateKey { get; init; }
    public int? NetworkMagic { get; init; }
    public int PublishIntervalSeconds { get; init; } = 3600;
    public int PublishTimeoutSeconds { get; init; } = 30;
}

/// <summary>Strategy-pattern evaluator config. Adopters usually implement <c>IEvaluator</c> directly.</summary>
public sealed record EvaluatorConfig
{
    /// <summary><c>shell</c>, <c>static</c>, or a custom kind your code knows how to resolve.</summary>
    public required string Kind { get; init; }

    /// <summary>Shell command (for <c>kind: shell</c>).</summary>
    public string? Command { get; init; }

    public int TimeoutMs { get; init; } = 60_000;

    /// <summary><c>exit-code</c> (0=approve, non-zero=reject) or <c>json</c> (parse stdout as JSON vote).</summary>
    public string ParseOutput { get; init; } = "exit-code";
}
