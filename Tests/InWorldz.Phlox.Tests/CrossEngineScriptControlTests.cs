/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The script functions that act on another script of the same prim by name work on a script whatever engine runs it: SL has
/// one script runtime, so llResetOtherScript (https://wiki.secondlife.com/wiki/LlResetOtherScript), llSetScriptState
/// (https://wiki.secondlife.com/wiki/LlSetScriptState) and llGetScriptState (https://wiki.secondlife.com/wiki/LlGetScriptState)
/// name a script of the prim and do not depend on how that script was compiled. osResetAllScripts resets every other script of
/// the prim or linkset. A Phlox script drives them here against a script that YEngine runs in the same prim, and each result
/// is read from what the YEngine script then does (it says "Y entry" each time it starts and "Y heard" for each message it
/// hears). In "phlox-yengine" as the other classes with YEngine (its compiler keeps state in statics).
/// </summary>
[Collection("phlox-yengine")]
public class CrossEngineScriptControlTests
{
    private readonly ITestOutputHelper _out;
    public CrossEngineScriptControlTests(ITestOutputHelper o) => _out = o;

    private const string Victim = "yvictim";

    private const string YScript = @"
        default {
            state_entry() { llSay(0, ""Y entry""); llListen(8, """", NULL_KEY, """"); }
            listen(integer c, string n, key k, string m) { llSay(0, ""Y heard "" + m); }
        }";

    /// <summary>The driver takes commands on channel 7 and says "D did &lt;command&gt;" after the call returns.</summary>
    private const string Driver = @"
        default {
            state_entry() { llSay(0, ""D entry""); llListen(7, """", NULL_KEY, """"); }
            listen(integer c, string n, key k, string m) {
                if (m == ""reset"") llResetOtherScript(""" + Victim + @""");
                else if (m == ""stop"") llSetScriptState(""" + Victim + @""", FALSE);
                else if (m == ""start"") llSetScriptState(""" + Victim + @""", TRUE);
                else if (m == ""state"") llSay(0, ""D state "" + (string)llGetScriptState(""" + Victim + @"""));
                else if (m == ""resetall"") osResetAllScripts(FALSE);
                else if (m == ""remove"") llRemoveInventory(""" + Victim + @""");
                llSay(0, ""D did "" + m);
            }
        }";

    private sealed class Rig : IDisposable
    {
        public readonly DataserverToPrimTests.Rig R;
        public UUID VictimItem;

        public Rig()
        {
            R = new DataserverToPrimTests.Rig(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"), withYEngine: true);
            R.Rez(R.H.Prim, "d driver", Driver);
            VictimItem = SchedulerHarnessYEngine.Rez(R.H, R.H.Prim, YScript, Victim);
            Assert.True(R.PumpUntil(() => R.Count("D entry") == 1 && R.Count("Y entry") == 1 && R.H.Engine.ListenManager.ListenCount >= 1),
                "not all started: " + string.Join(" | ", R.H.Said));
        }

        /// <summary>Sends a command to the driver and waits until it has made the call.</summary>
        public void Drive(string command)
        {
            int before = R.Count("D did " + command);
            R.Say(command);
            Assert.True(R.PumpUntil(() => R.Count("D did " + command) == before + 1), "the driver did not run " + command + ": " + string.Join(" | ", R.H.Said));
        }

        /// <summary>Says a message on the channel the YEngine script listens on.</summary>
        public void SayToY(string message)
            => R.H.Scene.SimChatBroadcast(message, ChatTypeEnum.Region, 8, R.H.Prim.AbsolutePosition, "tester", UUID.Zero, false);

        public bool YHears(string message)
        {
            int before = R.Count("Y heard " + message);
            SayToY(message);
            return R.PumpUntil(() => R.Count("Y heard " + message) > before, 3);
        }

        public void Dispose() => R.Dispose();
    }

    [Fact]
    public void LlResetOtherScriptRestartsAScriptYEngineRuns()
    {
        using var r = new Rig();
        r.Drive("reset");
        Assert.True(r.R.PumpUntil(() => r.R.Count("Y entry") == 2), "the YEngine script was not reset: " + string.Join(" | ", r.R.H.Said));
        r.R.Quiet(500, "Y entry");
        Assert.Equal(2, r.R.Count("Y entry"));
        _out.WriteLine(string.Join(" | ", r.R.H.Said));
    }

    [Fact]
    public void LlSetScriptStateStopsAndStartsAScriptYEngineRuns()
    {
        using var r = new Rig();
        Assert.True(r.YHears("one"), "the YEngine script does not hear before it is stopped");

        r.Drive("stop");
        int heard = r.R.CountStart("Y heard ");
        r.SayToY("two");
        r.R.Quiet(700, "Y heard ");
        Assert.Equal(heard, r.R.CountStart("Y heard "));

        r.Drive("start");
        Assert.True(r.YHears("three"), "the YEngine script does not hear after it was started: " + string.Join(" | ", r.R.H.Said));
    }

    [Fact]
    public void LlGetScriptStateAnswersForAScriptYEngineRuns()
    {
        using var r = new Rig();
        r.Drive("state");
        Assert.Equal(1, r.R.Count("D state 1"));

        r.Drive("stop");
        r.Drive("state");
        Assert.Equal(1, r.R.Count("D state 0"));
        Assert.Equal(1, r.R.Count("D state 1"));
    }

    [Fact]
    public void LlGetScriptStateSeesAStopYEngineMadeWithoutTheItemsRunningFlag()
    {
        // A script can be stopped without its item's flag changing (a fault stops it for the owner to see). The engine that runs
        // the script knows; the flag does not.
        using var r = new Rig();
        r.R.H.Scene.EventManager.TriggerStopScript(r.R.H.Prim.LocalId, r.VictimItem);
        Assert.True(r.R.H.YEngine.HasScript(r.VictimItem, out bool running));
        Assert.False(running);

        r.Drive("state");
        Assert.Equal(1, r.R.Count("D state 0"));
    }

    [Fact]
    public void OsResetAllScriptsRestartsAScriptYEngineRunsInThePrim()
    {
        using var r = new Rig();
        r.R.Say("resetall");   // the driver resets itself last, so it never says it is done
        Assert.True(r.R.PumpUntil(() => r.R.Count("Y entry") == 2), "the YEngine script was not reset: " + string.Join(" | ", r.R.H.Said));
        r.R.Quiet(500, "Y entry");
        Assert.Equal(2, r.R.Count("Y entry"));
    }

    [Fact]
    public void LlRemoveInventoryOfARunningYEngineScriptStopsIt()
    {
        using var r = new Rig();
        Assert.True(r.YHears("before"));

        r.Drive("remove");
        Assert.False(r.R.H.Prim.Inventory.GetInventoryItems(InventoryType.LSL).Any(i => i.ItemID == r.VictimItem), "the item is still in the prim");
        int heard = r.R.CountStart("Y heard ");
        r.SayToY("after");
        r.R.Quiet(700, "Y heard ");
        Assert.Equal(heard, r.R.CountStart("Y heard "));
    }
}
