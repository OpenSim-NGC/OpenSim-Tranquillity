using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The OSSL read-only remainder, each ported from OSSL_Api.cs under its upstream threat level through
/// OsslGate. Value assertions per group; the notecard trio reads a real notecard asset from the harness asset service.
/// </summary>
public class OsslReadOnlyTests
{
    private readonly ITestOutputHelper _out;
    public OsslReadOnlyTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private static SchedulerHarness Scene(string threat = "Severe")
        => new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat));

    private string Errors(SchedulerHarness h) => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    // ------------------------------------------------------------------ the notecard trio

    [Fact]
    public void TheNotecardTrioReadsARealNotecardAssetSynchronously()
    {
        using var h = Scene();
        TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, h.Prim, "cfg", UUID.Random(), UUID.Random(), "first line\nsecond line");
        h.RezScript(@"default { state_entry() {
            llSay(0, ""n="" + (string)osGetNumberOfNotecardLines(""cfg""));
            llSay(0, ""l0="" + osGetNotecardLine(""cfg"", 0));
            llSay(0, ""l1="" + osGetNotecardLine(""cfg"", 1));
            llSay(0, ""l9="" + osGetNotecardLine(""cfg"", 9) + ""|"");
            llSay(0, ""all="" + osGetNotecard(""cfg""));
            llSay(0, ""missing="" + (string)osGetNumberOfNotecardLines(""nope""));
        } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("missing=")) &&
                          h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("Notecard 'nope' could not be found")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("n=2", h.Said);
        Assert.Contains("l0=first line", h.Said);
        Assert.Contains("l1=second line", h.Said);
        Assert.Contains("l9=\n\n\n|", h.Said);                        // past the end: EOF, as the cache answers upstream
        Assert.Contains("all=first line\nsecond line\n", h.Said);
        Assert.Contains("missing=-1", h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("Notecard 'nope' could not be found"));
    }

    // ------------------------------------------------------------------ inventory

    [Fact]
    public void TheInventoryFamilyAnswersByNameByIdAndByPermission()
    {
        using var h = Scene();
        var nc = TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, h.Prim, "cfg", UUID.Random(), UUID.Random(), "x");
        nc.Description = "the config";
        nc.CurrentPermissions = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Transfer | OpenSim.Framework.PermissionMask.Modify);
        var locked = TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, h.Prim, "secret", UUID.Random(), UUID.Random(), "y");
        locked.CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.Copy;   // no transfer: its key must not be handed out

        h.RezScript(@"default { state_entry() {
            key k = osGetInventoryItemKey(""cfg"");
            llSay(0, ""key="" + (string)(k == """ + nc.ItemID + @"""));
            llSay(0, ""name="" + osGetInventoryName(k));
            llSay(0, ""desc="" + osGetInventoryDesc(""cfg""));
            llSay(0, ""locked="" + (string)osGetInventoryItemKey(""secret""));
            llSay(0, ""names="" + llDumpList2String(osGetInventoryNames(INVENTORY_NOTECARD), "",""));
            llSay(0, ""keys="" + (string)llGetListLength(osGetInventoryItemKeys(INVENTORY_NOTECARD)));
            llSay(0, ""lname="" + osGetLinkInventoryName(LINK_THIS, k) + ""|ldesc="" + osGetLinkInventoryDesc(LINK_THIS, ""cfg""));
            llSay(0, ""lkey="" + (string)(osGetLinkInventoryKey(LINK_THIS, ""cfg"", INVENTORY_NOTECARD) == """ + nc.AssetID + @"""));
            llSay(0, ""lnames="" + (string)llGetListLength(osGetLinkInventoryNames(LINK_THIS, -1)));
            llSay(0, ""owner="" + (string)(osGetInventoryLastOwner(""cfg"") == """ + nc.OwnerID + @"""));
        } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("owner=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("key=1", h.Said);
        Assert.Contains("name=cfg", h.Said);
        Assert.Contains("desc=the config", h.Said);
        Assert.Contains("locked=" + UUID.Zero, h.Said);            // not full-permission: NULL_KEY, as upstream
        Assert.Contains("names=cfg,secret", h.Said);
        Assert.Contains("keys=1", h.Said);                          // only the full-permission one
        Assert.Contains("lname=cfg|ldesc=the config", h.Said);
        Assert.Contains("lkey=1", h.Said);
        Assert.Contains("lnames=3", h.Said);                        // two notecards and the script itself
        Assert.Contains("owner=1", h.Said);
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ prim, parcel, misc

    [Fact]
    public void PrimParcelAndMiscReadersAnswerFromTheScene()
    {
        using var h = Scene();
        var lmm = new LandManagementModule();
        SceneHelpers.SetupSceneModules(h.Scene, h.Config, lmm);
        lmm.EventManagerOnNoLandDataFromStorage();
        var other = SceneHelpers.AddSceneObject(h.Scene, "other prim", h.Prim.OwnerID);
        h.Prim.SitActiveRange = 7.5f;
        h.Prim.StandOffset = new Vector3(1, 2, 3);
        var parcel = h.Scene.LandChannel.GetLandObject(h.Prim.AbsolutePosition.X, h.Prim.AbsolutePosition.Y);

        h.RezScript(@"default { state_entry() {
            llSay(0, ""prims="" + (string)osGetPrimCount() + ""|other="" + (string)osGetPrimCount(""" + other.UUID + @""") + ""|bogus="" + (string)osGetPrimCount(""x""));
            llSay(0, ""sit="" + (string)osGetSitActiveRange() + ""|lsit="" + (string)osGetLinkSitActiveRange(LINK_THIS) + ""|stand="" + (string)osGetStandTarget());
            llSay(0, ""seated="" + (string)osGetSittingAvatarsCount());
            llSetColor(<1,0,0>, ALL_SIDES);
            llSay(0, ""color="" + (string)osGetLinkColor(LINK_THIS, 0));
            llSay(0, ""link="" + (string)osGetLinkNumber(""Phlox test prim"") + ""|nolink="" + (string)osGetLinkNumber(""nope""));
            llSay(0, ""uuid="" + (string)osIsUUID(llGetKey()) + (string)osIsUUID(""hello""));
            llSay(0, ""parcel="" + (string)(osGetParcelID() == """ + parcel.GlobalID + @""") + ""|ids="" + (string)llGetListLength(osGetParcelIDs()) + ""|dwell="" + (string)osGetParcelDwell(llGetPos()));
            llSay(0, ""rezzer="" + (string)osGetRezzingObject());
            llSay(0, ""t24="" + osGetApparentTimeString(TRUE) + ""|t12="" + osGetApparentTimeString(FALSE) + ""|pst="" + (string)(osGetPSTWallclock() >= 0.0));
            llSay(0, ""home="" + osGetAvatarHomeURI(llGetOwner()) + ""|"");
        } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("home=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("prims=1|other=1|bogus=0", h.Said);
        Assert.Contains("sit=7.500000|lsit=7.500000|stand=<1.00000, 2.00000, 3.00000>", h.Said);
        Assert.Contains("seated=0", h.Said);
        Assert.Contains("color=<1.00000, 0.00000, 0.00000>", h.Said);
        Assert.Contains("link=0|nolink=-1", h.Said);               // an unlinked prim's link number is 0, as llGetLinkNumber says
        Assert.Contains("uuid=10", h.Said);
        Assert.Contains("parcel=1|ids=1|dwell=0", h.Said);
        Assert.Contains("rezzer=" + h.Prim.ParentGroup.RezzerID, h.Said);   // the helper's rezzer id is no presence here, so it reads as an object
        Assert.Contains("t24=00:00:00|t12=0:00:00 AM|pst=1", h.Said);   // no environment module in the harness
        Assert.Contains(h.Said, s => s.StartsWith("home=") && s.EndsWith("|"));
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ avatars and listens

    [Fact]
    public void AvatarReadersAndTheRegexListenWorkOnAPresence()
    {
        using var h = Scene();
        var sp = h.Scene.GetScenePresence(h.AddClient().AgentId);
        sp.HealRate = 2.5f;

        h.RezScript(@"default { state_entry() {
            llSay(0, ""heal="" + (string)osGetHealRate(""" + sp.UUID + @""") + ""|gender="" + osGetGender(""" + sp.UUID + @""") + ""|nogender="" + osGetGender(""nope""));
            llSay(0, ""att="" + llDumpList2String(osGetNumberOfAttachments(""" + sp.UUID + @""", [ATTACH_CHEST, ATTACH_HEAD]), "",""));
            llSay(0, ""country="" + osGetAgentCountry(""" + sp.UUID + @""") + ""|"");
            integer h = osListenRegex(7, """", NULL_KEY, ""^hel+o$"", OS_LISTEN_REGEX_MESSAGE);
            llSay(0, ""handle="" + (string)(h > 0));
            llSay(0, ""bad="" + (string)osListenRegex(7, ""("", NULL_KEY, """", OS_LISTEN_REGEX_NAME));
        }
        listen(integer c, string n, key k, string m) { llSay(0, ""heard="" + m); } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Contains("handle=1") && h.Said.Any(s => s.StartsWith("bad=")));
        // a second prim speaks on channel 7 once the listener is up: "hello" matches ^hel+o$, "goodbye" does not
        var speaker = SceneHelpers.AddSceneObject(h.Scene, "speaker", h.Prim.OwnerID);
        h.RezScriptInto(speaker.RootPart, "default { state_entry() { llSay(7, \"hello\"); llSay(7, \"goodbye\"); } }");
        h.PumpFor(TimeSpan.FromSeconds(3));
        h.PumpUntil(() => h.Said.Contains("heard=hello"));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("heal=2.500000|gender=female|nogender=unknown", h.Said);   // the default shape's male param is 0
        Assert.Contains("att=1,0,2,0", h.Said);
        Assert.Contains("country=|", h.Said);
        Assert.Contains("handle=1", h.Said);
        Assert.Contains("bad=-1", h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("Name regex is invalid"));
        Assert.Contains("heard=hello", h.Said);
        Assert.DoesNotContain("heard=goodbye", h.Said);
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    public void TheGateHoldsAtItsUpstreamLevel()
    {
        using var h = Scene("VeryLow");
        TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, h.Prim, "cfg", UUID.Random(), UUID.Random(), "first line");
        h.RezScript(@"default { state_entry() { osGetNotecardLine(""cfg"", 0); llSay(0, ""after""); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osGetNotecardLine permission denied")));

        Assert.DoesNotContain("after", h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osGetNotecardLine permission denied"));
    }
}
