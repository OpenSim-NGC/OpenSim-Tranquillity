/*
 * Copyright (c) 2026 Legion Builds
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

using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// An object that reaches the edge of a region with no neighbouring region: with neither STATUS_DIE_AT_EDGE nor
    /// STATUS_RETURN_AT_EDGE it is put back just inside the edge and stopped; with STATUS_DIE_AT_EDGE it is deleted
    /// and not returned; with STATUS_RETURN_AT_EDGE it is returned to its owner. Anyone seated on it is stood up
    /// inside the region first. The [EntityTransfer] ScriptRezzedObjectsDieAtEdge setting makes an object rezzed by a
    /// script die at a region edge with no neighbour (it crosses into a neighbour as usual). The crossing runs on its own thread, so each test waits for the end signal rather
    /// than for a fixed time. The tests use no physics engine: a stand-in actor reports the position an engine would.
    /// </summary>
    public class SceneObjectEdgeFlagsTests : OpenSimTestCase
    {
        /// <summary>
        /// A stand-in prim actor, the basic physics scene's own, that reports a position and counts the crossing calls
        /// core makes on it.
        /// </summary>
        private sealed class Body : BasicPhysicsPrim
        {
            private int m_starts, m_failures;

            public Body(Vector3 position)
                : base("Example Body", 1, position, Vector3.One, Quaternion.Identity, PrimitiveBaseShape.Default) { }

            public int Starts => Volatile.Read(ref m_starts);
            public int Failures => Volatile.Read(ref m_failures);

            public override void CrossingStart() { Interlocked.Increment(ref m_starts); }
            public override void CrossingFailure() { Interlocked.Increment(ref m_failures); }
        }

        private sealed class Fixture
        {
            private readonly object m_lock = new();
            private bool m_ended;

            public TestScene Scene;
            public UserAccount Owner;
            public AsyncSceneObjectGroupDeleter Deleter;
            public TestScene Neighbour;   // the region south of Scene, when asked for

            public Fixture(bool? scriptRezzedDieAtEdge = null, bool withNeighbour = false)
            {
                EntityTransferModule etm = new EntityTransferModule();
                LocalSimulationConnectorModule lscm = new LocalSimulationConnectorModule();

                IConfigSource config = new IniConfigSource();
                IConfig modules = config.AddConfig("Modules");
                modules.Set("EntityTransferModule", etm.Name);
                modules.Set("SimulationServices", lscm.Name);
                modules.Set("InventoryAccessModule", "BasicInventoryAccessModule");
                if (scriptRezzedDieAtEdge.HasValue)
                    config.AddConfig("EntityTransfer").Set("ScriptRezzedObjectsDieAtEdge", scriptRezzedDieAtEdge.Value);

                SceneHelpers sh = new SceneHelpers();
                Scene = sh.SetupScene("Example Region", UUID.Random(), 1000, 1000, config);
                if (withNeighbour)
                {
                    Neighbour = sh.SetupScene("Example Neighbour", UUID.Random(), 1000, 999);
                    SceneHelpers.SetupSceneModules(new Scene[] { Scene, Neighbour }, config, lscm);
                    SceneHelpers.SetupSceneModules(Neighbour, config, new EntityTransferModule());
                    SceneHelpers.SetupSceneModules(Scene, config, etm, new BasicInventoryAccessModule());
                }
                else
                    SceneHelpers.SetupSceneModules(Scene, config, lscm, etm, new BasicInventoryAccessModule());
                Owner = UserAccountHelpers.CreateUserWithInventory(Scene);

                // The returned objects are taken into inventory by this deleter's timer; the tests drain it by hand.
                Deleter = Scene.SceneObjectGroupDeleter;
                Deleter.Enabled = false;

                Scene.EventManager.OnGroupEndInTransit += (g, crossed) => { lock (m_lock) m_ended = true; };
            }

            public bool WaitForEnd(double seconds = 30)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < until)
                {
                    lock (m_lock)
                        if (m_ended)
                            return true;
                    Thread.Sleep(50);
                }
                lock (m_lock)
                    return m_ended;
            }

            /// <summary>One prim owned by <see cref="Owner"/>, just inside the south-to-north edge of the region.</summary>
            public SceneObjectGroup NewObject(int idTail, string name = "Example Rover")
            {
                SceneObjectGroup so = SceneHelpers.AddSceneObject(Scene, 1, Owner.PrincipalID, name, idTail);
                so.AbsolutePosition = new Vector3(128, 255.9f, 20);
                return so;
            }

            public Body MakePhysical(SceneObjectGroup so)
            {
                var body = new Body(so.AbsolutePosition);
                so.RootPart.PhysActor = body;
                return body;
            }

            /// <summary>The engine reports the object past the edge: the same call a physics step ends in.</summary>
            public void PhysicsMovesPastTheEdge(SceneObjectGroup so, Body body)
            {
                body.Position = new Vector3(128, 260, 20);
                so.RootPart.PhysicsRequestingTerseUpdate();
            }

            /// <summary>A keyframe step moves the group's position past the edge.</summary>
            public void KeyframeMovesPastTheEdge(SceneObjectGroup so)
            {
                so.AbsolutePosition = new Vector3(128, 260, 20);
            }

            public static void MakeKeyframed(SceneObjectGroup so)
            {
                so.RootPart.KeyframeMotion = new KeyframeMotion(so, KeyframeMotion.PlayMode.Forward, KeyframeMotion.DataFormat.Translation);
            }

            public ScenePresence SeatAvatarOn(SceneObjectGroup so)
            {
                // A sit target 3 m beyond the edge side of the object, so a stand-up beside the seat is outside the region.
                so.RootPart.SetSitTarget(true, new Vector3(0, 3, 0), Quaternion.Identity);
                ScenePresence sp = SceneHelpers.AddScenePresence(Scene, UUID.Random());
                sp.AbsolutePosition = new Vector3(128, 255.5f, 20);
                sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, so.RootPart.UUID, Vector3.Zero);
                Assert.NotEqual(0u, sp.ParentID);
                Assert.True(sp.IsSatOnObject);
                return sp;
            }

            /// <summary>What is in the owner's Lost And Found folder (where a returned object goes) after the deleter ran.</summary>
            public List<InventoryItemBase> ReturnedItems()
            {
                Deleter.InventoryDeQueueAndDelete();
                InventoryFolderBase lostAndFound = Scene.InventoryService.GetFolderForType(Owner.PrincipalID, FolderType.LostAndFound);
                Assert.NotNull(lostAndFound);
                return Scene.InventoryService.GetFolderItems(Owner.PrincipalID, lostAndFound.ID);
            }
        }

        private static void AssertInsideTheRegion(Scene scene, Vector3 pos)
        {
            Assert.True(scene.PositionIsInCurrentRegion(pos), "position " + pos + " is outside the region");
            Assert.InRange(pos.X, 0.5f, scene.RegionInfo.RegionSizeX - 0.5f);
            Assert.InRange(pos.Y, 0.5f, scene.RegionInfo.RegionSizeY - 0.5f);
        }

        // ---------------------------------------------------------------------------------------------
        // A physical object
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void APhysicalObjectWithNoFlagStopsJustInsideTheEdgeAndIsNotDeletedOrReturned()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x10);
            Body body = f.MakePhysical(so);

            f.PhysicsMovesPastTheEdge(so, body);

            Assert.True(f.WaitForEnd());
            Assert.Equal(1, body.Starts);
            Assert.Equal(1, body.Failures);
            Assert.Same(so, f.Scene.GetSceneObjectGroup(so.UUID));
            Assert.False(so.IsDeleted);
            Assert.False(so.inTransit);
            Assert.Equal(255.5f, so.AbsolutePosition.Y);
            AssertInsideTheRegion(f.Scene, so.AbsolutePosition);
            Assert.Empty(f.ReturnedItems());
        }

        [Fact]
        public void APhysicalObjectWithDieAtEdgeIsDeletedAndNotReturned()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x11);
            Body body = f.MakePhysical(so);
            so.RootPart.SetDieAtEdge(true);

            f.PhysicsMovesPastTheEdge(so, body);

            Assert.True(f.WaitForEnd());
            Assert.True(so.IsDeleted);
            Assert.Null(f.Scene.GetSceneObjectGroup(so.UUID));
            Assert.Equal(0, body.Failures);
            Assert.Empty(f.ReturnedItems());
        }

        [Fact]
        public void APhysicalObjectWithReturnAtEdgeIsReturnedToItsOwner()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x12);
            Body body = f.MakePhysical(so);
            so.RootPart.SetReturnAtEdge(true);

            f.PhysicsMovesPastTheEdge(so, body);

            Assert.True(f.WaitForEnd());
            List<InventoryItemBase> returned = f.ReturnedItems();
            Assert.True(so.IsDeleted);
            Assert.Null(f.Scene.GetSceneObjectGroup(so.UUID));
            Assert.Equal(0, body.Failures);
            Assert.Single(returned);
            Assert.StartsWith("Example Rover", returned[0].Name);
        }

        [Fact]
        public void AnAvatarSeatedOnAPhysicalObjectWithDieAtEdgeIsStoodUpInsideTheRegion()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x13);
            Body body = f.MakePhysical(so);
            so.RootPart.SetDieAtEdge(true);
            ScenePresence sp = f.SeatAvatarOn(so);

            f.PhysicsMovesPastTheEdge(so, body);

            Assert.True(f.WaitForEnd());
            Assert.True(so.IsDeleted);
            Assert.Equal(0u, sp.ParentID);
            Assert.False(sp.IsSatOnObject);
            Assert.False(sp.IsChildAgent);
            AssertInsideTheRegion(f.Scene, sp.AbsolutePosition);
        }

        [Fact]
        public void AnAvatarSeatedOnAPhysicalObjectWithReturnAtEdgeIsStoodUpInsideTheRegionBeforeTheReturn()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x14);
            Body body = f.MakePhysical(so);
            so.RootPart.SetReturnAtEdge(true);
            ScenePresence sp = f.SeatAvatarOn(so);

            f.PhysicsMovesPastTheEdge(so, body);

            Assert.True(f.WaitForEnd());
            // Before the deleter takes the object into inventory: the avatar is already on its feet inside the region.
            Assert.Equal(0u, sp.ParentID);
            Assert.False(sp.IsSatOnObject);
            AssertInsideTheRegion(f.Scene, sp.AbsolutePosition);
            Assert.Single(f.ReturnedItems());
        }

        [Fact]
        public void AnAvatarSeatedOnAnUnflaggedPhysicalObjectStaysSeatedWhenItIsStoppedAtTheEdge()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x15);
            Body body = f.MakePhysical(so);
            ScenePresence sp = f.SeatAvatarOn(so);

            f.PhysicsMovesPastTheEdge(so, body);

            Assert.True(f.WaitForEnd());
            Assert.False(so.IsDeleted);
            Assert.True(sp.IsSatOnObject);
            Assert.Equal(1, so.GetSittingAvatarsCount());
        }

        // ---------------------------------------------------------------------------------------------
        // A keyframed object
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void AKeyframedObjectWithNoFlagStopsJustInsideTheEdgeAndIsNotDeletedOrReturned()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x20);
            Fixture.MakeKeyframed(so);

            f.KeyframeMovesPastTheEdge(so);

            Assert.True(f.WaitForEnd());
            Assert.Same(so, f.Scene.GetSceneObjectGroup(so.UUID));
            Assert.False(so.IsDeleted);
            Assert.False(so.inTransit);
            Assert.NotNull(so.RootPart.KeyframeMotion);
            Assert.Equal(255.5f, so.AbsolutePosition.Y);
            AssertInsideTheRegion(f.Scene, so.AbsolutePosition);
            Assert.Empty(f.ReturnedItems());
        }

        [Fact]
        public void AKeyframedObjectWithDieAtEdgeIsDeletedAndNotReturned()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x21);
            Fixture.MakeKeyframed(so);
            so.RootPart.SetDieAtEdge(true);

            f.KeyframeMovesPastTheEdge(so);

            Assert.True(f.WaitForEnd());
            Assert.True(so.IsDeleted);
            Assert.Null(f.Scene.GetSceneObjectGroup(so.UUID));
            Assert.Empty(f.ReturnedItems());
        }

        [Fact]
        public void AKeyframedObjectWithReturnAtEdgeIsReturnedToItsOwner()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x22);
            Fixture.MakeKeyframed(so);
            so.RootPart.SetReturnAtEdge(true);

            f.KeyframeMovesPastTheEdge(so);

            Assert.True(f.WaitForEnd());
            List<InventoryItemBase> returned = f.ReturnedItems();
            Assert.True(so.IsDeleted);
            Assert.Single(returned);
            Assert.StartsWith("Example Rover", returned[0].Name);
        }

        [Fact]
        public void AnAvatarSeatedOnAKeyframedObjectWithDieAtEdgeIsStoodUpInsideTheRegion()
        {
            TestHelpers.InMethod();
            var f = new Fixture();
            SceneObjectGroup so = f.NewObject(0x23);
            Fixture.MakeKeyframed(so);
            so.RootPart.SetDieAtEdge(true);
            ScenePresence sp = f.SeatAvatarOn(so);

            f.KeyframeMovesPastTheEdge(so);

            Assert.True(f.WaitForEnd());
            Assert.True(so.IsDeleted);
            Assert.Equal(0u, sp.ParentID);
            Assert.False(sp.IsSatOnObject);
            AssertInsideTheRegion(f.Scene, sp.AbsolutePosition);
        }

        // ---------------------------------------------------------------------------------------------
        // The ScriptRezzedObjectsDieAtEdge setting
        // ---------------------------------------------------------------------------------------------

        private static (SceneObjectGroup Rezzer, TaskInventoryItem Item) NewRezzer(Fixture f)
        {
            SceneObjectGroup rezzer = SceneHelpers.AddSceneObject(f.Scene, 1, f.Owner.PrincipalID, "Example Rezzer", 0x30);
            TaskInventoryItem item = TaskInventoryHelpers.AddSceneObject(
                f.Scene.AssetService, rezzer.RootPart, "Example Child", UUID.Random(), f.Owner.PrincipalID);
            return (rezzer, item);
        }

        [Fact]
        public void TheSettingIsOffWhenTheConfigDoesNotSayAndOnWhenItDoes()
        {
            TestHelpers.InMethod();

            Assert.False(new Fixture().Scene.ScriptRezzedObjectsDieAtEdge);
            Assert.False(new Fixture(false).Scene.ScriptRezzedObjectsDieAtEdge);
            Assert.True(new Fixture(true).Scene.ScriptRezzedObjectsDieAtEdge);
        }

        [Fact]
        public void AnObjectRezzedByAScriptHasNoDieAtEdgeWhenTheSettingIsOff()
        {
            TestHelpers.InMethod();
            var f = new Fixture(false);
            var (rezzer, item) = NewRezzer(f);

            List<SceneObjectGroup> rezzed = f.Scene.RezObject(
                rezzer.RootPart, item, new Vector3(10, 10, 10), Quaternion.Identity, Vector3.Zero, 0, false);

            Assert.Single(rezzed);
            Assert.False(rezzed[0].RootPart.DIE_AT_EDGE);
            Assert.False(rezzed[0].RootPart.RETURN_AT_EDGE);
            Assert.False(rezzed[0].DieAtVoidEdge);
            Assert.False(rezzed[0].RootPart.GetDieAtEdge());
        }

        [Fact]
        public void AnObjectRezzedByAScriptDiesAtAVoidEdgeButKeepsItsStoredFlagClearWhenTheSettingIsOn()
        {
            TestHelpers.InMethod();
            var f = new Fixture(true);
            var (rezzer, item) = NewRezzer(f);

            List<SceneObjectGroup> rezzed = f.Scene.RezObject(
                rezzer.RootPart, item, new Vector3(10, 10, 10), Quaternion.Identity, Vector3.Zero, 0, false);

            Assert.Single(rezzed);
            Assert.True(rezzed[0].DieAtVoidEdge);
            Assert.False(rezzed[0].RootPart.DIE_AT_EDGE);   // the stored flag, which acts at every crossing, is not set
            Assert.True(rezzed[0].RootPart.GetDieAtEdge());   // what llGetStatus(STATUS_DIE_AT_EDGE) returns
            Assert.False(rezzed[0].RootPart.RETURN_AT_EDGE);
        }

        [Fact]
        public void AScriptCanClearDieAtEdgeOnAnObjectRezzedWhileTheSettingIsOn()
        {
            TestHelpers.InMethod();
            var f = new Fixture(true);
            var (rezzer, item) = NewRezzer(f);
            List<SceneObjectGroup> rezzed = f.Scene.RezObject(
                rezzer.RootPart, item, new Vector3(10, 10, 10), Quaternion.Identity, Vector3.Zero, 0, false);

            rezzed[0].RootPart.SetDieAtEdge(false);

            Assert.False(rezzed[0].DieAtVoidEdge);
            Assert.False(rezzed[0].RootPart.GetDieAtEdge());
        }

        [Fact]
        public void AnObjectRezzedByAScriptWithTheSettingOnIsDeletedAtAVoidEdge()
        {
            TestHelpers.InMethod();
            var f = new Fixture(true);
            var (rezzer, item) = NewRezzer(f);
            SceneObjectGroup so = f.Scene.RezObject(
                rezzer.RootPart, item, new Vector3(128, 255.9f, 20), Quaternion.Identity, Vector3.Zero, 0, false)[0];
            Body body = f.MakePhysical(so);

            f.PhysicsMovesPastTheEdge(so, body);

            Assert.True(f.WaitForEnd());
            Assert.True(so.IsDeleted);
            Assert.Empty(f.ReturnedItems());
        }

        [Fact]
        public void AnObjectRezzedByAScriptWithTheSettingOffIsStoppedAtAVoidEdge()
        {
            TestHelpers.InMethod();
            var f = new Fixture(false);
            var (rezzer, item) = NewRezzer(f);
            SceneObjectGroup so = f.Scene.RezObject(
                rezzer.RootPart, item, new Vector3(128, 255.9f, 20), Quaternion.Identity, Vector3.Zero, 0, false)[0];
            Body body = f.MakePhysical(so);

            f.PhysicsMovesPastTheEdge(so, body);

            Assert.True(f.WaitForEnd());
            Assert.False(so.IsDeleted);
            Assert.Equal(1, body.Failures);
            AssertInsideTheRegion(f.Scene, so.AbsolutePosition);
        }

        [Fact]
        public void AnObjectRezzedByAPersonHasNoDieAtEdgeWhateverTheSetting()
        {
            TestHelpers.InMethod();
            var f = new Fixture(true);
            var (rezzer, item) = NewRezzer(f);

            List<SceneObjectGroup> rezzed = f.Scene.RezObject(
                rezzer.RootPart, item, f.Owner.PrincipalID, UUID.Zero,
                new Vector3(10, 10, 10), Quaternion.Identity, Vector3.Zero, 0, false, false, true);

            Assert.Single(rezzed);
            Assert.False(rezzed[0].RootPart.DIE_AT_EDGE);
            Assert.False(rezzed[0].DieAtVoidEdge);
            Assert.False(rezzed[0].RootPart.GetDieAtEdge());
        }

        [Fact]
        public void AnObjectRezzedByAScriptWithTheSettingOnCrossesIntoANeighbourAndSurvives()
        {
            TestHelpers.InMethod();
            var f = new Fixture(true, withNeighbour: true);
            var (rezzer, item) = NewRezzer(f);
            SceneObjectGroup so = f.Scene.RezObject(
                rezzer.RootPart, item, new Vector3(128, 10, 20), Quaternion.Identity, Vector3.Zero, 0, false)[0];
            Assert.True(so.DieAtVoidEdge);

            so.AbsolutePosition = new Vector3(128, -10, 20);

            Assert.True(f.WaitForEnd());
            Assert.Null(f.Scene.GetSceneObjectGroup(so.UUID));
            Assert.NotNull(f.Neighbour.GetSceneObjectGroup(so.UUID));
            Assert.Empty(f.ReturnedItems());
        }

        [Fact]
        public void AnObjectWhoseScriptSetDieAtEdgeStillDiesAtACrossingIntoANeighbour()
        {
            TestHelpers.InMethod();
            var f = new Fixture(false, withNeighbour: true);
            SceneObjectGroup so = f.NewObject(0x31);
            so.AbsolutePosition = new Vector3(128, 10, 20);
            so.RootPart.SetDieAtEdge(true);

            so.AbsolutePosition = new Vector3(128, -10, 20);

            Assert.True(f.WaitForEnd());
            Assert.True(so.IsDeleted);
            Assert.Null(f.Neighbour.GetSceneObjectGroup(so.UUID));
            Assert.Empty(f.ReturnedItems());
        }
    }
}
