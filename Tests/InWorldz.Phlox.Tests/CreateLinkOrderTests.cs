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
using Phlox.ScriptEngine;
using InWorldz.Phlox.Glue;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Where llCreateLink and osForceCreateLink put the prims they link. The SL wiki
/// (https://wiki.secondlife.com/wiki/LlCreateLink): the linked object's prims "are inserted into the parent object's
/// link set starting at link number 2", and the parent's own children follow them. Its example links a three-prim
/// object B into a three-prim object A and gets A's root, then B's three prims in their order, then A's other two
/// (TheWikisExample below). With parent FALSE the script's object is the one inserted into the target.
/// YEngine does the same (LSL_Api.CreateLink calls LinkToGroup with insert set).
///
/// <para>The calls run inside a SyscallContext, so llCreateLink's 1 s sleep is added to the call's delay instead of
/// being waited out. No process-wide state is touched, so the class runs in parallel.</para>
/// </summary>
public class CreateLinkOrderTests
{
    private const int CHANGE_LINKS = 0x80;
    private const string Idle = "default { state_entry() { } }";

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly UUID Item;
        public UUID Owner => H.Prim.OwnerID;

        public Rig(bool ossl = false)
        {
            H = ossl
                ? new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"))
                : new SchedulerHarness();
            H.Prim.Name = "A1";
            Item = H.RezScript(Idle);
            WaitLoaded();
            // PERMISSION_CHANGE_LINKS, granted by the owner
            lock (H.Prim.TaskInventory)
            {
                TaskInventoryItem self = H.Prim.TaskInventory[Item];
                self.PermsMask |= CHANGE_LINKS;
                self.PermsGranter = Owner;
            }
        }

        private void WaitLoaded()
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (H.RunStateOf(Item) != "Waiting")
            {
                Assert.True(DateTime.UtcNow < until, "the script never loaded: " + H.RunStateOf(Item));
                if (!H.PumpOnceBusy()) System.Threading.Thread.Sleep(1);
            }
        }

        public SceneObjectGroup Host => H.Prim.ParentGroup;

        /// <summary>A new object of the script's owner with the named prims; the first is its root.</summary>
        public SceneObjectGroup Object(params string[] names)
        {
            SceneObjectGroup root = SceneHelpers.AddSceneObject(H.Scene, names[0], Owner);
            AddChildren(root, names.Skip(1).ToArray());
            return root;
        }

        /// <summary>Gives <paramref name="root"/> more prims, numbered after the ones it has.</summary>
        public void AddChildren(SceneObjectGroup root, params string[] names)
        {
            foreach (string name in names)
                root.LinkToGroup(SceneHelpers.AddSceneObject(H.Scene, name, Owner));
        }

        public LSLSystemAPI Api
        {
            get
            {
                var exe = (PhloxExecutionScheduler)Field(H.Engine, "m_ExeScheduler");
                return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[Item];
            }
        }

        public void Call(Action<LSLSystemAPI> call)
        {
            var ctx = new SyscallContext(Item, SyscallContext.NextSeq());
            ctx.Enter();
            try { call(Api); }
            finally { SyscallContext.Exit(); }
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

    /// <summary>The prims' names in link order, link 1 first.</summary>
    private static string Order(SceneObjectGroup group)
        => string.Join(" ", Enumerable.Range(1, group.PrimCount).Select(n => group.GetLinkNumPart(n)?.Name ?? "?"));

    [Fact]
    public void EachLinkedPrimBecomesLinkTwo()
    {
        using var r = new Rig();
        SceneObjectGroup p2 = r.Object("P2"), p3 = r.Object("P3"), p4 = r.Object("P4");

        r.Call(a => a.llCreateLink(p2.UUID.ToString(), 1));
        Assert.Equal("A1 P2", Order(r.Host));
        r.Call(a => a.llCreateLink(p3.UUID.ToString(), 1));
        Assert.Equal("A1 P3 P2", Order(r.Host));
        r.Call(a => a.llCreateLink(p4.UUID.ToString(), 1));

        Assert.Equal("A1 P4 P3 P2", Order(r.Host));
    }

    [Fact]
    public void TheWikisExampleGivesA1B1B2B3A2A3()
    {
        using var r = new Rig();
        r.AddChildren(r.Host, "A2", "A3");
        SceneObjectGroup b = r.Object("B1", "B2", "B3");
        Assert.Equal("A1 A2 A3", Order(r.Host));
        Assert.Equal("B1 B2 B3", Order(b));

        r.Call(a => a.llCreateLink(b.UUID.ToString(), 1));

        Assert.Equal("A1 B1 B2 B3 A2 A3", Order(r.Host));
        Assert.True(b.IsDeleted, "B is no longer an object of its own");
    }

    [Fact]
    public void WithParentFalseTheScriptsObjectIsInsertedIntoTheTarget()
    {
        using var r = new Rig();
        r.AddChildren(r.Host, "A2", "A3");
        SceneObjectGroup b = r.Object("B1", "B2", "B3");
        SceneObjectGroup host = r.Host;

        r.Call(a => a.llCreateLink(b.UUID.ToString(), 0));

        SceneObjectGroup linked = r.H.Prim.ParentGroup;
        Assert.Same(b, linked);
        Assert.True(host.IsDeleted, "the script's object is no longer an object of its own");
        Assert.Equal("B1 A1 A2 A3 B2 B3", Order(linked));
        Assert.Equal(2, r.H.Prim.LinkNum);
    }

    [Theory]
    [InlineData(1, "A1 B1 B2 B3 A2 A3")]
    [InlineData(0, "B1 A1 A2 A3 B2 B3")]
    public void ForceCreateLinkInsertsTheSameWay(int parent, string expected)
    {
        using var r = new Rig(ossl: true);
        r.AddChildren(r.Host, "A2", "A3");
        SceneObjectGroup b = r.Object("B1", "B2", "B3");

        r.Call(a => a.osForceCreateLink(b.UUID.ToString(), parent));

        Assert.Equal(expected, Order(r.H.Prim.ParentGroup));
    }

    [Fact]
    public void LinkingAPrimAlreadyInTheLinksetChangesNothing()
    {
        using var r = new Rig();
        r.AddChildren(r.Host, "A2", "A3");
        SceneObjectPart a3 = r.Host.GetLinkNumPart(3);

        r.Call(a => a.llCreateLink(a3.UUID.ToString(), 1));

        Assert.Equal("A1 A2 A3", Order(r.Host));
    }
}
