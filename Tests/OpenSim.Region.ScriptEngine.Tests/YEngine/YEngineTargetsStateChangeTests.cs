/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

#nullable disable

using System.Reflection;
using System.Runtime.CompilerServices;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Scripting.WorldComm;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Yengine.Tests;

/// <summary>
/// llTarget and llRotTarget targets belong to the script until llTargetRemove / llRotTargetRemove, a reset, or the
/// script or its object going away. The SL State page lists what a state change releases (listens, repeating sensors
/// and the event queue) and targets are not on it; the llTarget page says the target fires until it is removed.
///
/// These tests drive a real XMRInstance through StateChange(), the call YEngine makes when a script runs
/// "state x;", against a real scene object, the real shared AsyncCommandManager and a real Yengine object (not
/// started). The compiled script is replaced by a handler table that has at_target / at_rot_target handlers in
/// the states that should have them, so the instance computes its event flags from its own table as it does when
/// running a script. The targets are read back from the object and the at_target / at_rot_target events are
/// read from the scene's event manager, the way the engine's own handlers receive them.
///
/// The classes run in parallel: each builds its own scene, engine and instance, and the shared
/// AsyncCommandManager keeps its state per engine object.
/// </summary>
public class YEngineTargetsStateChangeTests
{
    private const int StateA = 0;
    private const int StateB = 1;
    private const int StateWithoutTargetHandlers = 2;

    private sealed class Rig
    {
        public Scene Scene;
        public SceneObjectGroup Group;
        public SceneObjectPart Part;
        public Yengine Engine;
        public XMRInstance Instance;
        public AsyncCommandManager Acm;
        public UUID ItemID = UUID.Random();
        public List<(UUID script, uint handle)> AtTargetEvents = new();
        public List<(UUID script, uint handle)> AtRotTargetEvents = new();
    }

    private static void Set(object target, string field, object value)
    {
        FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(f);
        f.SetValue(target, value);
    }

    private static void NoHandler(XMRInstAbstract instance) { }

    private static Rig MakeRig()
    {
        Rig rig = new();
        rig.Scene = new SceneHelpers().SetupScene();
        rig.Group = SceneHelpers.AddSceneObject(rig.Scene);
        rig.Part = rig.Group.RootPart;

        // A Yengine that has been initialised disabled: it has its config and its scene, and starts nothing.
        IConfigSource config = new IniConfigSource();
        config.AddConfig("YEngine").Set("Enabled", "false");
        rig.Engine = new Yengine();
        rig.Engine.Initialise(config);
        Set(rig.Engine, "m_Scene", rig.Scene);
        rig.Acm = new AsyncCommandManager(rig.Engine);

        // A handler table for three states: A and B handle the target events, the third handles none.
        ScriptObjCode objCode = (ScriptObjCode)RuntimeHelpers.GetUninitializedObject(typeof(ScriptObjCode));
        objCode.scriptEventHandlerTable = new ScriptEventHandler[3, (int)ScriptEventCode.Size];
        foreach (int state in new[] { StateA, StateB })
        {
            objCode.scriptEventHandlerTable[state, (int)ScriptEventCode.at_target] = NoHandler;
            objCode.scriptEventHandlerTable[state, (int)ScriptEventCode.at_rot_target] = NoHandler;
        }

        XMRLSL_Api api = new();
        api.acm = rig.Acm;

        rig.Instance = new XMRInstance();
        Set(rig.Instance, "m_Engine", rig.Engine);
        Set(rig.Instance, "m_XMRLSLApi", api);
        rig.Instance.m_Part = rig.Part;
        rig.Instance.m_LocalID = rig.Part.LocalId;
        rig.Instance.m_ItemID = rig.ItemID;
        rig.Instance.m_ObjCode = objCode;
        rig.Instance.stateCode = StateA;

        // The script's first state has handlers for the target events, as the engine registers on start.
        rig.Part.SetScriptEvents(rig.ItemID, rig.Instance.GetStateEventFlags(StateA));

        rig.Scene.EventManager.OnScriptAtTargetEvent += (id, handle, tp, cp) => { lock (rig.AtTargetEvents) rig.AtTargetEvents.Add((id, handle)); };
        rig.Scene.EventManager.OnScriptAtRotTargetEvent += (id, handle, tr, cr) => { lock (rig.AtRotTargetEvents) rig.AtRotTargetEvents.Add((id, handle)); };
        return rig;
    }

