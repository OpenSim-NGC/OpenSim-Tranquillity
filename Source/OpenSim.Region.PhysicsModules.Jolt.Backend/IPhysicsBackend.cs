/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Physics backend abstraction for the Jolt physics module.
//
// This is the seam between OpenSim's PhysicsScene/PhysicsActor contract and a
// concrete physics engine. Nothing above this interface knows what engine is
// running; nothing below it knows what OpenSim is.
//
// Design rules:
//   1. Handles, not objects. A region can hold 50k+ prims; we do not allocate a
//      managed wrapper per body. Handles are blittable structs that map onto the
//      engine's own id type (Jolt: BodyID).
//   2. Bulk step output into caller-owned buffers. Zero per-frame allocation.
//   3. Shapes have independent lifetime from bodies. Meshes are expensive to
//      cook and are shared across every prim using the same asset.
//   4. No SL semantics in here. The SL vehicle model, llCastRay filtering rules,
//      collision_start/end dispatch, and permission checks all live ABOVE this
//      interface. This layer knows about rigid bodies, not about scripts.

using System;
using System.Numerics;

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    /// <summary>How the job pools' thread count was chosen.</summary>
    public enum JobThreadSource
    {
        /// <summary>[Jolt] ThreadCount 0: each pool's share of the cores, at most 4.</summary>
        Automatic = 0,
        /// <summary>A positive [Jolt] ThreadCount, all pools together.</summary>
        Set = 1,
        /// <summary>[Jolt] DeterministicMode: one thread.</summary>
        Deterministic = 2,
    }

    // ---------------------------------------------------------------------
    // Handles
    // ---------------------------------------------------------------------

    /// <summary>Opaque reference to a rigid body owned by the backend.</summary>
    public readonly struct BodyId : IEquatable<BodyId>
    {
        public static readonly BodyId Invalid = default;

        public readonly uint Value;
        public BodyId(uint value) => Value = value;

        public bool IsValid => Value != 0u;
        public bool Equals(BodyId other) => Value == other.Value;
        public override bool Equals(object? o) => o is BodyId b && Equals(b);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"Body({Value})";
    }

    /// <summary>
    /// Opaque reference to a collision shape. Shapes are reference-counted and
    /// shared: one cooked mesh backs every prim that uses that asset.
    /// </summary>
    public readonly struct ShapeId : IEquatable<ShapeId>
    {
        public static readonly ShapeId Invalid = default;

        public readonly uint Value;
        public ShapeId(uint value) => Value = value;

        public bool IsValid => Value != 0u;
        public bool Equals(ShapeId other) => Value == other.Value;
        public override bool Equals(object? o) => o is ShapeId s && Equals(s);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"Shape({Value})";
    }

    /// <summary>Opaque reference to a character controller (avatar or NPC).</summary>
    public readonly struct CharacterId : IEquatable<CharacterId>
    {
        public static readonly CharacterId Invalid = default;

        public readonly uint Value;
        public CharacterId(uint value) => Value = value;

        public bool IsValid => Value != 0u;
        public bool Equals(CharacterId other) => Value == other.Value;
        public override bool Equals(object? o) => o is CharacterId c && Equals(c);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"Character({Value})";
    }

    // ---------------------------------------------------------------------
    // Enums
    // ---------------------------------------------------------------------

    /// <summary>
    /// Collision categories. Maps to Jolt ObjectLayer, and is coarsened into a
    /// BroadPhaseLayer by the backend. Keep this small - broad phase layers are
    /// a fixed cost and 4-5 is the sweet spot.
    /// </summary>
    public enum PhysicsLayer : byte
    {
        /// <summary>Region heightfield. Never moves.</summary>
        Terrain = 0,

        /// <summary>Non-physical prims. Collide with dynamics and avatars, never move.</summary>
        Static = 1,

        /// <summary>Physical prims. Full dynamics.</summary>
        Dynamic = 2,

        /// <summary>Avatar and NPC character controllers.</summary>
        Avatar = 3,

        /// <summary>VolumeDetect / phantom-with-events. Reports overlap, never resolves.</summary>
        Sensor = 4,

        /// <summary>
        /// Cheap tier: collides with Terrain, Static, Dynamic and Avatar, but NEVER
        /// with other Debris - the Debris-vs-Debris pair is what would be O(n^2).
        /// Useful for bullet-hell / particle-ish content. Not required for first light.
        /// </summary>
        Debris = 5,

        /// <summary>
        /// INTERNAL: the query-visible marker carried by each avatar. Collides with NOTHING in the
        /// simulation (never enters the solve - no push, no contacts), so it is inert; it exists ONLY
        /// so RayCast/Overlap/ShapeCast can find an avatar (whose CharacterVirtual is not a body).
        /// Backend-managed; do not put prims on this layer. Found only by Avatar-filtered queries.
        /// </summary>
        AvatarQuery = 6,

        /// <summary>
        /// Phantom prims (STATUS_PHANTOM / PRIM_PHANTOM). Collides with the terrain and nothing else: Second
        /// Life's phantom objects let objects and avatars pass through them, and a physical one "collide[s] with
        /// the ground but will not pass through" (llVolumeDetect, wiki.secondlife.com). Found by the queries that
        /// name Sensor, the same as a volume detector.
        /// </summary>
        Phantom = 7,

        /// <summary>
        /// A non-physical prim that keyframed motion is moving: a kinematic body that collides exactly as
        /// <see cref="Static"/> does. It sits in the moving broad phase, so that each move does not touch the large
        /// tree of fixed prims; the prim goes back to <see cref="Static"/> when the motion ends.
        /// </summary>
        MovingStatic = 8,
    }

    /// <summary>
    /// Continuous collision detection for a moving body: whether the engine casts it along its motion in a collision
    /// step, so it stops at what it would hit inside the step instead of ending the step past it or overlapping it.
    /// </summary>
    public enum ContinuousCollision : byte
    {
        /// <summary>Never cast: collisions are found where the body is at the start of each collision step.</summary>
        Off = 0,

        /// <summary>Jolt's LinearCast motion quality all the time: Jolt casts the body in every collision step in which
        /// it would move further than its cast threshold (PhysicsSettings.mLinearCastThreshold times the shape's inner
        /// radius).</summary>
        On = 1,

        /// <summary>
        /// LinearCast only for the collision steps in which the body moves fast enough for Jolt to cast it (the test of
        /// <see cref="On"/>, made with the body's velocity before each update), Discrete in every other. A body that
        /// is never that fast moves exactly as an <see cref="Off"/> one. (A LinearCast body that Jolt does not cast is
        /// not quite Discrete: when another body's cast meets it, Jolt takes it as not yet moved in that step, so the
        /// two bodies' relative motion differs from a Discrete body's.)
        /// </summary>
        WhenFast = 2,
    }

    public enum BodyMotionType : byte
    {
        /// <summary>Immovable. Infinite mass. Non-physical prims and terrain.</summary>
        Static = 0,

        /// <summary>Moved by script/animation, pushes dynamics, ignores forces.</summary>
        Kinematic = 1,

        /// <summary>Full rigid body dynamics.</summary>
        Dynamic = 2,
    }

    [Flags]
    public enum BodyStateFlags : byte
    {
        None = 0,
        /// <summary>Body is awake and simulating.</summary>
        Active = 1 << 0,
        /// <summary>Body went to sleep this step. Emit one final update, then stop.</summary>
        JustDeactivated = 1 << 1,
        /// <summary>Body woke this step.</summary>
        JustActivated = 1 << 2,
    }

    /// <summary>
    /// Contact lifecycle. Maps directly onto LSL collision_start / collision /
    /// collision_end so the dispatch layer above does not have to diff sets.
    /// </summary>
    public enum ContactPhase : byte
    {
        Begin = 0,
        Persist = 1,
        End = 2,
    }

    [Flags]
    public enum QueryFilter : uint
    {
        None = 0,
        Terrain = 1 << 0,
        Static = 1 << 1,
        Dynamic = 1 << 2,
        Avatar = 1 << 3,
        Sensor = 1 << 4,

        /// <summary>Everything a default llCastRay should see.</summary>
        Default = Terrain | Static | Dynamic | Avatar,
        All = Terrain | Static | Dynamic | Avatar | Sensor,
    }

    // ---------------------------------------------------------------------
    // Descriptors
    // ---------------------------------------------------------------------

    /// <summary>One child of a compound shape, in parent-local space.</summary>
    public struct CompoundChild
    {
        public ShapeId Shape;
        public Vector3 Position;
        public Quaternion Orientation;
        /// <summary>Copied into contact reports so a linkset hit knows which prim.</summary>
        public uint UserData;
    }

    public struct BodyDesc
    {
        public ShapeId Shape;
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;

        public PhysicsLayer Layer;
        public BodyMotionType MotionType;

        /// <summary>Explicit mass in kg. If &lt;= 0, computed from shape volume and Density.</summary>
        public float Mass;
        public float Density;

        public float Friction;
        public float Restitution;
        public float LinearDamping;
        public float AngularDamping;

        /// <summary>1.0 = normal gravity. 0 = floating. Negative = rises. Drives llSetBuoyancy.</summary>
        public float GravityFactor;

        /// <summary>Reports contacts but never resolves them. VolumeDetect.</summary>
        public bool IsSensor;

        /// <summary>
        /// Whether this body wants per-step contact (Persist) events forwarded. Begin/End edge
        /// events are ALWAYS reported; Persist is gated on this so a resting or standing object
        /// does not emit a contact every step forever. Set it from whether the object has a
        /// <c>collision</c> handler registered. (Jolt also stops firing Persist once a body sleeps,
        /// so this gate only ever suppresses awake-but-touching pairs.)
        /// </summary>
        public bool WantsContactEvents;

        /// <summary>
        /// Whether to wake the body on insertion. Default FALSE and that matters:
        /// region startup inserts tens of thousands of bodies and waking each one
        /// is the single easiest way to make startup pathological.
        /// </summary>
        public bool StartActive;

        /// <summary>Continuous collision detection (see <see cref="ContinuousCollision"/>). Only a movable body has it.</summary>
        public ContinuousCollision Ccd;

        /// <summary>SceneObjectPart.LocalId. Echoed back in every report to avoid a lookup.</summary>
        public uint UserData;

        public static BodyDesc Default => new BodyDesc
        {
            Orientation = Quaternion.Identity,
            Layer = PhysicsLayer.Static,
            MotionType = BodyMotionType.Static,
            Mass = 0f,
            Density = 1000f,       // water; OpenSim's historical prim density
            Friction = 0.6f,
            Restitution = 0.0f,
            LinearDamping = 0.05f,
            AngularDamping = 0.05f,
            GravityFactor = 1.0f,
            StartActive = false,
        };
    }

    public struct CharacterDesc
    {
        public Vector3 Position;
        public Quaternion Orientation;

        /// <summary>Capsule half-height EXCLUDING the caps.</summary>
        public float CapsuleHalfHeight;
        public float CapsuleRadius;

        public float Mass;
        public float Friction;

        /// <summary>Max slope the character can walk up, radians.</summary>
        public float MaxSlopeAngle;

        /// <summary>Max step height auto-climbed without a jump. SL feel lives here.</summary>
        public float StepHeight;

        /// <summary>
        /// Relative push strength against dynamic bodies the character walks into. 1.0 = the backend's
        /// default push force; scales linearly. (The backend maps this onto Jolt's MaxStrength in N.)
        /// </summary>
        public float PushStrength;

        /// <summary>Initial upward speed (m/s) of a jump from solid ground. SL jump feel lives here.</summary>
        public float JumpSpeed;

        /// <summary>
        /// Whether this avatar wants per-step contact (Persist) events forwarded. Same gate as
        /// <see cref="BodyDesc.WantsContactEvents"/>: Begin/End always report; Persist is gated so a
        /// standing avatar (whose controller never sleeps) does not emit a floor contact every step
        /// forever. Set from whether the avatar has a <c>collision</c> handler registered.
        /// </summary>
        public bool WantsContactEvents;

        public uint UserData;

        public static CharacterDesc Default => new CharacterDesc
        {
            Orientation = Quaternion.Identity,
            CapsuleHalfHeight = 0.45f,
            CapsuleRadius = 0.30f,
            Mass = 80f,
            Friction = 0.5f,
            MaxSlopeAngle = 50f * (MathF.PI / 180f),
            StepHeight = 0.45f,
            PushStrength = 1.0f,
            JumpSpeed = 4.0f,
        };
    }

    // ---------------------------------------------------------------------
    // Step output
    // ---------------------------------------------------------------------

    public struct BodyState
    {
        public BodyId Body;
        public uint UserData;
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public BodyStateFlags Flags;
    }

    public struct CharacterState
    {
        public CharacterId Character;
        public uint UserData;
        public Vector3 Position;
        /// <summary>The velocity the character really moved at in its last step (what a wall stopped is not in it),
        /// with the gravity it gained after the move.</summary>
        public Vector3 LinearVelocity;
        public Vector3 GroundNormal;
        public BodyId GroundBody;
        /// <summary>The ground body is the region's terrain. The terrain is a body like any other in the solve, so
        /// <see cref="GroundBody"/> alone does not tell the terrain from a prim.</summary>
        public bool GroundIsTerrain;
        public bool IsSupported;
        /// <summary>Standing on a slope too steep to hold. Drives the SL slide-off behaviour.</summary>
        public bool IsSliding;
    }

    public struct ContactReport
    {
        public BodyId BodyA;
        public BodyId BodyB;
        public uint UserDataA;
        public uint UserDataB;
        /// <summary>The STRUCK part's UserData on each side, resolved from the contact sub-shape: for a
        /// compound (linkset) body this is the specific child prim's id; for a single-shape body it is the
        /// body's own UserData. Drives per-child collision identity (llDetectedLinkNumber).</summary>
        public uint ChildUserDataA;
        public uint ChildUserDataB;
        public Vector3 Point;
        /// <summary>Points from A toward B.</summary>
        public Vector3 Normal;
        /// <summary>Newton-seconds. Feeds collision sound volume and damage models.</summary>
        public float Impulse;
        /// <summary>How fast B moves away from A along <see cref="Normal"/> where they touch (m/s), from their velocities
        /// before the contact acts on them: below zero while they close. The same number for either side, since both its
        /// velocity difference and its normal turn round. The simulator's collision sounds and impact damage read it as
        /// ContactPoint.RelativeSpeed. Zero on an End report.</summary>
        public float RelativeSpeed;
        public ContactPhase Phase;
    }

    /// <summary>
    /// Result of one Step. Counts tell you how much of each caller-owned buffer
    /// was filled; overflow flags tell you whether data was DROPPED, which is
    /// something the layer above must surface rather than silently eat.
    /// </summary>
    public readonly struct StepResult
    {
        public readonly int BodyUpdateCount;
        public readonly int CharacterUpdateCount;
        public readonly int ContactCount;
        public readonly bool BodyBufferOverflowed;
        public readonly bool ContactBufferOverflowed;
        public readonly int ActiveBodyCount;
        public readonly float PhysicsMilliseconds;

        public StepResult(
            int bodyUpdateCount,
            int characterUpdateCount,
            int contactCount,
            bool bodyOverflow,
            bool contactOverflow,
            int activeBodyCount,
            float physicsMs)
            : this(bodyUpdateCount, characterUpdateCount, contactCount, bodyOverflow, contactOverflow, activeBodyCount, physicsMs,
                   in StepWaits.None)
        {
        }

        public StepResult(
            int bodyUpdateCount,
            int characterUpdateCount,
            int contactCount,
            bool bodyOverflow,
            bool contactOverflow,
            int activeBodyCount,
            float physicsMs,
            in StepWaits waits)
        {
            BodyUpdateCount = bodyUpdateCount;
            CharacterUpdateCount = characterUpdateCount;
            ContactCount = contactCount;
            BodyBufferOverflowed = bodyOverflow;
            ContactBufferOverflowed = contactOverflow;
            ActiveBodyCount = activeBodyCount;
            PhysicsMilliseconds = physicsMs;
            Waits = waits;
        }

        /// <summary>What this step waited for before it could run. <see cref="PhysicsMilliseconds"/> includes the region
        /// lock wait (as it always has) but not the job pool wait.</summary>
        public readonly StepWaits Waits;
    }

    /// <summary>What one Step waited for: its job pool (another region's step), and its own region's lock (a body
    /// change or query on another thread). A wait is counted only when the lock was taken when the step arrived.</summary>
    public readonly struct StepWaits
    {
        public static readonly StepWaits None = default;

        public readonly bool PoolWaited;
        public readonly double PoolWaitMs;
        /// <summary>The region that last took the pool before this step started waiting for it: the holder at that
        /// moment (null if not known).</summary>
        public readonly string? PoolHeldBy;
        public readonly bool RegionLockWaited;
        public readonly double RegionLockWaitMs;

        public StepWaits(bool poolWaited, double poolWaitMs, string? poolHeldBy, bool regionLockWaited, double regionLockWaitMs)
        {
            PoolWaited = poolWaited;
            PoolWaitMs = poolWaitMs;
            PoolHeldBy = poolHeldBy;
            RegionLockWaited = regionLockWaited;
            RegionLockWaitMs = regionLockWaitMs;
        }
    }

    /// <summary>The capacity errors a physics update can report (Jolt's PhysicsUpdateError, engine-neutral).</summary>
    [Flags]
    public enum PhysicsUpdateErrors : byte
    {
        None = 0,
        /// <summary>The contact-manifold cache filled; some contacts were not generated.</summary>
        ManifoldCacheFull = 1 << 0,
        /// <summary>The body-pair cache filled; some colliding pairs were not tested.</summary>
        BodyPairCacheFull = 1 << 1,
        /// <summary>The contact-constraint buffer filled; some contacts were not solved.</summary>
        ContactConstraintsFull = 1 << 2,
    }

    /// <summary>
    /// Cumulative health counters for one backend, read off the step thread by the module's rate-limited logger
    /// and the `jolt capacity` console command. Everything here is a snapshot; nothing resets on read.
    /// </summary>
    public struct PhysicsCapacityStats
    {
        /// <summary>Mutator calls dropped because an argument was NaN/Inf (or a zero-length quaternion).</summary>
        public long RejectedNonFinite;

        // -- ray casts (RayCastLimited) --
        /// <summary>Casts made, cumulative; of them, refused (their budget spent) and cut short. Both budgets together.</summary>
        public long RayCasts;
        public long RayCastsRefused;
        public long RayCastsCutShort;
        /// <summary>Time spent in them, cumulative, and the most one heartbeat has spent on both budgets together (ms).</summary>
        public double RayCastMsTotal;
        public double RayCastMsMaxHeartbeat;
        /// <summary>The same counts for each budget (<see cref="RayCastBudget"/>).</summary>
        public RayCastBudgetStats ScriptRayCasts;
        public RayCastBudgetStats SimulatorRayCasts;

        // -- capacity --
        /// <summary>Steps whose update reported each flag, cumulative.</summary>
        public long ManifoldCacheFullSteps;
        public long BodyPairCacheFullSteps;
        public long ContactConstraintsFullSteps;
        /// <summary>The most recent non-None update error (None if there has never been one).</summary>
        public PhysicsUpdateErrors LastUpdateError;
        /// <summary>
        /// Bodies refused by the engine (MaxBodies reached): CreateBody calls (each returned BodyId.Invalid), terrain
        /// bodies and avatar query markers.
        /// </summary>
        public long BodyCreateFailures;
        /// <summary>The last SetTerrain was refused a body (MaxBodies reached): the region has no terrain collision.</summary>
        public bool TerrainBodyMissing;

        public int LiveBodyCount;
        /// <summary>Shape handles held (CreateBoxShape, CreateCompoundShape and the rest, until ReleaseShape drops the
        /// last reference), each owning a native shape reference.</summary>
        public int LiveShapeCount;
        public int ActiveBodyCount;
        public int MaxBodies;
        public int MaxBodyPairs;
        public int MaxContactConstraints;
        public int CharacterCount;
        public int ContactRingCapacity;
        /// <summary>Contacts dropped because the ring was full, cumulative.</summary>
        public long DroppedContacts;
        /// <summary>The resolved ThreadCount the job pools were sized from: TOTAL workers asked for, all pools (process-wide).</summary>
        public int JobThreadCount;

        // -- job pools: one physics update at a time per pool --
        /// <summary>How many job pools the process has (process-wide; the first region wins).</summary>
        public int JobPools;
        /// <summary>Worker threads in each pool: max(1, JobThreadCount / JobPools).</summary>
        public int JobThreadsPerPool;
        /// <summary>How JobThreadCount was chosen: automatic ([Jolt] ThreadCount 0), set, or DeterministicMode.</summary>
        public JobThreadSource JobThreadSource;
        /// <summary>The pool this region was assigned at Initialize (0-based).</summary>
        public int PoolIndex;
        /// <summary>This region's updates that had to wait for its pool's gate, cumulative.</summary>
        public long UpdateGateWaits;
        /// <summary>This region's total and longest gate wait, milliseconds, cumulative.</summary>
        public double UpdateGateWaitMsTotal;
        public double UpdateGateWaitMsMax;
        /// <summary>The most updates ever inside this region's pool at once since the pool was created. Must be 1.</summary>
        public int PoolPeakInside;
        /// <summary>Whether the job pools hand over first come, first served ([Jolt] JobPoolFairHandoff; process-wide).</summary>
        public bool JobPoolFairHandoff;
        /// <summary>The job pools the first region's settings asked for ([Jolt] JobPools, resolved); more than
        /// <see cref="JobPools"/> when the loaded native allows only one pool.</summary>
        public int JobPoolsRequested;
        /// <summary>Why <see cref="JobPools"/> is below <see cref="JobPoolsRequested"/>, in words; null when it is not.</summary>
        public string? JobPoolsLimitedBy;
        /// <summary>This region's steps that had to wait for the region's own lock, cumulative; and the total and
        /// longest of those waits, milliseconds.</summary>
        public long RegionLockWaits;
        public double RegionLockWaitMsTotal;
        public double RegionLockWaitMsMax;
    }

    /// <summary>
    /// Which per-heartbeat allowance a ray cast (<see cref="IPhysicsBackend.RayCastLimited"/>) is charged to. Each has
    /// its own time per heartbeat, and running out of one never refuses a cast charged to the other.
    /// </summary>
    public enum RayCastBudget
    {
        /// <summary>Casts known to come from a script (<see cref="PhysicsBackendSettings.RayCastBudgetMs"/>).</summary>
        Script,
        /// <summary>Every other cast: the simulator's own (rez placement, landing after login or teleport) and script
        /// casts that arrive without anything saying they are a script's
        /// (<see cref="PhysicsBackendSettings.RayCastSimulatorBudgetMs"/>).</summary>
        Simulator,
        /// <summary>Charged to no budget and counted nowhere: the module's own warm-up casts when a region loads, which
        /// pay the process's one-time costs of the cast path before the first real cast. Never refused; still cut
        /// short at the simulator budget's time.</summary>
        None,
    }

    /// <summary>One <see cref="RayCastBudget"/>'s counters, cumulative: casts made, refused and cut short, and the time
    /// they took (ms).</summary>
    public struct RayCastBudgetStats
    {
        public long Casts;
        public long Refused;
        public long CutShort;
        public double MsTotal;
    }

    /// <summary>How a ray cast (<see cref="IPhysicsBackend.RayCastLimited"/>) ended.</summary>
    public enum RayCastStatus
    {
        /// <summary>The cast ran; its hits are returned.</summary>
        Ok,
        /// <summary>Not cast: the region had spent the cast's budget for this heartbeat.</summary>
        Refused,
        /// <summary>Stopped part way: it ran past the time left, or past the hits one cast may test.</summary>
        CutShort,
    }

    public struct RayHit
    {
        public BodyId Body;
        public uint UserData;
        /// <summary>For compound shapes, the UserData of the struck child prim.</summary>
        public uint ChildUserData;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
    }

    // ---------------------------------------------------------------------
    // The interface
    // ---------------------------------------------------------------------

    /// <summary>
    /// A physics engine, as the region physics module needs one.
    ///
    /// THREADING: implementations must permit Create/Remove/Set* calls from
    /// threads other than the one calling Step, and must permit queries to run
    /// concurrently with Step. This is not a courtesy requirement - it is the
    /// reason we can drop the taint-list pattern that ODE and BulletSim carry.
    /// A backend that cannot honour it must serialise internally rather than
    /// pushing that burden back up.
    /// </summary>
    public interface IPhysicsBackend : IDisposable
    {
        string Name { get; }
        string Version { get; }

        // -- lifecycle ----------------------------------------------------

        void Initialize(in PhysicsBackendSettings settings);

        // -- shapes -------------------------------------------------------
        // Reference counted. CreateXShape returns a shape with refcount 1;
        // attaching to a body adds a reference. Call ReleaseShape when the
        // asset cache evicts it, not when a body dies.

        ShapeId CreateBoxShape(Vector3 halfExtents);
        ShapeId CreateSphereShape(float radius);
        ShapeId CreateCapsuleShape(float halfHeight, float radius);
        ShapeId CreateCylinderShape(float halfHeight, float radius);
        ShapeId CreateConvexHullShape(ReadOnlySpan<Vector3> points);
        ShapeId CreateMeshShape(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices);
        ShapeId CreateCompoundShape(ReadOnlySpan<CompoundChild> children);

        /// <summary>
        /// One shape made of convex hulls that already exist (CreateConvexHullShape), each at the shape's origin: how a
        /// mesh asset's hull list is cooked. Unlike a linkset's CreateCompoundShape the parts are not prims: a hit on it
        /// names the body, SetBodyPartMaterial treats it as one shape, and SetShapeDensity sets the density of every hull.
        /// Needs at least two hulls (use the single hull directly otherwise). The hull handles may be released after
        /// the call; the shape keeps the hulls alive.
        /// </summary>
        ShapeId CreateHullCompoundShape(ReadOnlySpan<ShapeId> hulls);

        ShapeId CreateHeightFieldShape(
            ReadOnlySpan<float> heights, int sampleCountX, int sampleCountY, Vector3 scale);

        /// <summary>
        /// Wrap an existing shape with a scale. This is how prim resize works
        /// without re-cooking geometry - critical, because cooking a sculpt or
        /// mesh is orders of magnitude more expensive than wrapping it.
        /// </summary>
        ShapeId CreateScaledShape(ShapeId baseShape, Vector3 scale);

        /// <summary>
        /// True when the shape can be scaled by exactly this scale (CreateScaledShape would otherwise change it to the
        /// nearest valid one: a sphere or capsule needs a uniform scale, a cylinder a uniform radial one). False for a
        /// shape that is not live.
        /// </summary>
        bool IsScaleValid(ShapeId shape, Vector3 scale);

        void AddShapeRef(ShapeId shape);
        void ReleaseShape(ShapeId shape);

        // -- bodies -------------------------------------------------------

        BodyId CreateBody(in BodyDesc desc);
        void RemoveBody(BodyId body);
        bool IsBodyValid(BodyId body);

        /// <summary>
        /// Attaches a provider that changes <paramref name="body"/>'s velocities before every collision step of an
        /// Update (see <see cref="IStepForceProvider"/>). The backend registers one step listener with the engine when the
        /// first provider attaches and removes it when the last detaches; both happen between Updates. Dispose the result
        /// to detach. Never call this, or dispose the result, from inside a provider. Removing the body detaches its
        /// providers.
        /// </summary>
        IDisposable AttachStepForceProvider(BodyId body, IStepForceProvider provider);

        void SetBodyShape(BodyId body, ShapeId shape, bool recomputeMass);
        void SetBodyMotionType(BodyId body, BodyMotionType motionType, bool activate);
        /// <summary>Moves a live body to another layer in place: its transform, velocity and id are kept.</summary>
        void SetBodyLayer(BodyId body, PhysicsLayer layer);
        /// <summary>Makes a live body a sensor (reports contacts, never resolves them) or a solid body, in place.</summary>
        void SetBodySensor(BodyId body, bool isSensor);

        void SetBodyTransform(BodyId body, Vector3 position, Quaternion orientation, bool activate);

        /// <summary>
        /// Moves a kinematic body so that it reaches the given pose after <paramref name="seconds"/> of simulation, by
        /// giving it the linear and angular velocity that cover the way in that time. What touches or stands on the body
        /// is carried or pushed by it. Nothing happens for a body that is not kinematic or a time that is not positive.
        /// </summary>
        void MoveKinematicBody(BodyId body, Vector3 position, Quaternion orientation, float seconds);

        /// <summary>
        /// Puts a body at a pose with the given velocities at once, with no travel in between: a teleport when the
        /// velocities are zero (what touches it is not flung), and the way to stop a kinematic body at a place.
        /// </summary>
        void SetBodyPoseAndVelocity(BodyId body, Vector3 position, Quaternion orientation, Vector3 linearVelocity, Vector3 angularVelocity);

        void SetBodyLinearVelocity(BodyId body, Vector3 velocity);
        void SetBodyAngularVelocity(BodyId body, Vector3 velocity);

        void SetBodyMass(BodyId body, float mass);
        /// <summary>Read the body's assigned mass (explicit, or shape Volume x Density). 0 if unknown/static.</summary>
        float GetBodyMass(BodyId body);

        /// <summary>True when the body (or the avatar) has Jolt's enhanced internal edge removal on (PhysicsBackendSettings
        /// BodyInternalEdgeRemoval, AvatarInternalEdgeRemoval). False for a handle that is not live.</summary>
        bool GetBodyEnhancedEdgeRemoval(BodyId body);
        bool GetCharacterEnhancedEdgeRemoval(CharacterId character);
        /// <summary>Local principal moments of inertia (diagonal), kg*m^2. Zero for non-dynamic bodies.
        /// The vehicle controller's vertical attractor scales its restoring torque by this (the
        /// BulletSim equivalent is the prim's CalculateLocalInertia result).</summary>
        Vector3 GetBodyInertiaDiagonal(BodyId body);
        /// <summary>The body's linear damping (1/s); 0 for a static or unknown body.</summary>
        float GetBodyLinearDamping(BodyId body);
        /// <summary>Recompute + apply the dynamic mass as (shape geometric Volume x physicalDensity kg/m^3).
        /// Lets the module honour a prim's SceneObjectPart.Density instead of the BodyDesc default.</summary>
        void SetBodyDensity(BodyId body, float physicalDensity);
        void SetBodyFriction(BodyId body, float friction);
        void SetBodyRestitution(BodyId body, float restitution);
        /// <summary>The contact friction and restitution of one part of a body: the compound child whose UserData is
        /// <paramref name="partUserData"/>, or the whole body when it has one shape. A contact combines the two touching
        /// parts' values: friction sqrt(f1 x f2), restitution r1 x r2. SetBodyFriction and SetBodyRestitution set every
        /// part. A recreated body starts again from its BodyDesc values.</summary>
        void SetBodyPartMaterial(BodyId body, uint partUserData, float friction, float restitution);
        /// <summary>true: every contact this body takes part in combines friction with <see cref="MaterialCombine.Min"/>, whatever
        /// the world's rule is, so a part whose friction is 0 stays frictionless on any surface. false (the default of a new
        /// body): the world's rule.</summary>
        void SetBodyFrictionMin(BodyId body, bool min);
        /// <summary>A live shape's geometric volume (m^3); 0 for an unknown handle.</summary>
        float GetShapeVolume(ShapeId shape);
        /// <summary>A live shape's centre of mass in the shape's own frame (m), as Jolt computes it from the geometry at a
        /// uniform density; zero for an unknown handle.</summary>
        Vector3 GetShapeCenterOfMass(ShapeId shape);
        /// <summary>The density (kg/m^3) a convex shape gives its mass properties; Jolt's default is 1000. A compound
        /// built from shapes afterwards weights each sub-shape's mass, centre of mass and inertia by it. A body made on the
        /// shape alone is not affected: its mass is set from BodyDesc. A hull compound (CreateHullCompoundShape) sets it on
        /// every hull. No-op for any other non-convex shape or an unknown handle.</summary>
        void SetShapeDensity(ShapeId shape, float density);
        /// <summary>A convex shape's density (kg/m^3); 0 for a non-convex shape or an unknown handle.</summary>
        float GetShapeDensity(ShapeId shape);
        void SetBodyDamping(BodyId body, float linear, float angular);
        void SetBodyGravityFactor(BodyId body, float factor);

        /// <summary>Lock or free turning about each of the body's own (local) axes: a locked axis gets an infinite
        /// inertia, so no torque, impulse or contact turns the body about it, and locking stops the body's turning.
        /// Kept across later mass and density changes; a new body starts with none.</summary>
        void SetBodyRotationLocks(BodyId body, bool lockX, bool lockY, bool lockZ);

        void ApplyForce(BodyId body, Vector3 force);
        void ApplyTorque(BodyId body, Vector3 torque);
        void ApplyImpulse(BodyId body, Vector3 impulse);
        void ApplyImpulseAtPoint(BodyId body, Vector3 impulse, Vector3 worldPoint);
        void ApplyAngularImpulse(BodyId body, Vector3 angularImpulse);

        /// <summary>
        /// Archimedes impulse for one step. NOT IMPLEMENTED: the call changes nothing (a non-finite argument is still
        /// counted and dropped). The vehicle buoyancy model applies its own lift, and Jolt's buoyancy impulse needs a
        /// step length and a fluid velocity that these arguments do not carry.
        /// </summary>
        void ApplyBuoyancy(
            BodyId body, float waterHeight, float buoyancy, float linearDrag, float angularDrag);

        void ActivateBody(BodyId body);
        void DeactivateBody(BodyId body);

        /// <summary>Allow/forbid the engine to sleep this body. Vehicles disable sleeping while
        /// active (Bullet's DISABLE_DEACTIVATION) - their controller must run every frame even when
        /// the body is momentarily at rest. No-op on static bodies.</summary>
        void SetBodyAllowSleeping(BodyId body, bool allow);

        /// <summary>Continuous collision detection for a moving body (see <see cref="ContinuousCollision"/>). With it, a
        /// body that moves further in a step than a share of its own size is cast along its motion, so it stops at what
        /// it would hit inside the step instead of ending the step overlapping it. No-op on static bodies.</summary>
        void SetBodyContinuousCollision(BodyId body, ContinuousCollision mode);

        /// <summary>Toggle the Persist (ongoing-contact) gate for a live body - a prim's collision-script
        /// subscription flips this so the script `collision` event streams while touching.</summary>
        void SetBodyWantsContactEvents(BodyId body, bool wants);

        /// <summary>Whether the solver had this body touching another body or the terrain in the last step,
        /// whether or not anyone listens for its contact events. False for a sensor's or an avatar marker's touch.</summary>
        bool BodyHadContact(BodyId body);

        /// <summary>Whether the engine had this body awake at the end of the last step. False for a sleeping body, a body
        /// created asleep that has not yet been woken, and a static body. The engine reports no contacts between two bodies
        /// of which neither is awake, so a contact between them that was there when they fell asleep is still there.</summary>
        bool IsBodyAwake(BodyId body);

        /// <summary>The UserData the body was created with (its owner's LocalID); false for a body that is gone.</summary>
        bool TryGetBodyUserData(BodyId body, out uint userData);

        bool TryGetBodyState(BodyId body, out BodyState state);

        // -- characters ---------------------------------------------------

        /// <summary>Makes an avatar controller. On a backend that has been disposed it makes nothing and returns <see cref="CharacterId.Invalid"/>.</summary>
        CharacterId CreateCharacter(in CharacterDesc desc);
        void RemoveCharacter(CharacterId character);

        void SetCharacterTransform(CharacterId character, Vector3 position, Quaternion orientation);
        void SetCharacterShape(CharacterId character, float capsuleHalfHeight, float capsuleRadius);

        /// <summary>
        /// Sets the mass a loose object meets the avatar with, and that an avatar's push is divided by (a push is a change
        /// of momentum, so the speed it gives is the impulse over this mass). A mass that is not finite and above 0 is ignored.
        /// </summary>
        void SetCharacterMass(CharacterId character, float mass);

        /// <summary>
        /// Atomically (under the character gate, so it cannot race the per-step CharacterVirtual update)
        /// move a character to <paramref name="position"/> AND clear its linear velocity. Used to un-bury a
        /// standing/flying avatar after a live terrain raise swaps the heightfield body out from under it:
        /// the snap lifts it onto the new surface; zeroing velocity stops the accumulated fall speed from
        /// lurching it back down / re-penetrating on the next step.
        /// </summary>
        void ReGroundCharacter(CharacterId character, Vector3 position);

        /// <summary>Toggle the Persist (ongoing-contact) gate for a live character. The simulator subscribes an
        /// avatar to collision events after it is created, so the gate is set here rather than only at creation:
        /// with it, a standing avatar's floor contact reports every step.</summary>
        void SetCharacterWantsContactEvents(CharacterId character, bool wants);

        /// <summary>
        /// Desired horizontal velocity plus explicit vertical control. Called once
        /// per step from the movement layer. The backend resolves stepping, slopes
        /// and moving platforms; it does not know about avatar animation state.
        /// </summary>
        /// <summary>
        /// A push on an avatar (llPushObject, or an attachment's impulse on its wearer): a change of velocity, applied at
        /// the avatar's next step. It is limited by the avatar's push allowance (<see cref="PhysicsBackendSettings.AvatarPushMaxSpeed"/>,
        /// refilled at <see cref="PhysicsBackendSettings.AvatarPushRecovery"/>), and pushes never raise the avatar's speed
        /// above AvatarPushMaxSpeed, or above the speed it already had.
        /// </summary>
        void AddCharacterImpulse(CharacterId character, Vector3 velocityChange);

        /// <summary>
        /// The horizontal velocity an avatar arrived with from another region (or a teleport that keeps it): momentum, not a walk
        /// command. The avatar keeps it while it is off the ground; on the ground it gives way to the velocity the avatar's
        /// controls ask for, over a tenth of a second, so an avatar holding a key goes on
        /// at its walking speed and one holding none comes to rest. The vertical part is the controller's own.
        /// </summary>
        void SetCharacterCarry(CharacterId character, Vector3 velocity);

        void SetCharacterMovement(
            CharacterId character, Vector3 desiredVelocity, bool jump, bool flying);

        /// <summary>
        /// An avatar's buoyancy (an attachment's llSetBuoyancy): gravity on it is scaled by 1 - buoyancy, so 1 floats,
        /// 0.5 falls at half gravity and above 1 rises. It does nothing while the avatar flies or hovers. 0 is none.
        /// </summary>
        void SetCharacterBuoyancy(CharacterId character, float buoyancy);

        /// <summary>
        /// An avatar's hover (an attachment's llSetHoverHeight): <paramref name="heightAt"/> gives the height its capsule
        /// centre is held at over a point (x, y) of the region, and the avatar is brought there by a critically damped
        /// spring of timescale <paramref name="tau"/>, gravity off, while it walks or flies across. Called on the step
        /// thread. Null ends the hover, and the avatar falls from where it was held.
        /// </summary>
        void SetCharacterHover(CharacterId character, Func<float, float, float>? heightAt, float tau);

        /// <summary>
        /// An avatar's constant force (an attachment's llSetForce on its wearer), as the acceleration it gives the avatar:
        /// force / mass, in region axes (m/s2). It acts on every step until the next call, on top of walking, flying,
        /// gravity and pushes: up and down while walking or in the air it adds to gravity; across, and in all three axes
        /// while flying, it adds a velocity kept from step to step, which what blocks the avatar takes and which fades as a
        /// push's speed does on walkable ground and while flying. While hovering it moves the height hover settles at by
        /// acceleration x tau^2. The avatar is never taken past <see cref="PhysicsBackendSettings.MaxBodyLinearSpeed"/>
        /// by it. Zero ends it; the velocity it gave stays, and fades or is kept as above.
        /// </summary>
        void SetCharacterConstantForce(CharacterId character, Vector3 acceleration);

        bool TryGetCharacterState(CharacterId character, out CharacterState state);

        // -- world --------------------------------------------------------

        void SetGravity(Vector3 gravity);
        void SetTerrain(ShapeId heightFieldShape, Vector3 position);
        void SetWaterHeight(float height);

        // -- queries ------------------------------------------------------
        // Must be safe to call concurrently with Step.

        bool RayCast(
            Vector3 origin, Vector3 direction, float maxDistance,
            QueryFilter filter, out RayHit hit);

        int RayCastAll(
            Vector3 origin, Vector3 direction, float maxDistance,
            QueryFilter filter, Span<RayHit> hits);

        /// <summary>
        /// A ray cast from outside the physics step (a script's, or the simulator's own): the closest
        /// <c>hits.Length</c> hits in distance order, as <see cref="RayCastAll"/> gives them, at a bounded cost. The
        /// engine stops looking past the furthest hit kept once the buffer is full. The cast is charged to
        /// <paramref name="budget"/>: it is refused when this region has spent that budget's time for the heartbeat
        /// (<see cref="PhysicsBackendSettings.RayCastBudgetMs"/> or
        /// <see cref="PhysicsBackendSettings.RayCastSimulatorBudgetMs"/>), and cut short when it runs past what is left
        /// of that time or the engine reports more than <see cref="PhysicsBackendSettings.RayCastMaxTestedHits"/> hits;
        /// both return 0 hits and say so in <paramref name="status"/>.
        /// </summary>
        int RayCastLimited(
            Vector3 origin, Vector3 direction, float maxDistance,
            QueryFilter filter, RayCastBudget budget, Span<RayHit> hits, out RayCastStatus status);

        /// <summary>A new heartbeat: this region's ray cast time, in both budgets, starts again from zero.</summary>
        void BeginRayCastBudget();

        /// <summary>Casts refused so far, cumulative, by the budget they were charged to. Cheap; safe from any
        /// thread.</summary>
        long RayCastsRefused(RayCastBudget budget);

        int OverlapSphere(Vector3 center, float radius, QueryFilter filter, Span<BodyId> results);

        int OverlapBox(
            Vector3 center, Vector3 halfExtents, Quaternion orientation,
            QueryFilter filter, Span<BodyId> results);

        bool ShapeCast(
            ShapeId shape, Vector3 origin, Quaternion orientation, Vector3 direction,
            float maxDistance, QueryFilter filter, out RayHit hit);

        /// <summary>
        /// Whether the body, where it is now, sinks deeper than <paramref name="tolerance"/> into another static or dynamic
        /// body (terrain, avatars and sensors are not counted). A body that only rests against another does not.
        /// </summary>
        bool BodyPenetratesOthers(BodyId body, float tolerance);

        // -- health -------------------------------------------------------

        /// <summary>Cumulative capacity / input-rejection counters. Cheap; safe from any thread.</summary>
        PhysicsCapacityStats GetCapacityStats();

        // -- step ---------------------------------------------------------

        /// <summary>
        /// Advance the simulation and drain results into caller-owned buffers.
        /// One call per frame. Buffers should be allocated once at region start
        /// and reused; nothing here allocates.
        /// </summary>
        StepResult Step(
            float deltaTime,
            Span<BodyState> bodyUpdates,
            Span<CharacterState> characterUpdates,
            Span<ContactReport> contacts);
    }

    /// <summary>How the materials of two touching surfaces combine into the one value a contact uses.</summary>
    public enum MaterialCombine
    {
        /// <summary>(a + b) / 2. PhysX's default combine mode, which Halcyon's tree never changes.</summary>
        Average = 0,
        /// <summary>sqrt(a x b). Jolt's own rule for friction.</summary>
        GeometricMean,
        /// <summary>a x b. The ubODE rule for restitution.</summary>
        Multiply,
        Min,
        /// <summary>Jolt's own rule for restitution.</summary>
        Max,
    }

    public struct PhysicsBackendSettings
    {
        public Vector3 Gravity;

        /// <summary>How two surfaces' friction combines in a contact. <see cref="MaterialCombine.Average"/> in an unset struct.</summary>
        public MaterialCombine FrictionCombine;

        /// <summary>How two surfaces' restitution combines in a contact. <see cref="MaterialCombine.Average"/> in an unset struct.</summary>
        public MaterialCombine RestitutionCombine;

        /// <summary>The terrain body's friction and restitution (<see cref="DefaultTerrainFriction"/> and
        /// <see cref="DefaultTerrainRestitution"/> in <see cref="Default"/>; 0 in an unset struct).</summary>
        public float TerrainFriction;
        public float TerrainRestitution;
        public const float DefaultTerrainFriction = 0.4f;
        public const float DefaultTerrainRestitution = 0.05f;

        /// <summary>Upper bound on bodies. Jolt preallocates; size for worst case.</summary>
        public int MaxBodies;
        public int MaxBodyPairs;
        public int MaxContactConstraints;

        /// <summary>Worker threads, all job pools together. 0 = automatic: each pool's share of
        /// Environment.ProcessorCount - 1, at most 4 per pool (JoltPhysicsBackend.ResolveThreadCount).</summary>
        public int ThreadCount;

        /// <summary>Solver position iterations. Higher = stiffer joints, slower.</summary>
        public int PositionIterations;
        public int VelocityIterations;

        /// <summary>Physics substeps per Step() call.</summary>
        public int CollisionSteps;

        /// <summary>
        /// Force single-threaded, bit-reproducible stepping. Costs throughput.
        /// Worth having as a config flag for reproducing user-reported bugs and
        /// for content-parity regression runs.
        /// </summary>
        public bool DeterministicMode;

        /// <summary>
        /// How many Jolt job pools the process runs. Each pool runs ONE region's physics update at a time; regions
        /// are spread across the pools and ThreadCount is split between them. Process-wide; the first region's
        /// value wins. 0 (an unset struct) means 1; otherwise clamped to [1, 64]. A loaded native that is not safe for
        /// more than one pool (<see cref="JoltNativeInfo.SafeForMultiplePools"/>) gets one pool whatever is asked.
        /// </summary>
        public int JobPools;

        /// <summary>
        /// How a job pool is handed over when regions wait for it. false (the default): the pool's lock is a Monitor,
        /// which does not queue its waiters, so the region holding the pool usually takes it straight back for its next
        /// physics step and a waiting region can sit through the rest of that region's heartbeat. true: the pool is
        /// granted first come, first served, so a waiting region gets it when the holder's current physics step ends.
        /// Process-wide, like <see cref="JobPools"/>; the first region's value wins.
        /// </summary>
        public bool JobPoolFairHandoff;

        /// <summary>The region's name, used only to say which region held a job pool while another waited for it.</summary>
        public string? RegionName;

        /// <summary>
        /// The engine's speed caps on every moving body (m/s and rad/s): Jolt clamps a body's velocity to them in every
        /// step. 0 (an unset struct) leaves Jolt's own defaults, which are <see cref="JoltMaxLinearSpeed"/> and
        /// <see cref="JoltMaxAngularSpeed"/> (BodyCreationSettings mMaxLinearVelocity, mMaxAngularVelocity).
        /// </summary>
        public float MaxBodyLinearSpeed;
        public float MaxBodyAngularSpeed;

        /// <summary>
        /// Jolt's enhanced internal edge removal (BodyCreationSettings mEnhancedInternalEdgeRemoval; CharacterVirtualSettings
        /// mEnhancedInternalEdgeRemoval): extra work to drop the contacts a body makes with the shared edges inside a
        /// triangle mesh or a heightfield, which push it off its path. For bodies that move under physics, and for avatars.
        /// Off in an unset struct, which is the engine's own default.
        /// </summary>
        public bool BodyInternalEdgeRemoval;
        public bool AvatarInternalEdgeRemoval;

        /// <summary>
        /// Cook every triangle mesh shape (CreateMeshShape) with a reversed copy of each triangle, so a moving body meets
        /// it from either side. A simulated contact with a mesh ignores its back faces, and the stock native library has no
        /// setting that changes that. Doubles the mesh's triangles. Off in an unset struct, which is one-sided.
        /// </summary>
        public bool TwoSidedMeshes;

        /// <summary>
        /// How far apart two shapes can be, in metres, and still make a contact that stops them closing (Jolt's
        /// PhysicsSettings mSpeculativeContactDistance). Null (an unset struct) leaves the engine's own value, 0.02. A body that
        /// rests a few millimetres into a surface sees the end face of the next prim along as a wall within this distance, which
        /// is how a box sliding across two abutted prims can be stopped and tipped at the seam; 0 turns the speculative contacts
        /// off, at the cost of contacts that begin only on overlap.
        /// </summary>
        public float? SpeculativeContactDistance;

        /// <summary>
        /// The speed in metres per second under which a body counts as still for sleeping (Jolt's PhysicsSettings
        /// mPointVelocitySleepThreshold). Null (an unset struct) leaves the engine's own value, 0.03.
        /// </summary>
        public float? SleepVelocityThreshold;

        public const float JoltMaxLinearSpeed = 500f;
        public const float JoltMaxAngularSpeed = 0.25f * MathF.PI * 60f;

        /// <summary>
        /// Load a joltc whose SHA-256 is not one the module has a record of (see <see cref="JoltNative.Known"/>). Off by
        /// default: an unrecorded native is refused. Process-wide, checked when the first region loads the native.
        /// </summary>
        public bool AllowUnrecordedNative;

        /// <summary>
        /// Script ray casts (<see cref="IPhysicsBackend.RayCastLimited"/> with <see cref="RayCastBudget.Script"/>): the
        /// time one region may spend on them in one heartbeat, in milliseconds. A cast holds the region's physics lock,
        /// and the region's physics step waits for it while holding its job pool, so this also bounds how long ray casts
        /// can delay the other regions on that pool. 0 (an unset struct) means <see cref="DefaultRayCastBudgetMs"/>.
        /// </summary>
        public float RayCastBudgetMs;
        public const float DefaultRayCastBudgetMs = 5f;

        /// <summary>
        /// The other ray casts (<see cref="RayCastBudget.Simulator"/>): the simulator's own, such as rez placement and
        /// landing after login or teleport, and script casts that arrive without anything saying they are a script's.
        /// The time one region may spend on them in one heartbeat, in milliseconds, apart from
        /// <see cref="RayCastBudgetMs"/>; the two together bound how long ray casts can delay a heartbeat.
        /// 0 (an unset struct) means <see cref="DefaultRayCastSimulatorBudgetMs"/>.
        /// </summary>
        public float RayCastSimulatorBudgetMs;
        public const float DefaultRayCastSimulatorBudgetMs = 5f;

        /// <summary>
        /// The hits the engine may report to one script ray cast before it is cut short: each is a shape the ray
        /// crossed nearer than the furthest hit kept so far (a mesh reports each triangle). 0 (an unset struct) means
        /// <see cref="DefaultRayCastMaxTestedHits"/>.
        /// </summary>
        public int RayCastMaxTestedHits;
        public const int DefaultRayCastMaxTestedHits = 1024;

        /// <summary>
        /// The clock the ray cast budgets are measured with: what a cast is charged, and when a cast in flight runs out
        /// of time. Null (an unset struct) means <see cref="TimeProvider.System"/>, the wall clock. A test passes its
        /// own, so what the budgets admit, refuse and charge can be checked without the machine's timing.
        /// </summary>
        public TimeProvider? RayCastClock;

        /// <summary>
        /// Pushes on avatars (<see cref="IPhysicsBackend.AddCharacterImpulse"/>): the most speed, in m/s, pushes can
        /// give an avatar. It is also the avatar's push allowance: each push spends the speed it adds, and the
        /// allowance refills at <see cref="AvatarPushRecovery"/> m/s every second. 0 (an unset struct): pushes do not
        /// move avatars.
        /// </summary>
        public float AvatarPushMaxSpeed;
        public const float DefaultAvatarPushMaxSpeed = 100f;

        /// <summary>
        /// How fast an avatar's push allowance refills, in m/s per second. Below gravity (9.8), repeated pushes cannot
        /// hold an avatar in the air.
        /// </summary>
        public float AvatarPushRecovery;
        public const float DefaultAvatarPushRecovery = 5f;

        /// <summary>
        /// What an avatar walking or running into a loose object pushes it with, as a force in newtons per unit of the
        /// avatar's momentum into the object (its mass times its speed): force = mass x speed x this, so a heavier or faster
        /// avatar pushes harder, and running pushes harder than walking. Halcyon's rule (PhysxCharacter.KickOrRideOnPrim:
        /// impulse = mass x distance moved x 100 each step, which is a force of mass x speed x 100). The force is held to
        /// <see cref="AvatarObjectPushMaxForce"/>.
        /// </summary>
        public float AvatarObjectPushPerMomentum;
        public const float DefaultAvatarObjectPushPerMomentum = 100f;

        /// <summary>The most force, in newtons, an avatar pushes a loose object with (before the character's PushStrength).</summary>
        public float AvatarObjectPushMaxForce;
        public const float DefaultAvatarObjectPushMaxForce = 2000f;

        public static PhysicsBackendSettings Default => new PhysicsBackendSettings
        {
            Gravity = new Vector3(0f, 0f, -9.80665f),
            TerrainFriction = DefaultTerrainFriction,
            TerrainRestitution = DefaultTerrainRestitution,
            MaxBodies = 65536,
            MaxBodyPairs = 65536,
            MaxContactConstraints = 16384,
            ThreadCount = 0,
            PositionIterations = 2,
            VelocityIterations = 10,
            CollisionSteps = 1,
            DeterministicMode = false,
            JobPools = 1,
            MaxBodyLinearSpeed = JoltMaxLinearSpeed,
            MaxBodyAngularSpeed = JoltMaxAngularSpeed,
            RayCastBudgetMs = DefaultRayCastBudgetMs,
            RayCastSimulatorBudgetMs = DefaultRayCastSimulatorBudgetMs,
            RayCastMaxTestedHits = DefaultRayCastMaxTestedHits,
            AvatarPushMaxSpeed = DefaultAvatarPushMaxSpeed,
            AvatarPushRecovery = DefaultAvatarPushRecovery,
            AvatarObjectPushPerMomentum = DefaultAvatarObjectPushPerMomentum,
            AvatarObjectPushMaxForce = DefaultAvatarObjectPushMaxForce,
        };
    }
}
