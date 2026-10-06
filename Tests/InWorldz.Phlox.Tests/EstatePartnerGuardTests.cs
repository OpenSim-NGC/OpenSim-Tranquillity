/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llManageEstateAccess never bans the estate owner's partner, as Halcyon's EstateBanUser refuses it ("never process
/// EO", EstateManagementModule.cs:263-270, IsEstateOwnerPartner). The partner is the one on the estate owner's profile
/// (IProfileModule.TryGetUserPartner). The call answers FALSE and changes nothing, with no IM and no error, as for the
/// estate owner. When the profile cannot be read the ban goes ahead, so an unreachable profiles service does not stop
/// estate management. SL documents no partner rule.
/// </summary>
// No test reaches a network service: the profile module is an in-memory table. Test grouping: no process-wide state, so
// the class runs in parallel.
public class EstatePartnerGuardTests
{
    private readonly ITestOutputHelper _out;
    public EstatePartnerGuardTests(ITestOutputHelper o) => _out = o;

    /// <summary>A profile module that knows partners from a table; a user missing from it cannot be read.</summary>
    private sealed class PartnerTable : IProfileModule
    {
        public readonly ConcurrentDictionary<UUID, UUID> Partners = new();
        public readonly ConcurrentQueue<UUID> Asked = new();

        public void RequestAvatarProperties(IClientAPI remoteClient, UUID avatarID) { }

        public bool TryGetUserPartner(UUID userID, out UUID partnerID)
        {
            Asked.Enqueue(userID);
            return Partners.TryGetValue(userID, out partnerID);
        }
    }

    private static PartnerTable Profiles(EstateRig r)
    {
        var table = new PartnerTable();
        r.H.Scene.RegisterModuleInterface<IProfileModule>(table);
        return table;
    }

    [Fact]
    public void TheEstateOwnersPartnerIsNeverBannedAndTheScriptGetsFalseWithNoMessage()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var profiles = Profiles(r);
        var partner = r.Avatar(UUID.Random());
        profiles.Partners[r.EstateOwner] = partner.UUID;
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, partner.UUID));
        int stores = r.Estates.Stores, ims = r.Ims.Sent.Count;

        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_ADD, partner.UUID));

        Assert.False(r.Banned(partner.UUID));
        Assert.Contains(partner.UUID, r.Estate.EstateAccess);   // the allowed entry stays
        Assert.Equal(stores, r.Estates.Stores);
        Assert.Empty(r.Tp.Calls);
        Assert.NotNull(r.H.Scene.GetScenePresence(partner.UUID));
        Assert.Equal(ims, r.Ims.Sent.Count);                      // no IM to the object's owner
        Assert.Equal(string.Empty, r.Errors);                      // nothing on DEBUG_CHANNEL
        Assert.Contains(r.EstateOwner, profiles.Asked);
    }

    [Fact]
    public void AnotherAvatarIsBannedWhenTheEstateOwnerHasAPartner()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var profiles = Profiles(r);
        profiles.Partners[r.EstateOwner] = UUID.Random();
        var visitor = UUID.Random();

        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, visitor));

        Assert.True(r.Banned(visitor));
        Assert.Contains(r.EstateOwner, profiles.Asked);
    }

    [Fact]
    public void WhenTheEstateOwnersProfileCannotBeReadTheBanGoesAhead()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var profiles = Profiles(r);   // the estate owner is not in the table: unknown
        var visitor = UUID.Random();

        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, visitor));

        Assert.True(r.Banned(visitor));
        Assert.Contains(r.EstateOwner, profiles.Asked);
    }

    [Fact]
    public void AnEstateOwnerWithNoPartnerGuardsNobody()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var profiles = Profiles(r);
        profiles.Partners[r.EstateOwner] = UUID.Zero;
        var visitor = UUID.Random();

        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, visitor));

        Assert.True(r.Banned(visitor));
        Assert.Contains(r.EstateOwner, profiles.Asked);
    }
}