    /// <summary>Sets a position target on the object's current position, so it is reached at once.</summary>
    private static int SetReachedTarget(Rig rig)
        => rig.Group.RegisterTargetWaypoint(rig.ItemID, rig.Group.RootPart.GroupPosition, 1.0f);

    /// <summary>Sets a rotation target on the object's current rotation, so it is reached at once.</summary>
    private static int SetReachedRotTarget(Rig rig)
        => rig.Group.RegisterRotTargetWaypoint(rig.ItemID, rig.Group.RootPart.RotationOffset, 1.0f);

    private static void ChangeState(Rig rig, int newState)
    {
        // The engine sets stateCode to the new state, then calls StateChange().
        rig.Instance.stateCode = newState;
        rig.Instance.StateChange();
    }

    [Fact]
    public void ATargetSetInOneStateStillFiresAtTargetInTheNextState()
    {
        Rig rig = MakeRig();
        int handle = SetReachedTarget(rig);

        ChangeState(rig, StateB);

        Assert.True(rig.Group.AtTargets.ContainsKey(handle));
        rig.Group.CheckAtTargets();
        lock (rig.AtTargetEvents)
            Assert.Contains((rig.ItemID, (uint)handle), rig.AtTargetEvents);
    }

    [Fact]
    public void ARotTargetSetInOneStateStillFiresAtRotTargetInTheNextState()
    {
        Rig rig = MakeRig();
        int handle = SetReachedRotTarget(rig);

        ChangeState(rig, StateB);

        Assert.True(rig.Group.RotTargets.ContainsKey(handle));
        rig.Group.CheckAtTargets();
        lock (rig.AtRotTargetEvents)
            Assert.Contains((rig.ItemID, (uint)handle), rig.AtRotTargetEvents);
    }

    [Fact]
    public void ATargetSurvivesSeveralStateChanges()
    {
        Rig rig = MakeRig();
        int handle = SetReachedTarget(rig);

        ChangeState(rig, StateB);
        ChangeState(rig, StateA);
        ChangeState(rig, StateB);

        Assert.True(rig.Group.AtTargets.ContainsKey(handle));
    }

    [Fact]
    public void LlTargetRemoveInTheNewStateStillRemovesTheTarget()
    {
        Rig rig = MakeRig();
        int handle = SetReachedTarget(rig);
        int rotHandle = SetReachedRotTarget(rig);
        ChangeState(rig, StateB);

        // llTargetRemove and llRotTargetRemove call these.
        rig.Group.UnregisterTargetWaypoint(handle);
        rig.Group.UnRegisterRotTargetWaypoint(rotHandle);

        Assert.False(rig.Group.AtTargets.ContainsKey(handle));
        Assert.False(rig.Group.RotTargets.ContainsKey(rotHandle));
        rig.Group.CheckAtTargets();
        lock (rig.AtTargetEvents)
            Assert.Empty(rig.AtTargetEvents);
        lock (rig.AtRotTargetEvents)
            Assert.Empty(rig.AtRotTargetEvents);
    }

    [Fact]
    public void AnotherScriptsTargetsAreNotTouchedByThisScriptsStateChange()
    {
        Rig rig = MakeRig();
        UUID otherScript = UUID.Random();
        int other = rig.Group.RegisterTargetWaypoint(otherScript, rig.Group.RootPart.GroupPosition, 1.0f);
        int mine = SetReachedTarget(rig);

        ChangeState(rig, StateB);

        Assert.True(rig.Group.AtTargets.ContainsKey(other));
        Assert.True(rig.Group.AtTargets.ContainsKey(mine));
    }

