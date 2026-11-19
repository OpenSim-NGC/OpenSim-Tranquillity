/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Choosing a script's engine without saving its text (<see cref="Scene.SetScriptEngine"/>, behind the SetScriptEngine
/// capability and osSetScriptEngine). Whoever may save the script may choose its engine. A change restarts the script in
/// the engine it now places the script in. The script's text is not stored again: only the saved choice and the engine
/// that runs it change. The script starts fresh there: state its earlier engine saved does not hold it back, and state the
/// new engine kept for the same text is not resumed (the script is reset). Choosing the engine that already runs the
/// script saves the choice and restarts nothing. Calls for one script are made one at a time.
/// </summary>
public class ScriptEngineChoiceSetTests : OpenSimTestCase
{
    private const string YEngine = "YEngine", Phlox = "InWorldz.Phlox";

    private sealed class Engine : MockScriptEngine, IScriptModule, IScriptStateProvider, IScriptEngineSelection
    {
        public new string ScriptEngineName { get; }
        public IReadOnlyList<string> HeaderAliases { get; init; } = Array.Empty<string>();
        public Func<UUID, UUID, bool> HoldsState { get; set; } = (_, _) => false;
        public readonly List<UUID> Started = new();
        public readonly List<UUID> Removed = new();
        public readonly List<UUID> Resets = new();
        public readonly HashSet<UUID> Holds = new();
        /// <summary>Called on the thread that removes a script this engine holds, before it is removed.</summary>
        public Action<UUID> OnRemoving { get; set; } = _ => { };

        public Engine(string name) { ScriptEngineName = name; }

        public bool ClaimsScript(string scriptSource) => false;
        public ArrayList GetScriptErrors(UUID itemID) => new();
        public bool SuspendScript(UUID itemID) => true;
        public bool ResumeScript(UUID itemID) => true;
        public bool HasScript(UUID itemID, out bool running) { lock (Holds) { running = Holds.Contains(itemID); return running; } }
        public bool GetScriptState(UUID itemID) => false;
        public void SetScriptState(UUID itemID, bool state, bool self) { }
        public void SaveAllState() { }
        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool HasSavedScriptState(UUID itemID, UUID assetID) => HoldsState(itemID, assetID);

        public void Attach(Scene scene)
        {
            scene.EventManager.OnRezScript += (localID, itemID, script, startParam, postOnRez, engine, stateSource) =>
            {
                if (engine == ScriptEngineName)
                {
                    lock (Holds) { Started.Add(itemID); Holds.Add(itemID); }
                }
            };
            scene.EventManager.OnRemoveScript += (localID, itemID) =>
            {
                bool held;
                lock (Holds) held = Holds.Contains(itemID);
                if (!held)
                    return;
                OnRemoving(itemID);
                lock (Holds) { Removed.Add(itemID); Holds.Remove(itemID); }
            };
            scene.EventManager.OnScriptReset += (localID, itemID) =>
            {
                lock (Holds)
                {
                    if (Holds.Contains(itemID))
                        Resets.Add(itemID);
                }
            };
        }
    }

    private sealed class Rig
    {
        public Scene Scene;
        public Engine Y = new(YEngine);
        public Engine P = new(Phlox) { HeaderAliases = new[] { "Phlox" } };
        public UUID Owner = UUID.Random();
        public SceneObjectPart Part;
        public TaskInventoryItem Item;

        public Rig(string choiceSetting = null, uint itemPerms = (uint)PermissionMask.All)
        {
            var config = new IniConfigSource();
            IConfig startup = config.AddConfig("Startup");
            startup.Set("DefaultScriptEngine", YEngine);
            startup.Set("serverside_object_permissions", "true");
            if (choiceSetting is not null)
                startup.Set("ScriptEngineChoice", choiceSetting);
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            SceneHelpers.SetupSceneModules(Scene, config, new DefaultPermissionsModule());
            Scene.StackModuleInterface<IScriptModule>(Y);
            Scene.StackModuleInterface<IScriptModule>(P);
            Y.Attach(Scene);
            P.Attach(Scene);
            Part = SceneHelpers.AddSceneObject(Scene, "Example Object", Owner).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "target script", "default { state_entry() { } }");
            Item.OwnerID = Owner;
            Item.BasePermissions = Item.CurrentPermissions = itemPerms;
            Assert.True(Part.Inventory.CreateScriptInstance(Item, 0, false, YEngine, 0));
            Y.Started.Clear();
        }

