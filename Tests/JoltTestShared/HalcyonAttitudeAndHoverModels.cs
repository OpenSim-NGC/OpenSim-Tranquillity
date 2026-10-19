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
 * Test-only models of InWorldz Halcyon's vertical attractor, banking, hover, move-target shift on a region crossing and
 * size checks, translated statement by statement from
 * InWorldz.PhysxPhysics/Vehicle/VehicleDynamics.cs (SimulateVerticalAttractor 1218-1330, SimulateBankingToYaw 1338-1401,
 * the banking turn motor inside VehicleMotor.Simulate 2949-3040, SimulateAngularFriction 1402-1458),
 * InWorldz.PhysxPhysics/PhysxPrim.pid.cs (DoHoverTarget 207-280, DoHoverDamping 281-296, DoHover 297-336),
 * InWorldz.PhysxPhysics/PhysxPrim.cs (SuspendPhysicsSync 3140-3185),
 * InWorldz.PhysxPhysics/PhysxPrim.stability.cs (329-410) and PhysUtil.cs (Rot2Euler, Rot2Angle).
 * Adapted 2026 by Legion Builds
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;

// Several test projects compile this file, and each sets only the fields its tests use; the rest keep their defaults.
#pragma warning disable CS0649

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Halcyon's attitude laws on a free rigid body, stepped at the length the caller chooses (15 ms is Halcyon's own step,
/// PhysxScene.cs:55-56): the vertical attractor with its damping and the escalation while overturned, banking to yaw and the
/// banking turn motor, and angular friction. Every term is worked out from the angular velocity at the start of the step and
/// the terms are added together as changes of velocity, as Halcyon's torque accumulators do. The engine's part is a stand-in:
/// the angular velocity is held to PhysX's maximum for the actor (PhysxActorFactory.cs:68, four times PhysX's default of 7 rad/s),
/// loses its angular damping (PhysxActorFactory.cs:41) and turns the body. What it leaves out: the angular motor (taken as
/// released), the linear motor, and every force that is not a torque.
/// </summary>
internal sealed class HalcyonAttitudeModel
{
    // VehicleLimits (VehicleDynamics.cs:57-112).
    public const float ThresholdAttractorAngle = (float)Math.PI / 256.0f;
    public const float ThresholdOverturnAngle = (float)Math.PI * 0.60f;
    public const float ThresholdBankAngle = (float)Math.PI / 128.0f;
    public const float ThresholdAngularMotorDeltaV = (float)Math.PI / 512;
    public const float ThresholdInverseCrossover = -2.010203f;
    public const float MaxLegacyLinearVelocity = 30.0f;
    public const float MaxAngularVelocity = (float)Math.PI * 4.0f;
    public const float MaxAttractTimescale = 500.0f;
    public const float MaxAttractDormancy = 1.5f;
    public const float MaxTimescale = 1000.0f;
    public const float MinPhysicsForce = 0.001f;
    public const float ThresholdAngularFrictionDeltaV = 0.004f;
    public const float ThresholdStictionFactor = 0.002f;

    /// <summary>The actor's angular speed limit (PhysxActorFactory.cs:68) and angular damping (:41).</summary>
    public float ActorMaxAngularSpeed = 28f;
    public float ActorAngularDamping = 0.05f;

    public VehicleType Type = VehicleType.Car;
    public bool LimitRollOnly;
    public float AttractTimescale = 1000f, AttractEfficiency;
    public float BankingEfficiency, BankingMix, BankingTimescale = 1000f, BankingAzimuth = (float)Math.PI / 2, InvertedBankingModifier = 1f;
    public Vector3 AngularFrictionTimescale = new(1000f, 1000f, 1000f);

    /// <summary>The vehicle's speed along its own x axis, which dynamic banking reads (a constant here).</summary>
    public float LocalSpeedX;

    public float VerticalForceAdjust = 1.0f, LastVerticalAngle, BankingDirection, BankingTargetVelocity;

    /// <summary>The largest strength factor the escalation has reached.</summary>
    public float MaxVerticalForceAdjust = 1.0f;
    private long lastVerticalFrame, frame;

    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 WorldAngularVel;

    /// <summary>True: the body does not turn (a vehicle held at its attitude).</summary>
    public bool HoldOrientation;

