/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
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

using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Linkset reads and llBreakLink(LINK_ROOT), sit-target rotations, and the inventory-first name lookup:
/// - llGetLinkNumber is 0 in an unlinked prim (SL; Halcyon's PartCount guard).
/// - llGetLinkName answers NULL_KEY for a link that is not there ("If link is out of bounds, NULL_KEY is returned")
///   and follows Halcyon's table for 0, LINK_THIS and the negative constants.
/// - llBreakLink(LINK_ROOT) takes the root out and leaves the other prims linked to each other (Halcyon).
/// - llSitTarget and llLinkSitTarget store a unit rotation (Halcyon's Rot2Quaternion).
/// - A texture or sound named by a string looks in the prim's inventory first, then reads the string as a key, so an
///   item named like a key resolves to its own asset (Halcyon's KeyOrName, which calls the other order an exploit).
/// </summary>
// Calls the API directly on the harness prims; no clock and no process-wide state, so the class runs in parallel.
public class LinksetNameNumberBreakTests
{
    private const int LINK_ROOT = 1, LINK_SET = -1, LINK_ALL_OTHERS = -2, LINK_ALL_CHILDREN = -3, LINK_THIS = -4;
    private static readonly string NullKey = UUID.Zero.ToString();

    private static LSLSystemAPI Api(SchedulerHarness h, SceneObjectPart p) => new LSLSystemAPI(h.Engine, p, p.LocalId, UUID.Random());

