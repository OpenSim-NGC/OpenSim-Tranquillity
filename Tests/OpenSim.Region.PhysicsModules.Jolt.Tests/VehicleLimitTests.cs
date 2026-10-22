/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The vehicle limits as [Jolt] keys: with no keys every limit is the value the module had as a constant (the motor
/// decay cap excepted: Second Life's documented 120 s), and each key changes the figure it governs. Pure, parallel.
/// </summary>
public class VehicleLimitTests
{
    private static IConfigSource Source(params (string key, string value)[] kv)
    {
        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        foreach (var (k, v) in kv)
            cfg.Set(k, v);
        return src;
    }

    private static VehicleSettings Settings(params (string key, string value)[] kv)
    {
        // These are the limits of Second Life's documented behaviour (the decay cap 120 s, the angular cap on the magnitude); the
        // shipped default is InWorldz, so the choice is pinned unless the test sets it.
        if (!kv.Any(p => p.key == "VehicleBehaviour"))
            kv = kv.Append(("VehicleBehaviour", "SecondLife")).ToArray();
        var warnings = new List<string>();
        VehicleSettings s = JoltConfig.FromConfig(Source(kv), warnings).ToVehicleSettings();
        Assert.Empty(warnings);
        return s;
    }

    [Fact]
    public void With_no_keys_every_limit_is_the_old_constant_and_the_decay_cap_is_120_s()
    {
        VehicleSettings s = Settings();
        Assert.Equal(200f, s.MaxLinearSpeed);
        Assert.Equal(30f, s.ReferenceSpeed);
        Assert.Equal((float)(Math.PI * 4.0), s.MaxAngularSpeed);
        Assert.Equal(0.0156f, s.MinTimescale);
        Assert.Equal(1000f, s.MaxTimescale);
        Assert.Equal(120f, s.MaxDecayTimescale);
        Assert.Equal(300f, s.MaxHoverTimescale);
        Assert.Equal(500f, s.MaxAttractTimescale);
        Assert.Equal(100f, s.MaxMotorOffset);
        Assert.Equal(-128f, s.MinHoverHeight);
        Assert.Equal(10000f, s.MaxHoverHeight);

        // The same as a controller with no settings from a host.
        VehicleSettings d = VehicleSettings.Default;
        Assert.Equal(d.MaxLinearSpeed, s.MaxLinearSpeed);
        Assert.Equal(d.MaxDecayTimescale, s.MaxDecayTimescale);
        Assert.Equal(d.MinHoverHeight, s.MinHoverHeight);

        // The engine's body caps: Jolt's own defaults.
        PhysicsBackendSettings b = JoltConfig.FromConfig(new IniConfigSource(), null).ToBackendSettings(256, 256);
        Assert.Equal(500f, b.MaxBodyLinearSpeed);
        Assert.Equal(0.25f * MathF.PI * 60f, b.MaxBodyAngularSpeed);
    }

    private static VehicleController Car(VehicleSettings s)
    {
        var v = new VehicleController(new HeldBody()) { Settings = s };
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        return v;
    }

    // What a script asks for, the figure with no keys, the key, and the figure with the key.
    [Theory]
    [InlineData("LINEAR_MOTOR_DIRECTION", 500f, 200f, "VehicleMaxLinearSpeed", "1000", 500f)]
    // The cap is on the magnitude (Second Life), and the scalar form sets all three axes: the first axis reads the cap over the square root of 3.
    [InlineData("ANGULAR_MOTOR_DIRECTION", 100f, 7.2552f, "VehicleMaxAngularSpeed", "20", 11.5470f)]
    [InlineData("LINEAR_FRICTION_TIMESCALE", 0.001f, 0.0156f, "VehicleMinTimescale", "0.005", 0.005f)]
    [InlineData("LINEAR_FRICTION_TIMESCALE", 5000f, 1000f, "VehicleMaxTimescale", "500", 500f)]
    [InlineData("LINEAR_MOTOR_DECAY_TIMESCALE", 1000f, 120f, "VehicleMaxDecayTimescale", "1000", 1000f)]
    [InlineData("ANGULAR_MOTOR_DECAY_TIMESCALE", 1000f, 120f, "VehicleMaxDecayTimescale", "60", 60f)]
    [InlineData("HOVER_TIMESCALE", 1000f, 300f, "VehicleMaxHoverTimescale", "600", 600f)]
    [InlineData("VERTICAL_ATTRACTION_TIMESCALE", 1000f, 500f, "VehicleMaxAttractTimescale", "800", 800f)]
    [InlineData("LINEAR_MOTOR_OFFSET", 500f, 100f, "VehicleMaxMotorOffset", "10", 10f)]
    [InlineData("HOVER_HEIGHT", 20000f, 10000f, "VehicleMaxHoverHeight", "100", 100f)]
    [InlineData("HOVER_HEIGHT", -1000f, -128f, "VehicleMinHoverHeight", "-500", -500f)]
    public void Each_key_changes_the_limit_it_governs(string param, float asked, float byDefault, string key, string value, float withKey)
    {
        Vehicle code = Enum.Parse<Vehicle>(param);
        Assert.Equal(byDefault, Read(Car(Settings()), code, asked), 4);
        Assert.Equal(withKey, Read(Car(Settings((key, value))), code, asked), 4);
    }

    // Sets the param as a script's scalar call would and reads back what was stored (the first axis of a vector).
    private static float Read(VehicleController v, Vehicle code, float value)
    {
        v.ProcessFloatVehicleParam(code, value);
        return code switch
        {
            Vehicle.LINEAR_MOTOR_DIRECTION => v.GetVecParam(VehVectorParam.LinearMotorDirection).X,
            Vehicle.ANGULAR_MOTOR_DIRECTION => v.GetVecParam(VehVectorParam.AngularMotorDirection).X,
            Vehicle.LINEAR_FRICTION_TIMESCALE => v.GetVecParam(VehVectorParam.LinearFrictionTimescale).X,
            Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE => v.GetVecParam(VehVectorParam.LinearMotorDecayTimescale).X,
            Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE => v.GetVecParam(VehVectorParam.AngularMotorDecayTimescale).X,
            Vehicle.HOVER_TIMESCALE => v.GetFloatParam(VehFloatParam.HoverTimescale),
            Vehicle.VERTICAL_ATTRACTION_TIMESCALE => v.GetFloatParam(VehFloatParam.VerticalAttractionTimescale),
            Vehicle.LINEAR_MOTOR_OFFSET => v.GetVecParam(VehVectorParam.LinearMotorOffset).X,
            Vehicle.HOVER_HEIGHT => v.GetFloatParam(VehFloatParam.HoverHeight),
            _ => throw new ArgumentException(code.ToString()),
        };
    }

    [Theory]
    [InlineData("VehicleMaxLinearSpeed", "0")]
    [InlineData("VehicleMaxLinearSpeed", "fast")]
    [InlineData("VehicleReferenceSpeed", "0")]
    [InlineData("VehicleMaxAngularSpeed", "-1")]
    [InlineData("VehicleAngularLimiterFloor", "2")]
    [InlineData("VehicleAngularLimiterFloor", "-0.5")]
    [InlineData("VehicleMinTimescale", "0")]
    [InlineData("VehicleMinTimescale", "2")]
    [InlineData("VehicleMaxTimescale", "5000")]
    [InlineData("VehicleMaxDecayTimescale", "0.5")]
    [InlineData("VehicleMaxHoverTimescale", "NaN")]
    [InlineData("VehicleMaxAttractTimescale", "0")]
    [InlineData("VehicleMaxMotorOffset", "-1")]
    [InlineData("BodyMaxLinearSpeed", "0")]
    [InlineData("BodyMaxAngularSpeed", "Infinity")]
    public void An_invalid_value_falls_back_with_a_warning(string key, string value)
    {
        var warnings = new List<string>();
        JoltConfig c = JoltConfig.FromConfig(Source((key, value)), warnings);
        Assert.Single(warnings);
        Assert.Contains(key, warnings[0]);
        JoltConfig d = JoltConfig.FromConfig(new IniConfigSource(), null);
        VehicleSettings a = c.ToVehicleSettings(), b = d.ToVehicleSettings();
        Assert.Equal((b.MaxLinearSpeed, b.ReferenceSpeed, b.MaxAngularSpeed, b.MinTimescale, b.MaxTimescale, b.MaxDecayTimescale, b.MaxHoverTimescale, b.MaxAttractTimescale, b.MaxMotorOffset),
                     (a.MaxLinearSpeed, a.ReferenceSpeed, a.MaxAngularSpeed, a.MinTimescale, a.MaxTimescale, a.MaxDecayTimescale, a.MaxHoverTimescale, a.MaxAttractTimescale, a.MaxMotorOffset));
        Assert.Equal(d.ToBackendSettings(256, 256), c.ToBackendSettings(256, 256));
    }

    [Fact]
    public void A_hover_height_range_upside_down_falls_back_to_the_defaults()
    {
        var warnings = new List<string>();
        VehicleSettings s = JoltConfig.FromConfig(Source(("VehicleMinHoverHeight", "50"), ("VehicleMaxHoverHeight", "10")), warnings).ToVehicleSettings();
        Assert.Single(warnings);
        Assert.Equal(-128f, s.MinHoverHeight);
        Assert.Equal(10000f, s.MaxHoverHeight);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The reference speed scales dynamic banking.

    private sealed class HeldBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => Vector3.Zero;
        public void SetGravityFactor(float factor) { }
        public bool HasCollision => false;
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    // One step of dynamic banking (mix 1) on a car rolled 0.3 rad moving forward at 15 m/s: the yaw rate it gives.
    private static float BankingYawRate(VehicleSettings s)
    {
        var body = new HeldBody { Orientation = Quaternion.CreateFromEulers(0.3f, 0f, 0f) };
        body.LinearVelocity = new Vector3(15f, 0f, 0f) * body.Orientation;
        var car = new VehicleController(body) { Settings = s };
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        car.Clock = () => t0;
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 1f);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, 1f);
        car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
        car.Step(0.1f);
        return body.AngularVelocity.Z;
    }

    [Fact]
    public void The_reference_speed_scales_dynamic_banking()
    {
        float at30 = BankingYawRate(Settings());
        float at15 = BankingYawRate(Settings(("VehicleReferenceSpeed", "15")));
        Assert.True(Math.Abs(at30) > 1e-3f, $"{at30}");
        Assert.Equal(2.0, at15 / at30, 3);   // 15 m/s is half of 30 and all of 15
    }
}

