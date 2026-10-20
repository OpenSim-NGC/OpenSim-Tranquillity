/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The refusal log gate counts what it holds back, so the line it lets through can say how many it stands for, and it
 * keeps a separate allowance for each kind of refusal.
 */

using OpenMetaverse;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class RefusalLogGateCountTests
    {
        private static readonly UUID Agent = new UUID("aaaaaaaa-1111-1111-1111-0000000e0072");
        private static readonly UUID Other = new UUID("aaaaaaaa-2222-2222-2222-0000000e0072");
        private static readonly DateTime T0 = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public void TheFirstRefusalPasses_TheRestInTheIntervalAreCounted_AndTheNextLineSaysHowMany()
        {
            RefusalLogGate gate = new RefusalLogGate();

            Assert.That(gate.ShouldLog("kind", Agent, T0, out int first), Is.True);
            Assert.That(first, Is.EqualTo(0));
            for (int i = 1; i <= 40; i++)
                Assert.That(gate.ShouldLog("kind", Agent, T0.AddSeconds(i), out _), Is.False);

            Assert.That(gate.ShouldLog("kind", Agent, T0.AddSeconds(61), out int more), Is.True);
            Assert.That(more, Is.EqualTo(40), "the 40 that were held back");
            Assert.That(gate.ShouldLog("kind", Agent, T0.AddSeconds(62), out _), Is.False);
            Assert.That(gate.ShouldLog("kind", Agent, T0.AddSeconds(125), out int next), Is.True);
            Assert.That(next, Is.EqualTo(1), "the count starts again after each line");
        }

        [Test]
        public void EachKindAndEachKeyHasItsOwnAllowance()
        {
            RefusalLogGate gate = new RefusalLogGate();

            Assert.That(gate.ShouldLog("channel-type", Agent, T0, out _), Is.True);
            Assert.That(gate.ShouldLog("child-agent", Agent, T0, out _), Is.True, "another kind for the same agent");
            Assert.That(gate.ShouldLog("channel-type", Other, T0, out _), Is.True, "another agent for the same kind");
            Assert.That(gate.ShouldLog("channel-type", Agent, T0.AddSeconds(1), out _), Is.False);
        }

        [Test]
        public void TheKeyOnlyForm_BehavesAsItAlwaysDid()
        {
            RefusalLogGate gate = new RefusalLogGate(TimeSpan.FromSeconds(30));

            Assert.That(gate.ShouldLog(Agent, T0), Is.True);
            Assert.That(gate.ShouldLog(Agent, T0.AddSeconds(29)), Is.False);
            Assert.That(gate.ShouldLog(Agent, T0.AddSeconds(31)), Is.True);
        }

        [Test]
        public void PruneForgetsOldKeys_AndTheirCounts()
        {
            RefusalLogGate gate = new RefusalLogGate();
            gate.ShouldLog("kind", Agent, T0, out _);
            gate.ShouldLog("kind", Agent, T0.AddSeconds(1), out _);

            Assert.That(gate.Prune(T0.AddMinutes(10), TimeSpan.FromMinutes(5)), Is.EqualTo(1));
            Assert.That(gate.ShouldLog("kind", Agent, T0.AddMinutes(10), out int more), Is.True);
            Assert.That(more, Is.EqualTo(0));
        }
    }
}
