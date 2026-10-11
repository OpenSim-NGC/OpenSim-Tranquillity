/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// The friction and restitution a material gives a prim, against the table on the
    /// Second Life wiki (LlSetPrimitiveParams, PRIM_MATERIAL).
    /// </summary>
    public class SOPMaterialTests
    {
        // material, friction, restitution
        public static TheoryData<Material, float, float> WikiTable => new()
        {
            { Material.Stone, 0.8f, 0.4f },
            { Material.Metal, 0.3f, 0.4f },
            { Material.Glass, 0.2f, 0.7f },
            { Material.Wood, 0.6f, 0.5f },
            { Material.Flesh, 0.9f, 0.3f },
            { Material.Plastic, 0.4f, 0.7f },
            { Material.Rubber, 0.9f, 0.9f },
            { Material.Light, 0.6f, 0.5f },
        };

        [Theory]
        [MemberData(nameof(WikiTable))]
        public void TableMatchesTheWiki(Material material, float friction, float restitution)
        {
            SOPMaterialData.friction(material).Should().Be(friction);
            SOPMaterialData.bounce(material).Should().Be(restitution);
        }

        [Theory]
        [MemberData(nameof(WikiTable))]
        public void SettingAPartsMaterialAppliesTheTableValues(Material material, float friction, float restitution)
        {
            var part = new SceneObjectPart(UUID.Zero, PrimitiveBaseShape.Default, Vector3.Zero, Quaternion.Identity, Vector3.Zero);

            // Start from a different material so the setter has something to change.
            part.Material = (byte)(material == Material.Stone ? Material.Metal : Material.Stone);
            part.Material = (byte)material;

            part.Friction.Should().Be(friction);
            part.Restitution.Should().Be(restitution);
        }
    }
}