/// <summary>The engine's body speed caps, on a real backend. Serial with the other native tests.</summary>
[Collection(JoltNativeSerial.Name)]
public class BodySpeedCapTests
{
    // A body in free space thrown at the given speed: its speed after one step.
    private static float SpeedAfterAStep(PhysicsBackendSettings settings, float thrown, bool angular)
    {
        settings.Gravity = SVector3.Zero;
        using var t = new JoltTestBackend(settings);
        BodyId body = t.Dynamic(t.B.CreateBoxShape(new SVector3(0.5f)), new SVector3(128f, 128f, 500f), userData: 7);
        t.B.SetBodyDamping(body, 0f, 0f);
        if (angular)
            t.B.SetBodyAngularVelocity(body, new SVector3(0f, 0f, thrown));
        else
            t.B.SetBodyLinearVelocity(body, new SVector3(thrown, 0f, 0f));
        var states = new BodyState[16];
        StepResult r = t.B.Step(1f / 11f, states, new CharacterState[4], new ContactReport[16]);
        for (int i = 0; i < r.BodyUpdateCount; i++)
            if (states[i].UserData == 7)
                return angular ? states[i].AngularVelocity.Length() : states[i].LinearVelocity.Length();
        throw new InvalidOperationException("no update for the body");
    }

    [Fact]
    public void With_no_keys_the_engine_caps_a_body_at_its_own_defaults()
    {
        PhysicsBackendSettings s = JoltConfig.FromConfig(new IniConfigSource(), null).ToBackendSettings(256, 256);
        Assert.Equal(400f, SpeedAfterAStep(s, 400f, false), 2);
        Assert.Equal(500f, SpeedAfterAStep(s, 600f, false), 2);
        Assert.Equal(0.25f * MathF.PI * 60f, SpeedAfterAStep(s, 100f, true), 2);
    }

    [Fact]
    public void The_keys_change_the_caps()
    {
        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        cfg.Set("BodyMaxLinearSpeed", "1000");
        cfg.Set("BodyMaxAngularSpeed", "10");
        PhysicsBackendSettings s = JoltConfig.FromConfig(src, null).ToBackendSettings(256, 256);
        Assert.Equal(600f, SpeedAfterAStep(s, 600f, false), 2);
        Assert.Equal(10f, SpeedAfterAStep(s, 100f, true), 2);
    }
}
