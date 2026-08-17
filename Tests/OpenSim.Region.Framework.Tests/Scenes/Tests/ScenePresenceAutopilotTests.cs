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
    public class ScenePresenceAutopilotTests : OpenSimTestCase
    {
        private TestScene m_scene;

        public override void Dispose()
        {
            // We must set this back afterwards, otherwise later tests will fail since they're expecting multiple
            // threads.  Possibly, later tests should be rewritten not to worry about such things.
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        }

        public ScenePresenceAutopilotTests()
        {
            // Don't allow tests to be bamboozled by asynchronous events.  Execute everything on the same thread.
            Util.FireAndForgetMethod = FireAndForgetMethod.None;
            m_scene = new SceneHelpers().SetupScene();
        }

        [Fact]
        public void TestMove()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));

            Vector3 startPos = sp.AbsolutePosition;
//            Vector3 startPos = new Vector3(128, 128, 30);

            // For now, we'll make the scene presence fly to simplify this test, but this needs to change.
            sp.Flying = true;

            m_scene.Update(1);
            Assert.Equal(startPos, sp.AbsolutePosition);

            Vector3 targetPos = startPos + new Vector3(0, 10, 0);
            sp.MoveToTarget(targetPos, false, false, false);

            Assert.Equal(startPos, sp.AbsolutePosition);
            Assert.True(sp.Rotation.ApproxEquals(new Quaternion(0, 0, 0.7071068f, 0.7071068f), 0.000001f));

            m_scene.Update(1);

            // We should really check the exact figure.
            Assert.Equal(startPos.X, sp.AbsolutePosition.X);
            Assert.True(sp.AbsolutePosition.Y > startPos.Y);
            Assert.Equal(startPos.Z, sp.AbsolutePosition.Z);
            Assert.True(sp.AbsolutePosition.Z < targetPos.X);

            m_scene.Update(50);

            double distanceToTarget = Util.GetDistanceTo(sp.AbsolutePosition, targetPos);
            Assert.True(distanceToTarget < 1);
            Assert.Equal(targetPos, sp.AbsolutePosition);
            Assert.Equal((uint)AgentManager.ControlFlags.NONE, sp.AgentControlFlags);

            // Try a second movement
            startPos = sp.AbsolutePosition;
            targetPos = startPos + new Vector3(10, 0, 0);
            sp.MoveToTarget(targetPos, false, false, false);

            Assert.Equal(startPos, sp.AbsolutePosition);
            Assert.True(sp.Rotation.ApproxEquals(new Quaternion(0, 0, 0, 1), 0.000001f));

            m_scene.Update(1);

            // We should really check the exact figure.
            Assert.True(sp.AbsolutePosition.X > startPos.X);
            Assert.True(sp.AbsolutePosition.X < targetPos.X);
            Assert.Equal(startPos.Y, sp.AbsolutePosition.Y);
            Assert.Equal(startPos.Z, sp.AbsolutePosition.Z);

            m_scene.Update(50);

            distanceToTarget = Util.GetDistanceTo(sp.AbsolutePosition, targetPos);
            Assert.True(distanceToTarget < 1);
            Assert.Equal(targetPos, sp.AbsolutePosition);
        }
    }
}