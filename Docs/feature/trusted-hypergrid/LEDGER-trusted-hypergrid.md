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
| 2 | Signature production + verification (`GridSignatureVerifier`, `TrustedGridAuthentication`) | **DONE** | `0500ae849b` |
| 2b | Operationalise keypair + wire gatekeeper XML-RPC pair | **DONE** | `0500ae849b` |
| 2c | Logging transport log4net → ILogger | **DONE** | `256584cc38` |
| 3 | Trust registry wiring + operator surface (`hgtrust`), no enforcement | **DONE**; signed `tg_uri` follow-up (D-5/R-2) built, tests green (60/60 incl. MySQL) — uncommitted | `11161ab8f0` |
| 3b | Enforcement, minimum viable: Blocked refuses (gatekeeper XML-RPC), nothing else changes | **Built, tests green (75/75 incl. MySQL) — uncommitted** | — |
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

---

## Slice 2b — 20 Aug 2026

**Delivered.** The keypair is operationalised and the first transport pair — the gatekeeper XML-RPC path — is wired. Still no enforcement.

- `TrustedHypergridRuntime` (new) — built from `[TrustedHypergrid]` in `Robust.HG.ini`. `Enabled=false` (default) loads nothing (no key file read or written), so behaviour is byte-identical to `a115734ff`. `Enabled=true` loads the keypair from `PrivateKeyFile` or generates+saves it on first run, logging the fingerprint at INFO (distinct "generated" vs "loaded" lines).
- `TrustedHypergridHooks` (new) — process-wide ambient entry points: `EnsureInitialized(config)` (idempotent), `SignOutbound(Hashtable, method)`, `ClassifyInbound(Hashtable, method)` (verify + log tier at DEBUG). All safe no-ops until enabled.
- Gatekeeper outbound signing and inbound classification wired (see diff surface below).

**ADR-010 (config surface), as decided in the slice brief.** `[TrustedHypergrid]` in `Robust.HG.ini`: `Enabled` (bool, default false) and `PrivateKeyFile` (default `TrustedHypergridSecret.ini`). Private key stored as hex under `[TrustedHypergrid] PrivateKey`, outside version control, following the `DirectDeliverySecret.ini` pattern; filename added to `.gitignore`. Generated on first run; fingerprint logged on generate and load; never in the database, never committed. Template documented in `Robust.HG.ini.example`. Should be promoted to a real ADR-010 record.

**Verified.** Solution builds 0 errors on net10.0. `dotnet test Tests/OpenSim.TrustedHypergrid.Tests --no-restore` → 25 passed, 3 skipped. New `TrustedHypergridWiringTests` cover all five done-when cases: Enabled=false → nothing signed/verified, no key file, Hashtable byte-identical; Enabled=true first run → keypair generated; second run → same fingerprint loaded, not regenerated; round-trip sign→classify from the same Hashtable → Verified + Trusted-eligible; unsigned inbound → Open, no throw.

**Diff surface — every existing file touched (Slice 2b):**
- `Source/OpenSim.Services.Connectors/Hypergrid/GatekeeperServiceConnector.cs` — +1 using (L32); `SignOutbound(hash,"link_region")` before the `link_region` request (L88–91); `SignOutbound(hash,"get_region")` before the `get_region` request (L232–234). HttpClient untouched.
- `Source/OpenSim.Server.Handlers/Hypergrid/HypergridHandlers.cs` — +1 using (L31); `ClassifyInbound(requestData,"link_region")` in `LinkRegionRequest` (L61–63); `ClassifyInbound(requestData,"get_region")` in `GetRegion` (L91–92).
- `Source/OpenSim.Server.Handlers/Hypergrid/GatekeeperServerConnector.cs` — +1 using (L29); `EnsureInitialized(config)` in the inbound connector ctor (L67–71).
- `.gitignore` — `TrustedHypergridSecret.ini` entries (L270–274).
- `Source/OpenSim.Server.GridServer/AppData/Robust.HG.ini.example` — `[TrustedHypergrid]` template section (L234–251).
New files: `TrustedHypergridRuntime.cs`, `TrustedHypergridHooks.cs` (both `Source/OpenSim.Framework/TrustedHypergrid/`), `Tests/OpenSim.TrustedHypergrid.Tests/TrustedHypergridWiringTests.cs`.

