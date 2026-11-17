/*
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

using System.Reflection;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.Land.Tests;

/// <summary>
/// Who may buy, be sold, be deeded or abandon a parcel, and what the parcel looks like afterwards: a parcel that is not
/// for sale, an authorised buyer, a purchase for a group, a deed, the payment a money module reports, the estate's
/// "allow land resell" and the parcel's group.
/// </summary>
public class LandOwnershipTests : OpenSimTestCase
{
    private static readonly UUID Seller = TestHelpers.ParseTail(0x1);
    private static readonly UUID Buyer = TestHelpers.ParseTail(0x2);
    private static readonly UUID Other = TestHelpers.ParseTail(0x3);
    private static readonly UUID EstateOwner = TestHelpers.ParseTail(0x4);
    private static readonly UUID Group = TestHelpers.ParseTail(0x100);
    private static readonly UUID OtherGroup = TestHelpers.ParseTail(0x200);

    private sealed class Fixture
    {
        public readonly LandManagementModule Module = new();
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly ILandObject Parcel;
        public readonly Dictionary<(UUID Group, UUID User), ulong> Powers = new();

        /// <param name="charge">
        /// What the money module reports it debited for a purchase; by default the price, as a module that charged it
        /// (or the no-money module, for which every price is paid). Its OnLandBuy handler is added after the land
        /// module's, so it runs after it.
        /// </param>
        public Fixture(Func<EventManager.LandBuyArgs, int> charge = null)
        {
            Scene.RegisterModuleInterface(FakeGroups.Create(Powers));
            Scene.RegionInfo.EstateSettings.EstateOwner = EstateOwner;
            SceneHelpers.SetupSceneModules(Scene, Module);
            // Without a permissions module every user counts as an administrator; here nobody is one.
            Scene.Permissions.OnIsAdministrator += _ => false;

            ILandObject lo = new LandObject(Seller, false, Scene);
            lo.SetLandBitmap(lo.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            Parcel = Module.AddLandObject(lo);

            charge ??= e => e.parcelPrice;
            Scene.EventManager.OnValidateLandBuy += (_, e) => e.economyValidated = true;
            Scene.EventManager.OnLandBuy += (_, e) => e.amountDebited = charge(e);
        }

        public LandData Land => Parcel.LandData;

        public void ForSale(int price = 0, UUID authBuyer = default, uint flags = (uint)ParcelFlags.ForSale)
        {
            Land.Flags |= flags;
            Land.SalePrice = price;
            Land.AuthBuyerID = authBuyer;
        }

        public void Buy(UUID agent, int price = 0, UUID group = default, bool forGroup = false)
            => Scene.ProcessParcelBuy(agent, group, true, forGroup, false, Land.LocalID, Land.Area, price, true);

        public TestClient Client(UUID agent) => new(SceneHelpers.GenerateAgentData(agent), Scene);
    }

    // ── what can be bought ─────────────────────────────────────────────────────

    [Fact]
    public void AParcelForSaleIsSold()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.ForSale();

        f.Buy(Buyer);

        Assert.Equal(Buyer, f.Land.OwnerID);
        Assert.Equal(0u, f.Land.Flags & (uint)ParcelFlags.ForSale);
    }

    [Fact]
    public void ACancelledSaleThatIncludedObjectsCannotBeBought()
    {
        TestHelpers.InMethod();
        // The viewer's "Cancel land sale" clears ForSale, the price and the authorised buyer, and leaves "sell objects
        // with land" (SellParcelObjects) set.
        var f = new Fixture();
        f.ForSale(flags: (uint)ParcelFlags.SellParcelObjects);

        f.Buy(Buyer);

        Assert.Equal(Seller, f.Land.OwnerID);
    }

    [Fact]
    public void OnlyTheAuthorisedBuyerMayBuy()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.ForSale(authBuyer: Buyer);

        f.Buy(Other);
        Assert.Equal(Seller, f.Land.OwnerID);

        f.Buy(Buyer);
        Assert.Equal(Buyer, f.Land.OwnerID);
    }

    // ── buying for a group ─────────────────────────────────────────────────────

    [Fact]
    public void BuyingForAGroupNeedsTheGroupsDeedPower()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.ForSale();
        f.Powers[(Group, Buyer)] = (ulong)GroupPowers.LandEjectAndFreeze;

        f.Buy(Buyer, group: Group, forGroup: true);

        Assert.Equal(Seller, f.Land.OwnerID);
        Assert.False(f.Land.IsGroupOwned);
    }

    [Fact]
    public void LandBoughtForAGroupIsOwnedByTheGroup()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.ForSale();
        f.Powers[(Group, Buyer)] = (ulong)GroupPowers.LandDeed;

        f.Buy(Buyer, group: Group, forGroup: true);

        Assert.True(f.Land.IsGroupOwned);
        Assert.Equal(Group, f.Land.OwnerID);
        Assert.Equal(Group, f.Land.GroupID);
    }

    // ── deeding ────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeedGoesOnlyToTheParcelsGroup()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Land.GroupID = Group;
        TestClient seller = f.Client(Seller);

        f.Module.ClientOnParcelDeedToGroup(f.Land.LocalID, OtherGroup, seller);
        Assert.Equal(Seller, f.Land.OwnerID);
        Assert.False(f.Land.IsGroupOwned);

        f.Module.ClientOnParcelDeedToGroup(f.Land.LocalID, Group, seller);
        Assert.Equal(Group, f.Land.OwnerID);
        Assert.True(f.Land.IsGroupOwned);
    }

    // ── payment ────────────────────────────────────────────────────────────────

    [Fact]
    public void AParcelIsNotHandedOverWhenThePaymentFails()
    {
        TestHelpers.InMethod();
        var f = new Fixture(charge: _ => 0);
        f.ForSale(price: 100);

        f.Buy(Buyer, price: 100);

        Assert.Equal(Seller, f.Land.OwnerID);
    }

    [Fact]
    public void AParcelIsHandedOverOnceTheMoneyModuleHasCharged()
    {
        TestHelpers.InMethod();
        // The money module's OnLandBuy handler is added after the land module's, so it runs after it.
        var f = new Fixture();
        f.ForSale(price: 100);

        f.Buy(Buyer, price: 100);

        Assert.Equal(Buyer, f.Land.OwnerID);
    }

    // ── land resell ────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutLandResellAResidentsParcelCannotBeBought()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Scene.RegionInfo.RegionSettings.AllowLandResell = false;
        f.ForSale();

        f.Buy(Buyer);

        Assert.Equal(Seller, f.Land.OwnerID);
    }

    [Fact]
    public void WithoutLandResellTheEstateOwnerStillSells()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Scene.RegionInfo.RegionSettings.AllowLandResell = false;
        f.Land.OwnerID = EstateOwner;
        f.ForSale();

        f.Buy(Buyer);

        Assert.Equal(Buyer, f.Land.OwnerID);
    }

    [Fact]
    public void WithoutLandResellAResidentCannotPutTheParcelUpForSale()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Scene.RegionInfo.RegionSettings.AllowLandResell = false;

        f.Parcel.UpdateLandProperties(SaleSave(f.Land, (uint)ParcelFlags.ForSale, 10, Buyer), f.Client(Seller), out _, out _);

        Assert.Equal(0u, f.Land.Flags & (uint)ParcelFlags.ForSale);
        Assert.Equal(0, f.Land.SalePrice);
        Assert.Equal(UUID.Zero, f.Land.AuthBuyerID);
    }

    // ── the parcel's group ─────────────────────────────────────────────────────

    [Fact]
    public void TheParcelsGroupMustBeAGroupTheOwnerIsIn()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Powers[(Group, Seller)] = (ulong)GroupPowers.None;

        f.Parcel.UpdateLandProperties(SaleSave(f.Land, 0, 0, UUID.Zero, OtherGroup), f.Client(Seller), out _, out _);
        Assert.Equal(UUID.Zero, f.Land.GroupID);

        f.Parcel.UpdateLandProperties(SaleSave(f.Land, 0, 0, UUID.Zero, Group), f.Client(Seller), out _, out _);
        Assert.Equal(Group, f.Land.GroupID);
    }

    // ── abandoning ─────────────────────────────────────────────────────────────

    [Fact]
    public void AnAbandonedParcelKeepsNoSaleTerms()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.ForSale(price: 50, authBuyer: Buyer);

        f.Module.ClientOnParcelAbandonRequest(f.Land.LocalID, f.Client(Seller));

        Assert.Equal(EstateOwner, f.Land.OwnerID);
        Assert.Equal(0, f.Land.SalePrice);
        Assert.Equal(UUID.Zero, f.Land.AuthBuyerID);
    }

    private static LandUpdateArgs SaleSave(LandData data, uint flags, int price, UUID authBuyer, UUID group = default)
        => new()
        {
            ParcelFlags = data.Flags | flags,
            Name = data.Name,
            Desc = data.Description,
            MediaURL = data.MediaURL,
            MusicURL = data.MusicURL,
            MediaType = data.MediaType,
            SalePrice = price,
            AuthBuyerID = authBuyer,
            GroupID = group == default ? data.GroupID : group,
        };

    /// <summary>An IGroupsModule that knows each (group, member)'s powers (GetMembershipData).</summary>
    private class FakeGroups : DispatchProxy
    {
        private Dictionary<(UUID, UUID), ulong> m_powers;

        public static IGroupsModule Create(Dictionary<(UUID, UUID), ulong> powers)
        {
            var proxy = Create<IGroupsModule, FakeGroups>();
            ((FakeGroups)(object)proxy).m_powers = powers;
            return proxy;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "GetMembershipData" && args.Length == 2)
            {
                var key = ((UUID)args[0], (UUID)args[1]);
                return m_powers.TryGetValue(key, out ulong p) ? new GroupMembershipData { GroupID = key.Item1, GroupPowers = p } : null;
            }
            var rt = targetMethod.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }
}