    /// <summary>The tilt the attractor measured in the last step.</summary>
    public float LastAngle;

    public double Time;

    private static int PosNeg(float f) => f < 0 ? -1 : 1;

    // PhysUtil.Rot2Angle (PhysUtil.cs:188-205).
    private static float Rot2Angle(Quaternion rot)
    {
        if (rot.W > 1)
        {
            float length = (float)Math.Sqrt(rot.X * rot.X + rot.Y * rot.Y + rot.Z * rot.Z + rot.W * rot.W);
            rot.X /= length; rot.Y /= length; rot.Z /= length; rot.W /= length;
        }
        return (float)(2 * Math.Acos(rot.W));
    }

    // PhysUtil.Rot2Euler (PhysUtil.cs:258-272).
    private static Vector3 Rot2Euler(Quaternion r)
    {
        Vector3 v = new Vector3(0.0f, 0.0f, 1.0f) * r;
        double m = Vector3.Mag(v);
        if (m == 0.0) return new Vector3();
        double x = Math.Atan2(-v.Y, v.Z);
        double sin = v.X / m;
        if (sin < -0.999999 || sin > 0.999999) x = 0.0;
        double y = Math.Asin(sin);
        v = new Vector3(1.0f, 0.0f, 0.0f) * ((r * new Quaternion((float)Math.Sin(-x / 2.0), 0.0f, 0.0f, (float)Math.Cos(-x / 2.0)))
            * new Quaternion(0.0f, (float)Math.Sin(-y / 2.0), 0.0f, (float)Math.Cos(-y / 2.0)));
        double z = Math.Atan2(v.Y, v.X);
        return new Vector3((float)x, (float)y, (float)z);
    }

    // VehicleMotor.MovementExpGrowth (VehicleDynamics.cs:2229-2233).
    private static float MovementExpGrowth(float timeindex, float timescale)
        => Math.Clamp((float)Math.Pow(Math.E, (timeindex / timescale) / Math.E) - 1.0f, 0.0f, 1.0f);

    // VehicleMotor.MovementExpDecay and MovementLimitedGrowth (2201-2222).
    private static float MovementLimitedGrowth(float timeindex, float timescale)
    {
        float factor = timeindex <= 0 ? 1.0f : (float)Math.Pow(Math.E, (double)(-timeindex / timescale));
        if (factor < ThresholdStictionFactor) factor = 0.0f;
        return 1.0f - factor;
    }

    // VehicleMotor.GetGrowthRate (2236-2283).
    private static float GetGrowthRate(float svel, float evel, float timescale)
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

