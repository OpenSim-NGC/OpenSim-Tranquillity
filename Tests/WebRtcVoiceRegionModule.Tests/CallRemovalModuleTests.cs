/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Removing a member from a group call through the region module: the media is dropped, the viewer is told to close
 * the call, the way back is refused (provision, call, accept invitation), the refusal is cheap for a viewer that
 * keeps retrying, and the end of the call ends the removal. The power-loss tick lets a member go whose powers went.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CallModerationModuleTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class CallRemovalModuleTests
    {
        internal static readonly UUID GroupId = new UUID("5e0c9a41-7b2d-4f86-a3c1-92d84b6e0f17");
        internal static readonly UUID Officer = new UUID("11111111-1111-4111-8111-111111111111");
        internal static readonly UUID Member = new UUID("22222222-2222-4222-8222-222222222222");
        private static readonly UUID Other = new UUID("33333333-3333-4333-8333-333333333333");

        private static void Moderation(Nini.Config.IConfig v) => v.Set("CallModerationEnabled", "true");

        internal sealed class ProvisionPost : IOSHttpRequest
        {
            public ProvisionPost(OSDMap body)
            {
                InputStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(body)));
            }
            public string HttpMethod => "POST";
            public Uri Url => new Uri("http://sim.example.org/provision");
            public string RawUrl => "/provision";
            public string UriPath => "/provision";
            public Stream InputStream { get; set; }
            public System.Collections.Specialized.NameValueCollection Headers { get; } = new();
            public bool HasEntityBody => true;
            public long ContentLength => InputStream.Length;
            public long ContentLength64 => InputStream.Length;
            public string ContentType => "application/llsd+xml";
            public string[] AcceptTypes => Array.Empty<string>();
            public System.Text.Encoding ContentEncoding => System.Text.Encoding.UTF8;
            public bool IsSecured => false;
            public bool KeepAlive => false;
            public System.Collections.Specialized.NameValueCollection QueryString => throw new NotImplementedException();
            public System.Collections.Hashtable Query => throw new NotImplementedException();
            public HashSet<string> QueryFlags => throw new NotImplementedException();
            public Dictionary<string, string> QueryAsDictionary => throw new NotImplementedException();
            public IPEndPoint RemoteIPEndPoint => new(IPAddress.Parse("192.0.2.1"), 1);
            public IPEndPoint LocalIPEndPoint => new(IPAddress.Parse("192.0.2.2"), 2);
            public string UserAgent => "test";
            public double ArrivalTS => 0;
        }

        /// <summary>The provision a viewer posts for a call it has been given: the channel, its credentials and an offer.</summary>
        internal static int Provision(Region r, UUID agent, out TestOSHttpResponse response)
        {
            NonSpatialVoiceSession s = r.Sessions.Get(GroupId, agent);
            OSDMap body = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["channel"] = OSD.FromString(s.RoomKey),
                ["credentials"] = OSD.FromString(r.Sessions.IssueToken(GroupId, agent)),
                ["voice_server_type"] = OSD.FromString("webrtc"),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
            };
            response = new TestOSHttpResponse();
            r.Module.ProvisionVoiceAccountRequest(new ProvisionPost(body), response, agent, r.Scene);
            return response.StatusCode;
        }

        /// <summary>The module in a call of two: an officer and a member, both with a seat and a participant.</summary>
        internal static Region InACall(Action<Nini.Config.IConfig> extra = null)
        {
            Region r = Region.Create(v =>
            {
                Moderation(v);
                extra?.Invoke(v);
            });
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);
            return r;
        }

        private static int RoomOf(Region r)
            => (int)typeof(WebRtcVoiceRegionModule).GetMethod("CallRoomNumber", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(r.Module, new object[] { r.Sessions.Get(GroupId) });

        // ---- the removal ------------------------------------------------------------------------------------

        [Test]
        public void ARemoval_DropsTheMembersMedia_TellsTheirViewerToClose_AndTheOthersThatTheyLeft()
        {
            using Region r = InACall();
            r.Events.Lists.Clear();

            WebRtcVoiceRegionModule.CallRemovalResult result = r.Module.RemoveFromCall(GroupId, Officer, Member, "disruptive");

            Assert.That(result.Ok, Is.True, result.Decision);
            Assert.That(result.Changed, Is.True);
            Assert.That(result.Dropped, Is.EqualTo(1));
            Assert.That(r.Voice.Drops, Is.EqualTo(new[] { (Member, RoomOf(r)) }), "the member's participants, in the call's room, and nobody else's");
            Assert.That(r.Events.Closes, Is.EqualTo(new[] { (Member, GroupId, "close_on_no_ability") }));
            var told = r.Events.Lists.Where(l => l.To == Officer).SelectMany(l => l.Updates).ToList();
            Assert.That(told.Any(u => u.agentID == Member && !u.enterOrLeave), Is.True, "the officer sees the member leave");
            NonSpatialVoiceSession s = r.Sessions.Get(GroupId);
            Assert.That(s.Find(Member).HoldsSeat, Is.False);
            Assert.That(s.IsRemoved(Member, DateTime.UtcNow), Is.True);
            Assert.That(s.Find(Officer).HoldsSeat, Is.True, "the rest of the call is untouched");
        }

        [Test]
        public void ARemovalTwice_DropsAndTellsOnce()
        {
            using Region r = InACall();

            r.Module.RemoveFromCall(GroupId, Officer, Member, "a");
            WebRtcVoiceRegionModule.CallRemovalResult again = r.Module.RemoveFromCall(GroupId, Officer, Member, "b");

            Assert.That(again.Ok, Is.True);
            Assert.That(again.Changed, Is.False);
            Assert.That(r.Voice.Drops.Count, Is.EqualTo(1));
            Assert.That(r.Events.Closes.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheRemovalIsRefusedWhenTheFeatureIsOff_AndForACallThatIsNotThere()
        {
            using Region off = Region.Create();
            Assert.That(off.Module.RemoveFromCall(GroupId, Officer, Member, "x").Decision, Is.EqualTo("call-moderation-off"));

            using Region on = InACall();
            Assert.That(on.Module.RemoveFromCall(UUID.Random(), Officer, Member, "x").Ok, Is.False);
            Assert.That(on.Voice.Drops, Is.Empty);
            Assert.That(on.Events.Closes, Is.Empty);
        }

        // ---- the way back is closed ---------------------------------------------------------------------------

        [Test]
        public void ARemovedMemberIsRefusedOnProvision_Call_AndAcceptInvitation()
        {
            using Region r = InACall();
            Assert.That(Provision(r, Member, out _), Is.Not.EqualTo((int)HttpStatusCode.Forbidden), "precondition: admitted before the removal");
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");

            Assert.That(Provision(r, Member, out _), Is.EqualTo((int)HttpStatusCode.Forbidden), "provision");
            Assert.That(r.Call(Member), Is.EqualTo((int)HttpStatusCode.Forbidden), "call");
            int accept = r.Post(Member, new OSDMap { ["method"] = OSD.FromString("accept invitation"), ["session-id"] = OSD.FromUUID(GroupId) });
            Assert.That(accept, Is.EqualTo((int)HttpStatusCode.Forbidden), "accept invitation");
            Assert.That(r.Sessions.Get(GroupId).Find(Member).HoldsSeat, Is.False);
            Assert.That(r.Call(Officer), Is.EqualTo((int)HttpStatusCode.OK), "everyone else is unaffected");
        }

        [Test]
        public void AProvisionRefusedBecauseOfARemoval_NamesTheRemovalOnTheDecisionWord()
        {
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            NonSpatialVoiceSession s = r.Sessions.Get(GroupId);
            OSDMap body = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["channel"] = OSD.FromString(s.RoomKey),
                ["credentials"] = OSD.FromString("anything"),
            };

            NonSpatialProvisionResult g = NonSpatialProvisionAdmission.Decide(body, Member, new GroupVoicePolicy { Enabled = true, IsMember = (a, gr) => true, Powers = (a, gr) => ulong.MaxValue },
                                                                              r.Sessions, UUID.Zero);

            Assert.That(g.Admitted, Is.False);
            Assert.That(g.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionRemoved));
            Assert.That(g.Status, Is.EqualTo(403));
        }

        // ---- a conference ---------------------------------------------------------------------------------------

        private static Region InAConference(out UUID conference)
        {
            Region r = Region.Create(v =>
            {
                Moderation(v);
                v.Set("AdhocVoiceEnabled", "true");
            });
            UUID home = r.Scene.RegionInfo.RegionID;
            SessionOutcome started = r.Sessions.StartAdhoc(Officer, UUID.Random(), home, new[] { Member, Other });
            conference = started.Session.SessionId;
            r.Sessions.Accept(conference, Member, home, "vs-member");
            r.Sessions.MarkPresent(conference, Member, home, "vs-member");
            return r;
        }

        [Test]
        public void ARemovalFromAConference_DropsTheMedia_LeavesTheText_AndShutsEveryWayBack()
        {
            using Region r = InAConference(out UUID id);
            r.Events.Lists.Clear();

            WebRtcVoiceRegionModule.CallRemovalResult result = r.Module.RemoveFromCall(id, Officer, Member, "x");

            Assert.That(result.Ok && result.Changed, Is.True, result.Decision);
            Assert.That(r.Events.Closes, Is.EqualTo(new[] { (Member, id, "close_on_no_ability") }), "told with the conference's own id");
            Assert.That(r.Voice.Drops.Select(d => d.Agent), Is.EqualTo(new[] { Member }));
            NonSpatialVoiceSession s = r.Sessions.Get(id);
            Assert.That(s.ChatMembers(), Does.Not.Contain(Member), "removed from the conference's text with the seat");
            Assert.That(s.ChatMembers(), Does.Contain(Officer));
            Assert.That(r.Events.Lists.Any(l => l.To == Officer && l.Updates.Any(u => u.agentID == Member && !u.enterOrLeave)), Is.True,
                        "the rest of the conference sees them leave");

            OSDMap accept = new OSDMap { ["method"] = OSD.FromString("accept invitation"), ["session-id"] = OSD.FromUUID(id) };
            Assert.That(r.Post(Member, accept), Is.EqualTo((int)HttpStatusCode.Forbidden), "accept invitation");

            OSDMap invite = new OSDMap
            {
                ["method"] = OSD.FromString("invite"),
                ["session-id"] = OSD.FromUUID(id),
                ["params"] = new OSDArray { OSD.FromUUID(Member) },
            };
            Assert.That(r.Post(Officer, invite), Is.EqualTo((int)HttpStatusCode.OK), "the invite is answered");
            Assert.That(r.Sessions.Get(id).Find(Member).State, Is.EqualTo(MemberState.Departed), "and does not ring the removed member back in");

            OSDMap body = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["channel"] = OSD.FromString(s.RoomKey),
                ["credentials"] = OSD.FromString(r.Sessions.IssueToken(id, Member)),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
            };
            TestOSHttpResponse response = new TestOSHttpResponse();
            r.Module.ProvisionVoiceAccountRequest(new ProvisionPost(body), response, Member, r.Scene);
            Assert.That(response.StatusCode, Is.EqualTo((int)HttpStatusCode.Forbidden), "provision");

            Assert.That(r.Post(Other, accept), Is.EqualTo((int)HttpStatusCode.OK), "another invitee is unaffected");
        }

        [Test]
        public void TheCreatorOfAConferenceCanBeRemovedToo_AndIsNotSeatedAgainByTheirOwnAccept()
        {
            using Region r = InAConference(out UUID id);

            r.Module.RemoveFromCall(id, Member, Officer, "x");

            Assert.That(r.Sessions.Get(id).Find(Officer).HoldsSeat, Is.False);
            Assert.That(r.Sessions.Accept(id, Officer, r.Scene.RegionInfo.RegionID).Decision, Is.EqualTo(SessionOutcome.Removed));
        }

        // ---- the viewer that keeps asking --------------------------------------------------------------------

        public class CountingSessions : DispatchProxy
        {
            public INonSpatialSessions Inner;
            public readonly Dictionary<string, int> Calls = new();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                lock (Calls) Calls[method.Name] = Calls.GetValueOrDefault(method.Name) + 1;
                try { return method.Invoke(Inner, args); }
                catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            }
        }

        [Test]
        public void AViewerThatKeepsRetryingAfterARemoval_IsRefusedFromTheCache_NotTheStore()
        {
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            CountingSessions counting = (CountingSessions)(object)DispatchProxy.Create<INonSpatialSessions, CountingSessions>();
            counting.Inner = r.Sessions;
            typeof(WebRtcVoiceRegionModule).GetField("m_nonSpatial", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(r.Module, (INonSpatialSessions)(object)counting);

            const int Retries = 300;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < Retries; i++)
                Assert.That(Provision(r, Member, out _), Is.EqualTo((int)HttpStatusCode.Forbidden), $"retry {i}");
            clock.Stop();

            TestContext.Out.WriteLine($"{Retries} refused provisions in {clock.ElapsedMilliseconds} ms "
                + $"({Retries * 1000.0 / Math.Max(1, clock.ElapsedMilliseconds):F0} per second); store lookups by room key: "
                + counting.Calls.GetValueOrDefault(nameof(INonSpatialSessions.GetByRoomKey)));
            // The test's own Provision() reads the call's record once per retry to build the body; what the module itself
            // asks the store for by room key is what the cache saves.
            Assert.That(counting.Calls.GetValueOrDefault(nameof(INonSpatialSessions.GetByRoomKey)), Is.EqualTo(1),
                        "the first retry asked; the other 299 were answered from the cache");
        }

        [Test]
        public void AViewerThatHonoursTheForcedClose_StopsAfterOneMorePost()
        {
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            int posts = 0;

            // The bot posts until the forced close it was sent has been acted on. It acts on it at its first chance,
            // which is after the post that was already in flight when the removal landed.
            bool closed = false;
            while (!closed && posts < 50)
            {
                Provision(r, Member, out _);
                posts++;
                closed = r.Events.Closes.Any(c => c.To == Member && c.Session == GroupId);
            }

            Assert.That(posts, Is.EqualTo(1));
            Assert.That(r.Events.Closes.Count, Is.EqualTo(1), "and was sent once, not once per retry");
        }

        // ---- the end of the removal -------------------------------------------------------------------------

        [Test]
        public void TheEndOfTheCallEndsTheRemoval()
        {
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            Assert.That(r.Call(Member), Is.EqualTo((int)HttpStatusCode.Forbidden), "during the call");

            r.Sessions.Depart(GroupId, Officer, DepartureReason.VoiceTeardown);   // the last seat: the call ends

            Assert.That(r.Call(Member), Is.EqualTo((int)HttpStatusCode.OK), "a new call");
            Assert.That(r.Sessions.Get(GroupId).Removed, Is.Empty);
        }

        [Test]
        public void ReadmittingLiftsTheRefusalAtOnce_EvenForAViewerWhoseRefusalWasCached()
        {
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            Assert.That(Provision(r, Member, out _), Is.EqualTo((int)HttpStatusCode.Forbidden));

            WebRtcVoiceRegionModule.CallRemovalResult back = r.Module.ReadmitToCall(GroupId, Officer, Member);

            Assert.That(back.Ok && back.Changed, Is.True);
            Assert.That(r.Call(Member), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(Provision(r, Member, out _), Is.Not.EqualTo((int)HttpStatusCode.Forbidden), "the cached refusal went with the removal");
        }

        [Test]
        public void ARemovalLastsAsLongAsTheKeySays_AndTheCallWhenItIsNotSet()
        {
            using Region plain = InACall();
            plain.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            Assert.That(plain.Sessions.Get(GroupId).RemovalOf(Member).ExpiresUtc, Is.Null, "default: until the call ends");

            using Region keyed = InACall(v => v.Set("CallRemovalMinutes", "90"));
            DateTime before = DateTime.UtcNow;
            keyed.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            DateTime? ends = keyed.Sessions.Get(GroupId).RemovalOf(Member).ExpiresUtc;
            Assert.That(ends, Is.Not.Null);
            Assert.That(ends.Value - before, Is.InRange(TimeSpan.FromMinutes(89), TimeSpan.FromMinutes(91)));
        }

        [Test]
        public void TheRemovalKey_DefaultsToUntilTheCallEnds_AndIsBounded()
        {
            Nini.Config.IConfig cfg = new Nini.Config.IniConfigSource().AddConfig("WebRtcVoice");
            Assert.That(CallModerationSettings.Read(cfg).RemovalMinutes, Is.EqualTo(0));
            cfg.Set("CallRemovalMinutes", "120");
            Assert.That(CallModerationSettings.Read(cfg).RemovalMinutes, Is.EqualTo(120));
            cfg.Set("CallRemovalMinutes", "-5");
            Assert.That(CallModerationSettings.Read(cfg).RemovalMinutes, Is.EqualTo(0));
            cfg.Set("CallRemovalMinutes", "999999999");
            Assert.That(CallModerationSettings.Read(cfg).RemovalMinutes, Is.EqualTo(VoiceSessionService.MaxRemovalMinutes));
        }

        // ---- the group's text ------------------------------------------------------------------------------------

        [Test]
        public void AGroupRemovalLeavesTheGroupsText()
        {
            using Region r = InACall();
            IGroupChatGate gate = r.Scene.RequestModuleInterface<IGroupChatGate>();

            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");

            Assert.That(gate.MaySend(GroupId, Member), Is.True, "the removed member's group chat is not muted");
            Assert.That(r.Sessions.Get(GroupId).Find(Member).InChat, Is.True);
        }

        // ---- a removal made elsewhere -----------------------------------------------------------------------

        [Test]
        public void ASeatTheServiceSaysWasRemovedElsewhere_IsTornDownHere()
        {
            using Region r = InACall();
            r.Voice.Drops.Clear();

            typeof(WebRtcVoiceRegionModule).GetMethod("OnGridSeatRemoved", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(r.Module, new object[] { new SeatKey(GroupId, Member) });

            Assert.That(r.Voice.Drops, Is.EqualTo(new[] { (Member, RoomOf(r)) }));
            Assert.That(r.Events.Closes, Is.EqualTo(new[] { (Member, GroupId, "close_on_no_ability") }));
        }

        // ---- the moderator's power and the call's powers --------------------------------------------------------

        private static void Tick(Region r)
            => typeof(WebRtcVoiceRegionModule).GetMethod("RevalidateCallFlags", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(r.Module, null);

        [Test]
        public void ALostModeratorPower_WithdrawsTheFlagOnTheTick_AndFurtherModerationIsRefused()
        {
            using Region r = InACall();
            Assert.That(r.Post(Officer, MuteRequest(Member, true)), Is.EqualTo((int)HttpStatusCode.OK), "precondition: a moderator can mute");
            r.Events.Lists.Clear();

            FakeGroups.Powers[Officer] = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;   // still in the call, no longer a moderator
            Tick(r);

            var flags = r.Events.Lists.Where(l => l.To == Officer).SelectMany(l => l.Updates).Where(u => u.agentID == Officer).ToList();
            Assert.That(flags.Count, Is.EqualTo(1));
            Assert.That(flags[0].isModerator, Is.False, "the flag is withdrawn");
            Assert.That(flags[0].canVoice, Is.True);
            Assert.That(r.Post(Officer, MuteRequest(Member, false)), Is.EqualTo((int)HttpStatusCode.Forbidden), "and the next request is refused");
            Assert.That(r.Sessions.Get(GroupId).Find(Officer).HoldsSeat, Is.True, "the officer stays in the call");
            Assert.That(r.Voice.Drops, Is.Empty);

            r.Events.Lists.Clear();
            Tick(r);
            Assert.That(r.Events.Lists, Is.Empty, "a stable call generates no traffic");
        }

        [Test]
        public void AMemberWhoLosesThePowerToBeInTheCall_IsLetGoOnTheTick_WithNoRemovalRecorded()
        {
            using Region r = InACall();
            FakeGroups.Powers[Member] = 0;

            Tick(r);

            NonSpatialVoiceSession s = r.Sessions.Get(GroupId);
            Assert.That(s.Find(Member).HoldsSeat, Is.False);
            Assert.That(s.Find(Member).Departure, Is.EqualTo(DepartureReason.PowerLost));
            Assert.That(r.Voice.Drops, Is.EqualTo(new[] { (Member, RoomOf(r)) }));
            Assert.That(r.Events.Closes, Is.EqualTo(new[] { (Member, GroupId, "close_on_no_ability") }));
            Assert.That(s.Removed, Is.Empty, "no removal: the powers decide, and they can come back");
            Assert.That(s.Find(Officer).HoldsSeat, Is.True);

            FakeGroups.Powers[Member] = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;
            Assert.That(r.Call(Member), Is.EqualTo((int)HttpStatusCode.OK), "back with the powers");
        }

        [Test]
        public void AMemberWhoIsNoLongerInTheGroup_IsLetGoOnTheTick_WithNoRemovalRecorded()
        {
            using Region r = InACall();
            FakeGroups.Powers.Remove(Member);   // the groups service no longer knows them as a member (a ban and eject, say)

            Tick(r);

            NonSpatialVoiceSession s = r.Sessions.Get(GroupId);
            Assert.That(s.Find(Member).HoldsSeat, Is.False);
            Assert.That(s.Find(Member).Departure, Is.EqualTo(DepartureReason.PowerLost));
            Assert.That(r.Voice.Drops, Is.EqualTo(new[] { (Member, RoomOf(r)) }));
            Assert.That(r.Events.Closes, Is.EqualTo(new[] { (Member, GroupId, "close_on_no_ability") }));
            Assert.That(s.Removed, Is.Empty);
            Assert.That(r.Call(Member), Is.Not.EqualTo((int)HttpStatusCode.OK), "and a new call is not answered: they are not a member");
        }

        private static OSDMap MuteRequest(UUID target, bool muted) => new OSDMap
        {
            ["method"] = OSD.FromString("mute update"),
            ["session-id"] = OSD.FromUUID(GroupId),
            ["params"] = new OSDMap
            {
                ["agent_id"] = OSD.FromUUID(target),
                ["mute_info"] = new OSDMap { ["voice"] = OSD.FromBoolean(muted) },
            },
        };
    }
}
