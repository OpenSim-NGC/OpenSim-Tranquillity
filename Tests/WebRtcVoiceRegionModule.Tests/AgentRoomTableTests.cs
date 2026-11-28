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
 * Table semantics: newest wins, missing key resolves to null. Plus the
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
    public class AgentRoomTableTests
    {
        [Fact(Timeout = 60000)]
        public void Resolve_MissingAgent_IsNull()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var t = new AgentRoomTable();
            Assert.Null(t.Resolve(UUID.Random()));
            Assert.Equal(0, t.Count);
        }

        [Fact(Timeout = 60000)]
        public void Record_ThenResolve_ReturnsTheRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var t = new AgentRoomTable();
            UUID a = UUID.Random();
            t.Record(a, 1967062692);
            Assert.Equal(1967062692, t.Resolve(a));
            Assert.Equal(1, t.Count);
        }

        // The newest provision wins; a relog overlap's older session is addressed at the new room.
        [Fact(Timeout = 60000)]
        public void Record_Twice_NewestWins()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var t = new AgentRoomTable();
            UUID a = UUID.Random();
            t.Record(a, 226001844);
            t.Record(a, 1578726032);
            {
                var assertedValue = t.Resolve(a);
                TestAssert.WithMessage(() => Assert.Equal(1578726032, assertedValue), "second provision replaces the first");
            }
            {
                var assertedValue = t.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one record per agent, not one per provision");
            }
        }

        [Fact(Timeout = 60000)]
        public void Record_ZeroUuid_IsIgnored()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var t = new AgentRoomTable();
            t.Record(UUID.Zero, 5);
            Assert.Null(t.Resolve(UUID.Zero));
            Assert.Equal(0, t.Count);
        }

        [Fact(Timeout = 60000)]
        public void Tables_AreIndependent_PerInstance()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // One table per VoiceVisibilityService, i.e. per region: a record in one is invisible in another.
            var t1 = new AgentRoomTable();
            var t2 = new AgentRoomTable();
            UUID a = UUID.Random();
            t1.Record(a, 7);
            Assert.Null(t2.Resolve(a));
        }

        // Service-level wiring: OnListenerProvisioned(agent, room) records; (agent, null) and the
        // the overload without a room leaves the record alone; RoomOf is the table's resolver.
        [Fact(Timeout = 60000)]
        public void Service_RecordsOnSuccess_LeavesRecordOnNull_NewestWins()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            var svc = new VoiceVisibilityService(scene, cadenceMs: 250);   // not started: no tick thread
            UUID a = UUID.Random();

            {
                var assertedValue = svc.RoomOf(a);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no record before any provision");
            }

            svc.OnListenerProvisioned(a, 226001844);                  // success map carried a room
            Assert.Equal(226001844, svc.RoomOf(a));

            svc.OnListenerProvisioned(a, null);                       // failure / logout map: no room
            {
                var assertedValue = svc.RoomOf(a);
                TestAssert.WithMessage(() => Assert.Equal(226001844, assertedValue), "a null room must not erase the record");
            }

            svc.OnListenerProvisioned(a);                             // overload without a room delegates null
            {
                var assertedValue = svc.RoomOf(a);
                TestAssert.WithMessage(() => Assert.Equal(226001844, assertedValue), "the old overload leaves the record untouched");
            }

            svc.OnListenerProvisioned(a, 1578726032);                 // re-provision elsewhere
            {
                var assertedValue = svc.RoomOf(a);
                TestAssert.WithMessage(() => Assert.Equal(1578726032, assertedValue), "newest provision wins");
            }
        }

        // The address of an agent is the room its last provision joined and nothing else: an agent that has only been
        // in a group or avatar-to-avatar call has no record, so the service resolves no room for it.
        [Fact(Timeout = 60000)]
        public void Service_ResolveRoom_IsTheRecordOnly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            var svc = new VoiceVisibilityService(scene, cadenceMs: 250);
            UUID a = UUID.Random();

            {
                var assertedValue = svc.ResolveRoom(a);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no provision, so no room");
            }

            svc.OnListenerProvisioned(a, 226001844);
            Assert.Equal(226001844, svc.ResolveRoom(a));
            {
                var assertedValue = svc.ResolveRoom(a);
                TestAssert.WithMessage(() => Assert.Equal(svc.RoomOf(a), assertedValue), "the resolver the sender uses is the sink's");
            }
        }
    }
}
