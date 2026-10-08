/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// osMakeNotecard, both forms, ported from OSSL_Api.cs (osMakeNotecard and SaveNotecard) under the same [OSSL] key and
/// threat level (High): the notecard lands in the script's prim with the given name and text, a name already there
/// gets the region's next free "name 1", "name 2" ..., and the script is told nothing. The parity cases run the
/// OSSL implementation itself under YEngine in the same scene and compare the stored notecard byte for byte.
/// Each test has its own harness and scene, so the class runs in parallel. No network.
/// </summary>
public class OsMakeNotecardTests
{
    private readonly ITestOutputHelper _out;
    public OsMakeNotecardTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private static SchedulerHarness Scene(string threat = "High", bool withYEngine = false)
        => new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat), withYEngine);

    private static string Errors(SchedulerHarness h)
        => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    private static TaskInventoryItem[] Notecards(SceneObjectPart part)
        => part.Inventory.GetInventoryItems().Where(i => i.Type == (int)AssetType.Notecard).OrderBy(i => i.Name, StringComparer.Ordinal).ToArray();

    private static string Text(SchedulerHarness h, TaskInventoryItem item)
        => Encoding.UTF8.GetString(h.Scene.AssetService.Get(item.AssetID.ToString()).Data);

    /// <summary>The asset text OSSL's SaveNotecard writes for <paramref name="body"/> (its byte length in the header).</summary>
    private static string Expected(string body)
        => "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length "
           + Encoding.UTF8.GetByteCount(body) + "\n" + body + "}";

    private void RunToDone(SchedulerHarness h, string body)
    {
        h.RezScript("default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("done") || Errors(h).Length > 0), "said: " + string.Join(" | ", h.Said));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");
    }

    [Fact]
    public void TheStringFormWritesTheTextAndANewlineToTheScriptsPrim()
    {
        using var h = Scene();
        RunToDone(h, @"osMakeNotecard(""made"", ""hello\nworld"");");
        Assert.Contains("done", h.Said);
        Assert.Empty(Errors(h));

        var item = Assert.Single(Notecards(h.Prim));
        Assert.Equal("made", item.Name);
        Assert.Equal(Expected("hello\nworld\n"), Text(h, item));

        Assert.Equal("Script generated notecard", item.Description);
        Assert.Equal((int)InventoryType.Notecard, item.InvType);
        Assert.Equal(h.Prim.OwnerID, item.OwnerID);
        Assert.Equal(h.Prim.OwnerID, item.CreatorID);
        Assert.Equal((uint)PermissionMask.All | (uint)PermissionMask.Export, item.BasePermissions);
        Assert.Equal((uint)PermissionMask.All | (uint)PermissionMask.Export, item.CurrentPermissions);
        Assert.Equal((uint)PermissionMask.All, item.NextPermissions);
        Assert.Equal(0u, item.EveryonePermissions);
        Assert.Equal(0u, item.GroupPermissions);
        Assert.Equal(h.Prim.UUID, item.ParentPartID);

        var asset = h.Scene.AssetService.Get(item.AssetID.ToString());
        Assert.Equal((sbyte)AssetType.Notecard, asset.Type);
        Assert.Equal("made", asset.Name);
        Assert.Equal("Script generated notecard", asset.Description);
        Assert.Equal(h.Prim.OwnerID.ToString(), asset.Metadata.CreatorID);
    }

    [Fact]
    public void TheListFormWritesEachItemOnItsOwnLine()
    {
        using var h = Scene();
        RunToDone(h, @"osMakeNotecard(""made"", [""first"", ""second line"", """"]);");
        Assert.Contains("done", h.Said);
        Assert.Empty(Errors(h));
        var item = Assert.Single(Notecards(h.Prim));
        Assert.Equal(Expected("first\nsecond line\n\n"), Text(h, item));
    }

    /// <summary>A name already in the prim is never replaced: the new notecard takes the next free "name n".</summary>
    [Fact]
    public void AnExistingNameIsKeptAndTheNewNotecardTakesTheNextFreeName()
    {
        using var h = Scene();
        InventoryGivesRig.AddNotecard(h, h.Prim, "made", "already here");
        RunToDone(h, @"osMakeNotecard(""made"", ""one""); osMakeNotecard(""made"", [""two""]);");
        Assert.Contains("done", h.Said);
        Assert.Empty(Errors(h));

        var cards = Notecards(h.Prim);
        Assert.Equal(new[] { "made", "made 1", "made 2" }, cards.Select(c => c.Name));
        Assert.Contains("already here", Text(h, cards[0]));
        Assert.Equal(Expected("one\n"), Text(h, cards[1]));
        Assert.Equal(Expected("two\n"), Text(h, cards[2]));
    }

    /// <summary>The script can read what it wrote, at once, with llGetNotecardLine.</summary>
    [Fact]
    public void TheScriptReadsBackWhatItWrote()
    {
        using var h = Scene();
        h.RezScript(@"default {
            state_entry() { osMakeNotecard(""made"", [""alpha"", ""beta""]); llGetNotecardLine(""made"", 1); }
            dataserver(key q, string d) { llSay(0, ""read "" + d); }
        }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("read beta") || Errors(h).Length > 0), string.Join(" | ", h.Said) + " " + Errors(h));
        Assert.Contains("read beta", h.Said);
    }

    [Theory]
    [InlineData("VeryLow")]
    [InlineData("Moderate")]
    [InlineData("VeryHigh")]
    public void TheThreatLevelGateDecides(string threat)
    {
        bool allowed = threat == "VeryHigh";
        using var h = Scene(threat);
        RunToDone(h, @"osMakeNotecard(""made"", ""text""); osMakeNotecard(""other"", [""text""]);");
        if (allowed)
        {
            Assert.Contains("done", h.Said);
            Assert.Equal(2, Notecards(h.Prim).Length);
            return;
        }
        Assert.DoesNotContain("done", h.Said);
        Assert.Empty(Notecards(h.Prim));
        Assert.Contains("OSSL Permission Error: osMakeNotecard permission denied.  Allowed threat level is " + threat
                        + " but function threat level is High", Errors(h));
    }

    /// <summary>[OSSL] Allow_osMakeNotecard names who may call it, whatever the threat level, as for every gated function.</summary>
    [Fact]
    public void TheAllowKeyOpensItForTheOwnerBelowTheThreatLevel()
    {
        using var h = new SchedulerHarness(cfg =>
        {
            var ossl = cfg.AddConfig("OSSL");
            ossl.Set("OSFunctionThreatLevel", "VeryLow");
            ossl.Set("Allow_osMakeNotecard", "true");
        });
        RunToDone(h, @"osMakeNotecard(""made"", ""text"");");
        Assert.Contains("done", h.Said);
        Assert.Single(Notecards(h.Prim));
    }

    public static TheoryData<string> ParityCalls => new()
    {
        @"""hello\nworld""",
        @"""""",
        @"""café 日本""",   // multi-byte UTF-8: the header counts bytes
        @"[""first"", ""second line"", """"]",
        @"[]",
        @"[""a"", 1, -2, 2.5, <1.0, 2.0, 3.0>, <0.0, 0.0, 0.0, 1.0>, (key)""00000000-0000-4000-8000-000000000001""]",
    };

    /// <summary>
    /// The same call made by the OSSL implementation (a YEngine script in another prim of the same scene) and by Phlox
    /// stores the same notecard text, and the same item name, description and permissions.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParityCalls))]
    public void PhloxWritesTheSameNotecardAsTheOsslImplementation(string contents)
    {
        using var h = Scene(withYEngine: true);
        var yPart = SceneHelpers.AddSceneObject(h.Scene, "yengine prim", h.Prim.OwnerID).RootPart;
        string body = "osMakeNotecard(\"made\", " + contents + "); llSay(0, \"done\");";
        SchedulerHarnessYEngine.Rez(h, yPart, "default { state_entry() { " + body + " } }");
        h.RezScript("default { state_entry() { " + body + " } }");
        Assert.True(h.PumpUntil(() => h.Said.Count(s => s == "done") == 2, TimeSpan.FromSeconds(30)),
            "said: " + string.Join(" | ", h.Said) + " errors: " + Errors(h));

        var y = Assert.Single(Notecards(yPart));
        var p = Assert.Single(Notecards(h.Prim));
        _out.WriteLine("yengine: " + Text(h, y).Replace("\n", "\\n"));
        _out.WriteLine("phlox:   " + Text(h, p).Replace("\n", "\\n"));
        Assert.Equal(h.Scene.AssetService.Get(y.AssetID.ToString()).Data, h.Scene.AssetService.Get(p.AssetID.ToString()).Data);
        Assert.Equal(y.Name, p.Name);
        Assert.Equal(y.Description, p.Description);
        Assert.Equal(y.BasePermissions, p.BasePermissions);
        Assert.Equal(y.CurrentPermissions, p.CurrentPermissions);
        Assert.Equal(y.NextPermissions, p.NextPermissions);
        Assert.Equal(y.EveryonePermissions, p.EveryonePermissions);
        Assert.Equal(y.GroupPermissions, p.GroupPermissions);
        Assert.Equal(y.CreatorID, p.CreatorID);
        Assert.Empty(Errors(h));
    }

    /// <summary>
    /// A text past 65536 bytes: whatever the OSSL implementation stores, Phlox stores the same. 22384 three-byte
    /// characters (67152 bytes of UTF-8), so the script stays inside its memory while the text is past the limit.
    /// </summary>
    [Fact]
    public void ALongTextIsStoredAsTheOsslImplementationStoresIt()
    {
        using var h = Scene(withYEngine: true);
        var yPart = SceneHelpers.AddSceneObject(h.Scene, "yengine prim", h.Prim.OwnerID).RootPart;
        const string body = @"string s = ""日""; while (llStringLength(s) < 16384) s += s; s += llGetSubString(s, 0, 5999);
            osMakeNotecard(""made"", s); llSay(0, ""done"");";
        SchedulerHarnessYEngine.Rez(h, yPart, "default { state_entry() { " + body + " } }");
        h.RezScript("default { state_entry() { " + body + " } }");
        Assert.True(h.PumpUntil(() => h.Said.Count(s => s == "done") == 2 || Errors(h).Length > 0, TimeSpan.FromSeconds(30)),
            "said: " + string.Join(" | ", h.Said) + " errors: " + Errors(h));
        Assert.True(Errors(h).Length == 0, Errors(h));
        var y = h.Scene.AssetService.Get(Assert.Single(Notecards(yPart)).AssetID.ToString()).Data;
        var p = h.Scene.AssetService.Get(Assert.Single(Notecards(h.Prim)).AssetID.ToString()).Data;
        _out.WriteLine("yengine " + y.Length + " bytes, phlox " + p.Length + " bytes");
        Assert.True(p.Length < 67152, "the text was not cut: " + p.Length + " bytes");
        Assert.Equal(y, p);
    }
}
