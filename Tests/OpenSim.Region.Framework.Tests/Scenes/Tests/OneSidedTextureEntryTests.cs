/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Tests.Scenes.Tests;

/// <summary>
/// SceneObjectPart.UpdateTextureEntry(byte[]) on a one-sided prim (a sphere) moves face 0 into the default face. The
/// new default must carry face 0's effective values, so a face 0 that names only one attribute (here the material
/// byte, the way a texture entry with a single override reads back) keeps the texture, colour, repeats, offsets,
/// rotation, glow and material id the prim already showed.
/// </summary>
public class OneSidedTextureEntryTests
{
    private static SceneObjectPart Sphere(UUID texture, Color4 colour, float glow)
    {
        Scene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup sog = SceneHelpers.AddSceneObject(scene);
        SceneObjectPart part = sog.RootPart;
        PrimitiveBaseShape shape = PrimitiveBaseShape.CreateBox();
        shape.PathCurve = 32;
        shape.ProfileCurve = 5;
        part.Shape = shape;
        var te = new Primitive.TextureEntry(texture);
        te.DefaultTexture.RGBA = colour;
        te.DefaultTexture.Glow = glow;
        te.DefaultTexture.RepeatU = 2f;
        te.DefaultTexture.OffsetV = 0.25f;
        te.DefaultTexture.Rotation = 0.5f;
        part.Shape.Textures = te;
        Assert.Equal(1, part.GetNumberOfSides());
        return part;
    }

    // The bytes a script-side write sends: the stored entry with face 0 created and one attribute changed.
    private static byte[] WithFaceZeroFullbright(SceneObjectPart part, bool on)
    {
        Primitive.TextureEntry te = part.Shape.Textures;
        te.CreateFace(0).Fullbright = on;
        return te.GetBytes();
    }

    [Fact]
    public void Moving_face_zero_into_the_default_keeps_what_the_prim_already_showed()
    {
        TestHelpers.InMethod();
        UUID texture = TestHelpers.ParseTail(0x77);
        var colour = new Color4(0.2f, 0.4f, 0.6f, 1f);
        SceneObjectPart part = Sphere(texture, colour, 0.5f);

        part.UpdateTextureEntry(WithFaceZeroFullbright(part, true));

        Primitive.TextureEntry got = part.Shape.Textures;
        Primitive.TextureEntryFace face = got.GetFace(0);
        Assert.True(face.Fullbright);
        Assert.Equal(texture, face.TextureID);
        Assert.Equal(colour.R, face.RGBA.R, 2);
        Assert.Equal(colour.G, face.RGBA.G, 2);
        Assert.Equal(colour.B, face.RGBA.B, 2);
        Assert.Equal(0.5f, face.Glow, 2);
        Assert.Equal(2f, face.RepeatU, 2);
        Assert.Equal(0.25f, face.OffsetV, 2);
        Assert.Equal(0.5f, face.Rotation, 2);
    }

    [Fact]
    public void A_face_zero_that_overrides_the_default_becomes_the_new_default()
    {
        TestHelpers.InMethod();
        SceneObjectPart part = Sphere(Util.BLANK_TEXTURE_UUID, Color4.White, 0f);
        Primitive.TextureEntry te = part.Shape.Textures;
        te.DefaultTexture.Fullbright = true;
        part.Shape.Textures = te;

        // Face 0 is given its own material byte: on first, then off (the off value differs from its own stored byte
        // only after the first write has made one).
        te = part.Shape.Textures;
        te.CreateFace(0).Fullbright = true;
        te.CreateFace(0).Fullbright = false;
        part.UpdateTextureEntry(te.GetBytes());

        Primitive.TextureEntry got = part.Shape.Textures;
        Assert.False(got.GetFace(0).Fullbright);
        Assert.False(got.DefaultTexture.Fullbright);
    }
}
