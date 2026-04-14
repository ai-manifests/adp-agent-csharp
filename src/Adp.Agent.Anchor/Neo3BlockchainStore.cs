namespace Adp.Agent.Anchor;

/// <summary>
/// Neo3 JSON-RPC calibration store. Commits <see cref="CalibrationRecord"/>
/// values to a Neo3-compatible chain via a <c>setCalibration</c> smart
/// contract invocation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Status: stub in v0.1.0.</b> The interface is defined and the options
/// record is stable, but the actual RPC wiring (build transaction, sign,
/// broadcast, poll application log for inclusion) is deferred to v0.2.0
/// alongside distributed deliberation. Adopters who want Neo3 anchoring
/// today should use the TypeScript runtime's <c>@ai-manifests/adp-agent-anchor</c>
/// package, which has a working <c>Neo3BlockchainStore</c> built on
/// <c>@cityofzion/neon-js</c>.
/// </para>
/// <para>
/// The four chain targets (<c>neo-express</c>, <c>neo-custom</c>,
/// <c>neo-testnet</c>, <c>neo-mainnet</c>) all use the same client code
/// and smart contract — only the RPC URL, contract hash, and signing
/// wallet differ. See <see cref="Neo3StoreOptions"/>.
/// </para>
/// </remarks>
public sealed class Neo3BlockchainStore : IBlockchainCalibrationStore
{
    private readonly Neo3StoreOptions _options;

    public Neo3BlockchainStore(Neo3StoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<CalibrationRecord?> GetCalibrationAsync(string agentId, string domain, CancellationToken ct = default) =>
        throw new NotImplementedException(
            "Neo3BlockchainStore.GetCalibrationAsync is a v0.2.0 deliverable. " +
            "Use MockBlockchainStore in tests, or the TypeScript adp-agent-anchor runtime for real chain integration today.");

    public Task<string> PublishCalibrationAsync(CalibrationRecord record, CancellationToken ct = default) =>
        throw new NotImplementedException(
            "Neo3BlockchainStore.PublishCalibrationAsync is a v0.2.0 deliverable. " +
            "Use MockBlockchainStore in tests, or the TypeScript adp-agent-anchor runtime for real chain integration today.");
}

/// <summary>Options for configuring a <see cref="Neo3BlockchainStore"/>.</summary>
public sealed record Neo3StoreOptions
{
    /// <summary>JSON-RPC endpoint URL. e.g. <c>http://10.0.0.127:50012</c>.</summary>
    public required string RpcUrl { get; init; }

    /// <summary>Contract hash (hex, with or without 0x prefix).</summary>
    public required string ContractHash { get; init; }

    /// <summary>Hex-encoded WIF or raw private key for signing. Optional for read-only stores.</summary>
    public string? PrivateKey { get; init; }

    /// <summary>Network magic number. Defaults to Neo3 MainNet.</summary>
    public uint NetworkMagic { get; init; } = 0x334F454E;

    /// <summary>Maximum seconds to wait for a published transaction to appear in a block.</summary>
    public int PublishTimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Factory that resolves the right <see cref="IBlockchainCalibrationStore"/>
/// from a runtime <see cref="CalibrationAnchorConfig"/>. Matches the
/// <c>createAnchorStore</c> helper in the TypeScript runtime.
/// </summary>
public static class BlockchainStoreFactory
{
    /// <summary>
    /// Build the store for a given config. Returns null if required fields
    /// are missing and the target is not <c>mock</c>.
    /// </summary>
    public static IBlockchainCalibrationStore? Create(CalibrationAnchorConfig config)
    {
        if (!config.Enabled) return null;

        if (config.Target == "mock")
            return new MockBlockchainStore();

        if (string.IsNullOrEmpty(config.RpcUrl) || string.IsNullOrEmpty(config.ContractHash))
            return null;

        var opts = new Neo3StoreOptions
        {
            RpcUrl = config.RpcUrl,
            ContractHash = config.ContractHash,
            PrivateKey = config.PrivateKey,
            NetworkMagic = (uint?)config.NetworkMagic ?? 0x334F454E,
            PublishTimeoutSeconds = config.PublishTimeoutSeconds,
        };
        return new Neo3BlockchainStore(opts);
    }
}
