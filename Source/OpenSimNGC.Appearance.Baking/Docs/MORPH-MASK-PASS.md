# The 5th component of a baked texture is the morph mask — bump layers are never rendered

**In one sentence:** the Linden Lab compositor never renders `render_pass="bump"` layers into a bake (its
layer-set render function composites only the colour-pass layers, `lltexlayer.cpp:392-401`); the 5th component a
viewer bake carries is the **morph mask** gathered by the layer set's morph-mask function
(`lltexlayer.cpp:460-472`), and that is what this library produces as `CompositeResult.MorphMask` and encodes as
component 4.

Authority: the Linden Lab viewer source, read read-only (viewer 26.1.1 per
`indra/newview/VIEWER_VERSION.txt`): `indra/llappearance/lltexlayer.cpp`, `lltexlayer.h`,
`lltexlayerparams.cpp`, `indra/newview/llviewertexlayer.cpp`, and `indra/newview/character/avatar_lad.xml`.
Line numbers below are from those files at that version; everything else in this document is a description in
our own words. This document is the spec `AvatarBakeCompositor` follows for the 5th component.

## 1. Finding: the bump layers are never rendered into a bake

`avatar_lad.xml` marks 22 layers with `render_pass="bump"` (5 in `head`, 8 in `upper_body`, 9 in
`lower_body`; none in `eyes`, `hair`, `skirt` or the five Bakes-on-Mesh sets). The viewer reads the attribute
when it loads a layer definition (`lltexlayer.cpp:596-604`) and stores one of three render passes (colour, bump,
shine; `lltexlayer.h:57-62`).

The layer set's render function (`lltexlayer.cpp:357-421`) walks its layer list in order and draws a layer only
when its render pass is the colour pass (`:392-401`); after the loop it renders the visibility alpha masks
(`:403`). Nothing else in `lltexlayer.cpp` or `lltexlayerparams.cpp` refers to the bump pass. The bump layers
(`head bump base`, `bump_head_base.tga`, `wrinkles_shading`, `eyebrowsbump`, `facialhair bump`,
`base_upperbody bump`, `upper_clothes bump`, ...) are parsed, kept in the layer list, and passed over by the render
loop. **The 5th component of an uploaded bake is not a bump map.** The name "bump" is a legacy of the 2009
viewers; the current compositor does not produce one, and neither does this library.

`head_wrinkles_highlights_alpha.tga`, referenced only by the bump layer `wrinkles_shading`
(`avatar_lad.xml:9192`), does not exist in the viewer's `character/` directory either, so it
cannot be embedded; nothing that is rendered needs it.

## 2. What the 5th component is: the morph mask

The reference bakes (one test avatar, captured 2026-09-03) are five-component J2C. Measured with the library's
decoder: component 3 is the visibility alpha (on the head it is the eyelash mask `head_alpha.tga`; on the
bald hair it is 1 everywhere), and component 4 is a flat 1 on the head and a flat 254 on upper, lower, eyes
and hair. That is what the viewer's morph-mask gather produces (`lltexlayer.cpp:460-472`), which works in three
steps:

1. It fills a byte array, one byte per pixel, with 255 (`:463`).
2. It asks every layer of the set, in list order, to multiply its own morph alpha mask into that array (`:467`).
3. It finally re-renders the visibility alpha masks into the GL buffer (`:471`); this touches the GL buffer
   only, not the byte array.

The packing of RGB + alpha + this array into a 5-component image is **not in the 26.1.1 viewer**: its
`indra/newview/llviewertexlayer.cpp` (353 lines) has no bake upload, no J2C encode and no upload path at all —
the LL viewer stopped uploading client bakes when SL's bake service took over, and only renders locally for its
own display. The five-component reference bakes therefore come from a client that still carries the old upload
path, which is a capture tool rather than an authority. The component order R, G, B, A(visibility), M(morph mask)
is established **empirically** from the five reference bakes above (component 3 carries the eyelash visibility mask
on the head; component 4 is the flat morph-mask value), and matches the historical LL packing that the old
upload path wrote (the five letters R, G, B, H and M, with the visibility alpha in the fourth place). It cannot be cited to a line of the
permitted viewer files.