**D-2 partially closed.** The gatekeeper XML-RPC path (`link_region`, `get_region`) now signs outbound and classifies inbound. Still open under D-2's spirit: the UserAgent XML-RPC calls and the HTTP transport (HGAsset/HGInventory), explicitly deferred by this slice's NOT.

### D-3 — runtime init point is the inbound gatekeeper connector only
**Type:** deviation from the 4-file scope + a topology limit. **Opened:** Slice 2b.

The four scoped call-site files have no `IConfigSource`, so `TrustedHypergridHooks.EnsureInitialized(config)` had to be placed in the one config-bearing owner of the gatekeeper path: `GatekeeperServiceInConnector` (a fifth file). Consequence: the runtime initialises only in a process that stands up the inbound gatekeeper service (Robust HG). A pure region simulator that makes *outbound* gatekeeper calls does not init the runtime, so its outbound calls stay unsigned → the far grid classifies it Open (ADR-005 safe, but no attribution from region-origin calls yet). **To close:** add a region-side init in the slice that wires region-origin HG signing.

### D-4 — inbound tier resolution is not registry-backed yet
**Type:** deferral. **Opened:** Slice 2b.

The production verifier is built with a null `IGridTrustLookup`, so a cryptographically verified caller is logged `outcome=Verified tier=Open` regardless of its registry entry — the concrete adapter over `ITrustedGridData` is deferred (it belongs with enforcement, and stacks on G-1/R-1 for MySQL). The round-trip test injects a lookup to prove Trusted-eligibility is reachable. **To close:** wire the `ITrustedGridData` adapter when tier actually influences a decision.

### Manual acceptance — live stock-grid interop (ADR-005), for the operator to run
Not executed here. This is the interop proof ADR-005 has asserted since day one and which has never been run.

**Setup.** On the Tranquillity grid's Robust, set `[TrustedHypergrid] Enabled = true` in `Robust.HG.ini`; leave `PrivateKeyFile` default; restart Robust.
- Expect at startup, INFO: `[TRUSTED HG]: generated new grid identity at TrustedHypergridSecret.ini, fingerprint <64-hex>`. Confirm `bin/TrustedHypergridSecret.ini` exists and that `git status` shows it ignored.
- Restart again; expect INFO: `[TRUSTED HG]: loaded grid identity from TrustedHypergridSecret.ini, fingerprint <same hex>` (NOT "generated").

**Exercise.** From the Tranquillity grid, hyperlink to and teleport to/from a live stock grid (e.g. OSGrid, `http://login.osgrid.org:80`). Separately, have the stock grid resolve/link INTO a Tranquillity region (drives inbound `link_region`/`get_region`).

**Pass.** Every hyperlink and teleport that worked with `Enabled=false` still works. On the Tranquillity gatekeeper, DEBUG shows `[TRUSTED HG]: inbound get_region classified tier=1 outcome=Unverified` for the unsigned stock caller, and the call still returns a valid region. No 500s, no faults, no teleport failures.

**Fail (revert `Enabled=false` and report).** Any previously-working hyperlink/teleport to or from the stock grid now faults, times out, or is refused (HTTP 403/Unauthorized, or `result=false` where it was true); any `[TRUSTED HG]`-tagged exception on the request path (the verifier must never throw); or an unsigned stock caller classified as anything other than tier=1 Open.

---

## Slice 2c — 29 Aug 2026 — logging transport (log4net → ILogger)

