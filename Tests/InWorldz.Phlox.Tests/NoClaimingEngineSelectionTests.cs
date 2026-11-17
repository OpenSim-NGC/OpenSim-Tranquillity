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
/// A region that loads only Phlox and YEngine, the two engines in this repository, places scripts by their first line
/// and the default engine, and nothing else: no loaded engine claims scripts (Phlox's ClaimsScript is false and YEngine
/// does not take part in selection), so a script without an engine header goes to the default engine and a script whose
/// header names YEngine or Phlox goes to that engine. A header naming an engine the region does not load ("//SLua:")
/// falls to the default, and so does a script that begins with "--!slua". Each case is checked twice: the engine
/// Scene.ResolveScriptEngine returns, and, for scripts both engines compile, the engine that runs the script after the
/// core's CreateScriptInstance. In "phlox-yengine" (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class NoClaimingEngineSelectionTests
{
    private const string Phlox = "InWorldz.Phlox", YEngine = "YEngine";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    private const string Lsl = "default { state_entry() { llSay(0, \"entry\"); } }";

    public static IEnumerable<object[]> Cases()
    {
        foreach (string defaultEngine in new[] { Phlox, YEngine })
        {
            // Started for real: both engines compile these.
            yield return new object[] { defaultEngine, Lsl, defaultEngine, true };
            yield return new object[] { defaultEngine, "\n" + Lsl, defaultEngine, true };
            yield return new object[] { defaultEngine, "// a comment: not a header\n" + Lsl, defaultEngine, true };
            yield return new object[] { defaultEngine, "//YEngine:\n" + Lsl, YEngine, true };
            yield return new object[] { defaultEngine, "//YEngine:lsl\n" + Lsl, YEngine, true };
            yield return new object[] { defaultEngine, "//InWorldz.Phlox:\n" + Lsl, Phlox, true };

            // Resolved only: the default engine may not compile these, but the region still chooses it.
            yield return new object[] { defaultEngine, "--!slua\nll.Say(0, \"entry\")\n", defaultEngine, false };
            yield return new object[] { defaultEngine, "//SLua:\n" + Lsl, defaultEngine, false };
            yield return new object[] { defaultEngine, "//SLua:LSL\n" + Lsl, defaultEngine, false };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WithOnlyPhloxAndYEngineLoadedAScriptGoesWhereItsHeaderOrTheDefaultSays(
        string defaultEngine, string source, string expected, bool start)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(h.Scene, defaultEngine);

        // No loaded engine claims scripts. Looked up by name, so the same file also builds and runs on a tree that
        // predates IScriptEngineSelection, to compare the placements with that tree.
        IScriptModule[] loaded = h.Scene.RequestModuleInterfaces<IScriptModule>();
        Assert.Equal(new[] { Phlox, YEngine }, loaded.Select(e => e.ScriptEngineName).OrderBy(n => n, StringComparer.Ordinal));
        foreach (IScriptModule engine in loaded)
        {
            System.Reflection.MethodInfo claims = engine.GetType().GetInterface("IScriptEngineSelection")?.GetMethod("ClaimsScript");
            if (claims is not null)
                Assert.False((bool)claims.Invoke(engine, new object[] { source }), $"{engine.ScriptEngineName} claims the script");
        }

        Assert.Equal(expected, h.Scene.ResolveScriptEngine(defaultEngine, source)?.ScriptEngineName);
        if (!start)
            return;

        TaskInventoryItem item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "Example Script", source);
        Assert.True(h.Prim.Inventory.CreateScriptInstance(item.ItemID, 0, false, defaultEngine, 0));
        h.Prim.ParentGroup.ResumeScripts();

        Assert.True(h.PumpUntil(() => h.Said.Contains("entry"), Cap), $"the script never ran: [{string.Join(" | ", h.Said)}]");
        Assert.Equal(expected == Phlox, h.Engine.HasScript(item.ItemID, out _));
        Assert.Equal(expected == YEngine, h.YEngine.HasScript(item.ItemID, out _));
    }
}
