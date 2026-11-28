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
    public class RefusalLogGateCountTests
    {
        private static readonly UUID Agent = new UUID("aaaaaaaa-1111-1111-1111-0000000e0072");
        private static readonly UUID Other = new UUID("aaaaaaaa-2222-2222-2222-0000000e0072");
        private static readonly DateTime T0 = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        [Fact(Timeout = 60000)]
        public void TheFirstRefusalPasses_TheRestInTheIntervalAreCounted_AndTheNextLineSaysHowMany()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalLogGate gate = new RefusalLogGate();

            Assert.True(gate.ShouldLog("kind", Agent, T0, out int first));
            Assert.Equal(0, first);
            for (int i = 1; i <= 40; i++)
                Assert.False(gate.ShouldLog("kind", Agent, T0.AddSeconds(i), out _));

            Assert.True(gate.ShouldLog("kind", Agent, T0.AddSeconds(61), out int more));
            {
                var assertedValue = more;
                TestAssert.WithMessage(() => Assert.Equal(40, assertedValue), "the 40 that were held back");
            }
            Assert.False(gate.ShouldLog("kind", Agent, T0.AddSeconds(62), out _));
            Assert.True(gate.ShouldLog("kind", Agent, T0.AddSeconds(125), out int next));
            {
                var assertedValue = next;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the count starts again after each line");
            }
        }

        [Fact(Timeout = 60000)]
        public void EachKindAndEachKeyHasItsOwnAllowance()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalLogGate gate = new RefusalLogGate();

            Assert.True(gate.ShouldLog("channel-type", Agent, T0, out _));
            Assert.True(gate.ShouldLog("child-agent", Agent, T0, out _), "another kind for the same agent");
            Assert.True(gate.ShouldLog("channel-type", Other, T0, out _), "another agent for the same kind");
            Assert.False(gate.ShouldLog("channel-type", Agent, T0.AddSeconds(1), out _));
        }

        [Fact(Timeout = 60000)]
        public void TheKeyOnlyForm_BehavesAsItAlwaysDid()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalLogGate gate = new RefusalLogGate(TimeSpan.FromSeconds(30));

            Assert.True(gate.ShouldLog(Agent, T0));
            Assert.False(gate.ShouldLog(Agent, T0.AddSeconds(29)));
            Assert.True(gate.ShouldLog(Agent, T0.AddSeconds(31)));
        }

        [Fact(Timeout = 60000)]
        public void PruneForgetsOldKeys_AndTheirCounts()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalLogGate gate = new RefusalLogGate();
            gate.ShouldLog("kind", Agent, T0, out _);
            gate.ShouldLog("kind", Agent, T0.AddSeconds(1), out _);

            Assert.Equal(1, gate.Prune(T0.AddMinutes(10), TimeSpan.FromMinutes(5)));
            Assert.True(gate.ShouldLog("kind", Agent, T0.AddMinutes(10), out int more));
            Assert.Equal(0, more);
        }
    }
}