    /// <summary>One step of <paramref name="dt"/> seconds: Halcyon's terms, then the stand-in engine.</summary>
    public void Step(float dt)
    {
        frame++;
        Quaternion rotation = Rotation;
        Vector3 worldAngularVel = WorldAngularVel;
        Vector3 localAngularVel = worldAngularVel * Quaternion.Inverse(rotation);
        Vector3 torque = Vector3.Zero;

        // ---- SimulateVerticalAttractor (1218-1330) ----
        float angle = 0.0f;
        bool inverted = false;
        float timescale = Math.Max(AttractTimescale, dt);
        float efficiency = AttractEfficiency;
        if (timescale < MaxAttractTimescale)
        {
            Vector3 xrot = new Vector3(1, 0, 0) * rotation;
            Vector3 yrot = new Vector3(0, 1, 0) * rotation;
            Vector3 xyrot = Rot2Euler(rotation);
            xyrot.Z = 0;
            angle = Math.Abs(Rot2Angle(Quaternion.CreateFromEulers(xyrot)));

            if (Type == VehicleType.Airplane)
            {
                if (angle > Math.PI / 2)
                {
                    angle = (float)Math.PI - angle;
                    inverted = true;
                }
            }

            float apct = angle / (float)Math.PI;

            if (Math.Abs(LastVerticalAngle - angle) >= ThresholdAttractorAngle)
                lastVerticalFrame = frame;

            if (angle >= ThresholdOverturnAngle || (frame - lastVerticalFrame) < (MaxAttractDormancy / dt))
            {
                Vector3 vtwix = new Vector3(-yrot.Z, xrot.Z, 0);

                // The cheat section.
                vtwix = vtwix * (float)Math.PI * (float)Math.Pow(Math.E, efficiency * 3.0);

                if (Type != VehicleType.Airplane)
                    vtwix *= (1.0f + (float)Math.Pow(1.0 + apct, 4.0));

                if (LimitRollOnly)
                {
                    if (Type == VehicleType.Airplane || Type == VehicleType.Balloon)
                        vtwix.Y = 0.0f;
                    else
                        vtwix.Y *= 0.1f;
                }

                if (Type != VehicleType.Airplane)
                {
                    if (angle >= ThresholdOverturnAngle && angle >= LastVerticalAngle)
                    {
                        VerticalForceAdjust *= 1.3f;
                        vtwix *= VerticalForceAdjust;
                        MaxVerticalForceAdjust = Math.Max(MaxVerticalForceAdjust, VerticalForceAdjust);
                    }
                    else
                    {
                        VerticalForceAdjust /= 1.1f;
                        if (VerticalForceAdjust < 1.0f)
                            VerticalForceAdjust = 1.0f;
                        vtwix *= VerticalForceAdjust;
                    }
                }

                Vector3 remvel = Vector3.Zero;
                remvel.X = localAngularVel.X * MovementExpGrowth(efficiency, 1.0f);
                remvel.Y = localAngularVel.Y * MovementExpGrowth(efficiency, 1.0f);

                // Halcyon scales the torque by the actor's inertia tensor (1311) and PhysX divides it out again, so the angular
                // velocity changes by the local push turned to the world.
                vtwix = vtwix * rotation;

                if (vtwix != Vector3.Zero)
                    torque += vtwix * dt / timescale;

                if (Vector3.Mag(remvel) > 0)
                {
                    remvel = remvel * rotation;
                    torque += -remvel;
                }
                LastVerticalAngle = angle;
            }
        }
        LastAngle = angle;

        // ---- SimulateBankingToYaw (1338-1401) ----
        float btimescale = Math.Max(BankingTimescale, dt);
        if (btimescale < MaxAttractTimescale)
        {
            float beff = BankingEfficiency;
            if (btimescale < MaxTimescale)
            {
                float xspeed = 0.0f;
                if (Math.Abs(LocalSpeedX) > ThresholdAngularMotorDeltaV)
                    xspeed = Math.Clamp(Math.Abs(LocalSpeedX), 0, MaxLegacyLinearVelocity);
                float xspeedpct = xspeed / MaxLegacyLinearVelocity;

                Vector3 erot = new Vector3(0f, 1f, 0f) * rotation;
                float xangle = erot.Z;
                float attitude = (angle > Math.PI / 2.0) ? -1 : 1;

                float xmax = BankingAzimuth;
                xangle = Math.Clamp(xangle * (float)Math.PI * 0.5f / xmax, -1, 1);

                if (inverted)
                    beff = beff * InvertedBankingModifier;

                if (Math.Abs(xangle) > ThresholdBankAngle)
                {
                    BankingDirection = -xangle * attitude * beff * (1.0f - BankingMix) * (float)Math.PI;
                    BankingDirection += -xangle * attitude * beff * BankingMix * xspeedpct * (float)Math.PI;
                }
                else
                {
                    BankingDirection = 0;
                }
            }
        }

        // ---- the banking turn motor, blended with the (released) angular motor's z (2949-3040) ----
        float bnewvel = 0;
        if (BankingDirection != 0 && BankingTimescale < MaxTimescale)
        {
            float blastvel = worldAngularVel.Z;
            float badjvel = blastvel;
            float dirbsign = PosNeg(BankingDirection);

            if (dirbsign * (BankingTargetVelocity - badjvel) > 0) badjvel = BankingTargetVelocity;

            float bfactor = GetGrowthRate(badjvel, BankingDirection, BankingTimescale / dt);
            bnewvel = (badjvel + badjvel * bfactor);

            if (bfactor == ThresholdInverseCrossover) bfactor = -bfactor;

            if (bfactor > 0 && Math.Abs(bnewvel) < ThresholdAngularMotorDeltaV)
                bnewvel = dirbsign * ThresholdAngularMotorDeltaV * 8;

            if (BankingDirection * bnewvel < 0 || bfactor < 0)
                BankingTargetVelocity = bnewvel;
            else
                BankingTargetVelocity = Math.Clamp(bnewvel, -Math.Abs(BankingDirection), Math.Abs(BankingDirection));

            bnewvel = Math.Clamp(bnewvel, -MaxAngularVelocity, MaxAngularVelocity);

            if (bfactor >= 0 && (dirbsign * (blastvel - bnewvel) > 0)) bnewvel = blastvel;

            bnewvel -= blastvel;
        }
        else
        {
            BankingTargetVelocity = 0.0f;
            bnewvel = 0;
        }

        Vector3 bz = Vector3.Zero;
        bz.Z = bnewvel + 0;
        if (Math.Abs(bz.Z) >= ThresholdAngularMotorDeltaV)
            torque += bz;

        // ---- SimulateAngularFriction (1402-1458) ----
        Vector3 frictionTS = AngularFrictionTimescale;
        if (frictionTS.X < MaxTimescale || frictionTS.Y < MaxTimescale || frictionTS.Z < MaxTimescale)
        {
            frictionTS.X = MovementLimitedGrowth(dt, Math.Max(frictionTS.X, dt));
            frictionTS.Y = MovementLimitedGrowth(dt, Math.Max(frictionTS.Y, dt));
            frictionTS.Z = MovementLimitedGrowth(dt, Math.Max(frictionTS.Z, dt));
            Vector3 newvel = new Vector3(-localAngularVel.X * frictionTS.X, -localAngularVel.Y * frictionTS.Y, -localAngularVel.Z * frictionTS.Z);
            newvel.X = Math.Clamp(newvel.X, -MaxAngularVelocity, MaxAngularVelocity);
            newvel.Y = Math.Clamp(newvel.Y, -MaxAngularVelocity, MaxAngularVelocity);
            newvel.Z = Math.Clamp(newvel.Z, -MaxAngularVelocity, MaxAngularVelocity);
            if (Math.Abs(newvel.X) < MinPhysicsForce) newvel.X = PosNeg(newvel.X) * MinPhysicsForce;
            if (Math.Abs(newvel.Y) < MinPhysicsForce) newvel.Y = PosNeg(newvel.Y) * MinPhysicsForce;
            if (Math.Abs(newvel.Z) < MinPhysicsForce) newvel.Z = PosNeg(newvel.Z) * MinPhysicsForce;
            Vector3 worldvel = newvel * rotation;
            if (Math.Abs(localAngularVel.X) >= MinPhysicsForce * 2 || Math.Abs(localAngularVel.Y) >= MinPhysicsForce * 2 || Math.Abs(localAngularVel.Z) >= MinPhysicsForce * 2)
            {
                if (Vector3.Mag(worldAngularVel) < ThresholdAngularFrictionDeltaV)
                    worldvel = -worldAngularVel;
                if (Vector3.Mag(worldvel) > 0)
                    torque += worldvel;
            }
        }

        // TorqueFini (2444-2469): too small a torque is taken as zero.
        if (Math.Abs(torque.X) < MinPhysicsForce) torque.X = 0.0f;
        if (Math.Abs(torque.Y) < MinPhysicsForce) torque.Y = 0.0f;
        if (Math.Abs(torque.Z) < MinPhysicsForce) torque.Z = 0.0f;

        // ---- the stand-in engine ----
        WorldAngularVel += torque;
        float speed = WorldAngularVel.Length();
        if (speed > ActorMaxAngularSpeed)
            WorldAngularVel *= ActorMaxAngularSpeed / speed;
        WorldAngularVel *= Math.Max(0f, 1f - ActorAngularDamping * dt);
        if (!HoldOrientation && WorldAngularVel.Length() > 0f)
        {
            Vector3 w = WorldAngularVel;
            float s = w.Length();
            var turn = System.Numerics.Quaternion.CreateFromAxisAngle(new System.Numerics.Vector3(w.X, w.Y, w.Z) / s, s * dt);
            var now = new System.Numerics.Quaternion(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);
            var next = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Concatenate(now, turn));
            Rotation = new Quaternion(next.X, next.Y, next.Z, next.W);
        }
        Time += dt;
    }
}

