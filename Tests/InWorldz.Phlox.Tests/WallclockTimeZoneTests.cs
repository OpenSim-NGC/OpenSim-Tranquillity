/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetWallclock answers the seconds since midnight in the zone named by GetWallclockTimeZone, as YEngine's does
/// (SL: https://wiki.secondlife.com/wiki/LlGetWallclock, Pacific time). [InWorldz.Phlox] sets it, else [YEngine]'s
/// setting of the same name, else UTC. The answer is truncated to whole seconds.
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state (the instant is set on each script's API,
// and the one zone-id cache is keyed by id, with a unique id in the unknown-zone test), so the class runs in parallel.
public class WallclockTimeZoneTests
{
    private static readonly DateTimeOffset Summer = new(2026, 7, 15, 19, 30, 45, TimeSpan.Zero);   // PDT: 12:30:45
    private static readonly DateTimeOffset Winter = new(2026, 1, 15, 19, 30, 45, TimeSpan.Zero);   // PST: 11:30:45

    private static InventoryGivesRig Rig(string phlox, string yengine)
        => new(configure: cfg =>
        {
            if (phlox != null) { if (cfg.Configs["InWorldz.Phlox"] == null) cfg.AddConfig("InWorldz.Phlox"); cfg.Configs["InWorldz.Phlox"].Set("GetWallclockTimeZone", phlox); }
            if (yengine != null) { if (cfg.Configs["YEngine"] == null) cfg.AddConfig("YEngine"); cfg.Configs["YEngine"].Set("GetWallclockTimeZone", yengine); }
        });

    private static float At(InventoryGivesRig r, DateTimeOffset utc)
    {
        LSLSystemAPI api = r.Api;
        api.WallclockUtcNow = () => utc;
        return api.llGetWallclock();
    }

    [Theory]
    [InlineData(true, 45045f)]    // 12:30:45 Pacific daylight time
    [InlineData(false, 41445f)]   // 11:30:45 Pacific standard time
    public void PhloxSettingGivesPacificSeconds(bool summer, float expected)
    {
        using var r = Rig("America/Los_Angeles", null);
        Assert.Equal(expected, At(r, summer ? Summer : Winter));
    }

    [Fact]
    public void YEngineSettingAloneIsFollowed()
    {
        using var r = Rig(null, "America/Los_Angeles");
        Assert.Equal(45045f, At(r, Summer));
    }

    [Fact]
    public void PhloxSettingWinsOverYEngine()
    {
        using var r = Rig("America/Los_Angeles", "Asia/Tokyo");
        Assert.Equal(45045f, At(r, Summer));
    }

    [Fact]
    public void NoSettingStaysOnUtc()
    {
        using var r = Rig(null, null);
        Assert.Equal(70245f, At(r, Summer));   // 19:30:45 UTC
    }

    [Fact]
    public void FractionalSecondsAreTruncatedNotRounded()
    {
        using var r = Rig("America/Los_Angeles", null);
        Assert.Equal(45045f, At(r, Summer.AddMilliseconds(750)));
    }

    [Fact]
    public void AnUnknownZoneIsUtcWithOneWarning()
    {
        string id = "Not/AZone-" + Guid.NewGuid().ToString("N");
        int warnings = 0;
        for (int i = 0; i < 50; i++)
            Assert.Null(WallclockZone.Resolve(id, _ => warnings++));
        Assert.Equal(1, warnings);

        using var r = Rig(id, null);
        for (int i = 0; i < 5; i++)
            Assert.Equal(70245f, At(r, Summer));   // UTC, no script error
    }

    [Theory]
    [InlineData("Pacific Standard Time")]
    [InlineData("America/Los_Angeles")]
    public void WindowsAndIanaIdsBothWork(string id)
    {
        using var r = Rig(id, null);
        Assert.Equal(45045f, At(r, Summer));
    }

    [Theory]
    [InlineData(7, 0, 0, 0f)]       // 00:00:00 Pacific
    [InlineData(7, 0, 1, 1f)]       // just after midnight
    [InlineData(6, 59, 59, 86399f)] // the last second of the day before; never 86400
    public void AroundLocalMidnightStaysInTheDay(int utcHour, int minute, int second, float expected)
    {
        using var r = Rig("America/Los_Angeles", null);
        float v = At(r, new DateTimeOffset(2026, 7, 15, utcHour, minute, second, 900, TimeSpan.Zero));
        Assert.Equal(expected, v);
        Assert.InRange(v, 0f, 86399f);
    }
}
