# Bake compositor: behaviour description

Purpose: a plain description of what the server-side avatar bake compositor does, detailed enough that a
developer who has seen neither the Second Life viewer source nor our current code can write a new
implementation that reproduces the reference results in `vectors/`.

This is prose. It describes outcomes, not how any existing code is written. Names of XML elements and
attributes are the data format of `avatar_lad.xml` and are used as they appear there. Rule ids (R01 to R18) tie
sections to the test vectors; `vectors/README.md` says which vector covers which rule.

Where the Second Life wiki documents the concepts, it is cited. It describes wearables, baked textures and
alpha masks in general terms only:

- Avatar Appearance: <https://wiki.secondlife.com/wiki/Avatar_Appearance> (wearables are parameter changes plus textures; baked textures are rebuilt when a wearable changes)
- Alpha masks: <https://wiki.secondlife.com/wiki/Viewerhelp:Appearance_Editor_-_Alpha> (an alpha wearable's texture hides the region where it is transparent)
- Avatar shape XML: <https://wiki.secondlife.com/wiki/Mesh/Avatar_Shape_XML_Format>

The pixel rules below are not on those pages. They were established by reading the viewer's compositing
behaviour and by running the current compositor as a black box. Where the black box and the viewer differ, the
black box is what the vectors record, and Section 15 lists those places.

---

## 1. Conventions

- Every image is stored as planes of 8-bit values, row-major, row 0 at the top. An image has either three colour planes or three colour planes plus alpha; an image "without alpha" is treated as alpha 255 everywhere.
- "Byte product" of a and b (both 0..255) means the integer `(a*b + 127) / 255` with integer division. 255 is the identity. This is used wherever two masks or a mask and a colour are combined.
- Colours are four floats in 0..1 (red, green, blue, alpha). Byte colours in the data (`"r,g,b,a"`, each 0..255) become floats by dividing by 255; a missing fourth component means 255.
- "Round" applied to a float means round to nearest, ties to even. "Truncate" means toward zero.
- Floating point maths is single precision (32-bit). The reference outputs were produced that way; double precision may flip individual pixels.
- "N" is the bake size: the requested edge length in pixels. Every output is N by N.

## 2. Inputs and outputs

### Inputs

1. A bake channel: one of head, upper, lower, eyes, hair, skirt, left arm, left leg, aux 1, aux 2, aux 3. It selects a `layer_set` in `avatar_lad.xml` by `body_region`: head, upper_body, lower_body, eyes, hair, skirt, leftarm, leftleg, aux1, aux2, aux3. If the data has no such set, the bake fails with an error.
2. The bake size N. The data's own `width` and `height` on a `layer_set` (2048, or 512 for eyes) are ignored; the output is always N by N, square, for every channel.
3. The worn wearables, in wear order (a wearable later in the list is "on top" of an earlier one of the same type). Each has:
   - a type: shape, skin, hair, eyes, shirt, pants, shoes, socks, jacket, gloves, undershirt, underpants, skirt, alpha, tattoo, physics, universal (names as in the `wearable` attribute of `param`);
   - a label (diagnostic only);
   - stored parameter values: a map from parameter id to float;
   - a texture id per texture slot, and for some slots decoded pixels (width, height, three colour planes, and optionally alpha). A slot can have an id but no pixels, and a wearable can have no entry for a slot at all. Both mean "no texture to draw", but only the id matters for the invisibility rule (Section 11).
4. Optional caller-supplied parameter values (a map id to float). Their use is in Section 4.
5. The bundled data: `avatar_lad.xml` and the 56 greyscale and colour TGA images it names (Section 9).

### Outputs

- The canvas: N by N, four planes (red, green, blue, alpha).
- The morph mask: N by N, one plane.
- Two flags: `Invisible` (an alpha wearable hid the whole region) and `NothingDrawn` (no layer drew anything, Section 13).
- A per-layer report (layer name, status, free-text detail, wearable type). The status words are `drawn`, `skipped`, `invisible`, `mask`, `morph`. The detail text is diagnostic and is not part of the behaviour.

## 3. The data: what is read from avatar_lad.xml

The file is read as ordinary XML. Element and attribute names are matched exactly (lower case). Boolean
attributes are true when their text, ignoring case and surrounding space, is `true`, `1` or `yes`; any other text
(and a missing attribute) uses the default stated.

### 3.1 Parameters (`param`)

