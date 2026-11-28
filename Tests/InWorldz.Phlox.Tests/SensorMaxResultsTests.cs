/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// With no SensorMaxResults in [InWorldz.Phlox], a sensor reports up to 32 objects, Second Life's limit since its
/// server release 2024-03-18 (https://wiki.secondlife.com/wiki/LlSensor), as YEngine's does.
/// </summary>
// No process-wide state: each test has its own scene, so the class runs in parallel.
public class SensorMaxResultsTests
{
    [Fact]
    public void ASensorWithNoConfiguredLimitReportsTheNearest32Of40Targets()
    {
        using var h = new SchedulerHarness();
        for (int i = 0; i < 40; i++)
        {
            var target = SceneHelpers.AddSceneObject(h.Scene, "Target", UUID.Random());
            target.AbsolutePosition = h.Prim.AbsolutePosition + new Vector3(i % 8, 2 + i / 8, 0);
        }

        h.RezScript(@"default {
            state_entry() { llSensor(""Target"", NULL_KEY, PASSIVE, 20.0, PI); }
            sensor(integer n) { llSay(0, ""sensed "" + (string)n); }
            no_sensor() { llSay(0, ""sensed none""); }
        }");

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("sensed ", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        Assert.Equal("sensed 32", h.Said.First(s => s.StartsWith("sensed ", StringComparison.Ordinal)));
    }
}
