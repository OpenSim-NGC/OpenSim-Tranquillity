# Reference results for the bake compositor

71 vectors, captured 2026-10-14 by running the current compositor as a black box (upstream/develop `1ab87a277b`,
the previous compositor's bake method and `Raster` called directly, no other code involved). They record behaviour, not
code. A rewrite must reproduce each output byte for byte; `Docs/BAKE-COMPOSITOR-SPEC.md` (SPEC.md in code comments) is the description they test.

They were produced by a separate harness (not part of this folder and not part of any repository) that writes the vectors (`gen`) and recomputes and compares them (`verify`). Two runs (`gen`, then `verify`) agreed on all 71.

## Layout of one bake vector (folders a01 … i06c)

- `manifest.json`: the inputs and the result summary.
  - `channel`, `size` (N, square), `overlayParams` (caller-supplied values, may be null).
  - `wearables`, in wear order: `kind`, `label`, `params` (stored values), and `textures`: per slot, the slot name, `generator` (how it was made, below), `width`, `height`, `hasAlpha`, `file`, and `textureId` (`ordinary`, `assetless` = id with no pixels, `invisible-texture-id` = the invisible texture id with no pixels).
  - `result`: `invisible`, `nothingDrawn`, `rgbaSha256`, `morphSha256`, and `layerStatus` (diagnostic; the status words are informative, the detail strings are free text and are not compared).
- `tex-<wearable label>-<slot>.rgba`: an input texture, width x height x 4 bytes, interleaved R, G, B, A, row 0 at the top. When `hasAlpha` is false the A bytes are 255 and the texture is to be treated as having no alpha channel.
- `out.rgba`: the canvas, N x N x 4 bytes, interleaved R, G, B, A, row 0 at the top.
- `out.morph`: the morph mask, N x N bytes.
- i05 stores hashes only (`out.rgba` would be 4 MB); the hash is SHA-256 of the same byte layout.

Parameter ids are the ids of `avatar_lad.xml`; values are floats in each parameter's native range.

### Texture generators (width w, height h; dx = max(1, w-1), dy = max(1, h-1); x, y are pixel coordinates, row-major)

All colour planes are bytes; products are integer divisions (truncating).

| generator | R | G | B | A |
|---|---|---|---|---|
| `grad` | x*255/dx | y*255/dy | (x*7 + y*13) mod 256 | none |
| `gradA` | as grad | as grad | as grad | (x+y)*255/max(1, w+h-2) |
| `checkA` | 200 | 100 + x*100/dx | 50 | 255 where (x/blk + y/blk) is even, else 0, blk = max(1, w/8) |
| `halfA` | as grad | as grad | as grad | 255 where x < w/2, else 100 |
| `bandA` | as grad | as grad | as grad | by y*3/h: 0 gives 255, 1 gives 128, 2 gives 0 |
| `solidA:r,g,b,a` | r | g | b | a |
| `alpha0` | 255 | 0 | 255 | 0 |
| `none` | no pixels, but the slot has a texture id | | | |
| `invisibleId` | no pixels, texture id is the invisible one | | | |

(`rampA`/`solid` exist in the generator but no vector uses them.)

## The raw vectors

- `j01-processalpha-ramps`: `out.bin` holds, for each domain (outer loop: 0, 0.01, 0.05, 0.2, 0.5, 1, 1.5) and each weight (inner: 0, 0.1, 0.25, 0.5, 0.75, 0.9, 1, -0.2, 1.3), 256 bytes: the ramp of SPEC 8.3 applied to the grey values 0..255 in order.
- `j02-multiply-table`: `out.bin[a*256 + b]` = byte product of a and b (SPEC section 1).
- `j03-resample`: 16 single-channel cases; `rs-NN-in.bin` (source, row-major) and `rs-NN-out.bin`; sizes in `manifest.json`. Input byte at (x, y) = (x*37 + y*91 + (x*y mod 17)) mod 256.

## Which vector covers which rule

| Rule (SPEC) | Vectors |
|---|---|
| R01 layer set, canvas start, size | a01, a02, d01, g01, g04, i01 to i05 |
| R02 layer order, bump layers never drawn, visibility layers last | a01, b11, c02, d01, b13 |
| R03 one draw per worn wearable of the type; none worn; assetless wearable | a03, a11, a12, b06, b07, b07b, b13, b14, c01, c02, d03, e05, f03, g02, g03 |
| R04 layers without a local texture drawn once with merged values | a01, d01, g01, g04 |
| R05 net colour: global, fixed, colour parameters, add/multiply/blend, clamp | a04, a05, a06b, a08, a11, b04, b04b, b05, b11, c02, d02, e02, f02 |
| R06 colour stops by weight | a04, a05, a08, d02, e02 |
| R07 global colours (skin, hair, eye) | a01, a04, a08, d02, e02 |
| R08 colour alpha about 0 skips the layer | a01, a05, a05b, b05, c03 |
| R09 alpha masks: ramp, skip_if_zero, owner not worn, additive vs multiply, all skipped | a05, a05b, a06, a07, a08, b01, b02, b03 (4 weights), b03e, c01, e04, f02, i06 to i06c |
| R10 mask times texture alpha, mask file, colour alpha | a05, a06b, b04b, b05, b06, h03 |
| R11 drawing: tint, blend, flat fill, bundled image, replace mode, rounding | a03, a05, a11, b02, b04, d02, e02, e03, f02, g01 |
| R12 parameter resolution: ownership, clamp, drivers, overlay, per-instance, sex | a04, a06, a07, b03, b03e, b05, b06, b08, b09, b10, c01, h06 |
| R13 visibility masks, invisible texture, final alpha, clear_alpha | a09, a09b, a09c, a10, b12, c04, d03, e02, e04 |
| R14 morph mask | a06, a06b, a07, b05, b06, b07, b07b, c03, h01 to h04, h06 |
| R15 resampling and bake sizes | i01 to i05, j03, b02 (64 from 64), a03 |
| R16 ramp arithmetic and weight rounding | j01, i06, i06b, i06c |
| R17 byte product, blend rounding | j02, every bake vector |
| R18 NothingDrawn, Invisible | e01 (nothing drawn), f01 (nothing drawn), e03 (bald hair: drawn), a10 (invisible) |

Checks I made on the results: i06 and i06b (weights 0.5039 and 0.5019, one 1/255 step) have identical output
hashes and i06c (0.5059) differs; a07 (female, facial hair values set) has a morph mask of 0 everywhere while a06
(male) peaks at 30 and a06b at 142 (colour alpha scaling); a01 has `NothingDrawn` false (base images drawn) and e01
and f01 have it true.

## What is not covered, and why

- Decoding of JPEG 2000 input and encoding of the output: not part of the compositor; the vectors start from decoded planes.
- Report detail strings and the exact set of `skipped` or `mask` report entries beyond the status word: diagnostic only.
- Parameters in the data that no bake layer reads, and `render_pass="bump"` layers (never drawn).
- A `param_alpha` without an image file in a colour layer (the shipped data has none outside bump layers).
- Bake sizes above 1024 other than the hash-only i05, and non-square textures other than 80x48 (i04) and the j03 cases.

The coverage table above is by design (which rule each scenario was built to exercise). I did not mutation-test
the vectors, so a rule listed against a vector is intended coverage, not proven coverage, apart from the three
checks named above.
