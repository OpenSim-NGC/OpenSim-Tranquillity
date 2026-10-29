/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using InWorldz.Phlox.Compiler;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The ll functions the engine did not have as Second Life defines them: llGetAttachedListFiltered,
/// llFindNotecardTextSync, llFindNotecardTextCount, llSetParcelForSale, llGiveAgentInventory, llTransferOwnership,
/// llMapBeacon, llRemoteLoadScript, llRemoteDataSetRegion and the three llGetGameControlMode functions. The reference is
/// the function's page on the SL wiki and its entry in LL's lsl-definitions (secondlife/lsl-definitions,
/// lsl_definitions.yaml), named beside each group. Where those documents are silent on a case, the test says so and
/// pins the choice the engine made.
/// </summary>
// No process-wide state: each test builds its own harness, so the class runs in parallel with the others.
public class MissingLlFunctionTests
{
    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const int ATTACH_CHEST = 1, ATTACH_SKULL = 2, ATTACH_PELVIS = 10, ATTACH_ANY_HUD = -1;
    private const int FILTER_INCLUDE = 1, FILTER_FLAGS = 2, FILTER_FLAG_HUDS = 1;
    private const int TRANSFER_OK = 0, TRANSFER_BAD_OPTS = -1, TRANSFER_NO_TARGET = -2, TRANSFER_NO_ITEMS = -4,
        TRANSFER_BAD_ROOT = -5, TRANSFER_NO_PERMS = -6, TRANSFER_NO_ATTACHMENT = -7;
    private const int TRANSFER_DEST = 0, TRANSFER_FLAGS = 1, TRANSFER_FLAG_TAKE = 2, TRANSFER_FLAG_COPY = 4;
    private const int PARCEL_SALE_PRICE = 1, PARCEL_SALE_AGENT = 2, PARCEL_SALE_OBJECTS = 3;
    private const int PARCEL_SALE_OK = 0, PARCEL_SALE_ERROR_NO_PERMISSIONS = 2, PARCEL_SALE_ERROR_INVALID_PRICE = 4,
        PARCEL_SALE_ERROR_BAD_PARAMS = 5;
    private const int PERMISSION_PRIVILEGED_LAND_ACCESS = 524288;
    private const string NAK = "\n\u0015\n";

    private static LSLList L(params object[] items) => new LSLList(new List<object>(items));

    // ------------------------------------------------------------------ the compiler knows them

    [Theory]
    [InlineData("list l = llGetAttachedListFiltered(llGetOwner(), [FILTER_INCLUDE, ATTACH_CHEST]);")]
    [InlineData("list l = llFindNotecardTextSync(\"n\", \"a+\", 0, 10, []);")]
    [InlineData("key k = llFindNotecardTextCount(\"n\", \"a+\", []);")]
    [InlineData("integer i = llSetParcelForSale(TRUE, [PARCEL_SALE_PRICE, 100, PARCEL_SALE_OBJECTS, TRUE]);")]
    [InlineData("llRemoteLoadScript(llGetKey(), \"s\", TRUE, 0);")]
    [InlineData("llRemoteDataSetRegion();")]
    [InlineData("integer i = llGetGameControlMode(llGetOwner());")]
    [InlineData("list l = llGetGameControlModeAxes(llGetOwner());")]
    [InlineData("integer i = llGetGameControlModeButtons(llGetOwner());")]
    [InlineData("llMapBeacon(\"Region\", <1,2,3>, [BEACON_MAP, TRUE, FALSE]);")]
    [InlineData("integer i = llGiveAgentInventory(llGetOwner(), \"f\", [\"a\", \"b\"], [TRANSFER_DEST, \"x|y\"]);")]
    [InlineData("integer i = llTransferOwnership(llGetOwner(), TRANSFER_FLAG_COPY, []);")]
    public void TheFunctionCompilesWithSecondLifesSignature(string statement)
    {
        var c = PhloxCompiler.CompileInDefault(statement);
        Assert.False(c.HasErrors(), c.Report);
    }

    [Theory]
    [InlineData("list l = llGetAttachedListFiltered(llGetOwner(), 0);")]
    [InlineData("integer i = llFindNotecardTextSync(\"n\", \"a\", 0, 0);")]
    [InlineData("llGiveAgentInventory(llGetOwner(), \"a\");")]
    [InlineData("llMapBeacon(llGetOwner(), \"t\", <1,1,1>);")]
    [InlineData("integer i = llTransferOwnership(llGetOwner());")]
    public void TheEarlierShapeOfAFunctionStillCompiles(string statement)
    {
        var c = PhloxCompiler.CompileInDefault(statement);
        Assert.False(c.HasErrors(), c.Report);
    }

