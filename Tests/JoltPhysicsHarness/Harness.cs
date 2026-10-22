/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The physics harness: one scenario at one heartbeat rate, run through the Jolt module's own per-heartbeat
// step (JoltScene.Simulate: linkset and activation drains, the vehicle controllers, one backend step with the
// character step inside it, then the body, character and contact drains) with no OpenSim region around it.
//
// What stands in for the region:
// - the terrain is a heightmap handed to the scene's own SetTerrain, as the region's terrain module does;
// - a prim is added with AddPrimShape and set up through the PhysicsActor surface a SceneObjectPart uses
//   (Density, VehicleType, vehicle params); an avatar with AddAvatar and TargetVelocity / AvatarJump, as
//   ScenePresence does;
// - the heartbeat is a loop calling Simulate(1 / rate) with simulated time, so a run is faster than real time
//   and repeatable. The vehicle controller's clock is that simulated time, and a script or key input lands
//   half a heartbeat before the step that follows it, where it would land on average in a region.

using System.Globalization;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using SOPVehicle = OpenSim.Region.Framework.Scenes.SOPVehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>What to run. Unset values take the scenario's defaults.</summary>
public sealed class HarnessOptions
{
    /// <summary>The environment variable that sets <see cref="PhysicsRateHz"/>'s default, so a whole test run can
    /// be repeated with physics steps on.</summary>
    public const string PhysicsRateVariable = "JOLT_HARNESS_PHYSICS_RATE";

    public double RateHz = 11.0;
    /// <summary>[Jolt] PhysicsStepRate: physics steps per second inside each heartbeat; 0 = one step per heartbeat.</summary>
    public double PhysicsRateHz = DefaultPhysicsRate();
    public float? SlopeDeg;
    public float? Duration;
    /// <summary>A raw float32 terrain file (256 x 256, x fastest) for the scenarios that take one (<see cref="Scenario.AllowsTerrainFile"/>).</summary>
    public string TerrainFile;
    /// <summary>A folder for the per-collision-step trace of the scenarios that write one (sled-drop).</summary>
    public string TraceDir;
    public float? Hold;
    /// <summary>How often a held key re-sends the motor, as a script's control event does while a key is down.</summary>
    public float KeyRepeat = 0.1f;
    /// <summary>The mouselook scenarios' driver is in mouselook (the simulator's CameraData.MouseLook); false gives the
    /// same camera with the driver out of mouselook.</summary>
    public bool DriverInMouselook = true;
    /// <summary>The mouselook scenarios' camera shakes as a hand on a mouse does: each camera update (<see cref="CameraRateHz"/>
    /// a second) adds a random yaw and pitch of this standard deviation (degrees) to where the camera looks. 0 = a held camera.</summary>
    public float CameraNoiseDegrees;
    public double CameraRateHz = 30.0;
    public int CameraSeed = 1;
    /// <summary>Feed a driven vehicle the way a region does (see <see cref="InputFeed"/>).</summary>
    public InputFeed Feed = InputFeed.Heartbeat;
    /// <summary>Region feed: seconds from physics turning on to the first control event.</summary>
    public float KeyDelay = Harness.RegionKeyDelay;
    /// <summary>Heartbeat feed: give a car this forward speed (m/s) just before its key goes down, as the few mm/s a
    /// rez leaves it with. When set, the key goes down one heartbeat later (the first heartbeat is the region's
    /// load, which keeps no horizontal velocity), so runs with and without a start speed are fed alike.</summary>
    public float? StartSpeed;
    /// <summary>The clock the region's ray cast budgets are measured with; null is the wall clock. Tests pass one they
    /// drive, so what the budgets refuse does not depend on the machine's load.</summary>
    public TimeProvider RayCastClock;
    /// <summary>[Jolt] keys, as an operator would set them in the region's config.</summary>
    public readonly Dictionary<string, string> Jolt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Vehicle params applied after the scenario's own, as llSetVehicle*Param calls would be.</summary>
    public readonly List<VehicleParamSetting> VehicleParams = new();
    /// <summary>Vehicle flags set (or removed) after the scenario's own, as llSetVehicleFlags / llRemoveVehicleFlags.</summary>
    public readonly List<VehicleFlagSetting> VehicleFlags = new();
    /// <summary>Crash scenarios: the arrival speed as a share of the scenario's own (the motor for a driven crash; the
    /// square root of the drop height for crash-drop).</summary>
    public float CrashSpeed = 1f;
    /// <summary>Crash scenarios: the car's sideways offset from the scenario's line (m; for crash-drop, along x).</summary>
    public float CrashOffset;
    /// <summary>Crash scenarios: the car turned this many degrees about the vertical from the scenario's heading (for
    /// crash-drop: rolled about its nose).</summary>
    public float CrashAngle;
    /// <summary>Moves the scenario's own body (the car, box or prim it drives or drops) this far on x and y (m) from
    /// where the scenario places it; nothing else moves. A start a millimetre away shows how far a run's results
    /// move under a difference that small.</summary>
    public float StartOffsetX, StartOffsetY;
    /// <summary>Avatar scenarios: the request times this, as ScenePresence.SpeedModifier scales it (osSetSpeed).</summary>
    public float AvatarSpeedModifier = 1f;
    /// <summary>Shot scenarios (<see cref="TunnelScenarios"/>): the ball's diameter (m) and the speed it is shot at (m/s).</summary>
    public float ShotBall = TunnelScenarios.DefaultBall;
    public float ShotSpeed = TunnelScenarios.DefaultSpeed;
    /// <summary>Build every prim and avatar with the values the simulator hands the engine for a new one, instead of
    /// the harness's own (on by default; <c>--no-sim-defaults</c> gives the earlier harness's values). A prim gets what
    /// SceneObjectPart.AddToPhysics sets on every new actor: the material (wood: friction 0.6, restitution 0.5), density
    /// 1000, gravity multiplier 1, buoyancy 0, and for a root no rotation locks, before it is linked or made a vehicle.
    /// An avatar gets the default appearance's box as ScenePresence.AddToPhysicalScene hands it to AddAvatar (0.2 m taller
    /// than the appearance's size, AvatarAppearance.SetSize), then its rotation, a 100 ms collision subscription and
    /// always-run off. Without it a prim keeps the backend's friction 0.6 and restitution 0, and an avatar is 1.9 m.</summary>
    public bool SimulatorDefaults
    {
        get => SimulatorPrims && SimulatorAvatars;
        set => SimulatorPrims = SimulatorAvatars = value;
    }
    /// <summary>The prim half of <see cref="SimulatorDefaults"/> (the material, density and other values a new actor gets).</summary>
    public bool SimulatorPrims = true;
    /// <summary>The avatar half of <see cref="SimulatorDefaults"/> (the appearance's box, the collision subscription).</summary>
    public bool SimulatorAvatars = true;
    /// <summary>The avatar box's height (m) in place of the one <see cref="SimulatorAvatars"/> picks, for runs that compare
    /// avatar heights; null keeps the picked box.</summary>
    public float? AvatarHeight;
    /// <summary>Give the scene the region's mesher, a Meshmerizer of a load context of its own (<see cref="RegionMesher"/>),
    /// as a running region does. Without it the scene has no mesher and a prim that is not a box, sphere or cylinder takes
    /// its bounding box. On by default.</summary>
    public bool RegionMesher = true;
    /// <summary>Step the scene once, empty, before the scenario's setup, so the setup's prims and avatars are added to a
    /// region that has already run a heartbeat, as a rezzed prim or an arriving avatar is. The simulator's region-load
    /// window (<c>JoltScene.IsRegionLoading</c>) is the time before the first step; a prim loaded from storage is added in
    /// it, anything else after it. Not done for the region-start vehicle restore, which is the load. On by default.</summary>
    public bool WarmUpStep = true;
    /// <summary>Start from the [Jolt] section of RegionServerDefaults.ini, which every simulator reads before its own ini,
    /// instead of from no keys. On by default.</summary>
    public bool ShippedConfig = true;
    /// <summary>Run with the module's own material defaults even where <see cref="MaterialPins"/> holds the earlier ones.</summary>
    public bool UseDefaults;
    /// <summary>Restore the scenario's vehicle from its record by this route when its setup is done, before the first
    /// heartbeat, instead of leaving it as its script set it up (<see cref="VehicleRestore"/>). Every route but the
    /// physics switch removes the actor and builds a new one from the record at the same place, as the simulator's
    /// SceneObjectPart.AddToPhysics does; the physics switch makes the actor non-physical and physical again.</summary>
    public VehicleRoute VehicleRoute;
    /// <summary>After <see cref="VehicleRoute"/>, the scenario's script makes its vehicle calls again on the vehicle's actor,
    /// as a script that sets its vehicle up in on_rez does. A route compared with itself with this on compares the vehicle
    /// it hands back with the same vehicle set up by its script on a body with the same history: the engine moves a body
    /// it is given after another was removed (a route that builds a new actor, or physics switched off and on, which
    /// rebuilds the body) slightly differently once it slows to rest, whatever the body is.</summary>
    public bool VehicleScriptAgain;

    private static double DefaultPhysicsRate()
    {
        string v = Environment.GetEnvironmentVariable(PhysicsRateVariable);
        return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r >= 0 ? r : 0.0;
    }
}

/// <summary>How a held key reaches a driven vehicle.</summary>
public enum InputFeed
{
    /// <summary>The car rests on the ground at the start; the key goes down at t = 0 and every key event lands
    /// half a heartbeat before the step that follows it.</summary>
    Heartbeat,

    /// <summary>As a seated driver's control events reach the car's script in a region. The car starts where an
    /// object rezzed on the ground is placed, its centre one box height above the ground, and drops onto it when
    /// physics turns on (t = 0). The first control event comes <see cref="HarnessOptions.KeyDelay"/> later, then
    /// one per agent update while the key is held, and one with the key up at the release. Each event lands at
    /// its own time between heartbeats (a script runs on its own thread), and the controller's clock reads that
    /// time when the event sets the motor.</summary>
    Region,
}

/// <summary>One vehicle param: a float when <see cref="IsVector"/> is false (the value is X).</summary>
public readonly record struct VehicleParamSetting(Vehicle Code, Vector3 Value, bool IsVector)
{
    /// <summary>Parses NAME=v or NAME=x,y,z; NAME is an LSL VEHICLE_* name with or without the prefix.</summary>
    public static VehicleParamSetting Parse(string text)
    {
        int eq = text.IndexOf('=');
        if (eq <= 0)
            throw new ArgumentException($"vehicle param '{text}': expected NAME=value or NAME=x,y,z");
        string name = text[..eq].Trim().ToUpperInvariant();
        if (name.StartsWith("VEHICLE_", StringComparison.Ordinal))
            name = name["VEHICLE_".Length..];
        if (!Enum.TryParse(name, out Vehicle code) || (int)code < 16)
            throw new ArgumentException($"vehicle param '{text}': unknown parameter name");
        string[] parts = text[(eq + 1)..].Split(',');
        var f = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            f[i] = float.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        if (parts.Length == 1)
            return new VehicleParamSetting(code, new Vector3(f[0], 0f, 0f), false);
        if (parts.Length == 3)
            return new VehicleParamSetting(code, new Vector3(f[0], f[1], f[2]), true);
        throw new ArgumentException($"vehicle param '{text}': expected one value or three");
    }
}

/// <summary>One vehicle flag set or removed.</summary>
public readonly record struct VehicleFlagSetting(int Flag, bool Remove)
{
    // The documented VEHICLE_FLAG_* values.
    private static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NO_DEFLECTION_UP"] = 0x1, ["LIMIT_ROLL_ONLY"] = 0x2, ["HOVER_WATER_ONLY"] = 0x4, ["HOVER_TERRAIN_ONLY"] = 0x8,
        ["HOVER_GLOBAL_HEIGHT"] = 0x10, ["HOVER_UP_ONLY"] = 0x20, ["LIMIT_MOTOR_UP"] = 0x40,
        ["MOUSELOOK_STEER"] = 0x80, ["MOUSELOOK_BANK"] = 0x100, ["CAMERA_DECOUPLED"] = 0x200, ["BLOCK_INTERFERENCE"] = 0x400,
    };

    /// <summary>Parses NAME (set) or -NAME (remove); NAME is an LSL VEHICLE_FLAG_* name with or without the prefix.</summary>
    public static VehicleFlagSetting Parse(string text)
    {
        bool remove = text.StartsWith('-');
        string name = (remove ? text[1..] : text).Trim();
        if (name.StartsWith("VEHICLE_FLAG_", StringComparison.OrdinalIgnoreCase))
            name = name["VEHICLE_FLAG_".Length..];
        if (!Names.TryGetValue(name, out int flag))
            throw new ArgumentException($"vehicle flag '{text}': unknown flag name");
        return new VehicleFlagSetting(flag, remove);
    }
}

/// <summary>One heartbeat's state of the scenario's object, read after the step. Touching is the actor's
/// IsColliding (for a vehicle, its ground check: touching anything in the last step). Active is the number of
/// bodies the engine has awake after the step. Other and OtherVelocity are the scenario's second object, if any
/// (a wall, a box, a second car).</summary>
public readonly record struct Sample(double T, Vector3 Position, Vector3 Velocity, float Tilt, float Height, bool Touching = false,
                                     Quaternion Rotation = default, int Active = 0, Vector3 Other = default, Vector3 OtherVelocity = default,
                                     Quaternion OtherRotation = default, Vector3 AngularVelocity = default)
{
    public float Speed => Velocity.Length();
    public float HorizontalSpeed => MathF.Sqrt(Velocity.X * Velocity.X + Velocity.Y * Velocity.Y);
    public float OtherSpeed => OtherVelocity.Length();
}

/// <summary>The figures a run is judged by. NaN means "does not apply" or "did not happen".</summary>
public sealed class Summary
{
    public float TopSpeed;
    public double ReleaseT = double.NaN;
    public float ReleaseSpeed = float.NaN;
    /// <summary>Mean speed over the scenario's steady window (see <see cref="Scenario.Steady"/>).</summary>
    public float SteadySpeed = float.NaN;
    public float SteadyHorizontalSpeed = float.NaN;
    /// <summary>Horizontal distance from the start to the release.</summary>
    public float DistanceBeforeRelease;
    /// <summary>Horizontal distance from the release to the end of the run.</summary>
    public float DistanceAfterRelease;
    /// <summary>Seconds after the release at which the object came to rest (speed under 0.1 m/s for 1 s).</summary>
    public double TimeToRest = double.NaN;
    /// <summary>Highest centre height above the terrain under it.</summary>
    public float PeakHeight;
    /// <summary>Highest rise of the centre above where it was at the release.</summary>
    public float PeakRise;
    /// <summary>Height range of the centre after the first second (a standing avatar's bob).</summary>
    public float ZRange;
    public float MaxTilt;
    public Vector3 End;
    /// <summary>When the object crossed the region's edge, which ends the run (there is no ground beyond it).</summary>
    public double LeftRegionT = double.NaN;
    public int Steps;
    public int NonFinite;

