# Third-party notices — OpenSimNGC.Appearance.Baking

## `Data/avatar_lad.xml`

**Origin:** Linden Lab Second Life viewer, file `indra/newview/character/avatar_lad.xml`.
This is the avatar "LAD" (Linden Avatar Definition) file: it defines the visual
parameters, wearable layers, texture layer sets and morph targets that the bake
compositor reproduces server-side (ADR-007).

**Licence:** GNU Lesser General Public License v2.1, with the Second Life viewer
linking exception granted by Linden Lab (the "Linden Lab Second Life Viewer
Source License" exception that permits linking the viewer source with non-LGPL
code). The file is embedded unmodified as a data resource; it is not compiled or
linked into executable code. The full LGPL 2.1 text is in the viewer's `LICENSE`
file and at <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>.

**Copied from:** a local checkout of the viewer source tree at `F:\viewer-develop`
on 2026-09-03. That directory is not a git repository (no `.git`), so the exact
upstream commit could not be read from it. Identifying data that could be read:

| Field | Value |
|---|---|
| `indra/newview/VIEWER_VERSION.txt` | `26.1.1` |
| `avatar_lad.xml` `wearable_definition_version` | `22` |
| `avatar_lad.xml` `version` | `2.0` |
| File size | 354,436 bytes |
| SHA-256 | `ace7a7aebac5bee593d2ec2f5a487404cf53859e54537d00e53173c8fa1ee2cd` |

The SSB design documents (`Docs/feature/ssb-appearance/RECON-ssb-appearance-addendum.md` §3)
name the viewer commit used for the wire contract as `62033f2`; that identity could
not be confirmed against `F:\viewer-develop` and is recorded here as a claim, not a fact.

**Modifications:** none. Byte-for-byte copy.

## `Data/character/*.tga` (56 files)

**Origin:** Linden Lab Second Life viewer, directory `indra/newview/character/`: the parameter alpha masks,
the skin base images, eye whites and the Bakes-on-Mesh `aux_base.tga` that the `layer_set` definitions of
`avatar_lad.xml` name by file. The compositor cannot reproduce a bake without them (every clothing layer is
shaped by one), so they ship with the library alongside `avatar_lad.xml` (ADR-007 extended in S0b).

**Licence:** the same as `avatar_lad.xml` above: GNU LGPL 2.1 with the Linden Lab viewer linking exception.
Embedded unmodified as data resources; not compiled or linked.

**Source:** the Linden Lab viewer source tree at `F:\viewer-develop` (viewer 26.1.1 per
`indra/newview/VIEWER_VERSION.txt`), directory `indra/newview/character/`. The files were first taken (S0b,
2026-09-03) from the copy redistributed inside the `LibreMetaverse` 3.1.4 NuGet package; in S0d every one of
the 56 was re-verified byte for byte (SHA-256 below) against the viewer tree and found identical, so the viewer
tree is the recorded source. `avatar_lad.xml` also names `head_wrinkles_highlights_alpha.tga`, which exists in
neither the viewer tree nor the package; only a bump-pass layer references it and bump layers are never rendered
into a bake (see `Docs/MORPH-MASK-PASS.md`), so nothing is affected.

