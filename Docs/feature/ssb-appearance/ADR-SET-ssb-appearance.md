# Design decisions: server-side baking

These are the decisions behind server-side baking (SSB), for maintainers. The operator description is in
`DESIGN-BRIEF-ssb-appearance.md`, and the viewer rules V1 to V7 are in `RECON-ssb-appearance-addendum.md` §3.
Code comments cite these records by number, so the numbers do not change.

---

## ADR-001: The viewer contract is the stock LL viewer; changes are add-only

**Decision.** The viewer rules in `RECON-ssb-appearance-addendum.md` §3 (V1 to V7) are the specification. No UDP
appearance handler is removed or changed in behaviour: `AgentSetAppearance`, `UploadBakedTexture`,
`AgentCachedTexture` and `AvatarNowWearing` all stay. All new behaviour is gated per region by
`[Appearance] ServerSideBaking`.

**Consequences.**
- Firestorm's client-side baking keeps working on regions with the flag off.
- `SendAppearance` has two forms, with and without the `AppearanceData` block. The form is chosen per avatar,
  by whether this region baked it.

---

## ADR-002: Bakes are composited in the region; Robust serves them

**Decision.** Compositing runs in the region module that owns the ScenePresence, which already has the visual
params, the texture entry and the appearance sender. The read path viewers use for other avatars' bakes is a
Robust HTTP handler, `AppearanceServerConnector`. It serves `texture/<agent>/<channel>/<uuid>`, resolves the
channel to the stored asset through the avatar service, and streams the asset.

**Rejected.**
- *Baking on Robust.* It needs a second copy of appearance state and a new region-to-Robust RPC.
- *Serving bakes from the region's own HTTP server.* The URL in the login response must stay stable across
  teleports.

**Consequence.** Compositing sits behind the `IBakeBackend` seam, so it could move out of process later
without changing the module.

---

## ADR-003: The compositor is a separate library in the tree

**Decision.** The compositor is its own project, `Source/OpenSimNGC.Appearance.Baking/`, targeting the tree's
framework. Its dependencies are SkiaSharp and the JPEG 2000 codec the tree already uses. Other consumers can
reference the same library instead of carrying a copy.

**Open.** Whether to also publish it as an NGC package is a decision for the NGC maintainers.

---

## ADR-004: Bakes are assets, indexed in the avatar service, superseded at once

**Decision.**
- Bakes are stored through `IAssetService` as texture assets named `bake:<agent>:<channel>`.
- The index lives in the avatar service's key/value rows: `Bake:<channel>`, `BakeHash:<channel>`,
  `BakeCOFVersion`, `BakeSize` and `BakeUpdated`.
- When a new bake for a channel is confirmed stored and its face has moved to it, the previous asset is
  deleted (supersede). An asset that any baked face still points at is never deleted.
- `AssetFlags.Collectable` is not used as a marker: stock code paths treat it as temporary.

**Why.** The key/value avatar table already expresses the index, so no new table and no migration are needed.
The index is read and written through the existing `IAvatarService` calls (`GetAvatar`, `SetItems`,
`RemoveItems`), so no service change is needed either.

**As built.** The hash skip is per channel and runs before any texture is fetched. A channel is reused only when
the stored hash matches **and** the stored asset still resolves.

**Not built.** An expiry reaper that would use `BakeUpdated` to remove bakes of avatars that have not returned.

**Watch out.** Any appearance save rewrites the avatar's record (`AvatarService.SetAvatar` deletes every row for
the agent first), which wipes the index. That is why bakes run only after a save completes
(`DESIGN-BRIEF-ssb-appearance.md` §4.6).

---

## ADR-005: Fidelity policy: bake what can be baked, refuse only corrupt input

**Decision.** On a region that bakes, the region is the only baker an LL viewer has, so refusing a bake means a
permanent cloud. Unsupported layers are skipped and reported, and each channel succeeds or fails on its own.
The one whole-bake refusal is input the backend cannot parse: then every channel keeps its existing face and
nothing is overwritten.

**Consequence.** Firestorm users on an SSB region get the region's best-effort bake instead of their own.

---

## ADR-006: The COF version comes from the inventory folder

**Decision.** The region reads the Current Outfit folder's `Version` from the inventory service and uses it as
`cof_version`. AIS v3 updates the same field, so SSB does not depend on AIS. The handshake is
`DESIGN-BRIEF-ssb-appearance.md` §4.3.

---

## ADR-007: Viewer data files ship with the library

**Decision.** The library embeds `avatar_lad.xml` and the parameter-mask and base-image TGAs that its
`layer_set` definitions name. Each file is a byte-for-byte copy from the LL viewer tree. Origin, viewer version
and SHA-256 are recorded in `Source/OpenSimNGC.Appearance.Baking/THIRD-PARTY-NOTICES.md`. A file that is not in
the viewer tree is not vendored.

**Why.** A compositor must not change behaviour because a client library was updated. The region does not
otherwise depend on such a library.

---

## ADR-008: Bake size 1024, configurable

**Decision.** The default is 1024 pixels per channel. `[Appearance] BakeSize` accepts 512, 1024 or 2048, and the
size is part of the input hash, so changing it invalidates stored bakes at the next bake instead of serving
mixed sizes.

**Why configurable.** 512 gives smaller assets at lower detail, and 2048 larger assets. The default sits between
them.

---

## ADR-009: Other clients consume the region's bakes

**Decision.** A client that can bake for itself, such as a web viewer gateway, must stop baking on a region that
sets `RegionProtocols` bit 0. It takes the region's `AvatarAppearance` and fetches bakes through the asset
route or the appearance service instead. Detection is per region and is re-evaluated on every teleport, as the
LL viewer does (`llviewerregion.cpp`, `region_protocols & 1`).

**Why.** Otherwise two bakers write to the same avatar record.