**Why.** Live evidence, 29 Aug: `[TrustedHypergrid] Enabled = true` on the production Robust (key generated 14:44, signed HG round trip completed 14:46:49) produced **zero** `[TRUSTED HG]` output — no fingerprint at INFO on generation. Cause established, not assumed: the three TrustedHypergrid files acquired loggers through log4net (`LogManager.GetLogger`), but upstream #198 moved Robust to `Microsoft.Extensions.Logging`, and on this branch log4net is never configured — `Log4NetBootstrapper.Configure` (`Source/OpenSim.Server.Base/Hosting/Log4NetBootstrapper.cs:29`) is the only `XmlConfigurator.Configure` call in the tree and has no callers, so the log4net repository has no appenders and drops every event. `OpenSim.Framework.csproj` still references the log4net package, which is why the calls compiled. The fingerprint line was therefore **not logged anywhere** (as opposed to logged somewhere invisible); the `OpenSim.Server.GridServer.dll.config` on disk is inert.

**Delivered.** Mechanical transport change, no behaviour change, no new statements. `using log4net` → `using Microsoft.Extensions.Logging`; `ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType)` → `ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType)` — the exact acquisition upstream uses in `OpenSim.Services.HypergridService/*` and in its own static classes (`NetworkUtil`, `SLUtil`, `PermissionsUtil`). `TrustedHypergridHooks` is a static class; the same static-field pattern applies unchanged, and `DeferredLogger` (`Source/OpenSim.Framework/DeferredLogger.cs:40-46`) rebinds to `LoggerProvider.LoggerFactory` on every call, so a static logger created before host logging is wired is safe. `TrustedGridAuthentication.cs` had no logging and is untouched. `LoggerProvider` lives in `OpenSim.Framework` and resolves from the child namespace without a using.

**Every call converted (file · level · template · placeholders/args):**
- `GridSignatureVerifier.cs:77` · Warning · `[TRUSTED HG]: signature material had malformed base64; classifying Open.` · 0/0
- `GridSignatureVerifier.cs:83` · Warning · `[TRUSTED HG]: presented key was {0} bytes, not {1}; classifying Open.` · 2/2
- `GridSignatureVerifier.cs:91` · Warning · `[TRUSTED HG]: unparseable timestamp; classifying Open.` · 0/0
- `GridSignatureVerifier.cs:98` · Information · `[TRUSTED HG]: timestamp skew {0}s outside ±{1}s; classifying Open.` · 2/2
- `GridSignatureVerifier.cs:114` · Information · `[TRUSTED HG]: signature did not verify; classifying Open.` · 0/0
- `GridSignatureVerifier.cs:122` · Information · `[TRUSTED HG]: nonce replay within window; classifying Open.` · 0/0
- `GridSignatureVerifier.cs:145` · Warning (+exception) · `[TRUSTED HG]: unexpected error during verification; classifying Open.` · 0/0 — log4net `Warn(msg, e)` → `LogWarning(e, msg)`
- `TrustedHypergridHooks.cs:70` · Warning (+exception) · `[TRUSTED HG]: failed to initialise runtime; feature disabled for this process.` · 0/0 — same exception-first form
- `TrustedHypergridHooks.cs:106` · Debug · `[TRUSTED HG]: inbound {0} classified tier={1} outcome={2} grid={3}` · 4/4
- `TrustedHypergridRuntime.cs:96` · Information · `[TRUSTED HG]: loaded grid identity from {0}, fingerprint {1}` · 2/2
- `TrustedHypergridRuntime.cs:103` · Information · `[TRUSTED HG]: generated new grid identity at {0}, fingerprint {1}` · 2/2

Placeholder counts verified against `OpenSimConsoleLoggerProvider.cs:142` (formatter outside the try/catch — a mismatch would throw into the verifier/runtime): every template's `{n}` count equals its argument count; no literal braces. One rendering nuance, not a behaviour change: MEL renders a null argument as `(null)` where `string.Format` rendered empty — only `grid={3}` can be null (unsigned callers).

