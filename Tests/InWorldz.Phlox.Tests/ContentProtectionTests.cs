using System;
using System.Collections.Generic;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using PermissionMask = OpenSim.Framework.PermissionMask;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetInventoryKey hands out an asset key only for a full-perm item
/// (https://wiki.secondlife.com/wiki/LlGetInventoryKey), and llSetContentType uses SL's numbering and
/// gives text/html only to the owner's own viewer (https://wiki.secondlife.com/wiki/LlSetContentType).
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class ContentProtectionTests
{
    private const uint FullPerm = (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer);

    private static UUID AddTexture(SchedulerHarness h, string name, uint perms)
    {
        var asset = UUID.Random();
        var item = new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(),
            Type = (int)AssetType.Texture, InvType = (int)InventoryType.Texture,
            BasePermissions = perms, CurrentPermissions = perms, NextPermissions = perms,
            OwnerID = h.Prim.OwnerID,
        };
        h.Prim.Inventory.AddInventoryItem(item, true);
        return asset;
    }

    [Fact]
    public void ANoModTextureGivesNullKeyAndAFullPermOneGivesItsAssetKey()
    {
        using var h = new SchedulerHarness();
        var full = AddTexture(h, "full", FullPerm);
        AddTexture(h, "nomod", (uint)(PermissionMask.Copy | PermissionMask.Transfer));
        h.RezScript("default { state_entry() { llSay(0, \"full=\" + (string)llGetInventoryKey(\"full\")); llSay(0, \"nomod=\" + (string)llGetInventoryKey(\"nomod\")); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("nomod=")));
        Assert.Contains("full=" + full, h.Said);
        Assert.Contains("nomod=" + UUID.Zero, h.Said);
    }

    private static RecordingUrlModule FakeUrls(SchedulerHarness h)
    {
        var fake = RecordingUrlModule.Create(out var rec);
        h.Scene.RegisterModuleInterface<IUrlModule>(fake);
        return rec;
    }

    [Theory]
    [InlineData("CONTENT_TYPE_JSON", "application/json")]
    [InlineData("CONTENT_TYPE_XML", "application/xml")]
    [InlineData("CONTENT_TYPE_XHTML", "application/xhtml+xml")]
    [InlineData("CONTENT_TYPE_ATOM", "application/atom+xml")]
    [InlineData("CONTENT_TYPE_LLSD", "application/llsd+xml")]
    [InlineData("CONTENT_TYPE_FORM", "application/x-www-form-urlencoded")]
    [InlineData("CONTENT_TYPE_RSS", "application/rss+xml")]
    [InlineData("CONTENT_TYPE_TEXT", "text/plain")]
    public void ContentTypeFollowsSlNumbering(string constant, string mime)
    {
        using var h = new SchedulerHarness();
        var rec = FakeUrls(h);
        var req = UUID.Random();
        h.RezScript("default { state_entry() { llSetContentType(\"" + req + "\", " + constant + "); llSay(0, \"set\"); } }");
        h.PumpUntil(() => h.Said.Contains("set") && rec.LastType(req) != null);
        Assert.Contains("set", h.Said);
        Assert.Equal(mime, rec.LastType(req));
    }

    [Fact]
    public void HtmlWithNoOwnerPresentComesBackTextPlain()
    {
        using var h = new SchedulerHarness();
        var rec = FakeUrls(h);
        var req = UUID.Random();
        h.RezScript("default { state_entry() { llSetContentType(\"" + req + "\", CONTENT_TYPE_HTML); llSay(0, \"set\"); } }");
        h.PumpUntil(() => h.Said.Contains("set") && rec.LastType(req) != null);
        Assert.Contains("set", h.Said);
        Assert.Equal("text/plain", rec.LastType(req));
    }
}

/// <summary>An IUrlModule that records HttpContentType calls and answers every other member with a default.</summary>
public class RecordingUrlModule : DispatchProxy
{
    private readonly Dictionary<UUID, string> m_types = new();

    public static IUrlModule Create(out RecordingUrlModule rec)
    {
        var p = Create<IUrlModule, RecordingUrlModule>();
        rec = (RecordingUrlModule)(object)p;
        return p;
    }

    public string LastType(UUID req) { lock (m_types) return m_types.TryGetValue(req, out var t) ? t : null; }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "HttpContentType") lock (m_types) m_types[(UUID)args[0]] = (string)args[1];
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
