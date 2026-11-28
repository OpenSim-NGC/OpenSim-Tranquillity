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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Interfaces;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CallModerationModuleTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void TheCommandsExistOnlyWhenCallModerationIsOn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region off = Region.Create();
            Assert.Null(off.Module.CallCommands);

            (Region on, _) = Rig();
            using (on)
                Assert.NotNull(on.Module.CallCommands);
        }

        // ---- listing -------------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ShowVoiceCalls_ListsEachCallWithItsSeatsAndModeration()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

                Assert.Contains("2 call(s)", c.Lines[0]);
                string group = c.Lines.Single(l => l.Contains(GroupId.ToString()));
                {
                    var assertedValue = group;
                    Assert.Contains("group call", assertedValue);
                    Assert.Contains("seats 2/", assertedValue);
                    Assert.Contains("text-muted 1", assertedValue);
                    Assert.Contains("removed 0", assertedValue);
                }
                string conference = c.Lines.Single(l => l.Contains(conf.ToString()));
                {
                    var assertedValue = conference;
                    Assert.Contains("conference", assertedValue);
                    Assert.Contains("voice-moderated YES", assertedValue);
                    Assert.Contains("removed 1", assertedValue);
                }
            }
        }

        [Fact(Timeout = 60000)]
        public void ShowVoiceCalls_WithNoCall_SaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                Run(r, "show voice calls");
                Assert.Contains("No group call or conference", c.Text);
            }
        }

        [Fact(Timeout = 60000)]
        public void ShowVoiceCall_ListsMembersWithMuteAndRemovalState_AndNeverACredential()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
                {
                    var assertedValue = groupRow;
                    Assert.Contains("text MUTED", assertedValue);
                    Assert.Contains("in voice", assertedValue);
                }
                string officerRow = c.Lines.First(l => l.Contains(Officer.ToString()));
                {
                    var assertedValue = officerRow;
                    Assert.Contains("voice -", assertedValue);
                    Assert.Contains("removal -", assertedValue);
                }
                string removedRow = c.Lines.Single(l => l.Contains(Other.ToString()));
                Assert.Contains("REMOVED by console until the call ends (too loud)", removedRow);
                Assert.NotEmpty(token);
                {
                    var assertedValue = text;
                    TestAssert.WithMessage(() => Assert.DoesNotContain(token, assertedValue), "the group call's token is never printed");
                }
                {
                    var assertedValue = text;
                    TestAssert.WithMessage(() => Assert.DoesNotContain(confToken, assertedValue), "nor a conference's");
                }
                Assert.True((string.IsNullOrEmpty(textKey) || !text.Contains(textKey)), "nor its text key");
                {
                    var assertedValue = text;
                    Assert.DoesNotContain("credentials", assertedValue);
                    Assert.DoesNotContain("secret", assertedValue);
                }
            }
        }

        [Fact(Timeout = 60000)]
        public void ShowVoiceCall_ForAnUnknownCall_OrABadId_SaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                Run(r, $"show voice call {UUID.Random()}");
                Run(r, "show voice call nonsense");
                Run(r, "show voice call");

                Assert.StartsWith("No such call", c.Lines[0]);
                Assert.StartsWith("Usage: show voice call", c.Lines[1]);
                Assert.StartsWith("Usage:", c.Lines[2]);
            }
        }

        // ---- mute and unmute ------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void VoiceCallMute_AndUnmute_OfOneMemberVoice_AreRecordedAndHeard()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);

                Run(r, $"voice call mute {GroupId} {Member} voice");

                Assert.StartsWith("Done:", c.Lines.Last());
                Assert.True(r.Sessions.Get(GroupId).IsVoiceMuted(Member));
                Assert.Contains<(UUID Agent, int Room, bool Muted)>(r.Voice.Mutes, m => m.Agent == Member && m.Muted);

                Run(r, $"voice call mute {GroupId} {Member} voice");
                Assert.StartsWith("No change:", c.Lines.Last());

                Run(r, $"voice call unmute {GroupId} {Member} voice");
                Assert.StartsWith("Done:", c.Lines.Last());
                Assert.False(r.Sessions.Get(GroupId).IsVoiceMuted(Member));
                Assert.False(r.Voice.Mutes.Last(m => m.Agent == Member).Muted);
            }
        }

        [Fact(Timeout = 60000)]
        public void VoiceCallMute_OfText_DropsTheMembersGroupChat_AndTellsThem()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);
                IGroupChatGate gate = r.Scene.RequestModuleInterface<IGroupChatGate>();

                Run(r, $"voice call mute {GroupId} {Member} text");

                Assert.False(gate.MaySend(GroupId, Member));
                Assert.Contains(r.Events.Lists, l => l.To == Member && l.Updates.Any(u => u.mutedText));
                Run(r, $"voice call unmute {GroupId} {Member} text");
                Assert.True(gate.MaySend(GroupId, Member));
            }
        }

        [Fact(Timeout = 60000)]
        public void MuteAll_AndUnmuteAll_OfAGroupCall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);

                Run(r, $"voice call mute-all {GroupId}");

                Assert.True(r.Sessions.Get(GroupId).VoiceModerated);
                {
                    var assertedValue = r.Voice.Mutes.Where(m => m.Muted).Select(m => m.Agent);
                    TestAssert.WithMessage(() =>
                    {
                        Assert.Contains(Member, assertedValue);
                        Assert.DoesNotContain(Officer, assertedValue);
                    }, "the moderator is not silenced");
                }
                Assert.Contains(r.Events.Modes, m => m.To == Member && m.Moderated);

                Run(r, $"voice call unmute-all {GroupId}");
                Assert.False(r.Sessions.Get(GroupId).VoiceModerated);
                Assert.False(r.Voice.Mutes.Last(m => m.Agent == Member).Muted);
            }
        }

        [Fact(Timeout = 60000)]
        public void MuteAll_OfAConference_SilencesWhoJoinsAfterwardsToo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig(conference: true);
            using (r)
            {
                UUID conf = StartConference(r);

                Run(r, $"voice call mute-all {conf}");
                Assert.StartsWith("Done:", c.Lines.Last());
                Assert.True(r.Sessions.Get(conf).VoiceModerated);
                {
                    var assertedValue = r.Voice.Mutes.Where(m => m.Muted).Select(m => m.Agent);
                    TestAssert.WithMessage(() =>
                    {
                        Assert.Contains(Member, assertedValue);
                        Assert.Contains(Officer, assertedValue);
                    }, "no conference member is a moderator");
                }

                UUID home = r.Scene.RegionInfo.RegionID;
                r.Sessions.Accept(conf, Other, home, "vs-other");
                r.Sessions.MarkPresent(conf, Other, home, "vs-other");
                r.Voice.Mutes.Clear();
                typeof(WebRtcVoiceRegionModule).GetMethod("SyncCallVoiceOnJoin", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(r.Module, new object[] { Other, r.Sessions.Get(conf) });
                {
                    var assertedValue = r.Voice.Mutes;
                    TestAssert.WithMessage(() => Assert.Contains<(UUID Agent, int Room, bool Muted)>(assertedValue, m => m.Agent == Other && m.Muted), "whoever joins the conference afterwards is silenced");
                }
            }
        }

        [Fact(Timeout = 60000)]
        public void AVoiceMuteOfAMemberTheRoomHasNoParticipantFor_IsRefusedAndNothingIsRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig(conference: true);
            using (r)
            {
                UUID conf = StartConference(r);
                r.Voice.NoParticipant.Add(Member);

                Run(r, $"voice call mute {conf} {Member} voice");

                {
                    var assertedValue = c.Lines.Last();
                    Assert.StartsWith("Not done (404)", assertedValue);
                    Assert.Contains("target-unresolved", assertedValue);
                }
                Assert.Equal(0, r.Sessions.Get(conf).ModerationGeneration);
                Assert.Empty(r.Voice.Mutes);
            }
        }

        [Fact(Timeout = 60000)]
        public void AMuteOfSomeoneNotInTheCall_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);

                Run(r, $"voice call mute {GroupId} {UUID.Random()} voice");

                {
                    var assertedValue = c.Lines.Last();
                    Assert.StartsWith("Not done (404)", assertedValue);
                    Assert.Contains("target-not-in-call", assertedValue);
                }
            }
        }

        [Fact(Timeout = 60000)]
        public void AVoiceMuteWithAVoiceServerThatCannotCarryItOut_IsRefusedWith501_ButTextStillWorks()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);
                r.Voice.Answer = ParticipantMuteResult.Unsupported;

                Run(r, $"voice call mute {GroupId} {Member} voice");
                Assert.StartsWith("Not done (501)", c.Lines.Last());
                Assert.False(r.Sessions.Get(GroupId).IsVoiceMuted(Member));

                Run(r, $"voice call mute {GroupId} {Member} text");
                Assert.StartsWith("Done:", c.Lines.Last());
            }
        }

        [Fact(Timeout = 60000)]
        public void AGroupModeratorMuted_FromTheConsole_IsRecordedButNeverSilenced_AndTheOutputSaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);

                Run(r, $"voice call mute {GroupId} {Officer} voice");

                Assert.Contains("never silenced", c.Lines.Last());
                Assert.True(r.Voice.Mutes.Where(m => m.Agent == Officer).All(m => !m.Muted));
            }
        }

        // ---- remove and readmit ------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void VoiceCallRemove_OfAConferenceMember_AndReadmit()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig(conference: true);
            using (r)
            {
                UUID conf = StartConference(r);

                Run(r, $"voice call remove {conf} {Member} talking over the speaker");

                {
                    var assertedValue = c.Lines.Last();
                    Assert.Contains("removed from call", assertedValue);
                    Assert.Contains("1 participant(s) dropped", assertedValue);
                }
                NonSpatialVoiceSession s = r.Sessions.Get(conf);
                Assert.True(s.IsRemoved(Member, DateTime.UtcNow));
                Assert.Equal("talking over the speaker", s.RemovalOf(Member).Reason);
                Assert.Equal(CallModerationCommands.Operator, s.RemovalOf(Member).By);
                {
                    var assertedValue = s.ChatMembers();
                    TestAssert.WithMessage(() => Assert.DoesNotContain(Member, assertedValue), "removed from the conference text");
                }
                Assert.Contains<(UUID To, UUID Session, string Reason)>(r.Events.Closes, x => x.To == Member && x.Session == conf);

                Run(r, $"voice call remove {conf} {Member}");
                Assert.Contains("already removed", c.Lines.Last());

                Run(r, $"voice call readmit {conf} {Member}");
                {
                    var assertedValue = c.Lines.Last();
                    Assert.Contains("may join call", assertedValue);
                    Assert.Contains("not seated", assertedValue);
                }
                Assert.True(r.Sessions.Accept(conf, Member, r.Scene.RegionInfo.RegionID).Ok);

                Run(r, $"voice call readmit {conf} {Member}");
                Assert.Contains("was not removed", c.Lines.Last());
            }
        }

        [Fact(Timeout = 60000)]
        public void VoiceCallRemove_OfAGroupMember_LeavesTheGroupsText()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (Region r, Console c) = Rig();
            using (r)
            {
                GroupCallOfTwo(r);
                IGroupChatGate gate = r.Scene.RequestModuleInterface<IGroupChatGate>();

                Run(r, $"voice call remove {GroupId} {Member}");

                Assert.Contains("removed from call", c.Lines.Last());
                Assert.Equal("removed from the console", r.Sessions.Get(GroupId).RemovalOf(Member).Reason);
                Assert.True(r.Sessions.Get(GroupId).Find(Member).InChat);
                Assert.True(gate.MaySend(GroupId, Member));
                Assert.Equal((int)HttpStatusCode.Forbidden, r.Call(Member));
            }
        }

        // ---- bad input --------------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void BadInputIsAnsweredWithAMessage_AndChangesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

                Assert.Equal(10, c.Lines.Count);
                Assert.True(c.Lines.All(l => l.Length > 0 && !l.StartsWith("Done")), c.Text);
                Assert.StartsWith("No such call", c.Lines[1]);
                Assert.Contains("not moderated", c.Lines[7]);
                {
                    var assertedValue = r.Sessions.Get(GroupId).ModerationGeneration;
                    TestAssert.WithMessage(() => Assert.Equal(generation, assertedValue), "nothing was changed");
                }
                Assert.Empty(r.Sessions.Get(GroupId).Removed);
            }
        }
    }
}
