/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Runtime.CompilerServices;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using OmvVector3 = OpenMetaverse.Vector3;
using OmvQuaternion = OpenMetaverse.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The non-finite firewall at the physics seam: backend mutators drop a NaN/Inf call and count
/// it, creators throw, queries return nothing, and the OpenSim-facing actors keep their previous value.
///
/// <para>Red-run exclusions (they put NaN into the broadphase or a native shape before the fix, which can take the
/// test host down rather than fail): <c>Body_transform_*</c>, <c>Character_shape_*</c>, <c>Creators_*</c> and
/// <c>Twenty_bodies_*</c>.</para>
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class NonFiniteGuardTests
{
    private static readonly float[] Bad = { float.NaN, float.PositiveInfinity };

    private static Vector3 V(float f) => new(f, 1f, 1f);

    private sealed class World : IDisposable
    {
        public readonly JoltTestBackend T = new();
        public readonly BodyId Body;
        public readonly CharacterId Character;

        public World()
        {
            T.Ground();
            Body = T.Dynamic(T.B.CreateBoxShape(new Vector3(0.5f)), new Vector3(128f, 128f, 5f));
            var cd = CharacterDesc.Default;
            cd.Position = new Vector3(100f, 100f, 1.5f);
            Character = T.B.CreateCharacter(cd);
        }

        public long Rejected => T.B.GetCapacityStats().RejectedNonFinite;
        public BodyState BodyState { get { Assert.True(T.B.TryGetBodyState(Body, out var s)); return s; } }
        public CharacterState CharState { get { Assert.True(T.B.TryGetCharacterState(Character, out var s)); return s; } }
        public void Dispose() => T.Dispose();
    }

    /// <summary>Every call gets a NaN and an Inf; each must be dropped (state unchanged) and counted once.</summary>
    private static void AssertDropped(Action<World, float> call)
    {
        using var w = new World();
        var body = w.BodyState;
        var ch = w.CharState;
        var mass = w.T.B.GetBodyMass(w.Body);
        var before = w.Rejected;

        foreach (var f in Bad)
            call(w, f);

        Assert.Equal(before + Bad.Length, w.Rejected);
        var after = w.BodyState;
        Assert.Equal(body.Position, after.Position);
        Assert.Equal(body.Orientation, after.Orientation);
        Assert.Equal(body.LinearVelocity, after.LinearVelocity);
        Assert.Equal(body.AngularVelocity, after.AngularVelocity);
        Assert.Equal(mass, w.T.B.GetBodyMass(w.Body));
        var chAfter = w.CharState;
        Assert.Equal(ch.Position, chAfter.Position);
        Assert.Equal(ch.LinearVelocity, chAfter.LinearVelocity);
    }

    public static IEnumerable<object[]> Mutators() => new[]
    {
        "SetBodyLinearVelocity", "SetBodyAngularVelocity",
        "ApplyForce", "ApplyTorque", "ApplyImpulse", "ApplyImpulseAtPoint.impulse", "ApplyImpulseAtPoint.point",
        "ApplyAngularImpulse", "ApplyBuoyancy",
        "SetBodyMass", "SetBodyDensity", "SetBodyFriction", "SetBodyRestitution", "SetBodyDamping",
        "SetBodyGravityFactor", "SetGravity", "SetWaterHeight",
        "ReGroundCharacter", "SetCharacterMovement",
    }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Mutators))]
    public void Mutator_drops_and_counts_non_finite(string mutator)
    {
        AssertDropped((w, f) =>
        {
            var b = w.T.B;
            switch (mutator)
            {
                case "SetBodyLinearVelocity": b.SetBodyLinearVelocity(w.Body, V(f)); break;
                case "SetBodyAngularVelocity": b.SetBodyAngularVelocity(w.Body, V(f)); break;
                case "ApplyForce": b.ApplyForce(w.Body, V(f)); break;
                case "ApplyTorque": b.ApplyTorque(w.Body, V(f)); break;
                case "ApplyImpulse": b.ApplyImpulse(w.Body, V(f)); break;
                case "ApplyImpulseAtPoint.impulse": b.ApplyImpulseAtPoint(w.Body, V(f), new Vector3(128f, 128f, 5f)); break;
                case "ApplyImpulseAtPoint.point": b.ApplyImpulseAtPoint(w.Body, Vector3.UnitX, V(f)); break;
                case "ApplyAngularImpulse": b.ApplyAngularImpulse(w.Body, V(f)); break;
                case "ApplyBuoyancy": b.ApplyBuoyancy(w.Body, f, 1f, 0f, 0f); break;
                case "SetBodyMass": b.SetBodyMass(w.Body, f); break;
                case "SetBodyDensity": b.SetBodyDensity(w.Body, f); break;
                case "SetBodyFriction": b.SetBodyFriction(w.Body, f); break;
                case "SetBodyRestitution": b.SetBodyRestitution(w.Body, f); break;
                case "SetBodyDamping": b.SetBodyDamping(w.Body, 0.1f, f); break;
                case "SetBodyGravityFactor": b.SetBodyGravityFactor(w.Body, f); break;
                case "SetGravity": b.SetGravity(new Vector3(0f, 0f, f)); break;
                case "SetWaterHeight": b.SetWaterHeight(f); break;
                case "ReGroundCharacter": b.ReGroundCharacter(w.Character, V(f)); break;
                case "SetCharacterMovement": b.SetCharacterMovement(w.Character, V(f), false, false); break;
                default: throw new ArgumentOutOfRangeException(mutator);
            }
        });
    }

    [Fact]
    public void Body_transform_drops_non_finite_position_and_orientation()
    {
        AssertDropped((w, f) => w.T.B.SetBodyTransform(w.Body, V(f), Quaternion.Identity, true));
        AssertDropped((w, f) => w.T.B.SetBodyTransform(w.Body, new Vector3(128f, 128f, 5f), new Quaternion(f, 0f, 0f, 1f), true));
        // A zero-length quaternion counts as non-finite: it cannot be normalised.
        AssertDropped((w, f) => w.T.B.SetBodyTransform(w.Body, new Vector3(128f, 128f, 5f), default, true));
    }

    [Fact]
    public void Character_transform_drops_non_finite()
    {
        AssertDropped((w, f) => w.T.B.SetCharacterTransform(w.Character, V(f), Quaternion.Identity));
        AssertDropped((w, f) => w.T.B.SetCharacterTransform(w.Character, new Vector3(100f, 100f, 1.5f), new Quaternion(0f, f, 0f, 1f)));
    }

    [Fact]
    public void Character_shape_drops_non_finite()
    {
        AssertDropped((w, f) => w.T.B.SetCharacterShape(w.Character, f, 0.3f));
        AssertDropped((w, f) => w.T.B.SetCharacterShape(w.Character, 0.45f, f));
    }

    [Fact]
    public void Creators_throw_on_NaN()
    {
        using var t = new JoltTestBackend();
        var box = t.B.CreateBoxShape(new Vector3(0.5f));

        BodyDesc Desc()
        {
            var d = BodyDesc.Default;
            d.Shape = box;
            d.Position = new Vector3(128f, 128f, 5f);
            d.Layer = PhysicsLayer.Dynamic;
            d.MotionType = BodyMotionType.Dynamic;
            return d;
        }

        var p = Desc(); p.Position = V(float.NaN);
        Assert.Throws<ArgumentException>(() => t.B.CreateBody(p));
        var o = Desc(); o.Orientation = new Quaternion(float.NaN, 0f, 0f, 1f);
        Assert.Throws<ArgumentException>(() => t.B.CreateBody(o));
        var lv = Desc(); lv.LinearVelocity = V(float.NaN);
        Assert.Throws<ArgumentException>(() => t.B.CreateBody(lv));
        var av = Desc(); av.AngularVelocity = V(float.PositiveInfinity);
        Assert.Throws<ArgumentException>(() => t.B.CreateBody(av));

        var c = CharacterDesc.Default; c.Position = V(float.NaN);
        Assert.Throws<ArgumentException>(() => t.B.CreateCharacter(c));
        var co = CharacterDesc.Default; co.Position = new Vector3(100f, 100f, 2f); co.Orientation = new Quaternion(0f, 0f, float.NaN, 1f);
        Assert.Throws<ArgumentException>(() => t.B.CreateCharacter(co));
    }

    [Fact]
    public void Queries_with_NaN_origin_or_direction_return_no_hits()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        var box = t.B.CreateBoxShape(new Vector3(0.5f));
        var nan = V(float.NaN);
        var down = -Vector3.UnitZ;
        var above = new Vector3(128f, 128f, 10f);

        Assert.True(t.B.RayCast(above, down, 50f, QueryFilter.All, out _), "control: the finite ray hits the ground");
        Assert.False(t.B.RayCast(nan, down, 50f, QueryFilter.All, out _));
        Assert.False(t.B.RayCast(above, nan, 50f, QueryFilter.All, out _));
        Assert.False(t.B.RayCast(above, down, float.NaN, QueryFilter.All, out _));

        var hits = new RayHit[8];
        Assert.Equal(0, t.B.RayCastAll(nan, down, 50f, QueryFilter.All, hits));
        Assert.Equal(0, t.B.RayCastAll(above, nan, 50f, QueryFilter.All, hits));

        var ids = new BodyId[8];
        Assert.Equal(0, t.B.OverlapSphere(nan, 1f, QueryFilter.All, ids));
        Assert.Equal(0, t.B.OverlapSphere(new Vector3(128f, 128f, 0f), float.NaN, QueryFilter.All, ids));
        Assert.Equal(0, t.B.OverlapBox(nan, new Vector3(1f), Quaternion.Identity, QueryFilter.All, ids));
        Assert.Equal(0, t.B.OverlapBox(new Vector3(128f, 128f, 0f), new Vector3(1f), new Quaternion(float.NaN, 0, 0, 1), QueryFilter.All, ids));

        Assert.False(t.B.ShapeCast(box, nan, Quaternion.Identity, down, 50f, QueryFilter.All, out _));
        Assert.False(t.B.ShapeCast(box, above, Quaternion.Identity, nan, 50f, QueryFilter.All, out _));
    }

    [Fact]
    public void Twenty_bodies_with_NaN_velocity_stay_finite_for_100_frames()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        var box = t.B.CreateBoxShape(new Vector3(0.4f));
        var bodies = Enumerable.Range(0, 20)
            .Select(i => t.Dynamic(box, new Vector3(110f + (i % 5) * 2f, 110f + (i / 5) * 2f, 2f + i * 0.1f), (uint)(1000 + i)))
            .ToArray();

        foreach (var b in bodies)
        {
            t.B.SetBodyLinearVelocity(b, V(float.NaN));
            t.B.SetBodyAngularVelocity(b, new Vector3(0f, float.NaN, 0f));
        }
        Assert.Equal(40, t.B.GetCapacityStats().RejectedNonFinite);

        for (var i = 0; i < 100; i++)
            t.Step();

        foreach (var b in bodies)
        {
            Assert.True(t.B.TryGetBodyState(b, out var s));
            Assert.True(JoltTestBackend.Finite(in s), $"body {s.UserData} went non-finite: {s.Position} {s.LinearVelocity}");
        }
        Assert.True(t.B.RayCast(new Vector3(140f, 140f, 20f), -Vector3.UnitZ, 50f, QueryFilter.All, out var hit));
        Assert.True(float.IsFinite(hit.Point.Z));
    }

    // ------------------------------------------------------------------ vehicles

    [Fact]
    public void Vehicle_NaN_params_leave_the_stored_value_unchanged()
    {
        var v = new VehicleController(null!);
        v.ProcessTypeChange(Vehicle.TYPE_BOAT);
        var buoy = v.GetFloatParam(VehFloatParam.Buoyancy);
        var hover = v.GetFloatParam(VehFloatParam.HoverHeight);
        var fric = v.GetVecParam(VehVectorParam.LinearFrictionTimescale);
        var angFric = v.GetVecParam(VehVectorParam.AngularFrictionTimescale);

        v.ProcessFloatVehicleParam(Vehicle.BUOYANCY, float.NaN);
        v.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, float.PositiveInfinity);
        v.ProcessFloatVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, float.NaN);
        v.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new OmvVector3(1f, float.NaN, 1f));

        Assert.Equal(buoy, v.GetFloatParam(VehFloatParam.Buoyancy));
        Assert.Equal(hover, v.GetFloatParam(VehFloatParam.HoverHeight));
        Assert.Equal(fric, v.GetVecParam(VehVectorParam.LinearFrictionTimescale));
        Assert.Equal(angFric, v.GetVecParam(VehVectorParam.AngularFrictionTimescale));

        v.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 0.5f);
        Assert.Equal(0.5f, v.GetFloatParam(VehFloatParam.Buoyancy));   // control: a finite value still lands
    }

    // ------------------------------------------------------------------ the actor guard helper

    [Fact]
    public void Guard_helper_classifies_values()
    {
        Assert.True(NonFiniteGuard.Ok(new OmvVector3(1, 2, 3)));
        Assert.False(NonFiniteGuard.Ok(new OmvVector3(float.NaN, 0, 0)));
        Assert.False(NonFiniteGuard.Ok(new OmvVector3(0, float.NegativeInfinity, 0)));
        Assert.True(NonFiniteGuard.Ok(OmvQuaternion.Identity));
        Assert.False(NonFiniteGuard.Ok(new OmvQuaternion(0, 0, 0, 0)));
        Assert.False(NonFiniteGuard.Ok(new OmvQuaternion(float.NaN, 0, 0, 1)));
        Assert.True(NonFiniteGuard.OkSize(new OmvVector3(0.01f, 1, 1)));
        Assert.False(NonFiniteGuard.OkSize(new OmvVector3(0, 1, 1)));
        Assert.False(NonFiniteGuard.OkSize(new OmvVector3(1, -1, 1)));
        Assert.False(NonFiniteGuard.OkSize(new OmvVector3(1, 1, float.NaN)));
    }

    [Fact]
    public void Guard_logger_is_rate_limited_to_one_line_per_10_s()
    {
        long last = 0;
        var t0 = new DateTime(2026, 9, 23).Ticks;
        Assert.True(NonFiniteGuard.ShouldLog(ref last, t0));
        Assert.False(NonFiniteGuard.ShouldLog(ref last, t0 + TimeSpan.FromSeconds(1).Ticks));
        Assert.False(NonFiniteGuard.ShouldLog(ref last, t0 + TimeSpan.FromSeconds(9.9).Ticks));
        Assert.True(NonFiniteGuard.ShouldLog(ref last, t0 + TimeSpan.FromSeconds(10).Ticks));
        long other = 0;   // the limit is per actor: another actor's field is independent
        Assert.True(NonFiniteGuard.ShouldLog(ref other, t0 + TimeSpan.FromSeconds(10.5).Ticks));
    }

    // ------------------------------------------------------------------ source-reading: P, C and SP call the guard

    private static string Repo([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));

    /// <summary>The body of a property's <c>set</c> accessor (or of a method), brace-matched.</summary>
    private static string SetterBody(string source, string signature, bool isMethod = false)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' not found; renamed?");
        if (!isMethod)
        {
            at = source.IndexOf("set", at, StringComparison.Ordinal);
            Assert.True(at >= 0);
        }
        var open = source.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..i];
        }
        Assert.Fail("unbalanced braces");
        return "";
    }

    [Theory]
    [InlineData("JoltPrim.cs", "public override Vector3 Position", false, "Ok(value)")]
    [InlineData("JoltPrim.cs", "public override Quaternion Orientation", false, "Ok(value)")]
    [InlineData("JoltPrim.cs", "public override Vector3 Velocity", false, "Ok(value)")]
    [InlineData("JoltPrim.cs", "public override Vector3 RotationalVelocity", false, "Ok(value)")]
    [InlineData("JoltPrim.cs", "public override Vector3 Size", false, "OkSize(value)")]
    [InlineData("JoltCharacter.cs", "public override Vector3 Position", false, "Ok(value)")]
    [InlineData("JoltCharacter.cs", "public override Quaternion Orientation", false, "Ok(value)")]
    [InlineData("JoltCharacter.cs", "public override Vector3 Velocity", false, "Ok(value)")]
    [InlineData("JoltCharacter.cs", "public override Vector3 TargetVelocity", false, "Ok(value)")]
    [InlineData("JoltCharacter.cs", "public override void SetMomentum(", true, "Ok(momentum)")]
    [InlineData("JoltCharacter.cs", "public override Vector3 Size", false, "Ok(value)")]
    public void Actor_setters_call_the_guard_first(string file, string signature, bool isMethod, string check)
    {
        var body = SetterBody(Src("Source", "OpenSim.Region.PhysicsModules.Jolt", file), signature, isMethod);
        var guard = body.IndexOf("NonFiniteGuard." + check, StringComparison.Ordinal);
        Assert.True(guard >= 0, $"{file} {signature} must check NonFiniteGuard.{check}");
        Assert.Contains("NonFiniteGuard.Rejected(", body);
        // The guard runs before the value is stored or pushed to the backend.
        var firstUse = body.IndexOf("_backend.", StringComparison.Ordinal);
        Assert.True(firstUse < 0 || guard < firstUse, "the guard must come before any backend call");
    }
}
