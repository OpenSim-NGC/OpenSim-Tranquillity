/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using Xunit;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Money: llGiveMoney transfers through the region's IMoneyModule and always returns 0, with no sleep (SL llGiveMoney:
/// "Returns an integer that is always zero", forced delay 0.0; YEngine LSL_Api.llGiveMoney). llTransferLindenDollars
/// answers with transaction_result(key, success, data) (SL; Halcyon LSLSystemAPI.cs:3083-3085), with Halcyon's checks
/// and error tags (:3018-3089); iwGiveMoney returns the transaction id or the tag, with no event (:3096-3100).
/// llSetPayPrice writes the root prim's prices, hides the buttons the list leaves out and marks the object changed
/// (Halcyon :13348-13362, YEngine LSL_Api.llSetPayPrice); from a child prim it does nothing (SL: "Calling it from a
/// child prim has no effect"; YEngine ignores it too).
/// </summary>
// No test reaches a network service: the money module is an in-memory recorder.
// Test grouping: no process-wide state, so the class runs in parallel.
public class MoneyTransferTests
{
    private const string LslErr = "Script error: LSL Runtime Error: ";
    private const string Handler =
        "default { transaction_result(key id, integer ok, string data) { llSay(0, \"tr \" + (string)id + \" \" + (string)ok + \" \" + data); } }";

    [Fact]
    public void GiveMoneyWithAMoneyModuleTransfersFromTheRootAndReturnsZeroWithoutASleep()
    {
        using var r = new InventoryGivesRig();
        var money = new RecordingMoney();
        r.H.Scene.RegisterModuleInterface<IMoneyModule>(money);
        r.GrantDebitByOwner();
        UUID to = UUID.Random();
        var (ms, ret) = r.Accounted(a => a.llGiveMoney(to.ToString(), 5));
        Assert.Equal(0, ret);
        Assert.Equal(0, ms);
        Assert.Empty(r.Errors);
        Assert.True(r.H.PumpUntil(() => !money.Gives.IsEmpty), "no transfer reached the money module");
        Assert.True(money.Gives.TryPeek(out var g));
        Assert.Equal((r.H.Prim.ParentGroup.RootPart.UUID, r.H.Prim.ParentGroup.RootPart.OwnerID, to, 5), (g.Object, g.From, g.To, g.Amount));
    }

    [Fact]
    public void GiveMoneyRefusalsDoNotSleep()
    {
        using var r = new InventoryGivesRig();
        r.GrantDebitByOwner();
        Assert.Equal(15, r.Accounted(a => a.llGiveMoney(UUID.Random().ToString(), 5)).Ms);   // not implemented: its 15 ms only
        Assert.Equal(15, r.Accounted(a => a.llGiveMoney("not a key", 5)).Ms);
        Assert.Equal(0, r.Accounted(a => a.llGiveMoney(UUID.Zero.ToString(), 5)).Ms);
    }

    [Fact]
    public void TransferLindenDollarsAnswersWithTransactionResultOnSuccess()
    {
        using var r = new InventoryGivesRig(Handler);
        var money = new RecordingMoney();
        r.H.Scene.RegisterModuleInterface<IMoneyModule>(money);
        r.GrantDebitByOwner();
        UUID to = UUID.Random();
        var (_, ret) = r.Accounted(a => a.llTransferLindenDollars(to.ToString(), 7));
        Assert.Equal("tr " + ret + " 1 " + to + ",7", r.WaitSaid("tr "));
        Assert.True(money.Gives.TryPeek(out var g));
        Assert.Equal(ret, g.Txn.ToString());
    }

