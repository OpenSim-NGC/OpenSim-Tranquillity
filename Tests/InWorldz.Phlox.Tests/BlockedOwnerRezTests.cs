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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Objects.BlockedOwners;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script whose owner the region has blocked from rezzing (the core's IBlockedOwnerModule, through the real
/// BlockedOwnerModule) rezzes nothing. Halcyon's iwRezAt (InWorldz.Phlox.Engine/LSLSystemAPI.cs:3160-3166) asked the
/// region's bad-user list first and failed silently with a 100 ms sleep; every Phlox rez function shares that path.
/// iwCheckRezError answers IW_REZ_NOT_PERMITTED for such an owner before any other check, as Halcyon's
/// Scene.CheckRezError (Scene.cs:2343-2344). SL documents no such list.
/// No process-wide state, so the class runs in parallel.
/// </summary>
public class BlockedOwnerRezTests
{
    private readonly ITestOutputHelper _out;
    public BlockedOwnerRezTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const string Idle = "default { state_entry() { } }";

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly BlockedOwnerModule Blocked = new();
        public readonly UUID Item;

        public Rig()
        {
            H = new SchedulerHarness();
            SceneHelpers.SetupSceneModules(H.Scene, new IniConfigSource(), Blocked);
            TaskInventoryHelpers.AddSceneObject(H.Scene.AssetService, H.Prim, "child", UUID.Random(), H.Prim.OwnerID);
            Item = H.RezScript(Idle);
            Assert.True(H.PumpUntil(() => H.RunStateOf(Item) == "Waiting"), "the script never loaded: " + H.RunStateOf(Item));
        }

        public LSLSystemAPI Api
        {
            get
            {
                var exe = (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
                return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[Item];
            }
        }

        public int Others => H.Scene.GetSceneObjectGroups().Count(g => g.UUID != H.Prim.ParentGroup.UUID);

        public List<string> Errors => H.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();

        /// <summary>Runs one call as the script's syscall and returns the delay it charged and its result.</summary>
        public (int Ms, object Ret) Accounted(Func<LSLSystemAPI, object> call)
        {
            H.ClearSaid(Item);
            var ctx = new SyscallContext(Item, SyscallContext.NextSeq());
            ctx.Enter();
            object ret;
            try { ret = call(Api); }
            finally { SyscallContext.Exit(); }
            return (ctx.DelayMs, ret);
        }

        public void Dispose() => H.Dispose();
    }

    private static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    private static Vector3 Above(Rig r) => r.H.Prim.AbsolutePosition + new Vector3(0, 0, 1);

    [Fact]
    public void ABlockedOwnersRezIsRefusedSilentlyWithHalcyonsPause()
    {
        using var r = new Rig();
        Assert.True(r.Blocked.Block(r.H.Prim.OwnerID));

        var (ms1, _) = r.Accounted(a => { a.llRezObject("child", Above(r), Vector3.Zero, Quaternion.Identity, 0); return null; });
        _out.WriteLine("llRezObject: " + ms1 + " ms, errors [" + string.Join(" | ", r.Errors) + "]");
        Assert.Equal(100, ms1);
        Assert.Empty(r.Errors);

        var (ms2, _) = r.Accounted(a => { a.llRezAtRoot("child", Above(r), Vector3.Zero, Quaternion.Identity, 0); return null; });
        Assert.Equal(100, ms2);
        Assert.Empty(r.Errors);

        var (ms3, key) = r.Accounted(a => a.iwRezAt("child", 0, Above(r), Vector3.Zero, Quaternion.Identity, 0));
        Assert.Equal(100, ms3);
        Assert.Equal(UUID.Zero.ToString(), key);
        Assert.Empty(r.Errors);

        Assert.Equal(0, r.Others);
    }

    [Fact]
    public void TheBlockIsAskedBeforeTheRezChecksAsHalcyonAskedIt()
    {
        // Halcyon's bad-user check came before the NaN rotation and the 10 m checks, so neither shouts for a blocked owner.
        using var r = new Rig();
        r.Blocked.Block(r.H.Prim.OwnerID);

        var (ms1, _) = r.Accounted(a => { a.llRezObject("child", Above(r), Vector3.Zero, new Quaternion(float.NaN, 0, 0, 1), 0); return null; });
        Assert.Equal(100, ms1);
        Assert.Empty(r.Errors);

        var (ms2, _) = r.Accounted(a => { a.llRezObject("child", r.H.Prim.AbsolutePosition + new Vector3(0, 0, 50), Vector3.Zero, Quaternion.Identity, 0); return null; });
        Assert.Equal(100, ms2);
        Assert.Empty(r.Errors);

        var (ms3, _) = r.Accounted(a => { a.llRezObject("missing", Above(r), Vector3.Zero, Quaternion.Identity, 0); return null; });
        Assert.Equal(100, ms3);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void ABlockedOwnersScriptGetsNoObjectRezAndAnEmptyKeyFromRezObjectWithParams()
    {
        using var h = new SchedulerHarness();
        var blocked = new BlockedOwnerModule();
        SceneHelpers.SetupSceneModules(h.Scene, new IniConfigSource(), blocked);
        TaskInventoryHelpers.AddSceneObject(h.Scene.AssetService, h.Prim, "child", UUID.Random(), h.Prim.OwnerID);
        blocked.Block(h.Prim.OwnerID);

        h.RezScript("default { state_entry() {\n" +
                    "  llRezObject(\"child\", llGetPos() + <0,0,1>, ZERO_VECTOR, ZERO_ROTATION, 0);\n" +
                    "  key k = llRezObjectWithParams(\"child\", [REZ_POS, llGetPos() + <0,0,1>, FALSE, FALSE]);\n" +
                    "  llSay(0, \"k=[\" + (string)k + \"]\");\n" +
                    "  llSay(0, \"check \" + (string)iwCheckRezError(llGetPos(), FALSE, 1));\n" +
                    "  llSay(0, \"done\");\n" +
                    "}\n" +
                    "object_rez(key id) { llSay(0, \"rez=\" + (string)id); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("done")), "the script never finished: " + string.Join(" | ", h.Said));
        _out.WriteLine(string.Join(" | ", h.SaidOn.Select(m => m.Channel + ":" + m.Message)));

        Assert.Contains("k=[]", h.Said);
        Assert.Contains("check 1", h.Said);
        Assert.DoesNotContain(h.Said, s => s.StartsWith("rez="));
        Assert.DoesNotContain(h.SaidOn, m => m.Channel == DEBUG_CHANNEL);
        Assert.Equal(0, h.Scene.GetSceneObjectGroups().Count(g => g.UUID != h.Prim.ParentGroup.UUID));
    }

    [Fact]
    public void CheckRezErrorNamesTheBlockBeforeAMissingParcel()
    {
        // Halcyon's Scene.CheckRezError asked IsBadUser before the parcel: a blocked owner is NOT_PERMITTED (1) everywhere.
        using var r = new Rig();
        r.Blocked.Block(r.H.Prim.OwnerID);
        r.H.Scene.LandChannel = DispatchProxy.Create<ILandChannel, CheckRezErrorTests.LandWithAHole>();
        ((CheckRezErrorTests.LandWithAHole)(object)r.H.Scene.LandChannel).Inner = new StripLand(r.H.Scene, (256, r.H.Prim.OwnerID));

        Assert.Equal(1, r.Api.iwCheckRezError(new Vector3(250, 128, 25), 0, 1));
        Assert.Equal(1, r.Api.iwCheckRezError(new Vector3(128, 128, 25), 0, 1));

        r.Blocked.Unblock(r.H.Prim.OwnerID);
        Assert.Equal(3, r.Api.iwCheckRezError(new Vector3(250, 128, 25), 0, 1));
        Assert.Equal(0, r.Api.iwCheckRezError(new Vector3(128, 128, 25), 0, 1));
    }

    [Fact]
    public void AnOwnerNobodyBlockedRezzesAsBeforeAndUnblockingGivesTheRezBack()
    {
        using var r = new Rig();
        var (ms1, _) = r.Accounted(a => { a.llRezObject("child", Above(r), Vector3.Zero, Quaternion.Identity, 0); return null; });
        Assert.Equal(100, ms1);
        Assert.Empty(r.Errors);
        Assert.Equal(1, r.Others);

        // Someone else on the list changes nothing for this owner.
        r.Blocked.Block(UUID.Random());
        r.Accounted(a => { a.llRezObject("child", Above(r), Vector3.Zero, Quaternion.Identity, 0); return null; });
        Assert.Equal(2, r.Others);

        r.Blocked.Block(r.H.Prim.OwnerID);
        r.Accounted(a => { a.llRezObject("child", Above(r), Vector3.Zero, Quaternion.Identity, 0); return null; });
        Assert.Equal(2, r.Others);

        r.Blocked.Unblock(r.H.Prim.OwnerID);
        var (ms4, key) = r.Accounted(a => a.iwRezAt("child", 0, Above(r), Vector3.Zero, Quaternion.Identity, 0));
        Assert.Equal(100, ms4);
        Assert.NotEqual(UUID.Zero.ToString(), key);
        Assert.Equal(3, r.Others);
    }
}
