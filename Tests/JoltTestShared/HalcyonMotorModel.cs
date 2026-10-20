/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 *
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 *
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

/*
 * A test-only model of the linear and angular motor of InWorldz Halcyon, translated statement by statement from
 * InWorldz.PhysxPhysics/Vehicle/VehicleDynamics.cs (VehicleMotor.Simulate, MoveLinear, MoveAngular, IsLinearMotorStalled,
 * SimulateLinearFriction, SimulateAngularFriction, MovementExpDecay, GetGrowthRate, Mathz.PosNeg).
 * Adapted 2026 by Legion Builds
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The reference the InWorldz motor of the Jolt vehicle controller is held to: Halcyon's linear and angular motor, with the
/// ramp (<c>GetGrowthRate</c>), the decay (<c>MovementExpDecay</c>), the rule that the motor's target replaces a lower velocity
/// (VehicleDynamics.cs:2587-2589 linear, 2778-2780 angular), LIMIT_MOTOR_UP / LIMIT_MOTOR_DOWN, the upward gravity check, the
/// stall check, and the vehicle's own friction timescales, in the order Halcyon's <c>Simulate</c> calls them: the motors
/// (2738), then angular friction (746), then linear friction (754). Every one is worked out from the velocity at the start of
/// the step and added up as a change of velocity, as Halcyon's accumulators do; the physics engine then takes the step.
/// The model is stepped at Halcyon's step of 15 ms (PhysxScene.cs:55, TIMESTEP).
/// <para>
/// What it leaves out, because it is not the motor law or needs PhysX: the engine itself (<see cref="HalcyonBody"/> stands in
/// for it, with contact friction as a constant deceleration); the motor-height rule (VehicleDynamics.cs:2497-2572), so
/// <c>DisableMotors</c> is always false; the linear motor offset (the torque of a force at an offset); banking (2948-3022),
/// so the banking direction is taken as zero; the vertical attractor, which only enters here as the change it asked for
/// (<c>aforces</c>, given by the caller) and as the clamp's timescale; the camera, wind, deflection, ground fix and sled code;
/// the wall-clock reset (<c>CheckResetMotors</c>); the tick smoothing of the step (2554); and PhysX's own thresholds.
/// </para>
/// </summary>
internal sealed class HalcyonMotorModel
{
    // VehicleLimits (VehicleDynamics.cs:57-103).
    public const float MinPhysicsTimestep = 0.0156f;
    public const float MinPhysicsForce = 0.001f;
    public const float ThresholdLinearMotorDeltaV = 0.005f;
    public const float ThresholdAngularMotorDeltaV = (float)Math.PI / 512;
    public const float ThresholdLinearFrictionDeltaV = 0.002f;
    public const float ThresholdAngularFrictionDeltaV = 0.004f;
    public const float ThresholdStictionFactor = 0.002f;
    public const float ThresholdAngularMotorEngaged = 0.9f;
    public const float ThresholdLinearMotorEngaged = 1.0f;
    public const float ThresholdLinearMotorUnstuck = 0.025f;
    public const float ThresholdDelayFubar = 0.06f;
    public const float ThresholdInverseCrossover = -2.010203f;
    public const float MaxLinearVelocity = 200.0f;
    public const float MaxAngularVelocity = (float)Math.PI * 4.0f;
    public const float MaxDecayTimescale = 120.0f;
    public const float MaxAttractTimescale = 500.0f;
    public const float MaxHoverTimescale = 300.0f;
    public const float MaxTimescale = 1000.0f;

    /// <summary>Halcyon's physics step (PhysxScene.cs:55-56).</summary>
    public const float HalcyonStep = 0.015f;

    // Settings.Instance.Gravity: the scene gravity as Halcyon's vehicle code reads it (a negative number).
    public const float Gravity = -9.8f;

    // The vehicle's parameters (VehicleProperties.ParamsVec / ParamsFloat / Flags).
    public Vector3 LinearMotorTimescale = new(1f, 1f, 1f);
    public Vector3 LinearMotorDecayTimescale = new(120f, 120f, 120f);
    public Vector3 AngularMotorTimescale = new(1f, 1f, 1f);
    public Vector3 AngularMotorDecayTimescale = new(120f, 120f, 120f);
    public Vector3 LinearFrictionTimescale = new(1000f, 1000f, 1000f);
    public Vector3 AngularFrictionTimescale = new(1000f, 1000f, 1000f);
    public float VerticalAttractionTimescale = 1000f;
    public float AngularDeflectionTimescale = 1000f;
    public float AngularDeflectionEfficiency = 0f;
    public float Buoyancy;
    public float HoverTimescale = 1000f;
    public bool HoverFlag = false;
    public bool LimitMotorUp, LimitMotorDown = false, TorqueWorldZ = false;
    public VehicleType Type = VehicleType.Car;

    // The vehicle's dynamics (VehicleDynamicsState).
    public Vector3 LinearDirection, AngularDirection;
    public Vector3 LinearTargetVelocity, AngularTargetVelocity;
    public float LinearDecayIndex = MaxDecayTimescale * 10.0f, AngularDecayIndex = MaxDecayTimescale * 10.0f;
    public Vector3 LastPosition, ShortTermPositionDelta;

    /// <summary>True (Halcyon as written): the rule that the motor's target replaces a lower velocity acts on every axis. False: it
    /// acts only on an axis the motor has been given a direction on at some time.</summary>
    public bool ReplaceOnUndrivenAxes = true;
    private bool drivenX, drivenY, drivenZ;

    // The velocities and rotation the step reads (VehicleMotor.MetricsInit).
    private Quaternion rotation = Quaternion.Identity;
    private Vector3 worldLinearVel, worldAngularVel, localLinearVel, localAngularVel;
    private float timeStep;
    private Vector3 position;

    private bool linearMotorStalled, linearMotorStallChecked;

    /// <summary>Mathz.PosNeg (VehicleDynamics.cs:46-51): -1 if below 0, else +1.</summary>
    public static int PosNeg(float f)
    {
        int sign = Math.Sign(f);
        return sign == 0 ? 1 : sign;
    }

    /// <summary>VehicleMotor.MovementExpDecay (VehicleDynamics.cs:2201-2212).</summary>
    public static float MovementExpDecay(float timeindex, float timescale)
    {
        if (timeindex <= 0) return 1.0f;
        float factor = (float)Math.Pow(Math.E, (double)(-timeindex / timescale));
        if (factor < ThresholdStictionFactor) factor = 0.0f;
        return factor;
    }

    /// <summary>VehicleMotor.MovementLimitedGrowth (2218-2222).</summary>
    public static float MovementLimitedGrowth(float timeindex, float timescale)
        => 1.0f - MovementExpDecay(timeindex, timescale);

    /// <summary>VehicleMotor.GetGrowthRate (2236-2283).</summary>
    public static float GetGrowthRate(float svel, float evel, float timescale)
    {
        float elog;
        if (svel * evel > 0)
        {
            evel = Math.Abs(evel);
            svel = Math.Abs(svel);
            elog = (float)Math.Log(evel / svel);
        }
        else
        {
            if (evel == 0 && svel == 0) return 0;
            if (evel == 0)
                elog = -(float)Math.Log(1.0 + Math.Abs(svel));
            else if (svel == 0)
                elog = (float)Math.Log(1.0 + Math.Abs(evel));
            else
            {
                if (Math.Abs(svel) > 0.3f)
                    elog = -(float)Math.Log(1.0f + Math.Abs(svel - evel));
                else return ThresholdInverseCrossover;
            }
        }
        return elog / timescale;
    }

    /// <summary>VehicleMotor.MoveLinear (3047-3078), the script's linear motor direction.</summary>
    public void MoveLinear(Vector3 direction)
    {
        // Scale the target if the motor has been decaying.
        if (LinearDecayIndex > ThresholdLinearMotorEngaged)
        {
            LinearTargetVelocity.X *= MovementExpDecay(LinearDecayIndex, LinearMotorDecayTimescale.X);
            LinearTargetVelocity.Y *= MovementExpDecay(LinearDecayIndex, LinearMotorDecayTimescale.Y);
            LinearTargetVelocity.Z *= MovementExpDecay(LinearDecayIndex, LinearMotorDecayTimescale.Z);
        }

        LinearDecayIndex = 0.0f;

        // SL cheat: delay the decay by a tiny amount.
        float moahfubar = 0.0f;
        Vector3 mts = LinearMotorTimescale;
        if (Vector3.Mag(mts) < 0.9)
            moahfubar = 1.0f - Vector3.Mag(mts);
        LinearDecayIndex = MinPhysicsTimestep * (1.0f - moahfubar) - ThresholdDelayFubar * moahfubar;

        LinearDirection = direction;
        drivenX |= direction.X != 0; drivenY |= direction.Y != 0; drivenZ |= direction.Z != 0;
    }

    /// <summary>VehicleMotor.MoveAngular (3080-3122), the script's angular motor direction.</summary>
    public void MoveAngular(Vector3 direction)
    {
        if (AngularDecayIndex > ThresholdAngularMotorEngaged)
        {
            AngularTargetVelocity.X *= MovementExpDecay(AngularDecayIndex, AngularMotorDecayTimescale.X);
            AngularTargetVelocity.Y *= MovementExpDecay(AngularDecayIndex, AngularMotorDecayTimescale.Y);
            AngularTargetVelocity.Z *= MovementExpDecay(AngularDecayIndex, AngularMotorDecayTimescale.Z);
        }

        AngularDecayIndex = 0.0f;

        float moahfubar = 0.0f;
        Vector3 mts = AngularMotorTimescale;
        if (Vector3.Mag(mts) < 0.9)
            moahfubar = 1.0f - Vector3.Mag(mts);
        AngularDecayIndex = MinPhysicsTimestep * (1.0f - moahfubar) - ThresholdDelayFubar * moahfubar;

        // SL induces an angular rate limiter based on the angular deflection timescale.
        float ts = AngularDeflectionTimescale;
        float de = AngularDeflectionEfficiency;
        float sl = (ts + 0.001f) / (de + 0.00001f);
        if (sl < 1.0f)
            direction *= Math.Max(sl, 0.1f);

        AngularDirection = direction;
    }

    /// <summary>VehicleMotor.IsLinearMotorStalled (2106-2145), checked at most once a step (ClearLinearMotorStalled, 2100).</summary>
    public bool IsLinearMotorStalled()
    {
        Vector3 posdelta = position - LastPosition;
        float currspeed = Vector3.Mag(worldLinearVel);
        float stposdelta;
        float stspeed;

        if (!linearMotorStallChecked)
        {
            ShortTermPositionDelta = ShortTermPositionDelta * 0.8f + posdelta * 0.2f;
            linearMotorStallChecked = true;

            if (LinearDecayIndex > ThresholdLinearMotorEngaged && AngularDecayIndex > ThresholdAngularMotorEngaged)
            {
                stposdelta = Vector3.Mag(ShortTermPositionDelta);
                stspeed = stposdelta / timeStep;
                if (stspeed < currspeed * 0.5f)
                {
                    if (currspeed >= ThresholdLinearMotorUnstuck)
                        linearMotorStalled = true;
                }
            }
        }
        return linearMotorStalled;
    }

    /// <summary>The changes of velocity (world frame) one step asks the engine for.</summary>
    public readonly record struct Changes(Vector3 Linear, Vector3 Angular);

    /// <summary>
    /// VehicleDynamics.Simulate (540-765), reduced to the motors and the two frictions, from the body's state at the start of the
    /// step. <paramref name="aforces"/> is what the vertical attractor changed this step (zero when it is off).
    /// </summary>
    public Changes Simulate(float step, Vector3 worldPosition, Vector3 worldLinearVelocity, Vector3 worldAngularVelocity,
        Quaternion bodyRotation, Vector3 aforces)
    {
        timeStep = step;
        rotation = bodyRotation;
        position = worldPosition;
        worldLinearVel = worldLinearVelocity;
        worldAngularVel = worldAngularVelocity;
        localLinearVel = worldLinearVel * Quaternion.Inverse(rotation);
        localAngularVel = worldAngularVel * Quaternion.Inverse(rotation);
        linearMotorStalled = false;           // ClearLinearMotorStalled (642)
        linearMotorStallChecked = false;

        Vector3 linear = Vector3.Zero;
        Vector3 torqueVel = Vector3.Zero;

        SimulateMotors(aforces, ref linear, ref torqueVel);
        SimulateAngularFriction(ref torqueVel);
        SimulateLinearFriction(ref linear);

        // TorqueFini (2444-2469): too small a torque is taken as zero.
        if (Math.Abs(torqueVel.X) < MinPhysicsForce) torqueVel.X = 0.0f;
        if (Math.Abs(torqueVel.Y) < MinPhysicsForce) torqueVel.Y = 0.0f;
        if (Math.Abs(torqueVel.Z) < MinPhysicsForce) torqueVel.Z = 0.0f;

        LastPosition = worldPosition;
        return new Changes(linear, torqueVel);
    }

    // VehicleMotor.Simulate (2475-3039).
    private void SimulateMotors(Vector3 aforces, ref Vector3 linearDelta, ref Vector3 torqueVel)
    {
        Vector3 rfactor, dfactor, lastvel, adjvel, newvel, worldvel, timescale, dirsign;
        float buoy = Buoyancy;
        float hoverts = HoverTimescale;
        bool DisableMotors = false;

        // ---- Linear motor (2576-2729) ----
        lastvel = localLinearVel;
        adjvel = lastvel;

        dirsign.X = PosNeg(LinearDirection.X);
        dirsign.Y = PosNeg(LinearDirection.Y);
        dirsign.Z = PosNeg(LinearDirection.Z);

        // "The target ensures forward progress." (2585-2589)
        if (dirsign.X * (LinearTargetVelocity.X - adjvel.X) >= 0 && (ReplaceOnUndrivenAxes || drivenX)) adjvel.X = LinearTargetVelocity.X;
        if (dirsign.Y * (LinearTargetVelocity.Y - adjvel.Y) >= 0 && (ReplaceOnUndrivenAxes || drivenY)) adjvel.Y = LinearTargetVelocity.Y;
        if (dirsign.Z * (LinearTargetVelocity.Z - adjvel.Z) >= 0 && (ReplaceOnUndrivenAxes || drivenZ)) adjvel.Z = LinearTargetVelocity.Z;

        timescale = LinearMotorTimescale;
        rfactor.X = GetGrowthRate(adjvel.X, LinearDirection.X, timescale.X / timeStep);
        rfactor.Y = GetGrowthRate(adjvel.Y, LinearDirection.Y, timescale.Y / timeStep);
        rfactor.Z = GetGrowthRate(adjvel.Z, LinearDirection.Z, timescale.Z / timeStep);

        timescale = LinearMotorDecayTimescale;
        dfactor.X = MovementExpDecay(LinearDecayIndex, timescale.X);
        dfactor.Y = MovementExpDecay(LinearDecayIndex, timescale.Y);
        dfactor.Z = MovementExpDecay(LinearDecayIndex, timescale.Z);
        LinearDecayIndex += timeStep;

        if (Vector3.Mag(dfactor) == 0)
        {
            LinearTargetVelocity = localLinearVel;
        }
        else
        {
            newvel.X = (adjvel.X + adjvel.X * rfactor.X);
            newvel.Y = (adjvel.Y + adjvel.Y * rfactor.Y);
            newvel.Z = (adjvel.Z + adjvel.Z * rfactor.Z);

            if (rfactor.X == ThresholdInverseCrossover) rfactor.X = -rfactor.X;
            if (rfactor.Y == ThresholdInverseCrossover) rfactor.Y = -rfactor.Y;
            if (rfactor.Z == ThresholdInverseCrossover) rfactor.Z = -rfactor.Z;

            if (rfactor.X > 0 && LinearDirection.X != 0 && Math.Abs(newvel.X) < ThresholdLinearMotorDeltaV)
                newvel.X = dirsign.X * ThresholdLinearMotorDeltaV * 8;
            if (rfactor.Y > 0 && LinearDirection.Y != 0 && Math.Abs(newvel.Y) < ThresholdLinearMotorDeltaV)
                newvel.Y = dirsign.Y * ThresholdLinearMotorDeltaV * 8;
            if (rfactor.Z > 0 && LinearDirection.Z != 0 && Math.Abs(newvel.Z) < ThresholdLinearMotorDeltaV)
                newvel.Z = dirsign.Z * ThresholdLinearMotorDeltaV * 8;

            if (LinearDirection.X * newvel.X < 0 || rfactor.X < 0)
                LinearTargetVelocity.X = newvel.X;
            else
                LinearTargetVelocity.X = Math.Clamp(newvel.X, -Math.Abs(LinearDirection.X), Math.Abs(LinearDirection.X));

            if (LinearDirection.Y * newvel.Y < 0 || rfactor.Y < 0)
                LinearTargetVelocity.Y = newvel.Y;
            else
                LinearTargetVelocity.Y = Math.Clamp(newvel.Y, -Math.Abs(LinearDirection.Y), Math.Abs(LinearDirection.Y));

            if (LinearDirection.Z * newvel.Z < 0 || rfactor.Z < 0)
                LinearTargetVelocity.Z = newvel.Z;
            else
                LinearTargetVelocity.Z = Math.Clamp(newvel.Z, -Math.Abs(LinearDirection.Z), Math.Abs(LinearDirection.Z));

            newvel.X = Math.Clamp(newvel.X, -MaxLinearVelocity, MaxLinearVelocity);
            newvel.Y = Math.Clamp(newvel.Y, -MaxLinearVelocity, MaxLinearVelocity);
            newvel.Z = Math.Clamp(newvel.Z, -MaxLinearVelocity, MaxLinearVelocity);

            // Release motors when the delta is zero and the direction is zero.
            if (LinearTargetVelocity.X == 0 && LinearDirection.X == 0) newvel.X = lastvel.X;
            if (LinearTargetVelocity.Y == 0 && LinearDirection.Y == 0) newvel.Y = lastvel.Y;
            if (LinearTargetVelocity.Z == 0 && LinearDirection.Z == 0) newvel.Z = lastvel.Z;

            // The velocity is already past the motor's: the motor adds no power.
            if (rfactor.X >= 0 && (dirsign.X * (lastvel.X - newvel.X) > 0)) dfactor.X = 0;
            if (rfactor.Y >= 0 && (dirsign.Y * (lastvel.Y - newvel.Y) > 0)) dfactor.Y = 0;
            if (rfactor.Z >= 0 && (dirsign.Z * (lastvel.Z - newvel.Z) > 0)) dfactor.Z = 0;

            // Accelerate the decay when stalled.
            if (Vector3.Mag(dfactor) != 0 && IsLinearMotorStalled())
            {
                dfactor = Vector3.Zero;
                LinearDecayIndex = MaxDecayTimescale * 100.0f;
                LinearTargetVelocity = Vector3.Zero;
            }

            // Apply decays to each axis.
            newvel.X = (newvel.X * dfactor.X) + lastvel.X * (1.0f - dfactor.X);
            newvel.Y = (newvel.Y * dfactor.Y) + lastvel.Y * (1.0f - dfactor.Y);
            newvel.Z = (newvel.Z * dfactor.Z) + lastvel.Z * (1.0f - dfactor.Z);

            worldvel = newvel * rotation;

            if (Math.Abs(worldvel.X) >= ThresholdLinearMotorDeltaV ||
                Math.Abs(worldvel.Y) >= ThresholdLinearMotorDeltaV ||
                Math.Abs(worldvel.Z) >= ThresholdLinearMotorDeltaV)
            {
                worldvel -= worldLinearVel;

                if (LimitMotorUp)
                {
                    if (worldvel.Z > 0) worldvel.Z = 0;
                }

                if (LimitMotorDown)
                {
                    if (worldvel.Z < 0) worldvel.Z = 0;
                }

                // Gravity check. Any up Z-forces less than the effective gravity are nulled.
                if (worldvel.Z > 0)
                {
                    worldvel.Z += Gravity * (1.0f - buoy);
                    if (worldvel.Z < 0) worldvel.Z = 0;
                }

                // Hover moderation.
                if (Math.Abs(worldvel.Z) > ThresholdLinearMotorDeltaV && HoverFlag && hoverts < MaxHoverTimescale)
                    LinearDecayIndex += 10.0f;

                if (!DisableMotors)
                    linearDelta += worldvel;           // AddForce(worldvel, VelocityChange): the accumulator is the physics step's
            }
        }

        // ---- Angular motor (2733-2945) ----
        worldvel = Vector3.Zero;
        float angularz = 0;

        timescale = AngularMotorDecayTimescale;
        dfactor.X = MovementExpDecay(AngularDecayIndex, timescale.X);
        dfactor.Y = MovementExpDecay(AngularDecayIndex, timescale.Y);
        dfactor.Z = MovementExpDecay(AngularDecayIndex, timescale.Z);
        AngularDecayIndex += timeStep;

        if (Vector3.Mag(dfactor) == 0 || DisableMotors)
        {
            AngularTargetVelocity = Vector3.Zero;
        }
        else
        {
            lastvel = localAngularVel;
            Vector3 ztorque = Vector3.Zero;

            if (TorqueWorldZ)
            {
                lastvel = worldAngularVel;
                ztorque.Z = lastvel.Z;
                lastvel.Z = 0;
                lastvel = lastvel * Quaternion.Inverse(rotation);

                ztorque.X = lastvel.Z;
                lastvel.Z = ztorque.Z;
            }

            adjvel = lastvel;

            dirsign.X = PosNeg(AngularDirection.X);
            dirsign.Y = PosNeg(AngularDirection.Y);
            dirsign.Z = PosNeg(AngularDirection.Z);

            // "The target ensures forward progress." The angular test is strict (2778-2780), the linear one is not.
            if (dirsign.X * (AngularTargetVelocity.X - adjvel.X) > 0) adjvel.X = AngularTargetVelocity.X;
            if (dirsign.Y * (AngularTargetVelocity.Y - adjvel.Y) > 0) adjvel.Y = AngularTargetVelocity.Y;
            if (dirsign.Z * (AngularTargetVelocity.Z - adjvel.Z) > 0) adjvel.Z = AngularTargetVelocity.Z;

            timescale = AngularMotorTimescale;
            rfactor.X = GetGrowthRate(adjvel.X, AngularDirection.X, timescale.X / timeStep);
            rfactor.Y = GetGrowthRate(adjvel.Y, AngularDirection.Y, timescale.Y / timeStep);
            rfactor.Z = GetGrowthRate(adjvel.Z, AngularDirection.Z, timescale.Z / timeStep);

            newvel.X = (adjvel.X + adjvel.X * rfactor.X);
            newvel.Y = (adjvel.Y + adjvel.Y * rfactor.Y);
            newvel.Z = (adjvel.Z + adjvel.Z * rfactor.Z);

            if (rfactor.X == ThresholdInverseCrossover) rfactor.X = -rfactor.X;
            if (rfactor.Y == ThresholdInverseCrossover) rfactor.Y = -rfactor.Y;
            if (rfactor.Z == ThresholdInverseCrossover) rfactor.Z = -rfactor.Z;

            if (rfactor.X > 0 && AngularDirection.X != 0 && Math.Abs(newvel.X) < ThresholdAngularMotorDeltaV)
                newvel.X = dirsign.X * ThresholdAngularMotorDeltaV * 8;
            if (rfactor.Y > 0 && AngularDirection.Y != 0 && Math.Abs(newvel.Y) < ThresholdAngularMotorDeltaV)
                newvel.Y = dirsign.Y * ThresholdAngularMotorDeltaV * 8;
            if (rfactor.Z > 0 && AngularDirection.Z != 0 && Math.Abs(newvel.Z) < ThresholdAngularMotorDeltaV)
                newvel.Z = dirsign.Z * ThresholdAngularMotorDeltaV * 8;

            // The clamp against the vertical attractor (2809-2856).
            float vtimescale = Math.Max(VerticalAttractionTimescale, timeStep);
            if (vtimescale < MaxAttractTimescale)
            {
                float velclamp;
                if (Type == VehicleType.Car || Type == VehicleType.Motorcycle)
                    velclamp = (float)Math.PI * 1.1f;
                else if (Type == VehicleType.Boat || Type == VehicleType.Sailboat)
                    velclamp = (float)Math.PI * 0.95f;
                else
                    velclamp = (float)Math.PI * 0.8f;

                if (vtimescale < 1.0f)
                    velclamp = velclamp * vtimescale;

                if (vtimescale < 1.0f)
                    vtimescale = 1.0f;
                float voverthrust = (float)(Math.Log(vtimescale + 0.06) / Math.Log(MaxAttractTimescale));

                float vvel = Math.Clamp(newvel.X, -velclamp, velclamp);
                if (newvel.X != vvel) vvel = vvel + (newvel.X - vvel) * voverthrust;
                newvel.X = vvel;

                vvel = Math.Clamp(newvel.Y, -velclamp, velclamp);
                if (newvel.Y != vvel) vvel = vvel + (newvel.Y - vvel) * voverthrust;
                newvel.Y = vvel;
            }

            if (AngularDirection.X * newvel.X < 0 || rfactor.X < 0)
                AngularTargetVelocity.X = newvel.X;
            else
                AngularTargetVelocity.X = Math.Clamp(newvel.X, -Math.Abs(AngularDirection.X), Math.Abs(AngularDirection.X));

            if (AngularDirection.Y * newvel.Y < 0 || rfactor.Y < 0)
                AngularTargetVelocity.Y = newvel.Y;
            else
                AngularTargetVelocity.Y = Math.Clamp(newvel.Y, -Math.Abs(AngularDirection.Y), Math.Abs(AngularDirection.Y));

            if (AngularDirection.Z * newvel.Z < 0 || rfactor.Z < 0)
                AngularTargetVelocity.Z = newvel.Z;
            else
                AngularTargetVelocity.Z = Math.Clamp(newvel.Z, -Math.Abs(AngularDirection.Z), Math.Abs(AngularDirection.Z));

            newvel.X = Math.Clamp(newvel.X, -MaxAngularVelocity, MaxAngularVelocity);
            newvel.Y = Math.Clamp(newvel.Y, -MaxAngularVelocity, MaxAngularVelocity);
            newvel.Z = Math.Clamp(newvel.Z, -MaxAngularVelocity, MaxAngularVelocity);

            // Release motors when the delta is zero and the direction is zero (2881-2883, tested on dfactor).
            if (dfactor.X == 0 && AngularDirection.X == 0) newvel.X = lastvel.X;
            if (dfactor.Y == 0 && AngularDirection.Y == 0) newvel.Y = lastvel.Y;
            if (dfactor.Z == 0 && AngularDirection.Z == 0) newvel.Z = lastvel.Z;

            if ((aforces.X != 0 && AngularDirection.X == 0) || (rfactor.X >= 0 && (dirsign.X * (lastvel.X - newvel.X) > 0))) dfactor.X = 0;
            if ((aforces.Y != 0 && AngularDirection.Y == 0) || (rfactor.Y >= 0 && (dirsign.Y * (lastvel.Y - newvel.Y) > 0))) dfactor.Y = 0;
            if ((aforces.Z != 0 && AngularDirection.Z == 0) || (rfactor.Z >= 0 && (dirsign.Z * (lastvel.Z - newvel.Z) > 0))) dfactor.Z = 0;

            if (Vector3.Mag(dfactor) != 0 && IsLinearMotorStalled())
            {
                dfactor = Vector3.Zero;
                AngularDecayIndex = MaxDecayTimescale * 100.0f;
                AngularTargetVelocity = Vector3.Zero;
            }

            newvel.X = (newvel.X * dfactor.X) + lastvel.X * (1.0f - dfactor.X);
            newvel.Y = (newvel.Y * dfactor.Y) + lastvel.Y * (1.0f - dfactor.Y);
            newvel.Z = (newvel.Z * dfactor.Z) + lastvel.Z * (1.0f - dfactor.Z);

            if (TorqueWorldZ)
            {
                ztorque.Z = newvel.Z;
                newvel.Z = ztorque.X;
                newvel.Z = 0;
                ztorque.X = 0;

                worldvel = newvel * rotation;
                worldvel += ztorque;
            }
            else
            {
                worldvel = newvel * rotation;
            }

            worldvel -= worldAngularVel;

            // Z is held until banking has been determined, since they are blended (banking is zero here).
            angularz = worldvel.Z;
            worldvel.Z = 0;

            if (Math.Abs(worldvel.X) >= ThresholdAngularMotorDeltaV ||
                Math.Abs(worldvel.Y) >= ThresholdAngularMotorDeltaV ||
                Math.Abs(worldvel.Z) >= ThresholdAngularMotorDeltaV)
            {
                torqueVel += worldvel;
            }
        }

        // ---- Banking, direction zero (3016-3022), and the blend of angular Z (3025-3038) ----
        worldvel = Vector3.Zero;
        worldvel.Z = 0 + angularz;
        if (Math.Abs(worldvel.Z) >= ThresholdAngularMotorDeltaV)
            torqueVel += worldvel;
    }

    // VehicleDynamics.SimulateAngularFriction (1402-1458).
    private void SimulateAngularFriction(ref Vector3 torqueVel)
    {
        Vector3 newvel, worldvel;
        Vector3 frictionTS = AngularFrictionTimescale;

        if (frictionTS.X < MaxTimescale || frictionTS.Y < MaxTimescale || frictionTS.Z < MaxTimescale)
        {
            frictionTS.X = Math.Max(frictionTS.X, timeStep);
            frictionTS.Y = Math.Max(frictionTS.Y, timeStep);
            frictionTS.Z = Math.Max(frictionTS.Z, timeStep);

            frictionTS.X = MovementLimitedGrowth(timeStep, frictionTS.X);
            frictionTS.Y = MovementLimitedGrowth(timeStep, frictionTS.Y);
            frictionTS.Z = MovementLimitedGrowth(timeStep, frictionTS.Z);

            newvel.X = -localAngularVel.X * frictionTS.X;
            newvel.Y = -localAngularVel.Y * frictionTS.Y;
            newvel.Z = -localAngularVel.Z * frictionTS.Z;

            newvel.X = Math.Clamp(newvel.X, -MaxAngularVelocity, MaxAngularVelocity);
            newvel.Y = Math.Clamp(newvel.Y, -MaxAngularVelocity, MaxAngularVelocity);
            newvel.Z = Math.Clamp(newvel.Z, -MaxAngularVelocity, MaxAngularVelocity);

            if (Math.Abs(newvel.X) < MinPhysicsForce) newvel.X = PosNeg(newvel.X) * MinPhysicsForce;
            if (Math.Abs(newvel.Y) < MinPhysicsForce) newvel.Y = PosNeg(newvel.Y) * MinPhysicsForce;
            if (Math.Abs(newvel.Z) < MinPhysicsForce) newvel.Z = PosNeg(newvel.Z) * MinPhysicsForce;

            worldvel = newvel * rotation;

            if (Math.Abs(localAngularVel.X) >= MinPhysicsForce * 2 ||
                Math.Abs(localAngularVel.Y) >= MinPhysicsForce * 2 ||
                Math.Abs(localAngularVel.Z) >= MinPhysicsForce * 2)
            {
                if (Vector3.Mag(worldAngularVel) < ThresholdAngularFrictionDeltaV)
                    worldvel = -worldAngularVel;

                if (Vector3.Mag(worldvel) > 0)
                    torqueVel += worldvel;
            }
        }
    }

    // VehicleDynamics.SimulateLinearFriction (1460-1517).
    private void SimulateLinearFriction(ref Vector3 linearDelta)
    {
        Vector3 newvel, worldvel;
        Vector3 frictionTS = LinearFrictionTimescale;

        if (frictionTS.X < MaxTimescale || frictionTS.Y < MaxTimescale || frictionTS.Z < MaxTimescale)
        {
            frictionTS.X = Math.Max(frictionTS.X, timeStep);
            frictionTS.Y = Math.Max(frictionTS.Y, timeStep);
            frictionTS.Z = Math.Max(frictionTS.Z, timeStep);

            frictionTS.X = MovementLimitedGrowth(timeStep, frictionTS.X);
            frictionTS.Y = MovementLimitedGrowth(timeStep, frictionTS.Y);
            frictionTS.Z = MovementLimitedGrowth(timeStep, frictionTS.Z);

            newvel.X = -localLinearVel.X * frictionTS.X;
            newvel.Y = -localLinearVel.Y * frictionTS.Y;
            newvel.Z = -localLinearVel.Z * frictionTS.Z;

            if (Math.Abs(newvel.X) < MinPhysicsForce) newvel.X = PosNeg(newvel.X) * MinPhysicsForce;
            if (Math.Abs(newvel.Y) < MinPhysicsForce) newvel.Y = PosNeg(newvel.Y) * MinPhysicsForce;
            if (Math.Abs(newvel.Z) < MinPhysicsForce) newvel.Z = PosNeg(newvel.Z) * MinPhysicsForce;

            worldvel = newvel * rotation;

            // Cheat: Do not fight gravity.
            if (worldvel.Z > 0) worldvel.Z = 0;

            if (Math.Abs(localLinearVel.X) >= MinPhysicsForce * 2 ||
                Math.Abs(localLinearVel.Y) >= MinPhysicsForce * 2 ||
                Math.Abs(localLinearVel.Z) >= MinPhysicsForce * 2)
            {
                if (Vector3.Mag(worldLinearVel) < ThresholdLinearFrictionDeltaV)
                    worldvel = -worldLinearVel;

                if (Vector3.Mag(worldvel) > 0)
                    linearDelta += worldvel;
            }
        }
    }
}

/// <summary>
/// The stand-in for PhysX under <see cref="HalcyonMotorModel"/>: a point body on a plane tilted <c>SlopeDegrees</c> (positive
/// is down the way the body's x axis points). Each 15 ms step it takes the model's changes of velocity, adds gravity, keeps the
/// body on the plane (the contact takes the velocity's part along the plane's normal), and takes contact friction as a
/// constant deceleration <c>Mu * g * cos(slope)</c> against the motion, to a stop at most. There is no static friction,
/// restitution, rotation or contact friction on the turn: it is a model of the motor against a friction, not of a car.
/// </summary>
internal sealed class HalcyonBody
{
    public readonly HalcyonMotorModel Motor;
    public readonly double Mu;
    public readonly double SlopeRad;
    public Vector3 Position, LinearVelocity, AngularVelocity;
    public Quaternion Rotation;
    public float Yaw;
    public double Time;

    public HalcyonBody(HalcyonMotorModel motor, double mu, double slopeDegrees, float heading = 0f)
    {
        Motor = motor;
        Yaw = heading;
        Mu = mu;
        SlopeRad = slopeDegrees * Math.PI / 180.0;
        // A body on a slope pitches with it: positive is nose down, about the y axis.
        Rotation = Quaternion.CreateFromEulers(0f, (float)SlopeRad, Yaw);
    }

    /// <summary>One step of <paramref name="dt"/> seconds: the model's motors and frictions, then the engine.</summary>
    public void Step(float dt = HalcyonMotorModel.HalcyonStep)
    {
        HalcyonMotorModel.Changes c = Motor.Simulate(dt, Position, LinearVelocity, AngularVelocity, Rotation, Vector3.Zero);
        LinearVelocity += c.Linear;
        AngularVelocity += c.Angular;

        // The engine: gravity, then the contact holds the body to the plane and rubs against its motion.
        LinearVelocity += new Vector3(0f, 0f, HalcyonMotorModel.Gravity * dt);
        var normal = new Vector3((float)Math.Sin(SlopeRad), 0f, (float)Math.Cos(SlopeRad));
        LinearVelocity -= normal * Vector3.Dot(LinearVelocity, normal);
        float speed = LinearVelocity.Length();
        if (speed > 0f)
        {
            float slow = (float)(Mu * -HalcyonMotorModel.Gravity * Math.Cos(SlopeRad) * dt);
            LinearVelocity *= Math.Max(0f, speed - slow) / speed;
        }

        Position += LinearVelocity * dt;
        // The turn is about the world's vertical, applied to the heading (a level plane is the only one it is meant for).
        Yaw += AngularVelocity.Z * dt;
        Rotation = Quaternion.CreateFromEulers(0f, (float)SlopeRad, Yaw);
        Time += dt;
    }
}
