# Design decisions: Trusted Hypergrid

These are the design decisions behind Trusted Hypergrid, for maintainers. Each record gives the decision, the
reason, what was rejected, and when to revisit it. The operator-facing description is in
`DESIGN-trusted-hypergrid.md`. Code comments cite these records by number, so the numbers do not change.
Numbers 009 and 010 were recorded after the others.

---

## ADR-001: Implement the existing NGC Trusted HyperGrid charter

**Decision.** The module is the implementation of NGC's existing *Trusted HyperGrid* wiki page, not a new
proposal.

**Why.** The charter already sets the requirements:
- trust relationships between grids;
- identifying and blocking sources of infringing content;
- per-item export control;
- users knowing whether an item may leave the grid.

**Rejected.** A separately named module, which would fragment the effort.

**Revisit if** NGC changes direction on the charter.

---

## ADR-002: No BinaryFormatter work

**Decision.** The module includes no BinaryFormatter removal work.

**Why.** BinaryFormatter is already gone from the tree. YEngine refuses legacy serialised blobs rather than
reading them, and `FlotsamAssetCache` uses a fixed-type XML format. The case for the module rests on access
control and export enforcement alone.

**Revisit if** an unsafe deserialiser is reintroduced.

---

## ADR-003: Grid identity is an Ed25519 keypair; trust is first use plus operator approval

**Decision.** Each grid holds an Ed25519 keypair, and the public key is the grid's identity. The first signed
contact records the key against the URI the caller claims, at tier Open and pending. Only an operator, acting
out of band, promotes a grid to Trusted. A key change on a known URI is flagged and keeps the original key.

**Why.** The existing allow and deny lists in the gatekeeper match the caller's self-declared home URI as a
string, after trimming a trailing slash. Grids can advertise several hostnames (`GatekeeperURIAlias`,
`HomeURIAlias`), so any list keyed on that string can be bypassed by a config edit on the far side. Trust has to
key on something the remote operator cannot rotate cheaply. Aliases then come for free: many URIs, one key.
First use plus approval needs no central authority.

**Rejected.**
- *A shared secret per pair of grids.* It does not scale and has no revocation story.
- *A signed community roster.* It needs an operator for the roster itself. The data model leaves room to add
  one later.
- *A full PKI.* Disproportionate for a community of independent operators.

**Revisit if** the number of trusted partner grids grows large enough for a roster to pay for its running cost.

---

## ADR-004: Signatures ride on existing Hypergrid calls

**Decision.** Authentication travels as extra parameters (XML-RPC) or headers (HTTP) on the existing Hypergrid
endpoints. There is no new endpoint and no new transport. The refusal component is an `IServiceAuth`
registered in the `ServiceAuth` factory.

**Why.** A new protocol would diverge permanently from stock OpenSim. `IServiceAuth` is a small interface with a
single factory switch, so adding one implementation is contained.

**Revisit if** the Hypergrid transport layer is replaced upstream.

---

## ADR-005: Unverifiable requests are Open; interop never fails

**Decision.** A missing, malformed or unverifiable signature is not an error. It classifies the caller as Open
and is logged. Stock OpenSim grids keep working with no configuration change and no difference in behaviour.

**Why.** This rule keeps the module an overlay on the Hypergrid rather than a wall around part of it. It also
handles version skew between grids that upgrade at different times.

**Revisit if:** never, for the Open tier. Trusted-tier requirements may tighten freely.

---

## ADR-006: Export-bit enforcement builds on the existing per-item check

**Decision.** Further export-bit enforcement extends the pattern that upstream PR #187 established in
`HGInventoryAccessModule`. That PR made `OutboundPermission` default to `false` and made the inventory-transfer
path require the item's own `PermissionMask.Export` bit. Nothing in this module reimplements that check.

**Still open.** These paths do not yet consult the item's export bit or the caller's tier:
- the asset-push path (`HGInventoryAccessModule`, the `m_OutboundPermission || Landmark` test);
- the two outbound checks that consult only `m_OutboundPermission`;
- `HGAssetService`, whose gates are by asset type only.

**Revisit when** the export work is scheduled. Coordinate with whoever is working that surface at the time.

---

## ADR-007: Landmarks always export

**Decision.** `AssetType.Landmark` keeps bypassing export restriction in `HGInventoryAccessModule` under every
tier, Blocked included.

**Why.** Landmarks are Hypergrid navigation, not content. Restricting them breaks travel.

**Revisit if:** never, short of an upstream change to Hypergrid addressing.

---

## ADR-008: The module claims accountability, not prevention

**Decision.** Documentation, config comments, console output and announcements say plainly what the module
does. It raises the cost of misbehaviour and creates attribution and revocation. It does not prevent copying.

**Why.** A modified viewer defeats server-side control of anything rendered on screen. A trusted grid's operator
can do as they like with assets that legitimately reach their asset server. Overclaiming loses community trust.

**Consequence.** The threat model in `DESIGN-trusted-hypergrid.md` §9 is part of the feature's description, not
a footnote.

---

## ADR-009: Ed25519 comes from BouncyCastle

**Decision.** Key generation, signing and verification use `BouncyCastle.Cryptography`, referenced from
`OpenSim.Framework`.

**Why.** It provides Ed25519 on every platform the tree targets. The private key is persisted as hex in an ini
file, so no platform key store is needed.

---

## ADR-010: The configuration surface

**Decision.**
- The feature is configured in `[TrustedHypergrid]` in `Robust.HG.ini`: `Enabled` (default `false`),
  `PrivateKeyFile`, and optional `StorageProvider` / `ConnectionString` overrides.
- The registry lives in the database, because it changes at runtime and must survive a restart.
- The private key lives in a file outside the database and outside version control.
- Refusing Blocked grids is a separate opt-in, `[GatekeeperService] AuthType = "TrustedGridAuthentication"`.
- `TrustedHypergridRuntime` is built once from that section. When `Enabled` is false it loads nothing.

**Why.** Each switch defaults to the previous behaviour. Turning the feature on adds signing, classification
and a registry but refuses nothing. Refusal is a second, deliberate step.

---

## ADR-011: Open loses nothing

**Decision.** Only Blocked refuses. Trusted and Open get identical access. Nothing a stock OpenSim grid can do
today is withheld from an Open grid.

**Why.** Every unmodified grid is Open, so anything Open lost would be ordinary Hypergrid breaking for most of
the network. Any future tier feature must be phrased as something Trusted gains, never as something Open loses.

**Rejected.** Tier-graded region access. Most transports are not yet signed or classified, so gating on tier
would refuse inconsistently depending on which call path a visitor took.

**Revisit when** a capability exists that is purely additive for Trusted, and the transports it depends on are
signed and classified.

---

## Open questions

- Whether tier should be evaluated per grid only, or per grid and region together with the existing
  `AuthorizationService` region policy.
- Which presence endpoints, if any, should be gated by tier, and what that would break for stock grids.
- How a key change should be re-approved without the `forget` / reconnect / `approve` round trip; the pending
  key is not stored today.
