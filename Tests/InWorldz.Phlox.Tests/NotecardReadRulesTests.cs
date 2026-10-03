/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Notecard reads and landmark data. A line read returns at most NotecardLineReadCharsMax bytes of UTF-8, 1024 by
/// default (SL llGetNotecardLine: "If the requested line is longer than 1024 bytes (not characters), dataserver will
/// only return the first 1024 bytes"); [InWorldz.Phlox] sets it, else [YEngine]'s setting of the same name. A negative
/// line answers "". iwGetNotecardSegment answers "" for a zero or negative length or a negative line (Halcyon
/// NotecardCache.GetLine, LSLSystemAPI.cs:18685-18709). Every reader takes a notecard asset key for the name (SL: "a
/// notecard in the inventory of the prim this script is in or a UUID of a notecard"), refusing an asset that is not a
/// notecard. iwGetLinkNotecardSegment says when no prim holds the notecard, as iwGetLinkNotecardLine does.
/// iwMakeNotecard waits its 5 s on every path (Halcyon :14740-14790). llRequestInventoryData answers a landmark's
/// position as an offset from this region's corner (SL: "a global position as an offset from the current region's
/// origin"), and for a missing landmark returns "", says so on DEBUG_CHANNEL and still sleeps its 1 s (SL; Halcyon
/// :5686-5692).
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class NotecardReadRulesTests
{
    private const string Dataserver = "default { dataserver(key q, string d) { llSay(0, \"ds \" + (string)llStringLength(d) + \" \" + llGetSubString(d, 0, 19)); } }";
    private static readonly string E = ((char)0xE9).ToString();   // two bytes in UTF-8

    /// <summary>What the dataserver handler says for <paramref name="s"/>: its length and first 20 characters (chat is capped).</summary>
    private static string Exp(string s) => s.Length + " " + s.Substring(0, Math.Min(20, s.Length));

    private static string Read(InventoryGivesRig r, Func<global::Phlox.ScriptEngine.LSLSystemAPI, object> call)
    {
        r.H.ClearSaid(r.Item);
        r.Accounted(call);
        return r.WaitSaid("ds ").Substring(3);
    }

    [Fact]
    public void LinesAreCutAt1024BytesByDefault()
    {
        using var r = new InventoryGivesRig(Dataserver);
        AddNotecard(r.H, r.H.Prim, "card", new string('a', 1500) + "\n" + string.Concat(Enumerable.Repeat(E, 600)) + "\nshort");
        Assert.Equal(Exp(new string('a', 1024)), Read(r, a => a.llGetNotecardLine("card", 0)));
        Assert.Equal(Exp(string.Concat(Enumerable.Repeat(E, 512))), Read(r, a => a.llGetNotecardLine("card", 1)));
        Assert.Equal(Exp("short"), Read(r, a => a.llGetNotecardLine("card", 2)));
        Assert.Equal(Exp(new string('a', 1024)), Read(r, a => a.iwGetLinkNotecardLine(1, "card", 0)));
    }

    [Theory]
    [InlineData("InWorldz.Phlox", 255)]
    [InlineData("YEngine", 300)]
    public void TheCapIsConfigurable(string section, int cap)
    {
        using var r = new InventoryGivesRig(Dataserver, cfg =>
        {
            if (cfg.Configs[section] == null) cfg.AddConfig(section);
            cfg.Configs[section].Set("NotecardLineReadCharsMax", cap.ToString());
        });
        AddNotecard(r.H, r.H.Prim, "card", new string('a', 1500));
        Assert.Equal(Exp(new string('a', cap)), Read(r, a => a.llGetNotecardLine("card", 0)));
    }

    [Fact]
    public void NegativeLinesAndEmptyLengthsAnswerEmpty()
    {
        using var r = new InventoryGivesRig(Dataserver);
        AddNotecard(r.H, r.H.Prim, "card", "hello\nworld");
        Assert.Equal(Exp(""), Read(r, a => a.llGetNotecardLine("card", -1)));
        Assert.Equal(Exp(""), Read(r, a => a.iwGetNotecardSegment("card", 0, 0, 0)));
        Assert.Equal(Exp(""), Read(r, a => a.iwGetNotecardSegment("card", 0, 0, -1)));
        Assert.Equal(Exp(""), Read(r, a => a.iwGetNotecardSegment("card", -1, 0, 5)));
        Assert.Equal(Exp("ell"), Read(r, a => a.iwGetNotecardSegment("card", 0, 1, 3)));
        Assert.Equal(Exp(""), Read(r, a => a.iwGetLinkNotecardSegment(1, "card", 0, 0, 0)));
    }

    [Fact]
    public void ReadersTakeANotecardAssetKey()
    {
        using var r = new InventoryGivesRig(Dataserver);
        // A notecard asset that is in no prim's inventory, read by its key.
        UUID asset = UUID.Random();
        var nc = new OpenMetaverse.Assets.AssetNotecard { BodyText = "one\ntwo" };
        nc.Encode();
        r.H.Scene.AssetService.Store(AssetHelpers.CreateAsset(asset, AssetType.Notecard, nc.AssetData, UUID.Zero));
        string k = asset.ToString();
        Assert.Equal(Exp("two"), Read(r, a => a.llGetNotecardLine(k, 1)));
        Assert.Equal(Exp("2"), Read(r, a => a.llGetNumberOfNotecardLines(k)));
        Assert.Equal(Exp("wo"), Read(r, a => a.iwGetNotecardSegment(k, 1, 1, 5)));
        Assert.Equal(Exp("one"), Read(r, a => a.iwGetLinkNotecardLine(1, k, 0)));
        Assert.Equal(Exp("on"), Read(r, a => a.iwGetLinkNotecardSegment(1, k, 0, 0, 2)));
        Assert.Equal(Exp("2"), Read(r, a => a.iwGetLinkNumberOfNotecardLines(1, k)));
    }

    [Fact]
    public void AKeyThatIsNotANotecardIsNotFound()
    {
        using var r = new InventoryGivesRig(Dataserver);
        UUID tex = UUID.Random();
        r.H.Scene.AssetService.Store(AssetHelpers.CreateAsset(tex, AssetType.Texture, new byte[] { 1, 2, 3 }, UUID.Zero));
        r.Accounted(a => a.llGetNotecardLine(tex.ToString(), 0));
        Assert.True(r.H.PumpUntil(() => r.Errors.Count > 0), "no error was said");
        Assert.Equal(new[] { "Script error: Notecard '" + tex + "' could not be found." }, r.Errors);
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("ds "));
    }

    [Fact]
    public void LinkNotecardSegmentSaysWhenNoPrimHoldsTheNotecard()
    {
        using var r = new InventoryGivesRig(Dataserver);
        Assert.Equal(UUID.Zero.ToString(), r.Accounted(a => a.iwGetLinkNotecardSegment(1, "nope", 0, 0, 5)).Ret);
        Assert.Equal(new[] { "Script error: iwGetLinkNotecardSegment: Notecard 'nope' not found in link 1." }, r.Errors);
    }

    [Fact]
    public void MakeNotecardWaitsOnEveryPath()
    {
        using var r = new InventoryGivesRig();
        Assert.Equal(5000, r.Accounted(a => { a.iwMakeNotecard("", L("x")); return null; }).Ms);
        var big = L(Enumerable.Range(0, 9000).Select(_ => (object)1234567).ToArray());   // 72K characters as text
        Assert.Equal(5000, r.Accounted(a => { a.iwMakeNotecard("big", big); return null; }).Ms);
        Assert.Null(r.H.Prim.Inventory.GetInventoryItem("big"));
    }

    [Fact]
    public void LandmarkDataIsRelativeToThisRegion()
    {
        using var r = new InventoryGivesRig(Dataserver);
        uint x = r.H.Scene.RegionInfo.WorldLocX, y = r.H.Scene.RegionInfo.WorldLocY;
        ulong east = Utils.UIntsToLong(x + 256, y);
        string text = "Landmark version 2\nregion_id " + UUID.Random() + "\nlocal_pos 10 20 30\nregion_handle " + east + "\n";
        UUID asset = UUID.Random();
        r.H.Scene.AssetService.Store(AssetHelpers.CreateAsset(asset, AssetType.Landmark, Encoding.UTF8.GetBytes(text), UUID.Zero));
        r.H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = "lm", AssetID = asset, ItemID = UUID.Random(), Type = (int)AssetType.Landmark, InvType = (int)InventoryType.Landmark
        }, true);
        Assert.Equal(Exp(new Vector3(266, 20, 30).ToString()), Read(r, a => a.llRequestInventoryData("lm")));
    }

    [Fact]
    public void AMissingLandmarkIsEmptySaidAndStillSleeps()
    {
        using var r = new InventoryGivesRig(Dataserver);
        var (ms, ret) = r.Accounted(a => a.llRequestInventoryData("nope"));
        Assert.Equal("", ret);
        Assert.Equal(1000, ms);
        Assert.Equal(new[] { "Script error: No landmark named 'nope'" }, r.Errors);
        r.H.PumpFor(TimeSpan.FromMilliseconds(300));   // a "did not happen" window: no dataserver answer
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("ds "));
    }
}