**Verified.** `dotnet restore` bare (no `--source`; nuget.org via `nuget.config`) and `dotnet build -c Release` → 0 errors on net10.0, 0 warnings in TrustedHypergrid files. `grep -rn "log4net\|ILog \|LogManager"` over `Source/OpenSim.Framework/TrustedHypergrid/` and `ServiceAuth/TrustedGridAuthentication.cs` → empty. `dotnet test Tests/OpenSim.TrustedHypergrid.Tests --no-restore` → 25 passed, 3 skipped (unchanged). Diff surface: exactly the three files above; 3 usings, 3 logger fields, 11 call sites.

**Expected production output once deployed** (Robust console/log, `[TrustedHypergrid] Enabled = true`): at startup INFO `[TRUSTED HG]: loaded grid identity from TrustedHypergridSecret.ini, fingerprint <64-hex>` (the key already generated 29 Aug 14:44 — "generated" only appears on a first run); on each inbound `link_region`/`get_region`, DEBUG `[TRUSTED HG]: inbound <method> classified tier=<n> outcome=<Verified|Unverified> grid=<id|(null)>`. Unchanged and still expected: no output at all when `Enabled=false`.

---

## Slice 3 — 29 Aug 2026 — trust registry wiring and operator surface

**Delivered.** The Slice 1 data layer now has a consumer. `TrustedGridServiceBase` (new, `Source/OpenSim.Services.HypergridService/`) loads the `ITrustedGridData` plugin exactly as `GridServiceBase`/`UserAgentServiceBase` load theirs — `[DatabaseService]` supplies `StorageProvider`/`ConnectionString`, `[TrustedHypergrid]` may override; `LoadPlugin<ITrustedGridData>(dll, [connString])`. No `Realm` is read: the Slice 1 backends fix their table names and take a 1-arg ctor (recorded here as the one convention divergence). `TrustedGridRegistryService` (new) implements `IGridTrustLookup` (fingerprint → grid id + tier) and the new `IGridTrustRecorder` (`Source/OpenSim.Framework/TrustedHypergrid/IGridTrustRecorder.cs`; TOFU via Slice 1's `RecordPresentedKey`, never reimplemented), and registers the console commands `hgtrust list | show <uri|fingerprint> | approve <uri> [by] | block <uri> | forget <uri> | key show` (`preview-export` deferred to the export slice). `GatekeeperServiceInConnector` constructs the registry when `Enabled=true` (`ServerUtils.LoadPlugin<IGridTrustLookup>("OpenSim.Services.HypergridService.dll:TrustedGridRegistryService")`) and passes it to `TrustedHypergridHooks.EnsureInitialized(config, lookup)` → `TrustedHypergridRuntime.FromConfig(config, lookup)` → `GridSignatureVerifier(lookup)`. A verified caller now resolves to its registry id and tier in `GridTrustContext`; the Slice 2c DEBUG line logs it. Approval is the only path to Trusted; repeated contact never promotes.

**Interface extension.** `ITrustedGridData` gained `GetAll()`, `GetAliases(UUID)`, `Delete(UUID)` (row + aliases) for the operator surface; both backends implement them.

**Defect found and fixed (data layer).** SQLite shifted UTC timestamps to local time on write (System.Data.SQLite converts a `DateTimeKind.Utc` value; 20:00Z came back 15:00). Invisible on MySQL and untested in Slice 1. `TrustedGridData.ToDbUtc`/`FromDbUtc` now bind UTC wall-clock as `Unspecified` and read back as `Utc` in both backends.

**Defect found and fixed (console).** `hgtrust show <garbage>` threw `ArgumentException` out of `HGUriNormalizer` through the console; `Find` now treats non-URI, non-fingerprint input as not found.

**Verified.** Bare `dotnet restore`, Release build 0 errors on net10.0. `Tests/OpenSim.TrustedHypergrid.Tests`: 49 passed / 4 skipped with `TRUSTED_HG_MYSQL_CONN` unset; **53 passed / 0 skipped** against the MySQL scratch database, identical under three connection strings (default `GuidFormat`, `Old Guids=true` — Legion's live form, `GuidFormat=None`). New `TrustedGridRegistryTests` cover: plugin load from `[DatabaseService]` and `[TrustedHypergrid]` override; unknown verified grid → Open/pending with key + fingerprint; second contact same key → one row, `last_seen` updated, `first_seen` kept (un-normalised spelling resolves to the same row); different key → state 2, original key intact, new fingerprint does not resolve; approve → Trusted/approved with by/at; block → Blocked and still resolves; forget → row and aliases gone, next contact is first contact; aliases through the shared normaliser (case/trailing-slash/path) resolve to one grid; `ClassifyInbound` with caller URI records pending and returns the grid id, without URI resolves but records nothing, unsigned → Open/Unverified; `hgtrust list`/`show`/`key show` formats; garbage operator input never throws. Commands were also driven end-to-end through a real `CommandConsole.RunCommand` over SQLite in a scratch harness (output reproduced in the slice report).

**No code path can refuse access — how verified.** (1) `grep` for every consumer of `Tier`/`TrustTier`/`TierBlocked` outside the registry, data layer and log calls: the only hit is `TrustedGridAuthentication.Authenticate(headers,…)` (`Source/OpenSim.Framework/ServiceAuth/TrustedGridAuthentication.cs:72`), which refuses only when `GridTrustContext.Current.Tier == Blocked`. (2) Nothing in production sets `GridTrustContext.Current` — the only assignments are in `SignatureVerificationTests`; `ClassifyInbound` returns the context and does not publish it. (3) No config anywhere selects `AuthType = TrustedGridAuthentication` (live Legion Robust: `None` / `BasicHttpAuthentication`). (4) The registry's `TryResolveByFingerprint` and `RecordPresentedKey` catch every exception and degrade to Open/no-op; `LoadTrustRegistry` returns null on any failure, leaving the Slice 2b verify-only behaviour. (5) Files on the NOT list untouched: `GatekeeperService.cs`, `HGAssetService`, `HGInventoryAccessModule.cs`. Consequence to carry forward: `hgtrust block` becomes live enforcement the moment a future slice both publishes `GridTrustContext.Current` and an operator configures `AuthType = TrustedGridAuthentication` — that is the intended §6 design, but it must be a deliberate step.

### G-1 — CLOSED (Slice 3)
Executed against `trusted_hg_scratch` in the `legiongrid_mysql` container (port 3308) as a dedicated user `trusted_hg` scoped to that database only; `legiongrid` never contacted (verified: no `hg_*` tables there). Migration ran on first construction: tables `hg_trusted_grids` (CHAR(36) PK, UNIQUE home_uri, KEY key_fingerprint, InnoDB utf8mb3), `hg_grid_aliases`, and `migrations` recording `TrustedGrid = 1`. All four MySQL facts (round-trip, key change, alias, list/delete) execute and pass. Gating changed from a static `[Fact(Skip=…)]` to `[MySqlFact]` (`Tests/OpenSim.TrustedHypergrid.Tests/MySqlFactAttribute.cs`), which sets `Skip` at discovery when `TRUSTED_HG_MYSQL_CONN` is unset: the MySQL path runs whenever the variable is set and is reported as skipped — never silently passed — otherwise. The standing constraint stands: `StandaloneCommon.ini:19` stays commented.

### R-1 — CLOSED (Slice 3): not hit
Probed with MySqlConnector 2.6.1 reading `hg_trusted_grids.id` (CHAR(36)): default `GuidFormat` returns `System.Guid`; `Old Guids=true` (Legion's live string) and `GuidFormat=None` return `String`. The data layer reads `r["id"].ToString()` → `new UUID(string)`, which round-trips under all three; the full suite passes under all three. The hazard would only bite code calling `GetString`/`GetGuid` on that column or a CHAR(36) holding a non-GUID — neither exists in this path. No connection-string change required; `Old Guids=true` may stay.

### D-5 — TOFU has no caller URI on the wired transport
**Type:** design gap (STOP AND REPORT). **Opened:** Slice 3.

§3 records the presented key "against the URI" on first contact, but the frozen §5 envelope carries `tg_key`, `tg_ts`, `tg_nonce`, `tg_sig` only, and the wired gatekeeper calls carry no caller identity beyond that (`link_region`: `region_name`; `get_region`: `region_uuid`); the remote endpoint is an IP, not a gatekeeper URI. `hg_trusted_grids.home_uri` is NOT NULL UNIQUE. The recording path is built and tested (`ClassifyInbound(parameters, method, claimedHomeUri)` → `IGridTrustRecorder`), but the production call sites pass no URI, so nothing is recorded on the gatekeeper XML-RPC path until one of these is decided: (a) add a fifth advisory key `tg_uri` (the sender's GatekeeperURI) to the envelope — a §5 change; whether it joins the signed canonical payload is a second decision; (b) record by fingerprint only with a synthetic placeholder URI — a §4 change; (c) defer TOFU to a transport that carries a URI (the UserAgent/`/foreignagent` path, D-2). Recommendation: (a), unsigned-advisory, because identity is the key (ADR-003) and the URI is only ever a label. Not implemented here.

### D-6 — re-approval after a key change is forget → reconnect → approve
**Type:** decision made, no ADR. **Opened:** Slice 3.

On a key change the original key is preserved and the new key is not stored (Slice 1 rule), so `hgtrust approve` on a state-2 row cannot adopt the new key. The operator flow is `hgtrust forget <uri>`, let the grid reconnect (recorded pending with the new key), then `hgtrust approve`. `hgtrust show` prints this under "Attention". Alternative (store the pending key in a second column and let approve adopt it) would be a §4 schema change; not taken.

### D-5 — CLOSED (Slice 3, operator decision 29 Aug 2026): `tg_uri`
Decision: option (a). A fifth key `tg_uri` (XML-RPC) / `X-TG-Uri` (HTTP) carries the sender's own GatekeeperURI, resolved from `GatekeeperURI` in [Startup]/[Hypergrid]/[GatekeeperService]/[UserAgentService] (the gatekeeper's own lookup) and normalised by `HGUriNormalizer`. It is **unsigned-advisory**: `HGSignatureEnvelope.ParametersDigest` already excludes every `tg_*` key, so the §5 canonical payload is byte-identical to before and no stock or Slice 2 verifier is affected. When this grid has no GatekeeperURI the key is simply absent (request byte-identical to Slice 2; WARN at startup). `TrustedHypergridHooks.ClassifyInbound(parameters, method)` — the production call in `HypergridHandlers` — now records first contact against `tg_uri` for a signature-verified caller, so TOFU is live on the gatekeeper XML-RPC path with no call-site change. Verified: signer writes the normalised key / omits it when unknown; a `tg_uri` altered after signing still verifies (it is a label, not identity); the two-arg production path records Open/pending with the caller's fingerprint and does not duplicate or promote on repeat contact. Tests: 53 passed / 4 skipped (no MySQL), 57/57 with MySQL. **Deviation from the frozen §5 four-key list, approved by the operator; the DESIGN text is not edited — promote to an ADR.**

### R-2 — the advisory URI is a claim; any keyholder can flag any URI
**Type:** risk. **Opened:** Slice 3. **Severity:** nuisance now (nothing enforces); matters once state gates anything.

Because `tg_uri` is a claim, a grid holding any valid keypair can sign a call claiming an established grid's URI with a different key; Slice 1's rule then sets that row `state=2` (key-changed, re-approval) while preserving the original key and tier. Signing the URI would not remove this (a keyholder signs its own claim); it is inherent in URI-as-label. Trust is never lost silently — the original key still resolves to its tier and `hgtrust show` names the event — but an operator could be nagged into `forget`/re-approve. **To close:** when a future slice gates on `state`, only flag a row when the mismatching key arrives on a request whose signature verifies *and* the claimed URI's row is not already Trusted-by-original-key — or move the flag to a side table so an established row's state is only ever changed by its own key or the operator.

### D-5 addendum (29 Aug 2026, operator correction): `tg_uri` is SIGNED
The first `tg_uri` cut left the key outside the digest ("advisory"). That was wrong for the threat that matters: not a keyholder lying about its own URI (inherent, accepted) but **a third party on the wire rewriting a legitimate grid's `tg_uri` to another established grid's URI, forcing a spurious `state=2` re-approval on that grid with no key required** — the test "a tg_uri altered after signing still verifies" demonstrated exactly that. Now `HGSignatureEnvelope.ParametersDigest(parameters, senderUri)` folds the URI in as the entry `tg_uri=<uri>`: the signer passes its `HomeUri`, the verifier passes the URI it extracted from the transport (`SignatureMaterial.Uri`), and the raw `tg_uri` parameter stays excluded from enumeration so it is digested exactly once whichever transport carried it. A rewritten `tg_uri` → digest mismatch → signature does not verify → Open, and the established row is untouched (tested: approved row keeps `state=approved`, tier Trusted, original fingerprint; nothing recorded for either URI).

**Absent-URI equivalence, confirmed by test.** With no sender URI nothing is added, and the digest is byte-identical to the Slice 2 form — including after Slice 2 material (no `tg_uri`) is attached. Stock grids (no material) and Slice 2 signers are unaffected; a Slice 2 signer verifies under the Slice 3 verifier.

**Versioning between a Slice 2 verifier and a Slice 3 signer.** No wire version field exists in §5 and none is added. Compatibility matrix: Slice 2 signer → Slice 3 verifier: Verified. Slice 3 signer (with `tg_uri`) → Slice 2 verifier: the Slice 2 digest rule excludes every `tg_*` key, so the signature does not match → classified Open/Unverified (INFO "signature did not verify"), never refused (ADR-005), and nothing recorded. Tested by reconstructing the Slice 2 payload against a Slice 3 signature. Consequence: **deploy verifiers before signers** — both members of a Tranquillity pair must be at this commit or later to classify each other Verified; until then the pair degrades to Open on the older side. Legion's live Robust (`1c35f18db7`) is a Slice 2 verifier and will classify a Slice 3 peer Open until it is redeployed. Slice 3 (`11161ab8f0`) and the unsigned `tg_uri` cut are already committed; this is a follow-up commit, not a revision.

### R-2 — updated: unsigned-rewrite case CLOSED; keyholder-claim case accepted
The on-wire rewrite (no key required) is closed by the signed `tg_uri` above. What remains is the accepted, inherent property: a grid holding any valid keypair can sign its own call claiming an established grid's URI, and Slice 1's rule then sets that row `state=2` while preserving the original key and tier. Never silent, never a loss of trust; nuisance only while nothing enforces. Options for the enforcement slice unchanged from the original entry.

---

## Slice 3b — 30 Aug 2026 — enforcement, minimum viable: Blocked refuses, nothing else changes

**Delivered.** (1) `GridTrustContext.Enter(context)` — a disposable scope that publishes `Current` for one request and restores the previous value (normally null) on dispose; `TrustedHypergridHooks.Classify(parameters, method)` = classify + publish in one scope. (2) `HypergridHandlers.LinkRegionRequest`/`GetRegion` wrap their bodies in that scope and consult ONE authenticator — a `TrustedGridAuthentication` the gatekeeper connector arms only when `AuthType = TrustedGridAuthentication` is configured for `[GatekeeperService]` (with the usual `[Network]` fallback, `TrustedGridAuthentication.IsConfigured`). The general `ServiceAuth.Create` chain is deliberately NOT applied to Hypergrid XML-RPC: it has never carried Basic auth and applying it there would be a behaviour change for existing operators. A refusal is an XML-RPC `result=False` with `message="Refused: this grid's operator has blocked your grid."` and an INFO `[TRUSTED HG]: refused <method> from <ip>: grid <id> is Blocked`; the gatekeeper service is never called. (3) `TrustedGridAuthentication.Authenticate` unchanged in rule: false only when `Current.Tier == Blocked`. No new config keys (`AuthType` already exists; this is its first use for the gatekeeper), no envelope/digest change.

**How the context cannot leak.** Sequential requests on one thread — including a long-lived listener thread never returned to the pool — cannot see a predecessor's context because the scope restores in `finally` on every exit path, exception included (tested: a throw inside the scope leaves `Current` null). Concurrent requests on different threads cannot see each other's context because `AsyncLocal<T>` lives in the execution context private to each thread / async flow (tested: 8 threads publishing distinct contexts behind a barrier, each observing only its own across 200 reads). The pre-existing `Current` setter remains for tests; nothing in production sets it except the scope.

**No automatic path to Blocked — verified.** The only assignment of `TrustTier.Blocked` in the tree is `TrustedGridRegistryService.Block` (`hgtrust block`). `RecordPresentedKey` (first contact / key change) never touches `tier`; a key change moves `state` only.

**Defaults, each verified by test.** `Enabled=false` with a Blocked row present and the authenticator armed → runtime disabled, `Classify` publishes null, `Authenticate` true, handler proceeds (§11.7). `Enabled=true`, empty registry → verified-but-unknown is Open, no refusal. `Enabled=true`, Trusted / Open / pending / unknown / unsigned / unverified / no context → all true. Refusal only when ALL of: `Enabled=true`, `hgtrust block` run, the blocked grid presents a verifying signature (an unsigned request from the same grid is Open and proceeds), and `AuthType = TrustedGridAuthentication` configured (the same Blocked grid through unarmed handlers proceeds). After `hgtrust forget` the grid is first contact again and proceeds.

**ADR-011 holds.** The only tier comparison anywhere is `== TierBlocked` (`TrustedGridAuthentication.cs:93`); Trusted and Open share every other path. The handler test drives a Trusted and an Open grid through the armed handlers and asserts identical `link_region` and `get_region` responses (same `result`, same payload fields, no `message`). No tier-graded region access, no export or presence work; `GatekeeperService.LoginAgent`, `HGAssetService`, `HGInventoryAccessModule` untouched.

**Scope note.** The refusal is on the only classified transport (gatekeeper XML-RPC). HTTP stream handlers already consult `IServiceAuth`, but no HTTP request is classified yet (D-2), so `Current` is null there and `TrustedGridAuthentication` returns true — unchanged.

**Verified.** Bare `dotnet restore`; Release build 0 errors on net10.0. `Tests/OpenSim.TrustedHypergrid.Tests`: 71 passed / 4 skipped without `TRUSTED_HG_MYSQL_CONN`; 75 passed / 0 skipped against the MySQL scratch database. New `EnforcementTests` (15): Blocked → false/403; Trusted/Open/pending/unknown/unsigned/unverified/no-context → true; scope publish/restore incl. nested; sequential no-leak incl. exception; concurrent no-leak; blocked+verified → false then forget → true; blocked+unsigned → true; `Enabled=false` + Blocked row → true; empty registry / Trusted / Open / pending → true; `IsConfigured` matrix; handler end-to-end (refused only when armed, gatekeeper not called, Trusted ≡ Open, stock proceeds, forget restores); disabled handlers never refuse.

**The first refusal this project can make — operator steps, in order.** 1. `[TrustedHypergrid] Enabled = true` in Robust.HG.ini and restart Robust (grid identity loads). 2. Let the other grid make one signed HG call (link/teleport) so it is recorded — `hgtrust list` shows it Open/pending. 3. `hgtrust block <its uri>`. 4. Add `AuthType = "TrustedGridAuthentication"` under `[GatekeeperService]` and restart Robust; the log shows `[TRUSTED HG]: TrustedGridAuthentication armed for the gatekeeper XML-RPC handlers`. 5. The blocked grid's next signed `link_region`/`get_region` is answered `result=False` with the refusal message and logged `[TRUSTED HG]: refused …`. Undo: `hgtrust forget <uri>` (or remove the AuthType line).