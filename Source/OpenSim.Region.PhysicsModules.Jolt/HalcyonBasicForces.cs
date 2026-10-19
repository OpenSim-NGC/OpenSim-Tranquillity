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
 * The move-to-target, hover, ground repel and rotation look-at laws, and the buoyancy hack, ported from InWorldz Halcyon
 * (InWorldz.PhysxPhysics/PhysxPrim.pid.cs: DoPIDMoveTarget, DoHoverTarget, DoHoverDamping, DoHover, DoGroundRepel,
 * GetHeightAbove, DoRotLookTarget, ShortestAngle, SetRotLookAtTarget; InWorldz.PhysxPhysics/PhysxPrim.cs: SetBuoyancy).
 * Adapted 2026 by Legion Builds
 */

using System;
using System.Numerics;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    /// <summary>
    /// What Halcyon does for an object that has a move target, a hover height (or a ground repel) or a rotation target,
    /// under <c>[Jolt] BasicForcesBehaviour = InWorldz</c>. Every function here is pure: the position, velocity and
    /// rotation the object has at the start of a collision step go in, the velocity it should have for that step comes out.
    ///
    /// <para>
    /// Halcyon runs each of these once per PhysX step (PhysxPrim.UpdateDynamicForPIDTargets), as an impulse or a velocity
    /// change, in a step that is 15 ms long when nothing is late (PhysxScene.TIMESTEP, <see cref="StepSeconds"/>) and 30 ms
    /// when it is. Some of its figures are per step and so change with the step's length. Here each law is Halcyon's, and
    /// the figure that depends on the step is made the same at every step length: at a step of <see cref="StepSeconds"/>
    /// the result is exactly Halcyon's, and at any other step the object follows the same path.
    /// </para>
    /// </summary>
    internal static class HalcyonBasicForces
    {
        /// <summary>Halcyon's physics step (PhysxScene.TIMESTEP_IN_SECONDS, 15 ms).</summary>
        public const float StepSeconds = 0.015f;

        /// <summary>A mouse drag takes this share of the object's angular velocity away in each of Halcyon's steps
        /// (PhysxPrim.pid.cs DoGrabTarget: <c>AddTorque(-angularVelocity * 0.9f, VelocityChange)</c>).</summary>
        public const float GrabAngularDamping = 0.9f;

        /// <summary>The share of its angular velocity a dragged object keeps over a step of <paramref name="h"/> seconds: what
        /// Halcyon's 0.1 a 15 ms step leaves, made the same at every step length.</summary>
        public static float GrabAngularKept(float h) => MathF.Pow(1f - GrabAngularDamping, h / StepSeconds);

        /// <summary>A move-to-target or hover tau at or under this does nothing (PhysxPrim.MIN_TAU: two steps).</summary>
        public const float MinTau = StepSeconds * 2f;

        /// <summary>The object counts as at its move target inside this radius (m) (AT_TARGET_TOLERANCE).</summary>
        public const float AtTargetTolerance = 0.05f;

        /// <summary>The object counts as at its hover height inside this band (m), times the damping (AT_HOVER_HEIGHT_TOLERANCE).</summary>
        public const float AtHoverHeightTolerance = 0.05f;

        /// <summary>Hover damping stops a vertical speed under this (m/s) times the damping outright (MIN_HOVER_DAMPING_VEL_BEFORE_ZEROING).</summary>
        public const float MinHoverDampingVelocityBeforeZeroing = 0.01f;

        /// <summary>The hover damping's rate per second at a damping of 1 (HOVER_DAMPING_MULTIPLIER, "determined through experimentation on the SL grid").</summary>
        public const float HoverDampingMultiplier = 2f;

        /// <summary>The damping a script's hover has: SceneObjectGroup.SetHoverHeight hands the actor 1.0.</summary>
        public const float ScriptHoverDamping = 1f;

        /// <summary>The look-at's scale on its stiffness and damping (ROT_LOOKAT_SCALING_CONSTANT).</summary>
        public const float RotLookAtScale = 100f;

        /// <summary>A rotation within this angle (rad) of its look-at target is there (AT_ROT_TOLERANCE, one degree).</summary>
        public const float AtRotTolerance = MathF.PI / 180f;

        /// <summary>SetRotLookAtTarget holds the strength to 0.1..1 and the damping to 0.1..100.</summary>
        public const float MinRotLookStrength = 0.1f, MaxRotLookStrength = 1f, MinRotLookDamping = 0.1f, MaxRotLookDamping = 100f;

        // ---- Buoyancy ----

        /// <summary>
        /// PhysxPrim.SetBuoyancy: "Legacy grid hack." A buoyancy from 0.977 up to (not including) 1 becomes 1, so that
        /// content which set 0.98 to float an object does float it.
        /// </summary>
        public static float LegacyBuoyancy(float buoyancy)
            => buoyancy >= 0.977f && buoyancy < 1f ? 1f : buoyancy;

        // ---- Move to target ----

        /// <summary>
        /// DoPIDMoveTarget: the object's velocity becomes <c>distance / tau</c> in every step, so the distance left is
        /// multiplied by <c>1 - h0 / tau</c> in each step of Halcyon's length h0: an exponential approach that never passes
        /// the target. Over a step of <paramref name="h"/> that is the same multiplier to the power h / h0, so the path
        /// does not depend on the step; the velocity that carries the object that far in the step is returned. For tau over
        /// <see cref="MinTau"/> the multiplier is between 0.5 and 1.
        /// </summary>
        public static Vector3 MoveToVelocity(Vector3 distance, float tau, float h)
        {
            double keep = Math.Pow(1.0 - StepSeconds / (double)tau, h / (double)StepSeconds);
            return distance * (float)((1.0 - keep) / h);
        }

        // ---- The second-order step (hover and look-at) ----

        /// <summary>
        /// Hover and rotation look-at are the same kind of law. Halcyon adds, in each step of length h0, a change of
        /// velocity of <c>-kappa h0 x - c h0 v</c> to a velocity v whose error from the target is x (the engine then moves
        /// the object by the new velocity): a spring of stiffness kappa with a drag c. As a map of (x, v) over one step
        /// that is v' = (1 - c h0) v - kappa h0 x, x' = x + h0 v'. This returns the same map for a step of any length h, in
        /// the same form (v' = alpha x + beta v, then x' = x + h v', the form the engine integrates), with the two
        /// eigenvalues of Halcyon's map taken to the power h / h0. At h = h0 it is Halcyon's own step; at any other h the
        /// object follows the same path, and it is stable at every h that Halcyon's own is. Where Halcyon's step is not stable in
        /// the sense that its drag overshoots (c h0 at or over 1, which the look-at's strength allows above 0.667), there is
        /// no step-independent form of it; the exact solution of the spring and drag as a differential equation is used
        /// (the law Halcyon meant), which is stable at every step.
        /// </summary>
        public static (float alpha, float beta) SecondOrder(float kappa, float c, float h)
        {
            double h0 = StepSeconds, k = kappa, cc = c;
            double det0 = 1.0 - cc * h0;
            double trace, beta;
            if (det0 > 0.0)
            {
                double r = h / h0;
                double tr0 = 2.0 - h0 * h0 * k - cc * h0;
                double disc = tr0 * tr0 - 4.0 * det0;
                if (disc >= 0.0 && tr0 > 0.0)
                {
                    double s = Math.Sqrt(disc);
                    trace = Math.Pow((tr0 + s) / 2.0, r) + Math.Pow((tr0 - s) / 2.0, r);
                }
                else
                {
                    double angle = Math.Atan2(Math.Sqrt(Math.Max(-disc, 0.0)) / 2.0, tr0 / 2.0);
                    trace = 2.0 * Math.Pow(det0, r / 2.0) * Math.Cos(r * angle);
                }
                beta = Math.Pow(det0, r);
            }
            else
            {
                double disc = cc * cc - 4.0 * k;
                if (disc >= 0.0)
                {
                    double s = Math.Sqrt(disc);
                    trace = Math.Exp((-cc + s) / 2.0 * h) + Math.Exp((-cc - s) / 2.0 * h);
                }
                else
                {
                    trace = 2.0 * Math.Exp(-cc * h / 2.0) * Math.Cos(Math.Sqrt(-disc) / 2.0 * h);
                }
                beta = Math.Exp(-cc * h);
            }
            return ((float)((trace - 1.0 - beta) / h), (float)beta);
        }

        // ---- Hover and ground repel ----

        /// <summary>What a hover step decided for the object.</summary>
        internal readonly record struct HoverResult(float VerticalVelocity, bool GravityOff, bool Acts);

        /// <summary>
        /// GetHeightAbove: the height the hover measures. Ground and water: above the higher of the two; water: above the
        /// water; ground: above the terrain; global: the z, except that it is a negative number under the terrain.
        /// </summary>
        public static float HeightAbove(PIDHoverType type, float z, float ground, float water)
        {
            switch (type)
            {
                case PIDHoverType.GroundAndWater: return z - MathF.Max(ground, water);
                case PIDHoverType.Water: return z - water;
                case PIDHoverType.Ground: return z - ground;
                default: return z < ground ? z - ground : z;
            }
        }

        /// <summary>
        /// DoHoverTarget with DoHover, DoGroundRepel and DoHoverDamping. <paramref name="heightAbove"/> is
        /// <see cref="HeightAbove"/>, <paramref name="vz"/> the object's vertical velocity, <paramref name="h"/> the step.
        /// Halcyon works all the changes out from the velocity the object had at the start of the step and adds them, so
        /// they are added here the same way. Not ported: the 0.001 floor Halcyon gives each force ("Stay above PhysX NOP
        /// limits"); Jolt has no such limit.
        /// </summary>
        public static HoverResult Hover(float heightAbove, float height, float tau, bool repel, float vz, float h)
        {
            const float damping = ScriptHoverDamping;
            (float alpha, float beta) = SecondOrder(1f / tau, HoverDampingMultiplier * damping, h);

            // Around the target height nothing pushes: gravity is off and only the vertical drag acts.
            if (MathF.Abs(heightAbove - height) < AtHoverHeightTolerance * damping)
                return new HoverResult(vz + HoverDamping(vz, damping, beta), true, true);

            float diff = height - heightAbove;
            if (repel)
            {
                // Too high for a repel: nothing, and gravity is back on.
                if (heightAbove > height + AtHoverHeightTolerance)
                    return new HoverResult(vz, false, false);

                // DoGroundRepel: only the upward push; a downward velocity is cancelled; the drag is damping 1.
                float dv = 0f;
                if (vz < 0f)
                    dv -= vz;
                dv += ForceChange(alpha, diff, tau, vz, damping: 1f);
                dv += HoverDamping(vz, 1f, beta);
                return new HoverResult(vz + dv, true, true);
            }
            else
            {
                // DoHover. A velocity away from the target is cancelled outright (damping over 0.75). The guard for UP_ONLY
                // (only vehicles set it) is open for a script's hover.
                float dv = 0f;
                if (MathF.Sign(vz) != MathF.Sign(diff) && damping > 0.75f)
                    dv -= vz;
                dv += ForceChange(alpha, diff, tau, vz, damping);
                dv += HoverDamping(vz, damping, beta);
                return new HoverResult(vz + dv, true, true);
            }
        }

        // The change of velocity the hover force gives in the step: diff / tau times the step (alpha = -h / tau at a step of h0).
        // While the vertical speed is under 0.01 x damping the damping takes the whole speed away in every step, so the velocity
        // the object keeps is this one step's change alone. Halcyon's change is a velocity per step, which at a shorter step is
        // too little to be felt and the hover never gets going (at 4 ms it does not move); it is taken at Halcyon's step
        // there, and the object then creeps at the speed it creeps at in Halcyon whatever the step.
        private static float ForceChange(float alpha, float diff, float tau, float vz, float damping)
            => MathF.Abs(vz) <= MinHoverDampingVelocityBeforeZeroing * damping
                ? diff * StepSeconds / tau
                : -alpha * diff;

        // DoHoverDamping: a vertical speed under 0.01 x damping is taken away (times the damping), a larger one loses 2 x damping
        // a second; <paramref name="beta"/> is the share of the speed kept over the step.
        private static float HoverDamping(float vz, float damping, float beta)
        {
            if (vz == 0f)
                return 0f;
            if (MathF.Abs(vz) <= MinHoverDampingVelocityBeforeZeroing * damping)
                return -vz * damping;
            return (beta - 1f) * vz;
        }

        // ---- Rotation look-at ----

        /// <summary>SetRotLookAtTarget's clamps on the strength and damping a script gives.</summary>
        public static void ClampRotLookAt(ref float strength, ref float damping)
        {
            strength = Math.Clamp(strength, MinRotLookStrength, MaxRotLookStrength);
            damping = Math.Clamp(damping, MinRotLookDamping, MaxRotLookDamping);
        }

        /// <summary>
        /// DoRotLookTarget: the angular velocity for the step. Halcyon pushes the object with a torque that gives an
        /// angular acceleration of <c>100 angle / damping</c> toward the target and takes <c>100 strength</c> times the angular
        /// velocity away, per second; the same spring and drag as the hover's, on the rotation vector from the
        /// object's rotation to the target (<see cref="SecondOrder"/>). Within <see cref="AtRotTolerance"/> of the target
        /// the angular velocity is set to zero.
        /// </summary>
        /// <param name="turn">The rotation vector (axis x angle, the short way round) from the object's rotation to the target.</param>
        public static Vector3 RotLook(Vector3 turn, Vector3 angularVelocity, float strength, float damping, float h)
        {
            if (turn.Length() <= AtRotTolerance)
                return Vector3.Zero;
            ClampRotLookAt(ref strength, ref damping);
            (float alpha, float beta) = SecondOrder(RotLookAtScale / damping, RotLookAtScale * strength, h);
            return angularVelocity * beta - turn * alpha;
        }
    }
}