    [Fact]
    public void TheGameControlModesHaveLLsValues()
    {
        // lsl-definitions: GAME_CONTROL_MODE_AVATAR 0, MOUSELOOK 1, FLYCAM 2, CAPTIVE 3, CURSOR 4.
        using var r = new ApiCallRig(source:
            "default { state_entry() { llSay(0, \"modes=\" + (string)GAME_CONTROL_MODE_AVATAR + (string)GAME_CONTROL_MODE_MOUSELOOK" +
            " + (string)GAME_CONTROL_MODE_FLYCAM + (string)GAME_CONTROL_MODE_CAPTIVE + (string)GAME_CONTROL_MODE_CURSOR); } }");
        Assert.True(r.H.PumpUntil(() => r.H.Said.Any(s => s.StartsWith("modes="))), string.Join(" | ", r.H.Said));
        Assert.Equal("modes=01234", r.H.Said.First(s => s.StartsWith("modes=")));
    }

    // ------------------------------------------------------------------ llGetAttachedListFiltered
    // SL wiki LlGetAttachedListFiltered: [FILTER_INCLUDE, point] limits to a point and may repeat; with none, every point;
    // HUDs only with FILTER_FLAG_HUDS and an experience the script, the avatar and the HUD share; "NOT FOUND" for a key
    // that is no agent.

    private static SceneObjectGroup Wear(ApiCallRig r, ScenePresence sp, uint point)
    {
        var sog = SceneHelpers.AddSceneObject(r.H.Scene, "worn at " + point, sp.UUID);
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = point;
        sp.AddAttachment(sog);
        return sog;
    }

    private static HashSet<string> Keys(LSLList l) => l.Data.Select(o => o.ToString()).ToHashSet();

    [Fact]
    public void AnIncludeLimitsTheListToThatAttachmentPoint()
    {
        using var r = new ApiCallRig();
        var sp = r.AddAvatar();
        var chest = Wear(r, sp, ATTACH_CHEST);
        Wear(r, sp, ATTACH_SKULL);
        Assert.Equal(new[] { chest.UUID.ToString() }.ToHashSet(),
            Keys(r.Api.llGetAttachedListFiltered(sp.UUID.ToString(), L(FILTER_INCLUDE, ATTACH_CHEST))));
    }

    [Fact]
    public void RepeatedIncludesAddTheirPointsAndNoOptionsGiveEveryPublicAttachment()
    {
        using var r = new ApiCallRig();
        var sp = r.AddAvatar();
        var chest = Wear(r, sp, ATTACH_CHEST);
        var skull = Wear(r, sp, ATTACH_SKULL);
        var pelvis = Wear(r, sp, ATTACH_PELVIS);
        Assert.Equal(new[] { chest.UUID.ToString(), skull.UUID.ToString() }.ToHashSet(),
            Keys(r.Api.llGetAttachedListFiltered(sp.UUID.ToString(), L(FILTER_INCLUDE, ATTACH_CHEST, FILTER_INCLUDE, ATTACH_SKULL))));
        Assert.Equal(new[] { chest.UUID.ToString(), skull.UUID.ToString(), pelvis.UUID.ToString() }.ToHashSet(),
            Keys(r.Api.llGetAttachedListFiltered(sp.UUID.ToString(), L())));
    }

    [Fact]
    public void HudsStayOutWithoutTheExperienceTheWikiRequires()
    {
        using var r = new ApiCallRig();
        var sp = r.AddAvatar();
        var chest = Wear(r, sp, ATTACH_CHEST);
        Wear(r, sp, (uint)AttachmentPoint.HUDCenter2);
        // The script has no experience, so FILTER_FLAG_HUDS adds nothing and ATTACH_ANY_HUD finds nothing.
        Assert.Equal(new[] { chest.UUID.ToString() }.ToHashSet(),
            Keys(r.Api.llGetAttachedListFiltered(sp.UUID.ToString(), L(FILTER_FLAGS, FILTER_FLAG_HUDS))));
        Assert.Empty(r.Api.llGetAttachedListFiltered(sp.UUID.ToString(), L(FILTER_FLAGS, FILTER_FLAG_HUDS, FILTER_INCLUDE, ATTACH_ANY_HUD)).Data);
    }

    [Fact]
    public void AKeyThatIsNoAgentAnswersNotFound()
    {
        using var r = new ApiCallRig();
        Assert.Equal(new object[] { "NOT FOUND" }, r.Api.llGetAttachedListFiltered(UUID.Random().ToString(), L()).Data);
        Assert.Equal(new object[] { "NOT FOUND" }, r.Api.llGetAttachedListFiltered("not a key", L()).Data);
    }

