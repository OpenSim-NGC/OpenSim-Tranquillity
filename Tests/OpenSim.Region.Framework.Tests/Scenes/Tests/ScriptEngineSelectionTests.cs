/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Which engine Scene.ResolveScriptEngine gives a script to: the engine its first line names (by name or by one of the
/// engine's header aliases), else the engine holding saved state for the item, else an engine that claims the script
/// (IScriptEngineSelection), else the default.
/// </summary>
public class ScriptEngineSelectionTests : OpenSimTestCase
{
    private sealed class Engine : MockScriptEngine, IScriptModule, IScriptStateProvider, IScriptEngineSelection
    {
        public new string ScriptEngineName { get; }
        public IReadOnlyList<string> HeaderAliases { get; init; } = Array.Empty<string>();
        public Func<string, bool> Claims { get; init; } = _ => false;
        public bool HoldsState { get; set; }
        public readonly List<string> Asked = new();

        public Engine(string name) { ScriptEngineName = name; }

        public bool ClaimsScript(string scriptSource)
        {
            Asked.Add(scriptSource);
            return Claims(scriptSource);
        }

        public bool HasSavedScriptState(OpenMetaverse.UUID itemID, OpenMetaverse.UUID assetID) => HoldsState;
    }

    /// <summary>An engine that knows nothing of the selection hooks.</summary>
    private sealed class PlainEngine : MockScriptEngine, IScriptModule
    {
        public new string ScriptEngineName { get; }
        public PlainEngine(string name) { ScriptEngineName = name; }
    }

    private static Scene Region(params IScriptModule[] engines)
    {
        Scene scene = new SceneHelpers().SetupScene();
        foreach (IScriptModule engine in engines)
            scene.StackModuleInterface<IScriptModule>(engine);
        return scene;
    }

    private static TaskInventoryItem Item() => new() { ItemID = OpenMetaverse.UUID.Random(), AssetID = OpenMetaverse.UUID.Random() };

    private static bool IsB(string source) => source.StartsWith("--!b");

    [Fact]
    public void A_header_alias_gives_the_script_to_the_engine_that_names_it()
    {
        var alpha = new Engine("Alpha") { HeaderAliases = new[] { "Al" } };
        var beta = new Engine("Beta");
        Scene scene = Region(beta, alpha);

        Assert.Same(alpha, scene.ResolveScriptEngine("Beta", "//Al:\ndefault {}"));
        Assert.Same(alpha, scene.ResolveScriptEngine("Beta", "//Al:slua\nprint(1)"));
        Assert.Same(alpha, scene.ResolveScriptEngine("Beta", "//Alpha:\ndefault {}"));   // its own name still works
        Assert.Same(beta, scene.ResolveScriptEngine("Beta", "//Be:\ndefault {}"));       // an unknown name is no header
    }

    [Fact]
    public void An_alias_is_compared_as_written_like_an_engine_name()
    {
        var alpha = new Engine("Alpha") { HeaderAliases = new[] { "Al" } };
        var beta = new Engine("Beta");
        Scene scene = Region(beta, alpha);

        Assert.Same(alpha, scene.ResolveScriptEngine("Beta", "//Al:\ndefault {}"));
        Assert.Same(beta, scene.ResolveScriptEngine("Beta", "//al:\ndefault {}"));
    }

    [Fact]
    public void A_header_names_an_engine_before_anything_claims_the_script()
    {
        var alpha = new Engine("Alpha");
        var beta = new Engine("Beta") { Claims = _ => true };
        Scene scene = Region(alpha, beta);

        Assert.Same(beta, scene.ResolveScriptEngine("Alpha", "default {}"));              // the claim works
        beta.Asked.Clear();
        Assert.Same(alpha, scene.ResolveScriptEngine("Beta", "//Alpha:\ndefault {}"));    // the header wins
        Assert.Empty(beta.Asked);
    }

    [Fact]
    public void An_engine_that_claims_the_script_gets_it_from_the_default()
    {
        var alpha = new Engine("Alpha");
        var beta = new Engine("Beta") { Claims = IsB };
        Scene scene = Region(alpha, beta);

        Assert.Same(beta, scene.ResolveScriptEngine("Alpha", "--!b\nprint(1)"));
        Assert.Same(beta, scene.ResolveScriptEngine("Alpha", "--!b\nprint(1)", Item()));
        Assert.Same(alpha, scene.ResolveScriptEngine("Alpha", "default {}"));
        Assert.Same(alpha, scene.ResolveScriptEngine("Alpha", "default {}", Item()));
    }

    [Fact]
    public void An_engine_that_holds_saved_state_keeps_the_script_over_a_claim()
    {
        var alpha = new Engine("Alpha");
        var beta = new Engine("Beta") { Claims = IsB };
        Scene scene = Region(alpha, beta);
        TaskInventoryItem item = Item();

        Assert.Same(beta, scene.ResolveScriptEngine("Alpha", "--!b\nprint(1)", item));    // no state anywhere: the claim
        alpha.HoldsState = true;
        Assert.Same(alpha, scene.ResolveScriptEngine("Alpha", "--!b\nprint(1)", item));   // the default holds state
        alpha.HoldsState = false;
        var gamma = new Engine("Gamma");
        scene.StackModuleInterface<IScriptModule>(gamma);
        gamma.HoldsState = true;
        Assert.Same(gamma, scene.ResolveScriptEngine("Alpha", "--!b\nprint(1)", item));   // another engine holds state
    }

    [Fact]
    public void A_script_with_no_source_is_not_offered_to_a_claim_and_goes_by_saved_state()
    {
        var alpha = new Engine("Alpha");
        var beta = new Engine("Beta") { Claims = _ => true };
        Scene scene = Region(alpha, beta);
        TaskInventoryItem item = Item();

        Assert.Same(beta, scene.ResolveScriptEngine("Alpha", "default {}", item));        // offered: the claim works
        beta.Asked.Clear();
        Assert.Same(alpha, scene.ResolveScriptEngine("Alpha", string.Empty, item));
        Assert.Same(alpha, scene.ResolveScriptEngine("Alpha", null, item));
        Assert.Empty(beta.Asked);
        beta.HoldsState = true;
        Assert.Same(beta, scene.ResolveScriptEngine("Alpha", string.Empty, item));        // saved state still routes it
    }

    [Fact]
    public void The_first_engine_that_claims_the_script_gets_it()
    {
        var alpha = new Engine("Alpha");
        var beta = new Engine("Beta") { Claims = IsB };
        var gamma = new Engine("Gamma") { Claims = IsB };
        Scene scene = Region(alpha, beta, gamma);

        Assert.Same(beta, scene.ResolveScriptEngine("Alpha", "--!b\n"));
        beta.Asked.Clear();
        gamma.Asked.Clear();
        Assert.Same(alpha, scene.ResolveScriptEngine("Alpha", "default {}"));
        Assert.Single(beta.Asked);                                                         // every claimant is asked
        Assert.Single(gamma.Asked);
    }

    [Fact]
    public void An_engine_without_the_selection_hooks_is_selected_only_as_before()
    {
        var plain = new PlainEngine("Plain");
        var beta = new Engine("Beta") { Claims = IsB };
        Scene scene = Region(plain, beta);

        Assert.Same(beta, scene.ResolveScriptEngine("Plain", "--!b\n"));                  // the claim is read
        Assert.Same(plain, scene.ResolveScriptEngine("Beta", "//Plain:\ndefault {}"));
        Assert.Same(plain, scene.ResolveScriptEngine("Plain", "default {}"));
        Assert.Same(beta, scene.ResolveScriptEngine("Beta", "default {}"));
    }

    [Fact]
    public void The_default_engine_can_claim_a_script_too()
    {
        var alpha = new Engine("Alpha") { Claims = IsB };
        var beta = new Engine("Beta") { Claims = IsB };
        Scene scene = Region(beta, alpha);

        Assert.Same(beta, scene.ResolveScriptEngine("Alpha", "--!b\n"));                  // module order, not the default, decides
        Assert.Same(alpha, scene.ResolveScriptEngine("Alpha", "default {}"));
    }
}
