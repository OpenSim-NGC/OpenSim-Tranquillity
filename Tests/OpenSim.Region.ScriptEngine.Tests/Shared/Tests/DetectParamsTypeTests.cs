/*
 * Copyright (c) Legion Builds
 * All rights reserved.
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

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// The type DetectParams gives an avatar, as llDetectedType returns it. The SL wiki's llDetectedType page lists
    /// example results: an agent standing is 3 (ACTIVE | AGENT) in physical movement and 1 (AGENT) when not; an agent
    /// sitting is 3 (ACTIVE | AGENT) in physical movement and 5 (PASSIVE | AGENT) on a non-physical object.
    /// </summary>
    /// <remarks>Each test builds its own scene: nothing process-wide.</remarks>
    public class DetectParamsTypeTests : OpenSimTestCase
    {
        private const int AGENT = 0x1, ACTIVE = 0x2, PASSIVE = 0x4;

        private readonly Scene m_scene = new SceneHelpers().SetupScene();

        private ScenePresence SeatedOn(SceneObjectGroup seat)
        {
            ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
            sp.AbsolutePosition = seat.AbsolutePosition + new Vector3(1, 0, 0);
            sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, seat.RootPart.UUID, Vector3.Zero);
            Assert.Equal(seat.RootPart.LocalId, sp.ParentID);
            return sp;
        }

        private int TypeByKey(ScenePresence sp)
        {
            DetectParams d = new() { Key = sp.UUID };
            d.Populate(m_scene);
            return d.Type;
        }

        [Fact]
        public void AnAgentSittingOnAStillNonPhysicalObjectIsPassiveAndAgent()
        {
            TestHelpers.InMethod();
            ScenePresence sp = SeatedOn(SceneHelpers.AddSceneObject(m_scene));

            Assert.Equal(PASSIVE | AGENT, TypeByKey(sp));
        }

        [Fact]
        public void AnAgentSittingOnAMovingObjectIsActiveAndAgent()
        {
            TestHelpers.InMethod();
            SceneObjectGroup seat = SceneHelpers.AddSceneObject(m_scene);
            ScenePresence sp = SeatedOn(seat);
            seat.RootPart.Velocity = new Vector3(2, 0, 0);

            Assert.Equal(ACTIVE | AGENT, TypeByKey(sp));
        }

        [Fact]
        public void AMovingAgentInACollisionIsActiveAndAgent()
        {
            TestHelpers.InMethod();
            ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));

            DetectParams d = new();
            d.Populate(m_scene, new DetectedObject
            {
                keyUUID = sp.UUID, nameStr = sp.Name, ownerUUID = sp.UUID, posVector = sp.AbsolutePosition,
                rotQuat = Quaternion.Identity, velVector = new Vector3(1, 0, 0), colliderType = AGENT,
            });

            Assert.Equal(ACTIVE | AGENT, d.Type);
        }
    }
}
