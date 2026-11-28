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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void WithTheKeyOff_EveryModerationRequestIsAnswered400_AsItWasBeforeTheFeature()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

                    {
                        var assertedValue = r.Post(Officer, MuteUpdate(Member, "voice", true));
                        TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.BadRequest, assertedValue), $"a2a={a2a}");
                    }
                    Assert.Equal((int)HttpStatusCode.BadRequest, r.Post(Officer, MuteUpdate(Member, "text", true)));
                    Assert.Equal((int)HttpStatusCode.BadRequest, r.Post(Officer, SessionUpdate(true)));
                    {
                        var assertedValue = r.Sessions.Get(GroupId).ModerationGeneration;
                        TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "nothing was recorded");
                    }
                    {
                        var assertedValue = r.Scene.RequestModuleInterface<IGroupChatGate>();
                        TestAssert.WithMessage(() => Assert.Null(assertedValue), "no group chat check is offered");
                    }
                    Assert.Empty(r.Events.Modes);
                    Assert.Empty(r.Voice.Mutes);
                }
        }

        [Fact(Timeout = 60000)]
        public void TheKeyOnWithNoCallFeature_StaysOff()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(v =>
            {
                v.Set("GroupVoiceEnabled", "false");
                Moderation(v);
            });

            Assert.Equal((int)HttpStatusCode.BadRequest, r.Post(Officer, MuteUpdate(Member, "voice", true)));
            Assert.Null(r.Scene.RequestModuleInterface<IGroupChatGate>());
        }

        // ---- grid mode: a member seated through another region server --------------------------------------------

        [Fact(Timeout = 60000)]
        public void InGridMode_AMuteOfAMemberSeatedThroughAnotherRegionServer_NamesThatAgentAndNoOtherAgent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            Assert.Equal((int)HttpStatusCode.OK, status);
            {
                var assertedValue = r.Voice.Mutes.Where(m => m.Muted).Select(m => m.Agent);
                TestAssert.WithMessage(() => Assert.Equal(new[] { Member }, assertedValue), "the mute names the agent id the other server set, and no one else");
            }
            Assert.True(there.Get(GroupId).IsVoiceMuted(Member), "and the other region server sees the record");
            Assert.False(there.Get(GroupId).IsVoiceMuted(Other));
        }

        // ---- the roster -----------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AModeratorsOwnEntryCarriesTheModeratorFlag_AndAPlainMembersDoesNot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);

            Assert.Equal((int)HttpStatusCode.OK, r.Call(Officer));
            Assert.Equal((int)HttpStatusCode.OK, r.Call(Member));

            var officer = r.Events.Lists.Single(l => l.To == Officer).Updates.Single();
            var member = r.Events.Lists.Single(l => l.To == Member).Updates.Single();
            Assert.Equal(Officer, officer.agentID);
            Assert.True(officer.isModerator);
            Assert.True(officer.canVoice, "false is read by the viewer as a refusal");
            Assert.False(member.isModerator);
            Assert.True(member.canVoice);
        }

        [Fact(Timeout = 60000)]
        public void WithTheKeyOff_NoModeratorEntryIsSent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create();

            r.Call(Officer);

            Assert.Empty(r.Events.Lists);
        }

        [Fact(Timeout = 60000)]
        public void ALateJoinerToAMutedCallIsToldTheCallIsModerated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Post(Officer, SessionUpdate(true));
            r.Events.Modes.Clear();

            r.Call(Member);

            Assert.Contains<(UUID To, UUID Session, bool Moderated)>(r.Events.Modes, m => m.To == Member && m.Moderated);
        }

        // ---- requests, through the module --------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheOfficerMutesAMember_TheVoiceServerIsAskedAndThePlainMemberIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);

            int refused = r.Post(Member, MuteUpdate(Other, "voice", true));
            int ok = r.Post(Officer, MuteUpdate(Member, "voice", true));

            Assert.Equal((int)HttpStatusCode.Forbidden, refused);
            Assert.Equal((int)HttpStatusCode.OK, ok);
            Assert.True(r.Sessions.Get(GroupId).IsVoiceMuted(Member));
            Assert.Contains<(UUID Agent, int Room, bool Muted)>(r.Voice.Mutes, m => m.Agent == Member && m.Muted);
            {
                var assertedValue = r.Voice.Mutes;
                TestAssert.WithMessage(() => Assert.DoesNotContain<(UUID Agent, int Room, bool Muted)>(assertedValue, m => m.Agent == Officer && m.Muted), "a moderator is not muted");
            }
        }

        [Fact(Timeout = 60000)]
        public void MuteAll_TellsTheCallAndMutesEveryoneButTheModerator_AndALaterJoinerIsMuted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);

            Assert.Equal((int)HttpStatusCode.OK, r.Post(Officer, SessionUpdate(true)));

            Assert.All(new[] { Officer, Member }, item => Assert.Contains(item, r.Events.Modes.Where(m => m.Moderated).Select(m => m.To)));
            {
                var assertedValue = r.Voice.Mutes.Where(m => m.Muted).Select(m => m.Agent);
                Assert.Contains(Member, assertedValue);
                Assert.DoesNotContain(Officer, assertedValue);
            }

            r.Call(Other);
            r.Present(Other);
            r.Voice.Mutes.Clear();
            typeof(WebRtcVoiceRegionModule).GetMethod("SyncCallVoiceOnJoin", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(r.Module, new object[] { Other, r.Sessions.Get(GroupId) });
            {
                var assertedValue = r.Voice.Mutes;
                TestAssert.WithMessage(() => Assert.Contains<(UUID Agent, int Room, bool Muted)>(assertedValue, m => m.Agent == Other && m.Muted), "whoever joins afterwards is silenced in the room");
            }

            Assert.Equal((int)HttpStatusCode.OK, r.Post(Officer, SessionUpdate(false)));
            Assert.False(r.Events.Modes.Last().Moderated);
            Assert.False(r.Voice.Mutes.Last().Muted);
        }

        [Fact(Timeout = 60000)]
        public void ATextMuteDropsTheMembersGroupChat_AndTheirOwnEntryShowsIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            IGroupChatGate gate = r.Scene.RequestModuleInterface<IGroupChatGate>();
            Assert.NotNull(gate);
            Assert.True(gate.MaySend(GroupId, Member));

            Assert.Equal((int)HttpStatusCode.OK, r.Post(Officer, MuteUpdate(Member, "text", true)));

            Assert.False(gate.MaySend(GroupId, Member));
            Assert.True(gate.MaySend(GroupId, Officer));
            Assert.True(gate.MaySend(UUID.Random(), Member), "another group's chat is not touched");
            var told = r.Events.Lists.Last(l => l.To == Member && l.Updates.Any(u => u.mutedText));
            Assert.Equal(Member, told.Updates.Single().agentID);

            Assert.Equal((int)HttpStatusCode.OK, r.Post(Officer, MuteUpdate(Member, "text", false)));
            Assert.True(gate.MaySend(GroupId, Member), "unmuting restores it at once");
        }

        // ---- who a voice mute can name -------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AMuteNamingAnAgentTheRoomHasNoParticipantFor_IsRefused404_AndNothingIsRecordedOrSent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);
            r.Voice.NoParticipant.Add(Member);

            Assert.Equal((int)HttpStatusCode.NotFound, r.Post(Officer, MuteUpdate(Member, "voice", true)));

            Assert.False(r.Sessions.Get(GroupId).IsVoiceMuted(Member));
            {
                var assertedValue = r.Sessions.Get(GroupId).ModerationGeneration;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "nothing recorded");
            }
            {
                var assertedValue = r.Voice.Mutes;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "and no mute went to the room");
            }
        }

        [Fact(Timeout = 60000)]
        public void AMuteNamingAnAgentThatIsNotInTheCall_IsRefused404()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Present(Officer);

            Assert.Equal((int)HttpStatusCode.NotFound, r.Post(Officer, MuteUpdate(Other, "voice", true)));

            Assert.Equal(0, r.Sessions.Get(GroupId).ModerationGeneration);
            Assert.Empty(r.Voice.Mutes);
        }

        [Fact(Timeout = 60000)]
        public void AMuteAllThatFailsForOneMember_LeavesNoMemberMutedInTheRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            foreach (UUID a in new[] { Officer, Member, Other })
            {
                r.Call(a);
                r.Present(a);
            }
            r.Voice.FailFor = Other;

            Assert.Equal((int)HttpStatusCode.ServiceUnavailable, r.Post(Officer, SessionUpdate(true)));

            Assert.False(r.Sessions.Get(GroupId).VoiceModerated);
            foreach (UUID a in new[] { Officer, Member, Other })
                Assert.False(r.Voice.Mutes.Last(m => m.Agent == a).Muted, $"{a}: the room was told the earlier state again");
        }

        // ---- a voice mute that cannot be heard ---------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void OnTheStockPluginWithNoParticipantMute_AVoiceMuteIsRefusedAnd501_ButTextMuteWorks()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            r.Scene.UnregisterModuleInterface<IWebRtcVoiceService>(r.Voice);
            r.Scene.RegisterModuleInterface<IWebRtcVoiceService>(new PlainVoice());
            r.Call(Officer);
            r.Call(Member);

            Assert.Equal((int)HttpStatusCode.NotImplemented, r.Post(Officer, MuteUpdate(Member, "voice", true)));
            Assert.Equal((int)HttpStatusCode.NotImplemented, r.Post(Officer, SessionUpdate(true)));
            {
                var assertedValue = r.Sessions.Get(GroupId).ModerationGeneration;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "nothing recorded");
            }
            Assert.Equal((int)HttpStatusCode.OK, r.Post(Officer, MuteUpdate(Member, "text", true)));
        }

        [Fact(Timeout = 60000)]
        public void OnTheMixerWithNoAdminEndpoint_AVoiceMuteIsRefusedAnd501()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation, j => j.Set("PluginName", "janus.plugin.slvoice"));
            r.Call(Officer);
            r.Call(Member);

            Assert.Equal((int)HttpStatusCode.NotImplemented, r.Post(Officer, MuteUpdate(Member, "voice", true)));
            Assert.Equal(0, r.Sessions.Get(GroupId).ModerationGeneration);
        }

        [Fact(Timeout = 60000)]
        public void AnAnswerOfFailedFromTheVoiceServerTakesTheMuteBackOut()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = Region.Create(Moderation);
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);
            r.Voice.Answer = ParticipantMuteResult.Failed;

            Assert.Equal((int)HttpStatusCode.ServiceUnavailable, r.Post(Officer, MuteUpdate(Member, "voice", true)));
            Assert.False(r.Sessions.Get(GroupId).IsVoiceMuted(Member));
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