    /// <summary>The harness prim as "root" with children "c2" and "c3", links 1..3.</summary>
    private static SceneObjectPart[] ThreePrims(SchedulerHarness h)
    {
        h.Prim.Name = "root";
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "c2", h.Prim.OwnerID));
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "c3", h.Prim.OwnerID));
        var g = h.Prim.ParentGroup;
        Assert.Equal(3, g.PrimCount);
        return new[] { g.GetLinkNumPart(1), g.GetLinkNumPart(2), g.GetLinkNumPart(3) };
    }

    // ---- llGetLinkNumber ----

    [Fact]
    public void AnUnlinkedPrimIsLinkZero()
    {
        using var h = new SchedulerHarness();
        h.Prim.LinkNum = 1;   // what a root keeps after its children are delinked
        Assert.Equal(0, Api(h, h.Prim).llGetLinkNumber());
    }

    [Fact]
    public void LinkedPrimsReportTheirLinkNumber()
    {
        using var h = new SchedulerHarness();
        var p = ThreePrims(h);
        Assert.Equal(1, Api(h, p[0]).llGetLinkNumber());
        Assert.Equal(3, Api(h, p[2]).llGetLinkNumber());
    }

    // ---- llGetLinkName ----

    [Fact]
    public void AnUnlinkedPrimNamesItselfOnlyForZeroAndLinkThis()
    {
        using var h = new SchedulerHarness();
        h.Prim.Name = "solo";
        var api = Api(h, h.Prim);
        Assert.Equal("solo", api.llGetLinkName(0));
        Assert.Equal("solo", api.llGetLinkName(LINK_THIS));
        Assert.Equal(NullKey, api.llGetLinkName(1));
        Assert.Equal(NullKey, api.llGetLinkName(2));
        Assert.Equal(NullKey, api.llGetLinkName(LINK_SET));
    }

    [Fact]
    public void FromTheRootNegativeConstantsNameLinkTwoAndZeroIsNullKey()
    {
        using var h = new SchedulerHarness();
        var p = ThreePrims(h);
        var api = Api(h, p[0]);
        Assert.Equal("root", api.llGetLinkName(LINK_THIS));
        Assert.Equal("root", api.llGetLinkName(LINK_ROOT));
        Assert.Equal("c3", api.llGetLinkName(3));
        Assert.Equal("c2", api.llGetLinkName(LINK_SET));
        Assert.Equal("c2", api.llGetLinkName(LINK_ALL_OTHERS));
        Assert.Equal(NullKey, api.llGetLinkName(0));
        Assert.Equal(NullKey, api.llGetLinkName(4));
    }

    [Fact]
    public void FromAChildZeroOneAndNegativeConstantsNameTheRoot()
    {
        using var h = new SchedulerHarness();
        var p = ThreePrims(h);
        var api = Api(h, p[2]);
        Assert.Equal("c3", api.llGetLinkName(LINK_THIS));
        Assert.Equal("root", api.llGetLinkName(0));
        Assert.Equal("root", api.llGetLinkName(LINK_ROOT));
        Assert.Equal("root", api.llGetLinkName(LINK_ALL_CHILDREN));
        Assert.Equal("c2", api.llGetLinkName(2));
        Assert.Equal(NullKey, api.llGetLinkName(9));
    }

    // ---- llBreakLink(LINK_ROOT) ----

    private static void BreakLink(LSLSystemAPI api, int link)
        => typeof(LSLSystemAPI).GetMethod("BreakLinkCore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(api, new object[] { link });

    [Fact]
    public void BreakingTheRootLeavesTheOtherPrimsLinked()
    {
        using var h = new SchedulerHarness();
        var p = ThreePrims(h);
        BreakLink(Api(h, p[0]), LINK_ROOT);
        Assert.Equal(1, p[0].ParentGroup.PrimCount);
        Assert.Same(p[1].ParentGroup, p[2].ParentGroup);
        Assert.Equal(2, p[1].ParentGroup.PrimCount);
        Assert.NotSame(p[0].ParentGroup, p[1].ParentGroup);
        Assert.Same(p[1], p[1].ParentGroup.RootPart);
    }

    [Fact]
    public void BreakingTheRootOfATwoPrimSetLeavesTwoSinglePrims()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "c2", h.Prim.OwnerID));
        var child = h.Prim.ParentGroup.Parts.Single(x => x != h.Prim);
        BreakLink(Api(h, h.Prim), LINK_ROOT);
        Assert.Equal(1, h.Prim.ParentGroup.PrimCount);
        Assert.Equal(1, child.ParentGroup.PrimCount);
        Assert.NotSame(h.Prim.ParentGroup, child.ParentGroup);
    }

    // ---- sit target rotation ----

    [Fact]
    public void SitTargetStoresAUnitRotation()
    {
        using var h = new SchedulerHarness();
        Api(h, h.Prim).llSitTarget(new Vector3(0, 0, 0.5f), new Quaternion(0, 0, 1, 1));
        var q = h.Prim.SitTargetOrientation;
        Assert.Equal(1f, q.Length(), 3);
        Assert.Equal(0.7071f, q.Z, 3);
        Assert.Equal(0.7071f, q.W, 3);
    }

    [Fact]
    public void LinkSitTargetStoresAUnitRotation()
    {
        using var h = new SchedulerHarness();
        var p = ThreePrims(h);
        Api(h, p[0]).llLinkSitTarget(2, new Vector3(0, 0, 0.5f), new Quaternion(0, 0, 2, 2));
        Assert.Equal(1f, p[1].SitTargetOrientation.Length(), 3);
    }

    // ---- inventory first ----

    private static TaskInventoryItem AddTexture(SchedulerHarness h, string name, UUID asset)
    {
        uint full = (uint)(OpenMetaverse.PermissionMask.Copy | OpenMetaverse.PermissionMask.Modify | OpenMetaverse.PermissionMask.Transfer | OpenMetaverse.PermissionMask.Move);
        var item = new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(),
            Type = (int)AssetType.Texture, InvType = (int)InventoryType.Texture,
            BasePermissions = full, CurrentPermissions = full, NextPermissions = full, OwnerID = h.Prim.OwnerID,
        };
        h.Prim.Inventory.AddInventoryItem(item, true);
        return item;
    }

    [Fact]
    public void AnItemNamedLikeAKeyResolvesToItsOwnAsset()
    {
        using var h = new SchedulerHarness();
        var named = new UUID("6f1d2c3b-0000-4000-8000-000000000001");
        var asset = UUID.Random();
        AddTexture(h, named.ToString(), asset);
        Api(h, h.Prim).llSetTexture(named.ToString(), 0);
        Assert.Equal(asset, h.Prim.Shape.Textures.GetFace(0).TextureID);
    }

    [Fact]
    public void AKeyWithNoItemOfThatNameIsUsedAsTheKey()
    {
        using var h = new SchedulerHarness();
        var key = new UUID("6f1d2c3b-0000-4000-8000-000000000002");
        Api(h, h.Prim).llSetTexture(key.ToString(), 0);
        Assert.Equal(key, h.Prim.Shape.Textures.GetFace(0).TextureID);
    }
}
