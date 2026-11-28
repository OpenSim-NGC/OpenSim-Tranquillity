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
using OpenSim.Region.Framework.Interfaces;
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

    // ---- implementers outside this tree -----------------------------------------------------------------------------

    // Written as an implementer that does not know the crossing members: it has to compile without them
    private sealed class ObjectWithoutCrossingMembers : ISceneObject
    {
        public string Name => "Test Object";
        public UUID UUID => UUID.Zero;
        public UUID OwnerID { get; set; }
        public ISceneObject CloneForNewScene() => this;
        public string ToXml2() => string.Empty;
        public string ExtraToXmlString() => string.Empty;
        public void ExtraFromXmlString(string xmlstr) { }
        public string GetStateSnapshot() => string.Empty;
        public void SetState(string xmlstr, IScene s) { }
        public bool HasGroupChanged { get; set; }
    }

    [Fact]
    public void An_implementer_without_the_crossing_members_has_no_sample_no_cut_and_no_avatars_and_ignores_sets()
    {
        TestHelpers.InMethod();
        ISceneObject so = new ObjectWithoutCrossingMembers();

        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(3.25f, 0f, 0f);
        so.CrossingCarriesAvatars = true;

        Assert.True(double.IsNaN(so.CrossingSampleAgeMs));
        Assert.Equal(Vector3.Zero, so.CrossingCutOffset);
        Assert.False(so.CrossingCarriesAvatars);
    }

    // ---- one process ------------------------------------------------------------------------------------------------

    private sealed class RecordingPrim : BasicPhysicsPrim
    {
        public readonly List<float> CrossedInAges = new();
        public readonly List<Vector3> CutsAdded = new();
        public Vector3 Cut;

        public override Vector3 CrossingCutOffset => Cut;

        public RecordingPrim(string name, uint id, Vector3 pos, Vector3 size, Quaternion rot, PrimitiveBaseShape shape)
            : base(name, id, pos, size, rot, shape) { }

        public override void CrossedIn(float sampleAgeSeconds, Vector3 cutAdded = default)
        {
            CrossedInAges.Add(sampleAgeSeconds);
            CutsAdded.Add(cutAdded);
        }
    }

    private sealed class RecordingEngine : BasicScene
    {
        public readonly List<RecordingPrim> Prims = new();
        public Vector3 Cut;
        public bool Applies = true;

        public override bool AppliesCrossingSampleAge => Applies;

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position, Vector3 size,
            Quaternion rotation, bool isPhysical, uint localid)
        {
            RecordingPrim prim = new(primName, localid, position, size, rotation, pbs) { IsPhysical = isPhysical, Cut = Cut };
            Prims.Add(prim);
            return prim;
        }
    }

    [Fact]
    public void A_physical_object_crossing_in_one_process_hands_its_sample_age_to_the_engine_of_the_new_region()
    {
        TestHelpers.InMethod();

        var (arrived, reads) = CrossInOneProcess(Vector3.Zero);

        RecordingPrim actor = Assert.IsType<RecordingPrim>(arrived.RootPart.PhysActor);
        float age = Assert.Single(actor.CrossedInAges);
        Assert.True(reads.Count >= 2, $"the sending region read its clock {reads.Count} times");
        Assert.Equal((reads[^1] - reads[0]) / 1000.0, age, 6);
        Assert.True(double.IsNaN(arrived.CrossingSampleAgeMs), "the age is used once, on arrival");
        Assert.Equal(1f, arrived.AbsolutePosition.X, 3);   // nothing was cut: placed where it was sent to
    }

    [Fact]
    public void An_object_its_engine_cut_back_crossing_in_one_process_is_placed_further_on_by_the_cut()
    {
        TestHelpers.InMethod();

        var (arrived, _) = CrossInOneProcess(new Vector3(3.25f, 0f, 0f));

        Assert.Equal(1f + 3.25f, arrived.AbsolutePosition.X, 3);
        RecordingPrim actor = Assert.IsType<RecordingPrim>(arrived.RootPart.PhysActor);
        Assert.Single(actor.CrossedInAges);
        Assert.Equal(new Vector3(3.25f, 0f, 0f), Assert.Single(actor.CutsAdded));   // the engine is told how far it was moved on
        Assert.Equal(Vector3.Zero, arrived.CrossingCutOffset);   // used once, on arrival
    }

    [Fact]
    public void A_region_whose_engine_does_not_apply_the_age_places_the_object_at_the_sent_position()
    {
        TestHelpers.InMethod();

        var (arrived, _) = CrossInOneProcess(new Vector3(3.25f, 0f, 0f), receiverApplies: false);

        Assert.Equal(1f, arrived.AbsolutePosition.X, 3);
        Assert.Empty(Assert.IsType<RecordingPrim>(arrived.RootPart.PhysActor).CrossedInAges);
        Assert.True(double.IsNaN(arrived.CrossingSampleAgeMs));
        Assert.Equal(Vector3.Zero, arrived.CrossingCutOffset);
    }

    [Fact]
    public void Between_simulators_an_engine_that_does_not_apply_the_age_leaves_the_object_at_the_sent_position()
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e60);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(3.25f, 0f, 0f);

        Assert.Equal(2f, TakeIn(MessageFor(so), engineApplies: false).AbsolutePosition.X, 3);
    }

    // Two parcels in the receiving region, split at x or y = line; the object is sent to 2 m in and cut back by 3.25 m.
    [Theory]
    [InlineData(true, false, 4f, 2f)]       // avatars aboard and the cut would cross the parcel line: the sent position
    [InlineData(true, false, 20f, 5.25f)]   // avatars aboard, both places in one parcel: the cut is added
    [InlineData(false, false, 4f, 5.25f)]   // nobody aboard: the cut is added
    [InlineData(true, true, 4f, 2f)]        // the same along y
    [InlineData(true, true, 20f, 5.25f)]
    public void With_avatars_aboard_the_cut_does_not_move_the_object_into_another_parcel(bool aboard, bool alongY, float line, float expected)
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e70);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = alongY ? new Vector3(0f, 3.25f, 0f) : new Vector3(3.25f, 0f, 0f);
        so.CrossingCarriesAvatars = aboard;
        Vector3 sentTo = alongY ? new Vector3(128f, 2f, 30) : new Vector3(2f, 128, 30);

        SceneObjectGroup arrived = TakeIn(MessageFor(so, sentTo), parcelLine: (alongY, line));

        Assert.Equal(expected, alongY ? arrived.AbsolutePosition.Y : arrived.AbsolutePosition.X, 3);
    }

    [Fact]
    public void The_cut_does_not_move_an_empty_object_into_a_parcel_that_would_refuse_it()
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7eb0);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(3.25f, 0f, 0f);

        // the parcel at the sent position takes the object, the one the cut leads into does not
        SceneObjectGroup arrived = TakeIn(MessageFor(so), parcelLine: (false, 4f), farParcelRefuses: true);

        Assert.Equal(2f, arrived.AbsolutePosition.X, 3);
    }

    [Theory]
    [InlineData(true, 3.25f, true)]
    [InlineData(false, 3.25f, false)]   // nobody aboard
    [InlineData(true, 0f, false)]       // nothing cut: nothing for the avatars to matter to
    public void The_message_says_that_avatars_are_aboard_only_along_with_a_cut(bool aboard, float cut, bool said)
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e80);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(cut, 0f, 0f);
        so.CrossingCarriesAvatars = aboard;

        Assert.Equal(said, MessageFor(so).ContainsKey("sample_avatars_aboard"));
    }

    [Theory]
    [InlineData(0f, 3.25f, 128f, 2f, 128f, 5.25f)]        // along +y
    [InlineData(0f, -3.25f, 128f, 254f, 128f, 250.75f)]   // along -y
    [InlineData(0f, 30f, 128f, 250f, 128f, 255.8f)]       // kept inside along y
    public void A_cut_along_y_is_added_the_same_way(float cutX, float cutY, float sentX, float sentY, float x, float y)
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e90);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(cutX, cutY, 0f);

        Vector3 at = TakeIn(MessageFor(so, new Vector3(sentX, sentY, 30))).AbsolutePosition;

        Assert.Equal(x, at.X, 3);
        Assert.Equal(y, at.Y, 3);
    }

    [Theory]
    [InlineData(1022f, 600f, -3.25f, 0f, 1018.75f, 600f)]   // in from the east: past 256 m, not clamped to a 256 m region
    [InlineData(600f, 1022f, 0f, -3.25f, 600f, 1018.75f)]   // in from the north
    [InlineData(1022f, 1022f, 30f, 30f, 1023.8f, 1023.8f)]  // kept inside the 1024 m region
    public void A_cut_in_a_1024_m_region_is_kept_inside_that_region(float sentX, float sentY, float cutX, float cutY, float x, float y)
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7ea0);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(cutX, cutY, 0f);

        Vector3 at = TakeIn(MessageFor(so, new Vector3(sentX, sentY, 30)), regionSize: 1024).AbsolutePosition;

        Assert.Equal(x, at.X, 3);
        Assert.Equal(y, at.Y, 3);
    }

    // An object of sceneA, whose engine reports the given cut, crosses 1 m past the east border into sceneB.
    private static (SceneObjectGroup arrived, List<double> reads) CrossInOneProcess(Vector3 cut, bool receiverApplies = true)
    {
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

        sceneA.PhysicsScene = new RecordingEngine { Cut = cut };
        RecordingEngine engineB = new() { Applies = receiverApplies };
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
        return (arrived, reads);
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
        public Vector3 Position;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(ISimulationService.GetScene))
                return Scene;
            if (method.Name == nameof(ISimulationService.CreateObject))
            {
                Position = (Vector3)args[1];
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

    private static ISceneObject Receive(OSD age, OSD cut = null)
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
        if (cut is not null)
            args["sample_cut_offset"] = cut;

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

    // ---- the cut-back position ------------------------------------------------------------------------------------------

    private static readonly GridRegion ExampleDestination =
        new() { RegionID = UUID.Random(), RegionName = "Example", ServerURI = "http://example.org:9000/" };

    // The object message a sending region builds for an object, with the position (2, 128, 30) in the destination
    // unless another is given.
    private static OSDMap MessageFor(SceneObjectGroup so, Vector3? sentTo = null)
    {
        var client = new RemoteHeldCrossingTests.RecordingClient();
        Assert.True(client.CreateObject(ExampleDestination, sentTo ?? new Vector3(2f, 128, 30), so, false));
        return Assert.Single(client.Posts);
    }

    private static SceneObjectGroup SendingObject(int tail)
    {
        TestScene scene = new SceneHelpers().SetupScene("sceneC", TestHelpers.ParseTail(tail), 7500, 7500);
        return SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(tail + 1), "", tail + 2);
    }

    // The message goes through the object handler into a real region, which takes the object in, held or not.
    private static SceneObjectGroup TakeIn(OSDMap message, bool held = false, bool engineApplies = true,
        (bool alongY, float at)? parcelLine = null, uint regionSize = 256, bool farParcelRefuses = false)
    {
        EntityTransferModule etm = new();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("EntityTransferModule", etm.Name);
        TestScene scene = new SceneHelpers().SetupScene("sceneD", TestHelpers.ParseTail(0x7c00), 7600, 7600,
            regionSize, regionSize, new IniConfigSource());
        SceneHelpers.SetupSceneModules(scene, config, etm);
        scene.PhysicsScene = new RecordingEngine { Applies = engineApplies };
        if (parcelLine is { } line)
            scene.LandChannel = TwoParcels.Over(scene, line.alongY, line.at);
        if (farParcelRefuses)
            scene.Permissions.OnObjectEntry += (_, _, point) => (parcelLine.Value.alongY ? point.Y : point.X) < parcelLine.Value.at;
        scene.CrossingClock = () => 1000;

        ISimulationService service = DispatchProxy.Create<ISimulationService, CapturingService>();
        CapturingService capture = (CapturingService)(object)service;
        capture.Scene = scene;
        message["destination_uuid"] = OSD.FromString(scene.RegionInfo.RegionID.ToString());
        TestOSHttpResponse response = new();
        // as it goes over the wire: written as JSON and read back
        var wire = (OSDMap)OSDParser.DeserializeJson(OSDParser.SerializeJsonString(message));
        new ClockedHandler(service).Post(wire, response);
        Assert.Equal(200, response.StatusCode);

        var received = Assert.IsType<SceneObjectGroup>(capture.Received);
        if (held)
        {
            var hold = new CrossingHoldRequest
            {
                Source = ExampleDestination,
                ExpectedRiders = new List<UUID> { TestHelpers.ParseTail(0x7d00) }
            };
            Assert.True(scene.IncomingCreateObject(capture.Position, received, hold));
        }
        else
        {
            Assert.True(scene.IncomingCreateObject(capture.Position, received));
        }
        return Assert.IsType<SceneObjectGroup>(scene.GetSceneObjectGroup(received.UUID));
    }

    /// <summary>A land channel with two parcels, split at x or y = a line; everything else is the test land channel's.</summary>
    public class TwoParcels : DispatchProxy
    {
        private ILandChannel m_inner;
        private ILandObject m_near, m_far;
        private bool m_alongY;
        private float m_line;

        public static ILandChannel Over(Scene scene, bool alongY, float line)
        {
            ILandChannel channel = DispatchProxy.Create<ILandChannel, TwoParcels>();
            TwoParcels parcels = (TwoParcels)(object)channel;
            parcels.m_inner = scene.LandChannel;
            parcels.m_near = new OpenSim.Region.CoreModules.World.Land.LandObject(UUID.Zero, false, scene);
            parcels.m_near.LandData.LocalID = 1;
            parcels.m_far = new OpenSim.Region.CoreModules.World.Land.LandObject(UUID.Zero, false, scene);
            parcels.m_far.LandData.LocalID = 2;
            parcels.m_alongY = alongY;
            parcels.m_line = line;
            return channel;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(ILandChannel.GetLandObject) && args.Length == 2)
            {
                float along = Convert.ToSingle(args[m_alongY ? 1 : 0]);
                return along < m_line ? m_near : m_far;
            }
            return method.Invoke(m_inner, args);
        }
    }

    [Fact]
    public void Between_simulators_a_cut_back_object_is_placed_at_the_sent_position_plus_the_cut()
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e00);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(3.25f, 0f, 0f);

        OSDMap message = MessageFor(so);
        Assert.True(message.ContainsKey("sample_cut_offset"), "no sample_cut_offset in the object message");
        SceneObjectGroup arrived = TakeIn(message);

        Assert.Equal(2f + 3.25f, arrived.AbsolutePosition.X, 3);
        Assert.Equal(128f, arrived.AbsolutePosition.Y, 3);
        Assert.Equal(30f, arrived.AbsolutePosition.Z, 3);
    }

    [Fact]
    public void The_cut_and_the_age_are_the_only_additions_to_the_object_message()
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e10);
        OSDMap without = MessageFor(so);   // an object with neither: the message as it was before these fields
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(3.25f, 0f, 0f);

        OSDMap with = MessageFor(so);

        var added = with.Keys.Cast<string>().Except(without.Keys.Cast<string>()).OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(new[] { "sample_age_ms", "sample_cut_offset" }, added);
        Assert.Empty(without.Keys.Cast<string>().Except(with.Keys.Cast<string>()));
        foreach (string key in without.Keys)
            Assert.Equal(OSDParser.SerializeJsonString(without[key]), OSDParser.SerializeJsonString(with[key]));
        Assert.Equal(new Vector3(2f, 128, 30).ToString(), with["new_position"].AsString());
    }

    [Theory]
    [InlineData(41.5, 0f)]           // nothing cut
    [InlineData(double.NaN, 3.25f)]  // no age: the cut means nothing without it
    public void The_cut_is_left_out_of_the_message_when_there_is_none_or_no_age(double age, float cut)
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e20);
        so.CrossingSampleAgeMs = age;
        so.CrossingCutOffset = new Vector3(cut, 0f, 0f);

        Assert.False(MessageFor(so).ContainsKey("sample_cut_offset"));
    }

    [Theory]
    [InlineData(true, false)]    // a sender that knows the age only, or an object with nothing cut
    [InlineData(false, true)]    // a cut without an age
    [InlineData(false, false)]   // an older sender
    public void Without_both_fields_the_object_is_placed_at_the_sent_position(bool withAge, bool withCut)
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e30);
        OSDMap message = MessageFor(so);
        if (withAge)
            message["sample_age_ms"] = OSD.FromReal(41.5);
        if (withCut)
            message["sample_cut_offset"] = OSD.FromVector3(new Vector3(3.25f, 0f, 0f));

        Assert.Equal(2f, TakeIn(message).AbsolutePosition.X, 3);
    }

    [Theory]
    [InlineData(1000f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_cut_that_is_not_sane_is_ignored(float value)
    {
        TestHelpers.InMethod();
        ISceneObject received = Receive(OSD.FromReal(41.5), OSD.FromVector3(new Vector3(value, 0f, 0f)));

        Assert.Equal(Vector3.Zero, received.CrossingCutOffset);
        Assert.Equal(41.5 + 3, received.CrossingSampleAgeMs, 6);
    }

    [Fact]
    public void A_cut_of_the_wrong_type_is_ignored()
    {
        TestHelpers.InMethod();
        Assert.Equal(Vector3.Zero, Receive(OSD.FromReal(41.5), OSD.FromString("<3, 0, 0>")).CrossingCutOffset);
    }

    [Fact]
    public void A_cut_never_places_an_object_outside_the_region_it_arrives_in()
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e40);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(-30f, 0f, 0f);

        Assert.Equal(0.2f, TakeIn(MessageFor(so)).AbsolutePosition.X, 3);
    }

    [Fact]
    public void An_object_held_for_its_riders_is_placed_at_the_sent_position()
    {
        TestHelpers.InMethod();
        SceneObjectGroup so = SendingObject(0x7e50);
        so.CrossingSampleAgeMs = 41.5;
        so.CrossingCutOffset = new Vector3(3.25f, 0f, 0f);

        Assert.Equal(2f, TakeIn(MessageFor(so), held: true).AbsolutePosition.X, 3);
    }
}