        public TaskInventoryItem Stored => Part.Inventory.GetInventoryItem(Item.ItemID);
        public int Choice => ScriptEngineChoices.FromFlags(Stored.Flags);
        public ScriptEngineChoiceResult Set(string target, UUID? who = null) => Scene.SetScriptEngine(who ?? Owner, Part.UUID, Item.ItemID, target);
    }

    [Theory]
    [InlineData("Phlox")]
    [InlineData("InWorldz.Phlox")]
    [InlineData("engine:Phlox")]
    [InlineData(" phlox ")]
    public void TheOwnerChoosesAnEngineAndTheScriptRestartsThere(string target)
    {
        var rig = new Rig();
        UUID before = rig.Stored.AssetID;
        string text = Utils.BytesToString(rig.Scene.AssetService.Get(before.ToString()).Data);

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set(target));

        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.P.Started);
        Assert.Empty(rig.Y.Started);
        Assert.Contains(rig.Item.ItemID, rig.Y.Removed);
        // the same text, not a new copy of it
        Assert.Equal(before, rig.Stored.AssetID);
        Assert.Equal(text, Utils.BytesToString(rig.Scene.AssetService.Get(rig.Stored.AssetID.ToString()).Data));
        Assert.Empty(rig.P.Resets);
    }

    [Fact]
    public void ChangingTheEngineAgainAndAgainKeepsTheSameText()
    {
        var rig = new Rig();
        UUID asset = rig.Stored.AssetID;

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));
        Assert.Equal(asset, rig.Stored.AssetID);
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        Assert.Equal(asset, rig.Stored.AssetID);
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set(""));
        Assert.Equal(asset, rig.Stored.AssetID);
    }

    [Fact]
    public void ChoosingTheEngineThatAlreadyRunsTheScriptSavesTheChoiceAndRestartsNothing()
    {
        var rig = new Rig();

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));

        Assert.Equal(ScriptEngineChoices.YEngine, rig.Choice);
        Assert.Empty(rig.Y.Removed);
        Assert.Empty(rig.Y.Started);
        Assert.Empty(rig.P.Started);
    }

    [Fact]
    public void StateTheNewEngineKeptForTheSameTextIsNotResumed()
    {
        var rig = new Rig();
        UUID item = rig.Item.ItemID, asset = rig.Stored.AssetID;
        rig.P.HoldsState = (i, a) => i == item && a == asset;

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));

        Assert.Equal(new[] { item }, rig.P.Started);
        Assert.Equal(new[] { item }, rig.P.Resets);
    }

    [Fact]
    public void ClearingTheChoiceLeavesAScriptWhereItsSavedStateIs()
    {
        var rig = new Rig();
        UUID item = rig.Item.ItemID, asset = rig.Stored.AssetID;
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));
        rig.P.HoldsState = (i, a) => i == item && a == asset;
        rig.P.Started.Clear();

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set(""));

        // the ordinary steps place it: saved state keeps it on Phlox, which already runs it, so nothing restarts
        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
        Assert.Empty(rig.P.Removed);
        Assert.Empty(rig.P.Started);
        Assert.Empty(rig.Y.Started);
    }

    [Fact]
    public void ASetScriptEngineCallAndAScriptCallOnOneScriptDoNotInterleave()
    {
        var rig = new Rig();
        using var inFirstCall = new ManualResetEventSlim();
        using var releaseFirstCall = new ManualResetEventSlim();
        int removals = 0;
        rig.Y.OnRemoving = _ =>
        {
            if (Interlocked.Increment(ref removals) == 1)
            {
                inFirstCall.Set();
                releaseFirstCall.Wait(TimeSpan.FromSeconds(30));
            }
        };
        UUID callingScript = UUID.Random();

        // the capability's call, held inside its restart
        Task<ScriptEngineChoiceResult> first = Task.Run(() => rig.Scene.SetScriptEngine(rig.Owner, rig.Part.UUID, rig.Item.ItemID, "Phlox"));
        Assert.True(inFirstCall.Wait(TimeSpan.FromSeconds(30)), "the first call never reached the restart");
        // an OSSL call from another script on the same script while the first has not finished
        Task<ScriptEngineChoiceResult> second = Task.Run(() =>
            rig.Scene.SetScriptEngine(rig.Owner, rig.Part.UUID, rig.Item.ItemID, "engine:InWorldz.Phlox", callingScript));

        // a fixed window, to prove the second call does NOT run while the first holds the script
        Assert.False(second.Wait(TimeSpan.FromMilliseconds(500)), "the second call ran while the first was still changing the script");
        releaseFirstCall.Set();

        Assert.True(Task.WaitAll(new Task[] { first, second }, TimeSpan.FromSeconds(30)), "the calls did not finish");
        Assert.Equal(ScriptEngineChoiceResult.Changed, first.Result);
        Assert.Equal(ScriptEngineChoiceResult.Unchanged, second.Result);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.P.Started);
        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);
    }

    [Fact]
    public void StateTheOldEngineSavedDoesNotHoldTheScriptBack()
    {
        var rig = new Rig();
        UUID item = rig.Item.ItemID, oldAsset = rig.Stored.AssetID;
        rig.Y.HoldsState = (i, a) => i == item && a == oldAsset;

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));

        Assert.Equal(new[] { item }, rig.P.Started);
        Assert.Empty(rig.Y.Started);
    }

    [Fact]
    public void TheSameChoiceAgainChangesNothingAndRestartsNothing()
    {
        var rig = new Rig();
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));
        UUID asset = rig.Stored.AssetID;
        rig.P.Started.Clear();

        Assert.Equal(ScriptEngineChoiceResult.Unchanged, rig.Set("engine:InWorldz.Phlox"));

        Assert.Empty(rig.P.Started);
        Assert.Equal(asset, rig.Stored.AssetID);
        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("engine:")]
    public void AnEmptyTargetClearsTheChoiceAndTheScriptGoesBackToTheDefault(string target)
    {
        var rig = new Rig();
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set(target));

        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Y.Started);
    }

    [Fact]
    public void AnEngineNotInTheTableChangesNothing()
    {
        var rig = new Rig();
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("YEngine"));
        uint flags = rig.Stored.Flags;

        Assert.Equal(ScriptEngineChoiceResult.UnknownEngine, rig.Set("NoSuchEngine"));

        Assert.Equal(flags, rig.Stored.Flags);
    }

    [Fact]
    public void ARegionThatKeepsNoChoicesRefuses()
    {
        var rig = new Rig(choiceSetting: "false");

        Assert.Equal(ScriptEngineChoiceResult.Disabled, rig.Set("Phlox"));
        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
        Assert.Empty(rig.P.Started);
    }

    [Fact]
    public void SomeoneWhoMayNotEditTheScriptIsRefused()
    {
        var rig = new Rig();

        Assert.Equal(ScriptEngineChoiceResult.NotAllowed, rig.Set("Phlox", UUID.Random()));
        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
        Assert.Empty(rig.P.Started);
    }

    [Theory]
    [InlineData((uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Move))]
    [InlineData((uint)(PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Move))]
    public void ByDefaultNotEvenTheOwnerMayChooseForAScriptTheyCannotEdit(uint perms)
    {
        var rig = new Rig(itemPerms: perms);

        Assert.Equal(ScriptEngineChoiceResult.NotAllowed, rig.Set("Phlox"));
        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
    }

    [Fact]
    public void AMissingScriptIsNotFound()
    {
        var rig = new Rig();
        Assert.Equal(ScriptEngineChoiceResult.NotFound, rig.Scene.SetScriptEngine(rig.Owner, rig.Part.UUID, UUID.Random(), "Phlox"));
        Assert.Equal(ScriptEngineChoiceResult.NotFound, rig.Scene.SetScriptEngine(rig.Owner, UUID.Random(), rig.Item.ItemID, "Phlox"));
    }

    [Fact]
    public void AScriptMayNotChangeItsOwnEngine()
    {
        var rig = new Rig();
        Assert.Equal(ScriptEngineChoiceResult.OwnScript,
            rig.Scene.SetScriptEngine(rig.Owner, rig.Part.UUID, rig.Item.ItemID, "Phlox", rig.Item.ItemID));
        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
    }

    [Fact]
    public void AStoppedScriptStaysStopped()
    {
        var rig = new Rig();
        rig.Stored.ScriptRunning = false;

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox"));

        Assert.False(rig.Stored.ScriptRunning);
    }
}
