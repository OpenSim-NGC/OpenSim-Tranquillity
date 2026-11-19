/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The other direction of CrossEngineScriptControlTests: a script YEngine runs calls llResetOtherScript, llSetScriptState,
/// llGetScriptState and osResetAllScripts on a script Phlox runs in the same prim. SL has one script runtime, so these
/// functions name a script of the prim whatever compiled it (https://wiki.secondlife.com/wiki/LlResetOtherScript,
/// https://wiki.secondlife.com/wiki/LlSetScriptState, https://wiki.secondlife.com/wiki/LlGetScriptState). The call reaches
/// the engine that runs the target; a YEngine caller keeps YEngine's own rules (its llResetOtherScript resets a stopped
/// script too, and the script stays stopped). Each result is read from what the Phlox script then does: it says
/// "P entry" each time it starts and "P heard" for each message it hears.
/// In "phlox-yengine" as the other classes with YEngine (its compiler keeps state in statics).
/// </summary>
[Collection("phlox-yengine")]
public class YEngineScriptControlOfPhloxTests
{
    private readonly ITestOutputHelper _out;
    public YEngineScriptControlOfPhloxTests(ITestOutputHelper o) => _out = o;

    private const string Victim = "pvictim";

    private const string PScript = @"
        default {
            state_entry() { llSay(0, ""P entry""); llListen(8, """", NULL_KEY, """"); }
            listen(integer c, string n, key k, string m) { llSay(0, ""P heard "" + m); }
        }";

    /// <summary>The YEngine driver takes commands on channel 7 and says "D did &lt;command&gt;" after the call returns.</summary>
    private const string Driver = @"
        default {
            state_entry() { llSay(0, ""D entry""); llListen(7, """", NULL_KEY, """"); }
            listen(integer c, string n, key k, string m) {
                if (m == ""reset"") llResetOtherScript(""" + Victim + @""");
                else if (m == ""stop"") llSetScriptState(""" + Victim + @""", FALSE);
                else if (m == ""start"") llSetScriptState(""" + Victim + @""", TRUE);
                else if (m == ""state"") llSay(0, ""D state "" + (string)llGetScriptState(""" + Victim + @"""));
                else if (m == ""resetall"") osResetAllScripts(FALSE);
                llSay(0, ""D did "" + m);
            }
        }";

    private sealed class Rig : IDisposable
    {
        public readonly DataserverToPrimTests.Rig R;
        public UUID VictimItem;

        public Rig()
        {
            R = new DataserverToPrimTests.Rig(cfg =>
            {
                var ossl = cfg.AddConfig("OSSL");
                ossl.Set("AllowOSFunctions", "true");
                ossl.Set("OSFunctionThreatLevel", "Severe");
            }, withYEngine: true);
            VictimItem = R.Rez(R.H.Prim, Victim, PScript);
            SchedulerHarnessYEngine.Rez(R.H, R.H.Prim, Driver, "ydriver");
            Assert.True(R.PumpUntil(() => R.Count("D entry") == 1 && R.Count("P entry") == 1 && R.H.Engine.ListenManager.ListenCount >= 1),
                "not all started: " + string.Join(" | ", R.H.Said));
        }

        /// <summary>Sends a command to the driver and waits until it has made the call.</summary>
        public void Drive(string command)
        {
            int before = R.Count("D did " + command);
            SayToDriver(command);
            Assert.True(R.PumpUntil(() => R.Count("D did " + command) == before + 1), "the driver did not run " + command + ": " + string.Join(" | ", R.H.Said));
        }

        /// <summary>YEngine's listens hear the region's broadcast chat (as CrossEngineScriptControlTests' YEngine script does).</summary>
        public void SayToDriver(string message)
            => R.H.Scene.SimChatBroadcast(message, ChatTypeEnum.Region, 7, R.H.Prim.AbsolutePosition, "tester", UUID.Zero, false);

        public void SayToP(string message)
            => R.H.Scene.SimChat(message, ChatTypeEnum.Region, 8, R.H.Prim.AbsolutePosition, "tester", UUID.Random(), false);

        public bool PHears(string message)
        {
            int before = R.Count("P heard " + message);
            SayToP(message);
            return R.PumpUntil(() => R.Count("P heard " + message) > before, 3);
        }

        public void Dispose() => R.Dispose();
    }

    [Fact]
    public void LlResetOtherScriptFromYEngineRestartsAScriptPhloxRuns()
    {
        using var r = new Rig();
        r.Drive("reset");
        Assert.True(r.R.PumpUntil(() => r.R.Count("P entry") == 2), "the Phlox script was not reset: " + string.Join(" | ", r.R.H.Said));
        r.R.Quiet(500, "P entry");
        _out.WriteLine(string.Join(" | ", r.R.H.Said));
        Assert.Equal(2, r.R.Count("P entry"));
    }

    /// <summary>
    /// YEngine's own rule for its llResetOtherScript: a stopped script is reset too and stays stopped, and its state_entry
    /// runs once it is started (XMREngine.ResetScript, XMRInstRun.ResetLocked). A Phlox target is treated the same way.
    /// </summary>
    [Fact]
    public void LlResetOtherScriptFromYEngineOfAStoppedPhloxScriptResetsItAndLeavesItStopped()
    {
        using var r = new Rig();
        r.Drive("stop");
        Assert.True(r.R.PumpUntil(() => !r.R.H.Engine.GetScriptState(r.VictimItem)));
        r.Drive("reset");
        r.R.Quiet(700, "P entry");
        Assert.False(r.R.H.Engine.GetScriptState(r.VictimItem));

        r.Drive("start");
        Assert.True(r.R.PumpUntil(() => r.R.Count("P entry") == 2), "the reset Phlox script did not run state_entry when started: " + string.Join(" | ", r.R.H.Said));
        r.R.Quiet(500, "P entry");
        _out.WriteLine(string.Join(" | ", r.R.H.Said));
        Assert.Equal(2, r.R.Count("P entry"));
    }

    [Fact]
    public void LlSetScriptStateFromYEngineStopsAndStartsAScriptPhloxRuns()
    {
        using var r = new Rig();
        Assert.True(r.PHears("one"), "the Phlox script does not hear before it is stopped");

        r.Drive("stop");
        Assert.True(r.R.PumpUntil(() => !r.R.H.Engine.GetScriptState(r.VictimItem)), "the Phlox script was not stopped");
        Assert.False(r.R.H.Prim.Inventory.GetInventoryItem(r.VictimItem).ScriptRunning, "the item's Running flag is still set");
        int heard = r.R.CountStart("P heard ");
        r.SayToP("two");
        r.R.Quiet(700, "P heard ");
        Assert.Equal(heard, r.R.CountStart("P heard "));

        r.Drive("start");
        // The start is applied on Phlox's own thread, which then takes up the script's listen again
        Assert.True(r.R.PumpUntil(() => r.R.Count("P heard two") == 1 && r.R.H.Engine.ListenManager.ListenCount >= 1), "the Phlox script was not started: " + string.Join(" | ", r.R.H.Said));
        Assert.True(r.PHears("three"), "the Phlox script does not hear after it was started: " + string.Join(" | ", r.R.H.Said));
        Assert.True(r.R.H.Prim.Inventory.GetInventoryItem(r.VictimItem).ScriptRunning);
    }

    [Fact]
    public void LlGetScriptStateFromYEngineAnswersForAScriptPhloxRuns()
    {
        using var r = new Rig();
        r.Drive("state");
        Assert.Equal(1, r.R.Count("D state 1"));

        r.R.H.Scene.EventManager.TriggerStopScript(r.R.H.Prim.LocalId, r.VictimItem);   // the viewer's Running box unticked
        Assert.True(r.R.PumpUntil(() => !r.R.H.Engine.GetScriptState(r.VictimItem)));
        r.Drive("state");
        Assert.Equal(1, r.R.Count("D state 0"));
        Assert.Equal(1, r.R.Count("D state 1"));
    }

    [Fact]
    public void OsResetAllScriptsFromYEngineResetsAScriptPhloxRunsInThePrim()
    {
        using var r = new Rig();
        r.SayToDriver("resetall");   // the driver resets itself last, so it never says it is done
        Assert.True(r.R.PumpUntil(() => r.R.Count("P entry") == 2), "the Phlox script was not reset: " + string.Join(" | ", r.R.H.Said));
        r.R.Quiet(500, "P entry");
        Assert.Equal(2, r.R.Count("P entry"));
    }
}
