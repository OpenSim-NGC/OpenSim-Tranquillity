/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// One script in a harness prim, and its API called directly from the test inside a SyscallContext, where every
/// ScriptSleep and SysReturn delay of the call adds up (<see cref="Accounted"/>). Touches no process-wide state
/// (SyscallContext is per thread), so the classes that use it run in parallel.
/// </summary>
internal sealed class InventoryGivesRig : IDisposable
{
    public const int DEBUG_CHANNEL = 0x7FFFFFFF;

    public readonly SchedulerHarness H;
    public readonly UUID Item;

    public InventoryGivesRig(string source = "default { state_entry() { } }", Action<Nini.Config.IConfigSource> configure = null)
    {
        H = new SchedulerHarness(configure);
        Item = H.RezScript(source);
        WaitLoaded(Item);
    }

    public void WaitLoaded(UUID item)
    {
        Assert.True(H.PumpUntil(() => H.RunStateOf(item) == "Waiting", TimeSpan.FromSeconds(30)),
            "the script never loaded: " + H.RunStateOf(item));
    }

    public LSLSystemAPI Api => ApiOf(Item);

    public LSLSystemAPI ApiOf(UUID item)
    {
        var exe = (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
        return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[item];
    }

    public TaskInventoryItem Self { get { lock (H.Prim.TaskInventory) return H.Prim.TaskInventory[Item]; } }

    public void GrantDebitByOwner()
    {
        Self.PermsGranter = Self.OwnerID;
        Self.PermsMask = 0x2;   // PERMISSION_DEBIT
    }

    public List<string> Errors => H.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();

    /// <summary>The call's summed delay, and its return (a SysReturn result when the call made one).</summary>
    public (int Ms, object Ret) Accounted(Func<LSLSystemAPI, object> call) => AccountedFor(Item, call);

    public (int Ms, object Ret) AccountedFor(UUID item, Func<LSLSystemAPI, object> call)
    {
        LSLSystemAPI api = ApiOf(item);
        H.ClearSaid(item);
        var ctx = new SyscallContext(item, SyscallContext.NextSeq());
        ctx.Enter();
        object ret;
        try { ret = call(api); }
        finally { SyscallContext.Exit(); }
        return (ctx.DelayMs, ctx.HasResult ? ctx.Result : ret);
    }

    /// <summary>Pump until a line starting with <paramref name="prefix"/> is said; that line.</summary>
    public string WaitSaid(string prefix)
    {
        Assert.True(H.PumpUntil(() => H.Said.Any(s => s.StartsWith(prefix)), TimeSpan.FromSeconds(15)),
            "nothing starting '" + prefix + "' was said; said: " + string.Join(" | ", H.Said));
        return H.Said.First(s => s.StartsWith(prefix));
    }

    /// <summary>Another prim linked to the harness object (link numbers 2, 3, ... in the order added).</summary>
    public SceneObjectPart AddChild(string name)
    {
        var sog = OpenSim.Tests.Common.SceneHelpers.AddSceneObject(H.Scene, name, H.Prim.OwnerID);
        H.Prim.ParentGroup.LinkToGroup(sog);
        return H.Prim.ParentGroup.Parts.First(p => p.Name == name);
    }

    public static TaskInventoryItem AddNotecard(SchedulerHarness h, SceneObjectPart part, string name, string text = "a gift")
        => OpenSim.Tests.Common.TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, part, name, UUID.Random(), UUID.Random(), text);

    public static LSLList L(params object[] items) => new LSLList(new List<object>(items));

    private static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    public void Dispose() => H.Dispose();
}

/// <summary>An in-memory money module: records each transfer and answers with <see cref="Succeed"/>.</summary>
internal sealed class RecordingMoney : IMoneyModule
{
    public readonly ConcurrentQueue<(UUID Object, UUID From, UUID To, int Amount, UUID Txn)> Gives = new();
    public bool Succeed = true;

    public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txn, out string reason)
    {
        Gives.Enqueue((objectID, fromID, toID, amount, txn));
        reason = Succeed ? string.Empty : "LINDENDOLLAR_INSUFFICIENTFUNDS";
        return Succeed;
    }

    public int GetBalance(UUID agentID) => 0;
    public bool UploadCovered(UUID agentID, int amount) => true;
    public bool AmountCovered(UUID agentID, int amount) => true;
    public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData = "") { }
    public void ApplyUploadCharge(UUID agentID, int amount, string text) { }
    public void MoveMoney(UUID fromUser, UUID toUser, int amount, string text) { }
    public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text) => true;
    public int UploadCharge => 0;
    public int GroupCreationCharge => 0;
#pragma warning disable CS0067
    public event ObjectPaid OnObjectPaid;
#pragma warning restore CS0067
}