    /// <summary>Seconds after the release from which the engine has no body awake to the end of the run; NaN if a
    /// body is still awake at the end.</summary>
    public double SleepAfter = double.NaN;
    /// <summary>Bodies awake after the last step.</summary>
    public int ActiveAtEnd;

    // Collision scenarios (those with a Scenario.Gap).
    /// <summary>When the gap between the surfaces that meet first closed; NaN if they never met.</summary>
    public double ImpactT = double.NaN;
    /// <summary>The fastest either object moved up to the impact.</summary>
    public float ArrivalSpeed = float.NaN;
    /// <summary>The fastest either object moved from 0.1 s to 1 s after the impact.</summary>
    public float LeavingSpeed = float.NaN;
    /// <summary>The deepest the surfaces overlapped after the impact (m).</summary>
    public float Penetration = float.NaN;
    /// <summary>1 if the object passed through what it hit, else 0.</summary>
    public int Tunneled;
    /// <summary>A vehicle held against what it hit: how far its centre moved along x (range, m) from 2 to 6 s after
    /// the impact, and from 6 to 10 s.</summary>
    public float PushRangeEarly = float.NaN;
    public float PushRangeLate = float.NaN;
    /// <summary>The deepest overlap from 2 to 10 s after the impact (m).</summary>
    public float PushPenetration = float.NaN;
    /// <summary>In the second after the impact: how far the centre of either object rose above where it was at the
    /// impact (m). A car thrown up by a contact, or riding over what it hit, rises; one stopped by it does not.</summary>
    public float CrashRise = float.NaN;

    /// <summary>Script ray casts the run made (ray_casts), what one cost on average (microseconds), and how many the
    /// region refused or cut short (its [Jolt] RayCastBudgetMs, RayCastMaxTestedHits).</summary>
    public long RayCasts;
    public double RayCastMicrosPerCast = double.NaN;
    public long RayCastsRefused;
    public long RayCastsCutShort;
}

public sealed class RunResult
{
    public string Scenario;
    public double RateHz;
    public double PhysicsRateHz;
    public float SlopeDeg;
    /// <summary>A crash run's changes from its scenario ("v" speed share, "o" offset, "a" angle), or empty.</summary>
    public string Variant = "";
    public readonly List<Sample> Samples = new();
    public Summary Summary;
    /// <summary>The phantom and volume-detect scenarios' prims and collision events (not in the CSV or summary).</summary>
    public readonly List<HarnessPart> Parts = new();
    public readonly List<CollisionWatch> Watches = new();

    public string Name => $"{Scenario}{Variant}-s{Fmt(SlopeDeg, "0.#")}-r{Fmt(RateHz, "0.#")}{(PhysicsRateHz > 0 ? "-p" + Fmt(PhysicsRateHz, "0.#") : "")}";

    /// <summary>The scenario column of the summary: the name and any crash variant, plus "/p" and the physics rate when
    /// physics steps are on.</summary>
    public string Label => PhysicsRateHz > 0 ? $"{Scenario}{Variant}/p{Fmt(PhysicsRateHz, "0.#")}" : Scenario + Variant;

    public const string CsvHeader = "t,x,y,z,vx,vy,vz,speed,hspeed,tilt_deg,height,touching,active,ox,oy,oz,ospeed,wx,wy,wz,ovx,ovy,ovz";

    /// <summary>The trace as CSV: one line per heartbeat, invariant culture, fixed decimals.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append('\n');
        foreach (Sample s in Samples)
        {
            sb.Append(Fmt(s.T, "0.0000")).Append(',')
              .Append(Fmt(s.Position.X, "0.0000")).Append(',').Append(Fmt(s.Position.Y, "0.0000")).Append(',').Append(Fmt(s.Position.Z, "0.0000")).Append(',')
              .Append(Fmt(s.Velocity.X, "0.0000")).Append(',').Append(Fmt(s.Velocity.Y, "0.0000")).Append(',').Append(Fmt(s.Velocity.Z, "0.0000")).Append(',')
              .Append(Fmt(s.Speed, "0.0000")).Append(',').Append(Fmt(s.HorizontalSpeed, "0.0000")).Append(',')
              .Append(Fmt(s.Tilt, "0.00")).Append(',').Append(Fmt(s.Height, "0.0000")).Append(',')
              .Append(s.Touching ? '1' : '0').Append(',').Append(s.Active.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Fmt(s.Other.X, "0.0000")).Append(',').Append(Fmt(s.Other.Y, "0.0000")).Append(',').Append(Fmt(s.Other.Z, "0.0000")).Append(',')
              .Append(Fmt(s.OtherSpeed, "0.0000")).Append(',')
              .Append(Fmt(s.AngularVelocity.X, "0.0000")).Append(',').Append(Fmt(s.AngularVelocity.Y, "0.0000")).Append(',').Append(Fmt(s.AngularVelocity.Z, "0.0000")).Append(',')
              .Append(Fmt(s.OtherVelocity.X, "0.0000")).Append(',').Append(Fmt(s.OtherVelocity.Y, "0.0000")).Append(',').Append(Fmt(s.OtherVelocity.Z, "0.0000")).Append('\n');
        }
        return sb.ToString();
    }

    public const string PartsCsvHeader = "t,part,x,y,z,vx,vy,vz";

    /// <summary>The traces of the parts a scenario keeps one for (HarnessPart.Trace), one line per part per heartbeat,
    /// as ToCsv; null when it keeps none.</summary>
    public string PartsToCsv()
    {
        if (!Parts.Any(p => p.Trace != null))
            return null;
        var sb = new StringBuilder();
        sb.Append(PartsCsvHeader).Append('\n');
        foreach (HarnessPart p in Parts.Where(p => p.Trace != null))
            foreach ((double t, Vector3 at, Vector3 v) in p.Trace)
                sb.Append(Fmt(t, "0.0000")).Append(',').Append(p.Name).Append(',')
                  .Append(Fmt(at.X, "0.0000")).Append(',').Append(Fmt(at.Y, "0.0000")).Append(',').Append(Fmt(at.Z, "0.0000")).Append(',')
                  .Append(Fmt(v.X, "0.0000")).Append(',').Append(Fmt(v.Y, "0.0000")).Append(',').Append(Fmt(v.Z, "0.0000")).Append('\n');
        return sb.ToString();
    }

    /// <summary>One line per part whose sleep the scenario watches: when it first fell asleep ("-": never), whether it
    /// was asleep at the end, and how far it moved from where it started.</summary>
    public IEnumerable<string> PartLines()
        => Parts.Where(p => p.SleepWatched).Select(p =>
            $"  part {p.Name}: asleep at {Fmt(p.SleptAt, "0.00")} s, asleep at the end {(p.AsleepAtEnd ? "yes" : "no")}, " +
            $"moved {Fmt(Vector3.Distance(p.Position, p.EndPosition), "0.0000")} m");

    public const string SummaryHeader =
        "scenario,slope_deg,rate_hz,steps,top_speed,release_t,release_speed,steady_speed,steady_hspeed,dist_before_release,dist_after_release,time_to_rest,peak_height,peak_rise,z_range,max_tilt_deg,end_x,end_y,end_z,left_region_t,nonfinite," +
        "sleep_after,active_at_end,impact_t,arrival_speed,leaving_speed,penetration,tunneled,push_range_early,push_range_late,push_penetration,crash_rise," +
        "ray_casts,ray_us_per_cast,ray_refused,ray_cut_short";

    public string SummaryLine()
    {
        Summary m = Summary;
        return string.Join(",",
            Label, Fmt(SlopeDeg, "0.#"), Fmt(RateHz, "0.#"), m.Steps.ToString(CultureInfo.InvariantCulture),
            Fmt(m.TopSpeed, "0.000"), Fmt(m.ReleaseT, "0.000"), Fmt(m.ReleaseSpeed, "0.000"),
            Fmt(m.SteadySpeed, "0.000"), Fmt(m.SteadyHorizontalSpeed, "0.000"),
            Fmt(m.DistanceBeforeRelease, "0.000"), Fmt(m.DistanceAfterRelease, "0.000"), Fmt(m.TimeToRest, "0.000"),
            Fmt(m.PeakHeight, "0.000"), Fmt(m.PeakRise, "0.000"), Fmt(m.ZRange, "0.000"), Fmt(m.MaxTilt, "0.0"),
            Fmt(m.End.X, "0.000"), Fmt(m.End.Y, "0.000"), Fmt(m.End.Z, "0.000"), Fmt(m.LeftRegionT, "0.000"), m.NonFinite.ToString(CultureInfo.InvariantCulture),
            Fmt(m.SleepAfter, "0.000"), m.ActiveAtEnd.ToString(CultureInfo.InvariantCulture), Fmt(m.ImpactT, "0.000"),
            Fmt(m.ArrivalSpeed, "0.000"), Fmt(m.LeavingSpeed, "0.000"), Fmt(m.Penetration, "0.0000"), m.Tunneled.ToString(CultureInfo.InvariantCulture),
            Fmt(m.PushRangeEarly, "0.0000"), Fmt(m.PushRangeLate, "0.0000"), Fmt(m.PushPenetration, "0.0000"), Fmt(m.CrashRise, "0.0000"),
            m.RayCasts.ToString(CultureInfo.InvariantCulture), Fmt(m.RayCastMicrosPerCast, "0.0"),
            m.RayCastsRefused.ToString(CultureInfo.InvariantCulture), m.RayCastsCutShort.ToString(CultureInfo.InvariantCulture));
    }

    internal static string Fmt(double v, string format)
        => double.IsNaN(v) ? "-" : v.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>
/// The test ground. A 256 m region with level ground at 25 m and water at 20 m, like a simple physics test
/// course. With a slope, the ground rises northward from y 40 to y 100 at that angle and is level again
/// beyond (the whole width of the region, so nothing can roll off a side).
/// </summary>
public static class Course
{
    public const int Size = 256;
    public const float Ground = 25f;
    public const float Water = 20f;
    public const float RampBottomY = 40f;
    public const float RampTopY = 100f;

    public static float HeightAt(float y, float slopeDeg)
    {
        if (slopeDeg <= 0f || y <= RampBottomY)
            return Ground;
        float tan = MathF.Tan(slopeDeg * MathF.PI / 180f);
        return Ground + (MathF.Min(y, RampTopY) - RampBottomY) * tan;
    }

    public static float[] Heightmap(float slopeDeg, float level = Ground)
    {
        var h = new float[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            float z = slopeDeg > 0f ? HeightAt(y, slopeDeg) : level;
            for (int x = 0; x < Size; x++)
                h[y * Size + x] = z;
        }
        return h;
    }
}

/// <summary>A running scenario: the scene, the object under test and the simulated clock.</summary>
public sealed class Run
{
    internal JoltScene Scene;
    public PhysicsActor Actor;
    public Vector3 ActorSize;
    public HarnessOptions Options;
    public float Slope;
    public float Duration;
    public float Hold;
    public double Dt;
    /// <summary>Time of the heartbeat about to run (inputs read this).</summary>
    public double Now;
    /// <summary>When the key is let go, the jump is pressed or the object is let fall; NaN if never.</summary>
    public double ReleaseAt = double.NaN;
    /// <summary>Stop once the object has come to rest after the release.</summary>
    public bool StopAtRest;
    /// <summary>Stop after the sample at this time; NaN = run the whole duration.</summary>
    public double StopAt = double.NaN;
    internal double NextKey;
    internal bool Released;
    /// <summary>The simulated time the vehicle controller's clock reads.</summary>
    internal double Clock;

    /// <summary>The scenario's second object (a wall, a box, a second car), if any.</summary>
    public PhysicsActor Other;

    /// <summary>Ray casts a scenario timed itself (raycast-cost-all), and their time in Stopwatch ticks.</summary>
    public long TimedRayCasts;
    public long TimedRayTicks;
    public Vector3 OtherSize;
    /// <summary>Further vehicles that get the same key input as <see cref="Actor"/>. The motor is in each vehicle's
    /// own frame, so two cars facing each other drive at each other.</summary>
    public readonly List<PhysicsActor> AlsoDriven = new();

    /// <summary>The prims a phantom or volume-detect scenario added, and the collision events watched.</summary>
    public readonly List<HarnessPart> Parts = new();
    public readonly List<CollisionWatch> Watches = new();
    /// <summary>Calls the run makes when it ends, before the scene is disposed (a scenario that gave a SceneObjectPart an
    /// actor takes it back here).</summary>
    public readonly List<Action> Cleanups = new();
    /// <summary>A scenario's own step counter for inputs that come one after another.</summary>
    public int Stage;

    public static readonly Vector3 AvatarSize = new(0.45f, 0.6f, 1.9f);   // the default appearance's box
    /// <summary>The box the simulator hands AddAvatar for the default appearance: AvatarAppearance.SetSize(0.45, 0.6, 1.9)
    /// adds 0.2 m to the height (AVBOXAJUST).</summary>
    public static readonly Vector3 SimulatorAvatarSize = new(0.45f, 0.6f, 2.1f);
    /// <summary>The avatar box this run uses: <see cref="SimulatorAvatarSize"/> with <see cref="HarnessOptions.SimulatorDefaults"/>.</summary>
    public Vector3 AvatarBox
    {
        get
        {
            Vector3 box = Options.SimulatorAvatars ? SimulatorAvatarSize : AvatarSize;
            return Options.AvatarHeight is float h ? new Vector3(box.X, box.Y, h) : box;
        }
    }
    /// <summary>How far an avatar's capsule centre stands above what it stands on.</summary>
    public float AvatarStandHalf => JoltCharacter.StandHalfFor(AvatarBox);

