/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// An object crossing to another region is sent with how old its position and velocity are, so that the engine of the
/// region that takes it in can move it only as far as the time that has passed (PhysicsActor.CrossedIn). The age is
/// measured by the sending region on its own clock and handed over as a duration: in one process through the local
/// connector, between simulators as the "sample_age_ms" field of the object message. No clock of one simulator is ever
/// compared with the clock of another. A message without the field, or with a value that is not a sane age, is taken
/// as before: no age.
/// </summary>
public class CrossingSampleAgeTests : OpenSimTestCase
{
    public CrossingSampleAgeTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    // ---- one process ------------------------------------------------------------------------------------------------

    private sealed class RecordingPrim : BasicPhysicsPrim
    {
        public readonly List<float> CrossedInAges = new();

        public RecordingPrim(string name, uint id, Vector3 pos, Vector3 size, Quaternion rot, PrimitiveBaseShape shape)
            : base(name, id, pos, size, rot, shape) { }

        public override void CrossedIn(float sampleAgeSeconds) => CrossedInAges.Add(sampleAgeSeconds);
    }

    private sealed class RecordingEngine : BasicScene
    {
        public readonly List<RecordingPrim> Prims = new();

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position, Vector3 size,
            Quaternion rotation, bool isPhysical, uint localid)
        {
            RecordingPrim prim = new(primName, localid, position, size, rotation, pbs) { IsPhysical = isPhysical };
            Prims.Add(prim);
            return prim;
        }
    }

    [Fact]
    public void A_physical_object_crossing_in_one_process_hands_its_sample_age_to_the_engine_of_the_new_region()
    {
        TestHelpers.InMethod();

        EntityTransferModule etmA = new(), etmB = new();
        LocalSimulationConnectorModule lscm = new();
        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        SceneHelpers sh = new();
        TestScene sceneA = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x7100), 7000, 7000);
        TestScene sceneB = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x7200), 7001, 7000);
        SceneHelpers.SetupSceneModules(new Scene[] { sceneA, sceneB }, config, lscm);
        SceneHelpers.SetupSceneModules(sceneA, config, etmA);
        SceneHelpers.SetupSceneModules(sceneB, config, etmB);

        RecordingEngine engineB = new();
        sceneB.PhysicsScene = engineB;
        sceneB.CrossingClock = () => 1000;   // the receiving region's own clock: its handling time is then 0

        // the sending region's clock: every read is 7 ms later than the one before
        List<double> reads = new();
        double clock = 50000;
        sceneA.CrossingClock = () => { clock += 7; reads.Add(clock); return clock; };

        SceneObjectGroup so = SceneHelpers.AddSceneObject(sceneA, 1, TestHelpers.ParseTail(0x71), "", 0x7300);
        so.AbsolutePosition = new Vector3(250, 128, 30);
        so.ScriptSetPhysicsStatus(true);
        Assert.NotNull(so.RootPart.PhysActor);

        so.AbsolutePosition = new Vector3(257, 128, 30);   // past the east border

        SceneObjectGroup arrived = sceneB.GetSceneObjectGroup(so.UUID);
        Assert.NotNull(arrived);
        RecordingPrim actor = Assert.IsType<RecordingPrim>(arrived.RootPart.PhysActor);
        float age = Assert.Single(actor.CrossedInAges);
        Assert.True(reads.Count >= 2, $"the sending region read its clock {reads.Count} times");
        Assert.Equal((reads[^1] - reads[0]) / 1000.0, age, 6);
        Assert.True(double.IsNaN(arrived.CrossingSampleAgeMs), "the age is used once, on arrival");
    }

    [Fact]
    public void An_object_that_stays_after_a_crossing_with_nowhere_to_go_keeps_no_sample()
    {
        TestHelpers.InMethod();

        EntityTransferModule etm = new();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("EntityTransferModule", etm.Name);
        TestScene scene = new SceneHelpers().SetupScene("sceneS", TestHelpers.ParseTail(0x7a00), 7400, 7400);
        SceneHelpers.SetupSceneModules(scene, config, etm);

        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(0x75), "", 0x7b00);
        so.AbsolutePosition = new Vector3(250, 128, 30);
        so.AbsolutePosition = new Vector3(257, 128, 30);   // past the east border, where no region is

        Assert.Same(so, scene.GetSceneObjectGroup(so.UUID));
        Assert.False(so.inTransit);
        Assert.True(double.IsNaN(so.CrossingSampleAgeMs), $"the object that stayed still has a sample {so.CrossingSampleAgeMs} ms old");
    }

    // ---- between simulators ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_object_message_carries_the_age_of_the_sample_as_a_duration()
    {
        TestHelpers.InMethod();
        TestScene scene = new SceneHelpers().SetupScene("sceneW", TestHelpers.ParseTail(0x7400), 7100, 7100);
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(0x72), "", 0x7500);
        so.CrossingSampleAgeMs = 41.5;

        var client = new RemoteHeldCrossingTests.RecordingClient();
        GridRegion destination = new() { RegionID = UUID.Random(), RegionName = "Example", ServerURI = "http://example.org:9000/" };
        Assert.True(client.CreateObject(destination, new Vector3(0.5f, 128, 30), so, false));

        OSDMap sent = Assert.Single(client.Posts);
        Assert.True(sent.ContainsKey("sample_age_ms"), "no sample_age_ms in the object message");
        Assert.Equal(41.5, sent["sample_age_ms"].AsReal(), 6);
    }

    [Fact]
    public void An_object_without_an_age_is_sent_exactly_as_before()
    {
        TestHelpers.InMethod();
        TestScene scene = new SceneHelpers().SetupScene("sceneX", TestHelpers.ParseTail(0x7600), 7200, 7200);
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(0x73), "", 0x7700);
        Assert.True(double.IsNaN(so.CrossingSampleAgeMs));

        var client = new RemoteHeldCrossingTests.RecordingClient();
        GridRegion destination = new() { RegionID = UUID.Random(), RegionName = "Example", ServerURI = "http://example.org:9000/" };
        Assert.True(client.CreateObject(destination, new Vector3(0.5f, 128, 30), so, false));

        Assert.False(Assert.Single(client.Posts).ContainsKey("sample_age_ms"));
    }

    public class CapturingService : DispatchProxy
    {
        public IScene Scene;
        public ISceneObject Received;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(ISimulationService.GetScene))
                return Scene;
            if (method.Name == nameof(ISimulationService.CreateObject))
            {
                Received = (ISceneObject)args[2];
                return true;
            }
            Type t = method.ReturnType;
            return t.IsValueType && t != typeof(void) ? Activator.CreateInstance(t) : null;
        }
    }

    /// <summary>The object handler with its clock in the test's hands: every read is 3 ms after the one before.</summary>
    public sealed class ClockedHandler : OpenSim.Server.Handlers.Simulation.ObjectSimpleHandler
    {
        public double Clock = 900;

        public ClockedHandler(ISimulationService service)
            : base(service, new OpenSim.Server.Handlers.Base.ControlPlaneAccess(new IniConfigSource()))
        {
        }

        protected override double NowMs() => Clock += 3;

        public void Post(OSDMap args, OpenSim.Framework.Servers.HttpServer.IOSHttpResponse response) => DoObjectPost(args, response);
    }

    private static ISceneObject Receive(OSD age)
    {
        TestScene scene = new SceneHelpers().SetupScene("sceneR", TestHelpers.ParseTail(0x7800), 7300, 7300);
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, TestHelpers.ParseTail(0x74), "", 0x7900);

        ISimulationService service = DispatchProxy.Create<ISimulationService, CapturingService>();
        CapturingService capture = (CapturingService)(object)service;
        capture.Scene = scene;
        var handler = new ClockedHandler(service);

        OSDMap args = new()
        {
            ["sog"] = OSD.FromString(so.ToXml2()),
            ["extra"] = OSD.FromString(so.ExtraToXmlString()),
            ["new_position"] = OSD.FromString(new Vector3(0.5f, 128, 30).ToString()),
            ["destination_uuid"] = OSD.FromString(scene.RegionInfo.RegionID.ToString())
        };
        if (age is not null)
            args["sample_age_ms"] = age;

        TestOSHttpResponse response = new();
        handler.Post(args, response);
        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(capture.Received);
        return capture.Received;
    }

    [Fact]
    public void The_receiving_region_takes_the_age_and_adds_only_its_own_handling_time()
    {
        TestHelpers.InMethod();
        // received at the handler's first clock read, handed on at its second: 3 ms of handling here
        double age = Receive(OSD.FromReal(41.5)).CrossingSampleAgeMs;
        Assert.Equal(41.5 + 3, age, 6);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(1e9)]
    [InlineData(double.NaN)]
    public void An_age_that_is_not_sane_is_ignored(double value)
    {
        TestHelpers.InMethod();
        Assert.True(double.IsNaN(Receive(OSD.FromReal(value)).CrossingSampleAgeMs));
    }

    [Fact]
    public void A_message_from_an_older_region_has_no_age()
    {
        TestHelpers.InMethod();
        Assert.True(double.IsNaN(Receive(null).CrossingSampleAgeMs));
    }
}
