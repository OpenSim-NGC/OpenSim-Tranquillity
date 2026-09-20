# S1b — The fidelity surface, measured against a second outfit

**Date:** 2026-09-03 **Branch:** `feature/ssb-appearance` **Closes/updates:** Ledger Q-11
**Sets:** `Source/OpenSimNGC.Appearance.Baking.Tests/Golden/truly-stock`, `.../aleric-max`

S1 shipped on one outfit. Ledger Q-11 recorded that this proved nothing about the layers that outfit never
touched. This session added a second reference set — Aleric Fenwood, Ebony, captured 2026-09-03 — and ran the
whole layer surface against it.

**Result in one line: one real defect, in the upper channel's morph mask, and it is a parity gap the stock
outfit could not have shown.** No layer returned UNSUPPORTED on either outfit.

---

## 1. What Aleric actually wears

From `aleric-max/fixtures/avatar.json` (the live `Avatars` rows), 10 worn slots:

| Type | Wearable | Index | Asset id | Parsed name |
|---|---|---|---|---|
| 0 | Shape | 0 | `8dca9b1a-9aeb-6580-c846-126d1cb5a280` | New Shape (82 params) |
| 1 | Skin | 0 | `045d0bb6-e055-6990-99e0-814936d03f9e` | Adam Skin (shaved) — 26 params, 3 textures |
| 2 | Hair | 0 | `e38631ab-a09a-3837-7965-59a485683402` | Professional Male 1-Hair base |
| 3 | Eyes | 0 | `cc5860c4-5651-0528-83ad-9dd95b25e9ee` | New Eyes |
| 4 | **Shirt** | 0 | `00000000-0000-0000-0000-000000000000` | **null asset id — slot worn but empty** |
| 5 | Pants | 0 | `5cc5ebc5-095e-328f-0435-13d2e0860531` | Professional Male 1-Pants |
| 6 | Shoes | 0 | `196eab6e-eae7-ca77-a752-0e97a2fcb7f3` | shoe base |
| 7 | **Socks** | 0 | `28cffb2a-557f-0cd8-5dee-089a915728ff` | mens black socks |
| 8 | **Jacket** | 0 | `ba106f90-f560-0ac3-868b-37088090b3a8` | Black Suede Blazer with blue shirt |
| 14 | **Tattoo** | 0 | `40294285-0ac8-3053-a459-acc66cc2101c` | Professional Male 1-Hair base |

253 visual-param bytes. 11 distinct textures referenced and fetched.

The Shirt row is worth its own line: the slot is **worn but carries the null asset id**. The orchestrator skips
it (`BakeOrchestrator.Resolve`, `assetId.IsZero()`), the harness skips it, and the compositor therefore reports
`upper_clothes skipped: no Shirt worn`. Section 4 argues this row may be the whole finding.

## 2. Q-11 coverage: what this outfit reached and what it did not

| Q-11 surface | Covered? | Evidence |
|---|---|---|
| **Tattoo** | **YES** | `head_tattoo drawn: Tattoo 40294285: HeadTattoo 512x512+alpha -> 1024`. `upper_tattoo` / `lower_tattoo` skipped: this tattoo wearable carries only a head texture. |
| **Socks** | **YES** | `lower_socks drawn: Socks 28cffb2a: LowerSocks 32x32 -> 1024; masks [Socks Length bump#1050=0.35+]` |
| **Jacket** | **YES** | `upper_jacket drawn` (5 param masks + texture alpha) and `lower_jacket drawn` (3) |
| Multi-wearables per slot | no | Every worn slot is index 0; no slot holds two wearables. The `LLTexLayerTemplate` multi-instance path (S0e) is still exercised only by the unit tests. |
| Universal | no | 6 layers report `no Universal worn` (`head_/upper_/lower_/eyes_/hair_universal_tattoo`, `*_tattoo` on eyes and hair). |
| Alpha | no | 5 layers report `no Alpha worn` (`head/upper/lower/eyes/hair alpha`). The `IMG_INVISIBLE` whole-region path is untouched. |
| Gloves | no | `upper_gloves skipped: no Gloves worn` |
| Skirt | no | No Skirt worn; the Skirt channel is not produced at all. |

**Three of the eight covered; five remain untested against live content.** The five BoM aux channels
(`leftarm`, `leftleg`, `aux1`, `aux2`, `aux3`) are also unexercised — they need a Universal wearable, which is
the same gap.

Q-11 therefore **narrows but does not close.** What it did buy: the first evidence that the compositor's
behaviour on a non-stock outfit is sound in RGB and alpha on every channel, and the discovery in §4.

## 3. The layer surface, all 11 channels

93 layers are declared across the 11 layer sets in `avatar_lad.xml`. 22 of them are `render_pass="bump"`
layers, which **the LL compositor never renders into a bake** (`lltexlayer.cpp:395`, S0d): they are listed
below as *not rendered by design*, not as gaps.