    [Theory]
    [InlineData("nodebit", "MISSING_PERMISSION_DEBIT", "No permissions to give money")]
    [InlineData("badkey", "INVALID_DESTINATION", "Bad key in llGiveMoney")]
    [InlineData("nomodule", "SERVICE_ERROR", null)]
    [InlineData("amount", "INVALID_AMOUNT", "")]
    [InlineData("refused", "LINDENDOLLAR_INSUFFICIENTFUNDS", "")]
    public void TransferLindenDollarsFailuresCarryTheErrorTag(string how, string tag, string error)
    {
        using var r = new InventoryGivesRig(Handler);
        var money = new RecordingMoney { Succeed = how != "refused" };
        if (how != "nomodule") r.H.Scene.RegisterModuleInterface<IMoneyModule>(money);
        if (how != "nodebit") r.GrantDebitByOwner();
        string dest = how == "badkey" ? "not a key" : UUID.Random().ToString();
        var (_, ret) = r.Accounted(a => a.llTransferLindenDollars(dest, how == "amount" ? 0 : 5));
        var errors = r.Errors;
        Assert.Equal("tr " + ret + " 0 " + tag, r.WaitSaid("tr "));
        if (error == null) Assert.Equal(new[] { "Script error: Command not implemented: llGiveMoney" }, errors);
        else if (error == "") Assert.Empty(errors);
        else Assert.Equal(new[] { LslErr + error }, errors);
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("ds "));
    }

    [Fact]
    public void IwGiveMoneyReturnsTheTransactionOrTheTagAndPostsNoEvent()
    {
        using var r = new InventoryGivesRig(Handler);
        Assert.Equal("MISSING_PERMISSION_DEBIT", r.Accounted(a => a.iwGiveMoney(UUID.Random().ToString(), 5)).Ret);
        r.GrantDebitByOwner();
        Assert.Equal("SERVICE_ERROR", r.Accounted(a => a.iwGiveMoney(UUID.Random().ToString(), 5)).Ret);
        var money = new RecordingMoney();
        r.H.Scene.RegisterModuleInterface<IMoneyModule>(money);
        Assert.Equal("INVALID_DESTINATION", r.Accounted(a => a.iwGiveMoney("nope", 5)).Ret);
        var (_, ret) = r.Accounted(a => a.iwGiveMoney(UUID.Random().ToString(), 5));
        Assert.True(money.Gives.TryPeek(out var g));
        Assert.Equal(g.Txn.ToString(), ret);
        money.Succeed = false;
        Assert.Equal("LINDENDOLLAR_INSUFFICIENTFUNDS", r.Accounted(a => a.iwGiveMoney(UUID.Random().ToString(), 5)).Ret);
        r.H.PumpFor(TimeSpan.FromMilliseconds(300));   // a "did not happen" window: no transaction_result arrives
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("tr "));
    }

    [Fact]
    public void SetPayPriceFromTheRootSetsItsPricesAndHidesTheMissingButtons()
    {
        using var r = new InventoryGivesRig();
        r.H.Prim.ParentGroup.HasGroupChanged = false;
        r.Accounted(a => { a.llSetPayPrice(10, L(1, 2)); return null; });
        Assert.Equal(new[] { 10, 1, 2, -1, -1 }, r.H.Prim.ParentGroup.RootPart.PayPrice);
        Assert.True(r.H.Prim.ParentGroup.HasGroupChanged);
    }

    [Fact]
    public void SetPayPriceFromAChildPrimHasNoEffect()
    {
        using var r = new InventoryGivesRig();
        var child = r.AddChild("child");
        UUID childScript = r.H.RezScriptInto(child, "default { state_entry() { } }");
        r.WaitLoaded(childScript);
        int[] rootBefore = (int[])r.H.Prim.ParentGroup.RootPart.PayPrice.Clone();
        int[] childBefore = (int[])child.PayPrice.Clone();
        r.H.Prim.ParentGroup.HasGroupChanged = false;
        r.AccountedFor(childScript, a => { a.llSetPayPrice(10, L(1, 2)); return null; });
        Assert.Equal(rootBefore, r.H.Prim.ParentGroup.RootPart.PayPrice);
        Assert.Equal(childBefore, child.PayPrice);
        Assert.False(r.H.Prim.ParentGroup.HasGroupChanged);
    }
}