    /// <summary>With <see cref="HarnessOptions.SimulatorDefaults"/>: what SceneObjectPart.AddToPhysics sets on a new prim's
    /// actor right after AddPrimShape, in its order, with a new part's values (SceneObjectPart: material wood, density
    /// 1000, gravity multiplier 1; SOPMaterialData: wood is friction 0.6, restitution 0.5; buoyancy 0; RotationAxisLocks
    /// 0, set on a root only). Returns the actor.</summary>
    public PhysicsActor AsSimulatorAdds(PhysicsActor pa, bool root = true)
    {
        if (!Options.SimulatorPrims || pa == null)
            return pa;
        pa.SetMaterial((int)Material.Wood);
        pa.Density = 1000f;
        pa.GravModifier = 1f;
        pa.Friction = 0.6f;
        pa.Restitution = 0.5f;
        pa.Buoyancy = 0f;
        if (root)
            pa.LockAngularMotion(0);
        return pa;
    }
    public const uint ActorLocalId = 1000;
    private const uint OtherLocalId = 1001;
    private const uint ChildLocalId = 1002;

    /// <summary>Physics time stepped so far (s): the backend's steps times their length, which with [Jolt]
    /// PhysicsStepRate on can differ from the heartbeats' time by up to one step.</summary>
    public double PhysicsTime => Scene.Substepping ? (Scene.Substeps.Steps - WarmUpSubsteps) * (double)Scene.Substeps.StepSeconds : Heartbeats * Dt;
    /// <summary>The physics steps the warm-up heartbeat (<see cref="HarnessOptions.WarmUpStep"/>) ran, which are not the run's.</summary>
    internal long WarmUpSubsteps;
    /// <summary>The region's mesher (<see cref="HarnessOptions.RegionMesher"/>), or null when the run has none.</summary>
    public IMesher Mesher { get; internal set; }
    /// <summary>Heartbeats run so far.</summary>
    public int Heartbeats;
    /// <summary>Bodies the engine has awake now.</summary>
    public int AwakeBodies => Harness.ActiveBodies(Scene);
    /// <summary>The region's physics scene, as core sees it (RemovePrim, RaycastWorld).</summary>
    public PhysicsScene PhysicsScene => Scene;
    /// <summary>The backend's counters now: live bodies and shapes among them.</summary>
    public OpenSim.Region.PhysicsModules.Jolt.Backend.PhysicsCapacityStats Capacity => Scene.CapacityStats();

    /// <summary>How a prim that is phantom from the start is built. The simulator passes isPhantom to AddPrimShape
    /// (SceneObjectPart.AddToPhysics); the earlier harness built it solid and set Phantom after, which is what the
    /// simulator does for a prim that turns phantom later. <see cref="HarnessOptions.SimulatorPrims"/> picks the first.</summary>
    private PhysicsActor AddPrimShapeAs(string name, PrimitiveBaseShape shape, Vector3 position, Vector3 size, Quaternion rotation,
                                        bool physical, bool phantom, uint localId)
    {
        bool atCreation = phantom && Options.SimulatorPrims;
        PhysicsActor pa = atCreation
            ? Scene.AddPrimShape(name, shape, position, size, rotation, physical, true, (byte)PhysShapeType.prim, localId)
            : Scene.AddPrimShape(name, shape, position, size, rotation, physical, localId);
        if (phantom && !atCreation)
            pa.Phantom = true;
        return pa;
    }

    /// <summary>A physical sphere prim, as a SceneObjectPart adds one (density 1000).</summary>
    public PhysicsActor AddSphere(float diameter, Vector3 position, bool phantom = false)
    {
        PhysicsActor pa = AddPrimShapeAs("harness sphere", PrimitiveBaseShape.CreateSphere(), position,
                                         new Vector3(diameter, diameter, diameter), Quaternion.Identity, true, phantom, ActorLocalId);
        AsSimulatorAdds(pa);
        pa.Density = 1000f;
        Actor = pa;
        ActorSize = new Vector3(diameter, diameter, diameter);
        return pa;
    }

    /// <summary>A physical box prim linked to <see cref="Actor"/> as its child, as a SceneObjectPart adds a linked part
    /// (density 1000, then link to the root's actor). Returns the child's actor. A second child needs its own
    /// <paramref name="localId"/>.</summary>
    public PhysicsActor AddChildBox(Vector3 size, Vector3 position, Quaternion rotation, uint localId = ChildLocalId)
    {
        PhysicsActor pa = Scene.AddPrimShape("harness child", PrimitiveBaseShape.CreateBox(), position, size, rotation, true, localId);
        AsSimulatorAdds(pa, root: false);
        pa.Density = 1000f;
        pa.link(Actor);
        return pa;
    }

    /// <summary>The second object: a box prim, physical (density 1000) or not (a fixed wall).</summary>
    public PhysicsActor AddOtherBox(Vector3 size, Vector3 position, Quaternion rotation, bool physical, bool phantom = false)
    {
        PhysicsActor pa = AddPrimShapeAs("harness other", PrimitiveBaseShape.CreateBox(), position, size, rotation, physical, phantom, OtherLocalId);
        AsSimulatorAdds(pa);
        if (physical)
            pa.Density = 1000f;
        Other = pa;
        OtherSize = size;
        return pa;
    }

    /// <summary>A further prim with its own local id, physical or not, as a SceneObjectPart adds one (density 1000, or
    /// <paramref name="density"/>). With <paramref name="linkTo"/> it is linked to that actor as its child, as core links
    /// a physical linkset's parts. <see cref="Actor"/> is not changed.</summary>
    public PhysicsActor AddPart(PrimitiveBaseShape shape, Vector3 size, Vector3 position, Quaternion rotation, bool physical,
                                uint localId, PhysicsActor linkTo = null, float density = 1000f)
    {
        PhysicsActor pa = Scene.AddPrimShape("harness part", shape, position, size, rotation, physical, localId);
        AsSimulatorAdds(pa, root: linkTo == null);
        pa.Density = density;
        if (linkTo != null)
            pa.link(linkTo);
        return pa;
    }

    private static readonly System.Reflection.FieldInfo MesherField =
        typeof(JoltScene).GetField("m_mesher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Gives the scene a mesher, as a region hands it the region's IMesher. The harness has none otherwise, so a
    /// prim that is not a box, sphere or cylinder takes its bounding box.</summary>
    public void UseMesher(IMesher mesher) => MesherField.SetValue(Scene, mesher);

    public float GroundAt(float x, float y) => Scene.TerrainHeightAt(x, y);

    /// <summary>A physical box prim, as a SceneObjectPart adds one (density 1000).</summary>
    public PhysicsActor AddBox(Vector3 size, Vector3 position, Quaternion rotation)
    {
        position += new Vector3(Options.StartOffsetX, Options.StartOffsetY, 0f);
        PhysicsActor pa = Scene.AddPrimShape("harness box", PrimitiveBaseShape.CreateBox(), position, size, rotation, true, ActorLocalId);
        AsSimulatorAdds(pa);
        pa.Density = 1000f;
        Actor = pa;
        ActorSize = size;
        return pa;
    }

    /// <summary>A box on the ground at (x, y), its base lifted clear of a slope under its length.</summary>
    public PhysicsActor AddBoxOnGround(Vector3 size, float x, float y, Quaternion rotation)
    {
        float lift = Slope > 0f ? MathF.Max(size.X, size.Y) * 0.5f * MathF.Tan(Slope * MathF.PI / 180f) : 0f;
        return AddBox(size, new Vector3(x, y, GroundAt(x, y) + size.Z * 0.5f + lift + 0.02f), rotation);
    }

    /// <summary>An avatar arriving standing on the terrain at (x, y): the capsule centre the simulator asks for
    /// at a login on open ground, the terrain's stand height plus 1 cm.</summary>
    public PhysicsActor AddAvatar(float x, float y)
        => AddAvatarAt(new Vector3(x, y, GroundAt(x, y) + AvatarStandHalf + 0.01f), false);

    /// <summary>An avatar arriving with its capsule centre at <paramref name="position"/>, as ScenePresence hands
    /// it to AddAvatar at login, teleport, a region crossing or standing up.</summary>
    public PhysicsActor AddAvatarAt(Vector3 position, bool flying)
    {
        PhysicsActor pa = Scene.AddAvatar(ActorLocalId, "Test User", position, AvatarBox, 0f, flying);
        if (Options.SimulatorAvatars)
        {
            // ScenePresence.AddToPhysicalScene, after AddAvatar: the body's rotation (none for a new arrival), a 100 ms
            // collision subscription, and always-run as the agent last set it (off).
            pa.Orientation = Quaternion.Identity;
            pa.SubscribeEvents(100);
            pa.SetAlwaysRun = false;
        }
        Actor = pa;
        ActorSize = AvatarBox;
        return pa;
    }

    /// <summary>The vehicle's record, kept as the simulator's SceneObjectPart keeps it: from <see cref="MakeVehicle"/> on,
    /// the vehicle calls below go through it, which updates its SOPVehicle and forwards the same call to
    /// <see cref="Actor"/>. <see cref="HarnessOptions.VehicleRoute"/> restores the vehicle from it.</summary>
    public OpenSim.Region.Framework.Scenes.SceneObjectPart VehiclePart;
    /// <summary>The vehicle calls made through <see cref="VehiclePart"/>, as the actor got them, for
    /// <see cref="HarnessOptions.VehicleScriptAgain"/>.</summary>
    internal readonly List<Action<PhysicsActor>> VehicleCalls = new();

    /// <summary>The run takes the earlier material keys (see <see cref="MaterialPins"/>).</summary>
    public bool Pinned => MaterialPins.Enabled && !Options.UseDefaults;

    public void MakeVehicle(Vehicle type)
    {
        VehiclePart ??= VehicleRestore.NewRecord(Actor);
        VehiclePart.SetVehicleType((int)type);
        bool pinned = Pinned;
        VehicleCalls.Add(a => { MaterialPins.PinVehicle(a, pinned); a.VehicleType = (int)type; });
    }

    public void SetFloat(Vehicle code, float v)
    {
        if (VehiclePart == null) { Actor.VehicleFloatParam((int)code, v); return; }
        VehiclePart.SetVehicleFloatParam((int)code, v);
        VehicleCalls.Add(a => a.VehicleFloatParam((int)code, v));
    }

    public void SetVector(Vehicle code, Vector3 v)
    {
        if (VehiclePart == null) { Actor.VehicleVectorParam((int)code, v); return; }
        VehiclePart.SetVehicleVectorParam((int)code, v);
        VehicleCalls.Add(a => a.VehicleVectorParam((int)code, v));
    }

    public void SetRotation(Vehicle code, Quaternion q)
    {
        if (VehiclePart == null) { Actor.VehicleRotationParam((int)code, q); return; }
        VehiclePart.SetVehicleRotationParam((int)code, q);
        VehicleCalls.Add(a => a.VehicleRotationParam((int)code, q));
    }

    /// <summary>llSetVehicleFlags (<paramref name="remove"/> false) or llRemoveVehicleFlags (true).</summary>
    public void SetFlags(int flags, bool remove)
    {
        if (VehiclePart == null) { Actor.VehicleFlags(flags, remove); return; }
        VehiclePart.SetVehicleFlags(flags, remove);
        VehicleCalls.Add(a => a.VehicleFlags(flags, remove));
    }

    /// <summary>The motor while a key is held: re-sent every KeyRepeat seconds until the hold ends, then zeroed once.
    /// With an angular motor (a steering key held with it), that is sent with the linear one and zeroed with it.</summary>
    public void HoldMotor(Vector3 motor, Vector3? angular = null)
    {
        if (Options.Feed == InputFeed.Region)
        {
            HoldMotorAsRegion(motor, angular);
            return;
        }
        if (Released)
            return;
        if (Now >= Hold)
        {
            SetMotors(Vector3.Zero, angular.HasValue ? Vector3.Zero : null);
            Released = true;
            return;
        }
        if (Now >= NextKey)
        {
            SetMotors(motor, angular);
            NextKey += Options.KeyRepeat;
        }
    }

    private void SetMotors(Vector3 motor, Vector3? angular)
    {
        SetVector(Vehicle.LINEAR_MOTOR_DIRECTION, motor);
        if (angular.HasValue)
            SetVector(Vehicle.ANGULAR_MOTOR_DIRECTION, angular.Value);
        foreach (PhysicsActor a in AlsoDriven)
        {
            a.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, motor);
            if (angular.HasValue)
                a.VehicleVectorParam((int)Vehicle.ANGULAR_MOTOR_DIRECTION, angular.Value);
        }
    }

    // Region feed: every control event due before this heartbeat, each at its own time. The key goes down at
    // KeyStart, repeats every KeyRepeat, and comes up at ReleaseAt (= KeyStart + Hold).
    private void HoldMotorAsRegion(Vector3 motor, Vector3? angular)
    {
        while (!Released)
        {
            double next = Math.Min(NextKey, ReleaseAt);
            if (next > Now + 1e-9)
                break;
            Clock = next;
            if (next >= ReleaseAt - 1e-9)
            {
                SetMotors(Vector3.Zero, angular.HasValue ? Vector3.Zero : null);
                Released = true;
            }
            else
            {
                SetMotors(motor, angular);
                NextKey += Options.KeyRepeat;
            }
        }
    }

    public void ApplyVehicleOverrides()
    {
        foreach (VehicleParamSetting p in Options.VehicleParams)
        {
            if (p.IsVector) SetVector(p.Code, p.Value);
            else SetFloat(p.Code, p.Value.X);
        }
        foreach (VehicleFlagSetting f in Options.VehicleFlags)
            SetFlags(f.Flag, f.Remove);
    }
}

/// <summary>A named, self-contained scenario.</summary>
public sealed class Scenario
{
    public string Name;
    public string Description;
    /// <summary>Slopes "all" runs this scenario on; a single 0 means it ignores the slope.</summary>
    public float[] Slopes = { 0f };
    public bool UsesSlope;
    public Func<float, float> DefaultDuration;
    public Func<float, float> DefaultHold = _ => 0f;
    /// <summary>Builds the terrain heightmap and water for a slope.</summary>
    public Func<float, (float[] heights, float water)> World = s => (Course.Heightmap(s), Course.Water);
    public Action<Run> Setup;
    /// <summary>Called before every heartbeat, with Run.Now set to the input time.</summary>
    public Action<Run> Input = _ => { };
    /// <summary>Which samples make up the steady window.</summary>
    public Func<Run, Sample, bool> Steady;
    /// <summary>Instead of <see cref="Steady"/>: the steady speeds (along the path, horizontal) from positions.</summary>
    public Func<Run, List<Sample>, (float along, float horizontal)?> SteadyFromPath;
    /// <summary>A collision scenario: the gap between the surfaces that meet (m; below zero they overlap).</summary>
    public Func<Run, Sample, float> Gap;
    /// <summary>A collision scenario: true once the object has passed through what it hit.</summary>
    public Func<Run, Sample, bool> PassedThrough;
    /// <summary>A shot scenario: it takes the ball's diameter and speed from the options, and its run is named by them.</summary>
    public bool UsesShot;
    /// <summary>The scenario can take its ground from <see cref="HarnessOptions.TerrainFile"/>.</summary>
    public bool AllowsTerrainFile;
}

