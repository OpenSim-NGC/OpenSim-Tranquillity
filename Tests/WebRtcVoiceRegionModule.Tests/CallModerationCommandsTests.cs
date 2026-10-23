/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The region console's commands for moderating a group call or a conference: listing, mute and unmute (voice or
 * text), mute-all and unmute-all, remove and readmit. They run through the real region module, so each change is
 * recorded, heard and shown in the members' panels as a viewer's request would be.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Interfaces;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CallModerationModuleTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class CallModerationCommandsTests
    {
        private static readonly UUID GroupId = new UUID("5e0c9a41-7b2d-4f86-a3c1-92d84b6e0f17");
        private static readonly UUID Officer = new UUID("11111111-1111-4111-8111-111111111111");
        private static readonly UUID Member = new UUID("22222222-2222-4222-8222-222222222222");
        private static readonly UUID Other = new UUID("33333333-3333-4333-8333-333333333333");

        private sealed class Console
        {
            public readonly List<string> Lines = new();
            public string Text => string.Join("\n", Lines);
        }

        private static (Region Region, Console Console) Rig(bool conference = false, bool group = true)
        {
            Region r = Region.Create(v =>
            {
                v.Set("CallModerationEnabled", "true");
                v.Set("GroupVoiceEnabled", group ? "true" : "false");
                if (conference) v.Set("AdhocVoiceEnabled", "true");
            });
            Console c = new();
            r.Module.CallCommands.Output = c.Lines.Add;
            return (r, c);
        }

        private static void Run(Region r, string line) => r.Module.CallCommands.Execute(line.Split(' '));

        private static UUID StartConference(Region r)
        {
            UUID home = r.Scene.RegionInfo.RegionID;
            UUID id = r.Sessions.StartAdhoc(Officer, UUID.Random(), home, new[] { Member, Other }).Session.SessionId;
            r.Sessions.Accept(id, Member, home, "vs-member");
            r.Sessions.MarkPresent(id, Member, home, "vs-member");
            r.Sessions.MarkPresent(id, Officer, home, "vs-officer");
            return id;
        }

        private static void GroupCallOfTwo(Region r)
        {
            r.Call(Officer);
            r.Call(Member);
            r.Present(Officer);
            r.Present(Member);
        }

        // ---- the switch ----------------------------------------------------------------------------------

        [Test]
        public void TheCommandsExistOnlyWhenCallModerationIsOn()
        {
            using Region off = Region.Create();
            Assert.That(off.Module.CallCommands, Is.Null);

            (Region on, _) = Rig();
            using (on)
                Assert.That(on.Module.CallCommands, Is.Not.Null);
        }

        // ---- listing -------------------------------------------------------------------------------------

        [Test]
        public void ShowVoiceCalls_ListsEachCallWithItsSeatsAndModeration()
        {
            (Region r, Console c) = Rig(conference: true);
            using (r)
            {
                GroupCallOfTwo(r);
                UUID conf = StartConference(r);
                Run(r, $"voice call mute-all {conf}");
                Run(r, $"voice call mute {GroupId} {Member} text");
                Run(r, $"voice call remove {conf} {Other} too loud");
                c.Lines.Clear();

                Run(r, "show voice calls");

                Assert.That(c.Lines[0], Does.Contain("2 call(s)"));
                string group = c.Lines.Single(l => l.Contains(GroupId.ToString()));
                Assert.That(group, Does.Contain("group call").And.Contain("seats 2/").And.Contain("text-muted 1").And.Contain("removed 0"));
                string conference = c.Lines.Single(l => l.Contains(conf.ToString()));
                Assert.That(conference, Does.Contain("conference").And.Contain("voice-moderated YES").And.Contain("removed 1"));
            }
        }

        [Test]
        public void ShowVoiceCalls_WithNoCall_SaysSo()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                Run(r, "show voice calls");
                Assert.That(c.Text, Does.Contain("No group call or conference"));
            }
        }

        [Test]
        public void ShowVoiceCall_ListsMembersWithMuteAndRemovalState_AndNeverACredential()
        {
            (Region r, Console c) = Rig(conference: true);
            using (r)
            {
                GroupCallOfTwo(r);
                string token = r.Sessions.IssueToken(GroupId, Officer);
                UUID conf = StartConference(r);
                string confToken = r.Sessions.IssueToken(conf, Officer);
                string textKey = r.Sessions.CarrierKey(conf, Officer);
                Run(r, $"voice call mute {GroupId} {Member} voice");
                Run(r, $"voice call mute {GroupId} {Member} text");
                Run(r, $"voice call remove {conf} {Other} too loud");
                c.Lines.Clear();

                Run(r, $"show voice call {GroupId}");
                Run(r, $"show voice call {conf}");

                string text = c.Text;
                string groupRow = c.Lines.Single(l => l.Contains(Member.ToString()) && l.Contains("voice MUTED"));
                Assert.That(groupRow, Does.Contain("text MUTED").And.Contain("in voice"));
                string officerRow = c.Lines.First(l => l.Contains(Officer.ToString()));
                Assert.That(officerRow, Does.Contain("voice -").And.Contain("removal -"));
                string removedRow = c.Lines.Single(l => l.Contains(Other.ToString()));
                Assert.That(removedRow, Does.Contain("REMOVED by console until the call ends (too loud)"));
                Assert.That(token, Is.Not.Empty);
                Assert.That(text, Does.Not.Contain(token), "the group call's token is never printed");
                Assert.That(text, Does.Not.Contain(confToken), "nor a conference's");
                Assert.That(string.IsNullOrEmpty(textKey) || !text.Contains(textKey), Is.True, "nor its text key");
                Assert.That(text, Does.Not.Contain("credentials").And.Not.Contain("secret"));
            }
        }

        [Test]
        public void ShowVoiceCall_ForAnUnknownCall_OrABadId_SaysSo()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                Run(r, $"show voice call {UUID.Random()}");
                Run(r, "show voice call nonsense");
                Run(r, "show voice call");

                Assert.That(c.Lines[0], Does.StartWith("No such call"));
                Assert.That(c.Lines[1], Does.StartWith("Usage: show voice call"));
                Assert.That(c.Lines[2], Does.StartWith("Usage:"));
            }
        }

        // ---- mute and unmute ------------------------------------------------------------------------------

        [Test]
        public void VoiceCallMute_AndUnmute_OfOneMemberVoice_AreRecordedAndHeard()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);

                Run(r, $"voice call mute {GroupId} {Member} voice");

                Assert.That(c.Lines.Last(), Does.StartWith("Done:"));
                Assert.That(r.Sessions.Get(GroupId).IsVoiceMuted(Member), Is.True);
                Assert.That(r.Voice.Mutes, Has.Some.Matches<(UUID Agent, int Room, bool Muted)>(m => m.Agent == Member && m.Muted));

                Run(r, $"voice call mute {GroupId} {Member} voice");
                Assert.That(c.Lines.Last(), Does.StartWith("No change:"));

                Run(r, $"voice call unmute {GroupId} {Member} voice");
                Assert.That(c.Lines.Last(), Does.StartWith("Done:"));
                Assert.That(r.Sessions.Get(GroupId).IsVoiceMuted(Member), Is.False);
                Assert.That(r.Voice.Mutes.Last(m => m.Agent == Member).Muted, Is.False);
            }
        }

        [Test]
        public void VoiceCallMute_OfText_DropsTheMembersGroupChat_AndTellsThem()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);
                IGroupChatGate gate = r.Scene.RequestModuleInterface<IGroupChatGate>();

                Run(r, $"voice call mute {GroupId} {Member} text");

                Assert.That(gate.MaySend(GroupId, Member), Is.False);
                Assert.That(r.Events.Lists.Any(l => l.To == Member && l.Updates.Any(u => u.mutedText)), Is.True);
                Run(r, $"voice call unmute {GroupId} {Member} text");
                Assert.That(gate.MaySend(GroupId, Member), Is.True);
            }
        }

        [Test]
        public void MuteAll_AndUnmuteAll_OfAGroupCall()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);

                Run(r, $"voice call mute-all {GroupId}");

                Assert.That(r.Sessions.Get(GroupId).VoiceModerated, Is.True);
                Assert.That(r.Voice.Mutes.Where(m => m.Muted).Select(m => m.Agent), Does.Contain(Member).And.Not.Contain(Officer),
                            "the moderator is not silenced");
                Assert.That(r.Events.Modes.Any(m => m.To == Member && m.Moderated), Is.True);

                Run(r, $"voice call unmute-all {GroupId}");
                Assert.That(r.Sessions.Get(GroupId).VoiceModerated, Is.False);
                Assert.That(r.Voice.Mutes.Last(m => m.Agent == Member).Muted, Is.False);
            }
        }

        [Test]
        public void MuteAll_OfAConference_SilencesWhoJoinsAfterwardsToo()
        {
            (Region r, Console c) = Rig(conference: true);
            using (r)
            {
                UUID conf = StartConference(r);

                Run(r, $"voice call mute-all {conf}");
                Assert.That(c.Lines.Last(), Does.StartWith("Done:"));
                Assert.That(r.Sessions.Get(conf).VoiceModerated, Is.True);
                Assert.That(r.Voice.Mutes.Where(m => m.Muted).Select(m => m.Agent), Does.Contain(Member).And.Contain(Officer),
                            "no conference member is a moderator");

                UUID home = r.Scene.RegionInfo.RegionID;
                r.Sessions.Accept(conf, Other, home, "vs-other");
                r.Sessions.MarkPresent(conf, Other, home, "vs-other");
                r.Voice.Mutes.Clear();
                typeof(WebRtcVoiceRegionModule).GetMethod("SyncCallVoiceOnJoin", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(r.Module, new object[] { Other, r.Sessions.Get(conf) });
                Assert.That(r.Voice.Mutes, Has.Some.Matches<(UUID Agent, int Room, bool Muted)>(m => m.Agent == Other && m.Muted),
                            "whoever joins the conference afterwards is silenced");
            }
        }

        [Test]
        public void AVoiceMuteOfAMemberTheRoomHasNoParticipantFor_IsRefusedAndNothingIsRecorded()
        {
            (Region r, Console c) = Rig(conference: true);
            using (r)
            {
                UUID conf = StartConference(r);
                r.Voice.NoParticipant.Add(Member);

                Run(r, $"voice call mute {conf} {Member} voice");

                Assert.That(c.Lines.Last(), Does.StartWith("Not done (404)").And.Contain("target-unresolved"));
                Assert.That(r.Sessions.Get(conf).ModerationGeneration, Is.EqualTo(0));
                Assert.That(r.Voice.Mutes, Is.Empty);
            }
        }

        [Test]
        public void AMuteOfSomeoneNotInTheCall_IsRefused()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);

                Run(r, $"voice call mute {GroupId} {UUID.Random()} voice");

                Assert.That(c.Lines.Last(), Does.StartWith("Not done (404)").And.Contain("target-not-in-call"));
            }
        }

        [Test]
        public void AVoiceMuteWithAVoiceServerThatCannotCarryItOut_IsRefusedWith501_ButTextStillWorks()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);
                r.Voice.Answer = ParticipantMuteResult.Unsupported;

                Run(r, $"voice call mute {GroupId} {Member} voice");
                Assert.That(c.Lines.Last(), Does.StartWith("Not done (501)"));
                Assert.That(r.Sessions.Get(GroupId).IsVoiceMuted(Member), Is.False);

                Run(r, $"voice call mute {GroupId} {Member} text");
                Assert.That(c.Lines.Last(), Does.StartWith("Done:"));
            }
        }

        [Test]
        public void AGroupModeratorMuted_FromTheConsole_IsRecordedButNeverSilenced_AndTheOutputSaysSo()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);

                Run(r, $"voice call mute {GroupId} {Officer} voice");

                Assert.That(c.Lines.Last(), Does.Contain("never silenced"));
                Assert.That(r.Voice.Mutes.Where(m => m.Agent == Officer).All(m => !m.Muted), Is.True);
            }
        }

        // ---- remove and readmit ------------------------------------------------------------------------

        [Test]
        public void VoiceCallRemove_OfAConferenceMember_AndReadmit()
        {
            (Region r, Console c) = Rig(conference: true);
            using (r)
            {
                UUID conf = StartConference(r);

                Run(r, $"voice call remove {conf} {Member} talking over the speaker");

                Assert.That(c.Lines.Last(), Does.Contain("removed from call").And.Contain("1 participant(s) dropped"));
                NonSpatialVoiceSession s = r.Sessions.Get(conf);
                Assert.That(s.IsRemoved(Member, DateTime.UtcNow), Is.True);
                Assert.That(s.RemovalOf(Member).Reason, Is.EqualTo("talking over the speaker"));
                Assert.That(s.RemovalOf(Member).By, Is.EqualTo(CallModerationCommands.Operator));
                Assert.That(s.ChatMembers(), Does.Not.Contain(Member), "removed from the conference text");
                Assert.That(r.Events.Closes, Has.Some.Matches<(UUID To, UUID Session, string Reason)>(x => x.To == Member && x.Session == conf));

                Run(r, $"voice call remove {conf} {Member}");
                Assert.That(c.Lines.Last(), Does.Contain("already removed"));

                Run(r, $"voice call readmit {conf} {Member}");
                Assert.That(c.Lines.Last(), Does.Contain("may join call").And.Contain("not seated"));
                Assert.That(r.Sessions.Accept(conf, Member, r.Scene.RegionInfo.RegionID).Ok, Is.True);

                Run(r, $"voice call readmit {conf} {Member}");
                Assert.That(c.Lines.Last(), Does.Contain("was not removed"));
            }
        }

        [Test]
        public void VoiceCallRemove_OfAGroupMember_LeavesTheGroupsText()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);
                IGroupChatGate gate = r.Scene.RequestModuleInterface<IGroupChatGate>();

                Run(r, $"voice call remove {GroupId} {Member}");

                Assert.That(c.Lines.Last(), Does.Contain("removed from call"));
                Assert.That(r.Sessions.Get(GroupId).RemovalOf(Member).Reason, Is.EqualTo("removed from the console"));
                Assert.That(r.Sessions.Get(GroupId).Find(Member).InChat, Is.True);
                Assert.That(gate.MaySend(GroupId, Member), Is.True);
                Assert.That(r.Call(Member), Is.EqualTo((int)HttpStatusCode.Forbidden));
            }
        }

        // ---- bad input --------------------------------------------------------------------------------------

        [Test]
        public void BadInputIsAnsweredWithAMessage_AndChangesNothing()
        {
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);
                int generation = r.Sessions.Get(GroupId).ModerationGeneration;
                UUID p2p = r.Sessions.Start(NonSpatialSessionType.P2P, Officer, Member, UUID.Random(), r.Scene.RegionInfo.RegionID).Session.SessionId;

                Run(r, "voice call mute not-a-uuid " + Member + " voice");
                Run(r, $"voice call mute {UUID.Random()} {Member} voice");
                Run(r, $"voice call mute {GroupId} not-a-uuid voice");
                Run(r, $"voice call mute {GroupId} {Member} video");
                Run(r, $"voice call mute {GroupId} {Member}");
                Run(r, $"voice call mute-all {GroupId} extra");
                Run(r, $"voice call remove {GroupId}");
                Run(r, $"voice call remove {p2p} {Member}");
                Run(r, $"voice call frobnicate {GroupId}");
                Run(r, "voice call");

                Assert.That(c.Lines.Count, Is.EqualTo(10));
                Assert.That(c.Lines.All(l => l.Length > 0 && !l.StartsWith("Done")), Is.True, c.Text);
                Assert.That(c.Lines[1], Does.StartWith("No such call"));
                Assert.That(c.Lines[7], Does.Contain("not moderated"));
                Assert.That(r.Sessions.Get(GroupId).ModerationGeneration, Is.EqualTo(generation), "nothing was changed");
                Assert.That(r.Sessions.Get(GroupId).Removed, Is.Empty);
            }
        }
    }
}
