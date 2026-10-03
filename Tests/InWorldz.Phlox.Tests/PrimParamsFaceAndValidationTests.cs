/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Linq;
using System.Text;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Prim-params validation and per-face reads, and the name and description caps:
/// - PRIM_FLEXIBLE turns the path curve to flexible (and back to straight) and makes the object phantom, as YEngine's
///   SetFlexi and Halcyon do; the SL wiki: flexible prims are phantom.
/// - PRIM_POINT_LIGHT clamps to the SL wiki's ranges; PRIM_MATERIAL outside 0..7 is ignored (Halcyon refused it).
/// - PRIM_GLOW with ALL_SIDES sets the prim's own faces and the default face; a face the prim lacks is ignored.
/// - The per-face reads give one group per face for ALL_SIDES and nothing for a face the prim lacks (Halcyon
///   GetPrimParams; SL: ALL_SIDES returns one entry per face).
/// - A multi-prim llGetLinkPrimitiveParams lists each rule for every prim before the next rule (Halcyon).
/// - Names are cut to 63 characters ("The name is limited to 63 characters"), descriptions to 127 bytes ("The prim
///   description is limited to 127 bytes"), by llSetObjectName, llSetObjectDesc, PRIM_NAME and PRIM_DESC.
/// </summary>
// Calls the API directly on the harness prim; no clock and no process-wide state, so the class runs in parallel.
public class PrimParamsFaceAndValidationTests
{
    private const int PRIM_MATERIAL = 2, PRIM_TEXTURE = 17, PRIM_COLOR = 18, PRIM_BUMP_SHINY = 19, PRIM_FULLBRIGHT = 20,
        PRIM_FLEXIBLE = 21, PRIM_POINT_LIGHT = 23, PRIM_GLOW = 25, PRIM_NAME = 27, PRIM_DESC = 28;
    private const int ALL_SIDES = -1, LINK_SET = -1;

    private static LSLSystemAPI Api(SchedulerHarness h, SceneObjectPart p) => new LSLSystemAPI(h.Engine, p, p.LocalId, UUID.Random());

    private static LSLList L(params object[] items) => new LSLList(items);

