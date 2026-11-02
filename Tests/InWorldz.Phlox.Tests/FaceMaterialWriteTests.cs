/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The per-face material writes (PRIM_FULLBRIGHT, PRIM_BUMP_SHINY, PRIM_TEXGEN) on a one-sided prim (sphere), a
/// six-sided prim (box) and a three-sided prim (cylinder), starting from the texture entry a freshly built prim has
/// (a default face only, no face of its own). Answers are the SL wiki's: a rule with a face number changes that
/// face only, ALL_SIDES changes every face, and writing the off value turns the attribute off again.
/// https://wiki.secondlife.com/wiki/LlSetPrimitiveParams
/// </summary>
// Calls the API directly on the harness prim; no clock and no process-wide state, so the class runs in parallel.
public class FaceMaterialWriteTests
{
    private const int PRIM_BUMP_SHINY = 19, PRIM_FULLBRIGHT = 20, PRIM_TEXGEN = 22, PRIM_GLOW = 25;
    private const int PRIM_TEXGEN_DEFAULT = 0, PRIM_TEXGEN_PLANAR = 1;
    private const int ALL_SIDES = -1, LINK_THIS = -4;

    private readonly ITestOutputHelper m_out;
    public FaceMaterialWriteTests(ITestOutputHelper output) { m_out = output; }

    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());
    private static LSLList L(params object[] items) => new LSLList(items);

    public enum Kind { Sphere, Box, Cylinder }
    public enum Call { Own, LinkFast }

    // The prim as the region builder makes it: the shape, and a texture entry with a default face and no faces of its own.
    private static void Shape(SchedulerHarness h, Kind kind, UUID? texture = null, Color4? color = null)
    {
        // The wire values a viewer sends for each shape: path curve (line 16, circle 32) and profile curve.
        var shape = PrimitiveBaseShape.CreateBox();
        (shape.PathCurve, shape.ProfileCurve) = kind switch
        {
            Kind.Sphere => ((byte)32, (byte)5),
            Kind.Box => ((byte)16, (byte)1),
            _ => ((byte)16, (byte)0),
        };
        h.Prim.Shape = shape;
        var te = new Primitive.TextureEntry(texture ?? OpenSim.Framework.Util.BLANK_TEXTURE_UUID);
        if (color.HasValue) te.DefaultTexture.RGBA = color.Value;
        h.Prim.Shape.Textures = te;
    }

    private static int Sides(Kind kind) => kind switch { Kind.Sphere => 1, Kind.Box => 6, _ => 3 };

    private void Write(SchedulerHarness h, Call call, params object[] rule)
    {
        var api = Api(h);
        if (call == Call.Own) api.llSetPrimitiveParams(L(rule));
        else api.llSetLinkPrimitiveParamsFast(LINK_THIS, L(rule));
        m_out.WriteLine("TE " + BitConverter.ToString(h.Prim.Shape.TextureEntry).Replace("-", ""));
    }

    // What a viewer shows: each face's attribute, from the stored bytes.
    private static string FullbrightFaces(SceneObjectPart p)
    {
        var te = p.Shape.Textures;
        return string.Concat(Enumerable.Range(0, p.GetNumberOfSides()).Select(i => te.GetFace((uint)i).Fullbright ? "1" : "0"));
    }

    // What the script reads back.
    private static string Read(SchedulerHarness h, int rule) =>
        string.Concat(Api(h).llGetPrimitiveParams(L(rule, ALL_SIDES)).Data.Select(o => o.ToString()));

    private static string Expected(int sides, int face, bool on) =>
        new string(Enumerable.Range(0, sides).Select(i => on && (face == ALL_SIDES || i == face) ? '1' : '0').ToArray());

    [Theory]
    [InlineData(Kind.Sphere, 0, Call.Own)] [InlineData(Kind.Sphere, ALL_SIDES, Call.Own)]
    [InlineData(Kind.Sphere, 0, Call.LinkFast)] [InlineData(Kind.Sphere, ALL_SIDES, Call.LinkFast)]
    [InlineData(Kind.Box, 0, Call.Own)] [InlineData(Kind.Box, ALL_SIDES, Call.Own)]
    [InlineData(Kind.Box, 0, Call.LinkFast)] [InlineData(Kind.Box, ALL_SIDES, Call.LinkFast)]
    [InlineData(Kind.Cylinder, 0, Call.Own)] [InlineData(Kind.Cylinder, ALL_SIDES, Call.Own)]
    [InlineData(Kind.Cylinder, 0, Call.LinkFast)] [InlineData(Kind.Cylinder, ALL_SIDES, Call.LinkFast)]
    public void FullbrightOnOffOnOffGivesTheSlAnswerAtEveryStep(Kind kind, int face, Call call)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        int sides = h.Prim.GetNumberOfSides();
        Assert.Equal(Sides(kind), sides);
        Assert.Equal(Expected(sides, face, false), Read(h, PRIM_FULLBRIGHT));
        foreach (bool on in new[] { true, false, true, false })
        {
            Write(h, call, PRIM_FULLBRIGHT, face, on ? 1 : 0);
            Assert.Equal(Expected(sides, face, on), Read(h, PRIM_FULLBRIGHT));
            Assert.Equal(Expected(sides, face, on), FullbrightFaces(h.Prim));
        }
    }

    [Theory]
    [InlineData(Kind.Sphere)] [InlineData(Kind.Box)] [InlineData(Kind.Cylinder)]
    public void AllSidesOnThenOneFaceOffLeavesTheOtherFacesOn(Kind kind)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        int sides = h.Prim.GetNumberOfSides();
        Write(h, Call.Own, PRIM_FULLBRIGHT, ALL_SIDES, 1);
        Write(h, Call.Own, PRIM_FULLBRIGHT, 0, 0);
        // A one-sided prim has only face 0, so it is off; otherwise only face 0 is off.
        string want = sides == 1 ? "0" : "0" + new string('1', sides - 1);
        Assert.Equal(want, Read(h, PRIM_FULLBRIGHT));
    }

    [Theory]
    [InlineData(Kind.Sphere, 0)] [InlineData(Kind.Sphere, ALL_SIDES)]
    [InlineData(Kind.Box, 0)] [InlineData(Kind.Box, ALL_SIDES)]
    [InlineData(Kind.Cylinder, 0)] [InlineData(Kind.Cylinder, ALL_SIDES)]
    public void ShinyAndBumpSetThenClearedGiveTheSlAnswerAtEveryStep(Kind kind, int face)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        int sides = h.Prim.GetNumberOfSides();
        string Want(bool on) => string.Concat(Enumerable.Range(0, sides)
            .Select(i => on && (face == ALL_SIDES || i == face) ? "21" : "00"));
        Assert.Equal(Want(false), Read(h, PRIM_BUMP_SHINY));
        foreach (bool on in new[] { true, false, true, false })
        {
            Write(h, Call.Own, PRIM_BUMP_SHINY, face, on ? 2 : 0, on ? 1 : 0);
            Assert.Equal(Want(on), Read(h, PRIM_BUMP_SHINY));
        }
    }

    [Theory]
    [InlineData(Kind.Sphere, 0)] [InlineData(Kind.Sphere, ALL_SIDES)]
    [InlineData(Kind.Box, 0)] [InlineData(Kind.Box, ALL_SIDES)]
    [InlineData(Kind.Cylinder, 0)] [InlineData(Kind.Cylinder, ALL_SIDES)]
    public void TexgenPlanarThenDefaultGiveTheSlAnswerAtEveryStep(Kind kind, int face)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        int sides = h.Prim.GetNumberOfSides();
        string Want(bool planar) => string.Concat(Enumerable.Range(0, sides)
            .Select(i => planar && (face == ALL_SIDES || i == face) ? "1" : "0"));
        foreach (bool planar in new[] { true, false, true, false })
        {
            Write(h, Call.Own, PRIM_TEXGEN, face, planar ? PRIM_TEXGEN_PLANAR : PRIM_TEXGEN_DEFAULT);
            Assert.Equal(Want(planar), Read(h, PRIM_TEXGEN));
        }
    }

    // A face written on a prim that already has a texture and colour must leave them alone.
    [Theory]
    [InlineData(Kind.Sphere)] [InlineData(Kind.Box)] [InlineData(Kind.Cylinder)]
    public void AFaceZeroFullbrightAndGlowWriteKeepTheTextureAndColour(Kind kind)
    {
        using var h = new SchedulerHarness();
        var tex = UUID.Random();
        var colour = new Color4(0.2f, 0.4f, 0.6f, 1f);
        Shape(h, kind, tex, colour);
        Write(h, Call.Own, PRIM_FULLBRIGHT, 0, 1);
        Write(h, Call.Own, PRIM_GLOW, 0, 0.5f);
        Write(h, Call.Own, PRIM_BUMP_SHINY, 0, 2, 1);
        Write(h, Call.Own, PRIM_TEXGEN, 0, PRIM_TEXGEN_PLANAR);
        var te = h.Prim.Shape.Textures;
        for (uint f = 0; f < h.Prim.GetNumberOfSides(); f++)
        {
            Assert.Equal(tex, te.GetFace(f).TextureID);
            Assert.Equal(colour.R, te.GetFace(f).RGBA.R, 2);
            Assert.Equal(colour.G, te.GetFace(f).RGBA.G, 2);
            Assert.Equal(colour.B, te.GetFace(f).RGBA.B, 2);
        }
    }

    // A face that inherits a value from the default face must still take the off value (OpenMetaverse compares
    // the new byte with the face's own stored byte, so these were dropped).
    [Theory]
    [InlineData(Kind.Box)] [InlineData(Kind.Cylinder)]
    public void ShinyAndBumpOnAllSidesThenNoneOnOneFaceTakesEffect(Kind kind)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        int sides = h.Prim.GetNumberOfSides();
        Write(h, Call.Own, PRIM_BUMP_SHINY, ALL_SIDES, 3, 4);
        Write(h, Call.Own, PRIM_BUMP_SHINY, 0, 0, 0);
        string want = "00" + string.Concat(Enumerable.Repeat("34", sides - 1));
        Assert.Equal(want, Read(h, PRIM_BUMP_SHINY));
    }

    [Theory]
    [InlineData(Kind.Box)] [InlineData(Kind.Cylinder)]
    public void PlanarOnAllSidesThenDefaultOnOneFaceTakesEffect(Kind kind)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        int sides = h.Prim.GetNumberOfSides();
        Write(h, Call.Own, PRIM_TEXGEN, ALL_SIDES, PRIM_TEXGEN_PLANAR);
        Write(h, Call.Own, PRIM_TEXGEN, 0, PRIM_TEXGEN_DEFAULT);
        Assert.Equal("0" + new string('1', sides - 1), Read(h, PRIM_TEXGEN));
    }

    [Theory]
    [InlineData(Kind.Sphere)] [InlineData(Kind.Box)] [InlineData(Kind.Cylinder)]
    public void AFaceThePrimLacksIsIgnoredByTheMaterialWrites(Kind kind)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        int sides = h.Prim.GetNumberOfSides();
        string before = BitConverter.ToString(h.Prim.Shape.TextureEntry);
        Write(h, Call.Own, PRIM_FULLBRIGHT, sides + 1, 1);
        Write(h, Call.Own, PRIM_BUMP_SHINY, sides + 1, 2, 1);
        Write(h, Call.Own, PRIM_TEXGEN, sides + 1, PRIM_TEXGEN_PLANAR);
        Assert.Equal(before, BitConverter.ToString(h.Prim.Shape.TextureEntry));
    }

    // ---- change events: CHANGED_TEXTURE for a texture attribute, CHANGED_COLOR for colour, nothing for no change ----

    private const int PRIM_TEXTURE = 17, PRIM_COLOR = 18;
    private const uint CHANGED_COLOR = 2, CHANGED_TEXTURE = 16;

    private static System.Collections.Generic.List<uint> WatchChanges(SchedulerHarness h)
    {
        var seen = new System.Collections.Generic.List<uint>();
        h.Prim.SetScriptEvents(UUID.Random(), (ulong)scriptEvents.changed);
        h.Scene.EventManager.OnScriptChangedEvent += (id, change, data) => { lock (seen) seen.Add(change); };
        return seen;
    }

    [Theory]
    [InlineData(Kind.Sphere)] [InlineData(Kind.Box)]
    public void AMaterialWriteReportsChangedTextureAndNoChangeReportsNothing(Kind kind)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        var seen = WatchChanges(h);
        Write(h, Call.Own, PRIM_FULLBRIGHT, ALL_SIDES, 1);
        Assert.Equal(new[] { CHANGED_TEXTURE }, seen.ToArray());
        seen.Clear();
        Write(h, Call.Own, PRIM_FULLBRIGHT, ALL_SIDES, 1);   // already on: nothing changed
        Assert.Empty(seen);
        Write(h, Call.Own, PRIM_BUMP_SHINY, ALL_SIDES, 2, 1);
        Assert.Equal(new[] { CHANGED_TEXTURE }, seen.ToArray());
        seen.Clear();
        Write(h, Call.Own, PRIM_TEXGEN, ALL_SIDES, PRIM_TEXGEN_PLANAR);
        Assert.Equal(new[] { CHANGED_TEXTURE }, seen.ToArray());
        seen.Clear();
        Write(h, Call.Own, PRIM_GLOW, ALL_SIDES, 0.5f);
        Assert.Equal(new[] { CHANGED_TEXTURE }, seen.ToArray());
    }

    [Theory]
    [InlineData(Kind.Sphere)] [InlineData(Kind.Box)]
    public void ColourAndTextureWritesReportChangedColorAndChangedTexture(Kind kind)
    {
        using var h = new SchedulerHarness();
        Shape(h, kind);
        var seen = WatchChanges(h);
        Write(h, Call.Own, PRIM_COLOR, ALL_SIDES, new Vector3(1f, 0f, 0f), 0.5f);
        Assert.Equal(CHANGED_COLOR, seen.Aggregate(0u, (a, c) => a | c) & CHANGED_COLOR);
        seen.Clear();
        Write(h, Call.Own, PRIM_TEXTURE, ALL_SIDES, UUID.Random().ToString(), new Vector3(1f, 1f, 0f), Vector3.Zero, 0f);
        Assert.NotEqual(0u, seen.Aggregate(0u, (a, c) => a | c) & CHANGED_TEXTURE);
        // The texture write names a new texture id on every face.
        var te = h.Prim.Shape.Textures;
        for (uint f = 0; f < h.Prim.GetNumberOfSides(); f++) Assert.NotEqual(OpenSim.Framework.Util.BLANK_TEXTURE_UUID, te.GetFace(f).TextureID);
    }
}
