/*
 * Copyright (c) Contributors, http://opensimulator.org/
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

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;
using Xunit;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// YEngine's scripted glTF materials and overrides (https://wiki.secondlife.com/wiki/GLTF_Overrides):
    /// PRIM_RENDER_MATERIAL and PRIM_GLTF_* in the prim-params calls, llSetRenderMaterial, llSetLinkRenderMaterial and
    /// llSetLinkGLTFOverrides, stored in the compact form viewers read (GltfMaterialOverride).
    /// </summary>
    // Each test builds its own scene and object and touches no process-wide state, so the class runs in parallel.
    public class LSL_ApiGltfTests
    {
        private static readonly UUID Mat = new("33333333-0000-0000-0000-000000000001");
        private static readonly UUID Tex = new("33333333-0000-0000-0000-000000000002");

        private readonly SceneObjectGroup m_group;
        private readonly SceneObjectPart m_part;
        private readonly LSL_Api m_api;

        public LSL_ApiGltfTests()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");

            Scene scene = new SceneHelpers().SetupScene();
            m_group = SceneHelpers.AddSceneObject(scene, 2, UUID.Random(), "gltf-", 0x10);
            m_part = m_group.RootPart;

            MockScriptEngine engine = new MockScriptEngine();
            engine.Initialise(config);
            engine.AddRegion(scene);

            m_api = new LSL_Api();
            m_api.Initialize(engine, m_part, null);
        }

        private static LSL_String S(string s) => new(s);
        private static LSL_Integer I(int i) => new(i);
        private static LSL_Float F(double f) => new(f);
        private static LSL_Vector V(double x, double y, double z) => new(x, y, z);
        private static readonly LSL_String Unset = new(string.Empty);

        private GltfMaterialOverride Ovr(int face) => GltfMaterialOverride.Get(m_part.Shape, face);

        [Fact]
        public void BaseColorIsStoredInTheFormViewersRead()
        {
            m_api.llSetRenderMaterial(S(Mat.ToString()), I(0));
            m_api.llSetPrimitiveParams(new LSL_List(
                I(ScriptBaseClass.PRIM_GLTF_BASE_COLOR), I(0), S(""), V(2, 3, 0), V(0.25, 0.5, 0), F(1.0),
                V(1, 0, 0), F(0.5), I(ScriptBaseClass.PRIM_GLTF_ALPHA_MODE_BLEND), F(0.3), I(1)));

            var entry = Assert.Single(m_part.Shape.RenderMaterials.overrides);
            var map = (OSDMap)OSDParser.DeserializeLLSDNotation(entry.data);
            Assert.Equal(new[] { "ac", "am", "bc", "ds", "ti" }, map.Keys.OrderBy(k => k));
            Assert.Equal(new[] { 1.0, 0.0, 0.0, 0.5 }, ((OSDArray)map["bc"]).Select(o => o.AsReal()));
            GltfMaterialOverride o = Ovr(0);
            Assert.Equal(new Vector2(2, 3), o.GetScale(GltfMaterialOverride.SLOT_BASE_COLOR));
            Assert.Equal(new Vector2(0.25f, 0.5f), o.GetOffset(GltfMaterialOverride.SLOT_BASE_COLOR));
            Assert.Equal(1, o.AlphaMode);
            Assert.True(o.DoubleSided);
        }

        [Fact]
        public void TheRulesMergeAndEmptyStringsClear()
        {
            m_api.llSetRenderMaterial(S(Mat.ToString()), I(ScriptBaseClass.ALL_SIDES));
            m_api.llSetPrimitiveParams(new LSL_List(
                I(ScriptBaseClass.PRIM_GLTF_METALLIC_ROUGHNESS), I(1), Unset, Unset, Unset, Unset, F(0.25), F(0.75),
                I(ScriptBaseClass.PRIM_GLTF_NORMAL), I(1), S(Tex.ToString()), Unset, Unset, F(0.5)));

            GltfMaterialOverride o = Ovr(1);
            Assert.Equal(0.25f, o.MetallicFactor);
            Assert.Equal(Tex, o.GetTexture(GltfMaterialOverride.SLOT_NORMAL));

            m_api.llSetPrimitiveParams(new LSL_List(
                I(ScriptBaseClass.PRIM_GLTF_METALLIC_ROUGHNESS), I(1), Unset, Unset, Unset, Unset, Unset, F(0.75)));

            o = Ovr(1);
            Assert.Null(o.MetallicFactor);
            Assert.Equal(0.75f, o.RoughnessFactor);
            Assert.Equal(Tex, o.GetTexture(GltfMaterialOverride.SLOT_NORMAL));
        }

        [Fact]
        public void AnUnsetOverrideReadsAsTheEmptyString()
        {
            m_api.llSetRenderMaterial(S(Mat.ToString()), I(0));
            m_api.llSetPrimitiveParams(new LSL_List(
                I(ScriptBaseClass.PRIM_GLTF_EMISSIVE), I(0), Unset, Unset, Unset, F(1.5), V(0, 1, 0)));

            LSL_List res = m_api.llGetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_GLTF_EMISSIVE), I(0)));

            Assert.Equal(5, res.Length);
            Assert.Equal("", res.GetStringItem(0));
            Assert.Equal("", res.GetStringItem(1));
            Assert.Equal("", res.GetStringItem(2));
            Assert.Equal(1.5f, res.GetStrictFloatItem(3));
            Assert.Equal(new LSL_Vector(0, 1, 0), res.GetVector3Item(4));
        }

        [Fact]
        public void TheRulesAfterAGltfRuleStillApply()
        {
            m_api.llSetPrimitiveParams(new LSL_List(
                I(ScriptBaseClass.PRIM_GLTF_NORMAL), I(ScriptBaseClass.ALL_SIDES), Unset, V(2, 2, 0), Unset, Unset,
                I(ScriptBaseClass.PRIM_NAME), S("after")));

            Assert.Equal("after", m_part.Name);
            // no material on the faces: only the texture transform is kept
            Assert.Equal(new Vector2(2, 2), Ovr(0).GetScale(GltfMaterialOverride.SLOT_NORMAL));
        }

        [Fact]
        public void AMissingTextureChangesNothing()
        {
            m_api.llSetRenderMaterial(S(Mat.ToString()), I(0));
            m_api.llSetPrimitiveParams(new LSL_List(
                I(ScriptBaseClass.PRIM_GLTF_NORMAL), I(0), S("no such texture"), Unset, Unset, Unset));

            Assert.True(Ovr(0).IsEmpty);
        }

        [Fact]
        public void PrimRenderMaterialSetsAndRemoves()
        {
            m_api.llSetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_RENDER_MATERIAL), I(ScriptBaseClass.ALL_SIDES), S(Mat.ToString())));
            Assert.Equal(1, m_api.llIsLinkGLTFMaterial(I(ScriptBaseClass.LINK_THIS), I(ScriptBaseClass.ALL_SIDES)).value);

            m_api.llSetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_RENDER_MATERIAL), I(0), S(ScriptBaseClass.NULL_KEY)));

            Assert.Equal(UUID.Zero, GltfMaterialOverride.GetRenderMaterial(m_part.Shape, 0));
            Assert.Equal(Mat, GltfMaterialOverride.GetRenderMaterial(m_part.Shape, 1));
            Assert.Equal(0, m_api.llIsLinkGLTFMaterial(I(ScriptBaseClass.LINK_THIS), I(ScriptBaseClass.ALL_SIDES)).value);
            Assert.Equal(1, m_api.llIsLinkGLTFMaterial(I(ScriptBaseClass.LINK_THIS), I(1)).value);
            Assert.Equal(ScriptBaseClass.NULL_KEY, m_api.llGetRenderMaterial(I(0)).m_string);

            LSL_List res = m_api.llGetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_RENDER_MATERIAL), I(1)));
            Assert.Equal(Mat.ToString(), res.GetStringItem(0));
        }

        [Fact]
        public void GetRenderMaterialOfABareFaceIsNullKey()
        {
            // a face with no entry while another face has one used to throw from Enumerable.First
            m_api.llSetRenderMaterial(S(Mat.ToString()), I(0));

            Assert.Equal(ScriptBaseClass.NULL_KEY, m_api.llGetRenderMaterial(I(1)).m_string);
        }

        [Fact]
        public void ANewMaterialKeepsOnlyTheTransforms()
        {
            m_api.llSetRenderMaterial(S(Mat.ToString()), I(0));
            m_api.llSetLinkGLTFOverrides(I(ScriptBaseClass.LINK_THIS), I(0), new LSL_List(
                I(ScriptBaseClass.OVERRIDE_GLTF_METALLIC_FACTOR), F(0.5)));
            m_api.llSetPrimitiveParams(new LSL_List(
                I(ScriptBaseClass.PRIM_GLTF_BASE_COLOR), I(0), Unset, V(4, 4, 0), Unset, Unset, Unset, Unset, Unset, Unset, Unset));

            m_api.llSetRenderMaterial(S(Tex.ToString()), I(0));

            GltfMaterialOverride o = Ovr(0);
            Assert.Null(o.MetallicFactor);
            Assert.Equal(new Vector2(4, 4), o.GetScale(GltfMaterialOverride.SLOT_BASE_COLOR));
        }

        [Fact]
        public void LinkGltfOverridesTakeSlsConstants()
        {
            m_api.llSetRenderMaterial(S(Mat.ToString()), I(0));

            m_api.llSetLinkGLTFOverrides(I(ScriptBaseClass.LINK_THIS), I(0), new LSL_List(
                I(ScriptBaseClass.OVERRIDE_GLTF_BASE_ALPHA), F(0.5),
                I(ScriptBaseClass.OVERRIDE_GLTF_BASE_ALPHA_MODE), I(ScriptBaseClass.PRIM_GLTF_ALPHA_MODE_BLEND),
                I(ScriptBaseClass.OVERRIDE_GLTF_EMISSIVE_FACTOR), V(0, 0, 1)));

            GltfMaterialOverride o = Ovr(0);
            Assert.Equal(0.5f, o.BaseAlpha);
            Assert.Equal(1, o.AlphaMode);
            Assert.Equal(new Vector3(0, 0, 1), o.EmissiveColor);

            // base color and alpha are one override: clearing the alpha alone puts it back to its default, 1.0
            m_api.llSetLinkGLTFOverrides(I(ScriptBaseClass.LINK_THIS), I(0), new LSL_List(
                I(ScriptBaseClass.OVERRIDE_GLTF_BASE_ALPHA), Unset));

            Assert.Equal(1f, Ovr(0).BaseAlpha);
            Assert.Equal(1, Ovr(0).AlphaMode);

            m_api.llSetLinkGLTFOverrides(I(ScriptBaseClass.LINK_THIS), I(0), new LSL_List(
                I(ScriptBaseClass.OVERRIDE_GLTF_BASE_ALPHA), Unset, I(ScriptBaseClass.OVERRIDE_GLTF_BASE_COLOR_FACTOR), Unset));

            Assert.Null(Ovr(0).BaseAlpha);
            Assert.Null(Ovr(0).BaseColor);
            Assert.Equal(1, Ovr(0).AlphaMode);
        }

        [Fact]
        public void LinkRenderMaterialSetsTheChildsMaterial()
        {
            SceneObjectPart child = m_group.GetLinkNumPart(2);

            m_api.llSetLinkRenderMaterial(I(2), S(Mat.ToString()), I(ScriptBaseClass.ALL_SIDES));

            for (int f = 0; f < child.GetNumberOfSides(); f++)
                Assert.Equal(Mat, GltfMaterialOverride.GetRenderMaterial(child.Shape, f));
            Assert.Equal(UUID.Zero, GltfMaterialOverride.GetRenderMaterial(m_part.Shape, 0));
        }
    }
}
