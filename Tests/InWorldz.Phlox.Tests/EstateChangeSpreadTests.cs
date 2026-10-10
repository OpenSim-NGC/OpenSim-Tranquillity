/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Estate;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An estate change made by llManageEstateAccess reaches the estate's other regions the way a change from NGC's own
/// estate tools does: the settings are stored, then the estate module's OnEstateInfoChange is raised
/// (EstateManagementModule.TriggerEstateInfoChange). EstateModule turns that event into
/// EstateConnector.SendUpdateEstate, which reloads the estate on this simulator's regions of the estate and sends
/// update_estate to the others. Halcyon does the same after a ban (EstateManagementModule
/// SaveEstateDataAndUpdateRegions). The event is raised once per real change, after the store, and never for a no-op,
/// a refusal or a query.
/// </summary>
// The real EstateManagementModule is added to the test region; nothing leaves the process. No process-wide state:
// the class runs in parallel.
public class EstateChangeSpreadTests
{
    private readonly ITestOutputHelper _out;
    public EstateChangeSpreadTests(ITestOutputHelper o) => _out = o;

    /// <summary>Each OnEstateInfoChange: the region it names and how many stores had been made when it fired.</summary>
    private sealed class Spread
    {
        public readonly ConcurrentQueue<(UUID Region, int StoresSoFar)> Raised = new();
    }

    private static Spread AddEstateModule(EstateRig r)
    {
        var module = new EstateManagementModule();
        module.Initialise(new IniConfigSource());
        module.AddRegion(r.H.Scene);
        var spread = new Spread();
        module.OnEstateInfoChange += region => spread.Raised.Enqueue((region, r.Estates.Stores));
        return spread;
    }

    [Fact]
    public void EachRealChangeRaisesTheEstateChangeOnceAfterItIsStored()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var spread = AddEstateModule(r);
        UUID agent = UUID.Random(), group = UUID.Random();

        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, agent));
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_REMOVE, agent));
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_GROUP_ADD, group));
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_GROUP_REMOVE, group));
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, agent));
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_REMOVE, agent));

        var raised = spread.Raised.ToArray();
        _out.WriteLine("raised: " + string.Join(", ", raised.Select(e => e.StoresSoFar)));
        Assert.Equal(6, r.Estates.Stores);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, raised.Select(e => e.StoresSoFar).ToArray());
        Assert.All(raised, e => Assert.Equal(r.H.Scene.RegionInfo.RegionID, e.Region));
    }

    [Fact]
    public void ABanOfAnAllowedAvatarAndAnAllowOfABannedOneEachRaiseItOnce()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var spread = AddEstateModule(r);
        var visitor = UUID.Random();
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, visitor));
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, visitor));     // clears the allowed entry and bans
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, visitor));    // lifts the ban and allows
        Assert.Equal(3, r.Estates.Stores);
        Assert.Equal(3, spread.Raised.Count);
    }

    [Fact]
    public void NoOpsRefusalsAndQueriesDoNotRaiseIt()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var spread = AddEstateModule(r);
        UUID agent = UUID.Random(), group = UUID.Random();
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, agent));
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_GROUP_ADD, group));
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, UUID.Random()));
        int before = spread.Raised.Count;

        Assert.Equal(0, r.Call(EstateRig.ALLOWED_AGENT_ADD, agent));         // already allowed
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_GROUP_ADD, group));         // already allowed
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_AGENT_REMOVE, UUID.Random()));
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_GROUP_REMOVE, UUID.Random()));
        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_REMOVE, UUID.Random()));
        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_ADD, r.EstateOwner));  // never banned
        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_ADD, UUID.Zero));
        Assert.Equal(0, r.Call(99, UUID.Random()));
        Assert.Equal(1, r.Call(EstateRig.QUERY_ALLOWED_AGENT, agent));
        Assert.Equal(1, r.Call(EstateRig.QUERY_CAN_MANAGE, UUID.Zero));

        Assert.Equal(3, before);
        Assert.Equal(before, spread.Raised.Count);
        Assert.Equal(3, r.Estates.Stores);
    }

    [Fact]
    public void ACallerWhoMayNotManageRaisesNothing()
    {
        using var r = new EstateRig(_out, EstateRole.Nobody);
        var spread = AddEstateModule(r);
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_AGENT_ADD, UUID.Random()));
        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_ADD, UUID.Random()));
        Assert.Empty(spread.Raised);
    }
}
