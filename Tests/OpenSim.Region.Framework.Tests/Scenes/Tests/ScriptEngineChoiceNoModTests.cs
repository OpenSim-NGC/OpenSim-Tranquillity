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
/// [Startup] ScriptEngineChoiceNoMod (default off, as in Second Life no one can retarget a script they cannot edit) lets
/// the owner of an object choose the engine of a script in it that they may not edit (no modify or no copy). No one else
/// can, and with the setting off not even the owner can.
/// </summary>
public class ScriptEngineChoiceNoModTests : OpenSimTestCase
{
    private const string YEngine = "YEngine", Phlox = "InWorldz.Phlox";
    private const uint NoMod = (uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Move);
    private const uint NoCopy = (uint)(PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Move);

    private sealed class Engine : MockScriptEngine, IScriptModule
    {
        private readonly string m_name;
        public readonly List<UUID> Started = new();
        public Engine(string name) { m_name = name; }
        string IScriptModule.ScriptEngineName => m_name;
        ArrayList IScriptModule.GetScriptErrors(UUID itemID) => new();
        bool IScriptModule.HasScript(UUID itemID, out bool running) { running = false; return false; }
        bool IScriptModule.SuspendScript(UUID itemID) => true;
        bool IScriptModule.ResumeScript(UUID itemID) => true;
        bool IScriptModule.GetScriptState(UUID itemID) => false;
        void IScriptModule.SaveAllState() { }
        string IScriptModule.GetXMLState(UUID itemID) => string.Empty;
        bool IScriptModule.SetXMLState(UUID itemID, string xml) => false;

        public void Attach(Scene scene)
            => scene.EventManager.OnRezScript += (localID, itemID, script, startParam, postOnRez, engine, stateSource) =>
            {
                if (engine == m_name)
                    Started.Add(itemID);
            };
    }

    private sealed class Rig
    {
        public Scene Scene;
        public Engine P = new(Phlox);
        public UUID Owner = UUID.Random();
        public SceneObjectPart Part;
        public TaskInventoryItem Item;

        public Rig(string noModSetting, uint itemPerms)
        {
            var config = new IniConfigSource();
            IConfig startup = config.AddConfig("Startup");
            startup.Set("DefaultScriptEngine", YEngine);
            startup.Set("serverside_object_permissions", "true");
            if (noModSetting is not null)
                startup.Set("ScriptEngineChoiceNoMod", noModSetting);
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            SceneHelpers.SetupSceneModules(Scene, config, new DefaultPermissionsModule());
            var y = new Engine(YEngine);
            Scene.StackModuleInterface<IScriptModule>(y);
            Scene.StackModuleInterface<IScriptModule>(P);
            y.Attach(Scene);
            P.Attach(Scene);
            Part = SceneHelpers.AddSceneObject(Scene, "Example Object", Owner).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "product script", "default { state_entry() { } }");
            Item.OwnerID = Owner;
            Item.CreatorID = UUID.Random();
            Item.BasePermissions = Item.CurrentPermissions = itemPerms;
        }

        public int Choice => ScriptEngineChoices.FromFlags(Part.Inventory.GetInventoryItem(Item.ItemID).Flags);
        public ScriptEngineChoiceResult Set(string target, UUID who) => Scene.SetScriptEngine(who, Part.UUID, Item.ItemID, target);
    }

    [Fact]
    public void TheSettingIsOffByDefault()
    {
        Assert.False(new Rig(null, NoMod).Scene.ScriptEngineChoiceNoModEnabled);
        Assert.True(new Rig("true", NoMod).Scene.ScriptEngineChoiceNoModEnabled);
    }

    [Theory]
    [InlineData(NoMod)]
    [InlineData(NoCopy)]
    public void WithTheSettingOnTheObjectsOwnerMayChoose(uint perms)
    {
        var rig = new Rig("true", perms);

        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("Phlox", rig.Owner));

        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.P.Started);
        Assert.Equal(ScriptEngineChoiceResult.Changed, rig.Set("", rig.Owner));
        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
    }

    [Theory]
    [InlineData(NoMod)]
    [InlineData(NoCopy)]
    public void WithTheSettingOnNoOneElseMayChoose(uint perms)
    {
        var rig = new Rig("true", perms);

        Assert.Equal(ScriptEngineChoiceResult.NotAllowed, rig.Set("Phlox", UUID.Random()));
        Assert.Equal(ScriptEngineChoiceResult.NotAllowed, rig.Set("Phlox", rig.Item.CreatorID));
        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void WithTheSettingOffNotEvenTheOwnerMayChoose(string setting)
    {
        var rig = new Rig(setting, NoMod);

        Assert.Equal(ScriptEngineChoiceResult.NotAllowed, rig.Set("Phlox", rig.Owner));
        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
    }
}
