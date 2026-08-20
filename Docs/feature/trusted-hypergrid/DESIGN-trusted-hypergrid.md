# Design Brief — Trusted Hypergrid

**Status:** FROZEN v1. This is the authority document. Anything not specified here is OPEN and must be escalated, not invented.
**Repo:** `JohnLegionH/OpenSim-Tranquillity`
**Upstream baseline:** `OpenSim-NGC/OpenSim-Tranquillity` `develop` @ `a115734ff` (net10.0)
**Feeds from:** `RECON-trusted-hypergrid.md`, `ADR-trusted-hypergrid.md`

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

**Identity is an Ed25519 public key, not a URI** (ADR-003). Multiple `HomeURI` values may map to one key, which is how aliasing is handled. A key change on an established relationship is a hard failure requiring re-approval.

**Establishment flow:** first contact records the presented public key against the URI, tier `Open`, state `pending`. An operator promotes to `Trusted` via console or web admin. No automatic promotion under any circumstance.

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

Signed payload is the canonical concatenation of: method name, sender key fingerprint, UTC timestamp (ISO-8601, seconds), nonce (16 random bytes, base64), and the SHA-256 digest of the method's own parameters in canonical form. Ed25519 signature over that, base64.

**Replay defence:** timestamp outside ±300 seconds is unverified. Nonce cache with a 600-second window; a repeated nonce is unverified. "Unverified" always means Open tier, never rejection (ADR-005).

**XML-RPC transport** (Gatekeeper, UserAgent, HGFriends — `Nwc.XmlRpc`, `request.Send(uri, httpClient)`): four well-known keys added to the existing param `Hashtable` — `tg_key`, `tg_ts`, `tg_nonce`, `tg_sig`. Stock grids ignore unknown keys and omit them on send, which yields Open tier automatically with no code path for failure.

**HTTP transport** (`HGAssetService`, `HGInventoryService`): headers `X-TG-Key`, `X-TG-Timestamp`, `X-TG-Nonce`, `X-TG-Signature`, attached via `IServiceAuth.AddAuthorization(NameValueCollection)` on the outbound side.

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

## 9. Threat model — mandatory, frozen (ADR-008)

**Defends against:** an unmodified-viewer user or a non-partner grid pulling assets the creator marked non-exportable; a blocked grid re-entering under an alias URI; unattributed cross-grid asset movement; casual UUID-guessing against the public asset endpoint for items with the Export bit cleared.

**Does not defend against, and must be stated wherever this feature is described:** a modified viewer copying anything rendered on screen; a Trusted grid's operator misusing assets that legitimately reached their asset server; a determined attacker with an asset UUID for an exportable item; anything at all once content is off the grid. This module produces attribution and revocation, not prevention.

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

- Whether the Legion tree diverges in `Source/OpenSim.Services.HypergridService/`, `Source/OpenSim.Region.CoreModules/Framework/InventoryAccess/`, or `Source/OpenSim.Framework/ServiceAuth/`. Gates the Build Plan, not this brief.
- @llaxton's remaining scope on the Export paths (ADR-006, PROPOSED).
- Whether `GridTrustContext` propagation needs a plumbing change to reach `HGAssetService`, or whether existing request context suffices. Determine by inspection before Build Plan slice ordering.