Every `param` element anywhere in the file is read, including those nested inside `layer` and `global_color`
elements. A parameter id appears in several places; the occurrences are merged:

- The first occurrence fixes `name`, `group`, `value_min` (default 0), `value_max` (default 1), `value_default` (default 0).
- `wearable` is the first non-empty value seen. `sex` is `male`, `female` (either case) or absent (applies to both); a later occurrence can supply it only if no earlier one did.
- `param_alpha` (child): `tga_file` (default empty), `domain` (default 0), `skip_if_zero` (default false), `multiply_blend` (default false). If several occurrences have a `param_alpha`, the last one read wins.
- `param_color` (child): `operation` is `multiply`, `blend` or anything else/absent meaning add. Its `value` children each have a `color` attribute and, in order, are the colour stops. The last occurrence with a `param_color` wins.
- `param_driver` (child): its `driven` children (`id`, `min1`, `max1`, `max2`, `min2`) are collected over all occurrences; a driven id already collected is not added again. Defaults: `min1` is the driver's own minimum, `max1` the driver's own maximum, `max2` equals `max1`, `min2` equals `max1`.

A layer's own `param` child decides whether the parameter goes in the layer's alpha list or colour list (by whether
that child element itself has a `param_alpha` or `param_color` child). Everything else about the parameter (file,
domain, stops, ranges, owner) comes from the merged record. In the shipped data every parameter that a colour
layer or global colour uses is defined exactly once, so merging and ordering questions do not arise there. A
parameter whose `wearable` is absent never matches a wearable type, so no stored value is accepted for it.

### 3.2 Global colours

Each `global_color` element has a `name` and `param` children whose `id`s, in order, are the parameters that are
folded into that colour (Section 7.2). In the shipped data the names are `skin_color`, `hair_color`, `eye_color`.

### 3.3 Layer sets

Each `layer_set` has `body_region`, `clear_alpha` (default true), and optionally `alpha_tga_file`. If two sets name
the same region, the later one wins. Its `layer` children, in file order, are the layers. A layer has:

- `name`;
- `global_color` (a global colour name), `fixed_color` (byte colour, default fully transparent black);
- `render_pass`: the value `bump` (any case) marks a bump layer; any other value, or none, is a colour layer;
- `write_all_channels` (default false) and `visibility_mask` (default false);
- `texture` children. A `texture` with `tga_file` sets the layer's bundled image (and its `file_is_mask`, default false). Otherwise a `texture` with `local_texture` names the wearable slot the layer draws (and its `local_texture_alpha_only`, default false). The two kinds are independent: a layer can have one of each. If there are several of one kind, the last wins.
- `param` children: one that has a `param_alpha` child goes in the layer's alpha list, otherwise one that has a `param_color` child goes in its colour list; others are ignored. Both lists keep file order.

### 3.4 Morph mask list

The `morph_masks` block holds `mask` elements with `body_region` and `layer`. For each region it yields the set of
layer names (compared ignoring case) that take part in the morph mask (Section 10). Entries missing either
attribute are ignored.

### 3.5 Slot names

A layer's `local_texture` name maps to a texture slot and each slot belongs to one wearable type:

| local_texture | wearable type |
|---|---|
| head_bodypaint, upper_bodypaint, lower_bodypaint | skin |
| upper_shirt | shirt |
| lower_pants | pants |
| eyes_iris | eyes |
| hair_grain | hair |
| lower_shoes | shoes |
| lower_socks | socks |
| upper_jacket, lower_jacket | jacket |
| upper_gloves | gloves |
| upper_undershirt | undershirt |
| lower_underpants | underpants |
| skirt | skirt |
| lower_alpha, upper_alpha, head_alpha, eyes_alpha, hair_alpha | alpha |
| head_tattoo, upper_tattoo, lower_tattoo | tattoo |
| head_universal_tattoo, upper_universal_tattoo, lower_universal_tattoo, skirt_tattoo, hair_tattoo, eyes_tattoo, leftarm_tattoo, leftleg_tattoo, aux1_tattoo, aux2_tattoo, aux3_tattoo | universal |

Names are matched ignoring case. A name not in this table makes the layer "skipped" (report only).

## 4. Parameter values (R12)

### 4.1 Which stored values count

A wearable's stored value for id X is accepted only if X exists in the data and the data's `wearable` for X is the
wearable's own type. Anything else is ignored (a skin wearable that carries a shirt's value contributes nothing).
An accepted value is clamped to the parameter's `value_min` .. `value_max`.

