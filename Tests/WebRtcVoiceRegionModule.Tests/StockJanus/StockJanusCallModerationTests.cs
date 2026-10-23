/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in integration tests (see StockJanusRegionTests.cs): a group call's voice moderation, heard. Real WebRTC peers
 * each play a tone into the call, and each measures what it receives. The same tests run against the stock
 * AudioBridge (WEBRTC_JANUS_TEST_PLUGIN unset) and against the slvoice mixer (janus.plugin.slvoice): the region asks
 * for the mute the plugin has, and the audio is what decides.
 *
 * The moderator and the members are made by a fake groups service on the scene: the officer holds Moderate Group
 * Chat, the others only the right to be in the call.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Interfaces;
using osWebRtcVoice.NonSpatial;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    // The assembly-wide 10 s backstop is too short for a session with a real gateway and real peers.
#pragma warning disable CS0618
    [TestFixture, NUnit.Framework.Timeout(300000)]
#pragma warning restore CS0618
    public class StockJanusCallModerationTests
    {
        private static readonly UUID Officer = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Member = new UUID("b0b00000-0000-4000-8000-000000000002");
        private static readonly UUID Late = new UUID("c0de0000-0000-4000-8000-000000000003");
        private static readonly UUID Group = new UUID("9a0a0000-0000-4000-8000-0000000000aa");
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
        private const double OfficerHz = 440, MemberHz = 880, LateHz = 1320;

        private readonly RawJanus _janus = new();

        [OneTimeSetUp]
        public void RequireGateway() => StockJanusEnvironment.IgnoreUnlessConfigured();

        private static StockRegion NewRegion()
        {
            StockRegion region = StockRegion.Create((voice, _) =>
            {
                voice.Set("GroupVoiceEnabled", "true");
                voice.Set("CallModerationEnabled", "true");
                voice.Set("CallModerationRevalidateSeconds", "0");
            });
            CallModerationModuleTests.FakeGroups.Powers.Clear();
            ulong inCall = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;
            CallModerationModuleTests.FakeGroups.Powers[Officer] = inCall | CallModerationPolicy.PowerModerateChat;
            CallModerationModuleTests.FakeGroups.Powers[Member] = inCall;
            CallModerationModuleTests.FakeGroups.Powers[Late] = inCall;
            region.Scene.RegisterModuleInterface<IGroupsModule>(
                DispatchProxy.Create<IGroupsModule, CallModerationModuleTests.FakeGroups>());
            foreach (UUID a in new[] { Officer, Member, Late })
                region.AddAvatar(a, StockRegion.InEstateChannelParcel);
            return region;
        }

        // The stock AudioBridge reports its participants to the gateway's API; the mixer's report is read differently,
        // so for it the peers are given a moment to be mixed in.
        private async Task InRoom(int room, int count)
        {
            if (StockJanusEnvironment.PluginName == "janus.plugin.audiobridge")
                Assert.That(await _janus.WaitForParticipantsAsync(room, count, Settle), Is.True);
            else
                await Task.Delay(2500);
        }

        private static OSDMap Chat(string method, OSDMap @params = null)
        {
            var m = new OSDMap
            {
                ["method"] = OSD.FromString(method),
                ["session-id"] = OSD.FromUUID(Group),
                ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc") },
            };
            if (@params is not null) m["params"] = @params;
            return m;
        }

        private static OSDMap MuteUpdate(UUID target, bool muted) => Chat("mute update", new OSDMap
        {
            ["agent_id"] = OSD.FromUUID(target),
            ["mute_info"] = new OSDMap { ["voice"] = OSD.FromBoolean(muted) },
        });

        private static OSDMap MuteAll(bool muted) => Chat("session update", new OSDMap
        {
            ["update_info"] = new OSDMap { ["moderated_mode"] = new OSDMap { ["voice"] = OSD.FromBoolean(muted) } },
        });

        /// <summary>The member calls the group (taking a seat) and joins the room with a tone.</summary>
        private static async Task<Joined> JoinCall(StockRegion region, UUID agent, double hz)
        {
            CapAnswer call = await region.ChatSessionAsync(agent, Chat("call"));
            Assert.That(call.Status, Is.EqualTo(200), "call: " + call.Body);
            var creds = (OSDMap)call.Body["voice_credentials"];
            string channel = creds["channel_uri"].AsString(), token = creds["channel_credentials"].AsString();
            Joined j = await region.JoinAsync(agent, o => StockRegion.MultiagentBody(o, channel, token), hz);
            Assert.That(j.Answer.Provisioned, Is.True, "joined: " + j.Answer.Body);
            return j;
        }

        private static async Task<List<Measurement>> Hear(Joined listener, params double[] hz)
        {
            await Task.Delay(1000);   // let the mix settle after a change
            List<Measurement> m = await listener.Peer.MeasureWindowsAsync(4, 0.5, hz);
            TestContext.Out.WriteLine($"{listener.Agent} heard: " + Measurement.Describe(m, hz));
            return m;
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task AMutedMembersAudioStopsReachingTheOthers_AndUnmuteRestoresIt()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using StockRegion region = NewRegion();
            using Joined officer = await JoinCall(region, Officer, OfficerHz);
            using Joined member = await JoinCall(region, Member, MemberHz);
            using Joined late = await JoinCall(region, Late, LateHz);
            await InRoom(officer.Answer.Room, 3);
            await Task.Delay(1500);
            List<Measurement> before = await Hear(late, OfficerHz, MemberHz);
            Assert.That(Measurement.MedianShare(before, MemberHz), Is.GreaterThan(0.3), "precondition: the member is heard");
            Assert.That(Measurement.MedianShare(before, OfficerHz), Is.GreaterThan(0.3), "precondition: the officer is heard");

            CapAnswer muted = await region.ChatSessionAsync(Officer, MuteUpdate(Member, true));
            Assert.That(muted.Status, Is.EqualTo(200));
            List<Measurement> afterMute = await Hear(late, OfficerHz, MemberHz);
            Assert.That(Measurement.MedianShare(afterMute, MemberHz), Is.LessThan(0.05), "the muted member is no longer heard");
            Assert.That(Measurement.MedianShare(afterMute, OfficerHz), Is.GreaterThan(0.5), "the others still are");
            List<Measurement> officerHears = await Hear(officer, MemberHz, LateHz);
            Assert.That(Measurement.MedianShare(officerHears, MemberHz), Is.LessThan(0.05), "nor by the moderator");
            Assert.That(Measurement.MedianShare(officerHears, LateHz), Is.GreaterThan(0.5));

            CapAnswer unmuted = await region.ChatSessionAsync(Officer, MuteUpdate(Member, false));
            Assert.That(unmuted.Status, Is.EqualTo(200));
            List<Measurement> afterUnmute = await Hear(late, OfficerHz, MemberHz);
            Assert.That(Measurement.MedianShare(afterUnmute, MemberHz), Is.GreaterThan(0.3), "unmute restores the member");

            CapAnswer refused = await region.ChatSessionAsync(Late, MuteUpdate(Member, true));
            Assert.That(refused.Status, Is.EqualTo(403), "a member without the power cannot mute");
            await region.LogoutAsync(officer);
            await region.LogoutAsync(member);
            await region.LogoutAsync(late);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task TwoMembersWithTheSameAvatarName_MutingOneLeavesTheOtherHeard()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using StockRegion region = NewRegion();
            // Every avatar a test scene makes is called the same thing, so the three here share one name. A mute names
            // the agent id the region set at join, never the name.
            Assert.That(region.Scene.GetScenePresence(Member).Name, Is.EqualTo(region.Scene.GetScenePresence(Late).Name),
                        "precondition: the two members share an avatar name");
            using Joined officer = await JoinCall(region, Officer, OfficerHz);
            using Joined member = await JoinCall(region, Member, MemberHz);
            using Joined late = await JoinCall(region, Late, LateHz);
            await InRoom(officer.Answer.Room, 3);
            await Task.Delay(1500);
            List<Measurement> before = await Hear(officer, MemberHz, LateHz);
            Assert.That(Measurement.MedianShare(before, MemberHz), Is.GreaterThan(0.2), "precondition: the first is heard");
            Assert.That(Measurement.MedianShare(before, LateHz), Is.GreaterThan(0.2), "precondition: the second is heard");

            CapAnswer muted = await region.ChatSessionAsync(Officer, MuteUpdate(Member, true));
            Assert.That(muted.Status, Is.EqualTo(200));

            List<Measurement> after = await Hear(officer, MemberHz, LateHz);
            Assert.That(Measurement.MedianShare(after, MemberHz), Is.LessThan(0.05), "the one muted is no longer heard");
            Assert.That(Measurement.MedianShare(after, LateHz), Is.GreaterThan(0.5), "the other, with the same name, still is");
            List<Measurement> lateHears = await Hear(late, OfficerHz, MemberHz);
            Assert.That(Measurement.MedianShare(lateHears, MemberHz), Is.LessThan(0.05));
            Assert.That(Measurement.MedianShare(lateHears, OfficerHz), Is.GreaterThan(0.5));
            await region.LogoutAsync(officer);
            await region.LogoutAsync(member);
            await region.LogoutAsync(late);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task MuteAll_SilencesEveryoneButTheModerator_AndALaterJoinerIsSilent()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using StockRegion region = NewRegion();
            using Joined officer = await JoinCall(region, Officer, OfficerHz);
            using Joined member = await JoinCall(region, Member, MemberHz);
            await InRoom(officer.Answer.Room, 2);
            await Task.Delay(1500);
            Assert.That(Measurement.MedianShare(await Hear(officer, MemberHz), MemberHz), Is.GreaterThan(0.8), "precondition");

            CapAnswer all = await region.ChatSessionAsync(Officer, MuteAll(true));
            Assert.That(all.Status, Is.EqualTo(200));
            List<Measurement> officerHears = await Hear(officer, MemberHz);
            Assert.That(Measurement.MedianRms(officerHears), Is.LessThan(0.005), "the officer hears nobody now");
            List<Measurement> memberHears = await Hear(member, OfficerHz);
            Assert.That(Measurement.MedianShare(memberHears, OfficerHz), Is.GreaterThan(0.8), "the moderator is still heard");

            using Joined late = await JoinCall(region, Late, LateHz);
            await InRoom(officer.Answer.Room, 3);
            List<Measurement> lateIsSilent = await Hear(officer, MemberHz, LateHz);
            Assert.That(Measurement.MedianRms(lateIsSilent), Is.LessThan(0.005), "whoever joins afterwards is silent");
            Assert.That(Measurement.MedianShare(await Hear(late, OfficerHz), OfficerHz), Is.GreaterThan(0.8),
                        "and hears the moderator");

            CapAnswer none = await region.ChatSessionAsync(Officer, MuteAll(false));
            Assert.That(none.Status, Is.EqualTo(200));
            List<Measurement> back = await Hear(officer, MemberHz, LateHz);
            Assert.That(Measurement.MedianShare(back, MemberHz), Is.GreaterThan(0.3), "unmute all restores the member");
            Assert.That(Measurement.MedianShare(back, LateHz), Is.GreaterThan(0.3), "and the later joiner");
            await region.LogoutAsync(officer);
            await region.LogoutAsync(member);
            await region.LogoutAsync(late);
        }
    }
}
