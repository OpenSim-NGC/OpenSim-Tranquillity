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
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A scripted car with a seated driver crosses into the next region, each region running a real script engine (Phlox
/// or YEngine), through the held crossing the core uses for an object that carries avatars. Whatever happens in
/// the second region, the car ends in exactly one region, and the driver ends seated on it, in the same region.
/// </summary>
// In the "phlox-state" collection: the tests swap the process's logger factory (LoggerProvider.LoggerFactory) to read
// the errors the crossing logs, and a stack of regions needs the engine clock alone.
[Collection("phlox-state")]
public class SeatedVehicleCrossingTests
{
    // A typical legacy car: it takes its vehicle type and parameters at once, asks its driver for the controls when one
    // sits, and drives on the keys.
    private const string Car = @"
        default {
            state_entry() {
                llSetVehicleType(VEHICLE_TYPE_CAR);
                llSetVehicleFloatParam(VEHICLE_ANGULAR_FRICTION_TIMESCALE, 2.0);
                llSetVehicleFloatParam(VEHICLE_LINEAR_MOTOR_TIMESCALE, 1.0);
                llSetVehicleFloatParam(VEHICLE_LINEAR_FRICTION_TIMESCALE, 3.0);
                llSetVehicleVectorParam(VEHICLE_LINEAR_FRICTION_TIMESCALE, <1000.0, 2.0, 1000.0>);
                llSetVehicleFloatParam(VEHICLE_VERTICAL_ATTRACTION_EFFICIENCY, 0.5);
                llSetVehicleFloatParam(VEHICLE_BANKING_EFFICIENCY, 1.0);
                llSetSitText(""Drive"");
                llSay(0, ""car ready"");
            }
            changed(integer c) {
                key a = llAvatarOnSitTarget();
                if ((c & CHANGED_LINK) && a != NULL_KEY)
                    llRequestPermissions(a, PERMISSION_TAKE_CONTROLS);
            }
            run_time_permissions(integer p) {
                if (p & PERMISSION_TAKE_CONTROLS)
                    llTakeControls(CONTROL_FWD | CONTROL_BACK | CONTROL_RIGHT | CONTROL_LEFT, TRUE, FALSE);
            }
            control(key id, integer level, integer edge) {
                if (level & CONTROL_FWD)
                    llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, <20.0, 0.0, 0.0>);
            }
        }";

    // coordinates no other test class uses
    private const uint BaseX = 7600;

    private static readonly Vector3 Start = new(128, 10, 30);
    private static readonly Vector3 Target = new(128, -0.05f, 30);

    /// <summary>Everything a crossing logs at Warning and above, with the exception text.</summary>
    internal sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        public readonly List<string> Lines = new();
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public string Text() { lock (Lines) return string.Join("\n", Lines); }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (level >= LogLevel.Warning)
                    lock (m_c.Lines) m_c.Lines.Add($"{level}: {formatter(state, exception)}" + (exception is null ? "" : "\n" + exception));
            }
        }
    }

    internal sealed class Rig : IDisposable
    {
        public TestScene A, B;
        public readonly List<IDisposable> Disposers = new();
        public readonly List<string> Said = new();
        public string EngineName;

        public bool Heard(string s) { lock (Said) return Said.Contains(s); }
        public string Text() { lock (Said) return "[" + string.Join(" | ", Said) + "]"; }

        public void Dispose()
        {
            foreach (IDisposable d in Disposers) { try { d.Dispose(); } catch { } }
        }
    }

    private sealed class Stop : IDisposable
    {
        private readonly Action m_a;
        public Stop(Action a) => m_a = a;
        public void Dispose() => m_a();
    }

    private static void StartEngine(Rig rig, TestScene scene, string kind, string dir)
    {
        IniConfigSource config = new();
        config.AddConfig("Startup").Set("DefaultScriptEngine", kind == "YEngine" ? "YEngine" : "InWorldz.Phlox");
        scene.EventManager.OnChatFromWorld += (_, chat) => { lock (rig.Said) rig.Said.Add(chat.Message ?? string.Empty); };

        if (kind == "YEngine")
        {
            var worldComm = new OpenSim.Region.CoreModules.Scripting.WorldComm.WorldCommModule();
            worldComm.Initialise(config);
            worldComm.AddRegion(scene);

            IConfig y = config.AddConfig("YEngine");
            y.Set("Enabled", "true");
            y.Set("ScriptEnginesPath", System.IO.Path.Combine(dir, "yengine-" + Guid.NewGuid().ToString("N")[..8]));
            var engine = new OpenSim.Region.ScriptEngine.Yengine.Yengine();
            engine.Initialise(config);
            engine.AddRegion(scene);
            engine.RegionLoaded(scene);
            engine.StartProcessing();
            rig.Disposers.Add(new Stop(() => { engine.RemoveRegion(scene); engine.Close(); }));
            rig.EngineName = "YEngine";
        }
        else
        {
            config.AddConfig("InWorldz.Phlox").Set("Enabled", "true");
            var engine = new PhloxEngine { BytecodeCacheDir = dir };
            engine.Initialise(config);
            engine.AddRegion(scene);
            if (scene.RequestModuleInterface<IWorldComm>() is null)
                scene.RegisterModuleInterface<IWorldComm>(NullWorldComm.Create());
            engine.RegionLoaded(scene);
            rig.Disposers.Add(new Stop(() => { engine.RemoveRegion(scene); engine.Close(); }));
            rig.EngineName = "InWorldz.Phlox";
        }

        typeof(Scene).GetField("m_defaultScriptEngine", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(scene, rig.EngineName);
    }

    private static Rig TwoRegions(string kind, uint x)
    {
        var etmA = new EntityTransferModule();
        var etmB = new EntityTransferModule();
        var connector = new LocalSimulationConnectorModule();
        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", connector.Name);
        config.AddConfig("EntityTransfer").Set("wait_for_callback", false);

        var sh = new SceneHelpers();
        var rig = new Rig
        {
            A = sh.SetupScene("Example Region A", TestHelpers.ParseTail(0x100), x, x),
            B = sh.SetupScene("Example Region B", TestHelpers.ParseTail(0x200), x, x - 1)
        };
        rig.A.EnableStandingRiderCrossing = true;
        rig.B.EnableStandingRiderCrossing = true;
        SceneHelpers.SetupSceneModules(new Scene[] { rig.A, rig.B }, config, connector);
        SceneHelpers.SetupSceneModules(rig.A, config, new CapabilitiesModule(), etmA);
        SceneHelpers.SetupSceneModules(rig.B, config, new CapabilitiesModule(), etmB);

        string dir = (string)typeof(SchedulerHarness).GetMethod("BytecodeDirForCaller", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        StartEngine(rig, rig.A, kind, dir);
        StartEngine(rig, rig.B, kind, dir);
        return rig;
    }

    internal sealed class Run
    {
        public Rig Rig;
        public SceneObjectGroup Car;
        public ScenePresence Driver;
        public Capture Log;
    }

    internal static Run Setup(string kind, uint x, Capture log)
    {
        Rig rig = TwoRegions(kind, x);
        UUID owner = TestHelpers.ParseTail(0x1);

        SceneObjectGroup car = SceneHelpers.AddSceneObject(rig.A, 3, owner, "", 0x2);
        car.RootPart.Scale = new Vector3(4f, 2f, 1f);
        car.AbsolutePosition = Start;
        UUID item = UUID.Random();
        TaskInventoryHelpers.AddScript(rig.A.AssetService, car.RootPart, item, UUID.Random(), "car", Car);
        Assert.Equal(1, car.CreateScriptInstances(0, true, rig.EngineName, 1));
        car.ResumeScripts();
        Assert.True(Wait.For(() => rig.Heard("car ready")), "the car did not start: " + rig.Text() + "\n" + log.Text());

        UUID userId = TestHelpers.ParseTail(0x11);
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(userId);
        acd.circuitcode = 1011;
        TestClient tc = new(acd, rig.A);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, new List<TestClient>());
        ScenePresence driver = SceneHelpers.AddScenePresence(rig.A, tc, acd);
        driver.AbsolutePosition = Start;
        driver.HandleAgentRequestSit(driver.ControllingClient, driver.UUID, car.RootPart.UUID, Vector3.Zero);
        Assert.True(driver.IsSatOnObject);

        return new Run { Rig = rig, Car = car, Driver = driver, Log = log };
    }

    /// <summary>The car's origin leaves the region by a tad, as a car does that drives over the edge.</summary>
    internal static void DriveOverTheEdge(Run run)
    {
        run.Rig.A.SceneGraph.UpdatePrimGroupPosition(run.Car.LocalId, Target, run.Driver.ControllingClient);
    }

    private static int Copies(Run run)
        => (run.Rig.A.GetSceneObjectGroup(run.Car.UUID) is null ? 0 : 1) + (run.Rig.B.GetSceneObjectGroup(run.Car.UUID) is null ? 0 : 1);

    [Theory]
    [InlineData("Phlox", "", 0u)]
    [InlineData("YEngine", "", 10u)]
    [InlineData("Phlox", "script-start", 20u)]
    [InlineData("YEngine", "script-start", 30u)]
    public void ASeatedScriptedCarCrossesAndItsDriverStaysSeated(string kind, string fault, uint offset)
    {
        var log = new Capture();
        ILoggerFactory previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = log;
        try
        {
            Run run = Setup(kind, BaseX + offset, log);
            using Rig rig = run.Rig;
            bool thrown = false;
            if (fault.Length > 0)
                rig.B.CrossingStepHook = (_, label) =>
                {
                    if (label == fault && !thrown)
                    {
                        thrown = true;
                        throw new InvalidOperationException("injected failure at " + label);
                    }
                };

            DriveOverTheEdge(run);

            Assert.True(Wait.For(() => rig.B.GetSceneObjectGroup(run.Car.UUID) is not null), "the car did not reach region B\n" + log.Text());
            Assert.True(Wait.For(() => rig.B.GetScenePresence(run.Driver.UUID) is not null), "the driver did not reach region B\n" + log.Text());
            if (fault.Length > 0)
                Assert.True(thrown, "the step was never reached");

            ScenePresence there = rig.B.GetScenePresence(run.Driver.UUID);
            ((TestClient)there.ControllingClient).CompleteMovement();

            SceneObjectGroup carThere = rig.B.GetSceneObjectGroup(run.Car.UUID);
            Assert.True(Wait.For(() => rig.A.GetSceneObjectGroup(run.Car.UUID) is null), "region A still holds the car\n" + log.Text());
            Assert.Equal(1, Copies(run));
            Assert.True(there.IsSatOnObject, "the driver is not seated\n" + log.Text());
            Assert.Equal(carThere, there.ParentPart?.ParentGroup);
            Assert.DoesNotContain("Crossing of", log.Text());
            Assert.DoesNotContain("Can't find prim", log.Text());
            foreach (SceneObjectPart part in carThere.Parts)
                Assert.True(part.GroupPosition.Z > 0, "a part fell out of the world");
        }
        finally
        {
            LoggerProvider.LoggerFactory = previous;
        }
    }

    /// <summary>The destination fails after taking the car in: the car and its driver stay in the first region.</summary>
    [Theory]
    [InlineData("Phlox", 40u)]
    [InlineData("YEngine", 50u)]
    public void ACarTheDestinationFailedToTakeInStaysWithItsDriverInTheFirstRegion(string kind, uint offset)
    {
        var log = new Capture();
        ILoggerFactory previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = log;
        try
        {
            Run run = Setup(kind, BaseX + offset, log);
            using Rig rig = run.Rig;
            bool thrown = false;
            rig.B.CrossingStepHook = (_, label) =>
            {
                if (label == "destination-add" && !thrown)
                {
                    thrown = true;
                    throw new InvalidOperationException("injected failure at " + label);
                }
            };

            DriveOverTheEdge(run);

            Assert.True(Wait.For(() => thrown), "the step was never reached\n" + log.Text());
            Assert.True(Wait.For(() => !run.Car.inTransit && !rig.A.GetScenePresence(run.Driver.UUID).IsInTransit), "the crossing did not end\n" + log.Text());

            Assert.Null(rig.B.GetSceneObjectGroup(run.Car.UUID));
            Assert.Equal(1, Copies(run));
            Assert.Equal(run.Car, rig.A.GetSceneObjectGroup(run.Car.UUID));
            ScenePresence here = rig.A.GetScenePresence(run.Driver.UUID);
            Assert.True(here.IsSatOnObject, "the driver is not seated\n" + log.Text());
            Assert.Equal(run.Car, here.ParentPart?.ParentGroup);
            Assert.Contains("Taking in", log.Text());   // the failed step is in the log, with its exception
            Assert.Contains("injected failure at destination-add", log.Text());
            Assert.DoesNotContain("Can't find prim", log.Text());
            foreach (SceneObjectPart part in run.Car.Parts)
                Assert.True(part.GroupPosition.Z > 0, "a part fell out of the world");
        }
        finally
        {
            LoggerProvider.LoggerFactory = previous;
        }
    }
}

internal static class Wait
{
    public static bool For(Func<bool> done, double seconds = 30)
        => VehicleCrossingStateTests.WaitFor(done, seconds);
}