    // ------------------------------------------------------------------ llFindNotecardTextSync (5 arguments)
    // lsl-definitions func-id 508 and SL wiki LlFindNotecardTextSync: a regular expression; a strided list [line, column,
    // length, ...]; start skips matches; count 0 means up to 64; invalid pattern: an error on DEBUG_CHANNEL and an empty
    // list; missing notecard: an error on DEBUG_CHANNEL and NAK.

    private static ApiCallRig WithCard(string text, out TaskInventoryItem card)
    {
        var r = new ApiCallRig();
        card = AddNotecard(r.H, r.H.Prim, "card", text);
        return r;
    }

    [Fact]
    public void EachMatchIsLineColumnAndLength()
    {
        using var r = WithCard("alpha beta\nBeta gamma\nalpha", out _);
        Assert.Equal(new object[] { 0, 6, 4, 1, 0, 4 }, r.Api.llFindNotecardTextSync("card", "[Bb]eta", 0, 0, L()).Data);
    }

    [Fact]
    public void StartSkipsMatchesAndCountLimitsThem()
    {
        using var r = WithCard("a a a\na", out _);
        Assert.Equal(new object[] { 0, 2, 1, 0, 4, 1 }, r.Api.llFindNotecardTextSync("card", "a", 1, 2, L()).Data);
        Assert.Equal(new object[] { 0, 0, 1 }, r.Api.llFindNotecardTextSync("card", "a", 0, 1, L()).Data);
    }

    [Fact]
    public void ACountOfZeroReturnsAtMostSixtyFourMatches()
    {
        using var r = WithCard(string.Join("\n", Enumerable.Repeat("x", 100)), out _);
        var list = r.Api.llFindNotecardTextSync("card", "x", 0, 0, L());
        Assert.Equal(192, list.Length);
        Assert.Equal(new object[] { 63, 0, 1 }, list.Data.Skip(189).ToArray());
        Assert.Equal(192, r.Api.llFindNotecardTextSync("card", "x", 0, 500, L()).Length);
    }

    [Fact]
    public void ACaseFoldingPatternMatchesAnyCase()
    {
        using var r = WithCard("Hello HELLO hello", out _);
        Assert.Equal(new object[] { 0, 0, 5, 0, 6, 5, 0, 12, 5 }, r.Api.llFindNotecardTextSync("card", "(?i)hello", 0, 0, L()).Data);
        Assert.Equal(new object[] { 0, 12, 5 }, r.Api.llFindNotecardTextSync("card", "hello", 0, 0, L()).Data);
    }

