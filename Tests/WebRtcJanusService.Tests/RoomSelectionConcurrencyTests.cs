/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Concurrency/regression tests for room selection (JanusAudioBridge).
 *
 * Live defect: three near-simultaneous ProvisionVoiceAccountRequests from one agent
 * (all region/estate room, parcel -999) raced. Because the AudioBridge (and its room
 * cache) was per viewer session, nothing coalesced the concurrent Janus room creates;
 * one create came back non-486 ("room selection failed") while the retry succeeded.
 *
 * Part 1 (CreateWithRecheck): an inconclusive first create is re-attempted once, so a
 *   cross-process create that won the race resolves to 486 -> reuse.
 * Part 2 (SelectRoomCoalesced): a process-wide per-room lock + existence hint collapse
 *   concurrent same-process creates of one room number to a single Janus create.
 *
 * Both helpers are Func-based (no Janus transport) so they unit-test per the house
 * pattern. Each test uses a distinct room number because the coalescing state is static
 * (process-wide by design).
 */

using System.Threading;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class RoomSelectionConcurrencyTests
    {
        // JanusRoom's ctor only stores the plugin handle (used later by Join/Leave); a
        // null handle is fine for these transport-free tests.
        private static JanusRoom FakeRoom(int id) => new JanusRoom(null, id);

        // --- Part 1: CreateWithRecheck ---

        // Non-486 / inconclusive first attempt, room exists on the retry -> reuse.
        [Test]
        public async Task CreateWithRecheck_InconclusiveThenExists_ReusesOnRetry()
        {
            int attempts = 0;
            Func<Task<JanusRoom>> attempt = () =>
            {
                attempts++;
                // 1st attempt: inconclusive (null). 2nd: room now exists (486 -> reuse).
                return Task.FromResult(attempts == 1 ? null : FakeRoom(4242));
            };

            JanusRoom result = await JanusAudioBridge.CreateWithRecheck(attempt);

            Assert.That(attempts, Is.EqualTo(2), "must re-check exactly once after an inconclusive first attempt");
            Assert.That(result, Is.Not.Null);
            Assert.That(result.RoomId, Is.EqualTo(4242));
        }

        [Test]
        public async Task CreateWithRecheck_FirstSucceeds_NoRetry()
        {
            int attempts = 0;
            Func<Task<JanusRoom>> attempt = () => { attempts++; return Task.FromResult(FakeRoom(1)); };

            JanusRoom result = await JanusAudioBridge.CreateWithRecheck(attempt);

            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public async Task CreateWithRecheck_BothInconclusive_ReturnsNull()
        {
            int attempts = 0;
            Func<Task<JanusRoom>> attempt = () => { attempts++; return Task.FromResult<JanusRoom>(null); };

            JanusRoom result = await JanusAudioBridge.CreateWithRecheck(attempt);

            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(result, Is.Null);
        }

        // --- Part 2: SelectRoomCoalesced ---

        // Every select asks the mixer, so N concurrent selects for one room number send N creates - one
        // at a time through the per-room gate, never overlapping - and the mixer answers one "created" and N-1 486s,
        // all of which are success. The process-wide hint used to make the other racers skip the create; that hint
        // is deleted (a stale one cost a viewer more than five seconds).
        [Test]
        public async Task SelectRoomCoalesced_ConcurrentSameRoom_CreatesSerialized_AllSucceed()
        {
            const int room = 900001;   // distinct per test (static process-wide state)
            int creates = 0, inFlight = 0, maxInFlight = 0;

            Func<Task<JanusRoom>> create = async () =>
            {
                int now = Interlocked.Increment(ref inFlight);
                int seen;
                while ((seen = Volatile.Read(ref maxInFlight)) < now
                       && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen) { }
                Interlocked.Increment(ref creates);
                await Task.Delay(25);          // widen the race window
                Interlocked.Decrement(ref inFlight);
                return FakeRoom(room);
            };

            const int N = 8;
            var tasks = new Task<JanusRoom>[N];
            for (int i = 0; i < N; i++)
                tasks[i] = JanusAudioBridge.SelectRoomCoalesced(room, create);
            JanusRoom[] results = await Task.WhenAll(tasks);

            Assert.That(creates, Is.EqualTo(N), "every select asks the mixer");
            Assert.That(maxInFlight, Is.EqualTo(1), "but never two creates of one room at once");
            Assert.That(results, Has.All.Not.Null);
        }

        // A room this process created is asked for again on the next select - nothing is remembered.
        [Test]
        public async Task SelectRoomCoalesced_AfterACreate_TheNextSelectAsksTheMixerAgain()
        {
            const int room = 900002;
            int creates = 0;
            Func<Task<JanusRoom>> create = () => { Interlocked.Increment(ref creates); return Task.FromResult(FakeRoom(room)); };

            await JanusAudioBridge.SelectRoomCoalesced(room, create);
            await JanusAudioBridge.SelectRoomCoalesced(room, create);

            Assert.That(creates, Is.EqualTo(2));
        }

        // An inconclusive create (null) is returned as null, and the next select simply tries again.
        [Test]
        public async Task SelectRoomCoalesced_FailedCreate_ReturnsNull_AndTheNextSelectTriesAgain()
        {
            const int room = 900003;
            int creates = 0;
            Func<Task<JanusRoom>> failingCreate = () => { Interlocked.Increment(ref creates); return Task.FromResult<JanusRoom>(null); };

            JanusRoom first = await JanusAudioBridge.SelectRoomCoalesced(room, failingCreate);
            JanusRoom second = await JanusAudioBridge.SelectRoomCoalesced(room, failingCreate);

            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null);
            Assert.That(creates, Is.EqualTo(2));
        }
    }
}
