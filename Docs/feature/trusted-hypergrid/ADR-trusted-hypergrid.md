# Architecture Decision Records — Trusted Hypergrid

Each record states the decision, the reasoning, what was rejected, and the condition under which it should be revisited. Decisions marked **PROPOSED** are not yet authority and must not constrain implementation.

**Feeds from:** `RECON-trusted-hypergrid.md` @ `a115734ff`
**Context:** approach reviewed by Mike and others, no objections. Development target is `JohnLegionH/OpenSim-Tranquillity`.

---

## ADR-001 — Build as the implementation of NGC's Trusted HyperGrid charter

**Status:** ACCEPTED

**Decision.** The module is named and framed as the implementation of the existing *Trusted HyperGrid* wiki page (Mike Dickson, July 2022), not as a new proposal.

**Why.** The charter already specifies the requirements — grid-to-grid trust relationships, identifying and blocking sources of infringing content, per-item export control, and users knowing whether an item may leave the grid. It has had no implementation in four years. Framing the work as fulfilling an existing NGC design rather than introducing a new architecture removes essentially all of the political risk of a large solo proposal, and the review has now confirmed this.

**Rejected.** A separately-named Legion-specific module. It would have fragmented the effort and forfeited the upstream path.

**Revisit if.** NGC changes direction on the charter.

---

## ADR-002 — Phase 0 (BinaryFormatter removal) is dropped from scope

**Status:** ACCEPTED

**Decision.** No BinaryFormatter work is planned. The build plan starts at what was Phase 1.

**Why.** Recon R1 confirms BinaryFormatter is fully removed on `develop` and the tree is on `net10.0`. YEngine refuses legacy blobs rather than deserializing them; FlotsamAssetCache uses a fixed-type XML format; the unsafe compatibility switch is gone from `Directory.Build.props`. Recon R10 additionally confirms Phlox uses protobuf-net with declared contract types and no `DynamicType`, so the engine that actually runs on Legion Grid was never exposed.

**Consequence.** The strongest single argument in the original analysis — "this is a security fix *and* the .NET 10 unblock" — no longer exists. The case for the module now rests entirely on access control and export enforcement.

**Revisit if.** A new unsafe deserializer is introduced, or a legacy-format migration path is later required.

---

## ADR-003 — Grid identity is an Ed25519 keypair; trust is TOFU plus explicit operator approval

**Status:** PROPOSED — this is the load-bearing decision and should be confirmed with Mike before implementation

**Decision.** Each grid holds an Ed25519 keypair. The public key, not the `HomeURI` string, is the grid's identity. First contact records the presented key against the URI and marks the grid pending at Open tier. An operator promotes it to Trusted out of band, after the agreement exists. A key change on an established relationship is a hard failure requiring re-approval.

**Why.** Recon R6 confirms `IsException()` compares the self-asserted `HomeURI` by string equality after trailing-slash normalisation. Since OpenSim ships `GatekeeperURIAlias`/`HomeURIAlias` precisely so grids can advertise multiple hostnames, any allow/deny list keyed on that string is defeated by a config edit on the other end. Trust must key on something the remote operator cannot cheaply rotate. Aliases then fall out naturally: many URIs, one key.

TOFU-plus-approval matches Mike's own framing that the agreements come first and software provides enforcement and audit. It requires no central authority and no organisation to run one.

**Rejected.**
- *Shared secret per grid pair.* Does not scale past a handful of partners and has no revocation story.
- *Signed community roster.* Better UX at scale, but needs someone to operate it and creates a gatekeeper of the gatekeepers. The data model should permit layering this on later; it is not v1.
- *Full PKI / CA.* Correct and disproportionate for a community of hobbyist operators.

**Revisit if.** The member set grows past roughly a dozen grids, at which point the signed roster becomes worth its operational cost.

---

## ADR-004 — Signatures ride on existing HG calls; no new wire protocol

**Status:** PROPOSED

**Decision.** Authentication is added as headers on the existing Hypergrid endpoints, implemented as a new `IServiceAuth` in `Source/OpenSim.Framework/ServiceAuth/` and registered in the `ServiceAuth` factory switch. No new endpoints, no new transport, no replacement of the XML-RPC calls.

