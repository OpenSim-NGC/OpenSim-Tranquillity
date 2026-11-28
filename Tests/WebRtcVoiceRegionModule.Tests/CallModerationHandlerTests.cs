/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The "mute update" and "session update" requests of a group call, as the stock viewers post them (the bodies
 * below are built the way llspeakers.cpp builds them), who may make them, and what is recorded and enforced.
 */
using System;
using System.Collections.Generic;
using System.Net;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class CallModerationHandlerTests
    {
        private static readonly UUID Group = NonSpatialIdentityTests.Group;
        private static readonly UUID Dave = new UUID("44444444-4444-4444-4444-444444444444");

        private sealed class FakeEffects : ICallModerationEffects
        {
            public bool Can = true;
            public bool Resolves = true;
            public ParticipantMuteResult Result = ParticipantMuteResult.Applied;
            public int Enforced;
            public readonly List<UUID> EnforcedTargets = new();
            public readonly List<(bool Moderated, int Muted)> Told = new();
            public readonly List<(ModerationOp Op, UUID Target, UUID By, bool Changed)> Changes = new();
            public bool CanEnforceVoice(NonSpatialVoiceSession session) => Can;
            public bool CanResolveVoiceTarget(NonSpatialVoiceSession session, UUID target) => Resolves;
            public ParticipantMuteResult EnforceVoice(NonSpatialVoiceSession session, UUID target)
            {
                Enforced++;
                EnforcedTargets.Add(target);
                Told.Add((session.VoiceModerated, session.VoiceMuted.Count));
                return Result;
            }
            public void Changed(NonSpatialVoiceSession session, ModerationOp op, UUID target, UUID by, bool changed)
                => Changes.Add((op, target, by, changed));
        }

        private sealed class Rig
        {
            public readonly NonSpatialVoiceSessionEngine Engine = NewEngine();
            public readonly FakeEffects Effects = new();
            public readonly HashSet<UUID> Members = new() { Alice, Bob, Carol };
            public readonly Dictionary<UUID, ulong> Powers = new();
            public CallModerationPolicy Policy;
            public bool Enabled = true;

            public Rig()
            {
                Powers[Alice] = CallModerationPolicy.PowerModerateChat;   // the officer
                Engine.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
                Engine.Accept(Group, Bob, RegionA);
                Engine.Accept(Group, Carol, RegionA);
                Policy = new CallModerationPolicy
                {
                    Settings = new CallModerationSettings { Enabled = true },
                    IsMember = (a, g) => g == Group && Members.Contains(a),
                    Powers = (a, g) => Powers.TryGetValue(a, out ulong p) ? p : 0UL,
                };
            }

            public NonSpatialVoiceSession Session => Engine.Get(Group);

            public ChatSessionOutcome Post(UUID who, OSDMap body, bool expectHandled = true)
            {
                bool handled = CallModerationHandler.TryHandle(body, who, Enabled ? Policy : new CallModerationPolicy { Settings = CallModerationSettings.Off, IsMember = Policy.IsMember, Powers = Policy.Powers },
                                                               Engine, Effects, out ChatSessionOutcome o);
                Assert.Equal(expectHandled, handled);
                return o;
            }
        }

        // The bodies a stock viewer posts (llspeakers.cpp: moderateVoiceParticipant, toggleAllowTextChat, moderateVoiceSession).
        private static OSDMap MuteUpdate(UUID session, UUID target, string kind, bool value)
            => new OSDMap
            {
                ["method"] = OSD.FromString("mute update"),
                ["session-id"] = OSD.FromUUID(session),
                ["params"] = new OSDMap
                {
                    ["agent_id"] = OSD.FromUUID(target),
                    ["mute_info"] = new OSDMap { [kind] = OSD.FromBoolean(value) },
                },
            };

        private static OSDMap SessionUpdate(UUID session, bool value)
            => new OSDMap
            {
                ["method"] = OSD.FromString("session update"),
                ["session-id"] = OSD.FromUUID(session),
                ["params"] = new OSDMap
                {
                    ["update_info"] = new OSDMap { ["moderated_mode"] = new OSDMap { ["voice"] = OSD.FromBoolean(value) } },
                },
            };

        [Fact(Timeout = 60000)]
        public void AModeratorMutesAMembersVoice_ItIsRecordedAndEnforced()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));

            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), o.Instrument);
            }
            Assert.True(r.Session.IsVoiceMuted(Bob));
            Assert.Equal(1, r.Effects.Enforced);
            Assert.Equal(new[] { (ModerationOp.VoiceMute, Bob, Alice, true) }, r.Effects.Changes);

            ChatSessionOutcome again = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));
            Assert.Equal(HttpStatusCode.OK, again.Status);
            Assert.Contains(CallModerationHandler.DecisionUnchanged, again.Instrument);
            Assert.Equal(1, r.Session.ModerationGeneration);

            r.Post(Alice, MuteUpdate(Group, Bob, "voice", false));
            Assert.False(r.Session.IsVoiceMuted(Bob));
        }

        [Fact(Timeout = 60000)]
        public void ATextMuteIsRecordedAndNeedsNoVoiceServer()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Effects.Can = false;

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Bob, "text", true));

            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.True(r.Session.IsTextMuted(Bob));
            Assert.False(r.Session.IsVoiceMuted(Bob));
            Assert.Equal(0, r.Effects.Enforced);
        }

        [Fact(Timeout = 60000)]
        public void MuteAllAndUnmuteAll()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();

            Assert.Equal(HttpStatusCode.OK, r.Post(Alice, SessionUpdate(Group, true)).Status);
            Assert.True(r.Session.VoiceModerated);
            Assert.True(r.Session.IsVoiceMuted(Dave), "a later joiner is muted");

            r.Post(Alice, MuteUpdate(Group, Bob, "voice", false));
            Assert.False(r.Session.IsVoiceMuted(Bob), "let speak by name");

            Assert.Equal(HttpStatusCode.OK, r.Post(Alice, SessionUpdate(Group, false)).Status);
            Assert.False(r.Session.VoiceModerated);
            Assert.False(r.Session.IsVoiceMuted(Carol));
            Assert.Equal(3, r.Effects.Enforced);
        }

        [Fact(Timeout = 60000)]
        public void ARequesterWhoDoesNotHoldTheModeratePowerIsRefused_AndNothingIsRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();

            ChatSessionOutcome o = r.Post(Bob, MuteUpdate(Group, Carol, "voice", true));
            ChatSessionOutcome all = r.Post(Bob, SessionUpdate(Group, true));
            ChatSessionOutcome text = r.Post(Bob, MuteUpdate(Group, Carol, "text", true));

            foreach (ChatSessionOutcome x in new[] { o, all, text })
            {
                Assert.Equal(HttpStatusCode.Forbidden, x.Status);
                Assert.Contains(CallModerationHandler.DecisionNotModerator, x.Instrument);
            }
            Assert.Equal(0, r.Session.ModerationGeneration);
            Assert.Equal(0, r.Effects.Enforced);
        }

        [Fact(Timeout = 60000)]
        public void AnOutsiderWithThePowerButNoMembershipIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Powers[Dave] = CallModerationPolicy.PowerModerateChat;   // holds the bit, is not in the group

            Assert.Equal(HttpStatusCode.Forbidden, r.Post(Dave, MuteUpdate(Group, Bob, "voice", true)).Status);
        }

        [Fact(Timeout = 60000)]
        public void ThePowerIsAskedOnEveryRequest_AndLosingItEndsTheRight()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            Assert.Equal(HttpStatusCode.OK, r.Post(Alice, MuteUpdate(Group, Bob, "voice", true)).Status);

            r.Powers[Alice] = 0;
            Assert.Equal(HttpStatusCode.Forbidden, r.Post(Alice, MuteUpdate(Group, Bob, "voice", false)).Status);
            Assert.True(r.Session.IsVoiceMuted(Bob), "the refused request changed nothing");

            r.Powers[Carol] = CallModerationPolicy.PowerModerateChat;   // gained mid-call: effective on the next request
            Assert.Equal(HttpStatusCode.OK, r.Post(Carol, MuteUpdate(Group, Bob, "voice", false)).Status);
        }

        [Fact(Timeout = 60000)]
        public void AModeratorIsNeverMuted_AndNobodyMutesThemself()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Powers[Carol] = CallModerationPolicy.PowerModerateChat;

            ChatSessionOutcome self = r.Post(Alice, MuteUpdate(Group, Alice, "voice", true));
            ChatSessionOutcome other = r.Post(Alice, MuteUpdate(Group, Carol, "text", true));

            Assert.Equal(HttpStatusCode.Forbidden, self.Status);
            Assert.Contains(CallModerationHandler.DecisionSelf, self.Instrument);
            Assert.Equal(HttpStatusCode.Forbidden, other.Status);
            Assert.Contains(CallModerationHandler.DecisionTargetModerator, other.Instrument);
            Assert.Equal(0, r.Session.ModerationGeneration);
        }

        [Fact(Timeout = 60000)]
        public void AnUnknownSessionIs404_AConferenceAndACallBetweenTwoAreRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            SessionOutcome conf = r.Engine.StartAdhoc(Alice, UUID.Random(), RegionA);
            SessionOutcome p2p = r.Engine.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Random(), RegionA);

            Assert.Equal(HttpStatusCode.NotFound, r.Post(Alice, MuteUpdate(UUID.Random(), Bob, "voice", true)).Status);
            ChatSessionOutcome c = r.Post(Alice, MuteUpdate(conf.Session.SessionId, Bob, "voice", true));
            Assert.Equal(HttpStatusCode.Forbidden, c.Status);
            Assert.Contains(CallModerationHandler.DecisionAdhoc, c.Instrument);
            ChatSessionOutcome all = r.Post(Alice, SessionUpdate(conf.Session.SessionId, true));
            Assert.Contains(CallModerationHandler.DecisionAdhoc, all.Instrument);
            Assert.Contains(CallModerationHandler.DecisionP2P, r.Post(Alice, MuteUpdate(p2p.Session.SessionId, Bob, "voice", true)).Instrument);
            Assert.Equal(0, conf.Session.ModerationGeneration);
        }

        [Fact(Timeout = 60000)]
        public void MalformedBodiesAreRefusedAndChangeNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            OSDMap noAgent = MuteUpdate(Group, Bob, "voice", true);
            ((OSDMap)noAgent["params"]).Remove("agent_id");
            OSDMap both = MuteUpdate(Group, Bob, "voice", true);
            ((OSDMap)((OSDMap)both["params"])["mute_info"])["text"] = OSD.FromBoolean(true);
            OSDMap neither = MuteUpdate(Group, Bob, "voice", true);
            ((OSDMap)((OSDMap)neither["params"])["mute_info"]).Remove("voice");
            OSDMap notBool = MuteUpdate(Group, Bob, "voice", true);
            ((OSDMap)((OSDMap)notBool["params"])["mute_info"])["voice"] = OSD.FromString("yes");
            OSDMap otherKey = MuteUpdate(Group, Bob, "video", true);
            OSDMap noParams = new OSDMap { ["method"] = OSD.FromString("mute update"), ["session-id"] = OSD.FromUUID(Group) };
            OSDMap noMode = SessionUpdate(Group, true);
            ((OSDMap)((OSDMap)((OSDMap)noMode["params"])["update_info"])["moderated_mode"]).Remove("voice");
            OSDMap modeNotBool = SessionUpdate(Group, true);
            ((OSDMap)((OSDMap)((OSDMap)modeNotBool["params"])["update_info"])["moderated_mode"])["voice"] = OSD.FromInteger(1);
            OSDMap noSession = MuteUpdate(Group, Bob, "voice", true);
            noSession.Remove("session-id");

            foreach (OSDMap bad in new[] { noAgent, both, neither, notBool, otherKey, noParams, noMode, modeNotBool, noSession })
            {
                ChatSessionOutcome o = r.Post(Alice, bad);
                {
                    var assertedValue = o.Status;
                    TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.BadRequest, assertedValue), o.Instrument);
                }
            }
            Assert.Equal(0, r.Session.ModerationGeneration);
            Assert.Equal(0, r.Effects.Enforced);
        }

        [Fact(Timeout = 60000)]
        public void WithTheFeatureOffNothingIsHandled_AndOtherMethodsAreNotTaken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new() { Enabled = false };
            r.Post(Alice, MuteUpdate(Group, Bob, "voice", true), expectHandled: false);
            r.Post(Alice, SessionUpdate(Group, true), expectHandled: false);
            Assert.Equal(0, r.Session.ModerationGeneration);

            Rig on = new();
            on.Post(Alice, new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(Group) }, expectHandled: false);
        }

        [Fact(Timeout = 60000)]
        public void AVoiceMuteThatCannotBeHeardIsRefusedBeforeAnythingIsRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Effects.Can = false;

            ChatSessionOutcome one = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));
            ChatSessionOutcome all = r.Post(Alice, SessionUpdate(Group, true));

            Assert.Equal(HttpStatusCode.NotImplemented, one.Status);
            Assert.Contains(CallModerationHandler.DecisionVoiceUnsupported, one.Instrument);
            Assert.Equal(HttpStatusCode.NotImplemented, all.Status);
            Assert.Equal(0, r.Session.ModerationGeneration);
            Assert.Empty(r.Effects.Changes);
        }

        [InlineData(ParticipantMuteResult.Unsupported,HttpStatusCode.NotImplemented)]
        [InlineData(ParticipantMuteResult.Failed,HttpStatusCode.ServiceUnavailable)]
        [Theory(Timeout = 60000)]
        public void AVoiceMuteThatIsRecordedButCannotBeApplied_IsTakenBackOutAndNeverAnswered200(ParticipantMuteResult result, HttpStatusCode status)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Effects.Result = result;

            ChatSessionOutcome one = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));
            Assert.Equal(status, one.Status);
            Assert.False(r.Session.IsVoiceMuted(Bob), "the record does not say what the room does not do");

            ChatSessionOutcome all = r.Post(Alice, SessionUpdate(Group, true));
            Assert.Equal(status, all.Status);
            Assert.False(r.Session.VoiceModerated);
            {
                var assertedValue = r.Effects.Changes;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nobody was told of a change that was not made");
            }
        }

        [Fact(Timeout = 60000)]
        public void AMuteOfAnAgentTheCallDoesNotHold_IsRefusedAndNothingIsRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Dave, "voice", true));

            Assert.Equal(HttpStatusCode.NotFound, o.Status);
            Assert.Contains(CallModerationHandler.DecisionTargetNotInCall, o.Instrument);
            Assert.False(r.Session.IsVoiceMuted(Dave));
            Assert.Equal(0, r.Session.ModerationGeneration);
            {
                var assertedValue = r.Effects.Enforced;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the room was not asked to mute anyone");
            }
        }

        [Fact(Timeout = 60000)]
        public void AMemberWhoHasLeft_CannotBeMuted_ButCanBeUnmuted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            Assert.Equal(HttpStatusCode.OK, r.Post(Alice, MuteUpdate(Group, Bob, "voice", true)).Status);
            r.Engine.Depart(Group, Bob, DepartureReason.VoiceTeardown);
            r.Engine.Depart(Group, Carol, DepartureReason.VoiceTeardown);

            ChatSessionOutcome muteCarol = r.Post(Alice, MuteUpdate(Group, Carol, "voice", true));
            {
                var assertedValue = muteCarol.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.NotFound, assertedValue), "a member who left holds no seat to mute");
            }
            Assert.False(r.Session.IsVoiceMuted(Carol));

            ChatSessionOutcome unmuteBob = r.Post(Alice, MuteUpdate(Group, Bob, "voice", false));
            {
                var assertedValue = unmuteBob.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), "a mute left on someone who left can be cleared");
            }
            Assert.False(r.Session.IsVoiceMuted(Bob));
        }

        [Fact(Timeout = 60000)]
        public void AnUnresolvableTarget_IsRefusedBeforeAnythingIsRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Effects.Resolves = false;   // the voice server shows no participant for the agent's id

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));

            Assert.Equal(HttpStatusCode.NotFound, o.Status);
            Assert.Contains(CallModerationHandler.DecisionTargetUnresolved, o.Instrument);
            Assert.False(r.Session.IsVoiceMuted(Bob));
            {
                var assertedValue = r.Session.ModerationGeneration;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "nothing was recorded");
            }
            Assert.Equal(0, r.Effects.Enforced);
            Assert.Empty(r.Effects.Changes);
        }

        [Fact(Timeout = 60000)]
        public void AParticipantThatVanishesBeforeTheMute_IsRefused_TheRecordIsRestored_AndTheRoomToldTheEarlierState()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Effects.Result = ParticipantMuteResult.NotInRoom;

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));

            Assert.Equal(HttpStatusCode.NotFound, o.Status);
            Assert.Contains(CallModerationHandler.DecisionTargetUnresolved, o.Instrument);
            Assert.False(r.Session.IsVoiceMuted(Bob), "not a mute: the record does not hold it");
            Assert.Empty(r.Effects.Changes);
            {
                var assertedValue = r.Effects.EnforcedTargets;
                TestAssert.WithMessage(() => Assert.Equal(new[] { Bob, UUID.Zero }, assertedValue), "asked once for Bob, then the earlier state to the whole room");
            }
            {
                var assertedValue = r.Effects.Told[1];
                TestAssert.WithMessage(() => Assert.Equal((false, 0), assertedValue), "the earlier state: nobody muted");
            }
        }

        [Fact(Timeout = 60000)]
        public void AMuteAllThatFailsPartway_IsTakenBackOut_AndTheRoomToldTheEarlierStateAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Effects.Result = ParticipantMuteResult.Failed;

            ChatSessionOutcome all = r.Post(Alice, SessionUpdate(Group, true));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, all.Status);
            Assert.False(r.Session.VoiceModerated);
            {
                var assertedValue = r.Effects.Enforced;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "the mute, then the earlier state, so no member stays muted at the room");
            }
            Assert.True(r.Effects.Told[0].Moderated);
            Assert.False(r.Effects.Told[1].Moderated);
        }

        [Fact(Timeout = 60000)]
        public void AMuteAllWithNobodyInTheRoomYet_IsRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = new();
            r.Effects.Result = ParticipantMuteResult.NotInRoom;

            Assert.Equal(HttpStatusCode.OK, r.Post(Alice, SessionUpdate(Group, true)).Status);
            Assert.True(r.Session.VoiceModerated, "each member is muted as it joins");
        }

        [Fact(Timeout = 60000)]
        public void TheModeratePowerIsTheViewersBit()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(1UL << 37, CallModerationPolicy.PowerModerateChat);
            Assert.Equal((ulong)GroupPowers.ModerateChat, CallModerationPolicy.PowerModerateChat);
        }

        [Fact(Timeout = 60000)]
        public void TheSettingsDefaultToOff_AndReadTheirKeys()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Nini.Config.IniConfigSource src = new();
            Nini.Config.IConfig cfg = src.AddConfig("WebRtcVoice");
            Assert.False(CallModerationSettings.Read(cfg).Enabled);
            Assert.Equal(60, CallModerationSettings.Read(cfg).RevalidateSeconds);
            Assert.False(CallModerationSettings.Read(null).Enabled);

            cfg.Set("CallModerationEnabled", "true");
            cfg.Set("CallModerationRevalidateSeconds", "0");
            Assert.True(CallModerationSettings.Read(cfg).Enabled);
            Assert.Equal(0, CallModerationSettings.Read(cfg).RevalidateSeconds);
        }
    }
}