    private static SceneObjectPart AddChild(SchedulerHarness h, string name)
    {
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, name, h.Prim.OwnerID));
        return h.Prim.ParentGroup.Parts.Single(p => p.Name == name);
    }

    // ---- PRIM_FLEXIBLE ----

    [Fact]
    public void FlexibleOnMakesThePathCurveFlexibleAndTheObjectPhantom()
    {
        using var h = new SchedulerHarness();
        Assert.Equal((byte)Extrusion.Straight, h.Prim.Shape.PathCurve);
        Assert.False(h.Prim.ParentGroup.IsPhantom);
        Api(h, h.Prim).llSetPrimitiveParams(L(PRIM_FLEXIBLE, 1, 2, 0.3f, 2.0f, 0.0f, 1.0f, Vector3.Zero));
        Assert.True(h.Prim.Shape.FlexiEntry);
        Assert.Equal((byte)Extrusion.Flexible, h.Prim.Shape.PathCurve);
        Assert.True(h.Prim.ParentGroup.IsPhantom);
    }

    [Fact]
    public void FlexibleOffPutsThePathCurveBackToStraight()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        api.llSetPrimitiveParams(L(PRIM_FLEXIBLE, 1, 2, 0.3f, 2.0f, 0.0f, 1.0f, Vector3.Zero));
        api.llSetPrimitiveParams(L(PRIM_FLEXIBLE, 0, 2, 0.3f, 2.0f, 0.0f, 1.0f, Vector3.Zero));
        Assert.False(h.Prim.Shape.FlexiEntry);
        Assert.Equal((byte)Extrusion.Straight, h.Prim.Shape.PathCurve);
    }

    // ---- PRIM_POINT_LIGHT and PRIM_MATERIAL ----

    [Fact]
    public void PointLightIsClampedToTheSlRanges()
    {
        using var h = new SchedulerHarness();
        Api(h, h.Prim).llSetPrimitiveParams(L(PRIM_POINT_LIGHT, 1, new Vector3(2f, -1f, 0.5f), 3f, 50f, 9f));
        var s = h.Prim.Shape;
        Assert.True(s.LightEntry);
        Assert.Equal(1f, s.LightColorR);
        Assert.Equal(0f, s.LightColorG);
        Assert.Equal(0.5f, s.LightColorB);
        Assert.Equal(1f, s.LightIntensity);
        Assert.Equal(20f, s.LightRadius);
        Assert.Equal(2f, s.LightFalloff);
    }

    [Fact]
    public void AMaterialOutsideZeroToSevenIsIgnored()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        api.llSetPrimitiveParams(L(PRIM_MATERIAL, 4));
        Assert.Equal(4, h.Prim.Material);
        api.llSetPrimitiveParams(L(PRIM_MATERIAL, 9));
        Assert.Equal(4, h.Prim.Material);
    }

    // ---- PRIM_GLOW set ----

    [Fact]
    public void GlowOnAllSidesSetsThePrimsFacesAndTheDefaultFaceOnly()
    {
        using var h = new SchedulerHarness();
        int sides = h.Prim.GetNumberOfSides();
        Assert.True(sides < 8, "the test prim has fewer than eight faces");
        Api(h, h.Prim).llSetPrimitiveParams(L(PRIM_GLOW, ALL_SIDES, 0.5f));
        var te = h.Prim.Shape.Textures;
        // Glow travels as a byte (n / 255), so 0.5 reads back as 0.498.
        Assert.Equal(0.5f, te.DefaultTexture.Glow, 2);
        for (int f = 0; f < sides; f++) Assert.Equal(0.5f, te.GetFace((uint)f).Glow, 2);
        Assert.Null(te.FaceTextures[sides]);
    }

    [Fact]
    public void GlowOnAFaceThePrimLacksIsIgnored()
    {
        using var h = new SchedulerHarness();
        int sides = h.Prim.GetNumberOfSides();
        Api(h, h.Prim).llSetPrimitiveParams(L(PRIM_GLOW, sides + 1, 0.5f));
        Assert.Null(h.Prim.Shape.Textures.FaceTextures[sides + 1]);
    }

    // ---- per-face reads ----

    [Theory]
    [InlineData(PRIM_TEXTURE, 4)]
    [InlineData(PRIM_COLOR, 2)]
    [InlineData(PRIM_GLOW, 1)]
    [InlineData(PRIM_FULLBRIGHT, 1)]
    [InlineData(PRIM_BUMP_SHINY, 2)]
    public void AllSidesReadsOneGroupPerFace(int rule, int valuesPerFace)
    {
        using var h = new SchedulerHarness();
        int sides = h.Prim.GetNumberOfSides();
        var got = Api(h, h.Prim).llGetPrimitiveParams(L(rule, ALL_SIDES));
        Assert.Equal(sides * valuesPerFace, got.Length);
    }

    [Theory]
    [InlineData(PRIM_TEXTURE)]
    [InlineData(PRIM_COLOR)]
    [InlineData(PRIM_GLOW)]
    [InlineData(PRIM_FULLBRIGHT)]
    [InlineData(PRIM_BUMP_SHINY)]
    public void AFaceThePrimLacksReadsNothing(int rule)
    {
        using var h = new SchedulerHarness();
        int sides = h.Prim.GetNumberOfSides();
        var got = Api(h, h.Prim).llGetPrimitiveParams(L(rule, sides + 1));
        Assert.Equal(0, got.Length);
    }

    [Fact]
    public void AllSidesGlowReadsEachFacesOwnValue()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        api.llSetPrimitiveParams(L(PRIM_GLOW, 1, 0.25f));
        var got = api.llGetPrimitiveParams(L(PRIM_GLOW, ALL_SIDES));
        Assert.Equal(0f, (float)got.Data[0], 3);
        Assert.Equal(0.25f, (float)got.Data[1], 2);
    }

    // ---- llGetLinkPrimitiveParams order ----

    [Fact]
    public void AMultiPrimReadListsEachRuleForEveryPrimBeforeTheNextRule()
    {
        using var h = new SchedulerHarness();
        h.Prim.Name = "root";
        h.Prim.Description = "root desc";
        var child = AddChild(h, "child");
        child.Description = "child desc";
        var got = Api(h, h.Prim).llGetLinkPrimitiveParams(LINK_SET, L(PRIM_NAME, PRIM_DESC));
        Assert.Equal(new object[] { "root", "child", "root desc", "child desc" }, got.Data.Select(o => (object)o.ToString()).ToArray());
    }

    // ---- name and description caps ----

    [Fact]
    public void SetObjectNameCutsTheNameTo63Characters()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        api.llSetObjectName(new string('n', 100));
        Assert.Equal(new string('n', 63), api.llGetObjectName());
    }

    [Fact]
    public void SetObjectDescCutsTheDescriptionTo127Bytes()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        api.llSetObjectDesc(new string('d', 200));
        Assert.Equal(new string('d', 127), api.llGetObjectDesc());
    }

    [Fact]
    public void ADescriptionCutDropsACharacterTheCutWouldSplit()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        // 126 one-byte characters and one two-byte character: 128 bytes. Cutting at 127 would split the last one.
        string e = ((char)0xE9).ToString();
        api.llSetObjectDesc(new string('d', 126) + e + "x");
        string got = api.llGetObjectDesc();
        Assert.Equal(new string('d', 126), got);
        Assert.True(Encoding.UTF8.GetByteCount(got) <= 127);
    }

    [Fact]
    public void PrimNameAndPrimDescAreCutTheSameWay()
    {
        using var h = new SchedulerHarness();
        Api(h, h.Prim).llSetPrimitiveParams(L(PRIM_NAME, new string('n', 100), PRIM_DESC, new string('d', 200)));
        Assert.Equal(new string('n', 63), h.Prim.Name);
        Assert.Equal(new string('d', 127), h.Prim.Description);
    }

    [Fact]
    public void ShortNamesAndDescriptionsAreKeptWhole()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        api.llSetObjectName("Example name");
        api.llSetObjectDesc("Example description");
        Assert.Equal("Example name", api.llGetObjectName());
        Assert.Equal("Example description", api.llGetObjectDesc());
    }
}
