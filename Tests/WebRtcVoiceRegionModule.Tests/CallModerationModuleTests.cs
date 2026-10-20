/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Call moderation through the region module: the real module, read from an ini, answering the viewer's
 * ChatSessionRequest for a group call, with a groups service that says who is a member and who may moderate,
 * a voice service that records the per-participant mutes it is asked for, and an event queue that records what
 * members are told.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class CallModerationModuleTests
    {
        private static readonly UUID GroupId = new UUID("5e0c9a41-7b2d-4f86-a3c1-92d84b6e0f17");
        private static readonly UUID Officer = new UUID("11111111-1111-4111-8111-111111111111");
        private static readonly UUID Member = new UUID("22222222-2222-4222-8222-222222222222");
        private static readonly UUID Other = new UUID("33333333-3333-4333-8333-333333333333");

        /// <summary>A groups service: who is in the group and what powers each holds.</summary>
        public class FakeGroups : DispatchProxy
        {
            public static readonly Dictionary<UUID, ulong> Powers = new();

            /// <summary>How many times each method was called since the last <see cref="Region.Create"/>.</summary>
            public static readonly Dictionary<string, int> Calls = new();

            protected override object Invoke(MethodInfo method, object[] args)
            {
                lock (Calls) Calls[method.Name] = Calls.GetValueOrDefault(method.Name) + 1;
                switch (method.Name)
                {
                    case nameof(IGroupsModule.GetMembershipData) when args.Length == 2:
                        return Powers.ContainsKey((UUID)args[1]) ? new GroupMembershipData { GroupID = (UUID)args[0] } : null;
                    case nameof(IGroupsModule.GetFullGroupPowers):
                        return Powers.TryGetValue((UUID)args[0], out ulong p) ? p : 0UL;
                    default:
                        Type rt = method.ReturnType;
                        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
                }
            }
        }

        internal sealed class FakeVoice : IWebRtcVoiceService, INonSpatialParticipantMute, INonSpatialParticipantRemoval
        {
            /// Every call to DropCallParticipants: the agent and the room.
            public readonly List<(UUID Agent, int Room)> Drops = new();
            public int DropAnswer = 1;
            public int DropCallParticipants(UUID pAgentID, int pRoomNumber)
            {
                lock (Drops) Drops.Add((pAgentID, pRoomNumber));
                return DropAnswer;
            }
            public readonly List<(UUID Agent, int Room, bool Muted)> Mutes = new();
            public ParticipantMuteResult Answer = ParticipantMuteResult.Applied;

            /// Agents the room has no participant for. Everyone else has one.
            public readonly HashSet<UUID> NoParticipant = new();

            /// An agent whose mute the room cannot carry out.
            public UUID FailFor;
            public ParticipantMuteResult SetCallMute(UUID pAgentID, int pRoomNumber, bool pMuted)
            {
                lock (Mutes) Mutes.Add((pAgentID, pRoomNumber, pMuted));
                if (NoParticipant.Contains(pAgentID)) return ParticipantMuteResult.NotInRoom;
                if (pMuted && pAgentID == FailFor) return ParticipantMuteResult.Failed;
                return Answer;
            }
            public bool HasCallParticipant(UUID pAgentID, int pRoomNumber) => !NoParticipant.Contains(pAgentID);
            private int m_provisions;

            /// What a real provision answers with: the success map carrying the viewer session and the room.
            private OSDMap Provisioned() => new OSDMap
            {
                ["viewer_session"] = OSD.FromString("vs-p" + System.Threading.Interlocked.Increment(ref m_provisions)),
                ["room"] = OSD.FromInteger(68971284),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("answer"), ["sdp"] = OSD.FromString("v=0") },
            };
            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => Provisioned();
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => Provisioned();
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;
        }

        internal sealed class Queue : IEventQueue
        {
            public readonly List<(UUID To, UUID Session, string Reason)> Closes = new();
            public readonly List<(UUID To, UUID Session, List<GroupChatListAgentUpdateData> Updates)> Lists = new();
            public readonly List<(UUID To, UUID Session, bool Moderated)> Modes = new();
            public bool Enqueue(byte[] o, UUID avatarID) => true;
            public bool Enqueue(OSD o, UUID avatarID) => true;
            public bool Enqueue(osUTF8 o, UUID avatarID) => true;
            public byte[] BuildEvent(string eventName, OSD eventBody) => new byte[] { 1 };
            public void EnableSimulator(ulong handle, IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY) { }
            public void EstablishAgentCommunication(UUID avatarID, IPEndPoint endPoint, string capsPath, ulong regionHandle, int regionSizeX, int regionSizeY) { }
            public void TeleportFinishEvent(ulong regionHandle, byte simAccess, IPEndPoint regionExternalEndPoint, uint locationID, uint flags, string capsURL, UUID agentID, int regionSizeX, int regionSizeY) { }
            public void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, IPEndPoint newRegionExternalEndPoint, string capsURL, UUID avatarID, UUID sessionID, int regionSizeX, int regionSizeY) { }
            public void ChatterboxInvitation(UUID sessionID, string sessionName, UUID fromAgent, string message, UUID toAgent, string fromName, byte dialog, uint timeStamp, bool offline, int parentEstateID, Vector3 position, uint ttl, UUID transactionID, bool fromGroup, byte[] binaryBucket) { }
            public void ChatterBoxSessionStartReply(UUID sessionID, string sessionName, int type, bool voiceEnabled, bool voiceModerated, UUID tmpSessionID, bool sucess, string error, UUID toAgent) { }
            public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID toAgent, List<GroupChatListAgentUpdateData> updates)
            {
                lock (Lists) Lists.Add((toAgent, sessionID, updates));
            }
            public void ChatterBoxSessionUpdate(UUID sessionID, UUID toAgent, bool voiceModerated)
            {
                lock (Modes) Modes.Add((toAgent, sessionID, voiceModerated));
            }
            public void ChatterBoxForceClose(UUID toAgent, UUID sessionID, string reason)
            {
                lock (Closes) Closes.Add((toAgent, sessionID, reason));
            }
            public void GroupMembershipData(UUID receiverAgent, GroupMembershipData[] data) { }
            public void ScriptRunningEvent(UUID objectID, UUID itemID, bool running, UUID avatarID) { }
            public void partPhysicsProperties(uint localID, byte physhapetype, float density, float friction, float bounce, float gravmod, UUID avatarID) { }
            public void WindlightRefreshEvent(int interpolate, UUID avatarID) { }
            public void SendEnvironmentUpdate(UUID experience_id, UUID agent_id, EnvironmentUpdate update) { }
            public void SendBulkUpdateInventoryItem(InventoryItemBase item, UUID avatarID, UUID? transationID = null) { }
            public osUTF8 StartEvent(string eventName) => throw new NotSupportedException();
            public osUTF8 StartEvent(string eventName, int cap) => throw new NotSupportedException();
            public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<byte[]> message) { }
            public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<string> message) { }
            public byte[] EndEventToBytes(osUTF8 sb) => throw new NotSupportedException();
        }

        internal sealed class Region : IDisposable
        {
            public Scene Scene;
            public WebRtcVoiceRegionModule Module;
            public FakeVoice Voice = new();
            public Queue Events = new();

            public void Dispose() => Module?.Close();

            public static Region Create(Action<IConfig> voice = null, Action<IConfig> janus = null)
            {
                FakeGroups.Powers.Clear();
                lock (FakeGroups.Calls) FakeGroups.Calls.Clear();
                FakeGroups.Powers[Officer] = CallModerationPolicy.PowerModerateChat | GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;
                FakeGroups.Powers[Member] = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;
                FakeGroups.Powers[Other] = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;

                IniConfigSource config = new IniConfigSource();
                IConfig v = config.AddConfig("WebRtcVoice");
                v.Set("Enabled", "true");
                v.Set("GroupVoiceEnabled", "true");
                IConfig j = config.AddConfig("JanusWebRtcVoice");
                j.Set("PluginName", "janus.plugin.audiobridge");
                voice?.Invoke(v);
                janus?.Invoke(j);

                Region r = new Region { Scene = new SceneHelpers().SetupScene(), Module = new WebRtcVoiceRegionModule() };
                r.Scene.RegisterModuleInterface<IEventQueue>(r.Events);
                r.Scene.RegisterModuleInterface<IGroupsModule>(DispatchProxy.Create<IGroupsModule, FakeGroups>());
                r.Scene.RegisterModuleInterface<IWebRtcVoiceService>(r.Voice);
                foreach (UUID a in new[] { Officer, Member, Other })
                    SceneHelpers.AddScenePresence(r.Scene, a);
                r.Module.Initialise(config);
                r.Module.AddRegion(r.Scene);
                return r;
            }

            public int Post(UUID agent, OSDMap body)
            {
                TestOSHttpResponse resp = new TestOSHttpResponse();
                Module.ChatSessionRequest(new LlsdPost(body), resp, agent, Scene);
                return resp.StatusCode;
            }

            public int Call(UUID agent) => Post(agent, new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(GroupId) });

            public INonSpatialSessions Sessions
                => (INonSpatialSessions)typeof(WebRtcVoiceRegionModule).GetField("m_nonSpatial", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Module);

            /// <summary>Put an agent in the call's room, as an admitted provision does.</summary>
            public void Present(UUID agent) => Sessions.MarkPresent(GroupId, agent, Scene.RegionInfo.RegionID, "vs-" + agent.ToString()[..4]);
        }

        private static OSDMap MuteUpdate(UUID target, string kind, bool value)
            => new OSDMap
            {
                ["method"] = OSD.FromString("mute update"),
                ["session-id"] = OSD.FromUUID(GroupId),
                ["params"] = new OSDMap
                {
                    ["agent_id"] = OSD.FromUUID(target),
                    ["mute_info"] = new OSDMap { [kind] = OSD.FromBoolean(value) },
                },
            };

        private static OSDMap SessionUpdate(bool value)
            => new OSDMap
            {
                ["method"] = OSD.FromString("session update"),
                ["session-id"] = OSD.FromUUID(GroupId),
                ["params"] = new OSDMap
                {
                    ["update_info"] = new OSDMap { ["moderated_mode"] = new OSDMap { ["voice"] = OSD.FromBoolean(value) } },
                },
            };

        private static void Moderation(IConfig v) => v.Set("CallModerationEnabled", "true");

        // ---- the switch -----------------------------------------------------------------------------------

        [Test]
        public void WithTheKeyOff_EveryModerationRequestIsAnswered400_AsItWasBeforeTheFeature()
        {
            // Four combinations of the other voice features, key unset and key explicitly false.
            foreach (bool a2a in new[] { false, true })
                foreach (bool setFalse in new[] { false, true })
                {
                    using Region r = Region.Create(v =>
                    {
                        v.Set("A2AVoiceEnabled", a2a ? "true" : "false");
                        if (setFalse) v.Set("CallModerationEnabled", "false");
                    });
                    r.Call(Officer);

                    Assert.That(r.Post(Officer, MuteUpdate(Member, "voice", true)), Is.EqualTo((int)HttpStatusCode.BadRequest), $"a2a={a2a}");
                    Assert.That(r.Post(Officer, MuteUpdate(Member, "text", true)), Is.EqualTo((int)HttpStatusCode.BadRequest));
                    Assert.That(r.Post(Officer, SessionUpdate(true)), Is.EqualTo((int)HttpStatusCode.BadRequest));
                    Assert.That(r.Sessions.Get(GroupId).ModerationGeneration, Is.EqualTo(0), "nothing was recorded");
                    Assert.That(r.Scene.RequestModuleInterface<IGroupChatGate>(), Is.Null, "no group chat check is offered");
                    Assert.That(r.Events.Modes, Is.Empty);
                    Assert.That(r.Voice.Mutes, Is.Empty);
                }
        }

        [Test]
        public void TheKeyOnWithNoCallFeature_StaysOff()
        {
            using Region r = Region.Create(v =>
            {
                v.Set("GroupVoiceEnabled", "false");
                Moderation(v);
            });

            Assert.That(r.Post(Officer, MuteUpdate(Member, "voice", true)), Is.EqualTo((int)HttpStatusCode.BadRequest));
            Assert.That(r.Scene.RequestModuleInterface<IGroupChatGate>(), Is.Null);
        }

        // ---- grid mode: a member seated through another region server --------------------------------------------

        [Test]
        public void InGridMode_AMuteOfAMemberSeatedThroughAnotherRegionServer_NamesThatAgentAndNoOtherAgent()
        {
            using Region r = Region.Create(Moderation);
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions here = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions there = VoiceSessionServiceTests.NewRegion(service);
            typeof(WebRtcVoiceRegionModule).GetField("m_nonSpatial", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(r.Module, here);
            UUID home = r.Scene.RegionInfo.RegionID, elsewhere = UUID.Random();
            here.Start(NonSpatialSessionType.Group, Officer, GroupId, GroupId, home);
            here.MarkPresent(GroupId, Officer, home, "vs-officer");
            foreach (UUID far in new[] { Member, Other })
            {
                there.Accept(GroupId, far, elsewhere, "vs-" + far.ToString()[..4]);
                there.MarkPresent(GroupId, far, elsewhere, "vs-" + far.ToString()[..4]);
            }

            int status = r.Post(Officer, MuteUpdate(Member, "voice", true));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(r.Voice.Mutes.Where(m => m.Muted).Select(m => m.Agent), Is.EqualTo(new[] { Member }),
                        "the mute names the agent id the other server set, and no one else");
            Assert.That(there.Get(GroupId).IsVoiceMuted(Member), Is.True, "and the other region server sees the record");
            Assert.That(there.Get(GroupId).IsVoiceMuted(Other), Is.False);
        }

        // ---- the roster -----------------------------------------------------------------------------------

        [Test]
        public void AModeratorsOwnEntryCarriesTheModeratorFlag_AndAPlainMembersDoesNot()
        {
            using Region r = Region.Create(Moderation);

            Assert.That(r.Call(Officer), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(r.Call(Member), Is.EqualTo((int)HttpStatusCode.OK));

            var officer = r.Events.Lists.Single(l => l.To == Officer).Updates.Single();
            var member = r.Events.Lists.Single(l => l.To == Member).Updates.Single();
            Assert.That(officer.agentID, Is.EqualTo(Officer));
            Assert.That(officer.isModerator, Is.True);
            Assert.That(officer.canVoice, Is.True, "false is read by the viewer as a refusal");
            Assert.That(member.isModerator, Is.False);
            Assert.That(member.canVoice, Is.True);
        }

        [Test]
        public void WithTheKeyOff_NoModeratorEntryIsSent()
        {
            using Region r = Region.Create();

            r.Call(Officer);

            Assert.That(r.Events.Lists, Is.Empty);
        }

        [Test]
        public void ALateJoinerToAMutedCallIsToldTheCallIsModerated()
        {
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Post(Officer, SessionUpdate(true));
            r.Events.Modes.Clear();

            r.Call(Member);

            Assert.That(r.Events.Modes, Has.Some.Matches<(UUID To, UUID Session, bool Moderated)>(m => m.To == Member && m.Moderated));
        }

        // ---- requests, through the module --------------------------------------------------------------------

        [Test]
        public void TheOfficerMutesAMember_TheVoiceServerIsAskedAndThePlainMemberIsRefused()
        {
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);

            int refused = r.Post(Member, MuteUpdate(Other, "voice", true));
            int ok = r.Post(Officer, MuteUpdate(Member, "voice", true));

            Assert.That(refused, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(ok, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(r.Sessions.Get(GroupId).IsVoiceMuted(Member), Is.True);
            Assert.That(r.Voice.Mutes, Has.Some.Matches<(UUID Agent, int Room, bool Muted)>(m => m.Agent == Member && m.Muted));
            Assert.That(r.Voice.Mutes, Has.None.Matches<(UUID Agent, int Room, bool Muted)>(m => m.Agent == Officer && m.Muted),
                        "a moderator is not muted");
        }

        [Test]
        public void MuteAll_TellsTheCallAndMutesEveryoneButTheModerator_AndALaterJoinerIsMuted()
        {
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);

            Assert.That(r.Post(Officer, SessionUpdate(true)), Is.EqualTo((int)HttpStatusCode.OK));

            Assert.That(r.Events.Modes.Where(m => m.Moderated).Select(m => m.To), Is.SupersetOf(new[] { Officer, Member }));
            Assert.That(r.Voice.Mutes.Where(m => m.Muted).Select(m => m.Agent), Does.Contain(Member).And.Not.Contain(Officer));

            r.Call(Other);
            r.Present(Other);
            r.Voice.Mutes.Clear();
            typeof(WebRtcVoiceRegionModule).GetMethod("SyncCallVoiceOnJoin", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(r.Module, new object[] { Other, r.Sessions.Get(GroupId) });
            Assert.That(r.Voice.Mutes, Has.Some.Matches<(UUID Agent, int Room, bool Muted)>(m => m.Agent == Other && m.Muted),
                        "whoever joins afterwards is silenced in the room");

            Assert.That(r.Post(Officer, SessionUpdate(false)), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(r.Events.Modes.Last().Moderated, Is.False);
            Assert.That(r.Voice.Mutes.Last().Muted, Is.False);
        }

        [Test]
        public void ATextMuteDropsTheMembersGroupChat_AndTheirOwnEntryShowsIt()
        {
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            IGroupChatGate gate = r.Scene.RequestModuleInterface<IGroupChatGate>();
            Assert.That(gate, Is.Not.Null);
            Assert.That(gate.MaySend(GroupId, Member), Is.True);

            Assert.That(r.Post(Officer, MuteUpdate(Member, "text", true)), Is.EqualTo((int)HttpStatusCode.OK));

            Assert.That(gate.MaySend(GroupId, Member), Is.False);
            Assert.That(gate.MaySend(GroupId, Officer), Is.True);
            Assert.That(gate.MaySend(UUID.Random(), Member), Is.True, "another group's chat is not touched");
            var told = r.Events.Lists.Last(l => l.To == Member && l.Updates.Any(u => u.mutedText));
            Assert.That(told.Updates.Single().agentID, Is.EqualTo(Member));

            Assert.That(r.Post(Officer, MuteUpdate(Member, "text", false)), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(gate.MaySend(GroupId, Member), Is.True, "unmuting restores it at once");
        }

        // ---- who a voice mute can name -------------------------------------------------------------------------

        [Test]
        public void AMuteNamingAnAgentTheRoomHasNoParticipantFor_IsRefused404_AndNothingIsRecordedOrSent()
        {
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);
            r.Voice.NoParticipant.Add(Member);

            Assert.That(r.Post(Officer, MuteUpdate(Member, "voice", true)), Is.EqualTo((int)HttpStatusCode.NotFound));

            Assert.That(r.Sessions.Get(GroupId).IsVoiceMuted(Member), Is.False);
            Assert.That(r.Sessions.Get(GroupId).ModerationGeneration, Is.EqualTo(0), "nothing recorded");
            Assert.That(r.Voice.Mutes, Is.Empty, "and no mute went to the room");
        }

        [Test]
        public void AMuteNamingAnAgentThatIsNotInTheCall_IsRefused404()
        {
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Present(Officer);

            Assert.That(r.Post(Officer, MuteUpdate(Other, "voice", true)), Is.EqualTo((int)HttpStatusCode.NotFound));

            Assert.That(r.Sessions.Get(GroupId).ModerationGeneration, Is.EqualTo(0));
            Assert.That(r.Voice.Mutes, Is.Empty);
        }

        [Test]
        public void AMuteAllThatFailsForOneMember_LeavesNoMemberMutedInTheRoom()
        {
            using Region r = Region.Create(Moderation);
            foreach (UUID a in new[] { Officer, Member, Other })
            {
                r.Call(a);
                r.Present(a);
            }
            r.Voice.FailFor = Other;

            Assert.That(r.Post(Officer, SessionUpdate(true)), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));

            Assert.That(r.Sessions.Get(GroupId).VoiceModerated, Is.False);
            foreach (UUID a in new[] { Officer, Member, Other })
                Assert.That(r.Voice.Mutes.Last(m => m.Agent == a).Muted, Is.False, $"{a}: the room was told the earlier state again");
        }

        // ---- a voice mute that cannot be heard ---------------------------------------------------------------

        [Test]
        public void OnTheStockPluginWithNoParticipantMute_AVoiceMuteIsRefusedAnd501_ButTextMuteWorks()
        {
            using Region r = Region.Create(Moderation);
            r.Scene.UnregisterModuleInterface<IWebRtcVoiceService>(r.Voice);
            r.Scene.RegisterModuleInterface<IWebRtcVoiceService>(new PlainVoice());
            r.Call(Officer);
            r.Call(Member);

            Assert.That(r.Post(Officer, MuteUpdate(Member, "voice", true)), Is.EqualTo((int)HttpStatusCode.NotImplemented));
            Assert.That(r.Post(Officer, SessionUpdate(true)), Is.EqualTo((int)HttpStatusCode.NotImplemented));
            Assert.That(r.Sessions.Get(GroupId).ModerationGeneration, Is.EqualTo(0), "nothing recorded");
            Assert.That(r.Post(Officer, MuteUpdate(Member, "text", true)), Is.EqualTo((int)HttpStatusCode.OK));
        }

        [Test]
        public void OnTheMixerWithNoAdminEndpoint_AVoiceMuteIsRefusedAnd501()
        {
            using Region r = Region.Create(Moderation, j => j.Set("PluginName", "janus.plugin.slvoice"));
            r.Call(Officer);
            r.Call(Member);

            Assert.That(r.Post(Officer, MuteUpdate(Member, "voice", true)), Is.EqualTo((int)HttpStatusCode.NotImplemented));
            Assert.That(r.Sessions.Get(GroupId).ModerationGeneration, Is.EqualTo(0));
        }

        [Test]
        public void AnAnswerOfFailedFromTheVoiceServerTakesTheMuteBackOut()
        {
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);
            r.Voice.Answer = ParticipantMuteResult.Failed;

            Assert.That(r.Post(Officer, MuteUpdate(Member, "voice", true)), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
            Assert.That(r.Sessions.Get(GroupId).IsVoiceMuted(Member), Is.False);
        }

        private sealed class PlainVoice : IWebRtcVoiceService
        {
            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;
        }
    }
}
