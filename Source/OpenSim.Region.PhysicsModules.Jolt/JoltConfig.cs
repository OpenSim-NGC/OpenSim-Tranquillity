/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The [Jolt] config section.
//
// Every knob the module used to hardcode, read once in Initialise. The rule: every default reproduces the
// module's built-in behaviour exactly (PhysicsBackendSettings.Default + CollisionSteps 6 + VelocityIterations 20 + the
// MaxBodies area rule + today's step-buffer caps). Scaling the pair/contact caps with region area is opt-in (ScaleCapsWithArea). The
// avatar speed factors are the exception: their defaults give Second Life's documented speeds (see below).
// Invalid values (unparsable, non-finite, out of range) warn and fall back to the default - a typo in the INI
// must never take a region down or silently change physics.

using System;
using System.Collections.Generic;
using System.Globalization;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Nini.Config;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class JoltConfig
    {
        internal const string Section = "Jolt";

        /// <summary>Whose basic-forces behaviour the engine follows where InWorldz and Second Life differ: for now the physics material presets.</summary>
        internal enum BasicForces { InWorldz = 0, SecondLife }

        // 65536 bodies per standard 256 x 256 m region, scaled by AREA for varregions.
        internal const int BaseMaxBodies = 65536;
        internal const long BaseArea = 256L * 256L;

        public float Gravity = -9.80665f;          // world Z gravity, m/s^2
        public int CollisionSteps = 6;             // solver sub-steps inside Update
        public int PositionIterations = 2;
        public int VelocityIterations = 20;        // Jolt's own is 10, at which ten stacked boxes do not fall asleep at 45 Hz
        public int ThreadCount = 0;                // all pools together; 0 = automatic: each pool's share of ProcessorCount - 1, at most 4 (first region wins)
        public bool DeterministicMode = false;
        public int MaxBodies = 0;                  // 0 = BaseMaxBodies scaled by area
        public int MaxBodyPairs = 65536;
        public int MaxContactConstraints = 16384;
        public bool ScaleCapsWithArea = false;     // true: scale MaxBodyPairs / MaxContactConstraints by area too
        public int BodyUpdateBufferMax = 65536;
        public int CharacterUpdateBufferMax = 1024;
        public int ContactBufferMax = 0;           // 0 = the backend's contact ring capacity
        public float AvatarJumpSpeed = 4.0f;       // CharacterDesc.JumpSpeed, m/s
        public float AvatarWalkSpeedFactor = DefaultAvatarWalkSpeedFactor;   // times the speed ScenePresence asks for
        public float AvatarRunSpeedFactor = DefaultAvatarRunSpeedFactor;     // the same, with always run on
        public float AvatarFlySpeedFactor = DefaultAvatarFlySpeedFactor;     // the same, flying
        public float AvatarFlyDownSpeedFactor = DefaultAvatarFlyDownSpeedFactor;   // the same, the downward part of a flight
        public float CapacityLogIntervalSeconds = 10f;
        public int JobPools = 1;                   // job pools, one physics update at a time each; splits ThreadCount
        public bool JobPoolFairHandoff = false;    // true: a job pool is granted first come, first served (per physics step)
        public float PhysicsStepRate = DefaultPhysicsStepRate; // Hz; 0 = one physics step per heartbeat
        public bool PhysicsStepRateSet = false;    // PhysicsStepRate came from a valid value in the configuration
        public int PhysicsStepCollisionSteps = 2;  // solver sub-steps per physics step, used only when PhysicsStepRate is on; at least 60 Hz (CollisionStepsAt)
        public float VehicleGroundGravityFactor = 1f;   // gravity on a car or sled touching something; 1 = whole
        public VehiclePresetSet VehiclePresets = VehiclePresetSet.Documented;   // the values llSetVehicleType gives each type
        public bool VehiclePresetsSet = false;     // VehiclePresets came from a valid value in the configuration
        public VehicleBehaviour VehicleBehaviour = VehicleBehaviour.InWorldz;   // whose behaviour the vehicle code follows where they differ

        // The limits a script's vehicle parameters are held to (VehicleSettings says what each one does).
        public float VehicleMaxLinearSpeed = VehicleLimits.MaxLinearVelocity;       // m/s
        public float VehicleReferenceSpeed = VehicleLimits.MaxLegacyLinearVelocity; // m/s
        public float VehicleMaxAngularSpeed = VehicleLimits.MaxAngularVelocity;     // rad/s
        public float VehicleAngularLimiterFloor = VehicleLimits.AngularLimiterFloor;// share; 1 = no limiter
        public float VehicleMinTimescale = VehicleLimits.MinPhysicsTimestep;        // s
        public float VehicleMaxTimescale = VehicleLimits.MaxTimescale;              // s
        public float VehicleMaxDecayTimescale = VehicleLimits.MaxDecayTimescale;    // s
        public bool VehicleMaxDecayTimescaleSet;    // the key is set (else the behaviour chooses: 120 s, or 1000 s under InWorldz)
        public float VehicleMaxHoverTimescale = VehicleLimits.MaxHoverTimescale;    // s
        public float VehicleMaxAttractTimescale = VehicleLimits.MaxAttractTimescale;// s
        public float VehicleMaxMotorOffset = VehicleLimits.MaxLinearOffset;         // m
        public float VehicleMinHoverHeight = VehicleLimits.MinRegionHeight;         // m
        public float VehicleMaxHoverHeight = VehicleLimits.MaxRegionHeight;         // m
        public float VehicleSledAssist = VehicleSettings.DefaultSledAssist;         // share of gravity
        // Halcyon's cut of velocity spikes in a vehicle's step (VehicleController, "Spike mitigation"). null: on under
        // VehicleBehaviour = InWorldz, off under SecondLife; true or false forces it.
        public bool? VehicleSpikeMitigation = null;
        public bool VehicleMotorTargetOnUndrivenAxes = false;                       // InWorldz: the motor's target also replaces a lower velocity on an axis it has no direction on
        public float? VehicleContactFriction;                                       // contact friction of every part of a vehicle body; null = each part's material
        public float SledContactFriction = 0f;                                      // contact friction of every part of a sled
        public BasicForces BasicForcesBehaviour = BasicForces.InWorldz;             // which preset table llSetPhysicsMaterial's materials use
        public MaterialCombine FrictionCombine = MaterialCombine.Average;           // how two surfaces' friction combines
        public MaterialCombine RestitutionCombine = MaterialCombine.Average;        // how two surfaces' restitution combines
        public float TerrainFriction = PhysicsBackendSettings.DefaultTerrainFriction;
        public float TerrainRestitution = PhysicsBackendSettings.DefaultTerrainRestitution;
        public float VehicleRestSpeed = VehicleSettings.DefaultRestSpeed;           // m/s; 0 = no rest rule

        // The engine's own speed caps on every moving body (Jolt's BodyCreationSettings defaults).
        public float BodyMaxLinearSpeed = PhysicsBackendSettings.JoltMaxLinearSpeed;    // m/s
        public float BodyMaxAngularSpeed = PhysicsBackendSettings.JoltMaxAngularSpeed;  // rad/s

        // The linear and angular damping of a physical prim that is not a vehicle, per second (Jolt's own default).
        public float PrimLinearDamping = DefaultPrimLinearDamping;
        public float PrimAngularDamping = DefaultPrimAngularDamping;

        public bool AllowUnrecordedNative = false; // true: load a joltc whose hash the module has no record of

        // true: a non-physical mesh whose physics shape type is CONVEX is one convex hull of its points, not its triangle
        // mesh. Core gives every mesh CONVEX unless the upload says otherwise, so this turns hollow mesh into solid.
        public bool HonourConvexMesh = false;

        // The most collision shapes one physical object (the whole linkset) may have: 1 for each prim that is a plain box, sphere
        // or cylinder, and for each of the others the number of convex hulls it is made of (a prim of shape type NONE has none).
        // An object over it is refused physical status and the owner is told. 0 = no limit.
        public int MaxPhysicalComplexity = 0;

        // Jolt's enhanced internal edge removal, for bodies that move under physics and for avatars: the contacts with the
        // shared edges inside a triangle mesh or a heightfield that can push a body off its path. Costs more per contact.
        public bool BodyEnhancedEdgeRemoval = false;
        public bool AvatarEnhancedEdgeRemoval = false;

        // A non-physical mesh prim's triangles are met from their front face only by a moving body, as Jolt does for a
        // simulated contact: a box or a ball that reaches a one-sided wall or floor from behind passes through it.
        // true cooks every triangle mesh with a reversed copy of each triangle, so the mesh is met from both sides.
        // Doubles the triangles of each mesh shape (memory and cook time). Avatars already meet both sides.
        public bool TwoSidedMeshCollision = false;

        // Jolt's speculative contact distance in metres (PhysicsSettings mSpeculativeContactDistance; the engine's own value is
        // 0.02, which is the default). A box sliding across two abutted prims can be stopped and tipped at the seam by the next
        // prim's end face seen from within this distance; 0.0025 and less remove that, 0.005 and more do not, and every value
        // under 0.02 also changes how other contacts settle. See "Seams between prims" in Docs/JoltPhysics.md.
        public float SpeculativeContactDistance = 0.02f;

        // Jolt's point velocity sleep threshold in metres per second (PhysicsSettings mPointVelocitySleepThreshold; the engine's
        // own value is 0.03, which is the default). A body falls asleep once every point of it, and of everything resting on it,
        // has moved slower than this for the engine's time before sleep. A tall stack sways a little above 0.03 at one physics
        // step per heartbeat, and whether it falls asleep then depends on float rounding, which differs between CPUs.
        public float SleepVelocityThreshold = 0.03f;

        // true: avatars and physical objects standing on a prim that keyframed motion (llSetKeyframedMotion) is moving are
        // carried with it, as Second Life does. false: the prim is a fixed body that is set to each new place, and what
        // stands on it is left behind.
        public bool KeyframedMotionCarriesRiders = true;

        // true: a non-physical prim that a series of position or rotation sets moves with no keyframed-motion hint
        // (llSetPos, llSetRegionPos, llSetPrimitiveParams, the build tool) carries what stands on it while the series lasts.
        // false (the default): such a prim is a fixed body set to each new place and what stands on it is left behind, as in
        // Second Life. A prim that keyframed motion moves is not affected: KeyframedMotionCarriesRiders decides.
        public bool MovedPrimsCarryRiders = false;

        // Script ray casts (llCastRay) and pushes on avatars (llPushObject): limits on what scripts can make one region
        // do. They guard against abuse, so each defaults to its safe value.
        public float RayCastBudgetMs = PhysicsBackendSettings.DefaultRayCastBudgetMs;         // script casts, per region per heartbeat
        public float RayCastSimulatorBudgetMs = PhysicsBackendSettings.DefaultRayCastSimulatorBudgetMs; // every other cast, the same
        public int RayCastMaxTestedHits = PhysicsBackendSettings.DefaultRayCastMaxTestedHits; // per cast
        public int RayCastMaxHits = MaxRayCastHits;                                          // hits one cast returns
        public float AvatarPushMaxSpeed = PhysicsBackendSettings.DefaultAvatarPushMaxSpeed;   // m/s; 0 = pushes do not move avatars
        public float AvatarPushRecovery = PhysicsBackendSettings.DefaultAvatarPushRecovery;   // m/s per second
        public float AvatarObjectPushPerMomentum = PhysicsBackendSettings.DefaultAvatarObjectPushPerMomentum;   // N per kg m/s of an avatar walking into a loose object
        public float AvatarObjectPushMaxForce = PhysicsBackendSettings.DefaultAvatarObjectPushMaxForce;         // N

        // What ScenePresence asks an avatar's physics for: AgentControlNormalVel (4.096 m/s) for a held movement key,
        // walking or running alike (the viewer sends running as the separate SetAlwaysRun flag), and four times that
        // while flying (ScenePresence.AddNewMovement). Its SpeedModifier (osSetSpeed) scales both.
        internal const float CoreWalkRequest = 4.096f;
        internal const float CoreFlyRequest = CoreWalkRequest * 4f;

        // Second Life's documented speeds on level ground: walk 3.20, run 5.13, fly 16.00 m/s, and flying up 16.00 and
        // down 22.87 m/s (https://wiki.secondlife.com/wiki/Default_Avatar_Movement_Speeds). Each default factor turns ScenePresence's
        // request into that speed; ubODE (av_movement_divisor_walk / _run) and BulletSim (AvatarWalkVelocityFactor,
        // AvatarAlwaysRunFactor) scale the same request with their own factors.
        internal const float DefaultAvatarWalkSpeedFactor = 3.20f / CoreWalkRequest;   // 0.78125
        internal const float DefaultAvatarRunSpeedFactor = 5.13f / CoreWalkRequest;    // 1.2524414
        internal const float DefaultAvatarFlySpeedFactor = 16.00f / CoreFlyRequest;    // 0.9765625
        internal const float DefaultAvatarFlyDownSpeedFactor = 22.87f / CoreFlyRequest; // 1.3958740
        internal const float MinAvatarSpeedFactor = 0.1f;
        internal const float MaxAvatarSpeedFactor = 10f;

        // Second Life's documented maximum for llCastRay's RC_MAX_HITS, 256 (wiki.secondlife.com/wiki/LlCastRay).
        internal const int MaxRayCastHits = 256;

        // The highest PhysicsStepRate accepted. Each step costs a backend update, so a rate far above the heartbeat
        // mostly hits the per-heartbeat step cap (see SubstepAccumulator.MaxStepsPerFrame).
        internal const float MaxPhysicsStepRate = 1000f;

        // The default PhysicsStepRate: fixed 45 Hz physics steps, whatever the heartbeat.
        internal const float DefaultPhysicsStepRate = 45f;
        internal const float DefaultPrimLinearDamping = 0.02f;   // Halcyon's, for every physical object, vehicles included (PhysxActorFactory.cs)
        internal const float DefaultPrimAngularDamping = 0.05f;

        /// <summary>Parse [Jolt]. Missing keys keep their defaults; each invalid one adds a line to <paramref name="warnings"/>.</summary>
        internal static JoltConfig FromConfig(IConfigSource source, List<string> warnings)
        {
            var c = new JoltConfig();
            IConfig cfg = source?.Configs[Section];
            if (cfg == null)
                return c;

            c.Gravity = F(cfg, "Gravity", c.Gravity, -1000f, 1000f, warnings);
            c.CollisionSteps = I(cfg, "CollisionSteps", c.CollisionSteps, 1, 64, warnings);
            c.PositionIterations = I(cfg, "PositionIterations", c.PositionIterations, 1, 255, warnings);
            c.VelocityIterations = I(cfg, "VelocityIterations", c.VelocityIterations, 1, 255, warnings);
            c.ThreadCount = I(cfg, "ThreadCount", c.ThreadCount, 0, 256, warnings);
            c.DeterministicMode = B(cfg, "DeterministicMode", c.DeterministicMode, warnings);
            c.MaxBodies = I(cfg, "MaxBodies", c.MaxBodies, 0, 8_388_607, warnings);   // Jolt's BodyID index limit
            c.MaxBodyPairs = I(cfg, "MaxBodyPairs", c.MaxBodyPairs, 1, 8_388_607, warnings);
            c.MaxContactConstraints = I(cfg, "MaxContactConstraints", c.MaxContactConstraints, 1, 8_388_607, warnings);
            c.ScaleCapsWithArea = B(cfg, "ScaleCapsWithArea", c.ScaleCapsWithArea, warnings);
            c.AllowUnrecordedNative = B(cfg, "AllowUnrecordedNative", c.AllowUnrecordedNative, warnings);
            c.HonourConvexMesh = B(cfg, "HonourConvexMesh", c.HonourConvexMesh, warnings);
            c.BodyEnhancedEdgeRemoval = B(cfg, "BodyEnhancedEdgeRemoval", c.BodyEnhancedEdgeRemoval, warnings);
            c.AvatarEnhancedEdgeRemoval = B(cfg, "AvatarEnhancedEdgeRemoval", c.AvatarEnhancedEdgeRemoval, warnings);
            c.TwoSidedMeshCollision = B(cfg, "TwoSidedMeshCollision", c.TwoSidedMeshCollision, warnings);
            c.SpeculativeContactDistance = F(cfg, "SpeculativeContactDistance", c.SpeculativeContactDistance, 0f, 0.5f, warnings);
            c.SleepVelocityThreshold = F(cfg, "SleepVelocityThreshold", c.SleepVelocityThreshold, 0.001f, 1f, warnings);
            c.KeyframedMotionCarriesRiders = B(cfg, "KeyframedMotionCarriesRiders", c.KeyframedMotionCarriesRiders, warnings);
            c.MovedPrimsCarryRiders = B(cfg, "MovedPrimsCarryRiders", c.MovedPrimsCarryRiders, warnings);
            c.BodyUpdateBufferMax = I(cfg, "BodyUpdateBufferMax", c.BodyUpdateBufferMax, 1, 8_388_607, warnings);
            c.CharacterUpdateBufferMax = I(cfg, "CharacterUpdateBufferMax", c.CharacterUpdateBufferMax, 1, 65536, warnings);
            c.ContactBufferMax = I(cfg, "ContactBufferMax", c.ContactBufferMax, 0, 16_777_216, warnings);
            c.AvatarJumpSpeed = F(cfg, "AvatarJumpSpeed", c.AvatarJumpSpeed, 0f, 100f, warnings);
            c.AvatarWalkSpeedFactor = F(cfg, "AvatarWalkSpeedFactor", c.AvatarWalkSpeedFactor, MinAvatarSpeedFactor, MaxAvatarSpeedFactor, warnings);
            c.AvatarRunSpeedFactor = F(cfg, "AvatarRunSpeedFactor", c.AvatarRunSpeedFactor, MinAvatarSpeedFactor, MaxAvatarSpeedFactor, warnings);
            c.AvatarFlySpeedFactor = F(cfg, "AvatarFlySpeedFactor", c.AvatarFlySpeedFactor, MinAvatarSpeedFactor, MaxAvatarSpeedFactor, warnings);
            c.AvatarFlyDownSpeedFactor = F(cfg, "AvatarFlyDownSpeedFactor", c.AvatarFlyDownSpeedFactor, MinAvatarSpeedFactor, MaxAvatarSpeedFactor, warnings);
            c.CapacityLogIntervalSeconds = F(cfg, "CapacityLogIntervalSeconds", c.CapacityLogIntervalSeconds, 0.1f, 86400f, warnings);
            c.JobPools = I(cfg, "JobPools", c.JobPools, 1, JoltPhysicsBackend.MaxJobPools, warnings);
            c.JobPoolFairHandoff = B(cfg, "JobPoolFairHandoff", c.JobPoolFairHandoff, warnings);
            // An invalid PhysicsStepRate warns and gives the default, as every other key does; it does not count as set.
            c.PhysicsStepRate = F(cfg, "PhysicsStepRate", c.PhysicsStepRate, 0f, MaxPhysicsStepRate, warnings);
            c.PhysicsStepRateSet = TryF(cfg.GetString("PhysicsStepRate", null), 0f, MaxPhysicsStepRate, out _);
            c.PhysicsStepCollisionSteps = I(cfg, "PhysicsStepCollisionSteps", c.PhysicsStepCollisionSteps, 1, 64, warnings);
            c.VehicleGroundGravityFactor = F(cfg, "VehicleGroundGravityFactor", c.VehicleGroundGravityFactor, 0f, 1f, warnings);
            c.VehiclePresets = E(cfg, "VehiclePresets", c.VehiclePresets, warnings, out bool presetsValid);
            c.VehiclePresetsSet = presetsValid && cfg.GetString("VehiclePresets", null) != null;
            c.VehicleBehaviour = E(cfg, "VehicleBehaviour", c.VehicleBehaviour, warnings, out _);
            c.VehicleMaxLinearSpeed = F(cfg, "VehicleMaxLinearSpeed", c.VehicleMaxLinearSpeed, 1f, 10000f, warnings);
            c.VehicleReferenceSpeed = F(cfg, "VehicleReferenceSpeed", c.VehicleReferenceSpeed, 0.1f, 10000f, warnings);
            c.VehicleMaxAngularSpeed = F(cfg, "VehicleMaxAngularSpeed", c.VehicleMaxAngularSpeed, 0.1f, 1000f, warnings);
            c.VehicleAngularLimiterFloor = F(cfg, "VehicleAngularLimiterFloor", c.VehicleAngularLimiterFloor, 0f, 1f, warnings);
            c.VehicleMinTimescale = F(cfg, "VehicleMinTimescale", c.VehicleMinTimescale, (float)VehicleMotorSolver.MinTimescale, 1f, warnings);
            c.VehicleMaxTimescale = F(cfg, "VehicleMaxTimescale", c.VehicleMaxTimescale, 1f, VehicleLimits.MaxTimescale, warnings);
            c.VehicleMaxDecayTimescale = F(cfg, "VehicleMaxDecayTimescale", c.VehicleMaxDecayTimescale, 1f, VehicleLimits.MaxTimescale, warnings);
            c.VehicleMaxDecayTimescaleSet = TryF(cfg.GetString("VehicleMaxDecayTimescale", null), 1f, VehicleLimits.MaxTimescale, out _);
            c.VehicleMaxHoverTimescale = F(cfg, "VehicleMaxHoverTimescale", c.VehicleMaxHoverTimescale, 1f, VehicleLimits.MaxTimescale, warnings);
            c.VehicleMaxAttractTimescale = F(cfg, "VehicleMaxAttractTimescale", c.VehicleMaxAttractTimescale, 1f, VehicleLimits.MaxTimescale, warnings);
            c.VehicleMaxMotorOffset = F(cfg, "VehicleMaxMotorOffset", c.VehicleMaxMotorOffset, 0f, 10000f, warnings);
            c.VehicleMinHoverHeight = F(cfg, "VehicleMinHoverHeight", c.VehicleMinHoverHeight, -100000f, 100000f, warnings);
            c.VehicleMaxHoverHeight = F(cfg, "VehicleMaxHoverHeight", c.VehicleMaxHoverHeight, -100000f, 100000f, warnings);
            if (c.VehicleMinHoverHeight > c.VehicleMaxHoverHeight)
            {
                warnings?.Add($"[{Section}] VehicleMinHoverHeight ({c.VehicleMinHoverHeight}) is above VehicleMaxHoverHeight ({c.VehicleMaxHoverHeight}); using the defaults for both.");
                c.VehicleMinHoverHeight = VehicleLimits.MinRegionHeight;
                c.VehicleMaxHoverHeight = VehicleLimits.MaxRegionHeight;
            }
            c.VehicleSledAssist = F(cfg, "VehicleSledAssist", c.VehicleSledAssist, 0f, 10f, warnings);
            string spikeRaw = cfg.GetString("VehicleSpikeMitigation", null);
            if (spikeRaw != null)
            {
                if (bool.TryParse(spikeRaw.Trim(), out bool spikeCut)) c.VehicleSpikeMitigation = spikeCut;
                else warnings?.Add($"[{Section}] VehicleSpikeMitigation = \"{spikeRaw}\" is invalid (expected true or false); using the behaviour's own choice.");
            }
            c.VehicleMotorTargetOnUndrivenAxes = B(cfg, "VehicleMotorTargetOnUndrivenAxes", c.VehicleMotorTargetOnUndrivenAxes, warnings);
            // Unset (or empty) leaves each part of a vehicle its material's friction; a number replaces it.
            string contactFriction = cfg.GetString("VehicleContactFriction", null);
            if (!string.IsNullOrWhiteSpace(contactFriction))
                c.VehicleContactFriction = TryF(contactFriction, 0f, 10f, out float vcf) ? vcf : WarnNull(contactFriction, "VehicleContactFriction", warnings);
            c.SledContactFriction = F(cfg, "SledContactFriction", c.SledContactFriction, 0f, 10f, warnings);
            c.BasicForcesBehaviour = E(cfg, "BasicForcesBehaviour", c.BasicForcesBehaviour, warnings, out _);
            c.FrictionCombine = E(cfg, "FrictionCombine", c.FrictionCombine, warnings, out _);
            c.RestitutionCombine = E(cfg, "RestitutionCombine", c.RestitutionCombine, warnings, out _);
            c.TerrainFriction = F(cfg, "TerrainFriction", c.TerrainFriction, 0f, JoltPrim.MaxFriction, warnings);
            c.TerrainRestitution = F(cfg, "TerrainRestitution", c.TerrainRestitution, 0f, 1f, warnings);
            c.VehicleRestSpeed = F(cfg, "VehicleRestSpeed", c.VehicleRestSpeed, 0f, 1f, warnings);
            c.BodyMaxLinearSpeed = F(cfg, "BodyMaxLinearSpeed", c.BodyMaxLinearSpeed, 1f, 100000f, warnings);
            c.BodyMaxAngularSpeed = F(cfg, "BodyMaxAngularSpeed", c.BodyMaxAngularSpeed, 0.1f, 10000f, warnings);
            c.MaxPhysicalComplexity = I(cfg, "MaxPhysicalComplexity", c.MaxPhysicalComplexity, 0, 1_000_000, warnings);
            c.PrimLinearDamping = F(cfg, "PrimLinearDamping", c.PrimLinearDamping, 0f, 100f, warnings);
            c.PrimAngularDamping = F(cfg, "PrimAngularDamping", c.PrimAngularDamping, 0f, 100f, warnings);
            c.RayCastBudgetMs = F(cfg, "RayCastBudgetMs", c.RayCastBudgetMs, 0.1f, 1000f, warnings);
            c.RayCastSimulatorBudgetMs = F(cfg, "RayCastSimulatorBudgetMs", c.RayCastSimulatorBudgetMs, 0.1f, 1000f, warnings);
            c.RayCastMaxTestedHits = I(cfg, "RayCastMaxTestedHits", c.RayCastMaxTestedHits, 1, 1_000_000, warnings);
            c.RayCastMaxHits = I(cfg, "RayCastMaxHits", c.RayCastMaxHits, 1, MaxRayCastHits, warnings);
            c.AvatarPushMaxSpeed = F(cfg, "AvatarPushMaxSpeed", c.AvatarPushMaxSpeed, 0f, 1000f, warnings);
            c.AvatarPushRecovery = F(cfg, "AvatarPushRecovery", c.AvatarPushRecovery, 0f, 1000f, warnings);
            c.AvatarObjectPushPerMomentum = F(cfg, "AvatarObjectPushPerMomentum", c.AvatarObjectPushPerMomentum, 0f, 100000f, warnings);
            c.AvatarObjectPushMaxForce = F(cfg, "AvatarObjectPushMaxForce", c.AvatarObjectPushMaxForce, 0f, 1000000f, warnings);
            return c;
        }

        /// <summary>
        /// The preset values llSetVehicleType gives: VehiclePresets where the configuration sets it; otherwise the
        /// InWorldz presets (legacy) under VehicleBehaviour = InWorldz and the documented presets under SecondLife.
        /// </summary>
        internal VehiclePresetSet EffectivePresets
            => VehiclePresetsSet ? VehiclePresets
             : VehicleBehaviour == VehicleBehaviour.InWorldz ? VehiclePresetSet.Legacy
             : VehiclePresetSet.Documented;

        /// <summary>
        /// The settings a region runs with, in one line for the region log, so an operator can check what a start-up read from
        /// [Jolt] without opening the file: the vehicle behaviour and the preset values in force, the avatar push keys, the
        /// physical complexity limit, the two rider-carrying settings, and the physics step rate the region got (<paramref name="effectiveStepRate"/>, 0 when it takes one step per heartbeat).
        /// </summary>
        internal string StartupSummary(float effectiveStepRate)
        {
            string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            string B(bool v) => v ? "true" : "false";
            return $"VehicleBehaviour={VehicleBehaviour}, VehiclePresets={EffectivePresets}, BasicForcesBehaviour={BasicForcesBehaviour}, " +
                   $"FrictionCombine={FrictionCombine}, RestitutionCombine={RestitutionCombine}, TerrainFriction={N(TerrainFriction)}, TerrainRestitution={N(TerrainRestitution)}, " +
                   $"VehicleContactFriction={(VehicleContactFriction.HasValue ? N(VehicleContactFriction.Value) : "material")}, SledContactFriction={N(SledContactFriction)}, " +
                   $"AvatarPushMaxSpeed={N(AvatarPushMaxSpeed)}, AvatarPushRecovery={N(AvatarPushRecovery)}, " +
                   $"AvatarObjectPushPerMomentum={N(AvatarObjectPushPerMomentum)}, AvatarObjectPushMaxForce={N(AvatarObjectPushMaxForce)}, " +
                   $"MaxPhysicalComplexity={MaxPhysicalComplexity}, KeyframedMotionCarriesRiders={B(KeyframedMotionCarriesRiders)}, " +
                   $"MovedPrimsCarryRiders={B(MovedPrimsCarryRiders)}, " +
                   (effectiveStepRate > 0f ? $"physics step rate={N(effectiveStepRate)} Hz" : "physics step rate=one step per heartbeat");
        }

        /// <summary>The vehicle settings every vehicle controller in a region with this configuration shares.</summary>
        internal VehicleSettings ToVehicleSettings() => new VehicleSettings
        {
            Presets = EffectivePresets,
            Behaviour = VehicleBehaviour,
            MaxLinearSpeed = VehicleMaxLinearSpeed,
            ReferenceSpeed = VehicleReferenceSpeed,
            MaxAngularSpeed = VehicleMaxAngularSpeed,
            AngularLimiterFloor = VehicleAngularLimiterFloor,
            MinTimescale = VehicleMinTimescale,
            MaxTimescale = VehicleMaxTimescale,
            MaxDecayTimescale = VehicleMaxDecayTimescale,
            MaxDecayTimescaleSet = VehicleMaxDecayTimescaleSet,
            MaxHoverTimescale = VehicleMaxHoverTimescale,
            MaxAttractTimescale = VehicleMaxAttractTimescale,
            MaxMotorOffset = VehicleMaxMotorOffset,
            MinHoverHeight = VehicleMinHoverHeight,
            MaxHoverHeight = VehicleMaxHoverHeight,
            SledAssist = VehicleSledAssist,
            MotorTargetOnUndrivenAxes = VehicleMotorTargetOnUndrivenAxes,
            SpikeMitigation = VehicleSpikeMitigation,
            ContactFriction = VehicleContactFriction,
            SledContactFriction = SledContactFriction,
            RestSpeed = VehicleRestSpeed,
        };

        /// <summary>
        /// The physics step rate a region with this heartbeat runs at: <see cref="PhysicsStepRate"/>, or 0 (one step
        /// per heartbeat) when it is off or below the heartbeat's own rate, which it cannot honour. A rate the operator
        /// set and that is refused sets <paramref name="warning"/>; the default refused (the operator set no rate) sets
        /// <paramref name="note"/>, an information line, instead.
        /// </summary>
        internal float EffectivePhysicsStepRate(float heartbeatSeconds, out string warning, out string note)
        {
            warning = null;
            note = null;
            if (PhysicsStepRate <= 0f)
                return 0f;
            if (!(heartbeatSeconds > 0f) || PhysicsStepRate * heartbeatSeconds < 1f - 1e-3f)
            {
                string rate = PhysicsStepRate.ToString(CultureInfo.InvariantCulture);
                string heartbeat = (heartbeatSeconds > 0f ? 1f / heartbeatSeconds : 0f).ToString("0.##", CultureInfo.InvariantCulture);
                if (PhysicsStepRateSet)
                    warning = $"[{Section}] PhysicsStepRate = {rate} is below the heartbeat's own rate " +
                              $"({heartbeat} Hz, [Startup] FrameTime); using 0, one physics step per heartbeat.";
                else
                    note = $"[{Section}] PhysicsStepRate is not set, and its default of {rate} Hz is below the heartbeat's own rate " +
                           $"({heartbeat} Hz, [Startup] FrameTime); running one physics step per heartbeat.";
                return 0f;
            }
            return PhysicsStepRate;
        }

        /// <summary>Area multiplier against a standard region: 1 for 256 x 256 (and anything smaller), 16 for 1024 x 1024.</summary>
        internal static long AreaFactorNumerator(uint sizeX, uint sizeY) => Math.Max((long)sizeX * sizeY, BaseArea);

        private static int ScaleByArea(int value, uint sizeX, uint sizeY)
            => (int)Math.Min((long)value * AreaFactorNumerator(sizeX, sizeY) / BaseArea, int.MaxValue);

        /// <summary>The backend settings for a region of this size - what AddRegion hands Initialize.</summary>
        internal PhysicsBackendSettings ToBackendSettings(uint sizeX, uint sizeY) => ToBackendSettings(sizeX, sizeY, substepping: false);

        /// <summary>As above; with <paramref name="substepping"/> the solver takes CollisionStepsAt(PhysicsStepRate) per step.</summary>
        internal PhysicsBackendSettings ToBackendSettings(uint sizeX, uint sizeY, bool substepping)
            => ToBackendSettings(sizeX, sizeY, substepping ? PhysicsStepRate : 0f);

        /// <summary>
        /// The slowest solver step rate the module gives Jolt. Jolt is built around a 1/60 s step: "In general, the
        /// system is stable when running at 60 Hz with 1 collision step" (JoltPhysics Docs/Architecture.md, "The
        /// Simulation Step"). A longer step leaves a tall stack swaying: ten stacked 0.5 m boxes stepped at 22.5 Hz with
        /// 2 collision steps (1/45 s) only just fall asleep at 40 velocity steps on one platform's native and never on the
        /// other's, while with 3 (1/67.5 s) both put them to sleep at the same time.
        /// </summary>
        internal const float MinCollisionStepRate = 60f;

        /// <summary>
        /// The solver's steps in each physics step at <paramref name="stepRate"/> physics steps a second:
        /// PhysicsStepCollisionSteps, or as many more as keep each solver step to 1/MinCollisionStepRate s (3 at 22.5 Hz,
        /// where 2 would be 1/45 s). With <paramref name="stepRate"/> 0, one physics step per heartbeat: CollisionSteps.
        /// </summary>
        internal int CollisionStepsAt(float stepRate)
            => stepRate > 0f
                ? Math.Max(PhysicsStepCollisionSteps, (int)Math.Ceiling(MinCollisionStepRate / stepRate - 1e-3f))
                : CollisionSteps;

        /// <summary>As above, for a region whose physics steps at <paramref name="stepRate"/> a second (0: one step per
        /// heartbeat).</summary>
        internal PhysicsBackendSettings ToBackendSettings(uint sizeX, uint sizeY, float stepRate)
        {
            PhysicsBackendSettings s = PhysicsBackendSettings.Default;
            s.Gravity = new SVector3(0f, 0f, Gravity);
            s.FrictionCombine = FrictionCombine;
            s.RestitutionCombine = RestitutionCombine;
            s.TerrainFriction = TerrainFriction;
            s.TerrainRestitution = TerrainRestitution;
            s.MaxBodies = MaxBodies > 0 ? MaxBodies : ScaleByArea(BaseMaxBodies, sizeX, sizeY);
            s.MaxBodyPairs = ScaleCapsWithArea ? ScaleByArea(MaxBodyPairs, sizeX, sizeY) : MaxBodyPairs;
            s.MaxContactConstraints = ScaleCapsWithArea ? ScaleByArea(MaxContactConstraints, sizeX, sizeY) : MaxContactConstraints;
            s.ThreadCount = ThreadCount;
            s.PositionIterations = PositionIterations;
            s.VelocityIterations = VelocityIterations;
            s.CollisionSteps = CollisionStepsAt(stepRate);
            s.DeterministicMode = DeterministicMode;
            s.AllowUnrecordedNative = AllowUnrecordedNative;
            s.JobPools = JobPools;
            s.JobPoolFairHandoff = JobPoolFairHandoff;
            s.MaxBodyLinearSpeed = BodyMaxLinearSpeed;
            s.MaxBodyAngularSpeed = BodyMaxAngularSpeed;
            s.BodyInternalEdgeRemoval = BodyEnhancedEdgeRemoval;
            s.AvatarInternalEdgeRemoval = AvatarEnhancedEdgeRemoval;
            s.TwoSidedMeshes = TwoSidedMeshCollision;
            s.SpeculativeContactDistance = SpeculativeContactDistance;
            s.SleepVelocityThreshold = SleepVelocityThreshold;
            s.RayCastBudgetMs = RayCastBudgetMs;
            s.RayCastSimulatorBudgetMs = RayCastSimulatorBudgetMs;
            s.RayCastMaxTestedHits = RayCastMaxTestedHits;
            s.AvatarPushMaxSpeed = AvatarPushMaxSpeed;
            s.AvatarPushRecovery = AvatarPushRecovery;
            s.AvatarObjectPushPerMomentum = AvatarObjectPushPerMomentum;
            s.AvatarObjectPushMaxForce = AvatarObjectPushMaxForce;
            return s;
        }

        /// <summary>How many worker threads these settings ask the shared pool for (the backend's own rule).</summary>
        internal int RequestedThreadCount => JoltPhysicsBackend.ResolveThreadCount(ThreadCount, DeterministicMode, JobPools);

        /// <summary>The workers each job pool gets when RequestedThreadCount is split across JobPools.</summary>
        internal int RequestedThreadsPerPool => JoltPhysicsBackend.ResolveThreadsPerPool(RequestedThreadCount, JobPools);

        // ---------------------------------------------------------------- parsing (invariant culture, never throws)

        private static float F(IConfig cfg, string key, float def, float min, float max, List<string> warnings)
        {
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            if (TryF(raw, min, max, out float v))
                return v;
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected a finite number in [{min}, {max}]); using the default {def.ToString(CultureInfo.InvariantCulture)}.");
            return def;
        }

        // A finite number in [min, max]; false for null.
        private static bool TryF(string raw, float min, float max, out float v)
        {
            v = 0f;
            return raw != null && float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                   && float.IsFinite(v) && v >= min && v <= max;
        }

        // An invalid VehicleContactFriction: warn, and leave the vehicle its material's friction.
        private static float? WarnNull(string raw, string key, List<string> warnings)
        {
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected a finite number in [0, 10], or nothing for the material's value); using the material's.");
            return null;
        }

        private static int I(IConfig cfg, string key, int def, int min, int max, List<string> warnings)
        {
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= min && v <= max)
                return v;
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected an integer in [{min}, {max}]); using the default {def}.");
            return def;
        }

        // An enum value by its name, case ignored (numbers are not accepted).
        private static T E<T>(IConfig cfg, string key, T def, List<string> warnings, out bool valid) where T : struct, Enum
        {
            valid = false;
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            string name = raw.Trim();
            if (name.Length > 0 && !char.IsDigit(name[0]) && name[0] != '-' && Enum.TryParse(name, true, out T v) && Enum.IsDefined(v))
            {
                valid = true;
                return v;
            }
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected one of {string.Join(", ", Enum.GetNames<T>())}); using the default {def}.");
            return def;
        }

        private static bool B(IConfig cfg, string key, bool def, List<string> warnings)
        {
            string raw = cfg.GetString(key, null);
            if (raw == null) return def;
            if (bool.TryParse(raw.Trim(), out bool v))
                return v;
            warnings?.Add($"[{Section}] {key} = \"{raw}\" is invalid (expected true or false); using the default {def}.");
            return def;
        }
    }
}