| Channel | Layers | Produced for Aleric? | Drawn | Skipped | Bump (by design) | UNSUPPORTED |
|---|---|---|---|---|---|---|
| head | 25 | yes | 5 | 15 | 5 | **0** |
| upper_body | 23 | yes | 4 | 11 | 8 | **0** |
| lower_body | 24 | yes | 7 | 8 | 9 | **0** |
| eyes | 4 | yes | 2 | 2 | 0 | **0** |
| hair | 4 | yes | 2 | 2 | 0 | **0** |
| skirt | 3 | **no — no Skirt worn** | — | — | — | untested |
| leftarm | 2 | **no — no Universal worn** | — | — | — | untested |
| leftleg | 2 | **no — no Universal worn** | — | — | — | untested |
| aux1 | 2 | **no — no Universal worn** | — | — | — | untested |
| aux2 | 2 | **no — no Universal worn** | — | — | — | untested |
| aux3 | 2 | **no — no Universal worn** | — | — | — | untested |

`unsupportedLayers=[]` on every produced channel, for both outfits. The compositor did not meet a mask file it
lacks, a resource it lacks, or a `local_texture` it does not know.

### Why each layer was skipped (the five produced channels)

Every skip falls into one of five reasons, none of which is a gap in the compositor:

| Reason | Count | Example |
|---|---|---|
| `no <type> worn` | 13 | `upper_gloves skipped: no Gloves worn` |
| `every mask parameter skipped` | 12 | `freckles skipped: every mask parameter skipped [Freckles#165=skip]` |
| `colour alpha 0` | 7 | `shadow skipped: colour alpha 0 (0.00,0.00,0.00,0.00)` |
| `no <slot> texture on the wearable` | 2 | `upper_tattoo skipped: Tattoo 40294285: no UpperTattoo texture on the wearable` |
| `no Shirt worn` (null-asset slot) | 3 | `upper_clothes skipped: no Shirt worn` — see §4 |

## 4. THE FINDING — upper channel morph mask, `upper_clothes`

**Channel:** `upper` (upper_body). **Layer:** `upper_clothes`, in its morph-mask role.
**Numbers, at all three bake sizes** (so this is structural, not a resampling artefact):

| Size | mean abs dM | pixels dM > 8 | threshold |
|---|---|---|---|
| 512 | **82.25** | **33.27%** | 4.0 / 5% |
| 1024 | **82.24** | **33.21%** | 4.0 / 5% |
| 2048 | **82.25** | **33.22%** | 4.0 / 5% |

The reference upper morph mask is a real mask — 31.4% of pixels near 0, 67.0% near 255. Ours is **uniform 255**:
we produce no upper morph mask at all. The compositor says why:

```
upper_clothes morph: no Shirt worn: mask left at 255
```

**The machinery is not at fault.** On the same avatar, the lower channel's morph mask reproduces the reference
*exactly* — identical histograms, 29.3% near 0 and 69.2% near 255 in both, mean abs difference 0.47:

```
lower_pants morph: Pants 5cc5ebc5: morph mask *= layer mask (mean 178.3)
```

So the S0d/S0e gather is right where a declared morph-mask layer is worn. What is wrong is that in this outfit
the upper morph mask should not have been empty. `avatar_lad.xml`'s `<morph_masks>` block names exactly one
upper_body layer, `upper_clothes` (for `Displace_Loose_Upperbody` and `Shirtsleeve_flair`), and that layer's
`local_texture` is the **Shirt** slot. Aleric wears no shirt asset — yet the LL viewer still produced a mask.

**Two candidate causes; the evidence does not yet separate them.**

1. *The gather should include `upper_jacket`.* The jacket is the only upper clothing worn, and it carries five
   param masks (`jacket Sleeve Length#1020`, `jacket Collar Front#1022`, `Collar Back#1024`,
   `bottom length upper#620`, `open upper#622`) that would produce a mask of roughly the observed shape.
   *Against it:* if the gather simply took every alpha-param layer, the lower channel would pick up
   `lower_socks` (`Socks Length bump#1050=0.35`) and `lower_shoes` (`Shoe Height#1052=0.10`) too, and our
   pants-only result would then differ from the reference. It does not — it matches exactly. So the viewer's
   gather is not "every layer", and any fix must explain that asymmetry.
2. *The null-asset Shirt row should still render `upper_clothes`.* Aleric's Shirt slot **is** worn, with the
   null asset id. If the viewer treats that as a present-but-blank shirt wearable, it would run the
   `upper_clothes` layer with the shirt's own param masks and produce a mask, while we skip the slot entirely.
   This explains the asymmetry with the lower channel for free.

Ruled out by measurement: the mask is **not** the jacket's texture alpha (that texture is 99.3% opaque, and
mean |refM − jacketAlpha| = 80.71), and **not** the bake's own alpha channel (mean |refM − refA| = 81.26).

