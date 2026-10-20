/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for the agent -> room record (AgentRoomTable) and its wiring through
 * VoiceVisibilityService.OnListenerProvisioned.
 *
 * Table semantics the brief calls for: newest wins (OQ7), missing key resolves to null. Plus the
 * two guards the wiring relies on: a null room (failure / logout map) leaves an existing record
 * untouched, and a zero UUID is ignored (mirrors the sender's pending-join guard).
 *
 * The table tests need no Scene. The service-level test builds a bare SceneHelpers scene (no
 * presences — sidestepping the ScenePresence finalizer fragility noted in FeederWorldFromSceneTests)
 * and never calls Start(), so no tick thread runs.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class AgentRoomTableTests
    {
        [Test]
        public void Resolve_MissingAgent_IsNull()
        {
            var t = new AgentRoomTable();
            Assert.That(t.Resolve(UUID.Random()), Is.Null);
            Assert.That(t.Count, Is.EqualTo(0));
        }

        [Test]
        public void Record_ThenResolve_ReturnsTheRoom()
        {
            var t = new AgentRoomTable();
            UUID a = UUID.Random();
            t.Record(a, 1967062692);
            Assert.That(t.Resolve(a), Is.EqualTo(1967062692));
            Assert.That(t.Count, Is.EqualTo(1));
        }

        // OQ7: the newest provision wins; a relog overlap's older session is addressed at the new room.
        [Test]
        public void Record_Twice_NewestWins()
        {
            var t = new AgentRoomTable();
            UUID a = UUID.Random();
            t.Record(a, 226001844);
            t.Record(a, 1578726032);
            Assert.That(t.Resolve(a), Is.EqualTo(1578726032), "second provision replaces the first");
            Assert.That(t.Count, Is.EqualTo(1), "one record per agent, not one per provision");
        }

        [Test]
        public void Record_ZeroUuid_IsIgnored()
        {
            var t = new AgentRoomTable();
            t.Record(UUID.Zero, 5);
            Assert.That(t.Resolve(UUID.Zero), Is.Null);
            Assert.That(t.Count, Is.EqualTo(0));
        }

        [Test]
        public void Tables_AreIndependent_PerInstance()
        {
            // One table per VoiceVisibilityService, i.e. per region: a record in one is invisible in another.
            var t1 = new AgentRoomTable();
            var t2 = new AgentRoomTable();
            UUID a = UUID.Random();
            t1.Record(a, 7);
            Assert.That(t2.Resolve(a), Is.Null);
        }

        // Service-level wiring: OnListenerProvisioned(agent, room) records; (agent, null) and the
        // the overload without a room leaves the record alone; RoomOf is the table's resolver.
        [Test]
        public void Service_RecordsOnSuccess_LeavesRecordOnNull_NewestWins()
        {
            Scene scene = new SceneHelpers().SetupScene();
            var svc = new VoiceVisibilityService(scene, cadenceMs: 250);   // not started: no tick thread
            UUID a = UUID.Random();

            Assert.That(svc.RoomOf(a), Is.Null, "no record before any provision");

            svc.OnListenerProvisioned(a, 226001844);                  // success map carried a room
            Assert.That(svc.RoomOf(a), Is.EqualTo(226001844));

            svc.OnListenerProvisioned(a, null);                       // failure / logout map: no room
            Assert.That(svc.RoomOf(a), Is.EqualTo(226001844), "a null room must not erase the record");

            svc.OnListenerProvisioned(a);                             // overload without a room delegates null
            Assert.That(svc.RoomOf(a), Is.EqualTo(226001844), "the old overload leaves the record untouched");

            svc.OnListenerProvisioned(a, 1578726032);                 // re-provision elsewhere
            Assert.That(svc.RoomOf(a), Is.EqualTo(1578726032), "newest provision wins (OQ7)");
        }
    }
}
