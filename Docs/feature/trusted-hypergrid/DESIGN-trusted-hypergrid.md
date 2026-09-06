# Design Brief — Trusted Hypergrid

**Status:** FROZEN v2 (29 Aug 2026). This is the authority document. Anything not specified here is OPEN and must be escalated, not invented. v2 reconciles the brief against what is built and committed in `0500ae849b..81a412e72c` on `feature/trusted-hypergrid`; where v1 and the code disagreed, the code was taken as correct. See *Revision history* at the end.
**Repo:** `JohnLegionH/OpenSim-Tranquillity`
**Upstream baseline:** `OpenSim-NGC/OpenSim-Tranquillity` `develop` @ `a115734ff` (net10.0) at v1; the branch now also carries upstream `develop` @ `93765a999e` (ILogger migration #198; libOMV from nuget.org as `UtopiaSkye.OpenMetaverse` 1.1.6).
**Feeds from:** `RECON-trusted-hypergrid.md`, `ADR-trusted-hypergrid.md`, `LEDGER-trusted-hypergrid.md`

---

## 1. Purpose

Give grid operators the ability to form explicit trust relationships with other grids, and to apply different access and export policy depending on whether a remote grid is party to such a relationship. Implements the NGC *Trusted HyperGrid* charter (ADR-001).

This is enforcement and audit for agreements that already exist between operators. It is not DRM (ADR-008).

## 2. Decisions frozen by this brief

The following were open and are now closed. They are authority.

**D1 — Tier granularity is grid × region.** Tier is evaluated per remote grid. Region-level policy composes with it via the existing `AuthorizationService` mechanism rather than replacing it. Rationale: region-level access control already exists and is proven working (`Region_<Name> = "DisallowForeigners"`); tiers add an axis to a working mechanism rather than substituting an unproven one.

**D2 — Presence-oracle gating is OUT OF SCOPE for v1.** Recon R7 (`locate_user`, `get_uui`, `get_uuid`, `get_server_urls`, `status_notification`, `get_online_friends`) is descoped entirely. It is privacy hardening rather than asset control, it is separable from every other part of this module, and it is the only item blocked on Balpien's requirements. It returns as its own ADR and its own brief.

**D3 — Storage splits by mutability.** The trust registry lives in the database: it is mutated at runtime, carries approval timestamps and audit fields, and must survive restart. Tier *policy defaults* live in `Robust.HG.ini` sections. Neither duplicates the other.

**D4 — Signature carriage differs by transport.** See §5. XML-RPC calls carry the signature in the existing param `Hashtable`. HTTP service calls carry it in headers.

**D5 — Verification and enforcement are separate components.** `IServiceAuth.Authenticate` returns `bool` where `false` is an HTTP rejection, which cannot express "unverified, therefore Open tier." Verification classifies; a separate policy check enforces. See §6.

## 3. Trust model

Three tiers, evaluated per remote grid:

| Tier | Establishment | Default for |
|---|---|---|
| **Trusted** | Operator-approved after out-of-band agreement | Nothing — always explicit |
| **Open** | Automatic | Every unknown grid, every unverifiable request |
| **Blocked** | Operator action | Nothing — always explicit |

**Identity is an Ed25519 public key, not a URI** (ADR-003). Multiple `HomeURI` values may map to one key, which is how aliasing is handled (`hg_grid_aliases`; `hgtrust show` resolves a home URI, an alias URI or a fingerprint to the same row). A key change on an established relationship is flagged, not silently accepted: the row moves to state `2` (key-changed-pending-reapproval), the original key and tier are preserved, and the new key's fingerprint resolves to nothing, so calls under it classify `Open` until an operator re-establishes the relationship (`hgtrust forget <uri>`, let the grid reconnect, `hgtrust approve <uri>` — LEDGER D-6). Nothing is refused by this: refusal is an enforcement concern outside what is built.

**Where the URI comes from.** The URI for first-contact recording is the caller's `tg_uri` (§5): the sending grid's own `GatekeeperURI`, read at runtime start from `[Startup]`, `[Hypergrid]`, `[GatekeeperService]` or `[UserAgentService]` (the same lookup the gatekeeper itself uses) and normalised through the single shared `HGUriNormalizer` before it is sent; the receiver normalises again through the same function at every write and lookup. A grid with no `GatekeeperURI` configured sends no `tg_uri` and is classified but cannot be recorded. The URI is a label the sender claims; it is signed against rewriting on the wire (§5, §9) but it is not the identity — the key is.

**Establishment flow:** first contact — a request whose signature verifies and which carries a `tg_uri` — records the presented public key and its fingerprint against that URI, tier `Open`, state `pending` (`TrustedHypergridHooks.ClassifyInbound` → `IGridTrustRecorder.RecordPresentedKey`). Repeat contact with the same key updates `last_seen` only. An operator promotes to `Trusted` via `hgtrust approve` (console; web admin is not built). No automatic promotion under any circumstance: `hgtrust approve` is the only code path that sets tier `Trusted`.

## 4. Trust registry — data model

Table `hg_trusted_grids`:

| Column | Type | Notes |
|---|---|---|
| `id` | UUID | PK |
| `home_uri` | VARCHAR(255) | Normalised: lowercase scheme+host, explicit port, trailing slash |
| `public_key` | VARBINARY(32) | Ed25519 raw public key; NULL for grids that have never signed |
| `key_fingerprint` | CHAR(64) | SHA-256 hex of public key; the operator-facing identifier |
| `tier` | TINYINT | 0=Blocked, 1=Open, 2=Trusted |
| `state` | TINYINT | 0=pending, 1=approved, 2=key-changed-pending-reapproval |
| `first_seen` | DATETIME | |
| `last_seen` | DATETIME | |
| `approved_by` | VARCHAR(64) | Operator identity, free text |
| `approved_at` | DATETIME | NULL until approved |
| `notes` | TEXT | |

Table `hg_grid_aliases`: `grid_id` (FK), `alias_uri` (normalised). Many-to-one.

**URI normalisation is a single shared function** used at write and lookup. Recon R6 established that ad-hoc string comparison is the existing defect; it must not be reintroduced.

Local grid keypair: generated on first run, private key stored outside version control following the existing `bin/DirectDeliverySecret.ini` pattern with a gitignored include. Never in the database.

## 5. Signature envelope

Signed payload (`HGSignatureEnvelope.BuildCanonicalPayload`) is the LF-joined canonical concatenation of: method name, sender key fingerprint (lowercase SHA-256 hex of the raw public key), UTC timestamp (`yyyy-MM-ddTHH:mm:ssZ`), nonce (16 random bytes, base64), and the parameter digest. Ed25519 signature over that, base64. `HGSignatureEnvelope` is the single canonicaliser used by both `GridSignatureSigner` and `GridSignatureVerifier`, so the two cannot drift.

**Parameter digest** (`HGSignatureEnvelope.ParametersDigest(parameters, senderUri)`): lowercase SHA-256 hex of the method's own parameters rendered `key=value`, sorted by ordinal key, LF-joined. Every raw `tg_*` / `X-TG-*` entry is excluded from that enumeration, so the digest is stable whether computed before or after the material is attached. The sender's URI is then folded in **once**, as the entry `tg_uri=<uri>`, taken from the signer's own `HomeUri` on the sending side and from the value the verifier extracted from the transport (`SignatureMaterial.Uri`) on the receiving side — never from the raw parameter set — so it is digested exactly once whichever transport carried it. **When no sender URI is present nothing is added and the digest is byte-identical to the four-key form**; stock grids (no material) and pre-`tg_uri` signers are unaffected.

**Replay defence:** timestamp outside ±300 seconds (`TimestampTolerance`) is unverified. Nonce cache with a 600-second window (`NonceWindow`); a repeated nonce is unverified, and a nonce is registered only after the signature is otherwise valid so a bad request cannot burn one. "Unverified" always means Open tier, never rejection (ADR-005); every verifier failure path — no material, malformed base64, wrong key length, unparseable or stale timestamp, bad signature, replayed nonce, any exception — returns `GridTrustContext.Open` and logs.

**XML-RPC transport** (Gatekeeper, UserAgent, HGFriends — `Nwc.XmlRpc`, `request.Send(uri, httpClient)`): **five** well-known keys in the existing param `Hashtable` — `tg_key` (base64 public key), `tg_ts`, `tg_nonce`, `tg_sig`, and `tg_uri` (the sender's normalised `GatekeeperURI`; omitted entirely when the sender has none, leaving the request byte-identical to the four-key form). The first four are required for a material to be complete (`SignatureMaterial.HasAll`); `tg_uri` never is. Stock grids ignore unknown keys and omit them on send, which yields Open tier automatically with no code path for failure. Wired call sites at v2: outbound `link_region` and `get_region` in `GatekeeperServiceConnector` (`TrustedHypergridHooks.SignOutbound`), inbound `link_region` and `get_region` in `HypergridHandlers` (`TrustedHypergridHooks.ClassifyInbound`); the UserAgent and HGFriends XML-RPC calls are carried by the same material class but are not yet wired (LEDGER D-2).

**HTTP transport** (`HGAssetService`, `HGInventoryService`): headers `X-TG-Key`, `X-TG-Timestamp`, `X-TG-Nonce`, `X-TG-Signature`, `X-TG-Uri`, read and written by `SignatureMaterial` and attached via `IServiceAuth.AddAuthorization(NameValueCollection)` (`TrustedGridAuthentication`) on the outbound side. The header carriage and the auth class exist; the HTTP call sites are not yet wired (LEDGER D-2).

### 5.1 Version compatibility

No wire version field exists and none is added. Verified by test against the two envelope forms that have been committed (four-key, `0500ae849b`; five-key signed, `81a412e72c`):

| Signer | Verifier | Outcome |
|---|---|---|
| pre-`tg_uri` (four-key) | current | **Verified** — absent URI adds nothing to the digest |
| current (sends `tg_uri`) | pre-`tg_uri` | **Open / Unverified** — the old digest rule excludes every `tg_*` key, so the signature does not match; never refused (ADR-005); nothing recorded |
| current | current | Verified; first contact recorded against `tg_uri` |
| stock grid (no material) | any | Open; unchanged |

**Operational rule: DEPLOY VERIFIERS BEFORE SIGNERS.** Both members of a Tranquillity pair must be at `81a412e72c` or later to classify each other `Verified`; until then the older side degrades the pair to `Open`. A Robust built before `81a412e72c` with `Enabled = true` is a pre-`tg_uri` verifier.

## 6. Verification and enforcement split

**`GridSignatureVerifier`** — not an `IServiceAuth`. Given a request's signature material, returns a `GridTrustContext` carrying resolved grid id, tier, and verification outcome. Never rejects. Populated early in request handling and available to services.

**`IServiceAuth` implementation** — used only where hard rejection is correct, i.e. refusing Blocked-tier callers. Registered in the `ServiceAuth` factory switch (`Source/OpenSim.Framework/ServiceAuth/ServiceAuth.cs`) alongside `BasicHttpAuthentication`, composed by the existing `CompoundAuthentication`. Returns `false` only for Blocked.

**Policy checks** consult `GridTrustContext` at the enforcement points in §7. Absence of a context means Open.

## 7. Enforcement points

| Point | File | Behaviour |
|---|---|---|
| Region access | `GatekeeperService.LoginAgent` | Tier-aware, composed with `AuthorizationService` per D1 |
| Inventory transfer export | `HGInventoryAccessModule.cs:392-393` | Already correct per #187 — **do not modify** |
| Asset push export | `HGInventoryAccessModule.cs:214` | Add Export-bit check. **Preserve the `AssetType.Landmark` carve-out unconditionally** (ADR-007) |
| Outbound abroad | `HGInventoryAccessModule.cs:560, 574` | Add tier dimension |
| Asset service export | `HGAssetService.Get` (112, 152, 197) | Add per-request dimension to the existing type-only `AssetPermissions` gate |
| Asset service import | `HGAssetService` (170) | Tier-aware accept/refuse |

Every check is an **additional restriction layered on the existing gates**. No change may loosen current behaviour. `m_OutboundPermission` remains an outer gate and retains its `false` default from #187.

## 8. Operator surface

Console: `hgtrust list`, `hgtrust show <uri|fingerprint>`, `hgtrust approve <uri>`, `hgtrust block <uri>`, `hgtrust forget <uri>`, `hgtrust key show`, `hgtrust preview-export` (dry-run showing what the Export-bit gate would block).

Config, `[TrustedHypergrid]` in `Robust.HG.ini`: `Enabled` (default `false`), per-tier policy defaults, `RequireExportBit` (default `false` for one release per §10).

*Built at v2:* every console command above except `hgtrust preview-export` (belongs to the export slice); `hgtrust approve <uri> [approved-by]` records the operator string (default `console`). Config keys that exist: `Enabled`, `PrivateKeyFile` (default `TrustedHypergridSecret.ini`), and `StorageProvider` / `ConnectionString` overriding `[DatabaseService]` for the registry. Per-tier policy defaults, `RequireExportBit` and the web admin are not built.

## 9. Threat model — mandatory, frozen (ADR-008)

**Defends against:** an unmodified-viewer user or a non-partner grid pulling assets the creator marked non-exportable; a blocked grid re-entering under an alias URI; unattributed cross-grid asset movement; casual UUID-guessing against the public asset endpoint for items with the Export bit cleared.

**Defends against (signature envelope, verified by test at `81a412e72c`):** a third party on the wire rewriting a legitimate grid's `tg_uri` to another established grid's URI. Because the URI is inside the signed digest, the rewritten request fails verification and classifies `Open`; nothing is recorded for either URI, and the impersonated grid's registry row — key, tier, state — is untouched. This closes the case where a spurious key-change flag could have been forced on an established grid with no key at all (LEDGER R-2, closed part).

**Does not defend against, and must be stated wherever this feature is described:** a modified viewer copying anything rendered on screen; a Trusted grid's operator misusing assets that legitimately reached their asset server; a determined attacker with an asset UUID for an exportable item; anything at all once content is off the grid. This module produces attribution and revocation, not prevention.

**Limitation, accepted (LEDGER R-2, residue):** a grid that holds any valid keypair can sign its own request while claiming another grid's URI in `tg_uri`. The protocol cannot distinguish that from a genuine key change, so the claimed URI's row is flagged state `2` — original key and tier preserved, never silently, and visible in `hgtrust show`. This is inherent in URI-as-label under ADR-003 and is handled by out-of-band operator approval (`hgtrust forget` / `approve`), not by the protocol. It is a nuisance while nothing enforces on `state`; it must be weighed before anything does.

## 10. Negative scope

Not in v1, and not to be added without a new ADR: presence-endpoint gating (D2); signed community roster (ADR-003); PKI or any certificate authority; TLS enforcement or configuration changes; `HGAssetMapper` retry/parallelism work (Recon R9); any modification to `HGInventoryAccessModule.cs:392-393`; any new HG endpoint or transport; any change to landmark handling.

## 11. Done-when

1. Two Tranquillity grids exchange signed calls, mutually classify `Trusted` after explicit approval, and the fingerprints match on both sides.
2. **A live stock OpenSim grid teleports in and out with no configuration change and no behavioural difference from today, classified `Open`.** This is a first-class criterion, not a regression check.
3. A tampered or absent signature classifies `Open` and logs. No 500, no rejection, no teleport failure.
4. An item with the Export bit cleared cannot be fetched from the public HG asset endpoint by an `Open` caller; the same item is available to a `Trusted` caller where policy permits; an item with the bit set behaves exactly as it does today.
5. Landmarks export under every tier including Blocked.
6. A grid re-presenting a different key for an established relationship is refused and flagged for re-approval.
7. `Enabled = false` produces byte-identical behaviour to `a115734ff`.

## 12. Known-open, escalate rather than invent

**Closed since v1** (how, and where recorded):

- *Legion tree divergence in `Source/OpenSim.Services.HypergridService/`, `…/InventoryAccess/`, `…/ServiceAuth/`* — closed by evidence: `feature/trusted-hypergrid` has merged into `integration/legiongrid-trusted-hg` twice (`0ac27e3f2d`, `1c35f18db7`) with no conflict under any of those paths; Legion's divergence is confined to voice, physics and membership.
- *G-1, MySQL path never executed* — closed in Slice 3: migration and all MySQL facts executed against a scratch database in the `legiongrid_mysql` container (LEDGER G-1 — CLOSED).
- *R-1, `CHAR(36)` UUID under MySqlConnector* — closed in Slice 3, not hit: `r["id"].ToString()` round-trips under default `GuidFormat`, `Old Guids=true` and `GuidFormat=None` (LEDGER R-1 — CLOSED).
- *D-1, OpenMetaverse packages as a local fiction* — closed by upstream `93765a999e` (libOMV published to nuget.org as `UtopiaSkye.OpenMetaverse` 1.1.6), now in this branch's history; a bare `dotnet restore` works and the local feed is gone.
- *D-5, TOFU had no URI source on the wired transport* — closed by `tg_uri`, signed (§5, §3; LEDGER D-5 and addendum).

**Still open:**

- @llaxton's remaining scope on the Export paths (ADR-006, PROPOSED).
- `GridTrustContext` propagation to `HGAssetService`: still undetermined. At v2 nothing in production sets `GridTrustContext.Current` — `ClassifyInbound` returns the context to its caller and does not publish it — which is also why `TrustedGridAuthentication`'s Blocked check cannot fire. Determine by inspection before the enforcement slice is ordered.
- D-2: the UserAgent and HGFriends XML-RPC calls and the HTTP transport (`HGAssetService`, `HGInventoryService`) carry material but are not wired at the call sites; only the gatekeeper `link_region`/`get_region` pair is.
- D-3: the runtime initialises only in a process that stands up the inbound gatekeeper connector (Robust HG); a region simulator's outbound gatekeeper calls are unsigned until a region-side init is added.
- D-6: re-approval after a key change is `forget` → reconnect → `approve`; the pending key is not stored. Decided, not yet an ADR.
- R-2 residue: a keyholder claiming another grid's URI (§9, limitation). Must be weighed before anything enforces on `state`.
- U-1: upstream `Tests/OpenSim.Framework.Tests` does not compile (introduced by #197); not ours to fix, report to NGC.
- Promotion of the following to ADRs: BouncyCastle.Cryptography 2.6.2 in `OpenSim.Framework` (proposed ADR-009), the `[TrustedHypergrid]` config surface (proposed ADR-010), the signed `tg_uri` (§5) and D-6.

---

## Revision history

**v1 → v2 (29 Aug 2026, reconciled against `0500ae849b..81a412e72c`).**

- **§5 gained a fifth key, `tg_uri`.** v1's four-key envelope carried the sender's key but no URI, and the gatekeeper XML-RPC params carry none either, so §3's "record the key against the URI on first contact" had no URI source once the registry was wired (Slice 3, LEDGER D-5). The sender's own normalised `GatekeeperURI` is now carried as `tg_uri` / `X-TG-Uri`.
- **`tg_uri` is signed, not advisory.** The first cut left it outside the digest because a keyholder can claim any URI regardless of signing. That missed the real threat: a third party on the wire rewriting a legitimate `tg_uri` to an established grid's URI would force a spurious state-2 re-approval on that grid with no key required — demonstrated by a test in the first cut. The URI is now folded into the signed parameter digest exactly once; an absent URI digests identically to the four-key form. §5.1 records the resulting version matrix and the deploy-verifiers-first rule.
- **§3 and §9 now describe the implementation.** §3 states where the URI comes from, that a key change flags rather than refuses (nothing enforces yet), and the operator flow (LEDGER D-6). §9 records the on-wire rewrite as defended and the keyholder-claim as an accepted limitation handled out of band (LEDGER R-2).
- **§12 reconciled with the LEDGER.** Legion divergence, G-1, R-1, D-1 and D-5 closed with the evidence; D-2, D-3, D-6, R-2 residue, U-1 and the pending ADR promotions listed as open. §10 and §11 are unchanged.
- **Header** records v2, the reconciled commit range, and the upstream commits the branch now carries.