/// <summary>
/// Halcyon's hover for a body held at a height above the ground (PhysxPrim.pid.cs DoHoverTarget, DoHover, DoHoverDamping): gravity
/// switched off while it acts, a strong correction of a movement away from the target, a push of (height gap / timescale) as a
/// change of velocity per step, and damping. The engine's part: velocity carries the body. The vehicle's hover goes through this
/// code (VehicleDynamics.cs:1552-1604 sets the actor's hover with the Vehicle flag among others, which DoHoverTarget's test for
/// "only the Vehicle flag" does not catch).
/// </summary>
internal sealed class HalcyonHoverModel
{
    private const float MinTau = 0.015f * 2.0f;                      // PhysxPrim.pid.cs:47
    private const float MinPhysxForce = 0.001f;                      // :51
    private const float AtHoverHeightTolerance = 0.05f;              // :77
    private const float MinHoverDampingVelBeforeZeroing = 0.01f;     // :83
    private const float HoverDampingMultiplier = 2.0f;               // :88

    public float Height, Tau, Damping;
    public float Z, Vz;                                              // above flat ground at 0
    public float Gravity = -9.8f;

    public void Step(float dt)
    {
        float dv = 0;
        bool gravityOff = false;
        if (Tau >= MinTau)
        {
            float targetDiff = Math.Abs(Z - Height);
            gravityOff = true;
            if (targetDiff < AtHoverHeightTolerance * Damping)
            {
                dv += DampingChange(dt);
            }
            else
            {
                float diff = Height - Z;
                float zforce = Vz;
                if (Math.Abs(zforce) < MinPhysxForce) zforce = Math.Sign(zforce) * MinPhysxForce;
                if (Math.Sign(Vz) != Math.Sign(diff))
                {
                    if (Damping > 0.75f) dv += -zforce;
                    else dv += -zforce * dt;
                }
                // The test of the HOVER_UP_ONLY flag (1 / buoyancy) is left out: this hover has no such flag.
                zforce = diff * (1.0f / Tau) * dt;
                if (Math.Abs(zforce) < MinPhysxForce) zforce = Math.Sign(zforce) * MinPhysxForce;
                dv += zforce;
                dv += DampingChange(dt);
            }
        }
        Vz += dv;
        if (!gravityOff) Vz += Gravity * dt;
        Z += Vz * dt;
    }

