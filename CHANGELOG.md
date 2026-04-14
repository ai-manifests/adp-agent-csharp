# Changelog

All notable changes to `Adp.Agent` and `Adp.Agent.Anchor` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-04-14

### Added

Initial C# / .NET 10 port of the TypeScript `@ai-manifests/adp-agent` runtime.

**`Adp.Agent` (NuGet package):**
- `AdpAgentHost` class — the entry point adopters instantiate
- `AgentConfig` record with runtime configuration
- `IEvaluator` interface + `ShellEvaluator` + `StaticEvaluator` implementations
- `IRuntimeJournalStore` interface + `JsonlJournalStore` + `SqliteJournalStore` backends
- Ed25519 proposal signing via `NSec.Cryptography` with recursive canonical JSON matching the TypeScript `@ai-manifests/adp-agent@^0.3.0` algorithm bit-for-bit (simplified RFC 8785 / JCS variant)
- Signed calibration snapshot builder + verifier per ADJ §7.4
- HTTP endpoints: `/healthz`, `/.well-known/adp-manifest.json`, `/.well-known/adp-calibration.json`, `/api/propose`, `/api/record-outcome`, `/api/budget`, `/adj/v0/calibration`, `/adj/v0/deliberation/{id}`, `/adj/v0/deliberations`, `/adj/v0/outcome/{id}`, `/adj/v0/entries`
- Bearer-token auth middleware with constant-time comparison
- Fixed-window rate limiter middleware
- Journal entry validator

**`Adp.Agent.Anchor` (NuGet package):**
- `IBlockchainCalibrationStore` interface + `CalibrationRecord` record
- `MockBlockchainStore` — in-memory implementation for tests and dev
- `CalibrationAnchorScheduler` — periodic publish loop with status history
- `BlockchainStoreFactory.Create(config)` for wire-up

**Dependencies:**
- Depends on `Adj.Manifest`, `Adp.Manifest`, `Acb.Manifest` from the Gitea NuGet feed (`https://git.marketally.com/api/packages/ai-manifests/nuget/index.json`)
- `Microsoft.Data.Sqlite` 10.0.0 for the SQLite journal backend
- `NSec.Cryptography` for Ed25519

### Feature parity matrix vs TypeScript `@ai-manifests/adp-agent@0.3.0`

| Feature                                     | TS 0.3.0 | C# 0.1.0 | Notes |
|---------------------------------------------|:---:|:---:|---|
| Agent manifest serving                      | ✓ | ✓ | |
| Signed calibration snapshots (ADJ §7.4)     | ✓ | ✓ | |
| Ed25519 proposal signing                    | ✓ | ✓ | Bit-identical canonicalize |
| JSONL journal                               | ✓ | ✓ | |
| SQLite journal                              | ✓ | ✓ | |
| Single-agent proposal emission              | ✓ | ✓ | |
| `POST /api/record-outcome`                  | ✓ | ✓ | |
| ADJ §7.1 query endpoints                    | ✓ | ✓ | |
| Bearer-token auth                           | ✓ | ✓ | |
| Rate limiting                               | ✓ | ✓ | |
| `POST /api/budget` (ACB defaults)           | ✓ | ✓ | Budget not persisted in v0.1.0 |
| Distributed deliberation (belief update)    | ✓ | ✗ | Deferred to v0.2.0 |
| Peer-to-peer HTTP transport                 | ✓ | ✗ | Deferred to v0.2.0 |
| MCP tool server                             | ✓ | ✗ | Deferred to v0.2.0 |
| `Neo3BlockchainStore` actual chain calls    | ✓ | ✗ | Stub; deferred to v0.2.0 |
| `MockBlockchainStore`                       | ✓ | ✓ | |
| Calibration anchor scheduler                | ✓ | ✓ | |
| Shell evaluator                             | ✓ | ✓ | |

### Known limitations
- Distributed deliberation, MCP tool server, and Neo3 RPC client are all scheduled for v0.2.0. Adopters who need those features today should use the TypeScript runtime — both implementations share the same wire format and can coexist in a mixed federation (once the TypeScript runtime is at v0.3.0).
- Cross-language golden-vector parity tests for signing are on the backlog; v0.1.0 ships self-consistent signing and a recursive canonicalizer that's structurally identical to the TS algorithm, but no test fixture yet pins specific signature bytes that must match across languages.
