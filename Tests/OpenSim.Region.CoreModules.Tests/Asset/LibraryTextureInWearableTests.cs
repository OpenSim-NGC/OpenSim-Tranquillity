using System;
using System.Collections.Generic;
using Xunit;
using OpenMetaverse;
using PermissionMask = OpenSim.Framework.PermissionMask;
using OpenSim.Region.CoreModules.Agent.AssetTransaction;

namespace OpenSim.Region.CoreModules.Tests.Asset;

/// <summary>
/// A19. A wearable may reference a texture from the grid's LIBRARY, which is what SL allows and what a resident
/// picking a library texture in the appearance editor produces.
///
/// <para>
/// Before this, <c>AssetXferUploader.ValidateAssets</c> asked <c>GetAssetPermissions</c> for the texture under
/// the <b>resident's</b> id only (<c>AssetXferUploader.cs:566</c>). A library texture is not in the resident's
/// inventory, so that returns nothing, the full-rights test failed, and the whole save was refused - observed on
/// Ebony at 2026-09-06 09:52:55 with the library texture
/// <c>00000000-0000-2222-3333-100000001002</c>: <i>"REJECTED update with texture ... because they do not own the
/// texture"</i>. The built-in system ids in <c>defaultIDs</c> (<c>AssetXferUploader.cs:40-54</c>) were already
/// exempt; a library asset was not.
/// </para>
/// </summary>
public class LibraryTextureInWearableTests
{
    private const uint FullPerms = (uint)(PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Copy);

    private static readonly UUID LibraryOwner = new("11111111-1111-0000-0000-000100bba000");


    /// <summary>The library texture from the 09:52:55 rejection.</summary>
    private static readonly UUID LibraryTexture = new("00000000-0000-2222-3333-100000001002");

    /// <summary>Another resident's texture: held by somebody, but not by the library and not by us.</summary>
    private static readonly UUID StrangersTexture = new("d4a1e4c2-0000-4000-8000-0000000000ff");

    /// <summary>A stand-in for IInventoryService.GetAssetPermissions over a fixed (owner, asset) table.</summary>
    private static Func<UUID, UUID, int> Inventory(params (UUID Owner, UUID Asset, uint Perms)[] rows)
    {
        var table = new Dictionary<(UUID, UUID), uint>();
        foreach (var r in rows) table[(r.Owner, r.Asset)] = r.Perms;
        return (owner, asset) => table.TryGetValue((owner, asset), out var p) ? (int)p : 0;
    }

    [Fact]
    public void a_texture_the_library_holds_with_full_rights_is_accepted()
    {
        var inv = Inventory((LibraryOwner, LibraryTexture, FullPerms));

        Assert.True(AssetXferUploader.IsLibraryTexture(LibraryTexture, LibraryOwner, FullPerms, inv),
            "a library texture must be usable in a wearable; refusing it loses the whole save");
    }

    [Fact]
    public void another_residents_private_texture_is_still_refused()
    {
        // The stranger holds it with full rights; the library does not hold it at all. Nothing about the library
        // rule may turn that into an acceptance - that would be the permission check gone.
        var inv = Inventory(
            (StrangersTexture, StrangersTexture, FullPerms),     // some other owner entirely
            (LibraryOwner, LibraryTexture, FullPerms));

        Assert.False(AssetXferUploader.IsLibraryTexture(StrangersTexture, LibraryOwner, FullPerms, inv),
            "a texture the library does not hold is not made acceptable by this rule");
    }

    [Fact]
    public void a_library_texture_without_full_rights_is_refused()
    {
        // The library is asked for exactly the rights the resident would have needed, not waved through by owner.
        var inv = Inventory((LibraryOwner, LibraryTexture, (uint)PermissionMask.Copy));

        Assert.False(AssetXferUploader.IsLibraryTexture(LibraryTexture, LibraryOwner, FullPerms, inv));
    }

    [Fact]
    public void a_region_with_no_library_changes_nothing()
    {
        // LibraryRootFolder.Owner is UUID.Zero when the region has no library service; the previous behaviour has
        // to survive that untouched rather than accepting everything or throwing.
        var inv = Inventory((LibraryOwner, LibraryTexture, FullPerms));

        Assert.False(AssetXferUploader.IsLibraryTexture(LibraryTexture, UUID.Zero, FullPerms, inv));
        Assert.False(AssetXferUploader.IsLibraryTexture(LibraryTexture, LibraryOwner, FullPerms, null));
    }
}
