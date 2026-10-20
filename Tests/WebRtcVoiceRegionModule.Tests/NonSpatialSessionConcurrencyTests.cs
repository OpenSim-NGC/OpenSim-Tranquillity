/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A voice session's member table is changed under the store's lock, but the session handed out by
 * Store.Get is read by callers that do not hold it (a log line's seat count, a ring's member walk).
 * Those reads enumerate the table while another request adds a member, which throws
 * "Collection was modified". These tests read a session from several threads while many
 * requests join it.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialSessionConcurrencyTests
    {
        private static OSDMap Call(UUID group) => new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(group) };

        private static (GroupVoicePolicy, NonSpatialVoiceSessionEngine) Setup(HashSet<(UUID, UUID)> members, int cap)
        {
            GroupVoicePolicy p = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = true,
                Cap = cap,
                IsMember = (a, g) => members.Contains((a, g)),
                Powers = (a, g) => GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice,
            };
            NonSpatialVoiceSessionEngine e = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), p.ToAdmissionOrNull() },
                "example-grid");
            return (p, e);
        }

        [Test]
        public void ReadingASessionWhileMembersJoin_NeverSeesTheTableChange()
        {
            const int Agents = 400;
            UUID grp = UUID.Random();
            UUID[] agents = Enumerable.Range(0, Agents).Select(n => UUID.Random()).ToArray();
            HashSet<(UUID, UUID)> members = new HashSet<(UUID, UUID)>(agents.Select(a => (a, grp)));
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e) = Setup(members, Agents);

            // The first caller creates the session; the rest join it while readers walk it.
            GroupVoiceChatSession.TryHandleCall(Call(grp), agents[0], p, e, RegionA, out _);

            ConcurrentQueue<Exception> failures = new ConcurrentQueue<Exception>();
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task[] readers = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        NonSpatialVoiceSession s = e.Store.Get(grp);
                        _ = s.SeatsHeld;
                        _ = s.IsDead;
                        _ = s.ChatMembers().Count;
                        foreach (NonSpatialMember m in s.Members) _ = m.AgentId;
                        _ = s.Find(agents[Agents - 1]);
                    }
                    catch (Exception ex) { failures.Enqueue(ex); }
                }
            })).ToArray();

            int admitted = 1;
            System.Threading.Tasks.Parallel.ForEach(agents.Skip(1), new ParallelOptions { MaxDegreeOfParallelism = 8 }, a =>
            {
                GroupVoiceChatSession.TryHandleCall(Call(grp), a, p, e, RegionA, out ChatSessionOutcome o);
                if (o is not null && o.Status == System.Net.HttpStatusCode.OK) Interlocked.Increment(ref admitted);
            });
            stop.Cancel();
            Task.WaitAll(readers);

            Assert.That(failures.Select(f => f.GetType().Name + ": " + f.Message).Distinct().ToList(), Is.Empty,
                "a reader of a live session must not fail while members join");
            Assert.That(e.Store.Get(grp).SeatsHeld, Is.EqualTo(admitted));
        }

        [Test]
        public void ManyGroupsStartedAtOnce_EveryCallAnswers()
        {
            const int Groups = 40, PerGroup = 25;
            UUID[] groups = Enumerable.Range(0, Groups).Select(n => UUID.Random()).ToArray();
            List<(UUID agent, UUID group)> calls = new List<(UUID, UUID)>();
            HashSet<(UUID, UUID)> members = new HashSet<(UUID, UUID)>();
            foreach (UUID g in groups)
                for (int i = 0; i < PerGroup; i++) { UUID a = UUID.Random(); members.Add((a, g)); calls.Add((a, g)); }
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e) = Setup(members, PerGroup);

            ConcurrentQueue<Exception> failures = new ConcurrentQueue<Exception>();
            System.Threading.Tasks.Parallel.ForEach(calls.OrderBy(n => Guid.NewGuid()), new ParallelOptions { MaxDegreeOfParallelism = 16 }, c =>
            {
                try { GroupVoiceChatSession.TryHandleCall(Call(c.group), c.agent, p, e, RegionA, out _); }
                catch (Exception ex) { failures.Enqueue(ex); }
            });

            Assert.That(failures.Select(f => f.GetType().Name + ": " + f.Message).Distinct().ToList(), Is.Empty);
        }
    }
}
