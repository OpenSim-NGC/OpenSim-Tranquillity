# The 5th component of a baked texture is the morph mask — bump layers are never rendered

**In one sentence:** the LL compositor never renders `render_pass="bump"` layers into a bake
(`lltexlayer.cpp:395` composites `RP_COLOR` layers only); the 5th component a viewer bake carries is the
**morph mask** from `LLTexLayerSet::gatherMorphMaskAlpha` (`lltexlayer.cpp:460-472`), and that is what this
library produces as `CompositeResult.MorphMask` and encodes as component 4.

Authority (Ledger P-1): the LL viewer source, read read-only at `F:\viewer-develop` (viewer 26.1.1 per
`indra/newview/VIEWER_VERSION.txt`): `indra/llappearance/lltexlayer.cpp`, `lltexlayer.h`,
`lltexlayerparams.cpp`, `indra/newview/llviewertexlayer.cpp`, and `indra/newview/character/avatar_lad.xml`.
Line numbers below are from those files at that version. This document is the spec `TexLayerCompositor`
follows for the 5th component.

## 1. Finding: the bump layers are never rendered into a bake

`avatar_lad.xml` marks 22 layers with `render_pass="bump"` (5 in `head`, 8 in `upper_body`, 9 in
`lower_body`; none in `eyes`, `hair`, `skirt` or the five Bakes-on-Mesh sets). The attribute is parsed at
`lltexlayer.cpp:596-604` (`LLTexLayerInfo::parseXml`: `render_pass_name == "bump"` →
`mRenderPass = LLTexLayer::RP_BUMP`; the enum `ERenderPass { RP_COLOR, RP_BUMP, RP_SHINE }` is
`lltexlayer.h:57-62`).

`LLTexLayerSet::render` (`lltexlayer.cpp:357-421`) composites **only** `RP_COLOR` layers:

```
    // composite color layers                                   lltexlayer.cpp:392-401
    for(LLTexLayerInterface* layer : mLayerList)
        if (layer->getRenderPass() == LLTexLayer::RP_COLOR)
            success &= layer->render(x, y, width, height, bound_target);
    renderAlphaMaskTextures(x, y, width, height, bound_target, false);   // :403
```

There is no other reference to `RP_BUMP` in `lltexlayer.cpp` or `lltexlayerparams.cpp`. The bump layers
(`head bump base`, `bump_head_base.tga`, `wrinkles_shading`, `eyebrowsbump`, `facialhair bump`,
`base_upperbody bump`, `upper_clothes bump`, …) are parsed, kept in `mLayerList`, and skipped by the
render loop. **The 5th component of an uploaded bake is not a bump map.** The name in Ledger Q-8 is a
legacy of the 2009 viewers; the current compositor does not produce one, and neither does this library.

`head_wrinkles_highlights_alpha.tga`, referenced only by the bump layer `wrinkles_shading`
(`avatar_lad.xml:9192`), does not exist in the viewer's `character/` directory either (checked S0d), so it
cannot be embedded; nothing that is rendered needs it.

## 2. What the 5th component is: the morph mask

The reference bakes (Truly Bazar, captured 2026-09-03) are five-component J2C. Measured with the library's
decoder: component 3 is the visibility alpha (on the head it is the eyelash mask `head_alpha.tga`; on the
bald hair it is 1 everywhere), and component 4 is a flat 1 on the head and a flat 254 on upper, lower, eyes
and hair. That is exactly the output of `LLTexLayerSet::gatherMorphMaskAlpha`:

```
void LLTexLayerSet::gatherMorphMaskAlpha(U8 *data, ...)        lltexlayer.cpp:460-472
{
    memset(data, 255, width * height);                        // :463  default: 255
    for(LLTexLayerInterface* layer : mLayerList)
        layer->gatherAlphaMasks(data, ...);                   // :467
    // Set alpha back to that of our alpha masks.
    renderAlphaMaskTextures(..., true);                       // :471  (GL buffer only; not `data`)
}
```

