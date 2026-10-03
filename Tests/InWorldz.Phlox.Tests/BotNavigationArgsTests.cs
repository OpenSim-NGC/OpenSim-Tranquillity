/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What the bot navigation calls hand the bot manager, as Halcyon's LSLSystemAPI checked it (botFollowAvatar,
/// botSetNavigationPoints, botWanderWithin): option keys are integers and values numbers (or a vector for
/// botFollowAvatar), else BOT_ERROR or no call; a number among the navigation points is BOT_TRAVELMODE_WAIT's
/// duration, passed as &lt;seconds, 0, 0&gt;; a point of any other type, or a movement type that is not an integer, cancels
/// the call. botGiveInventory says "Could not parse key ..." for a bad destination, as Halcyon did.
/// </summary>
// No process-wide state: each test has its own scene and fake bot manager, so the class runs in parallel.
public class BotNavigationArgsTests
{
    private const int BOT_ERROR = -3, BOT_SUCCESS = 0;

    private sealed class Calls
    {
        public readonly List<(string Name, object[] Args)> All = new();
        public (string Name, object[] Args)? Last(string name) => All.LastOrDefault(c => c.Name == name) is var c && c.Name != null ? c : null;
    }

    private static (ApiCallRig Rig, Calls Calls) Rig()
    {
        var rig = new ApiCallRig();
        var calls = new Calls();
        rig.H.Scene.RegisterModuleInterface<IBotManager>(Fake<IBotManager>.Create((m, a) =>
        {
            lock (calls.All) calls.All.Add((m.Name, a));
            if (m.Name == nameof(IBotManager.StartFollowingAvatar)) return BotMovementResult.Success;
            if (m.Name == nameof(IBotManager.CheckPermission)) return true;
            return null;
        }));
        return (rig, calls);
    }

    private static LSLList L(params object[] items) => new LSLList(items);

    [Fact]
    public void ANumberAmongTheNavigationPointsIsTheWaitDuration()
    {
        var (r, calls) = Rig();
        using (r)
        {
            UUID bot = UUID.Random();
            r.Api.botSetNavigationPoints(bot.ToString(), L(new Vector3(10, 10, 30), 5.0f, 7, new Vector3(20, 20, 30)),
                L(0, 4, 4, 0), new LSLList());

            var call = calls.Last(nameof(IBotManager.SetBotNavigationPoints));
            Assert.NotNull(call);
            var points = (List<Vector3>)call.Value.Args[1];
            Assert.Equal(4, points.Count);
            Assert.Equal(5f, points[1].X);
            Assert.Equal(0f, points[1].Y);
            Assert.Equal(7f, points[2].X);
            Assert.Equal(new Vector3(20, 20, 30), points[3]);
        }
    }

    [Fact]
    public void ANavigationPointOfAnotherTypeOrANonIntegerMovementTypeCancelsTheCall()
    {
        var (r, calls) = Rig();
        using (r)
        {
            UUID bot = UUID.Random();
            r.Api.botSetNavigationPoints(bot.ToString(), L(new Vector3(10, 10, 30), "here"), L(0, 0), new LSLList());
            r.Api.botSetNavigationPoints(bot.ToString(), L(new Vector3(10, 10, 30)), L(1.5f), new LSLList());
            Assert.Null(calls.Last(nameof(IBotManager.SetBotNavigationPoints)));
        }
    }

    [Fact]
    public void BadlyTypedOptionsAreRefused()
    {
        var (r, calls) = Rig();
        using (r)
        {
            UUID bot = UUID.Random(), av = UUID.Random();

            // A string key, then a string value: BOT_ERROR and no call
            Assert.Equal(BOT_ERROR, r.Api.botFollowAvatar(bot.ToString(), av.ToString(), L("1", 2.0f)));
            Assert.Equal(BOT_ERROR, r.Api.botFollowAvatar(bot.ToString(), av.ToString(), L(1, "far")));
            Assert.Null(calls.Last(nameof(IBotManager.StartFollowingAvatar)));
            // A vector value is one botFollowAvatar takes
            Assert.Equal(BOT_SUCCESS, r.Api.botFollowAvatar(bot.ToString(), av.ToString(), L(1, new Vector3(1, 2, 3))));
            Assert.NotNull(calls.Last(nameof(IBotManager.StartFollowingAvatar)));

            r.Api.botSetNavigationPoints(bot.ToString(), L(new Vector3(10, 10, 30)), L(0), L(1, new Vector3(1, 2, 3)));
            r.Api.botSetNavigationPoints(bot.ToString(), L(new Vector3(10, 10, 30)), L(0), L(2.5f, 1));
            Assert.Null(calls.Last(nameof(IBotManager.SetBotNavigationPoints)));
            r.Api.botSetNavigationPoints(bot.ToString(), L(new Vector3(10, 10, 30)), L(0), L(1, 2.5f));
            Assert.NotNull(calls.Last(nameof(IBotManager.SetBotNavigationPoints)));

            r.Api.botWanderWithin(bot.ToString(), new Vector3(10, 10, 20), 5, 5, L(1, "x"));
            r.Api.botWanderWithin(bot.ToString(), new Vector3(10, 10, 20), 5, 5, L(1, new Vector3(1, 1, 1)));
            Assert.Null(calls.Last(nameof(IBotManager.WanderWithin)));
            r.Api.botWanderWithin(bot.ToString(), new Vector3(10, 10, 20), 5, 5, L(1, 3));
            Assert.NotNull(calls.Last(nameof(IBotManager.WanderWithin)));
        }
    }

    [Fact]
    public void BotGiveInventorySaysABadDestinationKey()
    {
        var (r, calls) = Rig();
        using (r)
        {
            r.Api.botGiveInventory(UUID.Random().ToString(), "not a key", "thing");
            Assert.True(r.H.PumpUntil(() => r.H.SaidOn.Any(s => s.Channel == 0 && s.Message == "Could not parse key not a key")),
                string.Join(" | ", r.H.SaidOn.Select(s => s.Channel + ":" + s.Message)));
            Assert.Null(calls.Last(nameof(IBotManager.GiveInventoryObject)));
        }
    }
}