### 4.2 Merged view

Walk the wearables in wear order and store each accepted value in one map ("direct"); a later wearable overwrites
an earlier one for the same id, which therefore keeps the position it first had in the map. Then add the
caller-supplied values for ids that are in the data and are not yet in the map (clamped likewise). A stored value
of a worn wearable therefore always beats a caller-supplied one.

### 4.3 Drivers

A parameter with `driven` entries sets other parameters from its own value. Starting from every id in the merged
map (in the map's order), repeat up to three rounds: for each id that has `driven` entries, take its value (direct
or already derived), and for each driven id that is defined in the data and is in neither the direct nor the
derived set, derive a value and record it; the newly derived ids form the next round. The first driver that
reaches a driven id wins. Derived values are never written over direct ones. Details that matter:

- A driver only fires if its own id is in the merged map or was derived in an earlier round. A driver that nobody stored and nothing derived does not fire from its default value.
- Round one visits the merged map's ids in the map's order; rounds two and three visit only the ids derived in the round before, in the order they were derived.
- The value a driver passes on is its stored (already clamped) or derived value.
- Comparisons in the mapping below are plain float comparisons. When `max2` equals `min2` the fourth case is an empty range.

The derived value is a trapezoid mapping. Let the driver's range be Dlow..Dhigh, the entry's four numbers min1, max1,
max2, min2, the driven parameter's range L..H, and `v` the driver's value:

- `v <= min1`: H if (min1 equals max1 and min1 is at or below Dlow), otherwise L.
- `min1 < v <= max1`: L + (v - min1) / (max1 - min1) * (H - L).
- `max1 < v <= max2`: H.
- `max2 < v <= min2`: H + (v - max2) / (min2 - max2) * (L - H).
- `v > min2`: H if max2 is at or above Dhigh, otherwise L.

### 4.4 Per-wearable view

Each wearable also gets its own map: its accepted stored values, then the drivers applied to those values alone
(same rule, same three rounds), with derived values added only for ids the wearable does not itself store. Derived ids of any type are kept in the map, but lookups (4.5) consult it only for ids owned by the wearable's own type.
Caller-supplied values are not part of it.

### 4.5 Looking a value up (the "weight" of a parameter)

When a layer is being drawn for a particular wearable instance (Section 6), a parameter owned by that
wearable's type is first looked up in that wearable's own map. If it has an entry, the weight is that value
clamped to the parameter's range. Otherwise, and for every parameter when no instance is current, the weight is the
merged direct value, else the merged derived value (clamped), else the parameter's `value_default`. An id not in
the data has weight 0. Weights are limited only to the parameter's own range, never to 0..1. A colour parameter whose owner type is not worn still counts, with its default or derived weight.

### 4.6 Sex

The avatar is male when the weight of parameter 80 (looked up with no instance) is above 0.5. A parameter with
`sex="male"` applies to a male avatar, `sex="female"` to a female one, none to both. The "effective weight" is the
weight when the parameter applies and the parameter's `value_default` when it does not.

### 4.7 Skipping an alpha parameter

An alpha parameter is skipped when either (a) it has `skip_if_zero` and the absolute value of its effective
weight is below 0.000001, or (b) its `wearable` names a type and nobody of that type is worn.

## 5. The canvas and the layer loop (R01 to R04)