The packing of RGB + alpha + this array into a 5-component image is **not in the 26.1.1 viewer**: its
`indra/newview/llviewertexlayer.cpp` (353 lines) has no `LLViewerTexLayerSetBuffer::doUpload`, no J2C encode and
no upload path at all — the LL viewer stopped uploading client bakes when SL's bake service took over, and
only renders locally for its own display. The five-component reference bakes therefore come from a client
that still carries the old upload path, which under P-1 is a capture tool, not an authority. The component
order R, G, B, A(visibility), M(morph mask) is established **empirically** from the five reference bakes above
(component 3 carries the eyelash visibility mask on the head; component 4 is the flat morph-mask value), and
matches the historical LL packing (`"RGBHM"`) that the old upload path wrote. It cannot be cited to a line of
the permitted viewer files.

### 2.1 Which layers contribute

`gatherAlphaMasks` → `addAlphaMask` (`lltexlayer.cpp:1287-1290`, `:1513-1539`):

```
    const U8* alphaData = getAlphaData();                                  // :1517  cached morph mask
    if (!alphaData && hasAlphaParams())                                    // :1518
        renderMorphMasks(..., force_render = false);                       // :1525  → returns unless hasMorph()  (:1294)
    if (alphaData)
        for (i) data[i] = (U8)((data[i] * ((U16)alphaData[i] + 1)) >> 8);  // :1530-1537  multiply
```

`renderMorphMasks` caches its result only `if (hasMorph() && success)` (`:1389`), and refuses to render
at all without `force_render` unless `hasMorph()` (`:1294-1298`). So **only layers with `mHasMorph`
contribute**; every other layer leaves `data` untouched. `mHasMorph` is set through
`LLTexLayerTemplate::setHasMorph` (`lltexlayer.cpp:1717-1727`) for the layers named in the
`<morph_masks>` block of `avatar_lad.xml` (`avatar_lad.xml:17473-17502`):

| body_region | layer | morphs |
|---|---|---|
| head | `facialhair` | Displace_Hair_Facial |
| upper_body | `upper_clothes` | Displace_Loose_Upperbody, Shirtsleeve_flair |
| lower_body | `lower_pants` | Displace_Loose_Lowerbody, Leg_Pantflair, Low_Crotch, Leg_Longcuffs |

No other layer set has morph masks, so eyes, hair, skirt and the five extra bakes always carry 255.

### 2.2 Which wearables contribute: template layers vs plain layers

A layer set holds two kinds of layer (`LLTexLayerSet::setInfo`, `lltexlayer.cpp:290-297`): a layer whose
`isUserSettable()` is true becomes an `LLTexLayerTemplate`, any other a plain `LLTexLayer`. `isUserSettable()`
is exactly `mLocalTexture != -1` (`lltexlayer.cpp:64`): **only layers with a `local_texture` are templates.**

- A **template** layer is rendered once per worn wearable of its type: `LLTexLayerTemplate::render`
  (`:1659-1689`) walks `updateWearableCache()` (`:1615-1637`, the worn wearables of `getWearableType()`), and
  for each one calls `wearable->writeToAvatar` (`:1676`, so that wearable's parameters are current) and renders
  that wearable's clone of the layer (`:1678`). Its morph mask, however, contributes **only once, from the
  top (last) worn wearable** — `LLTexLayerTemplate::gatherAlphaMasks` (`:1710-1719`) takes
  `U32 i = num_wearables - 1; getLayer(i)` with the comment *"For rendering morph masks, we only want to use
  the top wearable"*, unlike `render`, which loops over all of them. (S1c correction: S0d/S0e recorded this as
  once per instance.) `upper_clothes` (local texture `upper_shirt`) and
  `lower_pants` (`lower_pants`) are templates.
- A **plain** layer is rendered exactly once by `LLTexLayer::render` (`:1023-1200`) whatever is worn, with the
  avatar's current parameter values — the values the worn wearables wrote in wear order, so the last-worn
  wearable of a type wins — and its morph mask contributes once (`LLTexLayer::gatherAlphaMasks`,
  `:1287-1290`). `facialhair` has no local texture and is a plain layer: with two hairs worn it is rendered
  once, from the second hair's parameters, not once per hair.

(`getWearableType`, `:842-880`, derives a texture-less layer's type from its parameters; it only matters for
templates and for `getSkip`, since a plain layer never enumerates instances.)

### 2.3 What each contribution is: the layer's alpha mask

`renderMorphMasks` (`lltexlayer.cpp:1292-1512`) builds the layer's mask in the alpha channel and reads it
back (`:1490-1493`: the alpha of an RGBA readback):

