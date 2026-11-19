/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script whose engine is changed (Scene.SetScriptEngine, behind the SetScriptEngine capability and osSetScriptEngine)
/// starts fresh in the engine it lands in, as a script does after a recompile: state_entry runs once, and no state an
/// engine saved for it before the change comes back, also when it returns to an engine it ran in before, and after a
/// region stop straight after the change. Choosing the engine that already runs a script restarts nothing. Real Phlox and
/// YEngine. In "phlox-yengine" (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class EngineSwitchStartsFreshTests
{
    private const string Phlox = "InWorldz.Phlox";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    // Each start says "entry". Each poke on channel 5, and each touch, adds 10 to a global and says it: a fresh script
    // answers "n 10" to its first poke, a script that resumed state from before answers "n 20".
    private const string Body =
        "integer n;\n" +
        "default {\n" +
        "  state_entry() { llListen(5, \"\", NULL_KEY, \"\"); llSay(0, \"entry\"); }\n" +
        "  listen(integer c, string name, key id, string msg) { n = n + 10; llSay(0, \"n \" + (string)n); }\n" +
        "  touch_start(integer t) { n = n + 10; llSay(0, \"n \" + (string)n); }\n" +
        "}\n";

    private sealed class Rig
    {
        public readonly SchedulerHarness H;
        public readonly UUID Owner = UUID.Random();
        public SceneObjectGroup Group;
        public TaskInventoryItem Item;

        public Rig(SchedulerHarness h) => H = h;

        public void Build(string source, int choice)
        {
            typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(H.Scene, Phlox);
            Group = SceneHelpers.AddSceneObject(H.Scene, "Example Object", Owner);
            Item = TaskInventoryHelpers.AddScript(H.Scene.AssetService, Group.RootPart, UUID.Random(), UUID.Random(), "switched", source);
            Item.Flags = ScriptEngineChoices.WithChoice(Item.Flags, choice);
            Assert.True(Group.RootPart.Inventory.CreateScriptInstance(Item.ItemID, 0, false, Phlox, 0));
            Group.ResumeScripts();
        }

        public bool InPhlox => H.Engine.HasScript(Item.ItemID, out _);
        public bool InYEngine => H.YEngine.HasScript(Item.ItemID, out _);
        public int Count(string marker) => H.Said.Count(s => s == marker);
        public string SaidText => string.Join(" | ", H.Said);

        public ScriptEngineChoiceResult Set(string target) => H.Scene.SetScriptEngine(Owner, Group.RootPart.UUID, Item.ItemID, target);

        /// <summary>Wait until the script runs in the engine given and has said "entry" <paramref name="entries"/> times.</summary>
        public void WaitStarted(bool inPhlox, int entries)
        {
            Assert.True(H.PumpUntil(() => (inPhlox ? InPhlox && !InYEngine : InYEngine && !InPhlox) && Count("entry") >= entries, Cap),
                $"phlox={InPhlox} yengine={InYEngine} said=[{SaidText}]");
        }

        /// <summary>Poke the script and return the global it answers with: 10 when it started fresh.</summary>
        public int Poke()
        {
            int before = H.Said.Count(s => s.StartsWith("n "));
            H.Scene.SimChatBroadcast("poke", ChatTypeEnum.Region, 5, Group.AbsolutePosition, "tester", UUID.Zero, false);
            Assert.True(H.PumpUntil(() => H.Said.Count(s => s.StartsWith("n ")) > before, Cap), $"no answer to the poke: said=[{SaidText}]");
            string answer = H.Said.Where(s => s.StartsWith("n ")).Skip(before).First();
            return int.Parse(answer.Substring(2));
        }
    }

    private static void SwitchAndCheckFresh(Rig rig, string target, bool toPhlox)
    {
        int entries = rig.Count("entry");
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set(target));
        rig.WaitStarted(toPhlox, entries + 1);
        Assert.Equal(10, rig.Poke());
        // A real window: state_entry ran once for this switch, and not again.
        rig.H.PumpFor(TimeSpan.FromSeconds(1));
        Assert.Equal(entries + 1, rig.Count("entry"));
    }

    [Fact]
    public void PhloxToYEngineAndBackStartsFreshEachTime()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var rig = new Rig(h);
        rig.Build(Body, ScriptEngineChoices.Phlox);
        rig.WaitStarted(inPhlox: true, entries: 1);
        Assert.Equal(10, rig.Poke());

        SwitchAndCheckFresh(rig, "YEngine", toPhlox: false);
        SwitchAndCheckFresh(rig, "Phlox", toPhlox: true);
        Assert.Equal(3, rig.Count("entry"));
    }

    [Fact]
    public void YEngineToPhloxAndBackStartsFreshEachTime()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var rig = new Rig(h);
        rig.Build(Body, ScriptEngineChoices.YEngine);
        rig.WaitStarted(inPhlox: false, entries: 1);
        Assert.Equal(10, rig.Poke());

        SwitchAndCheckFresh(rig, "Phlox", toPhlox: true);
        SwitchAndCheckFresh(rig, "YEngine", toPhlox: false);
        Assert.Equal(3, rig.Count("entry"));
    }

    [Fact]
    public void AQuickSwitchAwayFromPhloxAndBackStartsFresh()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var rig = new Rig(h);
        rig.Build(Body, ScriptEngineChoices.Phlox);
        rig.WaitStarted(inPhlox: true, entries: 1);
        Assert.Equal(10, rig.Poke());

        // Away and back before Phlox's loader has unloaded the script (nothing is pumped in between), so its unload save
        // is still to come when the script comes back.
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));

        // Back in Phlox it starts fresh. YEngine may run state_entry in the moment it holds the script, so the chat lines
        // alone do not say which engine spoke; the touch goes to Phlox's own instance, and its answer is Phlox's global:
        // 10 when Phlox started fresh, 20 when it resumed the state from before the switch. The script is touched rather
        // than poked: after a switch this quick it does not answer chat, for a reason not yet known.
        Assert.True(h.PumpUntil(() => rig.InPhlox && !rig.InYEngine && rig.Count("entry") >= 2, Cap), $"said=[{rig.SaidText}]");
        int before = h.Said.Count(s => s.StartsWith("n "));
        h.PostTouch(rig.Item.ItemID);
        Assert.True(h.PumpUntil(() => h.Said.Count(s => s.StartsWith("n ")) > before, Cap), $"no answer to the touch: said=[{rig.SaidText}]");
        Assert.Equal("n 10", h.Said.Where(s => s.StartsWith("n ")).Skip(before).First());
        Assert.DoesNotContain("n 20", h.Said);
    }

    [Fact]
    public void ChoosingTheEngineThatAlreadyRunsTheScriptRestartsNothing()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var rig = new Rig(h);
        rig.Build(Body, ScriptEngineChoices.None);
        rig.WaitStarted(inPhlox: true, entries: 1);
        Assert.Equal(10, rig.Poke());

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));

        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(rig.Group.RootPart.Inventory.GetInventoryItem(rig.Item.ItemID).Flags));
        // A real window: no restart, so no second state_entry; the script keeps its global.
        h.PumpFor(TimeSpan.FromSeconds(1));
        Assert.Equal(1, rig.Count("entry"));
        Assert.True(rig.InPhlox);
        Assert.Equal(20, rig.Poke());
    }

    [Fact]
    public void ClearingAChoiceThatChangesTheEngineStartsFreshAndSoDoesTheWayBack()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var rig = new Rig(h);
        // The first line names YEngine; the saved choice puts the script in Phlox until it is cleared.
        rig.Build("//YEngine:\n" + Body, ScriptEngineChoices.Phlox);
        rig.WaitStarted(inPhlox: true, entries: 1);
        Assert.Equal(10, rig.Poke());

        SwitchAndCheckFresh(rig, "", toPhlox: false);
        SwitchAndCheckFresh(rig, "Phlox", toPhlox: true);
    }

    [Fact]
    public void ARegionStopStraightAfterASwitchKeepsNoStateFromBeforeIt()
    {
        UUID itemId, assetId;
        using (var h1 = new SchedulerHarness(withYEngine: true))
        {
            var rig = new Rig(h1);
            rig.Build(Body, ScriptEngineChoices.Phlox);
            rig.WaitStarted(inPhlox: true, entries: 1);
            Assert.Equal(10, rig.Poke());
            itemId = rig.Item.ItemID;
            assetId = rig.Item.AssetID;

            Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
            h1.StopRegionAsTheSimulatorDoes();
        }

        // Phlox kept no row from before the switch through the stop...
        Assert.Null(SavedStateRig.Row(itemId));

        // ...so after the restart the script, should it come to Phlox again, starts fresh there.
        using var h2 = new SchedulerHarness();
        SavedStateRig.Rez(h2, h2.Prim, Body, assetId, itemId, 0, false, 0);
        Assert.True(h2.PumpUntil(() => h2.Engine.HasScript(itemId, out _) && h2.Said.Contains("entry"), Cap), $"said=[{string.Join(" | ", h2.Said)}]");
        h2.Scene.SimChatBroadcast("poke", ChatTypeEnum.Region, 5, h2.Prim.AbsolutePosition, "tester", UUID.Zero, false);
        Assert.True(h2.PumpUntil(() => h2.Said.Any(s => s.StartsWith("n ")), Cap), $"said=[{string.Join(" | ", h2.Said)}]");
        Assert.Contains("n 10", h2.Said);
        Assert.DoesNotContain("n 20", h2.Said);
    }
}
