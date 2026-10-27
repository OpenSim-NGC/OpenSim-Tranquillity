/*
 * Phlox Script Engine tests
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using InWorldz.Phlox.VM;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using Phlox.ScriptEngine;
using Phlox.ScriptEngine.AsyncCommand;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Seams A and B: LSLSystemAPI takes its engine as IScriptHostServices and its script as IScriptRuntimeAccess, so an
/// engine that is not PhloxEngine, and a script that is not an Interpreter, can host it. These tests build the API on
/// stand-ins for both and check that the settings it reads and the requests it makes go to them, and that
/// PhloxEngine, its listen manager and its OSSL gate are the real services behind the interfaces.
/// </summary>
// Runs in parallel: each test builds its own stand-ins; the engine tests use a harness of their own.
public class LslSystemApiHostSeamsTests
{
    private sealed class FakeListens : IScriptListenService
    {
        public readonly List<(uint Local, UUID Item, int Channel, string Name, string Msg, int Regex)> Added = new();
        public readonly List<(UUID Item, int Handle, bool Active)> Controlled = new();
        public readonly List<(UUID Item, int Handle)> Removed = new();
        public int NextHandle = 7;

        public int Add(uint localID, UUID itemID, UUID hostID, int channel, string name, UUID key, string msg, int regexBitfield, ActiveListen saved)
        {
            Added.Add((localID, itemID, channel, name, msg, regexBitfield));
            return NextHandle;
        }
        public void SetActive(UUID itemID, int handle, bool active) => Controlled.Add((itemID, handle, active));
        public void Remove(UUID itemID, int handle) => Removed.Add((itemID, handle));
        public void Remove(UUID itemID) => Removed.Add((itemID, 0));
        public void Forget(UUID itemID) { }
        public void DeliverChat(ChatTypeEnum type, int channel, string speakerName, UUID speakerKey, string message, Vector3 speakerPosition, UUID destId) { }
    }

    private sealed class FakeHost : IScriptHostServices
    {
        public float MinTimer = 0f;
        public FakeListens Listens;
        public readonly List<(uint Local, UUID Item, float Sec)> Timers = new();
        public readonly List<(UUID Item, float Seconds)> MinEventDelays = new();
        public readonly List<UUID> ApiResets = new();

        public string Name => "Fake";
        public Scene World => null;
        public IConfigSource ConfigSource => null;
        public IScriptModule ScriptModule => null;
        public IScriptEngine ScriptEngine => null;
        public ServiceCallDeferralMode ServiceCallDeferral => ServiceCallDeferralMode.Auto;
        public bool AllowGodFunctions => false;
        public bool AutomaticLinkPermission => false;
        public float MinTimerInterval => MinTimer;
        public bool ResetThrottle => false;
        public bool ChatThrottle => false;
        public bool BotThrottle => false;
        public bool PhysicsThrottle => false;
        public bool LinkMessageThrottle => false;
        public bool NotecardThrottle => false;
        public bool FormatStringThrottle => false;
        public bool HttpInFlightThrottle => false;
        public bool NotecardHalcyonTiming => false;
        public bool NotecardCacheEnabled => false;
        public PhloxNotecardCache NotecardCache => null;
        public int PhysicsFrameTimeAvg => 0;
        public IScriptOsslChecks Ossl => null;
        public IScriptListenService ListenManager => Listens;
        public IScriptStateTracker StateManager => null;
        public AsyncCommandManager AsyncCommands => null;

        public void SysReturn(UUID itemId, object retValue, int delay) { }
        public void SysReturnSequenced(UUID itemId, object retValue, int delay, int seq) { }
        public bool PostScriptEvent(UUID itemID, EventParams parms) => true;
        public bool PostObjectEvent(uint localID, EventParams parms) => true;
        public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value) => true;
        public int PostDataserverToPrim(SceneObjectPart part, UUID skip, UUID asker, string queryId, string data) => 0;
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public void SetTimerEvent(uint localID, UUID itemID, float sec) => Timers.Add((localID, itemID, sec));
        public void SetMinEventDelay(UUID itemID, float seconds) => MinEventDelays.Add((itemID, seconds));
        public void SetItemRunningFlag(uint localId, UUID itemId, bool running) { }
        public void ResetScript(UUID itemID) { }
        public void ApiResetScript(UUID itemID) => ApiResets.Add(itemID);
        public void RequestParcelCheck(UUID itemId) { }
        public void SendToWorldComm(Action<IWorldComm> send) { }
        public float GetEventQueueFreeSpacePercentage(UUID itemID) => 100f;
        public void ExperienceGrantRestored() { }
        public void ExperienceCannotRun(UUID experience, int code) { }
    }

    private sealed class FakeScript : IScriptRuntimeAccess
    {
        public RuntimeState State { get; } = new RuntimeState();
        public int[] Handled = Array.Empty<int>();
        public int AskedState = -1;

        public RuntimeState ScriptState => State;
        public IEnumerable<int> EventTypesHandledIn(int lslState) { AskedState = lslState; return Handled; }
    }

    // ── the settings the API reads and the requests it makes go to the host ───────────────────────

    [Fact]
    public void TheEventDelayAScriptAsksForReachesTheHost()
    {
        var host = new FakeHost();
        UUID item = UUID.Random();
        var api = new LSLSystemAPI(host, null, 5, item);

        api.llMinEventDelay(0.25f);

        Assert.Equal(new[] { (item, 0.25f) }, host.MinEventDelays);
    }

    [Fact]
    public void ATimerShorterThanTheHostsFloorIsSetAtTheFloor()
    {
        var host = new FakeHost { MinTimer = 0.5f };
        UUID item = UUID.Random();
        var api = new LSLSystemAPI(host, null, 9, item);

        api.llSetTimerEvent(0.1f);
        api.llSetTimerEvent(2f);

        Assert.Equal(new[] { ((uint)9, item, 0.5f), ((uint)9, item, 2f) }, host.Timers);
    }

    [Fact]
    public void AScriptResettingItselfIsResetByTheHost()
    {
        var host = new FakeHost();
        UUID item = UUID.Random();
        var api = new LSLSystemAPI(host, null, 1, item);

        api.llResetScript();

        Assert.Equal(new[] { item }, host.ApiResets);
    }

    [Fact]
    public void AListenIsAddedToTheHostsListenService()
    {
        using var h = new SchedulerHarness();
        var host = new FakeHost { Listens = new FakeListens() };
        UUID item = UUID.Random();
        var api = new LSLSystemAPI(host, h.Prim, h.Prim.LocalId, item);

        int handle = api.llListen(42, "name", UUID.Zero.ToString(), "hello");
        api.llListenControl(handle, 0);
        api.llListenRemove(handle);

        Assert.Equal(7, handle);
        Assert.Equal(new[] { (h.Prim.LocalId, item, 42, "name", "hello", 0) }, host.Listens.Added);
        Assert.Equal(new[] { (item, 7, false) }, host.Listens.Controlled);
        Assert.Equal(new[] { (item, 7) }, host.Listens.Removed);
    }

    // ── the script the API serves is reached through IScriptRuntimeAccess ───────────────────────

    [Fact]
    public void TheScriptsRunTimeIsReadAndResetThroughItsAccessObject()
    {
        var api = new LSLSystemAPI(new FakeHost(), null, 1, UUID.Random());
        var script = new FakeScript();
        script.State.OtherRuntime = 100f;
        api.RuntimeAccess = script;

        Assert.True(api.llGetTime() >= 100f);
        api.llResetTime();

        Assert.Equal(0f, script.State.OtherRuntime);
        Assert.True(api.llGetTime() < 5f);
    }

    [Fact]
    public void TheEventsAScriptHandlesInItsStateSetTheEventMaskOfItsPrim()
    {
        using var h = new SchedulerHarness();
        UUID item = UUID.Random();
        var api = new LSLSystemAPI(new FakeHost(), h.Prim, h.Prim.LocalId, item);
        var script = new FakeScript { Handled = new[] { (int)InWorldz.Phlox.Types.SupportedEventList.Events.TIMER } };
        script.State.GeneralEnable = true;
        script.State.LSLState = 3;
        api.RuntimeAccess = script;

        api.SetScriptEventFlags();

        Assert.Equal(3, script.AskedState);
        Assert.True(h.Prim.ScriptEvents.HasFlag(scriptEvents.timer));
    }

    [Fact]
    public void AScriptThatIsNotAnInterpreterHasNoInterpreterOnTheApi()
    {
        var api = new LSLSystemAPI(new FakeHost(), null, 1, UUID.Random());
        api.RuntimeAccess = new FakeScript();

        Assert.Null(api.Script);
        Assert.IsType<FakeScript>(api.RuntimeAccess);
    }

    // ── Phlox: the services behind the interfaces are the ones it already had ───────────────────

    [Fact]
    public void PhloxEngineIsTheHostAndItsServicesAreItsOwn()
    {
        using var h = new SchedulerHarness();
        IScriptHostServices host = h.Engine;

        Assert.Same(h.Engine.World, host.World);
        Assert.Same(h.Engine.ConfigSource, host.ConfigSource);
        Assert.Same(h.Engine.ScriptModule, host.ScriptModule);
        Assert.Equal(h.Engine.ChatThrottle, host.ChatThrottle);
        Assert.Equal(h.Engine.MinTimerInterval, host.MinTimerInterval);
        Assert.Same(h.Engine.Ossl, host.Ossl);
        Assert.Same(h.Engine.ListenManager, host.ListenManager);
        Assert.Same(h.Engine.AsyncCommands, host.AsyncCommands);
        Assert.Same(h.Engine.NotecardCache, host.NotecardCache);
        Assert.IsAssignableFrom<IScriptListenService>(h.Engine.ListenManager);
        Assert.IsAssignableFrom<IScriptOsslChecks>(h.Engine.Ossl);
    }

    [Fact]
    public void AnApiBuiltOnTheEngineStillUsesTheEnginesOwnListenManager()
    {
        using var h = new SchedulerHarness();
        var api = new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());

        int handle = api.llListen(77, "", UUID.Zero.ToString(), "");

        Assert.True(handle > 0);
        api.llListenRemove(handle);
    }
}