1. If the first alpha parameter is not `multiply_blend`, clear alpha to 0 (`:1309-1320`); otherwise start
   from the alpha already in the buffer.
2. For each alpha parameter, `LLTexLayerParamAlpha::render` (`lltexlayerparams.cpp:262-372`): skipped
   entirely when `getSkip()` (`:234-259`: `skip_if_zero` with an effective weight of 0, or the parameter's
   wearable type not worn); otherwise the mask file is pushed through `decodeAndProcess(domain, weight)`
   (`:330`) and blended **additively** (`BT_ADD`, "approximates max", `:287`) or by **multiplication**
   (`BF_DEST_ALPHA, BF_ZERO`, "approximates min", `:283`) per `multiply_blend`. The effective weight is
   the parameter's own value for the avatar's sex, else its default (`:272`).
3. Multiply by the local texture's alpha if the texture has 4 components (`lltexlayer.cpp:1338-1353`).
4. Multiply by the static mask image if `file_is_mask` (`:1355-1371`).
5. Multiply by the layer colour's alpha if it is not 1 (`:1373-1380`).

The colour pass calls this with `force_render = true` (`LLTexLayer::render`, `:1073-1076`) whenever the
layer has alpha parameters, so a layer all of whose parameters are skipped still produces a mask: all
zero (step 1 cleared it and nothing was added). That is why the female head's morph mask is 0 everywhere:
`facialhair`'s four masks (sideburns 1005, moustache 1007, soul patch 1009, chin curtains 1011 — `sex="male"`, `skip_if_zero`) all skip, the
mask is 0, and 255 × (0 + 1) >> 8 = 0. The reference head bake's component 4 is 1 (lossy coding of 0).

A layer whose net colour alpha is ≈ 0 is not rendered in the colour pass (`:1040-1044`) and so has no
cached mask; `addAlphaMask` then renders it on demand (`:1518-1526`) with the same rules.

### 2.4 A worn wearable with no texture asset still contributes (S1c)

This is the mechanism behind Ledger Q-12. Aleric Fenwood wears a jacket and **no shirt asset** — but his Shirt
slot is occupied: item `77c41e39-38f9-f75a-0000-585989bf0000` (the default shirt item) with asset id
`00000000-…`. His reference upper bake carries a real morph mask (31.4% of pixels at 0, 67.0% at 224); the
library produced a uniform 255 and logged `upper_clothes morph: no Shirt worn: mask left at 255`.

The authority resolves it, and rules out the other candidate S1b floated:

1. **`<morph_masks>` is the only selector; the jacket never contributes.** `gatherMorphMaskAlpha`
   (`lltexlayer.cpp:460-472`) does walk *every* layer of the set, so the block is not a filter there. But a layer
   can only contribute what `LLTexLayer::addAlphaMask` (`:1513-1531`) can get from `getAlphaData()`, and that
   cache (`:1183-1200`) is only ever filled by `renderMorphMasks`, which returns at once unless `hasMorph()`
   (`:1297-1302`) and caches only under `if (hasMorph() && success)` (`:1389`). `mHasMorph` is set exactly for
   the layers named in `<morph_masks>`: `LLAvatarAppearance::addMaskedMorph` (`llavatarappearance.cpp:1352-1358`,
   fed from `avatar_lad.xml:17473-17502` at `:971-985`) fills `mBakedTextureDatas[baked].mMaskedMorphs`, and the
   layerset loader looks each one up by name and calls `layer->setHasMorph(true)` (`:1237-1243`).
   `upper_jacket` is **not** in `<morph_masks>`, so `hasMorph()` is false for it and it contributes nothing to
   the morph mask however it is worn. S1b candidate (a) — "the gather should include `upper_jacket`" — is
   **refuted**. Its five `param_alpha` entries (`jacket Sleeve Length#1020`, `jacket Collar Front#1022`,
   `Collar Back#1024`, `bottom length upper#620`, `open upper#622`) drive the **colour** pass only.

