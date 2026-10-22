/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A vehicle set up by script calls, saved the way the simulator saves it, and handed back by each way the simulator
// hands a saved vehicle back to the physics engine (VehicleRestore in the harness lists them). The vehicle must end
// with exactly its saved type, flags, settings and reference frame.
//
// The script calls go through a SceneObjectPart, as a script's llSetVehicle* calls do: the part keeps the record (its
// SOPVehicle) and forwards each call to the actor. The record is read back as each route reads it: from the object's
// XML (rez, crossing), from the database's Vehicle column (region start), or as it is (copy, detach, a phantom prim's
// physics switched off and on). Each of those routes builds a new actor and calls PhysicsActor.SetVehicle with the
// record (SceneObjectPart.AddToPhysics). A solid prim's physics switched off and on keeps its actor and makes no
// vehicle call: the vehicle keeps what it had, as on ubODE, which keeps its vehicle (ODEPrim.m_vehicle) across the
// switch and only stops its motors.
//
// A restored vehicle's motors do not run until a script sets them again, as on ubODE (ODEDynamics.DoSetVehicle stores
// the motor directions with no motor effect). The saved directions are stored as its settings.
//
// Serial with the other native tests: every run steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleRestoreRouteTests
{
    private readonly ITestOutputHelper _out;
    public VehicleRestoreRouteTests(ITestOutputHelper output) => _out = output;

    private static readonly Vector3 Size = new(2f, 1f, 0.5f);
    private static readonly Vector3 At = new(128f, 128f, Course.Ground + 0.27f);

    // The flags a script sets and removes, one call each.
    internal static readonly (int flag, bool remove)[] ScriptFlags =
    {
        ((int)VehicleFlag.HOVER_TERRAIN_ONLY, false),
        ((int)VehicleFlag.HOVER_GLOBAL_HEIGHT, false),
        ((int)VehicleFlag.MOUSELOOK_BANK, false),
        ((int)VehicleFlag.NO_DEFLECTION_UP, true),
        ((int)VehicleFlag.LIMIT_ROLL_ONLY, true),
        ((int)VehicleFlag.HOVER_UP_ONLY, true),
    };

    internal static readonly Quaternion ScriptFrame = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);

    /// <summary>The script's setup: the type, six flag calls, four float settings, two vector settings and a reference
    /// frame, each through the record as llSetVehicle* calls go.</summary>
    internal static void ScriptSetup(Run r, SharedVehicle type)
    {
        r.MakeVehicle(type);
        foreach ((int flag, bool remove) in ScriptFlags)
            r.SetFlags(flag, remove);
        r.SetFloat(SharedVehicle.LINEAR_MOTOR_TIMESCALE, 0.5f);
        r.SetFloat(SharedVehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 2f);
        r.SetFloat(SharedVehicle.VERTICAL_ATTRACTION_TIMESCALE, 3f);
        r.SetFloat(SharedVehicle.BANKING_EFFICIENCY, 0.4f);
        r.SetVector(SharedVehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(2f, 4f, 1000f));
        r.SetVector(SharedVehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(3f, 3f, 5f));
        r.SetRotation(SharedVehicle.REFERENCE_FRAME, ScriptFrame);
    }

    /// <summary>Everything the vehicle controller holds that a script can set, and whether each motor is running.</summary>
    internal static SortedDictionary<string, string> State(PhysicsActor actor)
    {
        VehicleController v = ((JoltPrim)actor).VehicleControl;
        var s = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (v == null)
        {
            s["type"] = "none (no vehicle)";
            return s;
        }
        s["type"] = v.Type.ToString();
        s["flags"] = FlagText((uint)v.Flags);
        foreach (VehFloatParam k in Enum.GetValues<VehFloatParam>())
            s[k.ToString()] = v.GetFloatParam(k).ToString("R");
        foreach (VehVectorParam k in Enum.GetValues<VehVectorParam>())
            s[k.ToString()] = Text(v.GetVecParam(k));
        s["ReferenceFrame"] = Text(v.ReferenceFrame);
        s["linear motor running"] = v.LinearMotorSet.ToString();
        s["angular motor running"] = v.AngularMotorSet.ToString();
        return s;
    }

    /// <summary>What a vehicle restored from <paramref name="vd"/> must hold: the record's type, flags, settings and
    /// reference frame (as the controller stores them: a float timescale on all three axes, the frame normalized), no
    /// motor running, and the controller's own extension params, which the record does not carry, as the type sets
    /// them (<paramref name="sameType"/>, a vehicle of that type). Each setting is held to the region's vehicle limits,
    /// as the same value from a script is: the record keeps a preset's hover timescale of 1000 s, which the limit holds to
    /// 300 s, and both turn hover off (VehicleSettings.MaxHoverTimescale).</summary>
    internal static SortedDictionary<string, string> Expected(VehicleData vd, SortedDictionary<string, string> sameType, bool heldToLimits = true)
    {
        VehicleSettings l = VehicleSettings.Default;
        if (heldToLimits)
        {
            vd.m_VhoverTimescale = Math.Clamp(vd.m_VhoverTimescale, l.MinTimescale, l.MaxHoverTimescale);
            vd.m_verticalAttractionTimescale = Math.Clamp(vd.m_verticalAttractionTimescale, l.MinTimescale, l.MaxAttractTimescale);
        }
        var s = new SortedDictionary<string, string>(sameType, StringComparer.Ordinal)
        {
            ["type"] = ((Vehicles.Vehicle)(int)vd.m_type).ToString(),
            ["flags"] = FlagText((uint)vd.m_flags),
            [nameof(VehFloatParam.AngularDeflectionEfficiency)] = vd.m_angularDeflectionEfficiency.ToString("R"),
            [nameof(VehFloatParam.AngularDeflectionTimescale)] = vd.m_angularDeflectionTimescale.ToString("R"),
            [nameof(VehFloatParam.BankingEfficiency)] = vd.m_bankingEfficiency.ToString("R"),
            [nameof(VehFloatParam.BankingMix)] = vd.m_bankingMix.ToString("R"),
            [nameof(VehFloatParam.BankingTimescale)] = vd.m_bankingTimescale.ToString("R"),
            [nameof(VehFloatParam.Buoyancy)] = vd.m_VehicleBuoyancy.ToString("R"),
            [nameof(VehFloatParam.HoverEfficiency)] = vd.m_VhoverEfficiency.ToString("R"),
            [nameof(VehFloatParam.HoverHeight)] = vd.m_VhoverHeight.ToString("R"),
            [nameof(VehFloatParam.HoverTimescale)] = vd.m_VhoverTimescale.ToString("R"),
            [nameof(VehFloatParam.LinearDeflectionEfficiency)] = vd.m_linearDeflectionEfficiency.ToString("R"),
            [nameof(VehFloatParam.LinearDeflectionTimescale)] = vd.m_linearDeflectionTimescale.ToString("R"),
            [nameof(VehFloatParam.VerticalAttractionEfficiency)] = vd.m_verticalAttractionEfficiency.ToString("R"),
            [nameof(VehFloatParam.VerticalAttractionTimescale)] = vd.m_verticalAttractionTimescale.ToString("R"),
            [nameof(VehVectorParam.LinearFrictionTimescale)] = Text(vd.m_linearFrictionTimescale),
            [nameof(VehVectorParam.AngularFrictionTimescale)] = Text(vd.m_angularFrictionTimescale),
            [nameof(VehVectorParam.LinearMotorDirection)] = Text(vd.m_linearMotorDirection),
            [nameof(VehVectorParam.AngularMotorDirection)] = Text(vd.m_angularMotorDirection),
            [nameof(VehVectorParam.LinearMotorOffset)] = Text(vd.m_linearMotorOffset),
            [nameof(VehVectorParam.LinearMotorTimescale)] = Text(new Vector3(vd.m_linearMotorTimescale)),
            [nameof(VehVectorParam.AngularMotorTimescale)] = Text(new Vector3(vd.m_angularMotorTimescale)),
            [nameof(VehVectorParam.LinearMotorDecayTimescale)] = Text(new Vector3(vd.m_linearMotorDecayTimescale)),
            [nameof(VehVectorParam.AngularMotorDecayTimescale)] = Text(new Vector3(vd.m_angularMotorDecayTimescale)),
            ["ReferenceFrame"] = Text(Quaternion.Normalize(in vd.m_referenceFrame)),
            ["linear motor running"] = false.ToString(),
            ["angular motor running"] = false.ToString(),
        };
        return s;
    }

    private static string Text(Vector3 v) => $"<{v.X:R}, {v.Y:R}, {v.Z:R}>";
    private static string Text(Quaternion q) => $"<{q.X:R}, {q.Y:R}, {q.Z:R}, {q.W:R}>";
    private static string FlagText(uint f) => $"0x{f:X8} {(ExtendedVehicleFlags)f}";

    /// <summary>The lines where <paramref name="actual"/> differs from <paramref name="expected"/>.</summary>
    internal static List<string> Diff(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual)
        => expected.Keys.Union(actual.Keys).Where(k => !expected.TryGetValue(k, out string e) || !actual.TryGetValue(k, out string a) || e != a)
                   .Select(k => $"{k}: ended with {(actual.TryGetValue(k, out string a) ? a : "(absent)")}, saved {(expected.TryGetValue(k, out string e) ? e : "(absent)")}")
                   .ToList();

    internal sealed class Outcome
    {
        public SortedDictionary<string, string> ScriptSet;   // the vehicle as its script set it up, before the route
        public SortedDictionary<string, string> Restored;    // the vehicle after the route, a few heartbeats later
        public VehicleData Saved;                            // the record the route handed back
        public SortedDictionary<string, string> FreshType;   // a vehicle given only the type, for the extension params
        public Vector3 StartedAt, EndedAt;                   // where the restored vehicle was handed back, and ended
        public Vector3 EndVelocity;
    }

    private const int RouteAt = 2, OnAt = 4, ReadAt = 8;

    /// <summary>Sets a vehicle up by script, then hands it back by <paramref name="route"/>: at the region's first step
    /// for a region start, otherwise with the region running (heartbeat <see cref="RouteAt"/>; physics back on at
    /// <see cref="OnAt"/>). <paramref name="motor"/>: a linear motor the script set before the vehicle was saved.</summary>
    internal static Outcome RunRoute(SharedVehicle type, VehicleRoute route, Vector3? motor = null, double physicsRate = 0.0,
                                     Vector3 handBackVelocity = default, Action<Run> moreScriptCalls = null, string behaviour = null,
                                     Action<Run, SharedVehicle> setup = null,
                                     Action<HarnessOptions> configure = null, int readAt = ReadAt)
    {
        var o = new Outcome();
        SOPVehicle saved = null;
        bool phantom = route == VehicleRoute.PhantomPhysicsOffOn;
        void Build(PhysicsActor a, Run r) { r.AsSimulatorAdds(a); a.Density = 1000f; }

        void HandBack(Run r)
        {
            PhysicsActor old = r.Actor;
            if (route == VehicleRoute.Crossing)
            {
                // Core starts the crossing and has the engine write the motors' running state into the record it sends
                // (SceneObjectGroup.CrossAsync).
                old.CrossingStart();
                old.CaptureVehicleRunningState(ref r.VehiclePart.VehicleParams.vd);
            }
            saved = VehicleRestore.Saved(r.VehiclePart, route);
            o.Saved = saved.vd;
            Vector3 pos = old.Position;
            Quaternion rot = old.Orientation;
            r.PhysicsScene.RemovePrim(old);
            r.Actor = VehicleRestore.AddToPhysics(r.PhysicsScene, saved, pos, Size, rot, true, false, true, handBackVelocity, Vector3.Zero,
                                                  Run.ActorLocalId, a => Build(a, r));
            r.VehiclePart.PhysActor = r.Actor;
            o.StartedAt = pos;
        }

        var sc = new Scenario
        {
            Name = "vehicle-restore-" + route,
            DefaultDuration = _ => 1f,
            Setup = r =>
            {
                r.Actor = VehicleRestore.AddToPhysics(r.PhysicsScene, null, At, Size, Quaternion.Identity, true, phantom, false, Vector3.Zero,
                                                      Vector3.Zero, Run.ActorLocalId, a => Build(a, r));
                r.ActorSize = Size;
                (setup ?? ScriptSetup)(r, type);
                if (motor.HasValue)
                    r.SetVector(SharedVehicle.LINEAR_MOTOR_DIRECTION, motor.Value);
                moreScriptCalls?.Invoke(r);
                o.ScriptSet = State(r.Actor);
                if (route == VehicleRoute.RegionStart)
                    HandBack(r);
                r.StopAt = (readAt + 1) / 45.0;
            },
            Input = r =>
            {
                int k = (int)Math.Round(r.Now * 45.0);
                if (k == RouteAt)
                {
                    switch (route)
                    {
                        case VehicleRoute.PhysicsOffOn:
                            o.Saved = r.VehiclePart.VehicleParams.vd;
                            o.StartedAt = r.Actor.Position;
                            VehicleRestore.PhysicsOff(r.Actor);
                            break;
                        case VehicleRoute.PhantomPhysicsOffOn:
                            o.StartedAt = r.Actor.Position;
                            VehicleRestore.PhantomPhysicsOff(r.PhysicsScene, r.Actor);
                            break;
                        case VehicleRoute.RegionStart:
                            break;
                        default:
                            HandBack(r);
                            break;
                    }
                }
                else if (k == OnAt)
                {
                    if (route == VehicleRoute.PhysicsOffOn)
                        VehicleRestore.PhysicsOn(r.Actor);
                    else if (route == VehicleRoute.PhantomPhysicsOffOn)
                    {
                        saved = VehicleRestore.Saved(r.VehiclePart, route);
                        o.Saved = saved.vd;
                        r.Actor = VehicleRestore.PhantomPhysicsOn(r.PhysicsScene, saved, o.StartedAt, Size, Quaternion.Identity, Run.ActorLocalId,
                                                                  a => Build(a, r));
                        r.VehiclePart.PhysActor = r.Actor;
                    }
                }
                else if (k == readAt)
                {
                    o.Restored = State(r.Actor);
                    o.EndedAt = r.Actor.Position;
                    o.EndVelocity = r.Actor.Velocity;
                }
            },
        };
        var options = new HarnessOptions { RateHz = 45.0, PhysicsRateHz = physicsRate, SimulatorDefaults = true }.SecondLife();
        if (behaviour != null)
            options.Jolt["VehicleBehaviour"] = behaviour;
        configure?.Invoke(options);
        Harness.Harness.Run(sc, options);

        // The controller's extension params as the type alone sets them.
        var fresh = new Scenario
        {
            Name = "vehicle-restore-type",
            DefaultDuration = _ => 1f / 45f,
            Setup = r =>
            {
                r.AddBox(Size, At, Quaternion.Identity);
                r.MakeVehicle(type);
                o.FreshType = State(r.Actor);
            },
        };
        var freshOptions = new HarnessOptions { RateHz = 45.0, PhysicsRateHz = physicsRate }.SecondLife();
        if (behaviour != null)
            freshOptions.Jolt["VehicleBehaviour"] = behaviour;
        configure?.Invoke(freshOptions);
        Harness.Harness.Run(fresh, freshOptions);
        return o;
    }

    public static IEnumerable<object[]> TypesAndRoutes()
    {
        foreach (SharedVehicle type in new[] { SharedVehicle.TYPE_SLED, SharedVehicle.TYPE_CAR, SharedVehicle.TYPE_BOAT,
                                               SharedVehicle.TYPE_AIRPLANE, SharedVehicle.TYPE_BALLOON })
            foreach (VehicleRoute route in Enum.GetValues<VehicleRoute>().Where(r => r != VehicleRoute.None))
                yield return new object[] { type, route };
    }

    // Every route but a solid prim's physics switch hands back exactly the record; that switch leaves the vehicle exactly
    // as its script set it up.
    [Theory]
    [MemberData(nameof(TypesAndRoutes))]
    public void A_restored_vehicle_ends_with_exactly_what_was_saved(SharedVehicle type, VehicleRoute route)
    {
        Outcome o = RunRoute(type, route);
        Assert.NotNull(o.Restored);
        SortedDictionary<string, string> expected = route == VehicleRoute.PhysicsOffOn ? o.ScriptSet : Expected(o.Saved, o.FreshType);
        List<string> diff = Diff(expected, o.Restored);
        _out.WriteLine($"{type} by {route}: {diff.Count} differences from what was saved");
        foreach (string line in diff)
            _out.WriteLine("  " + line);
        Assert.Empty(diff);
    }

    // The saved record is what the script set: its type, the six flag calls on top of the record's preset, the four
    // floats, the two vectors and the frame. For a car and an airplane the simulator's preset (SOPVehicle) is the
    // controller's documented one, so the vehicle the script set up holds exactly the record too (its preset hover
    // timescale of 1000 s not held to the limit, as no script set it).
    [Theory]
    [InlineData(SharedVehicle.TYPE_CAR)]
    [InlineData(SharedVehicle.TYPE_AIRPLANE)]
    public void Where_the_presets_agree_the_script_set_vehicle_holds_the_record(SharedVehicle type)
    {
        Outcome o = RunRoute(type, VehicleRoute.Copy);
        Assert.Equal(0.5f, o.Saved.m_linearMotorTimescale);
        Assert.Equal(2f, o.Saved.m_linearMotorDecayTimescale);
        Assert.Equal(3f, o.Saved.m_verticalAttractionTimescale);
        Assert.Equal(0.4f, o.Saved.m_bankingEfficiency);
        Assert.Equal(new Vector3(2f, 4f, 1000f), o.Saved.m_linearFrictionTimescale);
        Assert.Equal(new Vector3(3f, 3f, 5f), o.Saved.m_angularFrictionTimescale);
        Assert.Equal(ScriptFrame, o.Saved.m_referenceFrame);
        Assert.Empty(Diff(Expected(o.Saved, o.FreshType, heldToLimits: false), o.ScriptSet));
    }

    // A vehicle saved while its script had a motor set is handed back with the motor's direction stored and no motor
    // running, as on ubODE: it stays where it was handed back until its script sets the motor again.
    [Theory]
    [InlineData(VehicleRoute.Rez)]
    [InlineData(VehicleRoute.RegionStart)]
    [InlineData(VehicleRoute.Copy)]
    public void A_vehicle_saved_with_its_motor_set_does_not_drive_off(VehicleRoute route)
    {
        Outcome o = RunRoute(SharedVehicle.TYPE_CAR, route, motor: new Vector3(8f, 0f, 0f));
        float moved = Vector3.Distance(new Vector3(o.StartedAt.X, o.StartedAt.Y, 0f), new Vector3(o.EndedAt.X, o.EndedAt.Y, 0f));
        _out.WriteLine($"{route}: moved {moved:0.000} m in the {ReadAt - RouteAt} heartbeats after it was handed back; linear motor running {o.Restored["linear motor running"]}");
        Assert.Equal(new Vector3(8f, 0f, 0f), o.Saved.m_linearMotorDirection);
        Assert.Equal("<8, 0, 0>", o.Restored[nameof(VehVectorParam.LinearMotorDirection)]);
        Assert.Equal("False", o.Restored["linear motor running"]);
        Assert.True(moved < 0.01f, $"{route}: moved {moved:0.000} m after it was handed back");
    }

    // A vehicle handed back with the region running keeps the velocity the simulator replays onto it (a vehicle arriving
    // from another region at speed), as on ubODE: only its own friction slows it. Its velocity is zeroed after the type
    // is set only for a type a script sets and for a vehicle handed back while the region loads.
    [Theory]
    [InlineData(VehicleRoute.Crossing)]
    [InlineData(VehicleRoute.Rez)]
    public void A_vehicle_handed_back_at_speed_keeps_its_speed(VehicleRoute route)
    {
        Outcome o = RunRoute(SharedVehicle.TYPE_CAR, route, handBackVelocity: new Vector3(10f, 0f, 0f));
        _out.WriteLine($"{route}: {o.EndVelocity.X:0.000} m/s along x {ReadAt - RouteAt} heartbeats after it was handed back at 10 m/s");
        // Forward friction timescale 2 s: 10 m/s times e^(-t / 2) after the 6 heartbeats, 9.35 m/s.
        Assert.InRange(o.EndVelocity.X, 9.2f, 9.5f);
    }

    // Where the simulator's own type presets (SOPVehicle.ProcessTypeChange), which it saves with every vehicle, differ from
    // the controller's documented ones (VehicleController.SetDocumentedDefaults). A vehicle handed back gets the
    // simulator's values in these fields, where the same vehicle set up by its script has the documented ones. The car
    // and the airplane agree in every field.
    [Theory]
    [InlineData(SharedVehicle.TYPE_SLED, "AngularDeflectionTimescale: ended with 10, saved 1000|HoverTimescale: ended with 1000, saved 10|VerticalAttractionEfficiency: ended with 1, saved 0")]
    [InlineData(SharedVehicle.TYPE_CAR, "")]
    [InlineData(SharedVehicle.TYPE_BOAT, "flags: ended with 0x00000065 NoDeflectionUp, HoverWaterOnly, HoverUpOnly, LimitMotorUp, saved 0x00000045 NoDeflectionUp, HoverWaterOnly, LimitMotorUp")]
    [InlineData(SharedVehicle.TYPE_AIRPLANE, "")]
    [InlineData(SharedVehicle.TYPE_BALLOON, "VerticalAttractionEfficiency: ended with 1, saved 0|flags: ended with 0x00000000 None, saved 0x00000012 LimitRollOnly, HoverGlobalHeight")]
    public void The_simulators_saved_presets_differ_from_the_documented_ones_only_here(SharedVehicle type, string differences)
    {
        SortedDictionary<string, string> engine = null;
        VehicleData record = default;
        var sc = new Scenario
        {
            Name = "vehicle-presets",
            DefaultDuration = _ => 1f / 45f,
            Setup = r =>
            {
                r.AddBox(Size, At, Quaternion.Identity);
                r.MakeVehicle(type);
                engine = State(r.Actor);
                record = r.VehiclePart.VehicleParams.vd;
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 45.0, PhysicsRateHz = 0 }.SecondLife());
        List<string> diff = Diff(Expected(record, engine, heldToLimits: false), engine);
        foreach (string line in diff)
            _out.WriteLine(line);
        Assert.Equal(differences, string.Join("|", diff));
    }
}