### 2.1 Which layers contribute

A layer adds its mask through the gather path at `lltexlayer.cpp:1287-1290` and `:1513-1539`. In words:

- It first asks for the layer's cached morph mask (`:1517`).
- If there is none and the layer has alpha parameters, it asks the layer to render its morph masks without
  forcing (`:1518-1525`). That render call returns at once unless the layer is flagged as having a morph
  (`:1294-1298`), so an unflagged layer never has a mask to offer.
- When it has a mask, it multiplies the array by `(mask + 1) / 256` per pixel, in integer arithmetic
  (`:1530-1537`).

The morph-mask render caches its result only for a flagged layer that rendered successfully (`:1389`). So
**only layers flagged as having a morph contribute**; every other layer leaves the array untouched. The flag is
set (`lltexlayer.cpp:1717-1727`) for the layers named in the `<morph_masks>` block of `avatar_lad.xml`
(`avatar_lad.xml:17473-17502`):

| body_region | layer | morphs |
|---|---|---|
| head | `facialhair` | Displace_Hair_Facial |
| upper_body | `upper_clothes` | Displace_Loose_Upperbody, Shirtsleeve_flair |
| lower_body | `lower_pants` | Displace_Loose_Lowerbody, Leg_Pantflair, Low_Crotch, Leg_Longcuffs |

No other layer set has morph masks, so eyes, hair, skirt and the five extra bakes always carry 255.

### 2.2 Which wearables contribute: template layers vs plain layers

The viewer builds two kinds of layer when it loads a layer set (`lltexlayer.cpp:290-297`): a layer the user can
set (one that has a `local_texture`; `:64`) becomes a per-wearable "template" layer, any other layer is a plain
layer. **Only layers with a `local_texture` are templates.**

