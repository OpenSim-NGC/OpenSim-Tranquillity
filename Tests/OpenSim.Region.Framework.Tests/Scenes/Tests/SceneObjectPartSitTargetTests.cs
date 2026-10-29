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

using OpenMetaverse;

using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// SceneObjectPart.SitTargetActive. SL PRIM_SIT_TARGET: "If the active value is 0 the sit target is deactivated.
    /// If it is nonzero the prim's sit target is set to the indicated offset and rotation." and "Unlike
    /// llLinkSitTarget(), an offset of &lt;0.0, 0.0, 0.0&gt; may be explicitly set". llSitTarget: "If offset ==
    /// &lt;0.0, 0.0, 0.0&gt; then the sit target is removed."
    /// </summary>
    public class SceneObjectPartSitTargetTests : OpenSimTestCase
    {
        private static SceneObjectGroup Bench()
            => SceneHelpers.CreateSceneObject(2, TestHelpers.ParseTail(0x1), "bench", 0x10);

        [Fact]
        public void AnActiveSitTargetAtAZeroOffsetIsSet()
        {
            SceneObjectPart part = Bench().RootPart;
            Assert.False(part.IsSitTargetSet);

            part.SetSitTarget(true, Vector3.Zero, Quaternion.Identity);

            Assert.True(part.IsSitTargetSet);
            Assert.True(part.SitTargetActive);
            Assert.Equal(Vector3.Zero, part.SitTargetPosition);
        }

        [Fact]
        public void AnInactiveSitTargetIsNotSetWhateverItsOffset()
        {
            SceneObjectPart part = Bench().RootPart;

            part.SetSitTarget(false, new Vector3(0, 0, 1), Quaternion.Identity);

            Assert.False(part.IsSitTargetSet);
        }

        [Fact]
        public void AssigningTheOffsetOrRotationOnItsOwnBehavesAsBefore()
        {
            // llSitTarget, llLinkSitTarget, the region stores and other engines assign these properties directly.
            SceneObjectPart part = Bench().RootPart;
            part.SetSitTarget(true, Vector3.Zero, Quaternion.Identity);

            part.SitTargetPosition = Vector3.Zero;
            Assert.False(part.IsSitTargetSet);

            part.SitTargetPosition = new Vector3(0, 0, 1);
            Assert.True(part.IsSitTargetSet);

            part.SetSitTarget(false, new Vector3(0, 0, 1), Quaternion.Identity);
            part.SitTargetOrientation = Quaternion.CreateFromEulers(0, 0, 1);
            Assert.True(part.IsSitTargetSet);

            part.SitTargetActive = false;
            Assert.False(part.IsSitTargetSet);
        }

        [Fact]
        public void AnActiveSitTargetAtAZeroOffsetSurvivesAnXmlRoundTrip()
        {
            SceneObjectGroup so = Bench();
            so.GetLinkNumPart(2).SetSitTarget(true, Vector3.Zero, Quaternion.Identity);

            SceneObjectGroup copy = SceneObjectSerializer.FromXml2Format(SceneObjectSerializer.ToXml2Format(so));

            Assert.True(copy.GetLinkNumPart(2).IsSitTargetSet);
            Assert.Equal(Vector3.Zero, copy.GetLinkNumPart(2).SitTargetPosition);
            Assert.False(copy.RootPart.IsSitTargetSet);
        }

        [Fact]
        public void OtherSitTargetsSerializeWithoutTheNewElement()
        {
            SceneObjectGroup so = Bench();
            so.RootPart.SitTargetPosition = new Vector3(0, 0, 1);
            so.GetLinkNumPart(2).SetSitTarget(true, new Vector3(0, 0, 2), Quaternion.Identity);

            string xml = SceneObjectSerializer.ToXml2Format(so);

            Assert.DoesNotContain("SitTargetActive", xml);
            SceneObjectGroup copy = SceneObjectSerializer.FromXml2Format(xml);
            Assert.True(copy.RootPart.IsSitTargetSet);
            Assert.True(copy.GetLinkNumPart(2).IsSitTargetSet);
        }

        [Fact]
        public void AnActiveSitTargetAtAZeroOffsetIsUsedByAManualSit()
        {
            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x2));
            sp.AbsolutePosition = new Vector3(1, 1, 1);
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 2, sp.UUID, "bench", 0x10);
            SceneObjectPart child = so.GetLinkNumPart(2);
            child.SetSitTarget(true, Vector3.Zero, Quaternion.Identity);

            sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, so.RootPart.UUID, Vector3.Zero);

            Assert.Equal(child.LocalId, sp.ParentID);
            Assert.Equal(sp.UUID, child.SitTargetAvatar);
        }
    }
}
