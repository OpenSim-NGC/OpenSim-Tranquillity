/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// One idle Phlox script in the harness prim, with its LSLSystemAPI reachable from the test thread, as ErrorPauseTests
/// reaches it. <see cref="Accounted"/> runs a call inside a SyscallContext, where every ScriptSleep of the call and the
/// delay of its SysReturn add to one total, so a delay is read without waiting for it and without the engine Clock.
/// No process-wide state: classes using it run in parallel.
/// </summary>
internal sealed class ApiCallRig : IDisposable
{
    public const int DEBUG_CHANNEL = 0x7FFFFFFF;
    public readonly SchedulerHarness H;
    public readonly UUID Item;

    public ApiCallRig(Action<IConfigSource> configure = null, string source = "default { state_entry() { } }")
    {
        H = new SchedulerHarness(configure);
        Item = H.RezScript(source);
        Assert.True(H.PumpUntil(() => H.RunStateOf(Item) == "Waiting", TimeSpan.FromSeconds(30)),
            "the script never loaded: " + H.RunStateOf(Item));
    }

    public LSLSystemAPI Api
    {
        get
        {
            var exe = (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
            return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[Item];
        }
    }

    public RuntimeState State => ((Interpreter)H.InterpreterFor(Item)).ScriptState;

    public TaskInventoryItem Self { get { lock (H.Prim.TaskInventory) return H.Prim.TaskInventory[Item]; } }

    /// <summary>Grant the script's permissions as an answered llRequestPermissions leaves them.</summary>
    public void Grant(UUID granter, int mask)
    {
        TaskInventoryItem item = Self;
        item.PermsGranter = granter;
        item.PermsMask = mask;
    }

    public ScenePresence AddAvatar(UUID id = default)
        => SceneHelpers.AddScenePresence(H.Scene, id == default ? UUID.Random() : id);

    /// <summary>Messages on DEBUG_CHANNEL so far.</summary>
    public List<string> Errors => H.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();

    /// <summary>Every ScriptSleep of the call and its SysReturn delay, added up, with the value it handed back.</summary>
    public (int Ms, object Ret) Accounted(Func<LSLSystemAPI, object> call)
    {
        var ctx = new SyscallContext(Item, SyscallContext.NextSeq());
        ctx.Enter();
        object ret;
        try { ret = call(Api); }
        finally { SyscallContext.Exit(); }
        return (ctx.DelayMs, ctx.HasResult ? ctx.Result : ret);
    }

    public static LSLList L(params object[] items) => new LSLList(new List<object>(items));

    public static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    /// <summary>Set a property whose setter is private (the scene keeps several presence states that way).</summary>
    public static void SetPrivate(object o, string property, object value)
        => o.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(o, value);

    public void Dispose() => H.Dispose();
}