    [Fact]
    public void AScriptResetStillClearsItsTargets()
    {
        Rig rig = MakeRig();
        int handle = SetReachedTarget(rig);
        int rotHandle = SetReachedRotTarget(rig);
        Assert.True(rig.Group.AtTargets.ContainsKey(handle));
        Assert.True(rig.Group.RotTargets.ContainsKey(rotHandle));

        // llResetScript ends in ResetLocked(), which clears the script's targets and puts it back in the default state.
        MethodInfo reset = typeof(XMRInstance).GetMethod("ResetLocked", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(reset);
        try
        {
            reset.Invoke(rig.Instance, new object[] { "test" });
        }
        catch (TargetInvocationException)
        {
            // The reset also touches parts of a running script that this rig does not have. By then it has already
            // cleared the targets, which is what is checked.
        }

        Assert.False(rig.Group.AtTargets.ContainsKey(handle));
        Assert.False(rig.Group.RotTargets.ContainsKey(rotHandle));
    }

    [Fact]
    public void RemovingTheScriptStillClearsItsTargets()
    {
        Rig rig = MakeRig();
        ChangeState(rig, StateB);
        int handle = SetReachedTarget(rig);
        int rotHandle = SetReachedRotTarget(rig);
        Assert.True(rig.Group.AtTargets.ContainsKey(handle));
        Assert.True(rig.Group.RotTargets.ContainsKey(rotHandle));

        // The scene's script-removal path (an item deleted or a script replaced) calls this.
        rig.Part.RemoveScriptEvents(rig.ItemID);

        Assert.False(rig.Group.AtTargets.ContainsKey(handle));
        Assert.False(rig.Group.RotTargets.ContainsKey(rotHandle));
    }

    [Fact]
    public void DerezzingTheObjectStillClearsTheScriptsTargets()
    {
        Rig rig = MakeRig();
        ChangeState(rig, StateB);
        int handle = SetReachedTarget(rig);
        int rotHandle = SetReachedRotTarget(rig);
        Assert.True(rig.Group.AtTargets.ContainsKey(handle));
        Assert.True(rig.Group.RotTargets.ContainsKey(rotHandle));

        TaskInventoryItem item = new()
        {
            ItemID = rig.ItemID,
            AssetID = UUID.Random(),
            Name = "target script",
            Type = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            OwnerID = rig.Part.OwnerID,
            CreatorID = rig.Part.OwnerID
        };
        rig.Part.Inventory.AddInventoryItem(item, false);

        rig.Scene.DeleteSceneObject(rig.Group, false);

        Assert.False(rig.Group.AtTargets.ContainsKey(handle));
        Assert.False(rig.Group.RotTargets.ContainsKey(rotHandle));
    }

    [Fact]
    public void AStateChangeStillReleasesTheRepeatingSensorsOfTheOldState()
    {
        Rig rig = MakeRig();
        UUID script = rig.ItemID;
        rig.Acm.SensorRepeatPlugin.SetSenseRepeatEvent(rig.Part.LocalId, script, "", UUID.Zero, 0, 10.0, 3.14, 5.0, rig.Part);
        Assert.NotEmpty(rig.Acm.SensorRepeatPlugin.GetSerializationData(script));

        ChangeState(rig, StateB);

        Assert.Empty(rig.Acm.SensorRepeatPlugin.GetSerializationData(script));
    }

    [Fact]
    public void AStateChangeStillReleasesTheListensOfTheOldState()
    {
        Rig rig = MakeRig();
        WorldCommModule comms = new();
        comms.Initialise(new IniConfigSource());
        comms.AddRegion(rig.Scene);
        comms.Listen(rig.ItemID, rig.Part.UUID, 7, "", UUID.Zero, "");
        Assert.NotEmpty(comms.GetSerializationData(rig.ItemID));

        ChangeState(rig, StateB);

        Assert.Empty(comms.GetSerializationData(rig.ItemID));
    }

    [Fact]
    public void AStateChangeToAStateWithNoTargetHandlersStillDropsTheTargetsThroughTheEventFlags()
    {
        // Unchanged behaviour, pinned so the fix is not mistaken for more than it is: when the object's scripts
        // between them have no at_target / not_at_target handler left, the object drops its position targets.
        Rig rig = MakeRig();
        int handle = SetReachedTarget(rig);

        ChangeState(rig, StateWithoutTargetHandlers);

        Assert.False(rig.Group.AtTargets.ContainsKey(handle));
    }
}