    [Fact]
    public void NoMatchIsAnEmptyList()
    {
        using var r = WithCard("alpha", out _);
        Assert.Equal(new object[] { 0, 0, 5 }, r.Api.llFindNotecardTextSync("card", "alpha", 0, 0, L()).Data);
        Assert.Empty(r.Api.llFindNotecardTextSync("card", "zzz", 0, 0, L()).Data);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void AnInvalidPatternShoutsAndAnswersEmpty()
    {
        using var r = WithCard("alpha", out _);
        Assert.Empty(r.Api.llFindNotecardTextSync("card", "(", 0, 0, L()).Data);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void AMissingNotecardShoutsAndAnswersNak()
    {
        using var r = WithCard("alpha", out _);
        Assert.Equal(new object[] { NAK }, r.Api.llFindNotecardTextSync("nope", "a", 0, 0, L()).Data);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void ANotecardMayBeNamedByItsAssetKey()
    {
        using var r = WithCard("alpha beta", out var card);
        Assert.Equal(new object[] { 0, 6, 4 }, r.Api.llFindNotecardTextSync(card.AssetID.ToString(), "beta", 0, 0, L()).Data);
    }

    [Fact]
    public void TheFourArgumentFormStillSearchesPlainText()
    {
        using var r = WithCard("alpha\nbeta", out _);
        Assert.Equal(1, r.Api.llFindNotecardTextSync("card", "beta", 0, 1));
    }

    // ------------------------------------------------------------------ llFindNotecardTextCount
    // lsl-definitions func-id 507: asynchronously requests the number of matches of pattern in the notecard and returns a
    // key query handle; the count arrives as a dataserver event. No delay (sleep 0.0).

    private static string RunCount(ApiCallRig r, string pattern)
    {
        r.H.ClearSaid(r.Item);
        r.H.RezScript("default { state_entry() { key q = llFindNotecardTextCount(\"card\", \"" + pattern + "\", []); llSay(0, \"query=\" + (string)q); }" +
                      " dataserver(key q, string data) { llSay(0, \"count=\" + data); } }");
        Assert.True(r.H.PumpUntil(() => r.H.Said.Any(s => s.StartsWith("count="))), string.Join(" | ", r.H.Said));
        return r.H.Said.First(s => s.StartsWith("count="));
    }

    [Fact]
    public void TheCountOfMatchesArrivesInADataserverEvent()
    {
        using var r = WithCard("beta one\nbeta two beta\nthree", out _);
        Assert.Equal("count=3", RunCount(r, "beta"));
        Assert.DoesNotContain("query=" + UUID.Zero, r.H.Said);
    }

    [Fact]
    public void ACountOfNothingIsZero()
    {
        using var r = WithCard("alpha", out _);
        Assert.Equal("count=0", RunCount(r, "zzz"));
    }

    [Fact]
    public void ACountHasNoDelayAndAnInvalidPatternOrAMissingNotecardShoutsAndGivesNoQuery()
    {
        using var r = WithCard("alpha", out _);
        var ok = r.Accounted(a => a.llFindNotecardTextCount("card", "a", L()));
        Assert.Equal(0, ok.Ms);
        Assert.NotEqual(UUID.Zero.ToString(), (string)ok.Ret);
        Assert.Equal(UUID.Zero.ToString(), r.Api.llFindNotecardTextCount("card", "(", L()));
        Assert.Equal(UUID.Zero.ToString(), r.Api.llFindNotecardTextCount("nope", "a", L()));
        Assert.Equal(2, r.Errors.Count);
    }

    // ------------------------------------------------------------------ llSetParcelForSale
    // SL wiki LlSetParcelForSale: the script is owned by the parcel owner and holds PERMISSION_PRIVILEGED_LAND_ACCESS;
    // PARCEL_SALE_PRICE, _AGENT and _OBJECTS; FALSE takes the parcel off sale and clears the options; codes
    // PARCEL_SALE_OK 0, _ERROR_NO_PERMISSIONS 2, _INVALID_PRICE 4, _BAD_PARAMS 5. (_NO_PARCEL 1 and _IN_ESCROW 3 need a
    // parcel lookup that fails and an escrow, which this region has not.)

    private static (ApiCallRig R, ILandObject Land) SaleRig(bool grant = true, bool ownParcel = true)
    {
        var r = new ApiCallRig();
        var land = new StripLand(r.H.Scene, (256, ownParcel ? r.H.Prim.OwnerID : UUID.Random()));
        r.H.Scene.LandChannel = land;
        if (grant) r.Grant(r.H.Prim.OwnerID, PERMISSION_PRIVILEGED_LAND_ACCESS);
        return (r, land.Parcels[0]);
    }

    [Fact]
    public void AParcelIsPutOnSaleWithAPriceABuyerAndItsObjects()
    {
        var (r, land) = SaleRig();
        using (r)
        {
            var buyer = UUID.Random();
            Assert.Equal(PARCEL_SALE_OK, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, 250, PARCEL_SALE_AGENT, buyer.ToString(), PARCEL_SALE_OBJECTS, 1)));
            Assert.NotEqual(0u, land.LandData.Flags & (uint)ParcelFlags.ForSale);
            Assert.NotEqual(0u, land.LandData.Flags & (uint)ParcelFlags.ForSaleObjects);
            Assert.Equal(250, land.LandData.SalePrice);
            Assert.Equal(buyer, land.LandData.AuthBuyerID);
        }
    }

    [Fact]
    public void FalseTakesTheParcelOffSaleAndClearsTheOptions()
    {
        var (r, land) = SaleRig();
        using (r)
        {
            r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, 250, PARCEL_SALE_AGENT, UUID.Random().ToString(), PARCEL_SALE_OBJECTS, 1));
            Assert.NotEqual(0u, land.LandData.Flags & (uint)ParcelFlags.ForSale);
            Assert.Equal(PARCEL_SALE_OK, r.Api.llSetParcelForSale(0, L()));
            Assert.Equal(0u, land.LandData.Flags & (uint)(ParcelFlags.ForSale | ParcelFlags.ForSaleObjects));
            Assert.Equal(0, land.LandData.SalePrice);
            Assert.Equal(UUID.Zero, land.LandData.AuthBuyerID);
        }
    }

    [Fact]
    public void WithoutThePrivilegedLandAccessGrantTheAnswerIsNoPermissionsAndTheParcelIsUntouched()
    {
        var (r, land) = SaleRig(grant: false);
        using (r)
        {
            Assert.Equal(PARCEL_SALE_ERROR_NO_PERMISSIONS, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, 10)));
            Assert.Equal(0u, land.LandData.Flags & (uint)ParcelFlags.ForSale);
        }
    }

    [Fact]
    public void AParcelOfAnotherOwnerIsNoPermissions()
    {
        var (r, land) = SaleRig(ownParcel: false);
        using (r)
        {
            Assert.Equal(PARCEL_SALE_ERROR_NO_PERMISSIONS, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, 10)));
            Assert.Equal(0u, land.LandData.Flags & (uint)ParcelFlags.ForSale);
        }
    }

    [Fact]
    public void ThePriceMustBeAboveZeroUnlessABuyerIsNamed()
    {
        var (r, land) = SaleRig();
        using (r)
        {
            Assert.Equal(PARCEL_SALE_ERROR_INVALID_PRICE, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, 0)));
            Assert.Equal(PARCEL_SALE_ERROR_INVALID_PRICE, r.Api.llSetParcelForSale(1, L()));
            Assert.Equal(PARCEL_SALE_ERROR_INVALID_PRICE, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, -5, PARCEL_SALE_AGENT, UUID.Random().ToString())));
            Assert.Equal(0u, land.LandData.Flags & (uint)ParcelFlags.ForSale);
            Assert.Equal(PARCEL_SALE_OK, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, 0, PARCEL_SALE_AGENT, UUID.Random().ToString())));
            Assert.NotEqual(0u, land.LandData.Flags & (uint)ParcelFlags.ForSale);
        }
    }

    [Fact]
    public void AnOptionOfTheWrongKindOrAnUnknownOptionIsBadParams()
    {
        var (r, land) = SaleRig();
        using (r)
        {
            Assert.Equal(PARCEL_SALE_ERROR_BAD_PARAMS, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, "ten")));
            Assert.Equal(PARCEL_SALE_ERROR_BAD_PARAMS, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE)));
            Assert.Equal(PARCEL_SALE_ERROR_BAD_PARAMS, r.Api.llSetParcelForSale(1, L(99, 1)));
            Assert.Equal(PARCEL_SALE_ERROR_BAD_PARAMS, r.Api.llSetParcelForSale(1, L(PARCEL_SALE_PRICE, 10, PARCEL_SALE_AGENT, "not a key")));
            Assert.Equal(0u, land.LandData.Flags & (uint)ParcelFlags.ForSale);
        }
    }

    // ------------------------------------------------------------------ llRemoteLoadScript, llRemoteDataSetRegion
    // lsl-definitions: both deprecated; llRemoteLoadScript sleeps 3.0 s, llRemoteDataSetRegion 0.0. OpenSim's own LSL says
    // "Command deprecated" on DEBUG_CHANNEL for both and so does this engine; neither does anything else.

    [Fact]
    public void LlRemoteLoadScriptIsDeprecatedAndSleepsThreeSeconds()
    {
        using var r = new ApiCallRig();
        var done = r.Accounted(a => { a.llRemoteLoadScript(UUID.Random().ToString(), "script", 1, 0); return null; });
        Assert.Equal(3000, done.Ms);
        Assert.Single(r.Errors);
        Assert.Contains("deprecated", r.Errors[0]);
        Assert.Contains("llRemoteLoadScript", r.Errors[0]);
    }

    [Fact]
    public void LlRemoteDataSetRegionIsDeprecatedWithNoDelay()
    {
        using var r = new ApiCallRig();
        var done = r.Accounted(a => { a.llRemoteDataSetRegion(); return null; });
        Assert.Equal(0, done.Ms);
        Assert.Single(r.Errors);
        Assert.Contains("deprecated", r.Errors[0]);
        Assert.Contains("llRemoteDataSetRegion", r.Errors[0]);
    }

    // ------------------------------------------------------------------ llGetGameControlMode, ...Axes, ...Buttons
    // SL wiki LlGetGameControlMode: -1 when the script has no game-control data for the avatar (outside a game_control
    // event, or a viewer that sends none); this region receives none from any viewer. The wiki and lsl-definitions do not
    // say what the other two return then: the engine answers an empty list and 0, the same "no data" read.

    [Fact]
    public void WithoutGameControlDataTheModeIsMinusOneTheAxesEmptyAndTheButtonsZero()
    {
        using var r = new ApiCallRig();
        var sp = r.AddAvatar();
        foreach (string id in new[] { sp.UUID.ToString(), UUID.Random().ToString(), "not a key" })
        {
            Assert.Equal(-1, r.Api.llGetGameControlMode(id));
            Assert.Empty(r.Api.llGetGameControlModeAxes(id).Data);
            Assert.Equal(0, r.Api.llGetGameControlModeButtons(id));
        }
        Assert.Empty(r.Errors);
    }

    // ------------------------------------------------------------------ llMapBeacon
    // lsl-definitions func-id 516: displays a beacon on the viewer of the avatar touching or wearing the object; only for
    // attached scripts or during touch events; sleep 1.0. This engine has no beacon message to a viewer, so the call does
    // what the definition says it does anywhere it has no effect: nothing, after the 1.0 s sleep, with no error.

    [Fact]
    public void LlMapBeaconSleepsOneSecondAndShoutsNothing()
    {
        using var r = new ApiCallRig();
        var done = r.Accounted(a => { a.llMapBeacon("Region", new Vector3(1, 2, 3), L(1, 1, 0)); return null; });
        Assert.Equal(1000, done.Ms);
        Assert.Empty(r.Errors);
    }

    // ------------------------------------------------------------------ llGiveAgentInventory
    // SL wiki LlGiveAgentInventory: the items go to the agent in a new folder; options TRANSFER_DEST (a "|" path of up to
    // four folders) and TRANSFER_FLAGS (none defined); no-copy items are left out, no-transfer items too unless the agent
    // is the script owner; TRANSFER_* codes; the script sleeps 3.0 s.

    private static ScenePresence Present(InventoryGivesRig r)
    {
        var account = UserAccountHelpers.CreateUserWithInventory(r.H.Scene);
        return SceneHelpers.AddScenePresence(r.H.Scene, account.PrincipalID);
    }

    private static int Count(InventoryGivesRig r, UUID user, string name)
    {
        int n = 0;
        foreach (var folder in r.H.Scene.InventoryService.GetInventorySkeleton(user) ?? new List<InventoryFolderBase>())
            n += r.H.Scene.InventoryService.GetFolderItems(user, folder.ID)?.Count(i => i.Name == name) ?? 0;
        return n;
    }

    private static int Folders(InventoryGivesRig r, UUID user, string name)
        => (r.H.Scene.InventoryService.GetInventorySkeleton(user) ?? new List<InventoryFolderBase>()).Count(f => f.Name == name);

    [Fact]
    public void ItemsGoToTheAgentInANewFolderAndTheCallSleepsThreeSeconds()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        AddNotecard(r.H, r.H.Prim, "one");
        AddNotecard(r.H, r.H.Prim, "two");
        var done = r.Accounted(a => a.llGiveAgentInventory(sp.UUID.ToString(), "gifts", L("one", "two"), L()));
        Assert.Equal((3000, (object)TRANSFER_OK), done);
        Assert.Equal(1, Folders(r, sp.UUID, "gifts"));
        Assert.Equal(1, Count(r, sp.UUID, "one"));
        Assert.Equal(1, Count(r, sp.UUID, "two"));
    }

    [Fact]
    public void ANoCopyItemIsLeftOutAndNothingLeftIsNoItems()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        var keep = AddNotecard(r.H, r.H.Prim, "keep");
        var item = AddNotecard(r.H, r.H.Prim, "nocopy");
        item.CurrentPermissions &= ~(uint)OpenSim.Framework.PermissionMask.Copy;
        Assert.Equal(TRANSFER_OK, r.Api.llGiveAgentInventory(sp.UUID.ToString(), "f", L("keep", "nocopy"), L()));
        Assert.Equal(1, Count(r, sp.UUID, "keep"));
        Assert.Equal(0, Count(r, sp.UUID, "nocopy"));
        Assert.Equal(TRANSFER_NO_ITEMS, r.Api.llGiveAgentInventory(sp.UUID.ToString(), "g", L("nocopy"), L()));
        Assert.Equal(TRANSFER_NO_ITEMS, r.Api.llGiveAgentInventory(sp.UUID.ToString(), "h", L(), L()));
        Assert.Equal(TRANSFER_NO_ITEMS, r.Api.llGiveAgentInventory(sp.UUID.ToString(), "i", L("absent"), L()));
        Assert.Equal(0, Folders(r, sp.UUID, "g"));
    }

    [Fact]
    public void ANoTransferItemIsLeftOutExceptForTheScriptOwner()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        var item = AddNotecard(r.H, r.H.Prim, "notrans");
        item.CurrentPermissions &= ~(uint)OpenSim.Framework.PermissionMask.Transfer;
        Assert.Equal(TRANSFER_NO_ITEMS, r.Api.llGiveAgentInventory(sp.UUID.ToString(), "f", L("notrans"), L()));
        Assert.Equal(0, Count(r, sp.UUID, "notrans"));
    }

    [Fact]
    public void AnAgentNotInTheRegionIsNoTarget()
    {
        using var r = new InventoryGivesRig();
        AddNotecard(r.H, r.H.Prim, "one");
        Assert.Equal(TRANSFER_NO_TARGET, r.Api.llGiveAgentInventory(UUID.Random().ToString(), "f", L("one"), L()));
        Assert.Equal(TRANSFER_NO_TARGET, r.Api.llGiveAgentInventory("not a key", "f", L("one"), L()));
    }

    [Fact]
    public void BadOptionsAreBadOptsAndAnEmptyOrTooDeepRootIsBadRoot()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        AddNotecard(r.H, r.H.Prim, "one");
        string id = sp.UUID.ToString();
        Assert.Equal(TRANSFER_BAD_OPTS, r.Api.llGiveAgentInventory(id, "f", L("one"), L(TRANSFER_DEST)));
        Assert.Equal(TRANSFER_BAD_OPTS, r.Api.llGiveAgentInventory(id, "f", L("one"), L(77, "x")));
        Assert.Equal(TRANSFER_BAD_OPTS, r.Api.llGiveAgentInventory(id, "f", L("one"), L(TRANSFER_DEST, 5)));
        Assert.Equal(TRANSFER_BAD_ROOT, r.Api.llGiveAgentInventory(id, "f", L("one"), L(TRANSFER_DEST, "")));
        Assert.Equal(TRANSFER_BAD_ROOT, r.Api.llGiveAgentInventory(id, "f", L("one"), L(TRANSFER_DEST, "||")));
        Assert.Equal(TRANSFER_BAD_ROOT, r.Api.llGiveAgentInventory(id, "f", L("one"), L(TRANSFER_DEST, "a|b|c|d|e")));
        Assert.Equal(0, Folders(r, sp.UUID, "f"));
        Assert.Equal(TRANSFER_OK, r.Api.llGiveAgentInventory(id, "f", L("one"), L(TRANSFER_FLAGS, 0, TRANSFER_DEST, "a|b")));
    }

    [Fact]
    public void AnAttachmentCannotGiveAndAnObjectWithoutTransferIsNoPerms()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        AddNotecard(r.H, r.H.Prim, "one");
        string id = sp.UUID.ToString();
        r.H.Prim.OwnerMask &= ~(uint)OpenSim.Framework.PermissionMask.Transfer;
        r.H.Prim.ParentGroup.InvalidateEffectivePerms();
        Assert.Equal(TRANSFER_NO_PERMS, r.Api.llGiveAgentInventory(id, "f", L("one"), L()));
        r.H.Prim.OwnerMask |= (uint)OpenSim.Framework.PermissionMask.Transfer;
        r.H.Prim.ParentGroup.InvalidateEffectivePerms();
        r.H.Prim.ParentGroup.IsAttachment = true;
        Assert.Equal(TRANSFER_NO_ATTACHMENT, r.Api.llGiveAgentInventory(id, "f", L("one"), L()));
        Assert.Equal(0, Folders(r, sp.UUID, "f"));
    }

    // ------------------------------------------------------------------ llTransferOwnership
    // SL wiki LlTransferOwnership: (agent, flags, options). The object goes to an agent in the region; TAKE puts it in the
    // agent's inventory, COPY gives a copy and the object stays; no options are defined; codes as above.

    private sealed class FakeInventoryAccess : IInventoryAccessModule
    {
        public readonly ConcurrentQueue<(DeRezAction Action, List<SceneObjectGroup> Groups, UUID Agent)> Copies = new();
        public List<InventoryItemBase> CopyToInventory(DeRezAction action, UUID folderID, List<SceneObjectGroup> objectGroups, IClientAPI remoteClient, bool asAttachment)
        {
            Copies.Enqueue((action, objectGroups, remoteClient.AgentId));
            return new List<InventoryItemBase>();
        }
        public UUID CapsUpdateInventoryItemAsset(IClientAPI remoteClient, UUID itemID, byte[] data) => UUID.Zero;
        public bool UpdateInventoryItemAsset(UUID ownerID, InventoryItemBase item, AssetBase asset) => false;
        public SceneObjectGroup RezObject(IClientAPI remoteClient, UUID itemID, UUID rezGroupID, Vector3 RayEnd, Vector3 RayStart, UUID RayTargetID, byte BypassRayCast, bool RayEndIsIntersection, bool RezSelected, bool RemoveItem, UUID fromTaskID, bool attachment) => null;
        public SceneObjectGroup RezObject(IClientAPI remoteClient, UUID itemID, Vector3 RayEnd, Vector3 RayStart, UUID RayTargetID, byte BypassRayCast, bool RayEndIsIntersection, bool RezSelected, bool RemoveItem, UUID fromTaskID, bool attachment) => null;
        public SceneObjectGroup RezObject(IClientAPI remoteClient, InventoryItemBase item, UUID rezGroupID, UUID assetID, Vector3 RayEnd, Vector3 RayStart, UUID RayTargetID, byte BypassRayCast, bool RayEndIsIntersection, bool RezSelected, bool RemoveItem, UUID fromTaskID, bool attachment) => null;
        public SceneObjectGroup RezObject(IClientAPI remoteClient, InventoryItemBase item, UUID assetID, Vector3 RayEnd, Vector3 RayStart, UUID RayTargetID, byte BypassRayCast, bool RayEndIsIntersection, bool RezSelected, bool RemoveItem, UUID fromTaskID, bool attachment) => null;
        public void FetchRemoteHGItemAssets(UUID OwnerID, InventoryItemBase item) { }
        public void TransferInventoryAssets(InventoryItemBase item, UUID sender, UUID receiver) { }
        public bool CanGetAgentInventoryItem(IClientAPI remoteClient, UUID itemID, UUID requestID) => true;
        public bool IsForeignUser(UUID userID, out string assetServerURL) { assetServerURL = null; return false; }
    }

    [Fact]
    public void WithNoFlagsTheObjectChangesOwnerInPlace()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        var result = r.Accounted(a => a.llTransferOwnership(sp.UUID.ToString(), 0, L()));
        Assert.Equal(TRANSFER_OK, result.Ret);
        Assert.Equal(0, result.Ms);
        Assert.Equal(sp.UUID, r.H.Prim.OwnerID);
    }

    [Fact]
    public void TheCopyFlagGivesACopyAndLeavesTheObjectWithItsOwner()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        var access = new FakeInventoryAccess();
        r.H.Scene.RegisterModuleInterface<IInventoryAccessModule>(access);
        var owner = r.H.Prim.OwnerID;
        Assert.Equal(TRANSFER_OK, r.Api.llTransferOwnership(sp.UUID.ToString(), TRANSFER_FLAG_COPY, L()));
        Assert.True(access.Copies.TryDequeue(out var copy));
        Assert.Equal(sp.UUID, copy.Agent);
        Assert.Same(r.H.Prim.ParentGroup, copy.Groups.Single());
        Assert.Equal(owner, r.H.Prim.OwnerID);
        Assert.NotNull(r.H.Scene.GetSceneObjectGroup(r.H.Prim.ParentGroup.UUID));
    }

    [Fact]
    public void TheTakeFlagMovesTheObjectIntoTheAgentsInventory()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        var access = new FakeInventoryAccess();
        r.H.Scene.RegisterModuleInterface<IInventoryAccessModule>(access);
        var id = r.H.Prim.ParentGroup.UUID;
        Assert.Equal(TRANSFER_OK, r.Api.llTransferOwnership(sp.UUID.ToString(), TRANSFER_FLAG_TAKE, L()));
        Assert.True(access.Copies.TryDequeue(out var taken));
        Assert.Equal(sp.UUID, taken.Agent);
        Assert.Null(r.H.Scene.GetSceneObjectGroup(id));
    }

    [Fact]
    public void ARefusedTransferChangesNothing()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        var owner = r.H.Prim.OwnerID;
        string id = sp.UUID.ToString();
        Assert.Equal(TRANSFER_NO_TARGET, r.Api.llTransferOwnership(UUID.Random().ToString(), 0, L()));
        Assert.Equal(TRANSFER_BAD_OPTS, r.Api.llTransferOwnership(id, 0, L(1)));
        Assert.Equal(TRANSFER_BAD_OPTS, r.Api.llTransferOwnership(id, TRANSFER_FLAG_TAKE | TRANSFER_FLAG_COPY, L()));
        Assert.Equal(TRANSFER_BAD_OPTS, r.Api.llTransferOwnership(id, 8, L()));
        r.H.Prim.OwnerMask &= ~(uint)OpenSim.Framework.PermissionMask.Transfer;
        r.H.Prim.ParentGroup.InvalidateEffectivePerms();
        Assert.Equal(TRANSFER_NO_PERMS, r.Api.llTransferOwnership(id, 0, L()));
        r.H.Prim.OwnerMask |= (uint)OpenSim.Framework.PermissionMask.Transfer;
        r.H.Prim.OwnerMask &= ~(uint)OpenSim.Framework.PermissionMask.Copy;
        r.H.Prim.ParentGroup.InvalidateEffectivePerms();
        Assert.Equal(TRANSFER_NO_PERMS, r.Api.llTransferOwnership(id, TRANSFER_FLAG_COPY, L()));
        r.H.Prim.ParentGroup.IsAttachment = true;
        Assert.Equal(TRANSFER_NO_ATTACHMENT, r.Api.llTransferOwnership(id, 0, L()));
        Assert.Equal(owner, r.H.Prim.OwnerID);
    }

    [Fact]
    public void TheEarlierOneArgumentFormStillWorks()
    {
        using var r = new InventoryGivesRig();
        var sp = Present(r);
        Assert.Equal(1, r.Api.llTransferOwnership(sp.UUID.ToString()));
        Assert.Equal(sp.UUID, r.H.Prim.OwnerID);
    }
}
