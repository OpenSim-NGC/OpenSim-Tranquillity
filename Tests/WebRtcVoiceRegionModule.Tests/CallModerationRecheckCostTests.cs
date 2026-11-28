/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What the call moderation re-check asks of the session store and the groups service. It runs every
 * CallModerationRevalidateSeconds for every member of a group call who is in this process, so the cost of a pass must
 * grow with the calls and the people in them, not with the questions asked about each person. Counted with fakes.
 */
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CallModerationModuleTests;
using static osWebRtcVoice.Tests.CallRemovalModuleTests;

namespace osWebRtcVoice.Tests
{
    public class CallModerationRecheckCostTests
    {
        // The third agent the module test region already has a presence and the group powers for.
        private static readonly UUID Third = new UUID("33333333-3333-4333-8333-333333333333");

        private static void Tick(Region r)
            => typeof(WebRtcVoiceRegionModule).GetMethod("RevalidateCallFlags", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(r.Module, null);

        private static CountingSessions Count(Region r)
        {
            CountingSessions counting = (CountingSessions)(object)DispatchProxy.Create<INonSpatialSessions, CountingSessions>();
            counting.Inner = r.Sessions;
            typeof(WebRtcVoiceRegionModule).GetField("m_nonSpatial", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(r.Module, (INonSpatialSessions)(object)counting);
            return counting;
        }

        private static Region ACallOfThree()
        {
            Region r = InACall();
            r.Call(Third);
            r.Present(Third);
            return r;
        }

        private static int Groups(string method)
        {
            lock (FakeGroups.Calls) return FakeGroups.Calls.GetValueOrDefault(method);
        }

        [Fact(Timeout = 60000)]
        public void OnePass_ReadsTheSessionOnce_NotOncePerMember()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = ACallOfThree();
            CountingSessions counting = Count(r);

            Tick(r);

            {
                var assertedValue = counting.Calls.GetValueOrDefault(nameof(INonSpatialSessions.Get));
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "three members of one call: one read of the call");
            }
        }

        [Fact(Timeout = 60000)]
        public void OnePass_AsksTheGroupsServiceOnceForEachMember_NotOncePerQuestion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = ACallOfThree();
            lock (FakeGroups.Calls) FakeGroups.Calls.Clear();

            Tick(r);

            {
                var assertedValue = Groups(nameof(IGroupsModule.GetMembershipData));
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "membership: one lookup for each of the three");
            }
            {
                var assertedValue = Groups(nameof(IGroupsModule.GetFullGroupPowers));
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "powers: one lookup for each of the three");
            }
        }

        [Fact(Timeout = 60000)]
        public void ThePassStillDoesWhatItDid_ALostModeratorFlagIsWithdrawn_AndAMemberWhoLeftTheGroupIsLetGo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = ACallOfThree();
            r.Events.Lists.Clear();
            FakeGroups.Powers[Officer] = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice;   // no longer a moderator
            FakeGroups.Powers.Remove(Third);                                                                 // no longer in the group

            Tick(r);

            var flags = r.Events.Lists.SelectMany(l => l.Updates.Select(u => (l.To, u))).Where(x => x.u.agentID == Officer && x.To == Officer).ToList();
            Assert.Single(flags);
            Assert.False(flags[0].u.isModerator);
            Assert.False(r.Sessions.Get(GroupId).Find(Third).HoldsSeat, "let go, as before");
            Assert.True(r.Sessions.Get(GroupId).Find(Member).HoldsSeat);
        }
    }
}
