# Server-side baking: the viewer contract

The rules the stock Linden Lab viewer follows for server-side baking. Every SSB design choice depends on them
(`ADR-SET-ssb-appearance.md`, ADR-001). Code comments cite them as V1 to V7, so the numbers do not change.

## 1. Scope

This file covers what the viewer does on the wire. How the region implements it is in
`DESIGN-BRIEF-ssb-appearance.md`.

## 2. Before SSB

Without SSB, a Tranquillity region:
- leaves bit 0 of `RegionProtocols` clear;
- has no `UpdateAvatarAppearance` capability;
- sends `AvatarAppearance` with no `AppearanceData` block;
- puts no `agent_appearance_service` in the login response.

The UDP client-bake path (`AgentSetAppearance`, `UploadBakedTexture`) is what Firestorm uses. It is unchanged
with SSB on or off.

## 3. Viewer contract

Read from the LL viewer source at commit `62033f2`.

| # | Rule | Evidence |
|---|---|---|
| V1 | The viewer uses server baking if and only if `RegionHandshake.RegionProtocols & 1` | `llviewerregion.cpp:3097` |
| V2 | The client-side bake path (`UploadBakedTexture` → `AgentSetAppearance`) has **no callers**; the code is kept but dead | `sendAppearanceMessage` appears only at its definition |
| V3 | After every Current Outfit change the viewer POSTs `UpdateAvatarAppearance` with `{cof_version}` and expects `{success, expected, error}` | `llappearancemgr.cpp:2572, 3865–3882`, `requestServerAppearanceUpdateCoro` |
| V4 | The viewer **drops its own** `AvatarAppearance` as "Stale appearance" unless `AppearanceData.CofVersion` is greater than the last one received; a message with **no** `AppearanceData` block is rejected for self | `llvoavatar.cpp:9779–9800` |
| V5 | `AppearanceVersion` is forced to 1 when the server bakes | `llvoavatar.cpp:9727–9737` |
| V6 | Other avatars' bakes are fetched from `<agent_appearance_service>texture/<avatar>/<channel>/<uuid>`; with an empty service URL the viewer logs a warning and the avatar is never textured | `LLVOAvatar::getImageURL` |
| V7 | Outfit *changes* (wear, take off, replace outfit, empty Trash) go only through AIS in the LL viewer | `AIS-V3-SPEC.md` §1g |

**Consequence of V7.** SSB without AIS gives the LL viewer "log in as yourself, but cannot change clothes".

## 4. Persistence rule

The rule that bakes persist comes from Halcyon:
- Bakes are persisted assets tied to the avatar record.
- A login does not force a rebake when the stored bakes match the stored wearables.
- The invalidation key is a hash of each channel's inputs.

Halcyon still had the viewer composite. Here the compositing moves to the region, so only the persistence rule
comes from Halcyon. No Halcyon wire message is used: the contract in §3 is the specification.
