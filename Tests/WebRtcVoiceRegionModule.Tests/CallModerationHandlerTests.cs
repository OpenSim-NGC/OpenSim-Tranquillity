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
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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
                Assert.That(handled, Is.EqualTo(expectHandled));
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

        [Test]
        public void AModeratorMutesAMembersVoice_ItIsRecordedAndEnforced()
        {
            Rig r = new();

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), o.Instrument);
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.True);
            Assert.That(r.Effects.Enforced, Is.EqualTo(1));
            Assert.That(r.Effects.Changes, Is.EqualTo(new[] { (ModerationOp.VoiceMute, Bob, Alice, true) }));

            ChatSessionOutcome again = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));
            Assert.That(again.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(again.Instrument, Does.Contain(CallModerationHandler.DecisionUnchanged));
            Assert.That(r.Session.ModerationGeneration, Is.EqualTo(1));

            r.Post(Alice, MuteUpdate(Group, Bob, "voice", false));
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.False);
        }

        [Test]
        public void ATextMuteIsRecordedAndNeedsNoVoiceServer()
        {
            Rig r = new();
            r.Effects.Can = false;

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Bob, "text", true));

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(r.Session.IsTextMuted(Bob), Is.True);
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.False);
            Assert.That(r.Effects.Enforced, Is.EqualTo(0));
        }

        [Test]
        public void MuteAllAndUnmuteAll()
        {
            Rig r = new();

            Assert.That(r.Post(Alice, SessionUpdate(Group, true)).Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(r.Session.VoiceModerated, Is.True);
            Assert.That(r.Session.IsVoiceMuted(Dave), Is.True, "a later joiner is muted");

            r.Post(Alice, MuteUpdate(Group, Bob, "voice", false));
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.False, "let speak by name");

            Assert.That(r.Post(Alice, SessionUpdate(Group, false)).Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(r.Session.VoiceModerated, Is.False);
            Assert.That(r.Session.IsVoiceMuted(Carol), Is.False);
            Assert.That(r.Effects.Enforced, Is.EqualTo(3));
        }

        [Test]
        public void ARequesterWhoDoesNotHoldTheModeratePowerIsRefused_AndNothingIsRecorded()
        {
            Rig r = new();

            ChatSessionOutcome o = r.Post(Bob, MuteUpdate(Group, Carol, "voice", true));
            ChatSessionOutcome all = r.Post(Bob, SessionUpdate(Group, true));
            ChatSessionOutcome text = r.Post(Bob, MuteUpdate(Group, Carol, "text", true));

            foreach (ChatSessionOutcome x in new[] { o, all, text })
            {
                Assert.That(x.Status, Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(x.Instrument, Does.Contain(CallModerationHandler.DecisionNotModerator));
            }
            Assert.That(r.Session.ModerationGeneration, Is.EqualTo(0));
            Assert.That(r.Effects.Enforced, Is.EqualTo(0));
        }

        [Test]
        public void AnOutsiderWithThePowerButNoMembershipIsRefused()
        {
            Rig r = new();
            r.Powers[Dave] = CallModerationPolicy.PowerModerateChat;   // holds the bit, is not in the group

            Assert.That(r.Post(Dave, MuteUpdate(Group, Bob, "voice", true)).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public void ThePowerIsAskedOnEveryRequest_AndLosingItEndsTheRight()
        {
            Rig r = new();
            Assert.That(r.Post(Alice, MuteUpdate(Group, Bob, "voice", true)).Status, Is.EqualTo(HttpStatusCode.OK));

            r.Powers[Alice] = 0;
            Assert.That(r.Post(Alice, MuteUpdate(Group, Bob, "voice", false)).Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.True, "the refused request changed nothing");

            r.Powers[Carol] = CallModerationPolicy.PowerModerateChat;   // gained mid-call: effective on the next request
            Assert.That(r.Post(Carol, MuteUpdate(Group, Bob, "voice", false)).Status, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void AModeratorIsNeverMuted_AndNobodyMutesThemself()
        {
            Rig r = new();
            r.Powers[Carol] = CallModerationPolicy.PowerModerateChat;

            ChatSessionOutcome self = r.Post(Alice, MuteUpdate(Group, Alice, "voice", true));
            ChatSessionOutcome other = r.Post(Alice, MuteUpdate(Group, Carol, "text", true));

            Assert.That(self.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(self.Instrument, Does.Contain(CallModerationHandler.DecisionSelf));
            Assert.That(other.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(other.Instrument, Does.Contain(CallModerationHandler.DecisionTargetModerator));
            Assert.That(r.Session.ModerationGeneration, Is.EqualTo(0));
        }

        [Test]
        public void AnUnknownSessionIs404_AConferenceAndACallBetweenTwoAreRefused()
        {
            Rig r = new();
            SessionOutcome conf = r.Engine.StartAdhoc(Alice, UUID.Random(), RegionA);
            SessionOutcome p2p = r.Engine.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Random(), RegionA);

            Assert.That(r.Post(Alice, MuteUpdate(UUID.Random(), Bob, "voice", true)).Status, Is.EqualTo(HttpStatusCode.NotFound));
            ChatSessionOutcome c = r.Post(Alice, MuteUpdate(conf.Session.SessionId, Bob, "voice", true));
            Assert.That(c.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(c.Instrument, Does.Contain(CallModerationHandler.DecisionAdhoc));
            ChatSessionOutcome all = r.Post(Alice, SessionUpdate(conf.Session.SessionId, true));
            Assert.That(all.Instrument, Does.Contain(CallModerationHandler.DecisionAdhoc));
            Assert.That(r.Post(Alice, MuteUpdate(p2p.Session.SessionId, Bob, "voice", true)).Instrument,
                        Does.Contain(CallModerationHandler.DecisionP2P));
            Assert.That(conf.Session.ModerationGeneration, Is.EqualTo(0));
        }

        [Test]
        public void MalformedBodiesAreRefusedAndChangeNothing()
        {
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
                Assert.That(o.Status, Is.EqualTo(HttpStatusCode.BadRequest), o.Instrument);
            }
            Assert.That(r.Session.ModerationGeneration, Is.EqualTo(0));
            Assert.That(r.Effects.Enforced, Is.EqualTo(0));
        }

        [Test]
        public void WithTheFeatureOffNothingIsHandled_AndOtherMethodsAreNotTaken()
        {
            Rig r = new() { Enabled = false };
            r.Post(Alice, MuteUpdate(Group, Bob, "voice", true), expectHandled: false);
            r.Post(Alice, SessionUpdate(Group, true), expectHandled: false);
            Assert.That(r.Session.ModerationGeneration, Is.EqualTo(0));

            Rig on = new();
            on.Post(Alice, new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(Group) }, expectHandled: false);
        }

        [Test]
        public void AVoiceMuteThatCannotBeHeardIsRefusedBeforeAnythingIsRecorded()
        {
            Rig r = new();
            r.Effects.Can = false;

            ChatSessionOutcome one = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));
            ChatSessionOutcome all = r.Post(Alice, SessionUpdate(Group, true));

            Assert.That(one.Status, Is.EqualTo(HttpStatusCode.NotImplemented));
            Assert.That(one.Instrument, Does.Contain(CallModerationHandler.DecisionVoiceUnsupported));
            Assert.That(all.Status, Is.EqualTo(HttpStatusCode.NotImplemented));
            Assert.That(r.Session.ModerationGeneration, Is.EqualTo(0));
            Assert.That(r.Effects.Changes, Is.Empty);
        }

        [TestCase(ParticipantMuteResult.Unsupported, HttpStatusCode.NotImplemented)]
        [TestCase(ParticipantMuteResult.Failed, HttpStatusCode.ServiceUnavailable)]
        public void AVoiceMuteThatIsRecordedButCannotBeApplied_IsTakenBackOutAndNeverAnswered200(ParticipantMuteResult result, HttpStatusCode status)
        {
            Rig r = new();
            r.Effects.Result = result;

            ChatSessionOutcome one = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));
            Assert.That(one.Status, Is.EqualTo(status));
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.False, "the record does not say what the room does not do");

            ChatSessionOutcome all = r.Post(Alice, SessionUpdate(Group, true));
            Assert.That(all.Status, Is.EqualTo(status));
            Assert.That(r.Session.VoiceModerated, Is.False);
            Assert.That(r.Effects.Changes, Is.Empty, "nobody was told of a change that was not made");
        }

        [Test]
        public void AMuteOfAnAgentTheCallDoesNotHold_IsRefusedAndNothingIsRecorded()
        {
            Rig r = new();

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Dave, "voice", true));

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(o.Instrument, Does.Contain(CallModerationHandler.DecisionTargetNotInCall));
            Assert.That(r.Session.IsVoiceMuted(Dave), Is.False);
            Assert.That(r.Session.ModerationGeneration, Is.EqualTo(0));
            Assert.That(r.Effects.Enforced, Is.EqualTo(0), "the room was not asked to mute anyone");
        }

        [Test]
        public void AMemberWhoHasLeft_CannotBeMuted_ButCanBeUnmuted()
        {
            Rig r = new();
            Assert.That(r.Post(Alice, MuteUpdate(Group, Bob, "voice", true)).Status, Is.EqualTo(HttpStatusCode.OK));
            r.Engine.Depart(Group, Bob, DepartureReason.VoiceTeardown);
            r.Engine.Depart(Group, Carol, DepartureReason.VoiceTeardown);

            ChatSessionOutcome muteCarol = r.Post(Alice, MuteUpdate(Group, Carol, "voice", true));
            Assert.That(muteCarol.Status, Is.EqualTo(HttpStatusCode.NotFound), "a member who left holds no seat to mute");
            Assert.That(r.Session.IsVoiceMuted(Carol), Is.False);

            ChatSessionOutcome unmuteBob = r.Post(Alice, MuteUpdate(Group, Bob, "voice", false));
            Assert.That(unmuteBob.Status, Is.EqualTo(HttpStatusCode.OK), "a mute left on someone who left can be cleared");
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.False);
        }

        [Test]
        public void AnUnresolvableTarget_IsRefusedBeforeAnythingIsRecorded()
        {
            Rig r = new();
            r.Effects.Resolves = false;   // the voice server shows no participant for the agent's id

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(o.Instrument, Does.Contain(CallModerationHandler.DecisionTargetUnresolved));
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.False);
            Assert.That(r.Session.ModerationGeneration, Is.EqualTo(0), "nothing was recorded");
            Assert.That(r.Effects.Enforced, Is.EqualTo(0));
            Assert.That(r.Effects.Changes, Is.Empty);
        }

        [Test]
        public void AParticipantThatVanishesBeforeTheMute_IsRefused_TheRecordIsRestored_AndTheRoomToldTheEarlierState()
        {
            Rig r = new();
            r.Effects.Result = ParticipantMuteResult.NotInRoom;

            ChatSessionOutcome o = r.Post(Alice, MuteUpdate(Group, Bob, "voice", true));

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(o.Instrument, Does.Contain(CallModerationHandler.DecisionTargetUnresolved));
            Assert.That(r.Session.IsVoiceMuted(Bob), Is.False, "not a mute: the record does not hold it");
            Assert.That(r.Effects.Changes, Is.Empty);
            Assert.That(r.Effects.EnforcedTargets, Is.EqualTo(new[] { Bob, UUID.Zero }), "asked once for Bob, then the earlier state to the whole room");
            Assert.That(r.Effects.Told[1], Is.EqualTo((false, 0)), "the earlier state: nobody muted");
        }

        [Test]
        public void AMuteAllThatFailsPartway_IsTakenBackOut_AndTheRoomToldTheEarlierStateAgain()
        {
            Rig r = new();
            r.Effects.Result = ParticipantMuteResult.Failed;

            ChatSessionOutcome all = r.Post(Alice, SessionUpdate(Group, true));

            Assert.That(all.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(r.Session.VoiceModerated, Is.False);
            Assert.That(r.Effects.Enforced, Is.EqualTo(2), "the mute, then the earlier state, so no member stays muted at the room");
            Assert.That(r.Effects.Told[0].Moderated, Is.True);
            Assert.That(r.Effects.Told[1].Moderated, Is.False);
        }

        [Test]
        public void AMuteAllWithNobodyInTheRoomYet_IsRecorded()
        {
            Rig r = new();
            r.Effects.Result = ParticipantMuteResult.NotInRoom;

            Assert.That(r.Post(Alice, SessionUpdate(Group, true)).Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(r.Session.VoiceModerated, Is.True, "each member is muted as it joins");
        }

        [Test]
        public void TheModeratePowerIsTheViewersBit()
        {
            Assert.That(CallModerationPolicy.PowerModerateChat, Is.EqualTo(1UL << 37));
            Assert.That(CallModerationPolicy.PowerModerateChat, Is.EqualTo((ulong)GroupPowers.ModerateChat));
        }

        [Test]
        public void TheSettingsDefaultToOff_AndReadTheirKeys()
        {
            Nini.Config.IniConfigSource src = new();
            Nini.Config.IConfig cfg = src.AddConfig("WebRtcVoice");
            Assert.That(CallModerationSettings.Read(cfg).Enabled, Is.False);
            Assert.That(CallModerationSettings.Read(cfg).RevalidateSeconds, Is.EqualTo(60));
            Assert.That(CallModerationSettings.Read(null).Enabled, Is.False);

            cfg.Set("CallModerationEnabled", "true");
            cfg.Set("CallModerationRevalidateSeconds", "0");
            Assert.That(CallModerationSettings.Read(cfg).Enabled, Is.True);
            Assert.That(CallModerationSettings.Read(cfg).RevalidateSeconds, Is.EqualTo(0));
        }
    }
}
