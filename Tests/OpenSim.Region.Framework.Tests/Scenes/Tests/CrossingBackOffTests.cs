/*
 * Copyright (c) Legion Builds
 * Copyright (c) 2026 Legion Builds
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The memory of a failed crossing: one avatar, one region, 60 seconds.
/// </summary>
public class CrossingBackOffTests
{
    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset m_now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => m_now;
        public void Advance(TimeSpan span) => m_now += span;
    }

    private static readonly UUID AvatarA = TestHelpers.ParseTail(0x1);
    private static readonly UUID AvatarB = TestHelpers.ParseTail(0x2);
    private const ulong RegionN = 1001UL << 40;
    private const ulong RegionM = 1002UL << 40;

    [Fact]
    public void NothingIsBlockedUntilACrossingFails()
    {
        CrossingBackOff backOff = new(new TestClock());
        Assert.False(backOff.IsBlocked(AvatarA, RegionN));
    }

    [Fact]
    public void AFailedCrossingBlocksThatAvatarIntoThatRegionOnly()
    {
        CrossingBackOff backOff = new(new TestClock());
        backOff.Mark(AvatarA, RegionN);

        Assert.True(backOff.IsBlocked(AvatarA, RegionN));
        Assert.False(backOff.IsBlocked(AvatarB, RegionN));
        Assert.False(backOff.IsBlocked(AvatarA, RegionM));
    }

    [Fact]
    public void TheBlockEndsAfterSixtySeconds()
    {
        TestClock clock = new();
        CrossingBackOff backOff = new(clock);
        backOff.Mark(AvatarA, RegionN);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(backOff.IsBlocked(AvatarA, RegionN));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(backOff.IsBlocked(AvatarA, RegionN));
        Assert.Equal(TimeSpan.FromSeconds(60), CrossingBackOff.Period);
    }

    [Fact]
    public void AnotherFailureRestartsThePeriodAndClearEndsIt()
    {
        TestClock clock = new();
        CrossingBackOff backOff = new(clock);
        backOff.Mark(AvatarA, RegionN);
        clock.Advance(TimeSpan.FromSeconds(50));
        backOff.Mark(AvatarA, RegionN);
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True(backOff.IsBlocked(AvatarA, RegionN));

        backOff.Clear(AvatarA, RegionN);
        Assert.False(backOff.IsBlocked(AvatarA, RegionN));
    }
}