- A **template** layer is rendered once per worn wearable of its type. Its render function (`:1659-1689`) walks
  the worn wearables of the layer's wearable type (the cache refresh at `:1615-1637`), makes that wearable
  write its parameters to the avatar (`:1676`, so that wearable's parameters are current) and renders that
  wearable's own copy of the layer (`:1678`). Its morph mask, however, contributes **only once, from the top
  (last) worn wearable**: the template's gather function (`:1710-1719`) picks the last worn wearable only, where
  the render function above loops over all of them; a source comment at that spot explains that for morph masks
  only the top wearable is wanted. `upper_clothes` (local texture `upper_shirt`) and `lower_pants` (local texture
  `lower_pants`) are templates.
- A **plain** layer is rendered exactly once (`:1023-1200`) whatever is worn, with the avatar's current
  parameter values — the values the worn wearables wrote in wear order, so the last-worn wearable of a type
  wins — and its morph mask contributes once (`:1287-1290`). `facialhair` has no local texture and is a plain
  layer: with two hairs worn it is rendered once, from the second hair's parameters, not once per hair.

(The viewer works out a texture-less layer's wearable type from its parameters, `:842-880`; that only matters
for templates and for the skip decision, since a plain layer never enumerates instances.)

### 2.3 What each contribution is: the layer's alpha mask

The morph-mask render (`lltexlayer.cpp:1292-1512`) builds the layer's mask in the alpha channel of a render
target and reads it back (`:1490-1493`, the alpha of an RGBA readback):

1. If the first alpha parameter is not `multiply_blend`, alpha is cleared to 0 (`:1309-1320`); otherwise the
   mask starts from the alpha already in the buffer.
2. Each alpha parameter is applied in turn (`lltexlayerparams.cpp:262-372`). A parameter is skipped entirely
   when its skip test says so (`:234-259`): it has `skip_if_zero` and an effective weight of 0, or its
   wearable type is not worn. Otherwise its mask file is pushed through the mask-ramp processing
   (`:330`, taking the parameter's domain and weight) and combined with the buffer either **additively**
   (a blend the source describes as approximating a maximum, `:287`) or by **multiplication** (a
   destination-alpha blend the source describes as approximating a minimum, `:283`), according to
   `multiply_blend`. The effective weight is the parameter's own value for the avatar's sex, else its
   default (`:272`).
3. The mask is multiplied by the local texture's alpha if the texture has 4 components (`lltexlayer.cpp:1338-1353`).
4. It is multiplied by the static mask image if the layer says `file_is_mask` (`:1355-1371`).
5. It is multiplied by the layer colour's alpha if that is not 1 (`:1373-1380`).

The colour pass requests this render with the force flag set (`:1073-1076`) whenever the layer has alpha
parameters, so a layer all of whose parameters are skipped still produces a mask: all zero (step 1 cleared it
and nothing was added). That is why the female head's morph mask is 0 everywhere: `facialhair`'s four masks
(sideburns 1005, moustache 1007, soul patch 1009, chin curtains 1011 — `sex="male"`, `skip_if_zero`) all skip,
the mask is 0, and 255 × (0 + 1) >> 8 = 0. The reference head bake's component 4 is 1 (lossy coding of 0).

A layer whose net colour alpha is about 0 is not rendered in the colour pass (`:1040-1044`) and so has no
cached mask; the gather then renders it on demand (`:1518-1526`) with the same rules.

### 2.4 A worn wearable with no texture asset still contributes

One reference avatar wears a jacket and **no shirt asset**, but its Shirt slot is occupied: the slot holds the
default shirt item, whose asset id is all zeros. Its reference upper bake carries a real morph mask (31.4% of
pixels at 0, 67.0% at 224); an earlier version of this library produced a uniform 255 and reported that no
Shirt was worn for the `upper_clothes` morph layer.

The authority resolves it, and rules out the other candidate that was considered:

1. **`<morph_masks>` is the only selector; the jacket never contributes.** The gather (`lltexlayer.cpp:460-472`)
   does ask *every* layer of the set, so the block is not a filter at that point. But a layer can only
   contribute what its add-mask step (`:1513-1531`) can get from its cached mask, and that cache
   (`:1183-1200`) is only ever filled by the morph-mask render, which returns at once unless the layer is
   flagged as having a morph (`:1297-1302`) and caches only for a flagged layer (`:1389`). The flag is set
   exactly for the layers named in `<morph_masks>`: the avatar loader records each named layer
   (`llavatarappearance.cpp:1352-1358`, fed from `avatar_lad.xml:17473-17502` at `:971-985`) in the masked-morph
   list of its baked texture, and the layer-set loader looks each one up by name and sets the flag on it
   (`:1237-1243`). `upper_jacket` is **not** in `<morph_masks>`, so it is not flagged and it contributes nothing
   to the morph mask however it is worn. The candidate reading "the gather should include `upper_jacket`" is
   **refuted**. Its five `param_alpha` entries (`jacket Sleeve Length#1020`, `jacket Collar Front#1022`,
   `Collar Back#1024`, `bottom length upper#620`, `open upper#622`) drive the **colour** pass only.

2. **The contributing wearable is counted, not its texture.** `upper_clothes` has `local_texture="upper_shirt"`,
   so it is a template layer of wearable type Shirt (2.2). The template's gather function (`:1710-1719`) asks
   the cache refresh (`:1615-1638`) for the worn wearables of that type, and that refresh counts
   **wearable objects**, not textures: it asks how many wearables of the type are worn and fetches each, with no
   reference to whether the wearable has an image. Getting the layer for that wearable (`:1639-1656`) then
   needs only the wearable's local-texture object for the layer's `local_texture`, which a worn shirt has
   whether or not a texture asset was ever set. So a worn-but-assetless shirt yields a real layer, the layer
   has alpha parameters (four `param_alpha` children), is flagged as having a morph, and the morph-mask render
   (`:1296-1400`) produces a mask from the parameter alphas alone. The texture-alpha step (`:1336-1353`)
   contributes nothing, because there is no image — which is also why the **colour** pass draws nothing for this
   layer and the visible bake is unaffected.

3. **The parameters are on the avatar, not the missing asset.** `upper_clothes`'s four alpha params are
   `Sleeve Length Cloth#600` (`shirt_sleeve_alpha.tga`, `multiply_blend="false"`), `Shirt Bottom Cloth#601`
   (`shirt_bottom_alpha.tga`), `Collar Front Height Cloth#602` (`shirt_collar_alpha.tga`) and
   `Collar Back Height Cloth#778` (`shirt_collar_back_alpha.tga`). All four are `group="1"` driven params; their
   drivers are `Sleeve Length#800`, `Shirt Bottom#801`, `Collar Front#802` and `Collar Back#781`, which are
   `group="0"` shirt-owned params and therefore travel in the avatar's `VisualParams`. A missing shirt *asset*
   costs no parameter value.

**Predicted mechanism for the observed histogram.** `#600` is first in the layer's alpha parameter list and is
`multiply_blend="false"`, so the morph-mask render clears the buffer to 0 and accumulates `shirt_sleeve_alpha.tga`
(`:1307-1330`); `#601`, `#602` and `#778` then multiply in. The 31.4% of pixels at 0 is the upper-body area no
(short-sleeved, bottom-limited, low-collar) shirt covers — beyond the sleeve, below the shirt bottom, above the
collar — and the 67.0% at 224 is where the shirt does cover. The parameters responsible, by name and id, are
`Sleeve Length Cloth#600` (dominant, being the clearing param), then `Shirt Bottom Cloth#601`,
`Collar Front Height Cloth#602` and `Collar Back Height Cloth#778`.

**The rule to implement** (general, not jacket- or shirt-specific): *a morph-mask layer with a `local_texture`
contributes the mask of the **top worn wearable of its type**; a wearable counts as worn when its slot is
occupied, whether or not it carries a texture asset. Its parameter values come from the avatar's parameters,
which for an assetless wearable are supplied entirely by the caller's `VisualParams`.*

## 3. What the library does (`AvatarBakeCompositor.Bake`)

1. `AvatarLad` parses `<morph_masks>` into `MorphMaskLayers[body_region] = { layer names }`.
2. The colour pass renders a layer with a local texture once per worn wearable of its type and a layer
   without one exactly once with the merged parameters (2.2), computing each rendered instance's mask and
   keeping it per (layer, instance) so the morph step can reuse it, as the viewer's per-layer mask cache does.
3. After the colour layers, `MorphMask` starts at 255 everywhere. For each layer of the set named in
   `MorphMaskLayers` that has alpha parameters: a template layer contributes the mask of the
   **top worn wearable of its type** (2.2), a plain layer contributes once; each contribution's mask (kept
   from the colour pass, or computed on demand with the same rules) is multiplied in as
   `(m * (mask + 1)) >> 8`. A wearable whose slot is worn but carries no texture asset is a contributing
   instance like any other (2.4); its parameters come from `BakeRequest.VisualParams`.
4. Sets without morph layers, and the invisible-alpha short-circuit, yield 255 everywhere.
5. `J2kCodec.EncodeBake` writes R, G, B, A, M as a five-component single-tile J2C; `Decode` exposes a
   fifth component as `RgbaPlanes.Mask`.

Reference check (golden run against those reference bakes, 512): head 0 vs reference 1, all other channels 255 vs
254 — the difference is the reference's lossy coding of the same constants. The plain-layer rule above
replaced an earlier per-instance treatment of `facialhair`; that avatar wears one hair, so its numbers are unchanged.
