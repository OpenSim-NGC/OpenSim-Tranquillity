/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Connector / recorder ARMING, proven end to end on the sim side.
 *
 * The behaviour under test: a voice connector needs no arming mechanism of its own. VoiceConnectorRegistrar.Register
 * adds a VoiceViewerSession for the NPC (so FeederWorldFromScene's IsAgentInRegion gate admits it to the matrix
 * population) and calls pRecordRoom -> VoiceVisibilityService.OnListenerProvisioned(npc, estateRoom), which arms the
 * listener. Unregister removes the voice session, the NPC leaves the population, and the next heartbeat omits it
 * (the heartbeat's omission rule is what disarms the still-connected peer at the mixer). A recorder (MayInject=false) is the
 * same record plus the existing moderation mute.
 *
 * Rig: a REAL Scene (SceneHelpers) with a land parcel, a REAL VoiceVisibilityService running its own tick thread with
 * arming on, and a REAL JanusPeerCtlBatchSink whose admin transport is a capturing fake answering as a vis_protocol 2
 * mixer. The registrar is driven with the module's own delegate bodies (VoiceConnectorModule.StartRecord/StopRecord):
 * pRecordRoom is svc.OnListenerProvisioned, pMute is svc.Moderation.MuteAgent on the NPC's parcel. The NPC presence
 * is added the way NPCModule.CreateNPC adds it (AddNewCircuit + AddNewAgent(PresenceType.Npc) + CompleteMovement), so
 * any presence-type filter on the path would bite; this project does not reference OptionalModules, so a TestClient
 * stands in for NPCAvatar as the IClientAPI.
 *
 * The knob-off case lives with its golden in WebRtcJanusService.Tests (VisibilityKnobOffGoldenTests); the knob-off
 * real-service half is here.
 */

using System.Diagnostics;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class ConnectorArmingTests
    {
        private static readonly UUID Owner = new UUID("00000000-0000-0000-0000-0000000000f1");
        private static readonly UUID Avatar = new UUID("00000000-0000-0000-0000-00000000a0a1");
        private const int CadenceMs = 20;
        private const int WaitMs = 8000;

        /// Records every admin body in order and answers as a vis_protocol 2 mixer with a switchable mixer_instance.
        private sealed class Transport
        {
            private readonly object _lock = new object();
            private readonly List<OSDMap> _sent = new();
            public volatile string Instance = "m1";

            public Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                OSDMap copy = (OSDMap)OSDParser.DeserializeJson(OSDParser.SerializeJsonString(request));
                lock (_lock)
                    _sent.Add(copy);
                OSDMap response = copy["request"].AsString() == "peer_ctl_heartbeat"
                    ? new OSDMap
                    {
                        ["slvoice"] = OSD.FromString("heartbeat"),
                        ["vis_protocol"] = OSD.FromInteger(2),
                        ["mixer_instance"] = OSD.FromString(Instance),
                        ["rooms"] = new OSDMap(),
                    }
                    : new OSDMap
                    {
                        ["slvoice"] = OSD.FromString("applied"),
                        ["room"] = copy["room"],
                        ["vis_protocol"] = OSD.FromInteger(2),
                        ["mixer_instance"] = OSD.FromString(Instance),
                    };
                var top = new OSDMap { ["janus"] = OSD.FromString("success"), ["response"] = response };
                return Task.FromResult((AdminSendResult.Ok, OSDParser.SerializeJsonString(top)));
            }

            public int Count { get { lock (_lock) return _sent.Count; } }
            public List<OSDMap> Since(int index) { lock (_lock) return _sent.Skip(index).ToList(); }
        }

        private sealed class Rig : IDisposable
        {
            public Scene Scene;
            public ILandObject Parcel;
            public UUID RegionId;
            public int EstateRoom;
            public VoiceVisibilityService Svc;
            public Transport Transport;
            public readonly List<string> SessionIds = new();

            public void Dispose()
            {
                try { Svc?.Stop(); } catch { }
                foreach (string s in SessionIds)
                    VoiceViewerSession.RemoveViewerSession(s);
            }
        }

        private static Rig NewRig(bool armingEnabled)
        {
            var rig = new Rig();
            rig.Scene = new SceneHelpers().SetupScene();
            var lmm = new LandManagementModule();
            SceneHelpers.SetupSceneModules(rig.Scene, lmm);
            ILandObject lo = new LandObject(Owner, false, rig.Scene);
            lo.SetLandBitmap(lo.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            // These tests are about a connector on the ESTATE channel - every assertion below
            // names rig.EstateRoom - so the parcel has to say so. The sim resolves a connector's room from
            // its parcel (ConnectorRoomResolver); on a parcel WITHOUT this flag that is the parcel's own room,
            // which is correct and is covered by ConnectorRoomResolverTests.
            lo.LandData.Flags |= (uint)ParcelFlags.UseEstateVoiceChan;
            rig.Parcel = lmm.AddLandObject(lo);
            rig.Scene.Permissions.OnIsAdministrator += _ => false;   // an empty test scene is otherwise god-mode for all
            rig.RegionId = rig.Scene.RegionInfo.RegionID;
            // Exactly VoiceConnectorModule.StartRecord's derivation.
            rig.EstateRoom = JanusAudioBridge.CalcRoomNumber(
                string.Empty, rig.RegionId.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            StartService(rig, armingEnabled);
            return rig;
        }

        private static void StartService(Rig rig, bool armingEnabled)
        {
            rig.Transport = new Transport();
            var sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), rig.RegionId,
                rig.Scene.RegionInfo.RegionName, sendOne: rig.Transport.SendAsync);
            rig.Svc = new VoiceVisibilityService(rig.Scene, CadenceMs, emitEnabled: true, sink: sink,
                adminTimeout: TimeSpan.FromSeconds(5), armingEnabled: armingEnabled);
            Assert.That(sink.FallbackRoom, Is.EqualTo(rig.EstateRoom), "the connector's estate room is the sink's fallback room");
            rig.Svc.StartLoop();
        }

        /// An avatar: a root presence plus a provisioned voice session in this region.
        private static void AddAvatar(Rig rig, UUID id)
        {
            SceneHelpers.AddScenePresence(rig.Scene, id);
            var session = new VoiceViewerSession(null, rig.RegionId, id) { ClientSessionId = UUID.Random() };
            VoiceViewerSession.AddViewerSession(session);
            rig.SessionIds.Add(session.ViewerSessionID);
            rig.Svc.OnListenerProvisioned(id, rig.EstateRoom);
        }

        /// NPCModule.CreateNPC's presence sequence, with a TestClient as the IClientAPI.
        private static UUID CreateNpcPresence(Rig rig, VoiceConnectorRecord record, UUID id)
        {
            var acd = new AgentCircuitData
            {
                circuitcode = (uint)Random.Shared.Next(1, int.MaxValue),
                AgentID = id,
                firstname = record.NpcFirstName,
                lastname = record.NpcLastName,
                ServiceURLs = new Dictionary<string, object>(),
                Appearance = new AvatarAppearance(),
            };
            rig.Scene.AuthenticateHandler.AddNewCircuit(acd);
            var client = new TestClient(acd, rig.Scene);
            rig.Scene.AddNewAgent(client, PresenceType.Npc);
            Assert.That(rig.Scene.TryGetScenePresence(id, out ScenePresence sp), Is.True, "NPC presence created");
            sp.CompleteMovement(client, false);
            Assert.That(sp.PresenceType, Is.EqualTo(PresenceType.Npc), "the presence is a real NPC-typed presence");
            return id;
        }

        private static VoiceConnectorRecord NewRecord(string name, bool mayInject)
            => new VoiceConnectorRecord(name, true, name, "Connector", new Vector3(128, 128, 25),
                VoiceConnectorScope.Estate, mayInject, "Operator");

        /// The registrar, wired with VoiceConnectorModule.StartRecord's delegate bodies.
        private static UUID Register(Rig rig, VoiceConnectorRecord record)
        {
            UUID derived = ConnectorIdentity.DeriveAgentId("test-grid", rig.Scene.RegionInfo.RegionName, record.Name);
            VoiceVisibilityService svc = rig.Svc;
            bool ok = VoiceConnectorRegistrar.Register(record, rig.EstateRoom,
                pCreateNpc: r => CreateNpcPresence(rig, r, derived),
                pCreateSession: npcId => new VoiceViewerSession(null, rig.RegionId, npcId),
                pRecordRoom: (npcId, room) => svc?.OnListenerProvisioned(npcId, room),
                pMute: npcId =>
                {
                    ILandObject parcel = rig.Scene.LandChannel?.GetLandObject(record.Position.X, record.Position.Y);
                    Assert.That(parcel?.LandData, Is.Not.Null, "the NPC stands on a parcel");
                    svc.Moderation.MuteAgent(parcel.LandData.GlobalID, npcId);
                },
                pLog: null);
            Assert.That(ok, Is.True, "registered");
            rig.SessionIds.Add(record.ViewerSessionId);
            Assert.That(VoiceViewerSession.IsAgentInRegion(rig.RegionId, derived), Is.True, "membership on");
            return derived;
        }

        private static void Unregister(Rig rig, VoiceConnectorRecord record)
            => VoiceConnectorRegistrar.Unregister(record, npcId => rig.Scene.CloseAgent(npcId, false), null);

        // ---- wire readers ----

        private static bool IsBatch(OSDMap m) => m["request"].AsString() == "peer_ctl_batch";
        private static bool IsHeartbeat(OSDMap m) => m["request"].AsString() == "peer_ctl_heartbeat";
        private static bool IsReplace(OSDMap m) => IsBatch(m) && m["op"].AsString() == "replace";

        private static OSDMap Channel(OSDMap m, string key)
            => m.TryGetValue(key, out OSD c) && c is OSDMap map ? map : new OSDMap();

        private static bool Names(OSDMap batch, UUID id)
            => Channel(batch, "excl").ContainsKey(id.ToString()) || Channel(batch, "mute").ContainsKey(id.ToString());

        private static OSDMap HeartbeatListeners(OSDMap hb, int room)
            => hb["rooms"] is OSDMap rooms && rooms.TryGetValue(room.ToString(), out OSD r) && r is OSDMap entry
                && entry["listeners"] is OSDMap ls ? ls : new OSDMap();

        /// Every body anywhere that mentions the id (a listener key, a source in a column, a heartbeat listener).
        private static bool Mentions(OSDMap body, UUID id) => OSDParser.SerializeJsonString(body).Contains(id.ToString());

        private static OSDMap WaitFor(Transport t, int from, Func<OSDMap, bool> match, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < WaitMs)
            {
                OSDMap hit = t.Since(from).FirstOrDefault(match);
                if (hit != null)
                    return hit;
                Thread.Sleep(10);
            }
            Assert.Fail($"timed out after {WaitMs} ms waiting for {what}");
            return null;
        }

        private static int IndexOf(Transport t, int from, Func<OSDMap, bool> match, string what)
        {
            WaitFor(t, from, match, what);
            return from + t.Since(from).FindIndex(m => match(m));
        }

        private static void AssertArmingReplace(OSDMap replace, Rig rig, UUID npc)
        {
            Assert.That(replace["room"].AsInteger(), Is.EqualTo(rig.EstateRoom), "arming replace addressed at the estate room");
            Assert.That(replace.ContainsKey("room_epoch"), Is.True, "stamped with the authority's epoch");
            Assert.That(Channel(replace, "excl")[npc.ToString()] is OSDArray e && e.Count == 0, Is.True,
                "the NPC's exclusion column is present and empty");
            Assert.That(Channel(replace, "mute")[npc.ToString()] is OSDArray m && m.Count == 0, Is.True,
                "the NPC's mute column is present and empty");
        }

        // ---- inject connector ----
        [Test]
        public void T1_RegisterInjectConnector_ArmsTheNpcAtTheEstateRoom_WithEmptyColumns()
        {
            using Rig rig = NewRig(armingEnabled: true);
            AddAvatar(rig, Avatar);
            WaitFor(rig.Transport, 0, m => IsReplace(m) && Names(m, Avatar), "the avatar's arming replace");

            int mark = rig.Transport.Count;
            UUID npc = Register(rig, NewRecord("Speaker", mayInject: true));

            OSDMap replace = WaitFor(rig.Transport, mark, m => IsReplace(m) && Names(m, npc), "an arming replace naming the NPC");
            AssertArmingReplace(replace, rig, npc);
            Assert.That(rig.Svc.RoomOf(npc), Is.EqualTo(rig.EstateRoom), "pRecordRoom recorded the estate room");
        }

        // ---- recorder ----
        [Test]
        public void T2_RegisterRecorder_ArmsTheNpcTheSameWay_AndTheMuteNamesTheNpc()
        {
            using Rig rig = NewRig(armingEnabled: true);
            AddAvatar(rig, Avatar);
            WaitFor(rig.Transport, 0, m => IsReplace(m) && Names(m, Avatar), "the avatar's arming replace");

            int mark = rig.Transport.Count;
            UUID npc = Register(rig, NewRecord("Recorder", mayInject: false));

            OSDMap replace = WaitFor(rig.Transport, mark, m => IsReplace(m) && Names(m, npc), "an arming replace naming the recorder NPC");
            AssertArmingReplace(replace, rig, npc);
            Assert.That(rig.Svc.Moderation.IsModerated(rig.Parcel.LandData.GlobalID, npc), Is.True,
                "the moderation store's mute key is (parcel, NPC id)");
            // The mute reaches the wire as the avatar's mute column naming the NPC as the silenced source.
            WaitFor(rig.Transport, mark, m => IsBatch(m) && Channel(m, "mute")[Avatar.ToString()] is OSDArray a
                && a.Any(s => s.AsString() == npc.ToString()), "a batch whose mute column for the avatar names the NPC");
        }

        // ---- mixer restart ----
        [Test]
        public void T3_MixerRestartAndNewEpoch_ReArmTheNpcAlongWithAvatars()
        {
            using Rig rig = NewRig(armingEnabled: true);
            AddAvatar(rig, Avatar);
            UUID npc = Register(rig, NewRecord("Speaker", mayInject: true));
            OSDMap first = WaitFor(rig.Transport, 0, m => IsReplace(m) && Names(m, npc), "the NPC armed");
            // Heartbeats start once a batch reply advertised vis_protocol 2; the restart is reported through them.
            WaitFor(rig.Transport, 0, IsHeartbeat, "a first heartbeat");

            int mark = rig.Transport.Count;
            rig.Transport.Instance = "m2";   // the mixer restarted
            OSDMap rearm = WaitFor(rig.Transport, mark, m => IsReplace(m) && Names(m, npc) && Names(m, Avatar),
                "one re-arm-all replace naming the NPC and the avatar after mixer_instance changed");
            AssertArmingReplace(rearm, rig, npc);

            // A new feeder start is a new authority, so a new room_epoch: the first snapshot arms both again.
            rig.Svc.Stop();
            StartService(rig, armingEnabled: true);
            // The new service starts with no room records; each agent is placed again by a provision into its room.
            rig.Svc.OnListenerProvisioned(npc, rig.EstateRoom);
            rig.Svc.OnListenerProvisioned(Avatar, rig.EstateRoom);
            OSDMap fresh = WaitFor(rig.Transport, 0, m => IsReplace(m) && Names(m, npc) && Names(m, Avatar),
                "the new epoch's snapshot naming the NPC and the avatar");
            AssertArmingReplace(fresh, rig, npc);
            Assert.That(fresh["room_epoch"].AsString(), Is.Not.EqualTo(first["room_epoch"].AsString()), "a new epoch");
        }

        // ---- heartbeat ----
        [Test]
        public void T4_HeartbeatListenersMap_CarriesTheNpcWhileRegistered()
        {
            using Rig rig = NewRig(armingEnabled: true);
            AddAvatar(rig, Avatar);
            UUID npc = Register(rig, NewRecord("Recorder", mayInject: false));
            WaitFor(rig.Transport, 0, m => IsReplace(m) && Names(m, npc), "the NPC armed");

            OSDMap hb = WaitFor(rig.Transport, 0,
                m => IsHeartbeat(m) && HeartbeatListeners(m, rig.EstateRoom).TryGetValue(npc.ToString(), out OSD g) && g.AsInteger() > 0,
                "a heartbeat listing the NPC as armed (generation > 0) at the estate room");
            Assert.That(HeartbeatListeners(hb, rig.EstateRoom).ContainsKey(Avatar.ToString()), Is.True, "alongside the avatar");
        }

        // ---- unregister ----
        [Test]
        public void T5_AfterUnregister_TheNextHeartbeatOmitsTheNpc_AndNothingLaterNamesIt()
        {
            using Rig rig = NewRig(armingEnabled: true);
            AddAvatar(rig, Avatar);
            var record = NewRecord("Speaker", mayInject: true);
            UUID npc = Register(rig, record);
            WaitFor(rig.Transport, 0,
                m => IsHeartbeat(m) && HeartbeatListeners(m, rig.EstateRoom).ContainsKey(npc.ToString()), "a heartbeat carrying the NPC");

            int mark = rig.Transport.Count;
            Unregister(rig, record);
            Assert.That(VoiceViewerSession.IsAgentInRegion(rig.RegionId, npc), Is.False, "membership off");

            // A heartbeat already in flight at Unregister may still carry it; the first one that omits it is the disarm.
            int omit = IndexOf(rig.Transport, mark,
                m => IsHeartbeat(m) && HeartbeatListeners(m, rig.EstateRoom).ContainsKey(Avatar.ToString())
                    && !HeartbeatListeners(m, rig.EstateRoom).ContainsKey(npc.ToString()),
                "a heartbeat that lists the avatar and omits the NPC");

            // Force batch traffic after the omission too: a mixer restart re-arms everyone still present.
            rig.Transport.Instance = "m2";
            int after = omit + 1;
            WaitFor(rig.Transport, after, m => IsReplace(m) && Names(m, Avatar), "a re-arm replace for the avatar after the omission");
            WaitFor(rig.Transport, rig.Transport.Count, IsHeartbeat, "one more heartbeat");

            List<OSDMap> later = rig.Transport.Since(after);
            Assert.That(later.Where(IsBatch).Count(), Is.GreaterThan(0), "later batches exist to check");
            Assert.That(later.Where(m => Mentions(m, npc)).Select(m => OSDParser.SerializeJsonString(m)).ToList(), Is.Empty,
                "no batch or heartbeat after the omitting heartbeat names the NPC");
        }

        // ---- knob off (real-service half; the golden half is VisibilityKnobOffGoldenTests) ----
        [Test]
        public void T6_KnobOff_ConnectorRegistered_NoArmingTrafficAtAll()
        {
            using Rig rig = NewRig(armingEnabled: false);
            AddAvatar(rig, Avatar);
            UUID npc = Register(rig, NewRecord("Speaker", mayInject: true));
            Thread.Sleep(2500);   // > two heartbeat intervals and ~100 ticks

            List<OSDMap> all = rig.Transport.Since(0);
            Assert.That(all.Where(IsHeartbeat), Is.Empty, "no heartbeat with the knob off");
            Assert.That(all.Where(m => m.ContainsKey("room_epoch") || m.ContainsKey("policy_generation") || m.ContainsKey("base")),
                Is.Empty, "no authority stamps with the knob off");
            Assert.That(all.Where(m => Mentions(m, npc)), Is.Empty,
                "an empty-column connector is never sent knob-off (the join path skips empty columns)");
        }
    }
}