**Why.** A new protocol guarantees permanent divergence from upstream and cannot be merged. Recon R8 confirms `IServiceAuth` is a small, clean interface with a single-`switch` factory, so adding one implementation is contained rather than invasive — and confirms no existing mechanism can be reused, since only `BasicHttpAuthentication` exists.

**Revisit if.** Upstream replaces the HG transport layer.

---

## ADR-005 — Unverifiable requests degrade to Open tier; interop never hard-fails

**Status:** ACCEPTED

**Decision.** A missing, malformed, or unverifiable signature is not an error. It classifies the caller as Open tier and is logged. Stock, unmodified OpenSim grids must continue to interoperate with no configuration change and no behavioural difference from today.

**Why.** This is the single rule that prevents the module from being read as an attempt to wall off the Hypergrid. It also handles version skew: grids upgrade at wildly different times, and a Tranquillity grid two releases behind must not be refused by one two releases ahead.

**Consequence.** Interop is a first-class acceptance criterion, not a regression check. A live connection to a stock grid — OSGrid or equivalent — must pass before any tier enforcement ships, and a ledger tracks the matrix.

**Revisit if.** Never, for the Open tier. Trusted-tier requirements may tighten freely.

---

## ADR-006 — Export-bit enforcement continues @llaxton's work rather than replacing it

**Status:** PROPOSED — blocked on contact with Laxton Consulting

**Decision.** The remaining Export-bit work extends the pattern established by PR #187 rather than reimplementing it. Scope to confirm before starting: `HGInventoryAccessModule.cs:214` (the `PostAsset` path), lines 560 and 574, and a per-request dimension on `HGAssetService`/`AssetPermissions`.

**Why.** Recon R3 found #187 already landed the inventory-transfer half on 26 July 2026, flipping the `OutboundPermission` default to `false` and wiring `PermissionMask.Export` into the transfer path, with in-code attribution. Recon R4 confirms the `PostAsset` path and the asset service are still open. Duplicating or contradicting a second contributor's active work in shared code is the fastest way to lose upstream goodwill.

**Rejected.** Treating the Export path as greenfield, which the original analysis assumed.

**Action required before this is ACCEPTED.** Ask @llaxton directly what remains in their scope.

**Revisit if.** Laxton Consulting completes the remaining paths, in which case this reduces to a verification pass.

---

## ADR-007 — The landmark export carve-out is preserved unconditionally

**Status:** ACCEPTED

**Decision.** `type == AssetType.Landmark` continues to bypass export restriction at `HGInventoryAccessModule.cs:214`, under every tier including Blocked.

**Why.** Landmarks are Hypergrid navigation, not content. Restricting them breaks the ability to travel, which is a functional break disguised as a security control.

**Revisit if.** Never, short of an upstream change to how HG addressing works.

---

## ADR-008 — The module claims accountability, not prevention

**Status:** ACCEPTED

**Decision.** All documentation, configuration comments, console output, and public messaging state plainly that this raises the cost of misbehaviour and creates attribution and revocation — and that it does not prevent copying. A modified viewer defeats all server-side control for anything rendered on screen, and a Trusted grid's operator can do as they like with assets that legitimately reach their asset server.

**Why.** Overclaiming is how a security feature loses community trust in one forum thread, and it is how a hobbyist project acquires liability it does not want. Mike's charter is explicit that the TOS defines how a grid operates and software enforces agreements that already exist.

**Consequence.** The threat model — what is defended against and what explicitly is not — is a mandatory frozen section of the Design Brief, not a caveat added at announcement time.

**Revisit if.** Never.

---

## Open decisions not yet recorded

These need answers before the Design Brief can freeze:

- **Tier granularity.** Is tier evaluated per grid only, or per grid × per region? Region-level policy already exists via `AuthorizationService`; whether tiers compose with it or subsume it is undecided.
- **Presence-oracle gating (Recon R7).** Which of `locate_user`, `get_uui`, `get_uuid`, `get_server_urls`, `status_notification`, `get_online_friends` move behind tier policy, and what breaks for stock grids when they do. Blocked on Balpien's requirements.
- **Config surface shape.** Whether tier policy lives in `Robust.HG.ini` sections, in the database alongside the trust registry, or both.
- **Legion tree divergence.** Whether Legion's HG paths differ from upstream, given the vendored LibOMV 1.2.13 versus the top-level NuGet `OpenMetaverse` package upstream adopted in `07006d9`.
