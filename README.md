# Adp.Agent

C# / .NET 10 reference implementation of the [Agent Deliberation Protocol](https://adp-manifest.dev). Sister project to the TypeScript [`@ai-manifests/adp-agent`](https://git.marketally.com/ai-manifests/adp-agent) runtime — same behaviour, same wire format, same cross-language golden-vector tests for signing interop.

Two NuGet packages published from this repo:

| Package | Description |
|---|---|
| `Adp.Agent` | Protocol runtime — `AdpAgentHost` class, deliberation state machine, journal backends (JSONL + SQLite), Ed25519 signing, signed calibration snapshots (ADJ §7.4), ACB pricing, MCP tool server, middleware. |
| `Adp.Agent.Anchor` | Optional Neo3 blockchain anchor — periodically commits signed calibration snapshots to a Neo3-compatible chain for third-party tamper evidence. |

The runtime depends on the three reference libraries:
- [`Adj.Manifest`](https://git.marketally.com/ai-manifests/adj-ref-lib-csharp) — ADJ entry types, scoring, journal store interface
- [`Adp.Manifest`](https://git.marketally.com/ai-manifests/adp-ref-lib-csharp) — ADP proposal types, weighting math, orchestrator
- [`Acb.Manifest`](https://git.marketally.com/ai-manifests/acb-ref-lib-csharp) — ACB entry types, pricing, settlement

## Install

```bash
dotnet add package Adp.Agent
```

Packages are published to the Gitea NuGet feed at `https://git.marketally.com/api/packages/ai-manifests/nuget/index.json`. Configure once in your project's `nuget.config`:

```xml
<configuration>
  <packageSources>
    <add key="ai-manifests" value="https://git.marketally.com/api/packages/ai-manifests/nuget/index.json" />
  </packageSources>
</configuration>
```

## Minimal use

```csharp
using Adp.Agent;
using Adp.Manifest;

var config = new AgentConfig
{
    AgentId = "did:adp:my-agent-v1",
    Port = 3000,
    Domain = "my-agent.example.com",
    DecisionClasses = ["code.correctness"],
    Authorities = new() { ["code.correctness"] = 0.7 },
    StakeMagnitude = StakeMagnitude.Medium,
    DefaultVote = Vote.Approve,
    DefaultConfidence = 0.65,
    DissentConditions = [ "if any test marked critical regresses" ],
    JournalDir = "./journal",
};

var agent = new AdpAgentHost(config);
await agent.StartAsync();
```

The `AdpAgentHost` class serves:
- `/healthz`
- `/.well-known/adp-manifest.json`
- `/.well-known/adp-calibration.json` (signed, ADJ §7.4)
- `POST /api/propose`
- `POST /api/respond-falsification`
- `POST /api/deliberate`
- `POST /api/record-outcome`
- `GET /adj/v0/calibration`
- `GET /adj/v0/deliberation/{id}`
- `GET /adj/v0/deliberations` (batch)
- `GET /adj/v0/outcome/{id}`
- `GET /adj/v0/entries`

The adopter implements `IEvaluator` (the function that produces votes) and hands it to the host via DI. See [`adp-agent-template-csharp`](https://git.marketally.com/ai-manifests/adp-agent-template-csharp) for the full starter pattern.

## With optional chain anchoring

```csharp
using Adp.Agent.Anchor;

var host = new AdpAgentHost(config);

if (config.CalibrationAnchor is { Enabled: true } anchor)
{
    var store = BlockchainStoreFactory.Create(anchor);
    var scheduler = new CalibrationAnchorScheduler(config, store, host.Journal);
    host.AfterStart(() => scheduler.Start());
    host.BeforeStop(() => scheduler.Stop());
}

await host.StartAsync();
```

Targets: `mock`, `neo-express`, `neo-custom`, `neo-testnet`, `neo-mainnet`. All use the same `Neo3BlockchainStore` client and the same `CalibrationStore.cs` smart contract — only RPC URL, contract hash, and signing wallet differ between deployments.

## Build

```bash
dotnet restore
dotnet build
dotnet test
```

Requires .NET 10 SDK. `nuget.config` at the repo root registers the Gitea NuGet feed so `dotnet restore` resolves `Adj.Manifest`, `Adp.Manifest`, and `Acb.Manifest` from `https://git.marketally.com/api/packages/ai-manifests/nuget/`. Those three packages must be published to Gitea before this project can build — see each ref lib's own publish workflow for the tag-gated release flow.

## Relationship to the TypeScript runtime

Both runtimes implement the same protocol and must be bit-for-bit compatible on the wire: a proposal signed in TypeScript must verify in C# and vice versa, a calibration snapshot signed in C# must verify in TypeScript, and ACB pricing math must produce identical draws for identical inputs. Cross-language golden-vector tests in `tests/Adp.Agent.Tests/CrossLanguage/` enforce this parity. If the C# runtime ever disagrees with the TypeScript runtime on a wire format or numeric result, the C# runtime is the one that is wrong (or the spec is under-specified — file an issue).

## License

Apache-2.0 — see [`LICENSE`](LICENSE) for the full license text and [`NOTICE`](NOTICE) for attribution.
