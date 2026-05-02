# Changelog

All notable changes to `Adp.Agent` and `Adp.Agent.Anchor` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - 2026-05-02

> **Version alignment.** This release jumps the C# library from `0.1.x`
> straight to `0.4.0` to align language ports across the family
> (`@ai-manifests/adp-agent@0.4.0` TS, `Adp.Agent@0.4.0` C#,
> `adp-agent==0.4.0` Python). All three publish the same feature surface
> for the distributed deliberation runtime; the version number is now a
> single feature-level marker across all language ports rather than three
> independent counters. The C# 0.2.0 / 0.3.0 versions were never
> published; consumers move from `0.1.1` directly to `0.4.0`.

### Added — Distributed deliberation runtime (feature parity with `@ai-manifests/adp-agent` 0.4.0)

The `0.1.x` C# port shipped the single-agent proposal path only and returned
`501 Not Implemented` from `POST /api/deliberate` and `POST /api/respond-falsification`.
This release brings full feature parity with the TypeScript reference runtime's
peer-to-peer deliberation state machine.

**New types in `Adp.Agent.Deliberation`:**
- `IPeerTransport` — peer transport contract with `RegisterAgent`,
  `FetchManifestAsync`, `FetchCalibrationAsync`, `RequestProposalAsync`,
  `SendFalsificationAsync`, `PushJournalEntriesAsync`. The `RegisterAgent`
  method is required and is the structural fix for the self-URL → self-agentId
  binding bug described below.
- `HttpPeerTransport` — HTTP implementation that owns an internal URL→agentId
  map populated by both `FetchManifestAsync` (side-effect) and
  `RegisterAgent` (explicit). Outbound auth headers are resolved via
  `PeerAuthHeaders.Build`.
- `PeerAuthHeaders` — outbound auth helper that mirrors the TS runtime's
  `middleware/auth.authHeaders`. Resolves a peer-token by agent id with
  optional wildcard `*` fallback.
- `ContributionTracker` — runtime tracker that records per-agent
  participation, falsification acknowledgements, and dissent-quality
  flags. Builds the per-agent `ParticipantContribution` list the
  `Acb.Manifest.SettlementCalculator` consumes for `default-v0`
  distribution. Static `ComputeLoadBearingAgents` matches the TS
  runtime's counterfactual.
- `PeerDeliberation` — full state machine driver. Discovers peers,
  registers self, requests proposals (peers + self), tallies via
  `Adp.Manifest.DeliberationOrchestrator`, runs belief-update rounds,
  emits `RoundEvent` entries (`FalsificationEvidence`, `Acknowledge`,
  `Reject`, `Amend`, `Revise`), produces a `DeliberationClosed` entry,
  and optionally produces an ACB `SettlementRecorded` via
  `SettlementCalculator.BuildSettlementRecord`. Returns
  `PeerDeliberationResult` with the full transcript.
- `IRuntimeJournalStoreScannable` — optional capability marker for
  journal stores that can yield every entry across deliberations. Used
  for ACB habit-memory lookups when the `PeerDeliberation` runner
  needs prior `DeliberationClosed` + `OutcomeObserved` pairs.

### Fixed — Initiator self-proposal 401 under bearer-token auth

This is the architectural bug `0.2.0` exists to fix in the C# library
(it shipped untouched in `0.1.x` because the distributed deliberation
runtime wasn't ported yet).

A deliberation runner that authenticates outbound peer calls with
per-peer bearer tokens needs a URL → agent-id map so each call resolves
the right token from `AuthConfig.PeerTokens`. The map is populated as
a side-effect of `FetchManifestAsync` for peers, but the initiator
never fetches its own manifest — it already knows what's in it. So
the self URL stayed unbound, outgoing self-proposal calls (and the
self-journal calibration fetch, and the journal gossip push) fell back
to the wildcard `'*'` lookup, which produced no `Authorization` header,
which made the agent's own `AuthMiddleware` reject the call with `401`.
The deliberation aborted with `fetch failed` before any journal entries
were written.

The fix: `PeerDeliberation.RunAsync` now calls
`_transport.RegisterAgent(selfUrl, _self.AgentId)` immediately after
binding the self URL in its internal `peerUrlMap`, so subsequent
self-proposal and self-journal calls resolve `peerTokens[self.agentId]`
correctly. Regression test:
`tests/Adp.Agent.Tests/PeerTransportTests.cs`.

### Changed (note)

- ACB `BudgetCommitted` and `SettlementRecorded` entries are returned
  out-of-band in `PeerDeliberationResult.Settlement` rather than written
  to `IRuntimeJournalStore` (which only accepts `Adj.Manifest.JournalEntry`).
  Callers that want a unified Adj+Acb journal wire the settlement entry
  to a separate ACB store or to a unified persistence layer of their
  choice. The TS runtime appends ACB entries to the same journal because
  its `JournalStore` interface is type-agnostic; the C# port keeps the
  Adj-only interface and surfaces ACB entries explicitly.

### Migration

- Adopters who relied on the `0.1.x` `501 Not Implemented` behavior of
  `POST /api/deliberate` should now wire `PeerDeliberation` into their
  `DeliberationEndpoints` mapping. Example wiring is forthcoming in the
  `adp-federation-prototype` reference deployment.
- No changes are required to `Adp.Agent.AgentConfig`, `IRuntimeJournalStore`,
  or the existing single-agent `RuntimeDeliberation` path — both stay
  backward-compatible.

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