**Impact.** The 5th component drives the viewer's *clothing morphs* — how the mesh body is displaced to sit
under loose clothing. A missing upper morph mask means an avatar wearing a jacket and no shirt would not get
the loose-upper-body displacement it gets on SL. It does not affect the visible texture: RGB and alpha for the
same channel are well inside threshold (mean abs RGB 0.78, 0.22% of pixels over 8, at 1024).

**Discriminating experiment** (not run here — it needs a code change and viewer-source access, neither in this
session's scope): re-bake Aleric's upper with (a) `upper_jacket` added to the gather and (b) a synthetic blank
Shirt wearable present, and see which reproduces the reference's 31.4%/67.0% histogram.

## 5. Everything else passes, on both outfits

At each set's manifest size, with S1's thresholds (mean abs RGB ≤ 4.0, pixels over 8 ≤ 5%, mean abs alpha ≤ 2.0,
mean abs morph ≤ 4.0):

| Set | Channel | meanAbsRGB | meanAbsA | pctRGB>8 | meanAbsM | verdict |
|---|---|---|---|---|---|---|
| truly-stock @512 | head | 1.37 | 0.64 | 1.35% | 0.00 | pass |
| | upper | 1.41 | 1.00 | 0.64% | 1.00 | pass |
| | lower | 1.36 | 1.00 | 0.63% | 1.00 | pass |
| | eyes | 0.36 | 1.00 | 0.00% | 1.00 | pass |
| | hair | 0.00 | 0.00 | 0.00% | 1.00 | pass (RGB skipped: reference alpha all zero) |
| aleric-max @1024 | head | 0.80 | 0.84 | 0.00% | 0.00 | pass |
| | upper | 0.78 | 1.00 | 0.22% | **82.24** | **FAIL on morph mask** (§4) |
| | lower | 0.89 | 1.00 | 0.04% | 0.47 | pass |
| | eyes | 0.45 | 1.00 | 0.48% | 1.00 | pass |
| | hair | 0.33 | 0.53 | 0.00% | 1.00 | pass |

The richer outfit is, if anything, **closer** in RGB than the stock one (0.78–0.89 against 1.36–1.41). Nothing
about jacket, socks or tattoo compositing degrades the visible bake.

## 6. Bake size (S1b Part 2) — evidence for ADR-008

References as captured: head, upper, lower and hair are **2048×2048**; **eyes is 512×512** on both avatars.

Fidelity against bake size (mean abs RGB; both images resampled to the compared size):

| Set | Channel | 512 | 1024 | 2048 |
|---|---|---|---|---|
| truly-stock | head | 1.37 | 0.96 | 0.97 |
| | upper | 1.41 | 1.25 | 1.26 |
| | lower | 1.36 | 1.11 | 1.19 |
| | eyes | 0.36 | 0.32 | 0.33 |
| aleric-max | head | 1.21 | 0.80 | 0.82 |
| | upper | 1.07 | 0.78 | 0.84 |
| | lower | **2.29** | 0.89 | 0.94 |
| | eyes | 0.42 | 0.45 | 0.46 |
| | hair | 0.47 | 0.33 | 0.21 |

Encoded bytes, all five channels summed:

| Set | 512 | 1024 | 2048 |
|---|---|---|---|
| truly-stock | 213,431 | 310,333 | 574,645 |
| aleric-max | 227,143 | 347,324 | 634,332 |

**Reading: 1024 is the knee.** Every channel improves from 512 to 1024 — and the richer outfit improves
dramatically (aleric lower 2.29 → 0.89, the single worst number in the whole matrix, and it is at 512). Going
1024 → 2048 buys nothing: five of the nine channel rows get *worse*, none improves materially, and the cost
rises ~1.8×. 512 costs ~0.69× of 1024, which is not much of a saving for a visible loss on a busy outfit.

This is evidence, not a decision: **ADR-008 is not changed here.** But note the inconsistency it should
settle — ADR-008 and Ledger D-7 record the default as **512**, while S1 shipped
`[Appearance] BakeSize = 1024` in `OpenSimDefaults.ini` and the live sim is running 1024. The measurements
support 1024; the ADR should be updated to match reality rather than the ini changed to match the ADR.

## 7. Harness changes

`Golden/` now holds one subdirectory per reference set, each with its own committed `manifest.json` and
gitignored `fixtures/`. `fetch-fixtures.sh <set>` populates one set, reading the avatar name from that set's
manifest, with the S0b fallbacks unchanged (Robust first, then the region's Flotsam cache; the source is
reported per UUID). `GoldenTests` is a `[Theory]` over the sets present, plus `bake_size_sweep`, which reports
§6's numbers and asserts only that every channel encodes at the size asked for.

For `aleric-max`: all 9 non-null wearables and all 11 textures came from **Robust**; all 5 reference bakes came
from the **region cache** (bakes are temporary assets — Robust 404s them). The fetch script now skips a worn
slot carrying the null asset id, as the orchestrator does, and says so in its table.