1. The canvas starts with red, green and blue 0 and alpha 255 everywhere.
2. Invisibility check (Section 11): if it triggers, the bake ends there.
3. Layers are processed in file order. Bump layers are never drawn and never take part in anything (they exist
   for the viewer's own shading and are not part of a bake). Visibility-mask layers are not drawn here; they act
   on the alpha at the end (Section 11).
4. A layer with a `local_texture`: if the name is unknown it is skipped. Otherwise it is drawn once for every
   worn wearable of the slot's type, in wear order, each time with that wearable as the current instance
   (Section 4.5). If none of that type is worn the layer is skipped. If the wearable has no pixels for the slot,
   that instance's layer is skipped at the texture step (6.3, item 1), but its mask has already been built and has taken part in masking (6.2).
5. A layer without a `local_texture` is drawn exactly once, with no current instance, so it uses the merged view
   in which the last-worn wearable's value wins.
6. After the loop, the morph mask is built (Section 10), then the final alpha (Section 11).

## 6. Drawing one layer instance (R05, R08 to R11)

### 6.1 Colour

Work out the layer's net colour and whether a colour was "specified" (Section 7). If the net colour's alpha is
below 0.0001 the layer is skipped entirely: nothing is drawn, the canvas alpha is not touched, and no mask is
remembered for it.

### 6.2 Mask

If the layer's alpha list is not empty, build its mask (Section 8). Two outcomes besides success:

- A needed mask image cannot be loaded: the layer is skipped and nothing changes.
- Nothing was applied (every alpha parameter was skipped or ignored, see 8.1) and the first one is not `multiply_blend`: the mask is all zero. That all-zero mask is copied into the canvas alpha, the mask is remembered for the morph step, the layer is reported skipped, and nothing is drawn.

Otherwise the mask is copied into the canvas alpha plane (overwriting it) and used as the source alpha of the
drawing below. The mask is also remembered, per (layer, wearable instance), for the morph step. The canvas alpha
therefore holds the last mask-bearing layer's shape until the final alpha step replaces it; in sets with
`clear_alpha="false"` and no alpha-replacing step this is the visible result (the skirt's shape).

### 6.3 What is drawn

Up to three things are drawn, in this order, onto the canvas. "Tint" is the net colour as bytes: each of red, green, blue, alpha is multiplied by 255 in float32 and then rounded (ties to even). The same rule gives the alpha byte used in 8.1 and the fill alpha.

1. **Wearable texture.** If the layer has a `local_texture` that is not `local_texture_alpha_only`: the instance's texture is resampled to N by N (Section 12) and drawn through "image drawing" below. If the instance has no pixels for the slot the layer is skipped at this point (the mask, if any, stays in the alpha).
2. **Bundled image** (`tga_file`).
   - If `file_is_mask` is true: the file's grey reading (Section 9.2) resampled to N is the shape, and the layer colour is the colour. The per-pixel alpha is the layer's mask if there is one, otherwise the file values each combined by byte product with the tint's alpha byte (skip the combination when that byte is 255). Drawn through "fill drawing".
   - Otherwise: the file's colour reading resampled to N is drawn through "image drawing".
   - A file that cannot be loaded skips the layer.
3. **Flat fill.** If the layer has no `local_texture` (or has one with `local_texture_alpha_only`), has no `tga_file`, and a colour was specified: fill with the colour, alpha per pixel = the layer's mask if there is one, else the tint's alpha byte. "Fill drawing".

When a layer has both a wearable texture and a bundled image, both are drawn, one after the other, each using the same
mask as its source alpha. The bundled-mask file's values are already inside the layer's mask (8.1 step 5); only a
layer without a mask multiplies them in here.

The layer is reported `drawn` if any of the three happened, otherwise `skipped`. A layer that has alpha parameters
but ends up drawing nothing (for example no colour specified, no texture, no image) still leaves its mask in the
canvas alpha.

### 6.4 Image drawing

For every pixel: red, green, blue of the source are replaced by their byte product with the tint's red, green,
blue; this is done only when at least one tint component is not 255. The pixel alpha `a` is the layer's mask value
at that pixel if the layer has a mask, otherwise the byte product of the source alpha and the tint's alpha byte.

- If the layer has `write_all_channels`: write red, green, blue and `a` into the canvas, replacing what is there.
- Otherwise, if `a` is 0 leave the pixel alone. Otherwise blend (6.6) with "alpha follows source" true when the layer has no mask and false when it has.

### 6.5 Fill drawing

Same as image drawing with the tint's red, green, blue as the source colour and the supplied per-pixel alpha,
except that the blend never lets the alpha channel follow the source (so the canvas alpha is untouched), and
with `write_all_channels` the alpha written is the supplied alpha.

### 6.6 Blend

Source colour `s`, canvas colour `d`, alpha `a` (0..255):

- `a` equal to 255: the canvas colour becomes `s`; if alpha follows source, the canvas alpha becomes 255.
- Otherwise each colour component becomes `d + trunc(((s - d) * a + 127) / 255)`, where the division truncates toward zero (the numerator can be negative). If alpha follows source, the canvas alpha becomes `(a*a + dA*(255-a) + 127) / 255` with integer division, `dA` being the old canvas alpha.

## 7. Colour (R05 to R08)

### 7.1 Net colour of a layer

1. If the layer has colour parameters: the start colour is the named global colour if the layer names one (7.2), else its fixed colour if that has non-zero alpha, else transparent black. The colour parameters are folded in over it in order (7.3). A colour is specified.
2. Else if the layer names a global colour: that colour; specified.
3. Else if its fixed colour has non-zero alpha: that colour; specified.
4. Else opaque white; not specified (so no flat fill happens).

### 7.2 Global colour

If the name is unknown, or the colour has no parameters, the colour is opaque white. Otherwise start from
transparent black and fold in each listed parameter in order (7.3). The current wearable instance (if any)
influences the weights as in 4.5.

### 7.3 Folding a colour parameter in

Parameters that are unknown or have no `param_color` are ignored. For the others, compute the parameter's
colour from its stops (7.4), then combine with the running colour `c`, component-wise on all four components:

- add: `c = c + p`
- multiply: `c = c * p`
- blend: `c = c + (p - c) * t` where `t` is the parameter's plain weight (Section 4.5), not its sex-gated effective weight.

There is no clamping between parameters. After the last one every component is clamped to 0..1. The global colour's result (7.2) is clamped this way before it becomes the start colour of a layer's fold, and the layer's own fold is clamped again at its end. A fixed colour used as a start colour is not clamped. Operations are plain float32 multiplies and adds; no fused multiply-add is assumed.

### 7.4 A parameter's colour from its stops

With no stops the colour is transparent black. Otherwise let `n` be the stop count and `e` the parameter's
effective weight (4.6). Compute `s = e * (n-1)`; `i` is `s` truncated, limited to 0..n-1. If `i` is the last stop,
the colour is the last stop. Otherwise it is stop `i` plus `t` times (stop `i+1` minus stop `i`), per
component, where `t = s - i` is not clamped further (it stays within 0..1 for weights inside 0..1). Note that the weight is used as it is, not rescaled by the parameter's own min and max, so the
parameter's range should be 0..1 for the stops to cover the whole scale.

## 8. Alpha masks of a layer (R09, R10, R16)

### 8.1 Building the mask

Operating on N by N bytes:

1. Look at the first parameter in the layer's alpha list (by its merged record; an unknown id counts as not multiply). If it is defined and has `multiply_blend`, the mask starts as a copy of the canvas's current alpha plane. Otherwise it starts at all zero.
2. For each alpha parameter in the list, in order: ignore it if it is unknown, has no `param_alpha`, or its `tga_file` is empty. If it is to be skipped (4.7), note that and continue. Otherwise:
   1. `e` is its effective weight (4.6).
   2. Obtain the ramped mask image for the file, domain and `e` (8.2), resampled to N by N.
   3. If the parameter has `multiply_blend`: mask = byte product of mask and image, per pixel. Otherwise mask = min(255, mask + image), per pixel.
   4. Count it as applied.
   A mask image that cannot be loaded fails the whole mask (6.2).
3. If nothing was applied and the first parameter is not a multiply one: the result is the all-zero mask (6.2). It is returned as it is, with none of the further steps.
4. If the layer has a `local_texture` and the current instance has pixels with an alpha channel for that slot: resample its alpha to N and multiply the mask by it (byte product). This happens for any such layer, including `local_texture_alpha_only` ones.
5. If the layer has a `tga_file` with `file_is_mask`: multiply the mask by that file's grey reading at N.
6. If the layer's net colour alpha differs from 1 by more than 0.0001: multiply the mask by the byte `round(alpha*255)`.

If the first parameter is a multiply one and every parameter is skipped, the mask is just the starting copy of the
canvas alpha and steps 4 to 6 still apply.

### 8.2 The ramped mask image

1. The weight `e` is limited to 0..1, multiplied by 255 and rounded to an integer `q` (0..255); the weight used from here on is the float32 division `q / 255`. (So two weights in the same 1/255 step give identical results.)
2. The ramp (below) is run over the file's grey values at the file's native size; the ramped result is resampled to N afterwards.

### 8.3 The ramp

Input: a grey value `g` (0..255), the parameter's `domain` and the weight `w` (limited to 0..1).

- If `domain` is greater than 0: `scale = 1 / domain`; `offset = (1 - domain) * (1 - w)`; `bias = -(scale * offset)`; the output is `255 * (g/255 * scale + bias)` limited to 0..255 and truncated to a byte. Order of operations, all float32: `g/255` is a division by 255; that is multiplied by `scale`; `bias` is added; the sum is multiplied by 255; then it is limited to 0..255 and truncated. `scale` is `1/domain`; `offset` is `(1 - domain)` times `(1 - w)`; `bias` is the negated product of `scale` and `offset`.
- If `domain` is 0 (or less): `threshold = trunc(255 * (1 - w))` (float32, then as a byte); the output is 255 where `g >= threshold`, else 0.

Meaning: the ramp's soft edge is `domain` wide (in units of the 0..1 grey scale) and its position moves with the weight;
with domain 0 it is a hard step.

## 9. Resources (R15)

### 9.1 Files

The bundled files are standard TGA. The reader must handle: greyscale (types 3 and 11; a two-byte pixel is grey
plus alpha), true colour (types 2 and 10; 24 or 32 bits stored blue, green, red, alpha; 16-bit as 5-5-5 without
alpha), colour-mapped (types 1 and 9), each raw or run-length coded, and either row origin (the result is always
held top row first). The id field and colour map are skipped over. A 32-bit true-colour file counts as having alpha
whatever its descriptor's alpha-bit count says (three shipped files have a count of 0 and still carry alpha). The
shipped files are 50 run-length 8-bit greyscale files, 1 raw 8-bit greyscale file, and 5 run-length true-colour
files (one 24-bit, four 32-bit). Other types fail to load. A file that fails or is missing counts as "cannot be loaded".

### 9.2 Grey reading (mask use)

A greyscale file gives its values. A colour file with alpha gives its alpha plane. A colour file without alpha
gives 255 everywhere.

### 9.3 Colour reading (image use)

A greyscale file gives red = green = blue = the value, alpha 255 (no alpha). A colour file gives its colours, and
its alpha plane if it has one (otherwise "without alpha").

## 10. The morph mask (R14)

One extra N by N plane that travels with the bake. It starts at 255 everywhere.

For each layer of the set in file order, skipping bump layers, take only those that are (a) named in the region's
morph mask list (3.4) and (b) have at least one alpha parameter. Each such layer contributes zero or one mask:

- Without a `local_texture`: exactly one contribution, computed with no current instance.
- With a `local_texture`: the slot's wearable type is looked up; if the name is unknown, nothing. Of the worn wearables of that type only the last one (the one on top) is used; if none is worn, nothing. A worn wearable counts whether or not it has pixels for the slot.

A contribution's mask is the mask this layer made for that same instance during the colour pass (Section 6.2,
including the all-zero case) if there was one. Otherwise (the layer's colour alpha was too low to be drawn, or it
was not drawn for another reason) it is computed now exactly as in 8.1 (the canvas alpha then is what the colour pass left, before the final alpha step), with that instance current, with the
instance's own net colour, and with the canvas alpha as it is at that moment as the starting copy for
multiply-first layers. If a mask image is missing, the layer contributes nothing.

Each contribution is merged byte by byte: `morph = (morph * (mask + 1)) >> 8`, integer arithmetic. So a mask of
255 leaves the value alone, a mask of 0 sets it to 0, and a mask of 224 turns 255 into 224.

A bake that ended through invisibility (Section 11) has a morph mask of 255 everywhere. Channels whose region has no
list (eyes, hair, skirt, the extra channels) always end with 255.

In the shipped data the list is: head: `facialhair`; upper_body: `upper_clothes`; lower_body: `lower_pants`.

## 11. Visibility and the final alpha (R13)

### 11.1 Invisibility

Before anything is drawn, for every visibility-mask layer of the set that has a `local_texture`: if any worn
wearable of that slot's type has a texture id for that slot exactly equal (as a 128-bit id) to the "invisible" texture id
(`3a367d1c-bef1-6d43-7595-e88c1e3aadb3`), the whole bake is hidden: alpha 0 everywhere, red, green, blue 0, morph
mask 255, `Invisible` true, and nothing else is evaluated. The pixels of that texture are not needed.

### 11.2 Final alpha

After the morph step:

1. If the set has `alpha_tga_file`: the alpha plane becomes that file's grey reading at N (when it cannot be loaded, the alpha plane stays as it is). Else if the set has `clear_alpha` true, or the set contains any visibility-mask layer: the alpha plane becomes 255 everywhere. Else it is left as the layers left it.
2. Then each visibility-mask layer, in file order, multiplies the alpha plane (byte product):
   - with a `tga_file` (this takes precedence: such a layer's `local_texture`, if it also has one, is not used here, though the invisibility check in 11.1 still uses it): by the file's grey reading (when `file_is_mask`) or by its alpha plane (255 if it has none, which includes every greyscale file), resampled to N. A file that cannot be loaded is skipped.
   - with a `local_texture`: for each worn wearable of the slot's type, in wear order, by the alpha plane of its texture resampled to N. If nothing of that type is worn, or the wearable has no pixels for the slot, nothing happens for it. A texture without an alpha channel changes nothing.

Red, green and blue are not touched by this step.

## 12. Resampling (R15)

Applies to any single plane, from `sw` by `sh` to `dw` by `dh`. All colour planes and the alpha plane are done
separately and without any pre-multiplication. A texture's own aspect ratio is not preserved: it is stretched to N by N.

- Same size: the plane is returned unchanged.
- Downscale where both `dw <= sw` and `dh <= sh` and `sw` is a multiple of `dw` and `sh` a multiple of `dh`: box average. Each output pixel is the rounded mean of the covering block: `(sum + area/2) / area` with integer division.
- Everything else (up, non-integer down, mixed): bilinear. For output pixel (x, y) the source position is `((x + 0.5) * sw/dw - 0.5, (y + 0.5) * sh/dh - 0.5)` (float32); `sw/dw` and `sh/dh` are float32 ratios. Let `x0` be the floor of the source x position and `tx` the position minus `x0` (so `tx` is in 0..1 even outside the image); likewise `y0`, `ty`. The neighbours are `x0` and `x0+1`, `y0` and `y0+1`, each index clamped to 0..size-1 (so beyond an edge both neighbours are the edge pixel). On each of the two rows the value is `a + (b - a) * tx`; the result is `top + (bottom - top) * ty`, plus 0.5, limited to 0..255 and truncated to a byte. No fused multiply-add is assumed. The identity case (same width and height) applies only when both match.

## 13. The two flags (R18)

`NothingDrawn` is true when no entry in the report has the status `drawn`: every colour layer was skipped. It
is a statement about layer decisions, not pixel values. A layer whose texture is fully transparent still counts as drawn, and the invisible case is
reported separately (`Invisible` true, `NothingDrawn` false). A channel with `NothingDrawn` true is not a meaningful
bake because its alpha starts opaque and nothing ever carved it.

## 14. Edge cases

- No wearables at all is valid: layers needing a wearable are skipped; layers that need none (the bundled base images with a global colour, the eye whites, the grey base of the extra channels) are drawn using parameter defaults.
- A wearable of a type no layer set uses (shape, physics) takes part only through parameter values (parameter 80 is the shape's sex value).
- An id given by the caller that is not in the data is ignored.
- A `local_texture` layer with several wearables of its type draws all of them over each other in wear order, each with its own values.
- Zero or one stop colours, an unknown global colour name, a `fixed_color` with fewer than 3 components (a data error) are not exercised by the shipped data.
- A `param_alpha` with no `tga_file` occurs only in bump layers in the shipped data (never evaluated). It is ignored if it occurs in a colour layer.
- A texture of any size, including 1 by 1, works; the output size may be any positive integer (the production range is 8 to 4096).

## 15. Where this description follows the current compositor rather than the viewer

Recorded so the next reader does not mistake them for documented Second Life behaviour:

1. An alpha parameter with no image file is ignored here. The viewer draws a constant alpha equal to the weight. The shipped data has this only in bump layers, which are never drawn, so no reference result depends on it.
2. The viewer rejects very faint source fragments (alpha under about 1/255) when drawing; this compositor skips a pixel only when the alpha is exactly 0. I can't confirm from the source how that difference shows in a real bake; the vectors follow this compositor.
3. When a layer has a mask, the viewer's blend uses the mask held in the canvas alpha and also updates the canvas alpha channel as part of the blend. This compositor leaves the canvas alpha equal to the mask in that case (6.4, "alpha follows source" false). Final alpha is reset in most sets (11.2), so this shows only in sets with `clear_alpha="false"` (hair, skirt, extra channels).
4. The viewer rounds a changing weight to 1/255 steps of its own range when it is set live; here, only the mask weights are rounded (8.2), and colour weights are not.
5. The viewer's skin may be blended by the graphics card in 8-bit steps; the byte rounding rules in 6.6 and Section 1 are what the reference results show.

## 16. Reference results

`vectors/` holds inputs and outputs for every rule. A new implementation must reproduce each output byte for
byte (images, morph mask, flags), except the free-text report details.
