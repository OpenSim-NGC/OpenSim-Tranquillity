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

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// ScenePresence.HasScriptControls and EventManager.OnScriptControlsReleased: every way a script's taken-controls
    /// registration goes away raises the event once, with the agent and exactly the removed item ids.
    /// </summary>
    public class ScenePresenceScriptControlsTests : OpenSimTestCase
    {
        private const int ControlFwd = 1; // CONTROL_FWD

        private readonly TestScene m_scene;
        private readonly ScenePresence m_sp;
        private readonly SceneObjectPart m_part;
        private readonly List<(UUID agent, UUID[] items)> m_released = new();

        public ScenePresenceScriptControlsTests()
        {
            m_scene = new SceneHelpers().SetupScene();
            m_sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
            m_part = SceneHelpers.AddSceneObject(m_scene).RootPart;
            m_scene.EventManager.OnScriptControlsReleased += (agent, items) => m_released.Add((agent, items));
        }

        private void Take(ScenePresence sp, UUID item)
            => sp.RegisterControlEventsToScript(ControlFwd, 1, 0, m_part.LocalId, item);

        private void AssertReleasedOnce(UUID agent, params UUID[] items)
        {
            Assert.Single(m_released);
            Assert.Equal(agent, m_released[0].agent);
            m_released[0].items.Should().BeEquivalentTo(items);
        }

        [Fact]
        public void HasScriptControlsIsTrueOnlyAfterRegister()
        {
            TestHelpers.InMethod();
            UUID item = TestHelpers.ParseTail(0x100);

            Assert.False(m_sp.HasScriptControls(item));
            Take(m_sp, item);
            Assert.True(m_sp.HasScriptControls(item));
            Assert.False(m_sp.HasScriptControls(TestHelpers.ParseTail(0x101)));
            Assert.Empty(m_released);
        }

        [Fact]
        public void UnRegisterReleasesThatItem()
        {
            TestHelpers.InMethod();
            UUID a = TestHelpers.ParseTail(0x100), b = TestHelpers.ParseTail(0x101);
            Take(m_sp, a);
            Take(m_sp, b);

            m_sp.UnRegisterControlEventsToScript(m_part.LocalId, a);

            Assert.False(m_sp.HasScriptControls(a));
            Assert.True(m_sp.HasScriptControls(b));
            AssertReleasedOnce(m_sp.UUID, a);
        }

        [Fact]
        public void TakeWithPassOnAndNoAcceptReleasesThatItem()
        {
            TestHelpers.InMethod();
            UUID a = TestHelpers.ParseTail(0x100);
            Take(m_sp, a);

            m_sp.RegisterControlEventsToScript(ControlFwd, 0, 1, m_part.LocalId, a);

            Assert.False(m_sp.HasScriptControls(a));
            AssertReleasedOnce(m_sp.UUID, a);
        }

        [Fact]
        public void ForceReleaseReleasesEveryItemInOneEvent()
        {
            TestHelpers.InMethod();
            UUID a = TestHelpers.ParseTail(0x100), b = TestHelpers.ParseTail(0x101);
            Take(m_sp, a);
            Take(m_sp, b);

            m_sp.HandleForceReleaseControls(m_sp.ControllingClient, m_sp.UUID);

            Assert.False(m_sp.HasScriptControls(a));
            Assert.False(m_sp.HasScriptControls(b));
            AssertReleasedOnce(m_sp.UUID, a, b);
        }

        [Fact]
        public void ClearControlsReleasesEveryItemInOneEvent()
        {
            TestHelpers.InMethod();
            UUID a = TestHelpers.ParseTail(0x100), b = TestHelpers.ParseTail(0x101);
            Take(m_sp, a);
            Take(m_sp, b);

            m_sp.ClearControls();

            Assert.False(m_sp.HasScriptControls(a));
            Assert.False(m_sp.HasScriptControls(b));
            AssertReleasedOnce(m_sp.UUID, a, b);
        }

        [Fact]
        public void StandingUpReleasesTheSeatsScripts()
        {
            TestHelpers.InMethod();
            UUID a = TestHelpers.ParseTail(0x100);
            m_sp.AbsolutePosition = new Vector3(1, 0, 0);
            m_sp.HandleAgentRequestSit(m_sp.ControllingClient, m_sp.UUID, m_part.UUID, Vector3.Zero);
            Assert.Equal(m_part.LocalId, m_sp.ParentID);
            Take(m_sp, a);

            m_sp.StandUp();

            Assert.False(m_sp.HasScriptControls(a));
            AssertReleasedOnce(m_sp.UUID, a);
        }

        [Fact]
        public void LeavingTheRegionReleasesEveryItem()
        {
            TestHelpers.InMethod();
            UUID a = TestHelpers.ParseTail(0x100);
            Take(m_sp, a);

            m_scene.CloseAgent(m_sp.UUID, false);

            Assert.Null(m_scene.GetScenePresence(m_sp.UUID));
            Assert.False(m_sp.HasScriptControls(a));
            AssertReleasedOnce(m_sp.UUID, a);
        }

        [Fact]
        public void AgentUpdateReleasesOnlyTheItemsItNoLongerCarries()
        {
            TestHelpers.InMethod();
            ScenePresence child = SceneHelpers.AddChildScenePresence(m_scene, TestHelpers.ParseTail(0x2));
            UUID a = TestHelpers.ParseTail(0x100), b = TestHelpers.ParseTail(0x101);
            Take(child, a);
            Take(child, b);

            AgentData update = new()
            {
                AgentID = child.UUID,
                Appearance = new AvatarAppearance(),
                Controllers = new[] { new ControllerData(m_part.ParentGroup.UUID, b, 0, (uint)ControlFwd) }
            };
            child.UpdateChildAgent(update);

            Assert.False(child.HasScriptControls(a));
            Assert.True(child.HasScriptControls(b));
            AssertReleasedOnce(child.UUID, a);
        }

        [Fact]
        public void NothingRegisteredRaisesNothing()
        {
            TestHelpers.InMethod();
            UUID a = TestHelpers.ParseTail(0x100);

            m_sp.UnRegisterControlEventsToScript(m_part.LocalId, a);
            m_sp.RegisterControlEventsToScript(ControlFwd, 0, 1, m_part.LocalId, a);
            m_sp.HandleForceReleaseControls(m_sp.ControllingClient, m_sp.UUID);
            m_sp.ClearControls();
            m_scene.CloseAgent(m_sp.UUID, false);

            Assert.Empty(m_released);
        }
    }
}
