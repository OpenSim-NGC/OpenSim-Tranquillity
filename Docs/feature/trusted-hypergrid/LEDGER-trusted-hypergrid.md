# Ledger — Trusted Hypergrid

Running record of what shipped, what is known-broken, and what was deliberately deferred. Append-only; do not rewrite history. Entries stay until closed, and closing an entry means stating how it was resolved.

**Branch:** `feature/trusted-hypergrid` (JohnLegionH/OpenSim-Tranquillity)
**Base:** upstream `develop` @ `a115734ff` (net10.0)
**Authority:** `Docs/feature/trusted-hypergrid/DESIGN-trusted-hypergrid.md` (FROZEN v1)

---

## Slice status

| Slice | Description | Status | Commit |
|---|---|---|---|
| 1 | Trust registry persistence + grid keypair | **DONE** | `82b8cec4ac` |
| 2 | Signature production + verification (`GridSignatureVerifier`, `TrustedGridAuthentication`) | **Built, tests green — uncommitted** | — |
| 3 | Policy engine and region access | Not started | — |
| 4 | Export-bit enforcement | Not started, see D-4 | — |
| 5 | Warnings and admin surface | Not started | — |
| 6 | Audit and provenance | Not started | — |

---

## Slice 1 — 20 Aug 2026

**Delivered.** `ITrustedGridData` (model, `TrustTier`/`TrustState`, default-interface `RecordPresentedKey` implementing the §3 key-change rule once for both backends); `HGUriNormalizer`; `GridKeypair` (Ed25519 via BouncyCastle.Cryptography 2.6.2, pure-managed); `TrustedGrid.migrations` for MySQL and SQLite; `MySQLTrustedGridData`; `SQLiteTrustedGridData`; `Tests/OpenSim.TrustedHypergrid.Tests` (xUnit, in solution).

**Verified.** Solution builds 0 errors on net10.0. `dotnet test Tests/OpenSim.TrustedHypergrid.Tests` → 12 passed, 3 skipped. All §11 done-when criteria for this slice met, including the load-bearing one: a grid presenting a different key for an existing `home_uri` is stored as `state=2` with the original key preserved, not silently overwritten.

**Schema realization.** Column names and types verbatim from §4, with two idiom adaptations: UUID PK realized as `CHAR(36)` (matches every existing OpenSim table, e.g. `auth.UUID`), `public_key` as `VARBINARY(32)` on MySQL / `BLOB` on SQLite. Added `UNIQUE(home_uri)`, an index on `key_fingerprint`, and the `hg_grid_aliases` table — keys and indexes, not column changes.

**Deferred within scope.** `GridKeypair` is path-agnostic; nothing reads a key yet, so no key file is written and no `.gitignore` wiring was added. The gitignored config-include belongs to the slice that operationalizes the key path. When saved, the private key goes to a `[TrustedHypergrid]` INI as hex, mirroring the `DirectDeliverySecret.ini` convention — never in the database.

---

## Open items

### G-1 — MySQL path has zero execution coverage
**Type:** gap. **Opened:** Slice 1. **Blocks:** any MySQL-facing slice.

MySQL tests use a static `[Fact(Skip=…)]`. xUnit v2 (2.9.3, the repo standard) has no runtime-skip equivalent of NUnit's `Assert.Ignore`; `Assert.Skip` is a v3 API and mis-binds to .NET 10's `AsyncEnumerable.Skip`. Static skip was chosen over adding `Xunit.SkippableFact` deliberately — this tree is SQLite standalone by policy, and a static skip cannot accidentally fire against a live database. The test bodies and `TRUSTED_HG_MYSQL_CONN` wiring are preserved; clearing the `Skip` is a one-line change.

Consequence: the MySQL round-trip, the migration, and `RecordPresentedKey` on MySQL are proven by construction only.

**To close:** run against a scratch database in the existing `legiongrid_mysql` container (port 3308) under a distinct DB name with no contact with live data. Must happen before any slice ships that exercises MySQL. Note the standing constraint: the commented MySQL template at `StandaloneCommon.ini:19` must never be uncommented.