public static class Harness
{
    public static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    private static readonly Quaternion South = Quaternion.CreateFromEulers(0f, 0f, -MathF.PI / 2f);
    private static readonly Quaternion North = Quaternion.CreateFromEulers(0f, 0f, MathF.PI / 2f);
    private const float AvatarWalk = 4.096f;   // ScenePresence.AgentControlNormalVel at speed modifier 1
    // What ScenePresence.AddNewMovement asks for while flying: the walk request times 4.
    private const float AvatarFly = AvatarWalk * 4f;
    public const float PushAvatarSpeed = 6f;     // push-avatar: the speed its one push gives
    public const float PlatformHeight = 3f;      // avatar-platform: the platform's top above the ground
    public const float PlatformDrop = 3f;        // avatar-platform-drop: the arrival's height above the platform

    /// <summary>The avatar-platform platform: a fixed 4 x 4 x 0.5 m box centred on (170, 60), its top
    /// <see cref="PlatformHeight"/> above the ground. Returns the top's height.</summary>
    public static float AddPlatform(Run r)
    {
        float top = r.GroundAt(170f, 60f) + PlatformHeight;
        r.AddOtherBox(new Vector3(4f, 4f, 0.5f), new Vector3(170f, 60f, top - 0.25f), Quaternion.Identity, false);
        return top;
    }
    public const int RayRowBoxes = 400;          // raycast-cost
    public const int RayCastsPerHeartbeat = 50;

    // The test car: VEHICLE_TYPE_CAR with a test-drive script's three params, motor <8,0,0> while the key is held.
    private static readonly Vector3 CarSize = new(2f, 1f, 0.5f);
    private static readonly Vector3 CarMotor = new(8f, 0f, 0f);
    private static readonly Vector3 CarTurn = new(0f, 0f, 1f);

