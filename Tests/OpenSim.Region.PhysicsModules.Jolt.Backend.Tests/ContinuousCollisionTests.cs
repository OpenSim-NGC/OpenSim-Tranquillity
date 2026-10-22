/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Continuous collision detection on the backend (<see cref="ContinuousCollision"/>), read back as Jolt holds it
/// (BodyInterface.GetMotionQuality: 0 Discrete, 1 LinearCast). A WhenFast body is LinearCast only for the updates in which
/// its velocity would carry it further in one collision step than Jolt's cast threshold, 0.75 times its shape's inner
/// radius (PhysicsSettings.mLinearCastThreshold; PhysicsSystem::JobIntegrateVelocity). No gravity, so a velocity stays as
/// set. Serial with the other native tests: every backend steps on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ContinuousCollisionTests
{
    private const int Discrete = 0, LinearCast = 1;
    private const float Heartbeat = 1f / 11f;
    private const int CollisionSteps = 6;
    private const float CollisionStep = Heartbeat / CollisionSteps;

    private static JoltPhysicsBackend NewBackend()
    {
        var b = new JoltPhysicsBackend();
        b.Initialize(new PhysicsBackendSettings
        {
            Gravity = Vector3.Zero,
            MaxBodies = 1024,
            MaxBodyPairs = 1024,
            MaxContactConstraints = 1024,
            ThreadCount = 1,
            PositionIterations = 2,
            VelocityIterations = 10,
            CollisionSteps = CollisionSteps,
        });
        return b;
    }

    private static BodyId Body(JoltPhysicsBackend b, ShapeId shape, ContinuousCollision ccd, BodyMotionType motion = BodyMotionType.Dynamic)
        => b.CreateBody(new BodyDesc
        {
            Shape = shape,
            Position = new Vector3(128f, 128f, 50f),
            Orientation = Quaternion.Identity,
            Layer = motion == BodyMotionType.Static ? PhysicsLayer.Static : PhysicsLayer.Dynamic,
            MotionType = motion,
            Density = 1000f,
            StartActive = motion != BodyMotionType.Static,
            Ccd = ccd,
        });

    private static void Step(JoltPhysicsBackend b)
        => b.Step(Heartbeat, new BodyState[16], new CharacterState[1], new ContactReport[64]);

    // A 0.2 m ball: inner radius 0.1, so Jolt casts it once it moves more than 0.075 m in a collision step.
    private static float CastSpeed(float innerRadius) => 0.75f * innerRadius / CollisionStep;

    [Fact]
    public void A_body_created_with_each_mode_holds_the_motion_quality_jolt_reads()
    {
        // The quality is set through the body interface: BodyCreationSettings.MotionQuality's setter in JoltPhysicsSharp
        // 2.19.1 stores neither value, so these read back neither 0 nor 1 when the body is created with it.
        JoltPhysicsBackend b = NewBackend();
        try
        {
            ShapeId ball = b.CreateSphereShape(0.1f);
            Assert.Equal(LinearCast, b.BodyMotionQualityForTest(Body(b, ball, ContinuousCollision.On)));
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(Body(b, ball, ContinuousCollision.Off)));
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(Body(b, ball, ContinuousCollision.WhenFast)));
            // A static body has no motion properties; Jolt reads it as Discrete whatever was asked.
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(Body(b, ball, ContinuousCollision.On, BodyMotionType.Static)));
        }
        finally { b.Dispose(); }
    }

    [Fact]
    public void A_when_fast_body_is_cast_only_in_updates_where_it_moves_past_the_threshold()
    {
        JoltPhysicsBackend b = NewBackend();
        try
        {
            BodyId body = Body(b, b.CreateSphereShape(0.1f), ContinuousCollision.WhenFast);
            float cast = CastSpeed(0.1f);   // 4.95 m/s
            Step(b);
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(body));

            b.SetBodyLinearVelocity(body, new Vector3(cast * 0.95f, 0f, 0f));
            Step(b);
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(body));

            b.SetBodyLinearVelocity(body, new Vector3(0f, cast * 1.05f, 0f));
            Step(b);
            Assert.Equal(LinearCast, b.BodyMotionQualityForTest(body));

            b.SetBodyLinearVelocity(body, new Vector3(0f, 0f, cast * 0.5f));
            Step(b);
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(body));
        }
        finally { b.Dispose(); }
    }

    [Fact]
    public void The_threshold_follows_the_bodys_shape_when_it_changes()
    {
        JoltPhysicsBackend b = NewBackend();
        try
        {
            BodyId body = Body(b, b.CreateSphereShape(0.1f), ContinuousCollision.WhenFast);
            float speed = CastSpeed(0.1f) * 2f;   // twice a 0.2 m ball's, a fifth of a 2 m ball's
            b.SetBodyLinearVelocity(body, new Vector3(speed, 0f, 0f));
            Step(b);
            Assert.Equal(LinearCast, b.BodyMotionQualityForTest(body));

            b.SetBodyShape(body, b.CreateSphereShape(1f), recomputeMass: true);
            b.SetBodyLinearVelocity(body, new Vector3(speed, 0f, 0f));
            Step(b);
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(body));
        }
        finally { b.Dispose(); }
    }

    [Fact]
    public void A_body_woken_by_its_velocity_is_judged_in_the_first_update_after()
    {
        // Created asleep, then given a fast velocity: Jolt wakes it, and it is in the backend's activation queue, not yet
        // its awake set, when the next update starts.
        JoltPhysicsBackend b = NewBackend();
        try
        {
            ShapeId ball = b.CreateSphereShape(0.1f);
            BodyId body = b.CreateBody(new BodyDesc
            {
                Shape = ball, Position = new Vector3(128f, 128f, 50f), Orientation = Quaternion.Identity,
                Layer = PhysicsLayer.Dynamic, MotionType = BodyMotionType.Dynamic, Density = 1000f,
                StartActive = false, Ccd = ContinuousCollision.WhenFast,
            });
            Step(b);
            b.SetBodyLinearVelocity(body, new Vector3(CastSpeed(0.1f) * 10f, 0f, 0f));
            Step(b);
            Assert.Equal(LinearCast, b.BodyMotionQualityForTest(body));
        }
        finally { b.Dispose(); }
    }

    [Fact]
    public void Switching_modes_on_a_live_body_takes_effect_at_once()
    {
        JoltPhysicsBackend b = NewBackend();
        try
        {
            BodyId body = Body(b, b.CreateSphereShape(0.1f), ContinuousCollision.WhenFast);
            Step(b);
            b.SetBodyContinuousCollision(body, ContinuousCollision.On);     // a vehicle: cast however slow it is
            Step(b);
            Assert.Equal(LinearCast, b.BodyMotionQualityForTest(body));
            b.SetBodyContinuousCollision(body, ContinuousCollision.WhenFast);
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(body));
            Step(b);
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(body));
            b.SetBodyContinuousCollision(body, ContinuousCollision.Off);
            b.SetBodyLinearVelocity(body, new Vector3(CastSpeed(0.1f) * 10f, 0f, 0f));
            Step(b);
            Assert.Equal(Discrete, b.BodyMotionQualityForTest(body));
        }
        finally { b.Dispose(); }
    }
}