### R-1 — `CHAR(36)` UUID PK under MySqlConnector
**Type:** risk. **Opened:** Slice 1. **Severity:** high if unaddressed, trivial once known.

`MySqlConnector` throws on `CHAR(36)` → .NET `Guid` reads of UUID-as-string columns. Fix is `GuidFormat=None` on the connection string. Previously hit and diagnosed on Legion Market. Stacks with G-1: because MySQL is unexecuted, this would first surface against a real grid rather than in test.

**To close:** confirm the connection-string handling when G-1 is executed.

### D-1 — OpenMetaverse packages are a local fiction
**Type:** local deviation. **Opened:** 20 Aug 2026. **Scope:** build environment only, no tree changes.

The three `OpenMetaverse*` 1.0.6 packages resolve from `D:/local-nuget`, built from LibOMV **1.2.13** DLLs extracted from `feature/voice-visibility-matrix:Library/`. The 1.0.6 version label is a fiction to satisfy `Directory.Build.props`.

Cause: NGC publishes these to a private GitHub Packages feed. `Docs/BUILDING.md` documents this as intentional and states the remedy is organization membership; a classic PAT with `read:packages` authenticates against the service index (200) but the package endpoints return 403. nuget.org carries only OpenMetaverse 0.9.3.3318 and has no `OpenMetaverse.Types` at all.

Verified API-compatible: full solution builds 0 errors at `a115734ff`. **The hazard is that future divergence between NGC's 1.0.6 and this 1.2.13 would be silent — behavioural, not a build error.**

Restore requires `--source https://api.nuget.org/v3/index.json --source D:/local-nuget`; a bare `dotnet restore` fails 403. `nuget.config` and `Directory.Build.props` are deliberately untouched, so the tree stays byte-identical to upstream.

**To close:** obtain OpenSim-NGC package read access, then restore normally and delete the local feed. Not urgent, not worth interrupting a maintainer for; raise if it comes up naturally.

### U-1 — Upstream: `OpenSim.Framework.Tests` does not compile
**Type:** upstream defect found. **Opened:** Slice 1. **Not ours to fix.**

`Tests/OpenSim.Framework.Tests/AgentCircuitManagerTests.cs:115-118` contains literal `Assert.Equal(,);`. `UtilTest.cs` has malformed `Assert.That(...)` calls and five `[Fact]` attributes in what was an NUnit project. The project is absent from `Tranquillity.sln`, which is why upstream CI does not catch it. Introduced by the xUnit migration in #197 — the commit this branch is based on.

Slice 1 tests were consequently placed in a new `Tests/OpenSim.TrustedHypergrid.Tests` project rather than the broken one. `OpenSim.Framework.Tests` is byte-identical to HEAD; we touched nothing in it.

**To close:** report to NGC. Small, verifiable in one command, and independent of the Trusted Hypergrid work.

---

## Decisions made outside the ADRs

- **BouncyCastle.Cryptography 2.6.2 added to `OpenSim.Framework.csproj`.** Unavoidable — `GridKeypair` lives there and .NET has no built-in Ed25519 even on net10. Pure-managed, so win-x64, linux-x64 self-contained and linux-arm64 all work from one build. Should be recorded as ADR-009 if it survives review.
- **Static skip over `Xunit.SkippableFact`** for the MySQL tests. Avoids a dependency and cannot accidentally execute against a live database. See G-1.

---

## Slice 2 — 20 Aug 2026

**Delivered.** Signature production and verification, no enforcement (Design Brief §5, §6; ADR-004, ADR-005). All in `Source/OpenSim.Framework/TrustedHypergrid/` except the `IServiceAuth` and its registration:

- `HGSignatureEnvelope` — the single shared canonicaliser (payload builder + parameter digest) used by BOTH signer and verifier. LF-joined method / fingerprint / ISO-8601-seconds timestamp / base64 nonce / SHA-256 param digest. Excludes the `tg_*` / `X-TG-*` fields from the digest so it is stable before and after material is attached.
- `GridSignatureSigner` — Ed25519 signing (BouncyCastle) → `SignatureMaterial`.
- `SignatureMaterial` — the four wire values plus transport adapters: XML-RPC `tg_key`/`tg_ts`/`tg_nonce`/`tg_sig` on the param Hashtable, HTTP `X-TG-Key/Timestamp/Nonce/Signature` headers.
- `GridSignatureVerifier` — classifies to `GridTrustContext`; NEVER throws, NEVER rejects; every failure path (no material, malformed base64, wrong key length, unparseable/stale timestamp, bad signature, replayed nonce, unexpected exception) degrades to Open and logs (ADR-005).
- `NonceCache` — 600 s replay window, lazily pruned.
- `GridTrustContext` (+ `VerificationOutcome`, ambient `Current` via `AsyncLocal`), `IGridTrustLookup` (the Framework↔registry seam).
- `TrustedGridAuthentication : IServiceAuth` in `ServiceAuth/`, registered in the `ServiceAuth` factory switch. Returns false ONLY for Blocked tier; unsigned/malformed/unknown/expired/replayed → true. No grid can be Blocked in this slice, so it is inert.

**Verified.** Solution builds 0 errors on net10.0. `dotnet test Tests/OpenSim.TrustedHypergrid.Tests --no-restore` → 20 passed, 3 skipped (the Slice-1 MySQL trio, see G-1). New `SignatureVerificationTests` cover every done-when case: sign→verify→Trusted-eligible; no material→Open (no throw, no rejection); tampered signature and tampered parameters→Open (no throw); +301 s→Open while +300 s still verifies; replayed nonce→Open; `TrustedGridAuthentication.Authenticate` returns true for every non-Blocked case and false only for Blocked; canonical payload byte-identical across signer and verifier (proven by validating the real signature against the independently reconstructed verifier payload).

**Decision — `tg_key` carries the base64 public key, not the fingerprint.** §5 names `tg_key` but does not state its content. ADR-003 is decisive: "the public key, not the `HomeURI` string, is the grid's identity … first contact records the presented key." The receiver derives the fingerprint (SHA-256 hex) from `tg_key`; the payload embeds that fingerprint. This makes verification and first-contact recording self-sufficient. Low-risk — internal wire format on a non-enforcing slice; revise here if review disagrees. Candidate for an ADR if it survives review.

**Only one existing HG-path file touched: `Source/OpenSim.Framework/ServiceAuth/ServiceAuth.cs`** — one `case "TrustedGridAuthentication"` added to the factory switch (mandated by the load-bearing constraint and ADR-004). Default `AuthType` is unchanged, so this is inert unless a deployment explicitly opts in. Byte-identical behaviour for every caller.

### D-2 — call-site sign/verify wiring is deferred
**Type:** deferral. **Opened:** Slice 2. **Blocks:** nothing in Slice 2's done-when; required before any tier actually rides real traffic.

The signer is not yet invoked on outbound Gatekeeper/UserAgent/HGFriends (XML-RPC) or HGAsset/HGInventory (HTTP) calls, and the verifier is not yet invoked inbound to populate `GridTrustContext.Current`. Why: (1) the signer needs the local private key, which Slice 1 deliberately left unwired, and the config surface to load it is an explicit ADR open decision ("Config surface shape … undecided") — wiring it now would invent frozen-adjacent config; (2) inbound classification needs a concrete `IGridTrustLookup` over `ITrustedGridData` plus DI at each service, which belongs with that config work; (3) the done-when is component/test-scoped, and touching many HG services without integration coverage is the exact interop risk ADR-005 guards. The machinery is transport-ready (the `SignatureMaterial` adapters and `AddAuthorization` are the seams). `AddAuthorization` currently no-ops (no signer configured); when a signer is present it binds only a freshness envelope (key+ts+nonce) because `IServiceAuth.AddAuthorization(NameValueCollection)` cannot see the request body — full parameter binding rides the XML-RPC transport.

**To close:** wire signer/verifier at the HG call sites in the slice that decides the config surface and operationalises the keypair, with a live stock-grid interop pass (ADR-005 acceptance).