2. **The contributing wearable is counted, not its texture.** `upper_clothes` has `local_texture="upper_shirt"`,
   so it is an `LLTexLayerTemplate` of wearable type Shirt (2.2). `LLTexLayerTemplate::gatherAlphaMasks`
   (`:1710-1719`) asks `updateWearableCache()` (`:1615-1638`) for the worn wearables of that type — and that
   function counts **`LLWearable` objects**, not textures: `getWearableCount(wearable_type)` and
   `getWearable(wearable_type, i)`, with no reference to whether the wearable has an image. `getLayer(i)`
   (`:1639-1656`) then needs only an `LLLocalTextureObject` for the layer's `local_texture`, which a worn shirt
   has whether or not a texture asset was ever set. So a worn-but-assetless shirt yields a real `LLTexLayer`,
   `hasAlphaParams()` is true (four `param_alpha` children), `hasMorph()` is true, and `renderMorphMasks`
   (`:1296-1400`) produces a mask from the parameter alphas alone. The texture-alpha accumulation step
   (`:1336-1353`) contributes nothing, because there is no image — which is also why the **colour** pass draws
   nothing for this layer and the visible bake is unaffected.

3. **The parameters are on the avatar, not the missing asset.** `upper_clothes`'s four alpha params are
   `Sleeve Length Cloth#600` (`shirt_sleeve_alpha.tga`, `multiply_blend="false"`), `Shirt Bottom Cloth#601`
   (`shirt_bottom_alpha.tga`), `Collar Front Height Cloth#602` (`shirt_collar_alpha.tga`) and
   `Collar Back Height Cloth#778` (`shirt_collar_back_alpha.tga`). All four are `group="1"` driven params; their
   drivers are `Sleeve Length#800`, `Shirt Bottom#801`, `Collar Front#802` and `Collar Back#781`, which are
   `group="0"` shirt-owned params and therefore travel in the avatar's `VisualParams`. A missing shirt *asset*
   costs no parameter value.

**Predicted mechanism for the observed histogram.** `#600` is first in `mParamAlphaList` and is
`multiply_blend="false"`, so `renderMorphMasks` clears the buffer to 0 and accumulates `shirt_sleeve_alpha.tga`
(`:1307-1330`); `#601`, `#602` and `#778` then multiply in. The 31.4% of pixels at 0 is the upper-body area no
(short-sleeved, bottom-limited, low-collar) shirt covers — beyond the sleeve, below the shirt bottom, above the
collar — and the 67.0% at 224 is where the shirt does cover. The parameters responsible, by name and id, are
`Sleeve Length Cloth#600` (dominant, being the clearing param), then `Shirt Bottom Cloth#601`,
`Collar Front Height Cloth#602` and `Collar Back Height Cloth#778`.

**The rule to implement** (general, not jacket- or shirt-specific): *a morph-mask layer with a `local_texture`
contributes the mask of the **top worn wearable of its type**; a wearable counts as worn when its slot is
occupied, whether or not it carries a texture asset. Its parameter values come from the avatar's parameters,
which for an assetless wearable are supplied entirely by the caller's `VisualParams`.*

## 3. What the library does (`TexLayerCompositor.Bake`)

1. `AvatarLad` parses `<morph_masks>` into `MorphMaskLayers[body_region] = { layer names }`.
2. The colour pass renders a layer with a local texture once per worn wearable of its type and a layer
   without one exactly once with the merged parameters (2.2), computing each rendered instance's mask
   (`ComputeMask`) and caching it per (layer, instance), mirroring `mAlphaCache`.
3. After the colour layers, `MorphMask` starts at 255 everywhere (`memset(data, 255)`). For each layer of
   the set named in `MorphMaskLayers` that has alpha parameters: a template layer contributes the mask of the
   **top worn wearable of its type** (2.2), a plain layer contributes once; each contribution's mask (cached,
   or computed on demand with the same rules) is multiplied in with `(m * (mask + 1)) >> 8`. A wearable whose
   slot is worn but carries no texture asset is a contributing instance like any other (2.4); its parameters
   come from `BakeRequest.VisualParams`.
4. Sets without morph layers, and the invisible-alpha short-circuit, yield 255 everywhere.
5. `J2kCodec.EncodeBake` writes R, G, B, A, M as a five-component single-tile J2C; `Decode` exposes a
   fifth component as `RgbaPlanes.Mask`.

Reference check (S0d golden run, Truly Bazar, 512): head 0 vs reference 1, all other channels 255 vs
254 — the difference is the reference's lossy coding of the same constants. S0e: the plain-layer rule above
replaced S0d's per-instance treatment of `facialhair`; Truly wears one hair, so her numbers are unchanged.