    private float DampingChange(float dt)
    {
        if (Vz == 0) return 0;
        if (Math.Abs(Vz) <= MinHoverDampingVelBeforeZeroing * Damping)
            return -Vz * Damping;
        return -Vz * dt * HoverDampingMultiplier * Damping;
    }
}

/// <summary>Halcyon's shift of a move target when an object is suspended for a region crossing (PhysxPrim.cs:3140-3185).</summary>
internal static class HalcyonCrossing
{
    public const float RegionSize = 256f;

    public static Vector3 ShiftTarget(Vector3 target)
    {
        if (target.X >= RegionSize) target.X -= RegionSize;
        else if (target.X < 0) target.X += RegionSize;

        if (target.Y >= RegionSize) target.Y -= RegionSize;
        else if (target.Y < 0) target.Y += RegionSize;
        return target;
    }
}

/// <summary>Halcyon's two size checks on a physical object (PhysxPrim.stability.cs:329-410), on the size of the actor's bounds.</summary>
internal static class HalcyonSizeChecks
{
    private const float MinSizeForDynamic = 0.009f, MaxPhysicalDimension = 128f;

    /// <summary>True when Halcyon sets the actor kinematic (EnforceMinimumDynamicDimensions or EnforceMaximumDynamicDimensions).</summary>
    public static bool MakesKinematic(float xsz, float ysz, float zsz)
    {
        int dimsTooSmall = 0;
        if (xsz < MinSizeForDynamic) dimsTooSmall++;
        if (ysz < MinSizeForDynamic) dimsTooSmall++;
        if (zsz < MinSizeForDynamic) dimsTooSmall++;
        if (dimsTooSmall >= 2) return true;

        if (xsz > MaxPhysicalDimension * 2.0f || ysz > MaxPhysicalDimension * 2.0f || zsz > MaxPhysicalDimension * 2.0f) return true;
        int dimsTooLarge = 0;
        if (xsz > MaxPhysicalDimension) dimsTooLarge++;
        if (ysz > MaxPhysicalDimension) dimsTooLarge++;
        if (zsz > MaxPhysicalDimension) dimsTooLarge++;
        return dimsTooLarge >= 2;
    }
}
