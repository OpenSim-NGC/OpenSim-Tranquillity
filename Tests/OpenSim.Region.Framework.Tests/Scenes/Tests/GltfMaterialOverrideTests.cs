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

using OpenMetaverse.StructuredData;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// GltfMaterialOverride: the per-face glTF override store both script engines write through. The stored data must
    /// be the compact LLSD the viewer reads (LLGLTFMaterial::applyOverrideLLSD), since LLClientView sends it as is.
    /// </summary>
    public class GltfMaterialOverrideTests : OpenSimTestCase
    {
        private static readonly UUID Material = new("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly UUID OtherMaterial = new("aaaaaaaa-0000-0000-0000-000000000002");
        private static readonly UUID Normal = new("bbbbbbbb-0000-0000-0000-000000000001");
        private static readonly UUID Emissive = new("bbbbbbbb-0000-0000-0000-000000000002");

        private static SceneObjectPart NewPart()
        {
            TestScene scene = new SceneHelpers().SetupScene();
            return SceneHelpers.AddSceneObject(scene).RootPart;
        }

        private static OSDMap Stored(SceneObjectPart part, int face)
        {
            foreach (var ovr in part.Shape.RenderMaterials.overrides)
            {
                if (ovr.te_index == face)
                    return (OSDMap)OSDParser.DeserializeLLSDNotation(ovr.data);
            }
            return null;
        }

        [Fact]
        public void TheStoredDataUsesTheViewersCompactKeys()
        {
            TestHelpers.InMethod();
            GltfMaterialOverride ovr = new();
            ovr.SetTexture(GltfMaterialOverride.SLOT_NORMAL, Normal);
            ovr.SetScale(GltfMaterialOverride.SLOT_BASE_COLOR, new Vector2(2, 3));
            ovr.SetOffset(GltfMaterialOverride.SLOT_BASE_COLOR, new Vector2(0.25f, -0.5f));
            ovr.SetRotation(GltfMaterialOverride.SLOT_BASE_COLOR, 1.5f);
            ovr.BaseColor = new Vector3(1, 0, 0);
            ovr.BaseAlpha = 0.5f;
            ovr.EmissiveColor = new Vector3(0, 1, 0);
            ovr.MetallicFactor = 0.25f;
            ovr.RoughnessFactor = 0.75f;
            ovr.AlphaMode = GltfMaterialOverride.ALPHA_MODE_MASK;
            ovr.AlphaCutoff = 0.3f;
            ovr.DoubleSided = true;

            var map = (OSDMap)OSDParser.DeserializeLLSDNotation(ovr.ToData());

            var tex = (OSDArray)map["tex"];
            tex.Count.Should().Be(2);
            tex[0].Type.Should().Be(OSDType.Unknown);
            tex[1].AsUUID().Should().Be(Normal);

            var ti = (OSDMap)((OSDArray)map["ti"])[0];
            ((OSDArray)ti["s"])[0].AsReal().Should().Be(2);
            ((OSDArray)ti["s"])[1].AsReal().Should().Be(3);
            ((OSDArray)ti["o"])[0].AsReal().Should().Be(0.25);
            ((OSDArray)ti["o"])[1].AsReal().Should().Be(-0.5);
            ti["r"].AsReal().Should().Be(1.5);

            ((OSDArray)map["bc"]).Select(o => o.AsReal()).Should().Equal(1, 0, 0, 0.5);
            ((OSDArray)map["ec"]).Select(o => o.AsReal()).Should().Equal(0, 1, 0);
            map["mf"].Type.Should().Be(OSDType.Real);
            map["mf"].AsReal().Should().Be(0.25);
            map["rf"].AsReal().Should().Be(0.75);
            map["am"].Type.Should().Be(OSDType.Integer);
            map["am"].AsInteger().Should().Be(2);
            map["ac"].AsReal().Should().BeApproximately(0.3, 1e-6);
            map["ds"].Type.Should().Be(OSDType.Boolean);
            map["ds"].AsBoolean().Should().BeTrue();
            map.Keys.Should().BeEquivalentTo(new[] { "tex", "ti", "bc", "ec", "mf", "rf", "am", "ac", "ds" });
        }

        [Fact]
        public void DataRoundTrips()
        {
            TestHelpers.InMethod();
            GltfMaterialOverride ovr = new();
            ovr.SetTexture(GltfMaterialOverride.SLOT_EMISSIVE, Emissive);
            ovr.SetRotation(GltfMaterialOverride.SLOT_NORMAL, 0.5f);
            ovr.RoughnessFactor = 0.2f;
            ovr.DoubleSided = false;

            GltfMaterialOverride back = GltfMaterialOverride.FromData(ovr.ToData());

            back.GetTexture(GltfMaterialOverride.SLOT_EMISSIVE).Should().Be(Emissive);
            back.GetTexture(GltfMaterialOverride.SLOT_BASE_COLOR).Should().BeNull();
            back.GetRotation(GltfMaterialOverride.SLOT_NORMAL).Should().Be(0.5f);
            back.GetScale(GltfMaterialOverride.SLOT_NORMAL).Should().BeNull();
            back.RoughnessFactor.Should().BeApproximately(0.2f, 1e-6f);
            back.MetallicFactor.Should().BeNull();
            back.DoubleSided.Should().BeFalse();
            back.BaseColor.Should().BeNull();
            back.ToData().Should().Be(ovr.ToData());
        }

        [Fact]
        public void AnAlphaAloneOverridesTheBaseColorAsWhite()
        {
            TestHelpers.InMethod();
            // SL llSetLinkGLTFOverrides: OVERRIDE_GLTF_BASE_COLOR_FACTOR and OVERRIDE_GLTF_BASE_ALPHA are coupled
            GltfMaterialOverride ovr = new() { BaseAlpha = 0.4f };

            var map = (OSDMap)OSDParser.DeserializeLLSDNotation(ovr.ToData());

            ((OSDArray)map["bc"]).Select(o => o.AsReal()).Should().Equal(1, 1, 1, 0.4);
        }

        [Fact]
        public void NothingOverriddenStoresNothing()
        {
            TestHelpers.InMethod();
            new GltfMaterialOverride().ToData().Should().BeNull();
        }

        [Fact]
        public void DataWrittenByTheMaterialsModuleIsRead()
        {
            TestHelpers.InMethod();
            // as MaterialsModule.AddMaterialOverride stores a viewer edit
            string data = "{'bc':[r0.5,r0.25,r1,r1],'mf':r0,'ti':[{'o':[r0.1,r0.2],'r':r0.7,'s':[r4,r5]}]," +
                          "'tex':[u" + Normal + ",!,!,u" + Emissive + "]}";

            GltfMaterialOverride ovr = GltfMaterialOverride.FromData(data);

            ovr.BaseColor.Should().Be(new Vector3(0.5f, 0.25f, 1f));
            ovr.BaseAlpha.Should().Be(1f);
            ovr.MetallicFactor.Should().Be(0f);
            ovr.GetOffset(0).Should().Be(new Vector2(0.1f, 0.2f));
            ovr.GetScale(0).Should().Be(new Vector2(4, 5));
            ovr.GetRotation(0).Should().BeApproximately(0.7f, 1e-6f);
            ovr.GetTexture(0).Should().Be(Normal);
            ovr.GetTexture(1).Should().BeNull();
            ovr.GetTexture(3).Should().Be(Emissive);
        }

        [Fact]
        public void DataWrittenByEarlierPhloxBuildsIsRepaired()
        {
            TestHelpers.InMethod();
            var legacy = new OSDMap
            {
                ["tex"] = OSD.FromString(Normal.ToString()),
                ["rep"] = new OSDArray { 2.0, 2.0 },
                ["off"] = new OSDArray { 0.0, 0.5 },
                ["rot"] = 0.25,
                ["etex"] = OSD.FromString(Emissive.ToString()),
                ["bc"] = new OSDArray { 1.0, 0.0, 0.0, 1.0 },
                ["am"] = 1,
            };

            GltfMaterialOverride ovr = GltfMaterialOverride.FromData(OSDParser.SerializeLLSDNotation(legacy));
            var map = (OSDMap)OSDParser.DeserializeLLSDNotation(ovr.ToData());

            ovr.GetTexture(GltfMaterialOverride.SLOT_BASE_COLOR).Should().Be(Normal);
            ovr.GetScale(GltfMaterialOverride.SLOT_BASE_COLOR).Should().Be(new Vector2(2, 2));
            ovr.GetOffset(GltfMaterialOverride.SLOT_BASE_COLOR).Should().Be(new Vector2(0, 0.5f));
            ovr.GetRotation(GltfMaterialOverride.SLOT_BASE_COLOR).Should().Be(0.25f);
            ovr.GetTexture(GltfMaterialOverride.SLOT_EMISSIVE).Should().Be(Emissive);
            ovr.AlphaMode.Should().Be(1);
            map.Keys.Should().BeEquivalentTo(new[] { "tex", "ti", "bc", "am" });
            map["tex"].Should().BeOfType<OSDArray>();
        }

        [Fact]
        public void UnreadableDataReadsAsNoOverride()
        {
            TestHelpers.InMethod();
            GltfMaterialOverride.FromData("{not llsd").IsEmpty.Should().BeTrue();
            GltfMaterialOverride.FromData(null).IsEmpty.Should().BeTrue();
        }

        [Fact]
        public void EditingAFaceWithAMaterialStoresTheOverride()
        {
            TestHelpers.InMethod();
            SceneObjectPart part = NewPart();
            GltfMaterialOverride.SetRenderMaterial(part, 2, Material).Should().BeTrue();

            GltfMaterialOverride.Edit(part, 2, o => { o.MetallicFactor = 0.5f; o.AlphaMode = 1; }).Should().BeTrue();

            OSDMap map = Stored(part, 2);
            map["mf"].AsReal().Should().Be(0.5);
            map["am"].AsInteger().Should().Be(1);
            GltfMaterialOverride.Get(part.Shape, 2).MetallicFactor.Should().Be(0.5f);
        }

        [Fact]
        public void EditingTheSameValueAgainChangesNothing()
        {
            TestHelpers.InMethod();
            SceneObjectPart part = NewPart();
            GltfMaterialOverride.SetRenderMaterial(part, 0, Material);
            GltfMaterialOverride.Edit(part, 0, o => o.RoughnessFactor = 0.1f);

            GltfMaterialOverride.Edit(part, 0, o => o.RoughnessFactor = 0.1f).Should().BeFalse();
        }

        [Fact]
        public void EditsMergeWithWhatIsAlreadyThere()
        {
            TestHelpers.InMethod();
            SceneObjectPart part = NewPart();
            GltfMaterialOverride.SetRenderMaterial(part, 0, Material);
            GltfMaterialOverride.Edit(part, 0, o => o.BaseColor = new Vector3(1, 0, 0));

            GltfMaterialOverride.Edit(part, 0, o => o.SetTexture(GltfMaterialOverride.SLOT_NORMAL, Normal));

            GltfMaterialOverride ovr = GltfMaterialOverride.Get(part.Shape, 0);
            ovr.BaseColor.Should().Be(new Vector3(1, 0, 0));
            ovr.GetTexture(GltfMaterialOverride.SLOT_NORMAL).Should().Be(Normal);
        }

        [Fact]
        public void ClearingTheLastOverrideRemovesTheEntryButKeepsRenderMaterials()
        {
            TestHelpers.InMethod();
            SceneObjectPart part = NewPart();
            GltfMaterialOverride.SetRenderMaterial(part, 0, Material);
            GltfMaterialOverride.Edit(part, 0, o => o.MetallicFactor = 0.5f);

            GltfMaterialOverride.Edit(part, 0, o => o.MetallicFactor = null).Should().BeTrue();

            part.Shape.RenderMaterials.Should().NotBeNull();
            part.Shape.RenderMaterials.overrides.Should().BeNull();
        }

        [Fact]
        public void OnAFaceWithNoMaterialOnlyTransformsAreKept()
        {
            TestHelpers.InMethod();
            // SL GLTF Overrides: setting an override while PRIM_RENDER_MATERIAL is NULL_KEY clears most overrides,
            // texture transforms excepted
            SceneObjectPart part = NewPart();

            GltfMaterialOverride.Edit(part, 1, o =>
            {
                o.MetallicFactor = 0.5f;
                o.SetTexture(GltfMaterialOverride.SLOT_BASE_COLOR, Normal);
                o.SetScale(GltfMaterialOverride.SLOT_BASE_COLOR, new Vector2(3, 3));
            }).Should().BeTrue();

            OSDMap map = Stored(part, 1);
            map.Keys.Should().BeEquivalentTo(new[] { "ti" });
        }

        [Fact]
        public void AllSidesEditsEveryFace()
        {
            TestHelpers.InMethod();
            SceneObjectPart part = NewPart();
            int sides = part.GetNumberOfSides();
            GltfMaterialOverride.SetRenderMaterial(part, -1, Material);

            GltfMaterialOverride.Edit(part, -1, o => o.DoubleSided = true);

            part.Shape.RenderMaterials.overrides.Should().HaveCount(sides);
            for (int f = 0; f < sides; f++)
                GltfMaterialOverride.Get(part.Shape, f).DoubleSided.Should().BeTrue();
        }

        [Fact]
        public void AFaceThatDoesNotExistChangesNothing()
        {
            TestHelpers.InMethod();
            SceneObjectPart part = NewPart();
            int sides = part.GetNumberOfSides();

            GltfMaterialOverride.SetRenderMaterial(part, sides, Material).Should().BeFalse();
            GltfMaterialOverride.Edit(part, sides, o => o.DoubleSided = true).Should().BeFalse();
            part.Shape.RenderMaterials.Should().BeNull();
        }

        [Fact]
        public void ANewMaterialClearsOverridesButKeepsTransforms()
        {
            TestHelpers.InMethod();
            // SL llSetRenderMaterial "will clear most PRIM_GLTF_* properties on the face", except "repeats, offsets,
            // and rotation_in_radians"
            SceneObjectPart part = NewPart();
            GltfMaterialOverride.SetRenderMaterial(part, 0, Material);
            GltfMaterialOverride.Edit(part, 0, o =>
            {
                o.BaseColor = new Vector3(0, 0, 1);
                o.SetTexture(GltfMaterialOverride.SLOT_NORMAL, Normal);
                o.SetOffset(GltfMaterialOverride.SLOT_NORMAL, new Vector2(0.5f, 0.5f));
            });

            GltfMaterialOverride.SetRenderMaterial(part, 0, OtherMaterial).Should().BeTrue();

            GltfMaterialOverride.GetRenderMaterial(part.Shape, 0).Should().Be(OtherMaterial);
            GltfMaterialOverride ovr = GltfMaterialOverride.Get(part.Shape, 0);
            ovr.BaseColor.Should().BeNull();
            ovr.GetTexture(GltfMaterialOverride.SLOT_NORMAL).Should().BeNull();
            ovr.GetOffset(GltfMaterialOverride.SLOT_NORMAL).Should().Be(new Vector2(0.5f, 0.5f));
        }

        [Fact]
        public void NoMaterialRemovesTheFacesEntry()
        {
            TestHelpers.InMethod();
            SceneObjectPart part = NewPart();
            GltfMaterialOverride.SetRenderMaterial(part, 0, Material);
            GltfMaterialOverride.SetRenderMaterial(part, 3, Material);

            GltfMaterialOverride.SetRenderMaterial(part, 0, UUID.Zero).Should().BeTrue();

            GltfMaterialOverride.GetRenderMaterial(part.Shape, 0).Should().Be(UUID.Zero);
            GltfMaterialOverride.GetRenderMaterial(part.Shape, 3).Should().Be(Material);
            part.Shape.RenderMaterials.entries.Should().HaveCount(1);
        }

        [Fact]
        public void SettingTheSameMaterialAgainChangesNothing()
        {
            TestHelpers.InMethod();
            SceneObjectPart part = NewPart();
            GltfMaterialOverride.SetRenderMaterial(part, -1, Material);

            GltfMaterialOverride.SetRenderMaterial(part, -1, Material).Should().BeFalse();
        }
    }
}
