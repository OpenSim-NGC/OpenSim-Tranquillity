/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The sim makes a room exist by ASKING THE MIXER, every time; it keeps no process-wide "room exists" hint.
 * The mixer's 60 s empty-room grace destroys rooms without telling the sim, so any cached answer goes
 * stale, and a stale one makes the viewer's join fail. The cases pinned here:
 *   H1  a room this process created and the mixer has since destroyed is created again by the ensure;
 *   H2  a room that exists is asked for anyway - already-exists is success - and RoomExists fires exactly once;
 *   H3  a create that fails proves nothing: RoomExists does not fire and the unknown_room backoff is not reset.
 * There is deliberately no inline same-handle retry of a join answered with 485: Janus core refuses a
 * second JSEP join on the same handle with error 490. CreateBeforeJoinTests covers the real provision path
 * against a fake gateway that models that rule. The 300 s backoff cap is tested in ConnectorRoomTests.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class RoomHintTests
    {
        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            return new UUID(b, 0);
        }

        /// The mixer, as far as rooms go: create is idempotent (already-exists answers 486, which the sim treats as
        /// success), and the grace destroy happens behind the sim's back.
        private sealed class FakeMixer
        {
            private readonly object _lock = new object();
            public readonly HashSet<int> Rooms = new HashSet<int>();
            public int Creates;
            public bool FailCreates;

            public Task<JanusRoom> Create(int room)
            {
                lock (_lock)
                {
                    Creates++;
                    if (FailCreates)
                        return Task.FromResult<JanusRoom>(null);   // inconclusive / error: CreateRoom answers null
                    Rooms.Add(room);                                // "created", or 486 already exists: both a room
                    return Task.FromResult(new JanusRoom(null, room));
                }
            }

            public void GraceDestroy(int room) { lock (_lock) Rooms.Remove(room); }
        }

        private static Func<Task<JanusRoom>> CreateOf(FakeMixer m, int room) => () => m.Create(room);

        /// <summary>H1: this process created the room, the mixer has since destroyed it. The ensure creates it again -
        /// the failure a stale hint of an earlier build caused.</summary>
        [Fact(Timeout = 60000)]
        public async Task H1_ARoomThisProcessCreated_DestroyedByTheMixer_TheEnsureCreatesItAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            const int room = 880101;
            var m = new FakeMixer();
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room));
            m.GraceDestroy(room);
            int before = m.Creates;

            JanusRoom ensured = await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room));

            {
                var assertedValue = m.Creates;
                TestAssert.WithMessage(() => Assert.Equal(before + 1, assertedValue), "the ensure asked the mixer to create");
            }
            {
                var assertedValue = m.Rooms;
                TestAssert.WithMessage(() => Assert.Contains(room, assertedValue), "and the mixer has the room now");
            }
            Assert.Equal(room, ensured?.RoomId);
        }

        /// <summary>H2: the room is really there. Asking again is not an error - already-exists is success - and
        /// RoomExists fires exactly once.</summary>
        [Fact(Timeout = 60000)]
        public async Task H2_EnsureWithTheRoomPresent_NoError_RoomExistsFiresOnce()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            const int room = 880201;
            var m = new FakeMixer();
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room));   // really exists
            int before = m.Creates, fired = 0;

            int? ensured = ConnectorRoomResolver.EnsureAndProve(
                () => JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room)).Result?.RoomId,
                r => fired++);

            {
                var assertedValue = ensured;
                TestAssert.WithMessage(() => Assert.Equal(room, assertedValue), "no error: already-exists is success");
            }
            {
                var assertedValue = m.Creates;
                TestAssert.WithMessage(() => Assert.Equal(before + 1, assertedValue), "the mixer was asked, even right after a create");
            }
            {
                var assertedValue = fired;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "RoomExists fired exactly once");
            }
        }

        /// <summary>H3: the create fails. Nothing is proven, so RoomExists does NOT fire and the backoff is NOT reset.</summary>
        [Fact(Timeout = 60000)]
        public async Task H3_AFailedCreate_FiresNoRoomExists_AndLeavesTheBackoff()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            const int room = 880301;
            var m = new FakeMixer();
            await JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room));
            m.GraceDestroy(room);
            m.FailCreates = true;

            long clock = 10_000;
            var auth = new VisAuthority(0x0000018f00000001UL, "h3", () => clock);
            UUID l = Id(1);
            auth.OnBatchOutcome(room, VisOp.Replace, auth.NextGeneration(room), new List<UUID> { l }, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });
            Assert.False(auth.CanArmNow(l), "precondition: backing off");
            int fired = 0;

            int? ensured = ConnectorRoomResolver.EnsureAndProve(
                () => JanusAudioBridge.SelectRoomCoalesced(room, CreateOf(m, room)).Result?.RoomId,
                r => { fired++; auth.RoomExists(r); });

            {
                var assertedValue = ensured;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a failed create is not a room");
            }
            {
                var assertedValue = fired;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "RoomExists did not fire");
            }
            Assert.False(auth.CanArmNow(l), "the backoff was not reset");
        }
    }
}