    // raycast-cost: the row, and a physical box beside it so the region has a body awake.
    private static void SetupRayRow(Run r)
    {
        for (int i = 0; i < RayRowBoxes; i++)
            r.AsSimulatorAdds(r.Scene.AddPrimShape("harness row", PrimitiveBaseShape.CreateBox(), new Vector3(20f + i * 0.5f, 128f, Course.Ground + 1f),
                                                   new Vector3(0.25f, 0.5f, 0.5f), Quaternion.Identity, false, 2000u + (uint)i));
        r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 100f, Course.Ground + 0.52f), Quaternion.Identity);
    }

    // raycast-cost: cast i of a heartbeat is 50 m long for the first and 300 m for the last.
    private static float RayLength(int i) => 50f + 250f * i / (RayCastsPerHeartbeat - 1);

    private static void SetupCar(Run r, bool testCar, int flags = 0)
    {
        bool region = r.Options.Feed == InputFeed.Region;
        float x = r.Slope > 0f ? 128f : 165f, y = r.Slope > 0f ? 120f : 85f;   // top run-out facing down the slope, or level ground facing east
        Quaternion facing = r.Slope > 0f ? South : Quaternion.Identity;
        if (region)
            r.AddBox(CarSize, new Vector3(x, y, r.GroundAt(x, y) + CarSize.Z), facing);
        else
            r.AddBoxOnGround(CarSize, x, y, facing);
        r.MakeVehicle(Vehicle.TYPE_CAR);
        if (testCar)
        {
            r.SetVector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
            r.SetFloat(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            r.SetFloat(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 0.5f);
        }
        if (flags != 0)
            r.SetFlags(flags, false);
        r.ApplyVehicleOverrides();
        r.NextKey = region ? r.Options.KeyDelay : r.Options.StartSpeed.HasValue ? r.Dt : 0.0;
        r.ReleaseAt = r.NextKey + r.Hold;
        r.StopAtRest = true;
    }

    /// <summary>Region feed's default key delay. A script's llSetStatus(STATUS_PHYSICS, TRUE) makes the body
    /// dynamic at once (JoltPrim.IsPhysical), so the drop starts at the next heartbeat's step; a driver who
    /// presses the key as soon as the script says physics is on (about 20 ms later on a test course) lands the
    /// first control event before that step, most of the time. So the key and the drop start together.</summary>
    public const float RegionKeyDelay = 0f;

    // On a slope: the lower part of the ramp, a second after the release. On level ground: the last second of the hold.
    private static bool DriveSteady(Run r, Sample s)
        => r.Slope > 0f
            ? s.T >= r.ReleaseAt + 1.0 && s.Position.Y >= 45f && s.Position.Y <= 70f
            : s.T > r.ReleaseAt - 1.0 && s.T <= r.ReleaseAt;

    private static bool LastSeconds(Run r, Sample s, double seconds) => s.T > r.Duration - seconds;

    /// <summary>The lower part of the ramp, where a car driven down it from the top has settled.</summary>
    public static bool OnLowerRamp(Run r, Sample s) => s.Position.Y >= 45f && s.Position.Y <= 70f;

    // Walking speed from positions, as the course's walk runs measured it: on a slope the time from y 40 to
    // y 100 gives the horizontal speed, and along the slope is that over cos(angle); on level ground the
    // distance covered from t = 1 s to the end. The avatar's reported velocity is the walk it was asked for,
    // not its motion over the ground, so it is not used here.
    private static (float along, float horizontal)? WalkSpeed(Run r, List<Sample> samples)
    {
        if (r.Slope <= 0f)
        {
            int i = samples.FindIndex(s => s.T >= 1.0);
            if (i < 0 || samples[^1].T <= samples[i].T)
                return null;
            float d = Horizontal(samples[^1].Position, samples[i].Position);
            float v = (float)(d / (samples[^1].T - samples[i].T));
            return (v, v);
        }
        double t40 = CrossingY(samples, Course.RampBottomY), t100 = CrossingY(samples, Course.RampTopY);
        if (double.IsNaN(t40) || double.IsNaN(t100) || t100 <= t40)
            return null;
        float h = (float)((Course.RampTopY - Course.RampBottomY) / (t100 - t40));
        return (h / MathF.Cos(r.Slope * MathF.PI / 180f), h);
    }

    // A straight-up or straight-down flight: the vertical speed from t = 1 s to the end (along), and the horizontal
    // drift over the same time (horizontal), which should stay near zero.
    private static (float along, float horizontal)? VerticalSpeed(Run r, List<Sample> samples)
    {
        int i = samples.FindIndex(s => s.T >= 1.0);
        if (i < 0 || samples[^1].T <= samples[i].T)
            return null;
        double dt = samples[^1].T - samples[i].T;
        float v = (float)(MathF.Abs(samples[^1].Position.Z - samples[i].Position.Z) / dt);
        float h = (float)(Horizontal(samples[^1].Position, samples[i].Position) / dt);
        return (v, h);
    }

    /// <summary>Interpolated time at which y first reaches the given value going north; NaN if never.</summary>
    public static double CrossingY(List<Sample> samples, float y)
    {
        for (int i = 1; i < samples.Count; i++)
        {
            Sample a = samples[i - 1], b = samples[i];
            if (a.Position.Y < y && b.Position.Y >= y)
                return a.T + (b.T - a.T) * (y - a.Position.Y) / (b.Position.Y - a.Position.Y);
        }
        return double.NaN;
    }

    public static readonly IReadOnlyList<Scenario> Scenarios = new List<Scenario>
    {
        new()
        {
            Name = "car",
            Description = "VEHICLE_TYPE_CAR with its presets only; motor <8,0,0> while the key is held, then released. Level ground: 3 s from rest facing east. Slope: 6 s from the top facing down.",
            Slopes = new[] { 0f, 5f, 15f, 33f }, UsesSlope = true,
            DefaultDuration = s => s > 0f ? 90f : 20f, DefaultHold = s => s > 0f ? 6f : 3f,
            Setup = r => SetupCar(r, false),
            Input = r => r.HoldMotor(CarMotor),
            Steady = DriveSteady,
        },
        new()
        {
            Name = "testcar",
            Description = "A typical test-drive script's car: as car, plus linear friction timescale <1,1,1000>, motor timescale 1, motor decay 0.5.",
            Slopes = new[] { 0f, 5f, 15f, 33f }, UsesSlope = true,
            DefaultDuration = s => s > 0f ? 90f : 20f, DefaultHold = s => s > 0f ? 6f : 3f,
            Setup = r => SetupCar(r, true),
            Input = r => r.HoldMotor(CarMotor),
            Steady = DriveSteady,
        },
        new()
        {
            Name = "testcar-down",
            Description = "The test car from the top run-out down the ramp with its key held all the way; steady = the lower ramp (y 45 to 70). On 33 degrees it leaves the crest and flies.",
            Slopes = new[] { 5f, 15f, 33f }, UsesSlope = true,
            DefaultDuration = _ => 40f, DefaultHold = _ => 1000f,
            Setup = r => SetupCar(r, true),
            Input = r => r.HoldMotor(CarMotor),
            Steady = OnLowerRamp,
        },
        new()
        {
            Name = "car-down",
            Description = "As testcar-down, with the car preset only.",
            Slopes = new[] { 5f, 15f }, UsesSlope = true,
            DefaultDuration = _ => 40f, DefaultHold = _ => 1000f,
            Setup = r => SetupCar(r, false),
            Input = r => r.HoldMotor(CarMotor),
            Steady = OnLowerRamp,
        },
        new()
        {
            Name = "carturn",
            Description = "VEHICLE_TYPE_CAR with its presets on level ground facing east: motor <8,0,0> and angular motor <0,0,1> (a forward and a turn key) held for 3 s, then both released.",
            DefaultDuration = _ => 20f, DefaultHold = _ => 3f,
            Setup = r => SetupCar(r, false),
            Input = r => r.HoldMotor(CarMotor, CarTurn),
            Steady = DriveSteady,
        },
        new()
        {
            Name = "sled",
            Description = "VEHICLE_TYPE_SLED with its presets, let go at rest on the slope facing down; no motor.",
            Slopes = new[] { 15f }, UsesSlope = true,
            DefaultDuration = _ => 40f,
            Setup = r =>
            {
                r.AddBoxOnGround(CarSize, 128f, 90f, South);
                r.MakeVehicle(Vehicle.TYPE_SLED);
                r.ApplyVehicleOverrides();
                r.ReleaseAt = 0;
                r.StopAtRest = true;
            },
            Steady = DriveSteady,
        },
        new()
        {
            Name = "boat",
            Description = "VEHICLE_TYPE_BOAT with its presets on 10 m of water; motor <5,0,0> for 5 s, then released.",
            DefaultDuration = _ => 30f, DefaultHold = _ => 5f,
            World = _ => (Course.Heightmap(0f, 10f), Course.Water),
            Setup = r =>
            {
                r.AddBox(new Vector3(3f, 1.5f, 0.5f), new Vector3(60f, 128f, Course.Water + 0.5f), Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_BOAT);
                r.ApplyVehicleOverrides();
                r.ReleaseAt = r.Hold;
                r.StopAtRest = true;
            },
            Input = r => r.HoldMotor(new Vector3(5f, 0f, 0f)),
            Steady = (r, s) => s.T > r.ReleaseAt - 1.0 && s.T <= r.ReleaseAt,
        },
        new()
        {
            Name = "airplane",
            Description = "VEHICLE_TYPE_AIRPLANE with its presets, 40 m above level ground, motor <15,0,0> held for the whole run.",
            DefaultDuration = _ => 15f, DefaultHold = s => 1000f,
            Setup = r =>
            {
                r.AddBox(new Vector3(3f, 3f, 0.5f), new Vector3(30f, 128f, Course.Ground + 40f), Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_AIRPLANE);
                r.ApplyVehicleOverrides();
            },
            Input = r => r.HoldMotor(new Vector3(15f, 0f, 0f)),
            Steady = (r, s) => LastSeconds(r, s, 2.0),
        },
        new()
        {
            Name = "balloon",
            Description = "VEHICLE_TYPE_BALLOON with its presets (hover 5 m), from rest on level ground; no motor.",
            DefaultDuration = _ => 40f,
            Setup = r =>
            {
                r.AddBoxOnGround(new Vector3(1f, 1f, 1f), 128f, 128f, Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_BALLOON);
                r.ApplyVehicleOverrides();
            },
            Steady = (r, s) => LastSeconds(r, s, 2.0),
        },
        new()
        {
            Name = "hover",
            Description = "VEHICLE_TYPE_BALLOON (buoyancy 1, hover 5 m, timescale 10 s, efficiency 0.8) from rest on level ground, with no friction on its vertical axis, so hover alone lifts it; no motor.",
            DefaultDuration = _ => 40f,
            Setup = r =>
            {
                r.AddBoxOnGround(new Vector3(1f, 1f, 1f), 128f, 128f, Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_BALLOON);
                r.SetVector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
                r.ApplyVehicleOverrides();
            },
            Steady = (r, s) => LastSeconds(r, s, 2.0),
        },
        new()
        {
            Name = "attract-roll",
            Description = "A 1 m box made VEHICLE_TYPE_BALLOON (buoyancy 1) 15 m up, with hover, banking and angular friction off, rolled 30 degrees at 2 s (the release): the vertical attractor alone rights it.",
            DefaultDuration = _ => 30f,
            Setup = SetupAttract,
            Input = r => Tilt(r, Quaternion.CreateFromEulers(AttractTilt, 0f, 0f)),
        },
        new()
        {
            Name = "attract-pitch",
            Description = "As attract-roll, pitched 30 degrees nose down instead.",
            DefaultDuration = _ => 30f,
            Setup = SetupAttract,
            Input = r => Tilt(r, Quaternion.CreateFromEulers(0f, AttractTilt, 0f)),
        },
        new()
        {
            Name = "rollonly",
            Description = "A car-sized box made VEHICLE_TYPE_CAR (buoyancy 1) 15 m up, with LIMIT_ROLL_ONLY, attractor 2 s / 1, everything else off, rolled 20 and pitched 30 degrees nose down at 2 s: the attractor rights the roll and leaves the pitch.",
            DefaultDuration = _ => 20f,
            Setup = r =>
            {
                r.ReleaseAt = AttractStart;
                r.AddBox(CarSize, new Vector3(128f, 128f, Course.Ground + 15f), Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_CAR);
                SetFloatingAlone(r);
                r.SetFloat(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 2f);
                r.SetFloat(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
                r.SetFlags(LimitRollOnlyFlag, false);
                r.ApplyVehicleOverrides();
            },
            // Rolled about its own x axis, then pitched about the world's y: the nose is 30 degrees down.
            Input = r => Tilt(r, Quaternion.CreateFromAxisAngle(Vector3.UnitY, AttractTilt) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20f * MathF.PI / 180f)),
        },
        new()
        {
            Name = "motor-offset",
            Description = "A car-sized box made VEHICLE_TYPE_CAR (buoyancy 1) 30 m up, everything but the linear motor off (LIMIT_MOTOR_UP removed), motor <5,0,0> (timescale 1) pushing 5 cm below the centre of mass (LINEAR_MOTOR_OFFSET <0,0,-0.05>) for the whole run: it pitches its nose up.",
            DefaultDuration = _ => 2f, DefaultHold = _ => 1000f,
            Setup = r =>
            {
                r.AddBox(CarSize, new Vector3(100f, 128f, Course.Ground + 30f), Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_CAR);
                SetFloatingAlone(r);
                r.SetVector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
                r.SetFloat(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
                r.SetVector(Vehicle.LINEAR_MOTOR_OFFSET, new Vector3(0f, 0f, -0.05f));
                r.SetFlags(LimitMotorUpFlag, true);
                r.ApplyVehicleOverrides();
            },
            Input = r => r.HoldMotor(new Vector3(5f, 0f, 0f)),
        },
        new()
        {
            Name = "bank-turn",
            Description = "VEHICLE_TYPE_AIRPLANE with its presets (banking efficiency 1, mix 0.7, timescale 2 s), held up by buoyancy 1, 60 m up facing east, motor <15,0,0> held for the whole run; at 2 s (the release) rolled 20 degrees right and pitched 30 degrees nose up: banking turns it right while the attractor rights the roll (LIMIT_ROLL_ONLY keeps the pitch).",
            DefaultDuration = _ => 8f, DefaultHold = _ => 1000f,
            Setup = r =>
            {
                SetupBankingAirplane(r, 0);
                r.ReleaseAt = AttractStart;
            },
            Input = r =>
            {
                r.HoldMotor(new Vector3(15f, 0f, 0f));
                if (r.Stage == 0 && r.Now >= AttractStart - 1e-9)
                {
                    // Rolled about its own x axis, then pitched about the world's y (negative: nose up).
                    r.Actor.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -BankTurnPitch) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, BankTurnRoll);
                    r.Stage = 1;
                }
            },
            Steady = (r, s) => LastSeconds(r, s, 2.0),
        },
        new()
        {
            Name = "mouselook-turn",
            Description = "VEHICLE_TYPE_CAR with its presets on level ground facing east, VEHICLE_FLAG_MOUSELOOK_STEER, the driver's camera looking north (90 degrees left): motor <8,0,0> and angular motor <0,0,1.5> (the fastest it may turn) held for 6 s, then both released. The car turns left until it faces the camera, and drives on north.",
            DefaultDuration = _ => 15f, DefaultHold = _ => 6f,
            Setup = r =>
            {
                SetupCar(r, false, MouselookSteerFlag);
                DriverCamera(r, Vector3.UnitY);
            },
            Input = r => r.HoldMotor(CarMotor, new Vector3(0f, 0f, 1.5f)),
            Steady = DriveSteady,
        },
        new()
        {
            Name = "mouselook-hold",
            Description = "VEHICLE_TYPE_CAR with its presets on level ground facing east, VEHICLE_FLAG_MOUSELOOK_STEER, the driver's camera looking along the car's heading (east), shaking as a hand on a mouse does when --camera-noise is given: motor <3,0,0> (slow enough to stay inside the region for 25 s) and angular motor <0,0,1.5> held. For measuring whether the steering hunts.",
            DefaultDuration = _ => 25f, DefaultHold = _ => 1000f,
            Setup = r =>
            {
                SetupCar(r, false, MouselookSteerFlag);
                DriverCamera(r, Vector3.UnitX);
            },
            Input = r => r.HoldMotor(new Vector3(3f, 0f, 0f), new Vector3(0f, 0f, 1.5f)),
            Steady = (r, s) => LastSeconds(r, s, 5.0),
        },
        new()
        {
            Name = "mouselook-bank",
            Description = "VEHICLE_TYPE_AIRPLANE with its presets, held up by buoyancy 1, 60 m up facing east, VEHICLE_FLAG_MOUSELOOK_BANK, the driver's camera looking 60 degrees right of east: motor <15,0,0> and angular motor <0.5,0,0> (the fastest it may roll) held for the whole run. The camera's yaw becomes a roll to the right, and banking turns the airplane right toward the camera.",
            DefaultDuration = _ => 15f, DefaultHold = _ => 1000f,
            Setup = r =>
            {
                SetupBankingAirplane(r, MouselookBankFlag);
                DriverCamera(r, new Vector3(MathF.Cos(-MathF.PI / 3f), MathF.Sin(-MathF.PI / 3f), 0f));
            },
            Input = r => r.HoldMotor(new Vector3(15f, 0f, 0f), new Vector3(0.5f, 0f, 0f)),
            Steady = (r, s) => LastSeconds(r, s, 2.0),
        },
        new()
        {
            Name = "avatar-stand",
            Description = "An avatar standing still for 10 s on the slope (y 70).",
            Slopes = new[] { 5f, 15f }, UsesSlope = true,
            DefaultDuration = _ => 10f,
            Setup = r => r.AddAvatar(128f, 70f),
            Steady = (r, s) => s.T >= 1.0,
        },
        new()
        {
            Name = "avatar-platform",
            Description = "An avatar arrives standing on a fixed 4 x 4 x 0.5 m platform whose top is 3 m above level ground (170, 60), and stands 10 s.",
            DefaultDuration = _ => 10f,
            Setup = r => r.AddAvatarAt(new Vector3(170f, 60f, AddPlatform(r) + r.AvatarStandHalf), false),
            Steady = (r, s) => s.T >= 1.0,
        },
        new()
        {
            Name = "avatar-platform-drop",
            Description = "An avatar, not flying, arrives 3 m above the avatar-platform platform, falls and lands on it.",
            DefaultDuration = _ => 10f,
            Setup = r => r.AddAvatarAt(new Vector3(170f, 60f, AddPlatform(r) + PlatformDrop + r.AvatarStandHalf), false),
            Steady = (r, s) => s.T >= 3.0,
        },
        new()
        {
            Name = "avatar-walk",
            Description = "An avatar walking at the scene's walk speed (4.096 m/s asked). Level ground: east for 5 s. Slope: north from y 24 up the ramp to y 110.",
            Slopes = new[] { 0f, 5f, 15f, 33f }, UsesSlope = true,
            DefaultDuration = s => s > 0f ? 40f : 5f,
            Setup = r =>
            {
                if (r.Slope > 0f) r.AddAvatar(128f, 24f);
                else r.AddAvatar(170f, 60f);
                float walk = AvatarWalk * r.Options.AvatarSpeedModifier;
                r.Actor.TargetVelocity = r.Slope > 0f ? new Vector3(0f, walk, 0f) : new Vector3(walk, 0f, 0f);
            },
            Input = r =>
            {
                if (r.Slope > 0f && r.Actor.Position.Y >= 110f && r.Actor.TargetVelocity != Vector3.Zero)
                {
                    r.Actor.TargetVelocity = Vector3.Zero;   // at the top: stop, and end the run a second later
                    r.ReleaseAt = r.Now;
                    r.StopAt = r.Now + 1.0;
                }
            },
            SteadyFromPath = WalkSpeed,
        },
        new()
        {
            Name = "avatar-run",
            Description = "An avatar running east on level ground for 5 s: always run on, and the walk request (4.096 m/s asked), as ScenePresence sends both.",
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                r.AddAvatar(170f, 60f);
                r.Actor.SetAlwaysRun = true;
                r.Actor.TargetVelocity = new Vector3(AvatarWalk * r.Options.AvatarSpeedModifier, 0f, 0f);
            },
            SteadyFromPath = WalkSpeed,
        },
        new()
        {
            Name = "avatar-fly",
            Description = "An avatar flying east, straight and level, 30 m above level ground for 5 s (16.384 m/s asked).",
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                r.AddAvatarAt(new Vector3(60f, 60f, Course.Ground + 30f), true);
                r.Actor.TargetVelocity = new Vector3(AvatarFly * r.Options.AvatarSpeedModifier, 0f, 0f);
            },
            SteadyFromPath = WalkSpeed,
        },
        new()
        {
            Name = "avatar-fly-up",
            Description = "An avatar flying straight up for 5 s from 30 m above level ground (16.384 m/s asked, as for flying level).",
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                r.AddAvatarAt(new Vector3(60f, 60f, Course.Ground + 30f), true);
                r.Actor.TargetVelocity = new Vector3(0f, 0f, AvatarFly * r.Options.AvatarSpeedModifier);
            },
            SteadyFromPath = VerticalSpeed,
        },
        new()
        {
            Name = "avatar-fly-down",
            Description = "An avatar flying straight down for 5 s from 300 m above level ground, high enough for twice the speed (16.384 m/s asked; the viewer sends the same fast flag going down as going up).",
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                r.AddAvatarAt(new Vector3(60f, 60f, Course.Ground + 300f), true);
                r.Actor.TargetVelocity = new Vector3(0f, 0f, -AvatarFly * r.Options.AvatarSpeedModifier);
            },
            SteadyFromPath = VerticalSpeed,
        },
        new()
        {
            Name = "avatar-jump",
            Description = "An avatar standing on level ground jumps at t = 1 s.",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                r.AddAvatar(170f, 60f);
                r.ReleaseAt = 1.0;
            },
            Input = r =>
            {
                if (!r.Released && r.Now >= r.ReleaseAt)
                {
                    r.Actor.AvatarJump(0f);
                    r.Released = true;
                }
            },
        },
        // Pushes (llPushObject reaches an avatar's or a prim's actor as AddForce with an impulse).
        new()
        {
            Name = "push-avatar",
            Description = "An avatar standing on level ground is pushed straight up once at t = 1 s, with the impulse that gives its 80 kg 6 m/s.",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                r.AddAvatar(170f, 60f);
                r.ReleaseAt = 1.0;
            },
            Input = r =>
            {
                if (!r.Released && r.Now >= r.ReleaseAt)
                {
                    r.Actor.AddForce(new Vector3(0f, 0f, PushAvatarSpeed * r.Actor.Mass), true);
                    r.Released = true;
                }
            },
        },
        new()
        {
            Name = "push-flood",
            Description = "An avatar standing on level ground is pushed up and east with an impulse of 1e9 every heartbeat from t = 1 s to t = 7 s.",
            DefaultDuration = _ => 10f,
            Setup = r =>
            {
                r.AddAvatar(60f, 128f);
                r.ReleaseAt = 1.0;
            },
            Input = r =>
            {
                if (r.Now >= r.ReleaseAt && r.Now < 7.0)
                    r.Actor.AddForce(new Vector3(1e9f, 0f, 1e9f), true);
            },
        },
        new()
        {
            Name = "push-car",
            Description = "VEHICLE_TYPE_CAR with its presets parks on level ground (it sleeps); at t = 5 s it is pushed east with the impulse that gives it 2 m/s.",
            DefaultDuration = _ => 10f,
            Setup = r =>
            {
                SetupParked(r, false);
                r.ReleaseAt = 5.0;
            },
            Input = r =>
            {
                if (!r.Released && r.Now >= r.ReleaseAt)
                {
                    r.Actor.AddForce(new Vector3(2f * r.Actor.Mass, 0f, 0f), false);   // llPushObject on an object: SOP.ApplyImpulse
                    r.Released = true;
                }
            },
        },
        new()
        {
            Name = "push-box",
            Description = "A 1 m physical box let fall 1 m onto level ground (it settles and sleeps); at t = 5 s it is pushed east with the impulse that gives it 2 m/s.",
            DefaultDuration = _ => 10f,
            Setup = r =>
            {
                r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 128f, Course.Ground + 1f + 0.5f), Quaternion.Identity);
                r.ReleaseAt = 5.0;
            },
            Input = r =>
            {
                if (!r.Released && r.Now >= r.ReleaseAt)
                {
                    r.Actor.AddForce(new Vector3(2f * r.Actor.Mass, 0f, 0f), false);
                    r.Released = true;
                }
            },
        },

        // Script ray casts: what one costs (the summary's ray_casts, ray_us_per_cast, ray_refused, ray_cut_short).
        new()
        {
            Name = "raycast-cost",
            Description = "A row of 400 non-physical 0.5 m boxes along y 128 (x 20 to 220); every heartbeat 50 casts along the row the way llCastRay makes them (the filtered RaycastWorld, up to 16 hits, all types), from x 10 to x 10 + 50 .. 300 m.",
            DefaultDuration = _ => 5f,
            Setup = SetupRayRow,
            Input = r =>
            {
                for (int i = 0; i < RayCastsPerHeartbeat; i++)
                    r.Scene.RaycastWorld(new Vector3(10f, 128f, Course.Ground + 1f), Vector3.UnitX, RayLength(i), 16,
                        RayFilterFlags.land | RayFilterFlags.agent | RayFilterFlags.physical | RayFilterFlags.nonphysical | RayFilterFlags.BackFaceCull);
            },
        },
        new()
        {
            Name = "raycast-cost-all",
            Description = "raycast-cost's row and casts, made the way llCastRay's casts were made before their cost was bounded (every hit along the ray, then the closest 16), timed here.",
            DefaultDuration = _ => 5f,
            Setup = SetupRayRow,
            Input = r =>
            {
                var hits = new Backend.RayHit[16];
                for (int i = 0; i < RayCastsPerHeartbeat; i++)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    r.Scene.Backend.RayCastAll(new System.Numerics.Vector3(10f, 128f, Course.Ground + 1f), System.Numerics.Vector3.UnitX, RayLength(i),
                        Backend.QueryFilter.Default, hits);
                    r.TimedRayTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    r.TimedRayCasts++;
                }
            },
        },
        new()
        {
            Name = "drop",
            Description = "A 1 m physical box let fall from 5 m above level ground, until it settles.",
            DefaultDuration = _ => 15f,
            Setup = r =>
            {
                r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 128f, Course.Ground + 5f + 0.5f), Quaternion.Identity);
                r.ReleaseAt = 0;
                r.StopAtRest = true;
            },
        },

        // Parked vehicles: does the engine let them sleep (the summary's sleep_after and active_at_end)?
        new()
        {
            Name = "park-new",
            Description = "VEHICLE_TYPE_CAR with its presets, at rest on level ground; no motor is ever set.",
            DefaultDuration = _ => 20f,
            Setup = r => SetupParked(r, false),
        },
        new()
        {
            Name = "park-faded",
            Description = "The test car on level ground; its motor is set to <8,0,0> once at the start and never again, so it fades.",
            DefaultDuration = _ => 20f,
            Setup = r => SetupParked(r, true),
            Input = r =>
            {
                if (!r.Released)
                {
                    r.SetVector(Vehicle.LINEAR_MOTOR_DIRECTION, CarMotor);
                    r.Released = true;
                }
            },
        },
        new()
        {
            Name = "park-drive",
            Description = "The test car driven on level ground as testcar (3 s key), then left parked to the end of the run.",
            DefaultDuration = _ => 40f, DefaultHold = _ => 3f,
            Setup = r =>
            {
                SetupCar(r, true);
                r.StopAtRest = false;
            },
            Input = r => r.HoldMotor(CarMotor),
        },
        new()
        {
            Name = "park-car",
            Description = "VEHICLE_TYPE_CAR with its presets driven on level ground as car (3 s key), then left parked to the end of the run.",
            DefaultDuration = _ => 20f, DefaultHold = _ => 3f,
            Setup = r =>
            {
                SetupCar(r, false);
                r.StopAtRest = false;
            },
            Input = r => r.HoldMotor(CarMotor),
        },

        // A test-drive script's car dropped onto level ground, driven, released and left to the end of the run.
        new()
        {
            Name = "drop-drive-script",
            Description = "A script-set car (VEHICLE_TYPE_CAR, attractor 0.8 / 1 s, LIMIT_ROLL_ONLY removed, deflection, banking, hover and buoyancy 0, sideways friction 0.6 s, forward 12 s) dropped 0.4 m onto level ground; motor <7,0,0> for 6 s, then released and left for 30 s.",
            DefaultDuration = _ => 36f, DefaultHold = _ => 6f,
            Setup = r => SetupDropDrive(r, false),
            Input = r => r.HoldMotor(GarageMotor),
        },
        new()
        {
            Name = "drop-drive-script-link",
            Description = "As drop-drive-script, the car a linkset of the sports car's seven parts (a cabin, four wheel cylinders, a headlight and a spoiler on the root box) in place of one box.",
            DefaultDuration = _ => 36f, DefaultHold = _ => 6f,
            Setup = r => SetupDropDrive(r, false, true),
            Input = r => r.HoldMotor(GarageMotor),
        },
        new()
        {
            Name = "drop-drive-script-old",
            Description = "As drop-drive-script with the earlier script's values: attractor 0.6 / 1.5 s, LIMIT_ROLL_ONLY kept, sideways friction 2 s, the type's deflection, banking and hover presets.",
            DefaultDuration = _ => 36f, DefaultHold = _ => 6f,
            Setup = r => SetupDropDrive(r, true),
            Input = r => r.HoldMotor(GarageMotor),
        },
        new()
        {
            Name = "drop-drive-script-old-link",
            Description = "As drop-drive-script-old, the car a linkset of the sports car's seven parts (a cabin, four wheel cylinders, a headlight and a spoiler on the root box) in place of one box.",
            DefaultDuration = _ => 36f, DefaultHold = _ => 6f,
            Setup = r => SetupDropDrive(r, true, true),
            Input = r => r.HoldMotor(GarageMotor),
        },
        new()
        {
            Name = "drop-drive-preset",
            Description = "A VEHICLE_TYPE_CAR with its presets only, dropped 0.4 m onto level ground; motor <7,0,0> for 6 s, then released and left for 30 s.",
            DefaultDuration = _ => 36f, DefaultHold = _ => 6f,
            Setup = r => SetupDropDrive(r, null),
            Input = r => r.HoldMotor(GarageMotor),
        },
        new()
        {
            Name = "drop-drive-preset-link",
            Description = "As drop-drive-preset, the car a linkset of the sports car's seven parts (a cabin, four wheel cylinders, a headlight and a spoiler on the root box) in place of one box.",
            DefaultDuration = _ => 36f, DefaultHold = _ => 6f,
            Setup = r => SetupDropDrive(r, null, true),
            Input = r => r.HoldMotor(GarageMotor),
        },
        new()
        {
            Name = "drop-rock-old-link",
            Description = "The sports car linkset let fall 1.5 m onto level ground rolled 7 and pitched 5 degrees (as if landing on a slope or a bump), with the earlier test-drive script's values; no key; left 30 s.",
            DefaultDuration = _ => 30f, DefaultHold = _ => 0f,
            Setup = r => SetupDropDrive(r, true, true, 1.5f, 7f, 5f, false),
        },
        new()
        {
            Name = "drop-rock-script-link",
            Description = "The sports car linkset let fall 1.5 m onto level ground rolled 7 and pitched 5 degrees (as if landing on a slope or a bump), with the current test-drive script's values; no key; left 30 s.",
            DefaultDuration = _ => 30f, DefaultHold = _ => 0f,
            Setup = r => SetupDropDrive(r, false, true, 1.5f, 7f, 5f, false),
        },
        new()
        {
            Name = "drop-rock-old-sitter-link",
            Description = "The sports car linkset with a 70 kg block at the driver's sit offset let fall 1.5 m onto level ground rolled 7 and pitched 5 degrees (as if landing on a slope or a bump), with the earlier test-drive script's values; no key; left 30 s.",
            DefaultDuration = _ => 30f, DefaultHold = _ => 0f,
            Setup = r => SetupDropDrive(r, true, true, 1.5f, 7f, 5f, true),
        },
        new()
        {
            Name = "drop-rock-script-sitter-link",
            Description = "The sports car linkset with a 70 kg block at the driver's sit offset let fall 1.5 m onto level ground rolled 7 and pitched 5 degrees (as if landing on a slope or a bump), with the current test-drive script's values; no key; left 30 s.",
            DefaultDuration = _ => 30f, DefaultHold = _ => 0f,
            Setup = r => SetupDropDrive(r, false, true, 1.5f, 7f, 5f, true),
        },
        new()
        {
            Name = "park-wake",
            Description = "VEHICLE_TYPE_CAR with its presets, parked on level ground; at 8 s (asleep by then) the key goes down for 2 s with motor <8,0,0>.",
            // The heartbeat feed lets the key up at Hold, counted from the start.
            DefaultDuration = _ => 14f, DefaultHold = _ => WakeKeyAt + 2f,
            Setup = r =>
            {
                SetupParked(r, false);
                r.NextKey = WakeKeyAt;
                r.ReleaseAt = r.Hold;
            },
            Input = r => r.HoldMotor(CarMotor),
        },

        // Collisions: the car preset with its key held (motor <20,0,0>) from rest 28 m away from what it hits.
        new()
        {
            Name = "crash-wall",
            Description = "VEHICLE_TYPE_CAR with its presets, motor <20,0,0> held, into a fixed wall (1 m thick), then held against it.",
            DefaultDuration = _ => 15f, DefaultHold = _ => 1000f,
            Setup = r =>
            {
                SetupCrashCar(r);
                r.AddOtherBox(new Vector3(1f, 20f, 3f), new Vector3(CrashTargetX, 85f, Course.Ground + 1.5f), Quaternion.Identity, false);
            },
            Input = r => r.HoldMotor(CrashMotorOf(r)),
            Gap = GapToOther,
            PassedThrough = PassedThroughOther,
        },
        new()
        {
            Name = "crash-box",
            Description = "As crash-wall, into a 1 m box at rest on the ground, of about the car's mass (1000 kg each).",
            DefaultDuration = _ => 8f, DefaultHold = _ => 1000f,
            Setup = r =>
            {
                SetupCrashCar(r);
                r.AddOtherBox(new Vector3(1f, 1f, 1f), new Vector3(CrashTargetX, 85f, Course.Ground + 0.5f + 0.02f), Quaternion.Identity, true);
            },
            Input = r => r.HoldMotor(CrashMotorOf(r)),
            Gap = GapToOther,
            PassedThrough = PassedThroughOther,
        },
        new()
        {
            Name = "crash-headon",
            Description = "Two VEHICLE_TYPE_CAR presets 60 m apart facing each other, both with motor <20,0,0> held.",
            DefaultDuration = _ => 8f, DefaultHold = _ => 1000f,
            Setup = r =>
            {
                SetupCrashCar(r);
                r.AddOtherBox(CarSize, new Vector3(CrashStartX + 60f, 85f, Course.Ground + CarSize.Z * 0.5f + 0.02f), West, true);
                MaterialPins.PinVehicle(r.Other, r.Pinned);
                r.Other.VehicleType = (int)Vehicle.TYPE_CAR;
                r.AlsoDriven.Add(r.Other);
            },
            Input = r => r.HoldMotor(CrashMotorOf(r)),
            Gap = GapToOther,
            PassedThrough = PassedThroughOther,
        },
        new()
        {
            Name = "crash-drop",
            Description = "VEHICLE_TYPE_CAR with its presets held level 2 m above level ground (buoyancy 1) and let fall at 2 s (buoyancy 0); no motor.",
            DefaultDuration = _ => 7f,
            Setup = r =>
            {
                float drop = 2f * r.Options.CrashSpeed * r.Options.CrashSpeed;
                r.AddBox(CarSize, new Vector3(128f + r.Options.CrashOffset, 128f, Course.Ground + CarSize.Z * 0.5f + drop),
                    Quaternion.CreateFromEulers(r.Options.CrashAngle * MathF.PI / 180f, 0f, 0f));
                r.MakeVehicle(Vehicle.TYPE_CAR);
                r.SetFloat(Vehicle.BUOYANCY, 1f);
                r.ApplyVehicleOverrides();
                r.ReleaseAt = AttractStart;
            },
            // Let go after the host's first frames of a new vehicle, in which it zeroes the body's velocity.
            Input = r =>
            {
                if (!r.Released && r.Now >= AttractStart - 1e-9)
                {
                    r.SetFloat(Vehicle.BUOYANCY, 0f);
                    r.Released = true;
                }
            },
            Gap = GapBelow,
            PassedThrough = (r, s) => s.Position.Z < r.GroundAt(s.Position.X, s.Position.Y),
        },
    }.Concat(PhantomScenarios.All).Concat(ContactScenarios.All).Concat(AvatarHitScenarios.All).Concat(TunnelScenarios.All).Concat(AvatarForceScenarios.All).Concat(GrabScenarios.All).Concat(SpinScenarios.All).Concat(SeamScenarios.All).Concat(KinematicScenarios.All).Concat(SledScenarios.All).ToList();

    private static readonly Quaternion West = Quaternion.CreateFromEulers(0f, 0f, MathF.PI);
    private static readonly Vector3 CrashMotor = new(20f, 0f, 0f);
    private static Vector3 CrashMotorOf(Run r) => CrashMotor * r.Options.CrashSpeed;
    private static Quaternion CrashYaw(Run r) => Quaternion.CreateFromEulers(0f, 0f, r.Options.CrashAngle * MathF.PI / 180f);
    public const float WakeKeyAt = 8f;
    private const float CrashStartX = 100f;
    private const float CrashTargetX = 130f;

    /// <summary>The attractor scenarios' starting tilt (radians).</summary>
    public const float AttractTilt = 30f * MathF.PI / 180f;

    /// <summary>When the attractor scenarios tilt the box: after the host's first frames of a new vehicle, in which it
    /// zeroes the body's velocity (the guard for a vehicle restored with a region), and on every rate's heartbeat.</summary>
    public const double AttractStart = 2.0;

    private static void Tilt(Run r, Quaternion tilt)
    {
        if (!r.Released && r.Now >= AttractStart - 1e-9)
        {
            r.Actor.Orientation = tilt;
            r.Released = true;
        }
    }

    private const int LimitRollOnlyFlag = 0x2;
    private const int LimitMotorUpFlag = 0x40;
    public const int MouselookSteerFlag = 0x80;
    public const int MouselookBankFlag = 0x100;

    /// <summary>The bank-turn tilt at <see cref="AttractStart"/>: rolled this far right (radians), about its own x axis,
    /// then pitched <see cref="BankTurnPitch"/> nose up about the world's y.</summary>
    public const float BankTurnRoll = 20f * MathF.PI / 180f;
    public const float BankTurnPitch = 30f * MathF.PI / 180f;

    /// <summary>The airplane the banking and mouselook-bank scenarios fly: VEHICLE_TYPE_AIRPLANE with its presets, held up
    /// by buoyancy 1 so that its turn is the banking's, 60 m above level ground facing east, in the north of the region
    /// (they turn right, to the south).</summary>
    private static void SetupBankingAirplane(Run r, int flags)
    {
        r.AddBox(new Vector3(3f, 3f, 0.5f), new Vector3(60f, 200f, Course.Ground + 60f), Quaternion.Identity);
        r.MakeVehicle(Vehicle.TYPE_AIRPLANE);
        r.SetFloat(Vehicle.BUOYANCY, 1f);
        if (flags != 0)
            r.SetFlags(flags, false);
        r.ApplyVehicleOverrides();
    }

    /// <summary>The camera of a driver in mouselook looking along <paramref name="at"/> (world, level), as the simulator's
    /// ScenePresence hands it to the vehicle's root actor once the driver's controls are taken.</summary>
    private static void DriverCamera(Run r, Vector3 at)
    {
        HarnessOptions o = r.Options;
        if (o.CameraNoiseDegrees > 0f)
        {
            // A shaking camera: a new offset at every camera update, held until the next (simulated time).
            float yaw0 = MathF.Atan2(at.Y, at.X);
            float sigma = o.CameraNoiseDegrees * MathF.PI / 180f;
            var rng = new Random(o.CameraSeed);
            long last = long.MinValue;
            long? t0 = null;
            float dyaw = 0f, dpitch = 0f;
            float Gauss() => (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble()));
            r.Actor.OnPhysicsRequestingCameraData += () =>
            {
                // The time of the step reading the camera: the vehicle controller's clock, which moves with each physics
                // step (the harness's own time moves once a heartbeat); the harness time before a controller has one.
                double now = r.Now;
                Func<DateTime> clock = (r.Actor as JoltPrim)?.VehicleControl?.Clock;
                if (clock != null)
                {
                    long ticks = clock().Ticks;
                    t0 ??= ticks - (long)(r.Now * TimeSpan.TicksPerSecond);
                    now = (ticks - t0.Value) / (double)TimeSpan.TicksPerSecond;
                }
                long tick = (long)Math.Floor(now * o.CameraRateHz + 1e-9);
                if (tick != last)
                {
                    last = tick;
                    dyaw = Gauss() * sigma;
                    dpitch = Gauss() * sigma;
                }
                float yaw = yaw0 + dyaw;
                return new CameraData
                {
                    CameraAtAxis = new Vector3(MathF.Cos(dpitch) * MathF.Cos(yaw), MathF.Cos(dpitch) * MathF.Sin(yaw), MathF.Sin(dpitch)),
                    CameraRotation = Quaternion.CreateFromEulers(0f, -dpitch, yaw),
                    MouseLook = o.DriverInMouselook,
                };
            };
            return;
        }
        var camera = new CameraData
        {
            CameraAtAxis = Vector3.Normalize(in at),
            CameraRotation = Quaternion.CreateFromEulers(0f, 0f, MathF.Atan2(at.Y, at.X)),
            MouseLook = r.Options.DriverInMouselook,
        };
        r.Actor.OnPhysicsRequestingCameraData += () => camera;
    }

    /// <summary>The world heading of an orientation's x axis (radians, counter-clockwise from east).</summary>
    public static float Heading(Quaternion q)
    {
        Vector3 x = Vector3.UnitX * q;
        return MathF.Atan2(x.Y, x.X);
    }

    // Buoyancy 1 and every behaviour off but the ones a scenario then sets: no hover, attractor, banking, deflection
    // or angular friction.
    private static void SetFloatingAlone(Run r)
    {
        r.SetFloat(Vehicle.BUOYANCY, 1f);
        r.SetFloat(Vehicle.HOVER_TIMESCALE, 1000f);
        r.SetFloat(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
        r.SetFloat(Vehicle.BANKING_EFFICIENCY, 0f);
        r.SetFloat(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
        r.SetFloat(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1000f);
        r.SetVector(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
    }

    private static void SetupAttract(Run r)
    {
        r.ReleaseAt = AttractStart;
        r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 128f, Course.Ground + 15f), Quaternion.Identity);
        r.MakeVehicle(Vehicle.TYPE_BALLOON);
        r.SetFloat(Vehicle.HOVER_TIMESCALE, 1000f);
        r.SetVector(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        r.SetFloat(Vehicle.BANKING_EFFICIENCY, 0f);
        r.ApplyVehicleOverrides();
    }

    // A sports car's linkset: a 3.4 x 1.5 x 0.45 m root box, a cabin, four wheel cylinders on their sides (their bottoms level
    // with the ground when the root centre is 0.52 m up), a headlight and a spoiler. Facing east, dropped `drop` metres.
    private static void AddSportsCar(Run r, float x, float y, float drop, float rollDeg = 0f, float pitchDeg = 0f, bool sitter = false)
    {
        var rootSize = new Vector3(3.4f, 1.5f, 0.45f);
        var centre = new Vector3(x, y, r.GroundAt(x, y) + 0.52f + drop);
        Quaternion lean = Quaternion.CreateFromEulers((r.Options.CrashAngle + rollDeg) * MathF.PI / 180f, pitchDeg * MathF.PI / 180f, 0f);   // --crash-angle adds to the roll
        PhysicsActor root = r.AddBox(rootSize, centre, lean);
        uint id = 1100;
        void Part(PrimitiveBaseShape shape, Vector3 at, Vector3 size, Quaternion rot) => r.AddPart(shape, size, centre + Vector3.Transform(at, lean), lean * rot, true, id++, root);
        Quaternion onSide = Quaternion.CreateFromEulers(MathF.PI / 2f, 0f, 0f);
        Part(PrimitiveBaseShape.CreateBox(), new Vector3(-0.2f, 0f, 0.4f), new Vector3(1.5f, 1.3f, 0.4f), Quaternion.Identity);
        foreach (var (wx, wy) in new[] { (1.05f, 0.85f), (1.05f, -0.85f), (-1.05f, 0.85f), (-1.05f, -0.85f) })
            Part(PrimitiveBaseShape.CreateCylinder(), new Vector3(wx, wy, -0.17f), new Vector3(0.7f, 0.7f, 0.3f), onSide);
        Part(PrimitiveBaseShape.CreateBox(), new Vector3(1.72f, 0f, 0.05f), new Vector3(0.12f, 0.9f, 0.14f), Quaternion.Identity);
        Part(PrimitiveBaseShape.CreateBox(), new Vector3(-1.55f, 0f, 0.5f), new Vector3(0.45f, 1.4f, 0.06f), Quaternion.Identity);
        if (sitter)
        {
            // A seated driver as a 70 kg block at the sit offset (-0.25, 0, 0.62 from the root centre), density 70 / 0.064 m3.
            r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(0.4f, 0.4f, 0.4f), centre + Vector3.Transform(new Vector3(-0.25f, 0f, 0.62f), lean), lean, true, id++, root, 70f / 0.064f);
        }
    }

    private static readonly Vector3 GarageMotor = new(7f, 0f, 0f);

    /// <summary>The drop-drive scenarios: a car box 0.4 m above level ground facing east. <paramref name="earlierScript"/>
    /// null leaves the type's presets; false sets what a current test-drive script sets; true what an earlier one set.</summary>
    private static void SetupDropDrive(Run r, bool? earlierScript, bool linkset = false, float drop = 0.4f, float roll = 0f, float pitch = 0f, bool sitter = false)
    {
        if (linkset)
            AddSportsCar(r, 165f, 85f, drop, roll, pitch, sitter);
        else
            r.AddBox(CarSize, new Vector3(165f, 85f, r.GroundAt(165f, 85f) + CarSize.Z * 0.5f + drop), Quaternion.Identity);
        r.MakeVehicle(Vehicle.TYPE_CAR);
        if (earlierScript.HasValue)
        {
            bool old = earlierScript.Value;
            r.SetVector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(12f, old ? 2f : 0.6f, 1000f));
            r.SetFloat(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            r.SetFloat(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 0.4f);
            r.SetVector(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(2f, 2f, 0.6f));
            r.SetFloat(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.4f);
            r.SetFloat(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 0.4f);
            r.SetFloat(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, old ? 0.6f : 0.8f);
            r.SetFloat(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, old ? 1.5f : 1f);
            if (!old)
            {
                r.SetFlags(0x2, true);   // VEHICLE_FLAG_LIMIT_ROLL_ONLY
                r.SetFloat(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
                r.SetFloat(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, 0f);
                r.SetFloat(Vehicle.BANKING_EFFICIENCY, 0f);
                r.SetFloat(Vehicle.HOVER_EFFICIENCY, 0f);
                r.SetFloat(Vehicle.BUOYANCY, 0f);
            }
        }
        r.ApplyVehicleOverrides();
        r.NextKey = r.Options.Feed == InputFeed.Region ? r.Options.KeyDelay : 0.0;
        r.ReleaseAt = r.NextKey + r.Hold;
        r.StopAtRest = false;
    }

    private static void SetupParked(Run r, bool testCar)
    {
        r.AddBoxOnGround(CarSize, 165f, 85f, Quaternion.Identity);
        r.MakeVehicle(Vehicle.TYPE_CAR);
        if (testCar)
        {
            r.SetVector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
            r.SetFloat(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            r.SetFloat(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 0.5f);
        }
        r.ApplyVehicleOverrides();
        r.ReleaseAt = 0;
    }

    private static void SetupCrashCar(Run r)
    {
        r.AddBoxOnGround(CarSize, CrashStartX, 85f + r.Options.CrashOffset, CrashYaw(r));
        r.MakeVehicle(Vehicle.TYPE_CAR);
        r.ApplyVehicleOverrides();
        r.NextKey = 0.0;
        r.ReleaseAt = r.Hold;
    }

    public static Scenario Find(string name)
        => Scenarios.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentException($"unknown scenario '{name}'; known: {string.Join(", ", Scenarios.Select(s => s.Name))}");

    private const float RestSpeed = 0.1f;
    private const double RestSeconds = 1.0;
    private static readonly DateTime ClockEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The name of the shipped defaults file, which the harness project copies next to the harness.</summary>
    public const string ShippedDefaultsFile = "RegionServerDefaults.ini";

    private static IConfig _shippedJolt;

    /// <summary>The [Jolt] section of RegionServerDefaults.ini, which every simulator reads before its own ini.</summary>
    public static IConfig ShippedJolt()
    {
        if (_shippedJolt != null)
            return _shippedJolt;
        string path = Path.Combine(AppContext.BaseDirectory, ShippedDefaultsFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{ShippedDefaultsFile} is not next to the harness; the harness project copies it from Source/OpenSim.Server.RegionServer/AppData.", path);
        IConfig section = new IniConfigSource(path).Configs["Jolt"]
            ?? throw new InvalidDataException($"{ShippedDefaultsFile} has no [Jolt] section.");
        return _shippedJolt = section;
    }

    /// <summary>Runs one scenario at one rate and slope. Writes nothing; the caller decides where output goes.</summary>
    public static RunResult Run(Scenario sc, HarnessOptions o)
    {
        float slope = sc.UsesSlope ? (o.SlopeDeg ?? sc.Slopes[0]) : 0f;
        var r = new Run
        {
            Options = o,
            Slope = slope,
            Duration = o.Duration ?? sc.DefaultDuration(slope),
            Hold = o.Hold ?? sc.DefaultHold(slope),
            Dt = 1.0 / o.RateHz,
        };

        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        if (o.ShippedConfig)
            foreach (string key in ShippedJolt().GetKeys())
                jolt.Set(key, ShippedJolt().Get(key));
        // 0 is one step per heartbeat, set as such: the module's own default is 45 Hz. A --jolt key overrides it.
        jolt.Set("PhysicsStepRate", "0");
        if (!o.UseDefaults)
            MaterialPins.Apply(jolt);
        foreach (KeyValuePair<string, string> kv in o.Jolt)
            jolt.Set(kv.Key, kv.Value);
        if (o.PhysicsRateHz > 0)
            jolt.Set("PhysicsStepRate", o.PhysicsRateHz.ToString(CultureInfo.InvariantCulture));

        var scene = new JoltScene { RayCastClock = o.RayCastClock };
        r.Scene = scene;
        scene.Initialise(config);
        scene.VehicleClock = () => ClockEpoch.AddTicks((long)Math.Round(r.Clock * TimeSpan.TicksPerSecond));
        (float[] heights, float water) = sc.World(slope);
        if (sc.AllowsTerrainFile && o.TerrainFile != null)
            heights = SledScenarios.LoadTerrain(o.TerrainFile);
        scene.InitialiseWithoutScene("Harness", Course.Size, Course.Size, heights, water, (float)r.Dt);

        var result = new RunResult { Scenario = sc.Name, RateHz = o.RateHz, PhysicsRateHz = scene.Substepping ? scene.Substeps.RateHz : 0, SlopeDeg = slope };
        if (o.CrashSpeed != 1f || o.CrashOffset != 0f || o.CrashAngle != 0f)
            result.Variant = $"~v{RunResult.Fmt(o.CrashSpeed, "0.###")}~o{RunResult.Fmt(o.CrashOffset, "0.###")}~a{RunResult.Fmt(o.CrashAngle, "0.###")}";
        if (sc.UsesShot)
            result.Variant += $"~b{RunResult.Fmt(o.ShotBall, "0.###")}~v{RunResult.Fmt(o.ShotSpeed, "0.###")}";
        try
        {
            r.Clock = -r.Dt * 0.5;
            if (o.RegionMesher)
            {
                r.Mesher = RegionMesher.Create(Path.Combine(Path.GetTempPath(), "harness-sculpt-cache"));
                r.UseMesher(r.Mesher);
            }
            if (o.WarmUpStep && o.VehicleRoute != VehicleRoute.RegionStart)
            {
                scene.Simulate((float)r.Dt);
                if (scene.Substepping)
                    r.WarmUpSubsteps = scene.Substeps.Steps;
            }
            sc.Setup(r);
            if (o.VehicleRoute != VehicleRoute.None)
                RestoreVehicle(r, o.VehicleRoute);
            if (o.VehicleScriptAgain)
                foreach (Action<PhysicsActor> call in r.VehicleCalls.ToArray())
                    call(r.Actor);
            float dt = (float)r.Dt;
            int frames = (int)Math.Ceiling(r.Duration / r.Dt - 1e-9);
            double restCandidate = double.NaN;
            bool movedSinceRelease = false;
            double leftAt = double.NaN;
            for (int k = 0; k < frames; k++)
            {
                // Inputs land half a heartbeat before the step (a region feed sets its own event times); the step
                // runs at the heartbeat's time.
                r.Now = k * r.Dt;
                r.Clock = r.Now - r.Dt * 0.5;
                if (k == 1 && o.StartSpeed is float startSpeed)
                    r.Actor.Velocity = Vector3.UnitX * r.Actor.Orientation * startSpeed;
                sc.Input(r);
                r.Clock = r.Now;
                scene.Simulate(dt);
                r.Heartbeats++;

                Sample s = Read(r, (k + 1) * r.Dt);
                result.Samples.Add(s);
                if (s.T >= r.StopAt - 1e-9)
                    break;
                if (!InRegion(s.Position))
                {
                    leftAt = s.T;
                    break;
                }

                if (!double.IsNaN(r.ReleaseAt) && s.T > r.ReleaseAt)
                {
                    if (s.Speed > 0.5f) movedSinceRelease = true;
                    if (movedSinceRelease && s.Speed < RestSpeed)
                    {
                        if (double.IsNaN(restCandidate)) restCandidate = s.T;
                        if (r.StopAtRest && s.T - restCandidate >= RestSeconds - 1e-9)
                            break;
                    }
                    else
                        restCandidate = double.NaN;
                }
            }
            result.Summary = Summarise(r, sc, result.Samples);
            foreach (HarnessPart p in r.Parts)
                p.EndPosition = p.Actor?.Position ?? p.Position;
            result.Parts.AddRange(r.Parts);
            result.Watches.AddRange(r.Watches);
            result.Summary.LeftRegionT = leftAt;
            // The run ends on the first sample outside the region, where the body has already left the engine and so
            // reads as nothing awake. That is not sleep, so a run that leaves the region has no sleep time.
            if (!double.IsNaN(leftAt))
                result.Summary.SleepAfter = double.NaN;
            Backend.PhysicsCapacityStats stats = scene.CapacityStats();
            result.Summary.RayCasts = stats.RayCasts;
            result.Summary.RayCastsRefused = stats.RayCastsRefused;
            result.Summary.RayCastsCutShort = stats.RayCastsCutShort;
            if (stats.RayCasts > 0)
                result.Summary.RayCastMicrosPerCast = stats.RayCastMsTotal * 1000.0 / stats.RayCasts;
            else if (r.TimedRayCasts > 0)
            {
                result.Summary.RayCasts = r.TimedRayCasts;
                result.Summary.RayCastMicrosPerCast = r.TimedRayTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / r.TimedRayCasts;
            }
        }
        finally
        {
            foreach (Action cleanup in r.Cleanups)
                cleanup();
            // A SceneObjectPart left holding an actor removes it from its scene when it is finalized, and it has none.
            if (r.VehiclePart != null)
            {
                r.VehiclePart.PhysActor = null;
                r.VehiclePart.Dispose();
            }
            scene.Dispose();
        }
        return result;
    }

    // The scenario's vehicle handed back by the route from its record, where its setup left it.
    private static void RestoreVehicle(Run r, VehicleRoute route)
    {
        if (r.VehiclePart == null)
            throw new InvalidOperationException("--vehicle-restore: the scenario made no vehicle");
        switch (route)
        {
            case VehicleRoute.PhysicsOffOn:
                VehicleRestore.PhysicsOff(r.Actor);
                VehicleRestore.PhysicsOn(r.Actor);
                return;
            case VehicleRoute.PhantomPhysicsOffOn:
                throw new NotSupportedException("--vehicle-restore: the scenarios build a solid vehicle; this route is for a phantom one");
        }
        PhysicsActor old = r.Actor;
        SOPVehicle saved = VehicleRestore.Saved(r.VehiclePart, route);
        Vector3 position = old.Position, velocity = old.Velocity, angular = old.RotationalVelocity;
        Quaternion rotation = old.Orientation;
        r.Scene.RemovePrim(old);
        // Built as the harness built the original (AddBox), then given the record.
        PhysicsActor pa = VehicleRestore.AddToPhysics(r.Scene, saved, position, r.ActorSize, rotation, true, false, true, velocity, angular,
                                                      old.LocalID, a => { r.AsSimulatorAdds(a); a.Density = 1000f; });
        r.Actor = pa;
        r.VehiclePart.PhysActor = pa;
    }

    private static Sample Read(Run r, double t)
    {
        PhysicsActor a = r.Actor;
        Vector3 p = a.Position;
        Vector3 v = a.Velocity;
        Vector3 up = Vector3.UnitZ * a.Orientation;
        float tilt = MathF.Acos(Math.Clamp(up.Z, -1f, 1f)) * 180f / MathF.PI;
        float height = p.Z - r.GroundAt(p.X, p.Y);
        PhysicsActor o = r.Other;
        return new Sample(t, p, v, tilt, height, a.IsColliding, a.Orientation, ActiveBodies(r.Scene),
                          o?.Position ?? Vector3.Zero, o?.Velocity ?? Vector3.Zero, o?.Orientation ?? Quaternion.Identity, a.RotationalVelocity);
    }

    // The engine's count of awake bodies. The scene keeps its backend private; the harness reads it the way the
    // `jolt capacity` console command does, through the backend's capacity stats.
    private static readonly System.Reflection.FieldInfo BackendField =
        typeof(JoltScene).GetField("_backend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    internal static int ActiveBodies(JoltScene scene)
        => BackendField?.GetValue(scene) is OpenSim.Region.PhysicsModules.Jolt.Backend.IPhysicsBackend b ? b.GetCapacityStats().ActiveBodyCount : -1;

    /// <summary>Half the size of a box with this rotation along a world axis: how far its surface reaches from its
    /// centre that way.</summary>
    public static float HalfExtent(Quaternion rotation, Vector3 size, Vector3 axis)
    {
        Vector3 x = Vector3.UnitX * rotation, y = Vector3.UnitY * rotation, z = Vector3.UnitZ * rotation;
        return 0.5f * (size.X * MathF.Abs(Vector3.Dot(x, axis)) + size.Y * MathF.Abs(Vector3.Dot(y, axis)) + size.Z * MathF.Abs(Vector3.Dot(z, axis)));
    }

    // How far apart the two objects are along a unit axis (below zero: by how much their projections overlap).
    private static float Separation(Run r, Sample s, Vector3 axis)
        => MathF.Abs(Vector3.Dot(s.Other - s.Position, axis))
           - HalfExtent(s.Rotation, r.ActorSize, axis) - HalfExtent(s.OtherRotation, r.OtherSize, axis);

    // The gap between the object and the second object, two boxes: the widest separation over the axes that can
    // separate two boxes (the three face normals of each and the nine cross products of their edges), or, when they
    // overlap on all of them, minus the shallowest overlap: the depth one box has pushed into the other. (Separation
    // along the world axes alone counts a box turned in a glancing hit as overlapping where its corners clear the other.)
    public static float GapToOther(Run r, Sample s)
    {
        Vector3[] a = { Vector3.UnitX * s.Rotation, Vector3.UnitY * s.Rotation, Vector3.UnitZ * s.Rotation };
        Vector3[] b = { Vector3.UnitX * s.OtherRotation, Vector3.UnitY * s.OtherRotation, Vector3.UnitZ * s.OtherRotation };
        float gap = float.MinValue;
        void Axis(Vector3 axis)
        {
            float length = axis.Length();
            if (length > 1e-4f)
                gap = MathF.Max(gap, Separation(r, s, axis / length));
        }
        foreach (Vector3 u in a) Axis(u);
        foreach (Vector3 v in b) Axis(v);
        foreach (Vector3 u in a)
            foreach (Vector3 v in b)
                Axis(Vector3.Cross(u, v));
        return gap;
    }

    // The object is going through the second one: their centres have crossed along x (the way it was driven) while
    // the two are still pushed into each other by more than ThroughOverlap, so it did not go round or over. (At the
    // harness's speeds a step moves less than the two objects span together, so a pass through always leaves such a
    // sample.) Only while both are in the region (BothInRegion).
    private const float ThroughOverlap = 0.1f;
    private static bool PassedThroughOther(Run r, Sample s)
        => BothInRegion(r, s) && s.Position.X >= s.Other.X && GapToOther(r, s) < -ThroughOverlap;

    // The gap from the object's lowest point to the ground under it.
    private static float GapBelow(Run r, Sample s)
        => s.Position.Z - HalfExtent(s.Rotation, r.ActorSize, Vector3.UnitZ) - r.GroundAt(s.Position.X, s.Position.Y);

    /// <summary>The leaving speed is measured from this long after the impact (s).</summary>
    public const double LeavingDelay = 0.1;

    /// <summary>A gap under this counts as the surfaces meeting.</summary>
    public const float ContactGap = 0.02f;

    internal static bool InRegion(Vector3 p) => p.X >= 0f && p.Y >= 0f && p.X <= Course.Size && p.Y <= Course.Size;

    /// <summary>The object and the second one (when there is one) are both still in the region. A body that crosses the
    /// edge leaves the engine and waits just outside it, where it crossed, until core crosses it into the neighbour
    /// (JoltPrim, after ubODE's ODEPrim.UpdatePositionAndVelocity); the harness has no core to cross it, so it waits
    /// there to the end of the run while the other one moves on. A position read after that is where it waits, not
    /// where it would have gone, so the rules that set the two objects' positions against each other skip such
    /// samples.</summary>
    internal static bool BothInRegion(Run r, Sample s) => InRegion(s.Position) && (r.Other == null || InRegion(s.Other));

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static float Horizontal(Vector3 a, Vector3 b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    internal static Summary Summarise(Run r, Scenario sc, List<Sample> samples)
    {
        var m = new Summary { Steps = samples.Count };
        if (samples.Count == 0)
            return m;
        Vector3 start = samples[0].Position;
        Vector3 atRelease = start;
        double rel = r.ReleaseAt;
        bool haveRelease = !double.IsNaN(rel);
        float minZ = float.MaxValue, maxZ = float.MinValue;
        double steadySum = 0, steadyHSum = 0;
        int steadyN = 0;
        double restCandidate = double.NaN;
        bool moved = false;
        float relZ = float.NaN;

        foreach (Sample s in samples)
        {
            if (!Finite(s.Position) || !Finite(s.Velocity)) { m.NonFinite++; continue; }
            m.TopSpeed = MathF.Max(m.TopSpeed, s.Speed);
            m.PeakHeight = MathF.Max(m.PeakHeight, s.Height);
            m.MaxTilt = MathF.Max(m.MaxTilt, s.Tilt);
            if (s.T >= 1.0) { minZ = MathF.Min(minZ, s.Position.Z); maxZ = MathF.Max(maxZ, s.Position.Z); }
            if (sc.Steady != null && sc.Steady(r, s)) { steadySum += s.Speed; steadyHSum += s.HorizontalSpeed; steadyN++; }

            if (!haveRelease)
                continue;
            if (s.T <= rel + 1e-9)
            {
                atRelease = s.Position;
                m.ReleaseT = rel;
                m.ReleaseSpeed = s.Speed;
                relZ = s.Position.Z;
                continue;
            }
            if (float.IsNaN(relZ)) { relZ = start.Z; m.ReleaseT = rel; m.ReleaseSpeed = 0f; }
            m.PeakRise = MathF.Max(m.PeakRise, s.Position.Z - relZ);
            if (s.Speed > 0.5f) moved = true;
            if (moved && s.Speed < RestSpeed)
            {
                if (double.IsNaN(restCandidate)) restCandidate = s.T;
                if (double.IsNaN(m.TimeToRest) && s.T - restCandidate >= RestSeconds - 1e-9)
                    m.TimeToRest = restCandidate - rel;
            }
            else if (double.IsNaN(m.TimeToRest))
                restCandidate = double.NaN;
        }

        Sample last = samples[^1];
        m.End = last.Position;
        m.DistanceBeforeRelease = haveRelease ? Horizontal(atRelease, start) : Horizontal(last.Position, start);
        m.DistanceAfterRelease = haveRelease ? Horizontal(last.Position, atRelease) : float.NaN;
        if (steadyN > 0) { m.SteadySpeed = (float)(steadySum / steadyN); m.SteadyHorizontalSpeed = (float)(steadyHSum / steadyN); }
        if (sc.SteadyFromPath != null && sc.SteadyFromPath(r, samples) is (float along, float horizontal))
        {
            m.SteadySpeed = along;
            m.SteadyHorizontalSpeed = horizontal;
        }
        m.ZRange = maxZ >= minZ ? maxZ - minZ : 0f;

        // Sleeping: from which sample on nothing is awake.
        m.ActiveAtEnd = last.Active;
        int asleepFrom = samples.Count;
        while (asleepFrom > 0 && samples[asleepFrom - 1].Active == 0)
            asleepFrom--;
        if (asleepFrom < samples.Count)
            m.SleepAfter = samples[asleepFrom].T - (haveRelease ? rel : 0.0);

        if (sc.Gap != null)
            SummariseCollision(r, sc, samples, m);
        else if (sc.PassedThrough != null && samples.Any(s => sc.PassedThrough(r, s)))
            m.Tunneled = 1;
        return m;
    }

    private static void SummariseCollision(Run r, Scenario sc, List<Sample> samples, Summary m)
    {
        int impact = samples.FindIndex(s => sc.Gap(r, s) < ContactGap);
        // A sample is read after its step. A hard hit in the middle of a step can leave the two objects further apart than
        // ContactGap again by the end of it (the second one thrown ahead faster than the first follows), so no sample ever
        // reads as touching. The step also counts as the impact when, at the speed they had at its start, the objects
        // would have met within it.
        for (int i = 1; i < samples.Count && (impact < 0 || i < impact); i++)
        {
            Sample before = samples[i - 1];
            float closing = (before.Velocity - before.OtherVelocity).Length();
            if (sc.Gap(r, before) >= ContactGap && sc.Gap(r, before) < closing * (samples[i].T - before.T))
            {
                impact = i;
                break;
            }
        }
        if (impact < 0)
            return;
        double t0 = samples[impact].T;
        m.ImpactT = t0;
        // A sample is read after its step, so the impact sample's velocities are already the collision's result.
        m.ArrivalSpeed = 0f;
        for (int i = 0; i < impact; i++)
            m.ArrivalSpeed = MathF.Max(m.ArrivalSpeed, MathF.Max(samples[i].Speed, samples[i].OtherSpeed));
        m.LeavingSpeed = 0f;
        m.Penetration = 0f;
        m.CrashRise = 0f;
        float z0 = samples[impact].Position.Z, otherZ0 = samples[impact].Other.Z;
        float pushPen = float.NaN, earlyMin = float.MaxValue, earlyMax = float.MinValue, lateMin = float.MaxValue, lateMax = float.MinValue;
        for (int i = impact; i < samples.Count; i++)
        {
            Sample s = samples[i];
            double after = s.T - t0;
            float overlap = -sc.Gap(r, s);
            // From a tenth of a second after the impact: until then the impact is still going on (the step that met
            // the contact, or a body still swinging onto it).
            if (after >= LeavingDelay - 1e-9 && after <= 1.0 + 1e-9)
                m.LeavingSpeed = MathF.Max(m.LeavingSpeed, MathF.Max(s.Speed, s.OtherSpeed));
            if (BothInRegion(r, s))
                m.Penetration = MathF.Max(m.Penetration, overlap);
            if (after <= 1.0 + 1e-9)
                m.CrashRise = MathF.Max(m.CrashRise, MathF.Max(s.Position.Z - z0, r.Other != null ? s.Other.Z - otherZ0 : 0f));
            if (sc.PassedThrough != null && sc.PassedThrough(r, s))
                m.Tunneled = 1;
            if (after >= 2.0 - 1e-9 && after <= 10.0 + 1e-9)
            {
                pushPen = float.IsNaN(pushPen) ? overlap : MathF.Max(pushPen, overlap);
                if (after < 6.0) { earlyMin = MathF.Min(earlyMin, s.Position.X); earlyMax = MathF.Max(earlyMax, s.Position.X); }
                else { lateMin = MathF.Min(lateMin, s.Position.X); lateMax = MathF.Max(lateMax, s.Position.X); }
            }
        }
        m.PushPenetration = pushPen;
        if (earlyMax >= earlyMin) m.PushRangeEarly = earlyMax - earlyMin;
        if (lateMax >= lateMin) m.PushRangeLate = lateMax - lateMin;
    }
}
