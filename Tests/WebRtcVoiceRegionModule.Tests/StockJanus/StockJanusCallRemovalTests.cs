/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in integration test (see StockJanusRegionTests.cs): removing a member from a group call, on a real gateway with
 * real WebRTC peers. It runs against the stock AudioBridge and against the slvoice mixer. The gateway itself is asked
 * how many participants the room holds, and the peers' audio decides whether the removed member is still heard.
 */

using System;
using System.Collections.Generic;
using System.Linq;
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
    public class StockJanusCallRemovalTests
    {
        private static readonly UUID Officer = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Member = new UUID("b0b00000-0000-4000-8000-000000000002");
        private static readonly UUID Late = new UUID("c0de0000-0000-4000-8000-000000000003");
        private static readonly UUID Group = new UUID("9a0a0000-0000-4000-8000-0000000000aa");
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
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

        private static OSDMap Chat(string method) => new OSDMap
        {
            ["method"] = OSD.FromString(method),
            ["session-id"] = OSD.FromUUID(Group),
            ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc") },
        };

        private static async Task<(Joined Peer, string Channel, string Token)> JoinCall(StockRegion region, UUID agent, double hz)
        {
            CapAnswer call = await region.ChatSessionAsync(agent, Chat("call"));
            Assert.That(call.Status, Is.EqualTo(200), "call: " + call.Body);
            var creds = (OSDMap)call.Body["voice_credentials"];
            string channel = creds["channel_uri"].AsString(), token = creds["channel_credentials"].AsString();
            Joined j = await region.JoinAsync(agent, o => StockRegion.MultiagentBody(o, channel, token), hz);
            Assert.That(j.Answer.Provisioned, Is.True, "joined: " + j.Answer.Body);
            return (j, channel, token);
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
        public async Task ARemovedMembersMediaLeavesTheRoom_TheyAreRefusedBack_AndTheCallEndingEndsTheRemoval()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using StockRegion region = NewRegion();
            (Joined officer, _, _) = await JoinCall(region, Officer, OfficerHz);
            (Joined member, string channel, string token) = await JoinCall(region, Member, MemberHz);
            (Joined late, _, _) = await JoinCall(region, Late, LateHz);
            using (officer) using (member) using (late)
            {
                int room = officer.Answer.Room;
                Assert.That(await _janus.WaitForParticipantsAsync(room, 3, Settle), Is.True, "three participants in the room");
                await Task.Delay(1500);
                List<Measurement> before = await Hear(officer, MemberHz, LateHz);
                Assert.That(Measurement.MedianShare(before, MemberHz), Is.GreaterThan(0.2), "precondition: the member is heard");

                WebRtcVoiceRegionModule.CallRemovalResult removed = region.Region.RemoveFromCall(Group, Officer, Member, "opt-in test");

                Assert.That(removed.Ok && removed.Changed, Is.True, removed.Decision);
                Assert.That(await _janus.WaitForParticipantsAsync(room, 2, Settle), Is.True, "the gateway holds two participants now");
                List<Measurement> after = await Hear(officer, MemberHz, LateHz);
                Assert.That(Measurement.MedianShare(after, MemberHz), Is.LessThan(0.05), "the removed member is no longer heard");
                Assert.That(Measurement.MedianShare(after, LateHz), Is.GreaterThan(0.5), "the others still are");
                Assert.That(region.Queue.Closes, Has.Some.Matches<(UUID To, UUID Session, string Reason)>(
                    c => c.To == Member && c.Session == Group && c.Reason == "close_on_no_ability"), "their viewer was told to close the call");

                // The viewer that keeps trying: every provision, and the call itself, is refused.
                for (int i = 0; i < 20; i++)
                {
                    CapAnswer again = await region.ProvisionAsync(Member, StockRegion.MultiagentBody(member.Peer.Offer, channel, token));
                    Assert.That(again.Status, Is.EqualTo(403), $"retry {i}");
                }
                Assert.That((await region.ChatSessionAsync(Member, Chat("call"))).Status, Is.EqualTo(403), "a new call");
                Assert.That(await _janus.ParticipantCountAsync(room), Is.EqualTo(2), "and none of it put them back in the room");

                // The call ends when the last seat goes; the removal goes with it.
                await region.LogoutAsync(officer);
                await region.LogoutAsync(late);
                Assert.That(await _janus.WaitForParticipantsAsync(room, 0, Settle), Is.True, "the room is empty");
                await Task.Delay(6000);   // past the viewer-retry cache (RefusalCacheSeconds, 5)
                Assert.That((await region.ChatSessionAsync(Member, Chat("call"))).Status, Is.EqualTo(200), "a new call is allowed once the call has ended");
            }
        }
    }
}
