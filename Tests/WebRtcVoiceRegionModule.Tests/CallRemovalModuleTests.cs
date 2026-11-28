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
using Xunit;
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

        [Fact(Timeout = 60000)]
        public void ARemoval_DropsTheMembersMedia_TellsTheirViewerToClose_AndTheOthersThatTheyLeft()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            r.Events.Lists.Clear();

            WebRtcVoiceRegionModule.CallRemovalResult result = r.Module.RemoveFromCall(GroupId, Officer, Member, "disruptive");

            Assert.True(result.Ok, result.Decision);
            Assert.True(result.Changed);
            Assert.Equal(1, result.Dropped);
            {
                var assertedValue = r.Voice.Drops;
                TestAssert.WithMessage(() => Assert.Equal(new[] { (Member, RoomOf(r)) }, assertedValue), "the member's participants, in the call's room, and nobody else's");
            }
            Assert.Equal(new[] { (Member, GroupId, "close_on_no_ability") }, r.Events.Closes);
            var told = r.Events.Lists.Where(l => l.To == Officer).SelectMany(l => l.Updates).ToList();
            Assert.True(told.Any(u => u.agentID == Member && !u.enterOrLeave), "the officer sees the member leave");
            NonSpatialVoiceSession s = r.Sessions.Get(GroupId);
            Assert.False(s.Find(Member).HoldsSeat);
            Assert.True(s.IsRemoved(Member, DateTime.UtcNow));
            Assert.True(s.Find(Officer).HoldsSeat, "the rest of the call is untouched");
        }

        [Fact(Timeout = 60000)]
        public void ARemovalTwice_DropsAndTellsOnce()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();

            r.Module.RemoveFromCall(GroupId, Officer, Member, "a");
            WebRtcVoiceRegionModule.CallRemovalResult again = r.Module.RemoveFromCall(GroupId, Officer, Member, "b");

            Assert.True(again.Ok);
            Assert.False(again.Changed);
            Assert.Single(r.Voice.Drops);
            Assert.Single(r.Events.Closes);
        }

        [Fact(Timeout = 60000)]
        public void TheRemovalIsRefusedWhenTheFeatureIsOff_AndForACallThatIsNotThere()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region off = Region.Create();
            Assert.Equal("call-moderation-off", off.Module.RemoveFromCall(GroupId, Officer, Member, "x").Decision);

            using Region on = InACall();
            Assert.False(on.Module.RemoveFromCall(UUID.Random(), Officer, Member, "x").Ok);
            Assert.Empty(on.Voice.Drops);
            Assert.Empty(on.Events.Closes);
        }

        // ---- the way back is closed ---------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ARemovedMemberIsRefusedOnProvision_Call_AndAcceptInvitation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            {
                var assertedValue = Provision(r, Member, out _);
                TestAssert.WithMessage(() => Assert.NotEqual((int)HttpStatusCode.Forbidden, assertedValue), "precondition: admitted before the removal");
            }
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");

            {
                var assertedValue = Provision(r, Member, out _);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.Forbidden, assertedValue), "provision");
            }
            {
                var assertedValue = r.Call(Member);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.Forbidden, assertedValue), "call");
            }
            int accept = r.Post(Member, new OSDMap { ["method"] = OSD.FromString("accept invitation"), ["session-id"] = OSD.FromUUID(GroupId) });
            {
                var assertedValue = accept;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.Forbidden, assertedValue), "accept invitation");
            }
            Assert.False(r.Sessions.Get(GroupId).Find(Member).HoldsSeat);
            {
                var assertedValue = r.Call(Officer);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "everyone else is unaffected");
            }
        }

        [Fact(Timeout = 60000)]
        public void AProvisionRefusedBecauseOfARemoval_NamesTheRemovalOnTheDecisionWord()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            Assert.False(g.Admitted);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionRemoved, g.Decision);
            Assert.Equal(403, g.Status);
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

        [Fact(Timeout = 60000)]
        public void ARemovalFromAConference_DropsTheMedia_LeavesTheText_AndShutsEveryWayBack()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InAConference(out UUID id);
            r.Events.Lists.Clear();

            WebRtcVoiceRegionModule.CallRemovalResult result = r.Module.RemoveFromCall(id, Officer, Member, "x");

            Assert.True((result.Ok && result.Changed), result.Decision);
            {
                var assertedValue = r.Events.Closes;
                TestAssert.WithMessage(() => Assert.Equal(new[] { (Member, id, "close_on_no_ability") }, assertedValue), "told with the conference's own id");
            }
            Assert.Equal(new[] { Member }, r.Voice.Drops.Select(d => d.Agent));
            NonSpatialVoiceSession s = r.Sessions.Get(id);
            {
                var assertedValue = s.ChatMembers();
                TestAssert.WithMessage(() => Assert.DoesNotContain(Member, assertedValue), "removed from the conference's text with the seat");
            }
            Assert.Contains(Officer, s.ChatMembers());
            Assert.True(r.Events.Lists.Any(l => l.To == Officer && l.Updates.Any(u => u.agentID == Member && !u.enterOrLeave)), "the rest of the conference sees them leave");

            OSDMap accept = new OSDMap { ["method"] = OSD.FromString("accept invitation"), ["session-id"] = OSD.FromUUID(id) };
            {
                var assertedValue = r.Post(Member, accept);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.Forbidden, assertedValue), "accept invitation");
            }

            OSDMap invite = new OSDMap
            {
                ["method"] = OSD.FromString("invite"),
                ["session-id"] = OSD.FromUUID(id),
                ["params"] = new OSDArray { OSD.FromUUID(Member) },
            };
            {
                var assertedValue = r.Post(Officer, invite);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "the invite is answered");
            }
            {
                var assertedValue = r.Sessions.Get(id).Find(Member).State;
                TestAssert.WithMessage(() => Assert.Equal(MemberState.Departed, assertedValue), "and does not ring the removed member back in");
            }

            OSDMap body = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["channel"] = OSD.FromString(s.RoomKey),
                ["credentials"] = OSD.FromString(r.Sessions.IssueToken(id, Member)),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
            };
            TestOSHttpResponse response = new TestOSHttpResponse();
            r.Module.ProvisionVoiceAccountRequest(new ProvisionPost(body), response, Member, r.Scene);
            {
                var assertedValue = response.StatusCode;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.Forbidden, assertedValue), "provision");
            }

            {
                var assertedValue = r.Post(Other, accept);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "another invitee is unaffected");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheCreatorOfAConferenceCanBeRemovedToo_AndIsNotSeatedAgainByTheirOwnAccept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InAConference(out UUID id);

            r.Module.RemoveFromCall(id, Member, Officer, "x");

            Assert.False(r.Sessions.Get(id).Find(Officer).HoldsSeat);
            Assert.Equal(SessionOutcome.Removed, r.Sessions.Accept(id, Officer, r.Scene.RegionInfo.RegionID).Decision);
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

        [Fact(Timeout = 60000)]
        public void AViewerThatKeepsRetryingAfterARemoval_IsRefusedFromTheCache_NotTheStore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            CountingSessions counting = (CountingSessions)(object)DispatchProxy.Create<INonSpatialSessions, CountingSessions>();
            counting.Inner = r.Sessions;
            typeof(WebRtcVoiceRegionModule).GetField("m_nonSpatial", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(r.Module, (INonSpatialSessions)(object)counting);

            const int Retries = 300;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < Retries; i++)
                {
                    var assertedValue = Provision(r, Member, out _);
                    TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.Forbidden, assertedValue), $"retry {i}");
                }
            clock.Stop();

            TestContext.Current.TestOutputHelper.WriteLine($"{Retries} refused provisions in {clock.ElapsedMilliseconds} ms "
                + $"({Retries * 1000.0 / Math.Max(1, clock.ElapsedMilliseconds):F0} per second); store lookups by room key: "
                + counting.Calls.GetValueOrDefault(nameof(INonSpatialSessions.GetByRoomKey)));
            // The test's own Provision() reads the call's record once per retry to build the body; what the module itself
            // asks the store for by room key is what the cache saves.
            {
                var assertedValue = counting.Calls.GetValueOrDefault(nameof(INonSpatialSessions.GetByRoomKey));
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the first retry asked; the other 299 were answered from the cache");
            }
        }

        [Fact(Timeout = 60000)]
        public void AViewerThatHonoursTheForcedClose_StopsAfterOneMorePost()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            Assert.Equal(1, posts);
            {
                var assertedValue = r.Events.Closes.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "and was sent once, not once per retry");
            }
        }

        // ---- the end of the removal -------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheEndOfTheCallEndsTheRemoval()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            {
                var assertedValue = r.Call(Member);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.Forbidden, assertedValue), "during the call");
            }

            r.Sessions.Depart(GroupId, Officer, DepartureReason.VoiceTeardown);   // the last seat: the call ends

            {
                var assertedValue = r.Call(Member);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "a new call");
            }
            Assert.Empty(r.Sessions.Get(GroupId).Removed);
        }

        [Fact(Timeout = 60000)]
        public void ReadmittingLiftsTheRefusalAtOnce_EvenForAViewerWhoseRefusalWasCached()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            Assert.Equal((int)HttpStatusCode.Forbidden, Provision(r, Member, out _));

            WebRtcVoiceRegionModule.CallRemovalResult back = r.Module.ReadmitToCall(GroupId, Officer, Member);

            Assert.True(back.Ok && back.Changed);
            Assert.Equal((int)HttpStatusCode.OK, r.Call(Member));
            {
                var assertedValue = Provision(r, Member, out _);
                TestAssert.WithMessage(() => Assert.NotEqual((int)HttpStatusCode.Forbidden, assertedValue), "the cached refusal went with the removal");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARemovalLastsAsLongAsTheKeySays_AndTheCallWhenItIsNotSet()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region plain = InACall();
            plain.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            {
                var assertedValue = plain.Sessions.Get(GroupId).RemovalOf(Member).ExpiresUtc;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "default: until the call ends");
            }

            using Region keyed = InACall(v => v.Set("CallRemovalMinutes", "90"));
            DateTime before = DateTime.UtcNow;
            keyed.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            DateTime? ends = keyed.Sessions.Get(GroupId).RemovalOf(Member).ExpiresUtc;
            Assert.NotNull(ends);
            Assert.InRange(ends.Value - before, TimeSpan.FromMinutes(89), TimeSpan.FromMinutes(91));
        }

        [Fact(Timeout = 60000)]
        public void TheRemovalKey_DefaultsToUntilTheCallEnds_AndIsBounded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Nini.Config.IConfig cfg = new Nini.Config.IniConfigSource().AddConfig("WebRtcVoice");
            Assert.Equal(0, CallModerationSettings.Read(cfg).RemovalMinutes);
            cfg.Set("CallRemovalMinutes", "120");
            Assert.Equal(120, CallModerationSettings.Read(cfg).RemovalMinutes);
            cfg.Set("CallRemovalMinutes", "-5");
            Assert.Equal(0, CallModerationSettings.Read(cfg).RemovalMinutes);
            cfg.Set("CallRemovalMinutes", "999999999");
            Assert.Equal(VoiceSessionService.MaxRemovalMinutes, CallModerationSettings.Read(cfg).RemovalMinutes);
        }

        // ---- the group's text ------------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AGroupRemovalLeavesTheGroupsText()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            IGroupChatGate gate = r.Scene.RequestModuleInterface<IGroupChatGate>();

            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");

            Assert.True(gate.MaySend(GroupId, Member), "the removed member's group chat is not muted");
            Assert.True(r.Sessions.Get(GroupId).Find(Member).InChat);
        }

        // ---- a removal made elsewhere -----------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ASeatTheServiceSaysWasRemovedElsewhere_IsTornDownHere()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            r.Voice.Drops.Clear();

            typeof(WebRtcVoiceRegionModule).GetMethod("OnGridSeatRemoved", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(r.Module, new object[] { new SeatKey(GroupId, Member) });

            Assert.Equal(new[] { (Member, RoomOf(r)) }, r.Voice.Drops);
            Assert.Equal(new[] { (Member, GroupId, "close_on_no_ability") }, r.Events.Closes);
        }

        // ---- the moderator's power and the call's powers --------------------------------------------------------

        private static void Tick(Region r)
            => typeof(WebRtcVoiceRegionModule).GetMethod("RevalidateCallFlags", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(r.Module, null);

        [Fact(Timeout = 60000)]
        public void ALostModeratorPower_WithdrawsTheFlagOnTheTick_AndFurtherModerationIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            {
                var assertedValue = r.Post(Officer, MuteRequest(Member, true));
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "precondition: a moderator can mute");
            }
            r.Events.Lists.Clear();

            FakeGroups.Powers[Officer] = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;   // still in the call, no longer a moderator
            Tick(r);

            var flags = r.Events.Lists.Where(l => l.To == Officer).SelectMany(l => l.Updates).Where(u => u.agentID == Officer).ToList();
            Assert.Single(flags);
            Assert.False(flags[0].isModerator, "the flag is withdrawn");
            Assert.True(flags[0].canVoice);
            {
                var assertedValue = r.Post(Officer, MuteRequest(Member, false));
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.Forbidden, assertedValue), "and the next request is refused");
            }
            Assert.True(r.Sessions.Get(GroupId).Find(Officer).HoldsSeat, "the officer stays in the call");
            Assert.Empty(r.Voice.Drops);

            r.Events.Lists.Clear();
            Tick(r);
            {
                var assertedValue = r.Events.Lists;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a stable call generates no traffic");
            }
        }

        [Fact(Timeout = 60000)]
        public void AMemberWhoLosesThePowerToBeInTheCall_IsLetGoOnTheTick_WithNoRemovalRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            FakeGroups.Powers[Member] = 0;

            Tick(r);

            NonSpatialVoiceSession s = r.Sessions.Get(GroupId);
            Assert.False(s.Find(Member).HoldsSeat);
            Assert.Equal(DepartureReason.PowerLost, s.Find(Member).Departure);
            Assert.Equal(new[] { (Member, RoomOf(r)) }, r.Voice.Drops);
            Assert.Equal(new[] { (Member, GroupId, "close_on_no_ability") }, r.Events.Closes);
            {
                var assertedValue = s.Removed;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no removal: the powers decide, and they can come back");
            }
            Assert.True(s.Find(Officer).HoldsSeat);

            FakeGroups.Powers[Member] = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;
            {
                var assertedValue = r.Call(Member);
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "back with the powers");
            }
        }

        [Fact(Timeout = 60000)]
        public void AMemberWhoIsNoLongerInTheGroup_IsLetGoOnTheTick_WithNoRemovalRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            FakeGroups.Powers.Remove(Member);   // the groups service no longer knows them as a member (a ban and eject, say)

            Tick(r);

            NonSpatialVoiceSession s = r.Sessions.Get(GroupId);
            Assert.False(s.Find(Member).HoldsSeat);
            Assert.Equal(DepartureReason.PowerLost, s.Find(Member).Departure);
            Assert.Equal(new[] { (Member, RoomOf(r)) }, r.Voice.Drops);
            Assert.Equal(new[] { (Member, GroupId, "close_on_no_ability") }, r.Events.Closes);
            Assert.Empty(s.Removed);
            {
                var assertedValue = r.Call(Member);
                TestAssert.WithMessage(() => Assert.NotEqual((int)HttpStatusCode.OK, assertedValue), "and a new call is not answered: they are not a member");
            }
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