| File | SHA-256 |
|---|---|
| `aux_base.tga` | `2fc3cc8a65c332ba03abba73e747501f2d4cd1127539e7ad3a36820a6b1c9932` |
| `blush_alpha.tga` | `167f13ae91a6f48b07df261469f670f70fef9116d7e6f024ebb35c1ce07649bf` |
| `body_skingrain.tga` | `272efb5be2f339ea34d510dbf454e8d7c3397c67eec5fd26fc4a38a5ce03c70e` |
| `bodyfreckles_alpha.tga` | `da5c28bcebd359e03d27dbd8532efc3dcdfef2606d3490f30f15231be9abaefc` |
| `bump_face_wrinkles.tga` | `6ec85b56861e85320533bede582ff66edb62370333bfa627d52a361e4b8b1eed` |
| `bump_head_base.tga` | `7081ba6dc675c7c80cf5140aa12ca108b6fd33290e83958e9e8cfaa2c6c353ad` |
| `bump_lowerbody_base.tga` | `c93f907dc57841095965e4626b1a62709d5e9805c685ab3747731d9847f8079e` |
| `bump_pants_wrinkles.tga` | `41e0e2b9687f4181651ef498a7cffd77cd97e166113dcd98b04dfb3af18d8e5d` |
| `bump_shirt_wrinkles.tga` | `3265c2bf5446d171a31f30222e016e026d171e65c74dae09eb136bc271d16f44` |
| `bump_upperbody_base.tga` | `4733155e00b7c7f915e457328f9ad4e9c4ad927e59ea01a01ef5e3a167b2f54e` |
| `eyebrows_alpha.tga` | `24754e92c79df93ec8340203d83053a979adf4fa50d5cb5462422bae68f170a7` |
| `eyeliner_alpha.tga` | `415d8211facb46604d671ac81ffccd562bda045d32038cad8e7710af4a7b248c` |
| `eyeshadow_inner_alpha.tga` | `c93f47bafa17d611a2a324a67476f928647bd95d5f0bd4a3c8a16a0c73ad4233` |
| `eyeshadow_outer_alpha.tga` | `ba8172b57b8634c72a022f6ee8421f5c61bb2e89079640fbb5a9876db8e2aec9` |
| `eyewhite.tga` | `868162aca011392fcdbf58edf3dd9bd0d4a2c35f48f7d5e5473403b493f6e1e9` |
| `facehair_chincurtains_alpha.tga` | `a855d69630f404ea71a95c8df9a9c32a64e226514973296928a782a33c596b54` |
| `facehair_moustache_alpha.tga` | `b373c83536c7f2bbb3456b615a9ce5c8826e3ee11af0cc2dccd094070bc6be24` |
| `facehair_sideburns_alpha.tga` | `2c530eeae1dd16ea512080fb2245409dfdc6fab037ad73253256350e66ef681d` |
| `facehair_soulpatch_alpha.tga` | `edbdd8ceedb7edba67e4fe542e949536737e29786dddbc84ffa8506fa678e79d` |
| `freckles_alpha.tga` | `8d00e3786f9ad9e4be969182fa0d5983128049536dc69a553d69b93549628da8` |
| `glove_length_alpha.tga` | `d3ca442c95377459455de9f8cf36d36d2120e4ff5a6212fc2331ad5cf9228e7b` |
| `gloves_fingers_alpha.tga` | `8ac5b237a0647a7373c0bd0e0fcd967dc510ba49f0201d7f88a24774a5968711` |
| `head_alpha.tga` | `6b8b7d9bc4dd54f6b2a1d977875f02d00e687608749089fa7a504531718cf877` |
| `head_color.tga` | `6cd54f034a8c7fe3a1faf2aa66a489ea7e2db06d112a7c3e4fbd185046f8ac62` |
| `head_hair.tga` | `e0c0136059337b115d3d533d9b55244ab035f3ac42765dc72419e870ee5472d2` |
| `head_highlights_alpha.tga` | `f28fd1ddeb1ee7e4280536dfedf76698867ba2b13aababab14a74d06fdf69253` |
| `head_shading_alpha.tga` | `06aff93404b5a164d2c0631c8e0140094b723aafca985362a7ef2c672923e0de` |
| `head_skingrain.tga` | `59d8ec4188cf8a66996804829beb7f807a9fa8d99f94c3bd2f4763ff995de029` |
| `jacket_length_lower_alpha.tga` | `e6f7c87f356258c317d49a4e9b28f6a64893c136cb10fd39fbb18999c7282a66` |
| `jacket_length_upper_alpha.tga` | `2d40a4c818a05c3112c8185c9ac5fe96a6cc1675722ec73ee310e1a3d5d55801` |
| `jacket_open_lower_alpha.tga` | `5ac2b754b48859e0706519798205198ba59912d5319b7a185e725ce45f6b3415` |
| `jacket_open_upper_alpha.tga` | `efb61d9d080a0221f0677d513b98d608814548b95283f4b6fab37cfbe52e6604` |
| `lipgloss_alpha.tga` | `3ea04c5662aa36927051f728530ee8b9e8d6d74473ed63b3e00f5573665bf540` |
| `lips_mask.tga` | `e8540a42e40d10e92be1598194707148bf15a55f6553f9b293cc3004881ca883` |
| `lipstick_alpha.tga` | `afd140146466bf0f95d39e2b50be267de047b7666925a2d054e9a86cc2e98f96` |
| `lowerbody_color.tga` | `cdf46efc0284dd74d8a26e9adb0fa769a2b2130bd712c4f2b566f3cc89c4d5f6` |
| `lowerbody_highlights_alpha.tga` | `64d2cd0e3ce6e506e0a805121a4b047b375f3359a74eb6c77582f3f45dadef99` |
| `lowerbody_shading_alpha.tga` | `b6608d4ff3dac02aadcabb8d6565a54a245d4705f604078c9b3786bac3bd3d10` |
| `nailpolish_alpha.tga` | `cb88a2bcf379abe8f524eca960bad4ca7f2668af3357dc012407ad98202cbc19` |
| `pants_length_alpha.tga` | `dcbdfd9be0fe0ee3948f18681ec3076b060dac9be4ab90ee24ba29cdb06fd23b` |
| `pants_waist_alpha.tga` | `416a348b0b1cd7883f093e00d14c402e82ebf883c713cea0fa5c1614c388ddb7` |
| `rosyface_alpha.tga` | `c54fd26df68e6f13f9e312f925c785e911b54f00ba4f2e1039a97f0c1478171b` |
| `shirt_bottom_alpha.tga` | `d4e2e56c1b75eb1f173e004e2ec889a49f36d830975607a53308dba5da12ba0a` |
| `shirt_collar_alpha.tga` | `07dcfb155b833bb6ed12cb975437ae9929ef000f1c911fe846c24e322817a9fb` |
| `shirt_collar_back_alpha.tga` | `ff012cade37e65581f4e0f753541b945e0731baa80d5a6bd1a40ec9baf0b4981` |
| `shirt_sleeve_alpha.tga` | `363654ffdd35d80f42c13e13c834d349e925210b940a2abfe97c2d9f833eb359` |
| `shoe_height_alpha.tga` | `7fa78cb7925f13a3115c552fcca7d033ed7eb7897ae2853e2b6706aabf38ca25` |
| `skirt_length_alpha.tga` | `35e63c27f666b3b99b6214104635c4748ecc1968e9c12a182a6eb3933b839846` |
| `skirt_slit_back_alpha.tga` | `d1a16ec74e572a6d7cc2e7abfb1d9562a3af07e71bd24783c045f49099c3bcc2` |
| `skirt_slit_front_alpha.tga` | `f5d88fc2e2d2e003cdc8340a493b62e939544f11caa0d5e657dd3cda70bf2f6e` |
| `skirt_slit_left_alpha.tga` | `f1edee562c09b787e89efcf4f5977d0d3c281caa48fce4c49c3d6339d0d4c4e3` |
| `skirt_slit_right_alpha.tga` | `727aaffc9a8aba1ac47a9259a8af1a641d1453f3ca6528188cd96e83ce56d2a4` |
| `upperbody_color.tga` | `50571c9aee7289162eb10d7b5ddfea2c1feb9bf7941f255b828e5c7d88f5543f` |
| `upperbody_highlights_alpha.tga` | `b98e34fea0659604cf713263028641cadedc0c694f683c6c997089e951c48d72` |
| `upperbody_shading_alpha.tga` | `1f91154b45efdfabae9433b0e65bb2bf88721ca561c8762db1d932dcba666415` |
| `upperbodyfreckles_alpha.tga` | `ddf4c3aa27990bdd7d33c8762a87f50e87e32f0b5cb5660e108df00c8a68cf93` |
