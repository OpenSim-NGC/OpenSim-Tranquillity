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

using Nini.Config;
using OpenMetaverse;

using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// EventManager.OnGroupBeginInTransit and OnGroupEndInTransit: a crossing, or an object teleport to another
    /// region, is announced before it starts and its end is announced once, with whether the object left. The
    /// crossing runs on its own thread, so each test waits for the end signal rather than for a fixed time.
    /// </summary>
    public class SceneObjectTransitSignalTests : OpenSimTestCase
    {
        private sealed class Signals
        {
            private readonly List<string> m_seen = new();

            public Signals(Scene scene)
            {
                scene.EventManager.OnGroupBeginInTransit += g => Add("begin " + g.UUID + " inTransit=" + g.inTransit);
                scene.EventManager.OnGroupEndInTransit += (g, crossed) =>
                    Add("end " + g.UUID + " crossed=" + crossed + " inTransit=" + g.inTransit + " deleted=" + g.IsDeleted);
            }

            private void Add(string s) { lock (m_seen) m_seen.Add(s); }

            public string[] Seen { get { lock (m_seen) return m_seen.ToArray(); } }

            public bool WaitForEnd(double seconds = 30)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < until)
                {
                    if (Seen.Any(s => s.StartsWith("end ")))
                        return true;
                    Thread.Sleep(50);
                }
                return Seen.Any(s => s.StartsWith("end "));
            }
        }

        private static (TestScene A, TestScene B) TwoRegions()
        {
            EntityTransferModule etmA = new EntityTransferModule();
            EntityTransferModule etmB = new EntityTransferModule();
            LocalSimulationConnectorModule lscm = new LocalSimulationConnectorModule();

            IConfigSource config = new IniConfigSource();
            IConfig modulesConfig = config.AddConfig("Modules");
            modulesConfig.Set("EntityTransferModule", etmA.Name);
            modulesConfig.Set("SimulationServices", lscm.Name);

            SceneHelpers sh = new SceneHelpers();
            TestScene sceneA = sh.SetupScene("Example Region A", UUID.Random(), 1000, 1000);
            TestScene sceneB = sh.SetupScene("Example Region B", UUID.Random(), 1000, 999);

            SceneHelpers.SetupSceneModules(new Scene[] { sceneA, sceneB }, config, lscm);
            SceneHelpers.SetupSceneModules(sceneA, config, etmA);
            SceneHelpers.SetupSceneModules(sceneB, config, etmB);
            return (sceneA, sceneB);
        }

        private static TestScene OneRegion()
        {
            EntityTransferModule etm = new EntityTransferModule();
            LocalSimulationConnectorModule lscm = new LocalSimulationConnectorModule();

            IConfigSource config = new IniConfigSource();
            IConfig modulesConfig = config.AddConfig("Modules");
            modulesConfig.Set("EntityTransferModule", etm.Name);
            modulesConfig.Set("SimulationServices", lscm.Name);

            TestScene scene = new SceneHelpers().SetupScene("Example Region", UUID.Random(), 1000, 1000);
            SceneHelpers.SetupSceneModules(scene, config, lscm, etm);
            return scene;
        }

        [Fact]
        public void ACrossingIntoTheNextRegionIsAnnouncedThenEndsCrossed()
        {
            TestHelpers.InMethod();

            var (sceneA, sceneB) = TwoRegions();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(sceneA, 1, UUID.Random(), "", 0x10);
            so.AbsolutePosition = new Vector3(128, 10, 20);
            var signals = new Signals(sceneA);
            var signalsB = new Signals(sceneB);

            so.AbsolutePosition = new Vector3(128, -10, 20);

            Assert.True(signals.WaitForEnd(), string.Join(" | ", signals.Seen));
            Assert.Equal(new[] { "begin " + so.UUID + " inTransit=True", "end " + so.UUID + " crossed=True inTransit=True deleted=True" },
                signals.Seen);
            Assert.Null(sceneA.GetSceneObjectGroup(so.UUID));
            Assert.NotNull(sceneB.GetSceneObjectGroup(so.UUID));
            Assert.Empty(signalsB.Seen);   // the region it arrives in announces nothing
        }

        [Fact]
        public void ACrossingWithNoRegionBeyondIsAnnouncedThenEndsNotCrossedWithTheObjectBackInTheRegion()
        {
            TestHelpers.InMethod();

            TestScene scene = OneRegion();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 1, UUID.Random(), "", 0x11);
            so.AbsolutePosition = new Vector3(128, 10, 20);
            var signals = new Signals(scene);

            so.AbsolutePosition = new Vector3(128, -10, 20);

            Assert.True(signals.WaitForEnd(), string.Join(" | ", signals.Seen));
            // The end comes once the object has been put back inside the region with its in-transit flag clear.
            Assert.Equal(new[] { "begin " + so.UUID + " inTransit=True", "end " + so.UUID + " crossed=False inTransit=False deleted=False" },
                signals.Seen);
            Assert.Same(so, scene.GetSceneObjectGroup(so.UUID));
            Assert.True(so.AbsolutePosition.Y >= 0);
        }

        [Fact]
        public void AnObjectThatDiesAtTheEdgeEndsNotCrossedAndDeleted()
        {
            TestHelpers.InMethod();

            var (sceneA, _) = TwoRegions();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(sceneA, 1, UUID.Random(), "", 0x12);
            so.AbsolutePosition = new Vector3(128, 10, 20);
            so.RootPart.DIE_AT_EDGE = true;
            var signals = new Signals(sceneA);

            so.AbsolutePosition = new Vector3(128, -10, 20);

            Assert.True(signals.WaitForEnd(), string.Join(" | ", signals.Seen));
            Assert.Equal(2, signals.Seen.Length);
            Assert.StartsWith("end " + so.UUID + " crossed=False", signals.Seen[1]);
            Assert.EndsWith("deleted=True", signals.Seen[1]);
        }

        [Fact]
        public void AnObjectTeleportToAnotherRegionThatFailsIsAnnouncedThenEndsNotCrossed()
        {
            TestHelpers.InMethod();

            TestScene scene = OneRegion();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 1, UUID.Random(), "", 0x13);
            so.AbsolutePosition = new Vector3(128, 10, 20);
            var signals = new Signals(scene);

            Assert.Equal(0, so.TeleportObject(UUID.Random(), new Vector3(128, -100, 20), Quaternion.Identity, 0));

            Assert.True(signals.WaitForEnd(), string.Join(" | ", signals.Seen));
            Assert.Equal(new[] { "begin " + so.UUID + " inTransit=True", "end " + so.UUID + " crossed=False inTransit=False deleted=False" },
                signals.Seen);
        }

        [Fact]
        public void MovesAndTeleportsInsideTheRegionAnnounceNothing()
        {
            TestHelpers.InMethod();

            TestScene scene = OneRegion();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 1, UUID.Random(), "", 0x14);
            so.AbsolutePosition = new Vector3(128, 10, 20);
            var signals = new Signals(scene);

            so.AbsolutePosition = new Vector3(100, 50, 20);
            Assert.Equal(1, so.TeleportObject(UUID.Random(), new Vector3(60, 60, 30), Quaternion.Identity, 0));

            Assert.Empty(signals.Seen);
            Assert.False(so.inTransit);
        }
    }
}
