# AIS v3 + Server-Side Baking on NGC-Tranquillity — fact sheet

*Prepared 2026-09-03 for Mike. Sources: ten-pass SL-parity audit of the tree at `645b0f3` against the LL viewer at `62033f2`; web-viewer sessions 11–12.*

## Why both, why now
The current LL viewer cannot function on Tranquillity today. Two of the four reasons are these: it **only** bakes server-side (client-bake code is retained but has no callers), and its outfit changes, item/folder deletes, and Empty Trash are **AIS-only** (they no-op with a log warning otherwise). Firestorm masks both. SSB makes an avatar visible; AIS makes it changeable. One without the other is half a viewer.

## SSB — what it is on the wire
- Viewer decides per region from `RegionHandshake.RegionProtocols & 1`.
- After every outfit change it POSTs `UpdateAvatarAppearance {cof_version}`; expects `{success, expected, error}`.
- Sim composites the 11 bake channels (6 legacy + 5 Bakes-on-Mesh), stores them, and sends `AvatarAppearance` **with** an `AppearanceData{AppearanceVersion=1, CofVersion}` block — the viewer drops its own appearance as stale without it.
- Other avatars' bakes are fetched from `agent_appearance_service` (login response) at `texture/<agent>/<channel>/<uuid>`.

**Where we stand:** a faithful C#/.NET 10 compositor already exists — built for the web-viewer gateway, driven by the viewer's own `avatar_lad.xml` with `LLTexLayerSet` semantics, tested against Firestorm's bakes. The viewer C++ (`lltexlayer.cpp`, `llavatarappearance.cpp`) was the template, as you suggested; we did **not** wrap LL's GL-based `appearance-utility-bin` (x86 Linux + Xvfb, not a fit). LibreMetaverse's baker is disqualified (decompile: tiles sub-1024 layers into a 2×2 mosaic; skips layers — two upstream bugs reported). Plan is to lift the compositor into a shared library (`OpenSimNGC.Appearance.Baking`, proposed NuGet — your call) used by both the sim and the gateway. The remaining SSB work is plumbing: cap, `AppearanceData`, per-region flag, persistence with expiry, Robust appearance service.

**On your two benefits:** fewer transfers — yes, 11 bakes replace every wearable texture per avatar per observer. Raw-texture protection — **only partly** on OpenSim as-is: `GetTexture` serves any asset UUID, so a client that learns a skin's UUID can still fetch it. Making SSB actually protective needs one more slice: on bit-0 regions, refuse `GetTexture` for wearable-referenced textures to anyone but the owner (SL's behaviour). Filed as a follow-up, not in the first build.

## AIS v3 — what makes it "janky"
You're right that it isn't plain REST. The parts that carry the effort:
- **HAL-style envelopes** with `_embedded{categories,items,links}` (links are a separate collection, not items) and, on mutations, delta sets (`_updated_items`, `_created_items`, `_removed_items`, `_updated_categories`…) that the viewer applies directly to its local model — wrong deltas = silently divergent inventory.
- **Per-operation folder-version bump rules** (which folders a slam, a link create, a move must bump) and echo of a client `tid`.
- **SlamFolder** (`PUT /category/<id>/links`) — atomic replace-all-links; done non-atomically, a mid-way failure strips the avatar's COF.
- `simulate` dry-run, `COPY` verb for library copy, `/category/current` alias, `/orphans`.
- **All-or-nothing:** once `InventoryAPIv3` is advertised the viewer routes *all* inventory through it, fetches included. A partial AIS is worse than none.

**Where we stand:** `OpenSim.Services.AISv3` in the tree is the `dotnet new webapi` template (32-line weather controller). The full route/verb/envelope table is already extracted from `llaisapi.cpp` for the build. Hosting: Phase 1 as a region-side caps module translating to `IInventoryService` (auth free via the caps seed); behind an interface so Phase 2 can host the same handler on Robust with a tokenized URL — that's the "inventory out of the simulator" step, one later session, not a redesign.

## Rules that hold for both
- **Add-only.** No UDP handler (`AgentSetAppearance`, `UploadBakedTexture`, UDP inventory ops) is removed. Firestorm keeps working either way.
- **Per-region flags**, default off in every shipped ini (`[Appearance] ServerSideBaking`, `[AIS] Enabled`). One test region with both viewers before any other region flips.
- **Harness-defined done**: AIS — HTTP acceptance harness against fixture envelopes; SSB — pixel-diff against Firestorm's bakes of a stock-Library outfit.

## Size (Claude Code wall-clock, measured cadence)
| | CC time | Sessions |
|---|---|---|
| SSB | ~4.75 h | 9 |
| AIS | ~3.6 h | 6 |
| Both, interleaved, joint soak | **~8.5 h** | 15 |

About three working days at the cadence the web viewer ran at. Two questions where your input matters: publishing the compositor as an NGC NuGet package, and whether Phase-2 Robust hosting of AIS is the direction you want.
