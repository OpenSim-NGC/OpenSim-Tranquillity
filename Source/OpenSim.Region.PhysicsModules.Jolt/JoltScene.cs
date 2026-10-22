/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Jolt physics as an OpenSim region module (PhysicsScene).
//
// ============================ READ THIS FIRST ============================
// This is the seam between OpenSim's PhysicsScene contract and the engine-agnostic
// IPhysicsBackend (implemented for Jolt in JoltPhysicsBackend, which has its own standalone tests).
// The module registers, boots under `physics = Jolt`, and drives real physics:
//   - AddPrimShape cooks a shape (fixed-shape fast path, IMesher mesh/hull, or bbox fallback) and
//     creates a JoltPrim - STATIC when non-physical, dynamic when physical - tracked in _prims.
//   - AddAvatar has three overloads (no-localID, localID, feetOffset); the avatar gets a Jolt
//     CharacterVirtual carrying its kinematic query marker.
//   - SetTerrain cooks the real (N+1)-square heightfield and swaps it in; SetWaterLevel
//     pushes the water height to the backend.
//   - Simulate rebuilds dirty linkset compounds, activates bodies created inert, runs the
//     vehicle controllers, steps the backend once per frame, then drains.
// The batched-buffer drain (StepResult -> per-actor RequestPhysicsterseUpdate / collision dispatch)
// IS here, at the tail of Simulate.
//
// The host loads JoltModule (JoltModule.cs; registered in PluginRegistration.cs), which makes this scene only when
// [Startup] physics == Name. No [Startup] edit - the operator picks `physics = Jolt`. Initialise checks the name
// again, so a JoltScene made directly (the tests and the harness) also stays inert under another engine.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using Nini.Config;
using Microsoft.Extensions.Logging;
using OpenMetaverse;

using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
// The backend speaks System.Numerics.Vector3; OpenSim speaks OpenMetaverse.Vector3 (the unqualified
// Vector3 here). Alias the numerics one so backend calls are unambiguous.
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    public sealed partial class JoltScene : PhysicsScene, IJoltRegion
    {
        internal static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        internal const string LogHeader = "[JOLT SCENE]";

        // Gate for JoltCharacter's [charjump] path trace; toggled by `jolt charframe` and kept in sync with
        // the [charframe] window by Simulate. Static so the per-avatar actor can read it without a back-ref.
        internal static bool CharJumpTrace;

        private bool m_Enabled = false;
        private IConfigSource m_Config;

        // [Jolt] TestCommands: load the diagnostics assembly with the console test commands (the
        // OpenSim.Region.PhysicsModules.Jolt.Diagnostics project). Off by default.
        private bool m_testCommands;

        // The diagnostics assembly's object for this region; null unless TestCommands is true and the assembly loaded.
        private IJoltDiagnostics _diagnostics;
        internal IJoltDiagnostics Diagnostics => _diagnostics;

        // Where the diagnostics assembly is looked for: the folder of this assembly. A seam for tests.
        internal static Func<string> DiagnosticsFolder = JoltDiagnosticsLoader.ModuleFolder;

        /// <summary>True when [Jolt] TestCommands is set; false (the default) leaves the test commands out.</summary>
        internal static bool TestCommandsEnabled(IConfigSource source)
            => source?.Configs["Jolt"]?.GetBoolean("TestCommands", false) ?? false;

        // The [Jolt] section, parsed once in Initialise. The defaults reproduce the earlier hardcoded constants.
        private JoltConfig _joltConfig = new JoltConfig();
        internal float AvatarJumpSpeed => _joltConfig.AvatarJumpSpeed;
        internal float AvatarWalkSpeedFactor => _joltConfig.AvatarWalkSpeedFactor;
        internal float AvatarRunSpeedFactor => _joltConfig.AvatarRunSpeedFactor;
        internal float AvatarFlySpeedFactor => _joltConfig.AvatarFlySpeedFactor;
        internal float AvatarFlyDownSpeedFactor => _joltConfig.AvatarFlyDownSpeedFactor;
        internal float VehicleGroundGravityFactor => _joltConfig.VehicleGroundGravityFactor;
        internal float BodyMaxLinearSpeed => _joltConfig.BodyMaxLinearSpeed;
        internal float BodyMaxAngularSpeed => _joltConfig.BodyMaxAngularSpeed;
        internal JoltConfig.BasicForces BasicForcesBehaviour => _joltConfig.BasicForcesBehaviour;
        internal float PrimLinearDamping => _joltConfig.PrimLinearDamping;
        internal float PrimAngularDamping => _joltConfig.PrimAngularDamping;
        internal bool HonourConvexMesh => _joltConfig.HonourConvexMesh;
        internal int MaxPhysicalComplexity => _joltConfig.MaxPhysicalComplexity;
        internal bool KeyframedMotionCarriesRiders => _joltConfig.KeyframedMotionCarriesRiders;
        internal bool MovedPrimsCarryRiders => _joltConfig.MovedPrimsCarryRiders;

        // The vehicle settings every vehicle controller in this region shares, built from [Jolt] in Initialise.
        internal VehicleSettings VehicleSettings { get; private set; } = VehicleSettings.Default;

        // The job pool is process-wide; log its size once, whichever region creates it.
        private static readonly object s_poolLogGate = new object();
        private static bool s_poolLogged;

        // The engine-agnostic backend.
        private IPhysicsBackend _backend;

        // The region's IMesher, driving the cook path for cut/hollow/sculpt/mesh prims.
        private IMesher m_mesher;

        // The region's wind module, which vehicles with REACT_TO_WIND or REACT_TO_CURRENTS ask for the wind at their position
        // under VehicleBehaviour = InWorldz. Null when the region has none.
        private IWindModule m_wind;

        /// <summary>Gives the scene a wind module without a region (the tests and the harness; a region's is found when it loads).</summary>
        internal void UseWindModule(IWindModule module) => m_wind = module;

        /// <summary>
        /// The wind at a region position (the water's current below the water, the surface wind above it, the wind aloft over
        /// that), from the region's wind module. False when the region has no wind module.
        /// </summary>
        internal bool TryGetWind(Vector3 pos, out Vector3 wind) => SampleWind(m_wind, pos, out wind);

        /// <summary>The region's wind module (null when it has none), for a step provider to capture before the Update.</summary>
        internal IWindModule WindModule => m_wind;

        /// <summary>
        /// The wind at a region position from <paramref name="module"/>, which must answer without taking a lock (it is read
        /// inside the engine's collision steps). Allocation free. False when there is no module or the position is not finite.
        /// </summary>
        internal static bool SampleWind(IWindModule module, Vector3 pos, out Vector3 wind)
        {
            wind = Vector3.Zero;
            if (module == null || !float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
                return false;
            const float Limit = 1e6f;
            wind = module.WindSpeed((int)MathF.Floor(Math.Clamp(pos.X, -Limit, Limit)), (int)MathF.Floor(Math.Clamp(pos.Y, -Limit, Limit)),
                (int)MathF.Floor(Math.Clamp(pos.Z, -Limit, Limit)));
            return true;
        }

        public string RegionName { get; private set; }

        // Terrain: the current cooked heightfield ShapeId (released + replaced on each SetTerrain),
        // and the region dimensions needed to interpret the flat float[] heightmap OpenSim hands us.
        private ShapeId _terrainShape = ShapeId.Invalid;
        private int _regionSizeX;
        private int _regionSizeY;
        internal int RegionSizeX => _regionSizeX;
        internal int RegionSizeY => _regionSizeY;
        private Scene _scene;
        /// <summary>The region's scene (the diagnostics assembly's test commands rez and delete through it).</summary>
        internal Scene RegionScene => _scene;

        // Vehicle-controller world inputs: the region water plane, the last cooked terrain
        // sample field (for height-at-XY without a per-frame raycast), the world gravity handed to
        // the backend, and the last Simulate dt (BulletSim's LastTimeStep, used by AddForce).
        internal float WaterLevel { get; private set; }
        /// <summary>The solver's sub-steps in each backend step (PhysicsBackendSettings.CollisionSteps).</summary>
        internal int CollisionSteps { get; private set; } = 1;
        internal SVector3 DefaultGravity { get; private set; } = new SVector3(0f, 0f, -9.80665f);
        internal float LastTimeStep = 0.0909f;
        // Simulated seconds: the sum of every heartbeat's time step. The collision repeat rate is measured on it.
        internal double SimTime;
        // The heartbeat timer's clock, in Stopwatch ticks. Null reads Stopwatch.GetTimestamp(); the tests set their own.
        internal Func<long> HeartbeatClock;
        // The clock new vehicle controllers read (motor reset and spike checks). Null in a region, where
        // they read the wall clock; the test harness sets a simulated one so it can step faster than real time.
        internal Func<DateTime> VehicleClock;

        // [Jolt] PhysicsStepRate: null = one backend step per heartbeat (PhysicsStepRate = 0). Otherwise each heartbeat runs
        // the steps this accumulator hands out, each 1 / rate seconds long. Set once, in InitialiseRegion.
        private SubstepAccumulator _substeps;
        internal bool Substepping => _substeps != null;
        internal SubstepAccumulator Substeps => _substeps;

        // A push force (AddForce with pushforce) acts for one backend step; this keeps its impulse at force x the
        // heartbeat time when a step is shorter than the heartbeat. 1 when not substepping.
        internal float PushForceScale = 1f;

        // Physics time while substepping: advanced by exactly one step per backend step, so a vehicle controller
        // sees the true interval between its steps. The wall clock does not move between the steps of one
        // heartbeat, and the controller divides by that interval (spike checks). 0 until the first step.
        private long _physicsClockTicks;
        internal DateTime PhysicsClock()
        {
            long t = Interlocked.Read(ref _physicsClockTicks);
            return t != 0 ? new DateTime(t) : (VehicleClock?.Invoke() ?? DateTime.Now);
        }

        // The clock a new vehicle controller gets: physics time while substepping, otherwise VehicleClock (null in a
        // region: the controller keeps its wall clock).
        internal Func<DateTime> ControllerClock => _substeps != null ? PhysicsClock : VehicleClock;
        private float[] _terrainField;   // the (N+1)-square field SetTerrain cooked (row = y * _terrainFieldM)
        private int _terrainFieldM;

        // Bilinear terrain height at region XY, from the same samples the collision heightfield was
        // cooked from (1 m spacing, origin at the region corner). Clamps outside the field.
        internal float TerrainHeightAt(float x, float y)
        {
            float[] f = _terrainField;
            int m = _terrainFieldM;
            if (f == null || m < 2)
                return 0f;
            x = Math.Clamp(x, 0f, m - 1.001f);
            y = Math.Clamp(y, 0f, m - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            float h00 = f[y0 * m + x0], h10 = f[y0 * m + x0 + 1];
            float h01 = f[(y0 + 1) * m + x0], h11 = f[(y0 + 1) * m + x0 + 1];
            return h00 * (1 - fx) * (1 - fy) + h10 * fx * (1 - fy) + h01 * (1 - fx) * fy + h11 * fx * fy;
        }

        // Live prims by SceneObjectPart.LocalId. RemovePrim looks up here; also the
        // Step-drain target for physical actors. Guarded because Add/RemovePrim can arrive off
        // the heartbeat thread (the backend permits concurrent Create/Remove with Step).
        private readonly Dictionary<uint, JoltPrim> _prims = new Dictionary<uint, JoltPrim>();
        /// <summary>The region's prims by local id; hold the dictionary's own lock to read it.</summary>
        internal Dictionary<uint, JoltPrim> Prims => _prims;

        // True only before the first Simulate (the region-reload window). Used by JoltPrim to drop a restored
        // physical prim's horizontal velocity on load (so a reloaded body doesn't inherit a stale coast).
        internal bool IsRegionLoading => _stepCount == 0;

        // Mirrors BulletSim's taint-deferred body creation: physical bodies are created INERT and
        // their activation is deferred to the top of the next Simulate (step thread), so a body is never
        // stepped by the engine before all its load-time state (incl. the vehicle's gravity-cancellation) is
        // applied - it cannot free-fall during the load or the reload stall. Drained in Simulate.
        private readonly List<JoltPrim> _pendingActivation = new List<JoltPrim>();
        internal void RegisterPendingActivation(JoltPrim p)
        {
            lock (_pendingActivation)
                if (!_pendingActivation.Contains(p)) _pendingActivation.Add(p);
        }

        // The prims whose body is kinematic because keyframed motion is moving them. Core sets their position about every
        // 50 ms, on its own timer; the prim keeps only the last pose it was given, and once per heartbeat, before the step,
        // each of them is moved to it over the time that step covers (JoltPrim.StepKinematic), so the body carries the
        // velocity of the motion and what stands on it is carried.
        private readonly HashSet<JoltPrim> _kinematicPrims = new HashSet<JoltPrim>();
        private JoltPrim[] _kinematicSnapshot = Array.Empty<JoltPrim>();

        // Simulated seconds the region has stepped (written on the step thread, read by whatever thread sets a prim's pose)
        // and the time the last heartbeat covered. A set of a pose is stamped with this clock, so a series of sets is judged
        // in simulated time, in whole heartbeats (see JoltPrim.NoteMoveSet).
        private double _kinematicClock;
        private volatile float _kinematicHeartbeat = 1f / 11f;
        internal double KinematicClock => Volatile.Read(ref _kinematicClock);
        internal float KinematicHeartbeat => _kinematicHeartbeat;

        internal void SetKinematicTracked(JoltPrim p, bool tracked)
        {
            lock (_kinematicPrims)
            {
                if (tracked ? _kinematicPrims.Add(p) : _kinematicPrims.Remove(p))
                    _kinematicSnapshot = _kinematicPrims.Count == 0 ? Array.Empty<JoltPrim>() : new List<JoltPrim>(_kinematicPrims).ToArray();
            }
        }

        // Moves every kinematic prim to the pose it was last given, over `seconds` of simulation: the time the coming
        // step (or burst of steps) covers. Runs on the step thread, first thing in the heartbeat.
        private void StepKinematics(float seconds)
        {
            if (seconds > 0f)
            {
                Volatile.Write(ref _kinematicClock, _kinematicClock + seconds);
                _kinematicHeartbeat = seconds;
            }
            JoltPrim[] prims = _kinematicSnapshot;
            for (int i = 0; i < prims.Length; i++)
            {
                try { prims[i].StepKinematic(seconds); }
                catch (Exception e) { m_log.LogError($"{LogHeader} kinematic move EXCEPTION for prim {prims[i].LocalID}: {e}"); }
            }
        }

        // The logged-in avatars, keyed by their CharacterId handle (the value the character drain
        // echoes back). Keyed by handle rather than LocalID so the drain mapping is independent of when
        // ScenePresence assigns LocalID after AddAvatar returns.
        private readonly Dictionary<uint, JoltCharacter> _avatars = new Dictionary<uint, JoltCharacter>();

        // Collision-mesh LOD (matches BulletSim's BSParam.MeshLOD default), and the
        // characterization of the last RAW mesher output cooked (verts/tris/degenerate/duplicate/AABB).
        private const float MeshLod = 32f;
        internal struct MeshStats
        {
            public int Verts, Tris, DegenerateTris, DuplicateVerts, OutOfRangeIndices;
            public SVector3 Min, Max;
            public float Volume;   // enclosed volume of the (closed) mesh; == convex-hull volume for a convex prim
        }
        private MeshStats _lastMeshStats;
        internal MeshStats LastMeshStats => _lastMeshStats;
        internal long StepCount => _stepCount;
        internal int LastActiveBodyCount => _lastActiveBodyCount;

        // Dynamics: per-frame step counter + latest active-body count (the console test commands read these).
        private long _stepCount;
        private int _lastActiveBodyCount;
        private long _charFrameUntil = -1;  // window: log per-frame avatar Z/support/vZ ([charframe] toggle)

        // Caller-owned step buffers (backend contract: nothing allocates per frame). Simulate drains all
        // three every step. They start at these sizes and DOUBLE on overflow, up to the caps below
        // (the backend drain is fair, so an overflowing frame loses nothing, it only delays), and allocate only
        // when they grow. The contact cap defaults to the backend's ring capacity - past that, growing is useless.
        private BodyState[] _bodyBuf = new BodyState[1024];
        private CharacterState[] _charBuf = new CharacterState[256];
        private ContactReport[] _contactBuf = new ContactReport[2048];
        private int _bodyBufMax = 65536;      // [Jolt] BodyUpdateBufferMax
        private int _charBufMax = 1024;       // [Jolt] CharacterUpdateBufferMax
        private int _contactBufMax = 2048;    // [Jolt] ContactBufferMax; 0 there = the ring capacity (set in AddRegion)
        private long _overflowLastWarnTicks;

        // Collision dispatch: per-frame accumulation of colliders per subscribed prim,
        // and the set of prims that reported collisions LAST frame - so a prim that stops touching gets one
        // empty CollisionEventUpdate this frame, which is how OpenSim's SOP.PhysicsCollision fires collision_end.
        private readonly CollisionFrameTracker _collisions = new CollisionFrameTracker();
        // This frame's prims, resolved under ONE lock(_prims) rather than a lock per contact.
        private readonly HashSet<uint> _frameIds = new HashSet<uint>();
        private readonly Dictionary<uint, JoltPrim> _framePrims = new Dictionary<uint, JoltPrim>();
        private readonly Dictionary<uint, JoltCharacter> _frameAvatars = new Dictionary<uint, JoltCharacter>();   // by LocalID

        // Capacity surfacing. Step-thread only. The warning window starts at the last logged
        // snapshot; a failure inside the quiet period accumulates into the next line instead of being lost.
        private long _capacityLogIntervalTicks = System.TimeSpan.FromSeconds(10).Ticks;   // [Jolt] CapacityLogIntervalSeconds
        private PhysicsCapacityStats _capBase;
        private long _capBaseTicks;
        private bool _capBaseSet;
        private long _bodyOverflowFrames, _charFullFrames, _contactOverflowFrames;

        // ---------------------------------------------------------------------
        // The region module calls, made by JoltModule (IJoltRegion). JoltScene is not itself a region module: the
        // host's plugin discovery makes every class that implements one, and it must make only JoltModule.
        // ---------------------------------------------------------------------

        public string Name => "Jolt";

        // The native's path and hash are logged once per process, by the first region's module.
        private static int s_nativeLogged;

        // Finds, checks and loads the native (JoltNative.EnsureLoaded). A test replaces it to stand in for a missing
        // file, an unrecorded hash or a platform with no native, without touching the real files.
        internal static Func<bool, JoltNativeInfo> NativeLoader = JoltNative.EnsureLoaded;

        public System.Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            // Self-selection: only enable when the operator chose us. Mirrors BSScene - we do NOT
            // hard-enable, and we never touch [Startup] ourselves. Under another engine nothing below runs: no [Jolt]
            // key is read, the native is not loaded and nothing is logged.
            IConfig config = source.Configs["Startup"];
            if (config != null)
            {
                string physics = config.GetString("physics", string.Empty);
                if (physics == Name)
                {
                    string mesher = config.GetString("meshing", string.Empty);
                    if (string.IsNullOrEmpty(mesher) || !mesher.Equals("Meshmerizer"))
                    {
                        m_log.LogError($"{LogHeader} [Startup] meshing must be set to \"Meshmerizer\" for the Jolt physics module.");
                        throw new System.Exception("Invalid physics meshing option for Jolt");
                    }

                    m_testCommands = TestCommandsEnabled(source);
                    if (m_testCommands)
                    {
                        // The test commands live in their own assembly, which is not shipped with the region server.
                        _diagnostics = JoltDiagnosticsLoader.Load(DiagnosticsFolder(), m_log);
                        _diagnostics?.Attach(this);
                    }

                    var warnings = new List<string>();
                    JoltConfig joltConfig = JoltConfig.FromConfig(source, warnings);
                    foreach (string w in warnings)
                        m_log.LogWarning($"{LogHeader} {w}");

                    // The native for this platform, checked and loaded before the module enables, so a platform
                    // with no native, a missing file or an unrecorded build stops startup here with one clear line
                    // instead of failing at the first physics call with a region half up.
                    JoltNativeInfo native;
                    try
                    {
                        native = NativeLoader(joltConfig.AllowUnrecordedNative);
                    }
                    catch (JoltNativeException e)
                    {
                        m_log.LogError($"{LogHeader} {e.Message}");
                        throw;
                    }
                    if (System.Threading.Interlocked.Exchange(ref s_nativeLogged, 1) == 0)
                    {
                        if (native.Recorded)
                            m_log.LogInformation($"{LogHeader} {native.Describe()}");
                        else
                            m_log.LogWarning($"{LogHeader} {native.Describe()}");
                        if (native.UntestedNotice != null)
                            m_log.LogWarning($"{LogHeader} {native.UntestedNotice}");
                        // More than one job pool asked for on a native that is not safe for it: the backend runs one.
                        string poolLimit = CapacityReport.JobPoolLimitWarning(joltConfig.JobPools, native);
                        if (poolLimit != null)
                            m_log.LogWarning($"{LogHeader} {poolLimit}");
                    }

                    m_Enabled = true;
                    m_Config = source;
                    _joltConfig = joltConfig;
                    VehicleSettings = joltConfig.ToVehicleSettings();
                    if (VehicleSettings.Behaviour != VehicleBehaviour.SecondLife)
                        m_log.LogInformation($"{LogHeader} vehicle behaviour: {VehicleSettings.Behaviour} (preset values: {VehicleSettings.Presets}).");
                    m_log.LogInformation($"{LogHeader} enabled (physics = {Name}).");
                }
            }
        }

        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            RegionName = scene.RegionInfo.RegionName;
            PhysicsSceneName = Name + "/" + RegionName;

            scene.RegisterModuleInterface<PhysicsScene>(this);

            _scene = scene;
            InitialiseRegion(scene.RegionInfo.RegionSizeX, scene.RegionInfo.RegionSizeY, scene.PhysicsRequestAsset,
                () => scene.Heightmap != null ? scene.Heightmap.GetFloatsSerialised() : new float[scene.RegionInfo.RegionSizeX * scene.RegionInfo.RegionSizeY],
                () => (float)scene.RegionInfo.RegionSettings.WaterHeight, scene.FrameTime);
        }

        /// <summary>
        /// The physics test harness's way in: the same backend setup, terrain and water as
        /// <see cref="AddRegion"/>, for a scene with no OpenSim <see cref="Scene"/>. Call
        /// <see cref="Initialise"/> first with a config that selects this module.
        /// </summary>
        internal void InitialiseWithoutScene(string regionName, uint sizeX, uint sizeY, float[] heightMap, float waterHeight, float heartbeatSeconds)
        {
            if (!m_Enabled)
                throw new InvalidOperationException("Initialise with [Startup] physics = Jolt first.");
            RegionName = regionName;
            PhysicsSceneName = Name + "/" + RegionName;
            InitialiseRegion(sizeX, sizeY, null, () => heightMap, () => waterHeight, heartbeatSeconds);
        }

        // AddRegion's work once the scene is known. The heightmap and water height are read where AddRegion
        // always read them, after the backend exists.
        /// <summary>TEST-ONLY: run on the new backend before it is initialised, so a test can make Initialize fail.</summary>
        internal Action<JoltPhysicsBackend> BackendCreatedForTest;

        private void InitialiseRegion(uint sizeX, uint sizeY, RequestAssetDelegate requestAsset, Func<float[]> heightMap, Func<float> waterHeight, float heartbeatSeconds)
        {
            // Stored BEFORE base.Initialise, because that calls SetTerrain(heightMap) - which needs the
            // region dims to interpret the flat float[] and build the (N+1) field.
            _regionSizeX = (int)sizeX;
            _regionSizeY = (int)sizeY;

            // Every knob comes from [Jolt] (JoltConfig). The defaults are exactly what this used to hardcode:
            // the backend's default settings, MaxBodies = 65536 per 256 m scaled by area, and
            // CollisionSteps = 6 - the RIGID-BODY solver sub-stepped 6x inside _system.Update: at
            // OpenSim's ~11 fps a single integration lets a fast prim move ~1.5 m and tunnel through the terrain
            // heightfield. CollisionSteps slices the SOLVER only, NOT the character step (once per Step, before
            // Update), so dropped prims rest WITHOUT disturbing the avatar's known-good 1-step path.
            // [Jolt] PhysicsStepRate, checked against the heartbeat Simulate is called at ([Startup] FrameTime).
            float stepRate = _joltConfig.EffectivePhysicsStepRate(heartbeatSeconds, out string rateWarning, out string rateNote);
            if (rateWarning != null)
                m_log.LogWarning($"{LogHeader} region '{RegionName}': {rateWarning}");
            if (rateNote != null)
                m_log.LogInformation($"{LogHeader} region '{RegionName}': {rateNote}");
            _substeps = stepRate > 0f ? new SubstepAccumulator(stepRate) : null;

            PhysicsBackendSettings settings = _joltConfig.ToBackendSettings(sizeX, sizeY, _substeps != null ? stepRate : 0f);
            CollisionSteps = Math.Max(1, settings.CollisionSteps);
            settings.RegionName = RegionName;   // names this region when another waits for its job pool (metrics)
            settings.RayCastClock = RayCastClock;
            _bodyBufMax = _joltConfig.BodyUpdateBufferMax;
            _charBufMax = _joltConfig.CharacterUpdateBufferMax;
            _capacityLogIntervalTicks = System.TimeSpan.FromSeconds(_joltConfig.CapacityLogIntervalSeconds).Ticks;

            // Instrumentation: per-region RSS delta across backend init (8MB TempAllocator +
            // MaxBodies preallocation + JobSystemThreadPool), reported by `jolt metrics`.
            long rssBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            var created = new JoltPhysicsBackend();
            BackendCreatedForTest?.Invoke(created);
            try { created.Initialize(settings); }
            catch
            {
                // A backend whose Initialize failed part-way still holds what it had taken (its reference on the shared
                // job pools and Jolt, and the physics system if that was made); the region will not use it, so give
                // those back before the failure goes on.
                created.Dispose();
                throw;
            }
            _backend = created;
            PhysicsCapacityStats initStats = _backend.GetCapacityStats();
            _contactBufMax = _joltConfig.ContactBufferMax > 0 ? _joltConfig.ContactBufferMax : initStats.ContactRingCapacity;
            LogJobPool(initStats);
            DefaultGravity = settings.Gravity;   // the vehicle controller applies this manually
            JoltMetrics.RecordRegionInit(RegionName,
                System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 - rssBefore);

            EngineType = Name;                              // osGetPhysicsEngineType
            EngineName = $"{_backend.Name} {_backend.Version}"; // osGetPhysicsEngineName

            // The base Initialise wires the request-asset delegate and calls our SetTerrain - which
            // cooks the real heightfield - and SetWaterLevel, which pushes the water height down
            // to the backend for vehicle hover.
            base.Initialise(requestAsset, heightMap(), waterHeight());

            m_log.LogInformation($"{LogHeader} region '{RegionName}' {sizeX}x{sizeY}m: backend initialised, MaxBodies={settings.MaxBodies}. {EngineName}");

            RayWarmUpMs = WarmUpRayCasts();
            m_log.LogInformation($"{LogHeader} region '{RegionName}': ray casts warmed up in {RayWarmUpMs:0.00} ms.");
            if (_substeps != null)
                m_log.LogInformation($"{LogHeader} region '{RegionName}': physics steps at {_substeps.RateHz:0.##} Hz " +
                                     $"({settings.CollisionSteps} collision steps each, at most {SubstepAccumulator.MaxStepsPerFrame} per heartbeat).");
            m_log.LogInformation($"{LogHeader} region '{RegionName}': settings: {_joltConfig.StartupSummary(stepRate)}.");
        }

        // The shared job pools are sized once, by the first region's request; every region reads the same
        // [Jolt] ThreadCount and JobPools, so they agree unless the pools predate this config (or a harness sized them).
        private void LogJobPool(in PhysicsCapacityStats pool)
        {
            int poolThreads = pool.JobThreadCount;
            lock (s_poolLogGate)
            {
                if (!s_poolLogged)
                {
                    s_poolLogged = true;
                    m_log.LogInformation($"{LogHeader} shared Jolt job pools: {CapacityReport.JobPoolsStartup(in pool)}");
                }
            }
            // Which pool this region steps on, once per region.
            m_log.LogInformation($"{LogHeader} region '{RegionName}' steps on Jolt job pool {pool.PoolIndex} of {pool.JobPools}.");
            // When the native runs one pool whatever JobPools asks (warned at start), the threads are sized for one pool.
            int requested = pool.JobPoolsLimitedBy == null
                ? _joltConfig.RequestedThreadCount
                : JoltPhysicsBackend.ResolveThreadCount(_joltConfig.ThreadCount, _joltConfig.DeterministicMode, pool.JobPools);
            if (requested != poolThreads)
                m_log.LogWarning($"{LogHeader} region '{RegionName}' asked for {requested} job threads but the shared pools were sized for {poolThreads}; the first region's size wins.");
            // The pool count is process-wide too.
            if (_joltConfig.JobPools != pool.JobPoolsRequested)
                m_log.LogWarning($"{LogHeader} region '{RegionName}' asked for JobPools={_joltConfig.JobPools} but the pools were made for JobPools={pool.JobPoolsRequested} " +
                                 $"({pool.JobPools} in use); the first region's value wins.");
            if (_joltConfig.JobPoolFairHandoff != pool.JobPoolFairHandoff)
                m_log.LogWarning($"{LogHeader} region '{RegionName}' asked for JobPoolFairHandoff={_joltConfig.JobPoolFairHandoff} but the pools already exist with " +
                                 $"{pool.JobPoolFairHandoff}; the first region's value wins.");
            else if (pool.JobPoolFairHandoff)
                m_log.LogInformation($"{LogHeader} region '{RegionName}': Jolt job pools hand over first come, first served ([Jolt] JobPoolFairHandoff).");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            // The console keeps every handler it was given (ICommands has no way to take one back), so end this
            // region's route to the scene: its `jolt` commands then do nothing and the console holds no reference to
            // the scene. The other regions' handlers are theirs and stay.
            _consoleRoute?.Detach();
            _diagnostics?.RegionRemoved(scene);

            // The region is going away: tear its physics down now rather than leave it to whoever closes the scene.
            // Dispose can run again from the scene's own close; it does nothing the second time.
            Dispose();
        }

        public void RegionLoaded(Scene scene)
        {
            // Under another engine the module does nothing for the region: no console command, and no reference to
            // its scene is kept.
            if (!m_Enabled)
                return;

            // `jolt parity`, a test command of the diagnostics assembly: registered only when [Jolt] TestCommands is
            // true and that assembly is loaded, and only in a region on Jolt.
            _diagnostics?.RegisterParity(scene);

            // The IMesher the cook path needs; without one, prims fall back to bounding boxes.
            m_wind = scene.RequestModuleInterface<IWindModule>();

            m_mesher = scene.RequestModuleInterface<IMesher>();
            if (m_mesher == null)
                m_log.LogWarning($"{LogHeader} no IMesher available - shape cooking will need it.");

            scene.PhysicsEnabled = true;

            // Test scaffolding of the diagnostics assembly (gated by [Startup] JoltAutoDropTest, and only when
            // [Jolt] TestCommands is true and that assembly is loaded): a few physical boxes dropped at region load.
            _diagnostics?.AutoDrop(m_Config);

            // The `jolt` console commands (read-only diagnostics such as a straight-down raycast onto the
            // cooked terrain, plus the test commands when the diagnostics assembly is loaded).
            //
            // REGISTERED PER REGION, not once behind a static gate. With a single registration the
            // FIRST region to boot would own the delegate and capture ITS `this` forever, so on a
            // multi-region simulator every `jolt ...` command would run against that one region no matter
            // what the console prompt said (for example `jolt heights 512 512` reporting "no terrain" for a
            // 1024x1024 var region because it was measuring a 256x256 region that booted first).
            // Registering per region + the WrongConsoleScene() check in each handler makes
            // `change region <name>` actually select the target.
            if (MainConsole.Instance != null)
                AttachConsole(MainConsole.Instance.Commands);
        }

        /// <summary>Registers this region's `jolt` console commands on <paramref name="commands"/>.</summary>
        internal void AttachConsole(ICommands commands)
        {
            _consoleRoute ??= new ConsoleRoute(this);
            RegisterConsoleCommands(commands, _consoleRoute.Read);
            _diagnostics?.RegisterCommands(commands);
        }

        // What the console is given to call. The console cannot drop a handler, so what it holds is this small object,
        // and RemoveRegion cuts the scene out of it.
        private ConsoleRoute _consoleRoute;

        private sealed class ConsoleRoute
        {
            private volatile JoltScene _scene;
            public ConsoleRoute(JoltScene scene) { _scene = scene; }
            public void Detach() => _scene = null;
            public void Read(string module, string[] cmd) => _scene?.HandleJoltConsole(module, cmd);
        }

        // The `jolt` subcommands that only read and report. They never change the region, so they are always
        // registered. The test subcommands are in the diagnostics assembly.
        internal static readonly string[] ReadOnlyCommands =
        {
            "capacity", "metrics", "terraintest", "probe", "heights", "avatarstatus", "charframe",
            "sitstatus", "sensortest", "raytest", "reloadcheck", "vehiclestatus",
        };

        internal const string ReadOnlyUsage =
            "jolt capacity | metrics | terraintest | probe <x> <y> | heights <x> <y> | avatarstatus | charframe [secs] | sitstatus | sensortest | raytest | reloadcheck | vehiclestatus";

        /// <summary>
        /// Register `jolt` with its read-only subcommands. A test subcommand, which the diagnostics assembly registers
        /// as its own command, falls through to <paramref name="readOnly"/> when that assembly is not loaded, and the
        /// handler answers with the usage line.
        /// </summary>
        internal static void RegisterConsoleCommands(ICommands commands, CommandDelegate readOnly)
        {
            commands.AddCommand("Physics", false, "jolt", ReadOnlyUsage,
                "Jolt physics diagnostics for the current region: capacity and step metrics, terrain and ray probes, "
                + "avatar, sit, vehicle and reload status. None of them changes the region.",
                readOnly);
        }

        /// <summary>
        /// True when the console is scoped to a DIFFERENT region than this instance, so this handler
        /// should stay quiet. Mirrors the house idiom (ExperienceModule.WrongConsoleScene).
        ///
        /// ConsoleScene == null means the ROOT prompt ("Regions #"), which addresses ALL regions - every
        /// region's handler runs. That is standard OpenSim behaviour and is what makes a root-scoped
        /// command a whole-sim sweep. See the note in HandleJoltConsole about which commands you actually
        /// want to run that way.
        /// </summary>
        internal bool WrongConsoleScene()
            => !(MainConsole.Instance.ConsoleScene is null || MainConsole.Instance.ConsoleScene == _scene);

        // Raycast straight down at XY (from well above the region) and report the hit Z - proves the
        // heightfield's ACTUAL collision surface, not "it booted". `jolt terraintest` sweeps the extent
        // probes (interior + the far edge that the (N+1) field must now cover); `jolt probe x y` is ad hoc.
        private void HandleJoltConsole(string module, string[] cmd)
        {
            // Region scoping. Every region registers this command, so without this check all of them
            // would answer every invocation. `change region <name>` -> only that region's handler proceeds.
            if (WrongConsoleScene())
                return;

            // Read once: a teardown on another thread nulls backend. A torn-down backend answers every query with a
            // miss, so a command typed during a teardown reports misses rather than throwing.
            IPhysicsBackend backend = _backend;
            if (backend == null) { MainConsole.Instance.Output($"{LogHeader} no backend."); return; }

            // At the ROOT prompt every region runs the command, so stamp whose output follows - otherwise
            // three regions' results interleave with no way to tell them apart. When the console is scoped
            // to one region this is redundant, so it is skipped.
            //
            // NOTE: a root-scoped `jolt ...` is a SIM-WIDE sweep. Every subcommand handled here is read-only
            // (heights / reloadcheck / vehiclestatus ...), so that is safe. The world-mutating test commands
            // (rezprims / droptest / clearprims / linktest / sittest ...) live in the diagnostics assembly, are
            // registered only when [Jolt] TestCommands is true and that assembly is loaded, and use the same region
            // scoping - so at the root prompt they too act in EVERY region. Use `change region <name>` before those.
            if (MainConsole.Instance.ConsoleScene is null)
                MainConsole.Instance.Output($"{LogHeader} --- region '{RegionName}' ---");

            if (cmd.Length >= 2 && cmd[1] == "capacity") { JoltCapacity(); return; }   // read-only
            if (cmd.Length >= 2 && cmd[1] == "metrics")   // step-time / RSS instrumentation, and the last interval's timing
            {
                MainConsole.Instance.Output(JoltMetrics.Report());
                MainConsole.Instance.Output(JoltMetrics.LastIntervalLine() ?? "[JOLT METRICS] no interval has finished yet (the log closes one about every 30 s)");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "terraintest")
            {
                int n = _regionSizeX;
                // Interior probes + the FAR METRE (n-0.5): the (N+1) field spans [0,n], so (n-0.5) - which
                // an old N-sample field would MISS (it only reached n-1) - must now HIT. (n) is the exact
                // outer vertex and may graze (float); (n+0.5) is beyond the region and must miss.
                var pts = new (float x, float y)[]
                { (1f, 1f), (n / 2f, n / 2f), (n - 1f, n - 1f), (n - 0.5f, n - 0.5f), (n, n), (n + 0.5f, n + 0.5f) };
                MainConsole.Instance.Output($"{LogHeader} terrain raycast probes (region {n}x{_regionSizeY}; (N+1) field spans [0,{n}] m):");
                foreach (var (px, py) in pts)
                {
                    bool hit = backend.RayCast(new SVector3(px, py, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.All, out RayHit h);
                    MainConsole.Instance.Output(hit
                        ? $"  ({px,7:0.0},{py,7:0.0}) -> HIT  z={h.Point.Z:0.000}  n.z={h.Normal.Z:0.00}"
                        : $"  ({px,7:0.0},{py,7:0.0}) -> miss");
                }
                MainConsole.Instance.Output($"  interior + (n-0.5) must HIT at the flat Z; (n) exact vertex may graze; (n+0.5) beyond region misses.");
                return;
            }

            if (cmd.Length >= 4 && cmd[1] == "probe"
                && float.TryParse(cmd[2], out float x) && float.TryParse(cmd[3], out float y))
            {
                bool hit = backend.RayCast(new SVector3(x, y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.All, out RayHit h);
                MainConsole.Instance.Output(hit
                    ? $"{LogHeader} ({x:0.0},{y:0.0}) -> HIT z={h.Point.Z:0.000} normal=({h.Normal.X:0.00},{h.Normal.Y:0.00},{h.Normal.Z:0.00})"
                    : $"{LogHeader} ({x:0.0},{y:0.0}) -> miss");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "avatarstatus")
            {
                AvatarStatus(backend);
                return;
            }

            // Console diagnostic (`jolt reloadcheck`): snapshot every physical prim's
            // saved (birth) pos vs where it is NOW, plus terrain/water under it, and classify. Run a few
            // seconds after a region reload to SEE which physical objects were displaced (SANK/FLUNG) and
            // by how much.
            if (cmd.Length >= 2 && cmd[1] == "reloadcheck")
            {
                ReloadCheck();
                return;
            }

            // Console diagnostic (`jolt vehiclestatus`): dump the LIVE vehicle state of every prim so you can CONFIRM a
            // boat is actually TYPE_BOAT + buoyancy=1 + active BEFORE testing reload.
            if (cmd.Length >= 2 && cmd[1] == "vehiclestatus")
            {
                VehicleStatus();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "charframe")
            {
                // Toggle the per-frame avatar trace for a window (default ~20 s at 11 fps). Also enables the
                // [charjump] path trace in JoltCharacter for the same window so a jump attempt is captured.
                int secs = (cmd.Length >= 3 && int.TryParse(cmd[2], out int s)) ? s : 20;
                _charFrameUntil = _stepCount + (long)Math.Ceiling(secs / 0.0908);
                CharJumpTrace = true;   // JoltCharacter reads this to emit its [charjump] path trace
                MainConsole.Instance.Output($"{LogHeader} [charframe]+[charjump] on for ~{secs}s (until step {_charFrameUntil}). Walk/jump now; trace goes to the log at Debug.");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "sitstatus")
            {
                SitStatus();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "sensortest")
            {
                SensorTest(backend);
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "raytest")
            {
                RayTest(backend);
                return;
            }

            if (cmd.Length >= 4 && cmd[1] == "heights"
                && float.TryParse(cmd[2], out float hx) && float.TryParse(cmd[3], out float hy))
            {
                // Line up the four heights at one XY so a "box rests at the wrong Z" is unambiguous:
                // (a) what Jolt actually collides at (heightfield raycast), (b) what OpenSim's scene
                // heightmap says, (c) where the dropped box actually is, (d) the water plane. Water and
                // buoyancy are non-colliding, so the box MUST rest on (a); if (a)!=(b) the cook is wrong,
                // if (c)!=(a) the box isn't resting on terrain.
                bool hit = backend.RayCast(new SVector3(hx, hy, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit rh);
                float sceneH = float.NaN;
                int gx = (int)Math.Round(hx), gy = (int)Math.Round(hy);
                if (_scene?.Heightmap != null && gx >= 0 && gx < _regionSizeX && gy >= 0 && gy < _regionSizeY)
                    sceneH = (float)_scene.Heightmap[gx, gy];
                float water = (float)(_scene?.RegionInfo?.RegionSettings?.WaterHeight ?? 0.0);

                MainConsole.Instance.Output($"{LogHeader} heights at ({hx:0.0},{hy:0.0}):");
                MainConsole.Instance.Output($"  (a) Jolt heightfield raycast : {(hit ? $"HIT z={rh.Point.Z:0.000} (n.z={rh.Normal.Z:0.00})" : "MISS - NO terrain collision here")}");
                MainConsole.Instance.Output($"  (b) OpenSim scene heightmap  : {sceneH:0.000}");
                MainConsole.Instance.Output($"  (d) region water height      : {water:0.000}");
                _diagnostics?.DescribeDrops(backend, line => MainConsole.Instance.Output(line));
                MainConsole.Instance.Output($"  read: (a)==(b) => cook matches OpenSim; box rest (c) should ~= (a)+halfHeight. (c)~water while (a)!=water => box not on terrain.");
                return;
            }

            MainConsole.Instance.Output("Usage: " + ReadOnlyUsage);
        }

        // Report each logged-in avatar's CharacterVirtual state - position, IsSupported, ground normal/body,
        // capsule dims - and assert it stands on what is under it (supported, not sinking: on the terrain, capsule
        // centre ~ terrainZ + StandHalf, or on a prim, the ground body), not at NaN or underground. Run it right
        // after login, and again after walking somewhere to confirm position tracks and IsSupported stays true.
        private void AvatarStatus(IPhysicsBackend backend)
        {
            System.Collections.Generic.List<JoltCharacter> avs;
            lock (_avatars)
                avs = new System.Collections.Generic.List<JoltCharacter>(_avatars.Values);

            if (avs.Count == 0) { MainConsole.Instance.Output($"{LogHeader} no avatars in the physics scene - log in first, then re-run."); return; }

            MainConsole.Instance.Output($"{LogHeader} avatar status ({avs.Count} in scene, step {_stepCount}):");
            foreach (JoltCharacter a in avs)
            {
                Vector3 p = a.Position;
                bool nan = float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z);

                float terrainZ = float.NaN;
                if (!nan && backend.RayCast(new SVector3(p.X, p.Y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit th))
                    terrainZ = th.Point.Z;
                float expectedCentre = terrainZ + a.StandHalf + a.FeetOffset;
                float dZ = p.Z - expectedCentre;

                string groundBody = a.GroundIsTerrain ? $"terrain({a.GroundBody.Value})" : a.GroundBody.IsValid ? $"body({a.GroundBody.Value})" : "none";
                string verdict = nan ? "FAIL: NaN position"
                    : a.Flying ? "flying (gravity off - ground checks N/A)"
                    : (a.IsSupported && !float.IsNaN(dZ) && MathF.Abs(dZ) < 0.5f) ? "PASS: supported, seated on terrain"
                    : (a.IsSupported && a.GroundBody.IsValid && !a.GroundIsTerrain && !float.IsNaN(dZ) && dZ > -0.5f) ? "PASS: supported, standing on a prim above the terrain"
                    : !a.IsSupported ? "off: not supported (in the air / falling)"
                    : "OFF: supported but not at terrain height (check dZ)";

                MainConsole.Instance.Output($"  id={a.LocalID,-6} '{a.Name}' pos=({p.X:0.00},{p.Y:0.00},{p.Z:0.000}) speed={a.Velocity.Length():0.000} m/s flying={(a.Flying ? "Y" : "N")}");
                MainConsole.Instance.Output($"        supported={(a.IsSupported ? "Y" : "N")} sliding={(a.IsSliding ? "Y" : "N")} groundNormal=({a.GroundNormal.X:0.00},{a.GroundNormal.Y:0.00},{a.GroundNormal.Z:0.00}) groundBody={groundBody}");
                MainConsole.Instance.Output($"        capsule: halfHeight={a.CapsuleHalfHeight:0.000} radius={a.CapsuleRadius:0.000} standHalf={a.StandHalf:0.000} feetOffset={a.FeetOffset:0.000}");
                MainConsole.Instance.Output($"        terrainZ={terrainZ:0.000} expectedCentreZ={expectedCentre:0.000} dZ={dZ:0.000}  [{verdict}]");
            }
            MainConsole.Instance.Output($"  PASS = supported=Y, not NaN, |dZ|<0.5 (capsule centre ~ terrain + standHalf), or supported=Y on a prim (groundBody=body(..)) above the terrain. After walking: pos tracks, supported stays Y on flat terrain.");
        }

        // `jolt reloadcheck`: after a region reload, print each
        // PHYSICAL prim's DB-saved (birth) pos vs where it is NOW, the terrain/water under it, the drift,
        // and a verdict (OK / SANK / SANK-BELOW-TERRAIN / FLUNG). A physical
        // object that reads e.g. saved z=25.0 -> now z=-40.2 SANK-BELOW-TERRAIN is the silent loss - that
        // now-position is what OpenSim persists back, so it is invisible on the next reload.
        // `jolt vehiclestatus`: live vehicle-state dump - confirms a
        // boat is a working vehicle (TYPE_BOAT, buoyancy=1, active) LIVE, before testing reload.
        private void VehicleStatus()
        {
            System.Collections.Generic.List<JoltPrim> ps;
            lock (_prims)
                ps = new System.Collections.Generic.List<JoltPrim>(_prims.Values);
            var vs = ps.FindAll(p => p.IsVehicle);
            MainConsole.Instance.Output($"{LogHeader} vehiclestatus: {ps.Count} prims, {vs.Count} with a vehicle controller (water={WaterLevel:0.0}):");
            if (vs.Count == 0)
                MainConsole.Instance.Output($"  NO prim has a vehicle. If you set llSetVehicleType and see this, the vehicle did NOT reach physics (script/plumbing) - reset the script to re-run state_entry.");
            foreach (JoltPrim p in vs)
                MainConsole.Instance.Output($"  id={p.LocalID,-6} '{p.Name}' physical={(p.IsPhysicalBody ? "Y" : "N")}  {p.VehicleInfo()}");
            MainConsole.Instance.Output($"  EXPECT for a live boat: type=Boat active=Y buoyancy=1.00 physical=Y. Then it should HOVER at water+0.5, not fall/sink.");
        }

        private void ReloadCheck()
        {
            System.Collections.Generic.List<JoltPrim> ps;
            lock (_prims)
                ps = new System.Collections.Generic.List<JoltPrim>(_prims.Values);

            var phys = ps.FindAll(p => p.IsPhysicalBody);
            MainConsole.Instance.Output($"{LogHeader} reloadcheck: {ps.Count} prims in scene, {phys.Count} PHYSICAL (step {_stepCount}, water={WaterLevel:0.0}):");
            if (phys.Count == 0) { MainConsole.Instance.Output($"  (no physical prims - rez one physical, reload the region, then re-run.)"); return; }

            int displaced = 0;
            foreach (JoltPrim p in phys)
            {
                Vector3 b = p.BirthPos, c = p.CurrentPos;
                float dz = c.Z - b.Z;
                float dh = MathF.Sqrt((c.X - b.X) * (c.X - b.X) + (c.Y - b.Y) * (c.Y - b.Y));
                float terrZ = TerrainHeightAt(c.X, c.Y);
                bool belowTerrain = c.Z < terrZ - 0.5f;
                bool bad = belowTerrain || MathF.Abs(dz) > 2f || dh > 5f;
                if (bad) displaced++;
                string verdict = belowTerrain ? "SANK-BELOW-TERRAIN (hidden)"
                    : dz < -2f ? "SANK"
                    : dh > 5f ? "FLUNG"
                    : "OK (survived at saved pos)";
                MainConsole.Instance.Output(
                    $"  id={p.LocalID,-6} '{p.Name}' vehicle={(p.IsVehicle ? "Y" : "N")} kind={p.ShapeKind}");
                MainConsole.Instance.Output(
                    $"        saved=({b.X:0.0},{b.Y:0.0},{b.Z:0.0}) -> now=({c.X:0.0},{c.Y:0.0},{c.Z:0.0}) dz={dz:0.0} dh={dh:0.0} terrainZ={terrZ:0.0}  [{verdict}]");
            }
            MainConsole.Instance.Output($"  => {displaced}/{phys.Count} physical prims DISPLACED from their saved position. Any 'now' below terrain/at seabed is the silent loss (Return recovers it).");
        }

        // ---------------------------------------------------------------------
        // Sit / unsit. The physics core is the CHARACTER LIFECYCLE: OpenSim SITS by REMOVING the
        // physics actor (ScenePresence.RemoveFromPhysicalScene -> RemoveAvatar -> the CharacterVirtual +
        // its query marker are destroyed) and STANDS by re-adding it (AddToPhysicalScene -> AddAvatar -> a
        // fresh character at the release position). So "suspend" == the character is GONE (no gravity, no
        // ground-detection, no movement integration), and "re-engage" == the walking model rebuilt.
        // A moving seat is ridden via OpenSim scene-graph parenting (the seated avatar's world position
        // tracks the prim), independent of physics. `jolt sitstatus` OBSERVES that transition; the
        // sittest / unsit test commands (the diagnostics assembly) DRIVE it.
        // ---------------------------------------------------------------------

        internal ScenePresence FirstRootAvatar()
        {
            if (_scene == null) return null;
            foreach (ScenePresence sp in _scene.GetScenePresences())
                if (!sp.IsChildAgent) return sp;
            return null;
        }

        // Report each root avatar's SIT state (parented to a prim) vs its PHYSICS presence (a live
        // JoltCharacter). Invariant: seated => NO character (suspended); walking => character present.
        internal void SitStatus()
        {
            if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }

            var byId = new System.Collections.Generic.Dictionary<uint, JoltCharacter>();
            lock (_avatars)
                foreach (JoltCharacter a in _avatars.Values) byId[a.LocalID] = a;

            int roots = 0;
            MainConsole.Instance.Output($"{LogHeader} sit status (step {_stepCount}, {byId.Count} physics character(s) live):");
            foreach (ScenePresence sp in _scene.GetScenePresences())
            {
                if (sp.IsChildAgent) continue;
                roots++;
                bool seated = sp.IsSatOnObject;
                bool hasChar = byId.TryGetValue(sp.LocalId, out JoltCharacter jc);
                Vector3 p = sp.AbsolutePosition;

                string verdict = seated
                    ? (hasChar ? "FAIL: SEATED but a physics character is still alive (suspend did not take)"
                               : "PASS: SEATED -> character removed (no gravity / ground-detection / movement)")
                    : (hasChar ? "PASS: WALKING -> character present (walking model live)"
                               : "note: not seated and no character (not yet physical / mid-transition)");

                MainConsole.Instance.Output($"  id={sp.LocalId,-6} '{sp.Name}' seated={(seated ? "Y" : "N")} parentId={sp.ParentID} pos=({p.X:0.00},{p.Y:0.00},{p.Z:0.000}) hasCharacter={(hasChar ? "Y" : "N")}");
                if (hasChar)
                    MainConsole.Instance.Output($"        character: Z={jc.Position.Z:0.000} supported={(jc.IsSupported ? "Y" : "N")} sliding={(jc.IsSliding ? "Y" : "N")} vZ={jc.Velocity.Z:0.000}");
                MainConsole.Instance.Output($"        [{verdict}]");
            }
            if (roots == 0)
                MainConsole.Instance.Output($"  no root avatars in the region - log in first.");
            else
                MainConsole.Instance.Output($"  SEATED avatars have no physics body, so they CANNOT fall/slide - position is driven by the prim (scene-graph). Stand -> character re-created at release pos.");
        }

        // Avatar query-marker check. NOTE: OpenSim's llSensor is SCENE-GRAPH, not
        // physics - SensorRepeat.doAgentSensor/doObjectSensor iterate the ScenePresence / Entities lists and
        // compute distance/arc directly (and explicitly handle SEATED avatars), so llSensor never touches the
        // engine and finds avatars with or without a marker. The kinematic query-marker matters for the
        // PHYSICS query path (llCastRay / OverlapSphere with the Avatar filter). This console is a
        // LIVE test of that: a physics agent-overlap must find the logged-in avatar's marker, by UserData
        // (avatar presence carries UserData, not a solver BodyId), and be range-correct.
        private void SensorTest(IPhysicsBackend backend)
        {
            ScenePresence sp = FirstRootAvatar();
            if (sp == null) { MainConsole.Instance.Output($"{LogHeader} no logged-in avatar - log in first."); return; }
            Vector3 p = sp.AbsolutePosition;

            MainConsole.Instance.Output($"{LogHeader} avatar query marker - physics agent-query vs the live avatar '{sp.Name}' (LocalId={sp.LocalId}):");
            MainConsole.Instance.Output($"  (OpenSim llSensor is scene-graph and does NOT use this; the marker is what makes llCastRay/overlap agent-queries find an avatar.)");

            var hits = new BodyId[32];
            int nNear = backend.OverlapSphere(new SVector3(p.X, p.Y, p.Z), 5f, QueryFilter.Avatar, hits);
            bool nearFound = false; uint nearUd = 0;
            for (int i = 0; i < nNear; i++)
                if (backend.TryGetBodyState(hits[i], out BodyState bs) && bs.UserData == sp.LocalId) { nearFound = true; nearUd = bs.UserData; }

            int nFar = backend.OverlapSphere(new SVector3(p.X + 100f, p.Y + 100f, p.Z), 5f, QueryFilter.Avatar, hits);
            bool farFound = false;
            for (int i = 0; i < nFar; i++)
                if (backend.TryGetBodyState(hits[i], out BodyState bs) && bs.UserData == sp.LocalId) farFound = true;

            bool seated = sp.IsSatOnObject;
            MainConsole.Instance.Output($"  NEAR overlap (sphere r=5 at avatar): {nNear} agent-layer hit(s); avatar marker (UserData={sp.LocalId}) found = {(nearFound ? "Y" : "N")}{(nearFound ? $" (resolved id={nearUd})" : "")}");
            MainConsole.Instance.Output($"  FAR  overlap (sphere r=5, +100 m):   {nFar} agent-layer hit(s); avatar marker found = {(farFound ? "Y" : "N")}");
            MainConsole.Instance.Output($"  avatar seated = {(seated ? "Y" : "N")}  (SEATED => the query marker is destroyed with the character, so a PHYSICS agent-query cannot find it; OpenSim's scene-graph llSensor still finds seated avatars.)");

            string verdict = (nearFound && !farFound)
                ? "PASS: the query marker is query-visible LIVE via the physics Avatar filter - avatar found in range, identity by UserData, not found out of range."
                : seated ? "note: avatar is SEATED -> no marker -> physics agent-query can't find it (expected). `jolt unsit` and re-run to see the marker."
                : "FAIL: the physics agent-query did NOT find the avatar marker in range - the query marker is not query-visible live.";
            MainConsole.Instance.Output($"  [{verdict}]");
            MainConsole.Instance.Output($"  llSensor(AGENT) itself: works out-of-box (scene-graph) for WALKING and SEATED avatars - no physics wiring needed. This test validates the marker for the llCastRay/overlap path (see `jolt raytest`).");
        }

        // llCastRay through the real ray path. Casts a ray straight DOWN through the logged-in
        // avatar with the Avatar|Terrain filter and expects, in DISTANCE order: [0] the avatar's query marker
        // (near), [1] the terrain (far). Proves in one shot: llCastRay(AGENT) hits the avatar via the marker
        // (identity by UserData), a terrain hit, and multi-hit distance ordering. This is the SAME RayCastAll
        // path llCastRay takes (Scene.RayCastFiltered -> RaycastWorld -> backend.RayCastAll).
        private void RayTest(IPhysicsBackend backend)
        {
            ScenePresence sp = FirstRootAvatar();
            if (sp == null) { MainConsole.Instance.Output($"{LogHeader} no logged-in avatar - log in first."); return; }
            Vector3 p = sp.AbsolutePosition;

            var origin = new SVector3(p.X, p.Y, p.Z + 3f);          // 3 m above the avatar centre
            var dir = new SVector3(0f, 0f, -1f);
            QueryFilter qf = QueryFilter.Avatar | QueryFilter.Terrain;
            var hits = new RayHit[8];
            int n = backend.RayCastAll(origin, dir, 200f, qf, hits);

            MainConsole.Instance.Output($"{LogHeader} llCastRay path test - ray DOWN through '{sp.Name}' (filter=Avatar|Terrain), {n} hit(s) in distance order:");
            bool avatarHit = false, terrainHit = false, ordered = true;
            float last = -1f;
            for (int i = 0; i < n; i++)
            {
                string what = hits[i].UserData == sp.LocalId ? "AVATAR-MARKER" : hits[i].UserData == 0 ? "TERRAIN" : $"prim({hits[i].UserData})";
                MainConsole.Instance.Output($"  [{i}] dist={hits[i].Distance:0.000} UserData={hits[i].UserData} => {what} pos=({hits[i].Point.X:0.00},{hits[i].Point.Y:0.00},{hits[i].Point.Z:0.000}) normal=({hits[i].Normal.X:0.00},{hits[i].Normal.Y:0.00},{hits[i].Normal.Z:0.00})");
                if (hits[i].UserData == sp.LocalId) avatarHit = true;
                if (hits[i].UserData == 0) terrainHit = true;
                if (hits[i].Distance < last) ordered = false;
                last = hits[i].Distance;
            }

            bool seated = sp.IsSatOnObject;
            string verdict = (avatarHit && ordered)
                ? "PASS: the physics ray HITS the walking avatar via the query marker (identity by UserData), terrain hit, multi-hit sorted by distance."
                : seated ? "note: SEATED -> the query marker is gone, so this RAW physics ray misses the avatar. That is CORRECT and SL-exact: llCastRay itself STILL hits a seated avatar via OpenSim's AvatarIntersection(skipPhys) fallback (it handles agents WITHOUT a physics body). `jolt unsit` + re-run to see the marker hit."
                : "FAIL: the agent ray did not hit the walking avatar marker.";
            MainConsole.Instance.Output($"  terrainHit={(terrainHit ? "Y" : "N")} avatarHit={(avatarHit ? "Y" : "N")} distanceOrdered={(ordered ? "Y" : "N")}");
            MainConsole.Instance.Output($"  [{verdict}]");
            MainConsole.Instance.Output($"  llCastRay is SL-exact: WALKING avatars come from this physics marker (agent->Avatar filter); SEATED avatars are added by OpenSim's AvatarIntersection(skipPhys) - so each avatar is detected exactly ONCE (no duplicate). RayFilterFlags map: agent->Avatar, physical->Dynamic, nonphysical->Static, land->Terrain, LSLPhantom(phantom|volumedtc)->Sensor.");
        }

        // ---------------------------------------------------------------------
        // PhysicsScene - actor creation (avatars; prim shape cooking and dynamics)
        // ---------------------------------------------------------------------

        // The avatar gets a physics body - a Jolt CharacterVirtual. ScenePresence calls the
        // localID overload (via the feetOffset one); overriding it here means we have the avatar's LocalID
        // up front, so the CharacterVirtual + its query marker carry the right identity. The abstract
        // no-localID overload delegates so any caller of the base contract still works.
        public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
            => CreateAvatar(0, avName, position, velocity, size, 0f, isFlying);

        public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
            => CreateAvatar(localID, avName, position, velocity, size, 0f, isFlying);

        // The overload ScenePresence actually calls carries the avatar's feetOffset - the gap between the
        // capsule centre and the visual feet. Override it (rather than let the base drop it) so the spawn
        // seat can put the FEET on the surface, not the capsule centre.
        public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 size, float feetOffset, bool isFlying)
            => CreateAvatar(localID, avName, position, Vector3.Zero, size, feetOffset, isFlying);

        // The avatar of an object that carried it in, outside the region, is made with the flag set: the region steps on
        // its own thread, and a step between the character's creation and a flag set by the caller would pull it inside.
        public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 size, float feetOffset, bool isFlying, bool allowOutsideRegion)
            => CreateAvatar(localID, avName, position, Vector3.Zero, size, feetOffset, isFlying, allowOutsideRegion);

        // Test seam: called on the creating thread once the new character is registered, before the caller has it back.
        // A test steps the scene from here, which is what another thread stepping the region could do at that moment.
        internal Action<JoltCharacter> AvatarCreatedHook;

        private PhysicsActor CreateAvatar(uint localID, string avName, Vector3 position, Vector3 velocity, Vector3 size, float feetOffset, bool isFlying, bool allowOutsideRegion = false)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null)
                return PhysicsActor.Null;

            // Put the avatar where the simulator asks. ScenePresence has already chosen the height: on the
            // ground, on a prim its landing ray found (MakeRootAgent), on a seat's stand point (StandUp), or in
            // the air for a teleport or crossing in the air. BulletS (BSCharacter) and ubODE (OdeCharacter) take
            // that position as given, and so does this. Login, teleport, region crossing and standing up all
            // arrive here, as ScenePresence.AddToPhysicalScene.
            //
            // The one correction: a position that would put the body below the terrain is lifted to stand on
            // it, so an avatar never starts underground. It is never moved down.
            float standHalf = JoltCharacter.StandHalfFor(size);
            float terrainZ = float.NaN;
            // THREAD SAFETY: do NOT raycast the Jolt heightfield here. CreateAvatar runs on the
            // LOGIN/TELEPORT thread, so a native query could land inside a running _system.Update
            // and corrupt Jolt's LIFO TempAllocator -> "Freeing in the wrong order" -> std::abort(), which
            // kills the simulator when an avatar teleports into the region. TerrainHeightAt reads the SAME cooked
            // sample field the collision heightfield was built from, but it is a plain managed float[]
            // (bilinear interpolation, no native call), so it is safe from any thread and needs no lock.
            // Heights agree with the collision surface because both come from that one field.
            if (_terrainField != null && _terrainFieldM >= 2)
                terrainZ = TerrainHeightAt(position.X, position.Y);
            float centreZ = ArrivalCentreZ(position.Z, terrainZ, standHalf, feetOffset);
            var spawn = new Vector3(position.X, position.Y, centreZ);

            var jc = new JoltCharacter(this, backend, localID, avName, spawn, size, feetOffset, isFlying, allowOutsideRegion);
            if (velocity != Vector3.Zero)
                jc.SetMomentum(velocity);

            lock (_avatars)
                _avatars[jc.CharacterHandle.Value] = jc;

            AvatarCreatedHook?.Invoke(jc);

            string lifted = centreZ != position.Z ? " (lifted onto the terrain)" : "";
            m_log.LogInformation($"{LogHeader} avatar '{avName}' id={localID} placed at ({position.X:0},{position.Y:0}) askedZ={position.Z:0.000} centreZ={spawn.Z:0.000}{lifted} terrainZ={terrainZ:0.00} standHalf={standHalf:0.000} feetOffset={feetOffset:0.000} flying={isFlying}.");
            return jc;
        }

        /// <summary>
        /// The capsule centre Z an arriving avatar gets: the asked centre Z, unless that would put the body below
        /// the terrain, when it is lifted to stand on the terrain (terrainZ + standHalf + feetOffset, the same seat
        /// as <see cref="TryComputeUnbury"/>) plus 1 cm, so the character settles from just above rather than
        /// starting in penetration (which would resolve as a shove on its first step). Never lower than asked. A
        /// non-finite asked Z also gets the terrain seat, as every arrival did before. A NaN terrainZ (no terrain
        /// yet) leaves the asked Z as it is.
        /// </summary>
        internal static float ArrivalCentreZ(float askedCentreZ, float terrainZ, float standHalf, float feetOffset)
        {
            if (!float.IsFinite(terrainZ))
                return askedCentreZ;
            if (!float.IsFinite(askedCentreZ) || TryComputeUnbury(askedCentreZ, terrainZ, standHalf, feetOffset, 0f, out _))
                return terrainZ + standHalf + feetOffset + 0.01f;
            return askedCentreZ;
        }

        public override void RemoveAvatar(PhysicsActor actor)
        {
            if (actor is not JoltCharacter jc)
                return;
            lock (_avatars)
                _avatars.Remove(jc.CharacterHandle.Value);
            jc.Destroy();   // RemoveCharacter also tears down the query marker
            m_log.LogInformation($"{LogHeader} avatar '{jc.Name}' id={jc.LocalID} removed.");
        }

        public override void RemovePrim(PhysicsActor prim)
        {
            if (prim is JoltPrim jp)
            {
                jp.Destroy();
                lock (_prims)
                    _prims.Remove(jp.LocalID);
            }
        }

        // The real OpenSim delivery boundary: SceneObjectPart.AddToPhysics calls the 9-argument overload with the
        // prim's phantom flag and PRIM_PHYSICS_SHAPE_TYPE. A non-physical, non-phantom prim becomes a STATIC Jolt
        // body; a physical one becomes a dynamic body, created INERT and woken in Simulate. A phantom one goes on
        // the Phantom layer (it touches only the terrain); a volume detector, which the scene adds as phantom and
        // then calls SetVolumeDetect(1) on, ends up a sensor. A non-physical phantom that is not a volume detector
        // never reaches here: SceneObjectPart.ApplyPhysics and UpdatePrimFlags leave it out of physics.
        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                                  Vector3 size, Quaternion rotation, bool isPhysical, uint localid)
            => AddPrimShape(primName, pbs, position, size, rotation, isPhysical, false, (byte)PhysShapeType.prim, localid);

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                                  Vector3 size, Quaternion rotation, bool isPhysical, bool isPhantom, uint localid)
            => AddPrimShape(primName, pbs, position, size, rotation, isPhysical, isPhantom, (byte)PhysShapeType.prim, localid);

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                                  Vector3 size, Quaternion rotation, bool isPhysical, bool isPhantom, byte shapetype, uint localid)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null || pbs == null)
                return PhysicsActor.Null;

            // Defence in depth: the cook path is throw-free (CookPrimShape always returns a valid shape -
            // fast-path, mesh/hull, or bbox fallback), but if body creation ever throws we accept-and-ignore
            // so one bad prim can never abort a whole region load. Returns PhysicsActor.Null on failure.
            JoltPrim prim;
            try
            {
                prim = new JoltPrim(this, backend, localid, primName, pbs, position, size, rotation, isPhysical, isPhantom, shapetype);
            }
            catch (Exception e)
            {
                m_log.LogWarning($"{LogHeader} AddPrimShape failed for '{primName}' (localid {localid}): {e.GetType().Name}: {e.Message}; prim has no physics.");
                return PhysicsActor.Null;
            }
            lock (_prims)
                _prims[localid] = prim;
            prim.RequestMeshAssetIfNeeded();   // after registration: the answer may arrive before this returns
            return prim;
        }

        // Fixed-shape fast path: an UN-CUT box / sphere / cylinder cooks straight to a
        // Jolt primitive with NO meshmerizer. Classification matches what a real viewer/OAR prim
        // carries (canonical ProfileShape+Extrusion), NOT PrimitiveBaseShape.CreateCylinder() - whose
        // factory emits Square+Curve1 (an SL "tube"), a known OpenSim quirk. Anything else (cut/hollow/
        // twisted, sculpt/mesh, non-uniform sphere/cylinder) goes to the IMesher path - a
        // triangle mesh when static, a convex hull when physical - with a bounding box as the fallback
        // when there is no mesher or the geometry is unusable. `axisCorrection` (System.Numerics) is
        // folded into the body
        // orientation by JoltPrim; `kind` is for the diagnostic read-out.
        //
        // `isPhysical` says how a mesher shape is cooked (true: one convex hull, so the body has volume and mass). The prim's
        // own physical flag is `bodyIsPhysical`; it differs from `isPhysical` only for a non-physical prim cooked as a hull,
        // and the small-mesh rule below goes by it.
        //
        // `useHullList` is set for a physical prim whose shape type is PRIM: a mesh asset's own hull list is then cooked, one
        // convex hull per entry. `share` lets a mesh prim take the cooked shape another prim with the same asset, size and
        // kind already holds (a shape taken this way is given back with ReleaseShape of this class, not the backend's).
        internal ShapeId CookPrimShape(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out SQuaternion axisCorrection, out string kind,
                                       bool? bodyIsPhysical = null, bool useHullList = false, bool share = false)
            => CookPrimShape(backend, pbs, size, isPhysical, out axisCorrection, out kind, out _, bodyIsPhysical, useHullList, share);

        // As below, without the real volume.
        internal ShapeId CookPrimShape(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out SQuaternion axisCorrection, out string kind,
                                       out int hullCount, bool? bodyIsPhysical = null, bool useHullList = false, bool share = false)
            => CookPrimShape(backend, pbs, size, isPhysical, out axisCorrection, out kind, out hullCount, out _, bodyIsPhysical, useHullList, share);

        // As above, and `hullCount` is how many convex hulls the shape is made of: the cells of a concave prim or the hulls of
        // a mesh asset's list, else 1 (a box, a sphere, a cylinder, a mesh, one hull, the bounding box). A complexity limit
        // reads it (JoltPrim.HullCount). `massVolume` is the real volume (m^3, at `size`) a concave prim cooked as cells weighs,
        // from the mesher's closed mesh; 0 when the shape's own volume is the one to use (everything else, and a concave prim
        // whose mesh cannot be trusted).
        internal ShapeId CookPrimShape(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out SQuaternion axisCorrection, out string kind,
                                       out int hullCount, out double massVolume, bool? bodyIsPhysical = null, bool useHullList = false, bool share = false)
        {
            axisCorrection = SQuaternion.Identity;
            hullCount = 1;
            massVolume = 0.0;
            float hx = size.X * 0.5f, hy = size.Y * 0.5f, hz = size.Z * 0.5f;

            if (pbs != null && PrimHasNoCuts(pbs))
            {
                byte path = pbs.PathCurve;
                ProfileShape profile = pbs.ProfileShape;

                // BOX: square profile, straight extrusion. Half-extents = size/2.
                if (profile == ProfileShape.Square && path == (byte)Extrusion.Straight)
                {
                    kind = "box";
                    return backend.CreateBoxShape(new SVector3(hx, hy, hz));
                }

                // SPHERE: half-circle profile, curve1 extrusion. Native sphere only when uniform - a
                // non-uniform "sphere" is an ellipsoid and must go through the mesher.
                if (profile == ProfileShape.HalfCircle && path == (byte)Extrusion.Curve1
                    && Approx(size.X, size.Y) && Approx(size.Y, size.Z))
                {
                    kind = "sphere";
                    return backend.CreateSphereShape(hx);
                }

                // CYLINDER: circle profile, straight extrusion. SL cylinders are Z-height; Jolt's
                // CylinderShape axis is Y, so correct +90 deg about X (local Y -> local Z) before the
                // prim's own rotation. Circular cross-section only (X==Y); elliptical -> mesher.
                if (profile == ProfileShape.Circle && path == (byte)Extrusion.Straight
                    && Approx(size.X, size.Y))
                {
                    kind = "cylinder";
                    axisCorrection = SQuaternion.CreateFromAxisAngle(SVector3.UnitX, MathF.PI * 0.5f);
                    return backend.CreateCylinderShape(hz, hx);   // halfHeight=Z/2, radius=X/2
                }
            }

            // Not a basic fast-path shape (cut/hollow/twisted, prism, torus, sculpt, mesh): go through
            // the meshmerizer. The convex-vs-mesh decision lives HERE - our equivalent of
            // BulletSim's BSShapeCollection.CreateGeomMeshOrHull (physical && ShouldUseHulls -> hull;
            // else mesh). Contract: a triangle MeshShape has Volume 0, so a PHYSICAL prim
            // MUST use the convex hull or it would rez with mass 0 - hence physical -> hull here.
            ShapeId cooked = CookMeshShape(backend, pbs, size, isPhysical, bodyIsPhysical ?? isPhysical, useHullList, share, out kind, out hullCount, out massVolume);
            if (cooked.IsValid)
                return cooked;

            // Mesher unavailable / returned nothing usable / cook threw: conservative solid bounding box.
            kind = BoxFallbackKind;
            hullCount = 1;
            massVolume = 0.0;
            return backend.CreateBoxShape(new SVector3(hx, hy, hz));
        }

        // The IMesher path: PrimitiveBaseShape -> IMesher.CreateMesh -> getVertexListAsFloat /
        // getIndexListAsInt -> CreateMeshShape (non-physical triangle mesh) or CreateConvexHullShape
        // (physical hull). Returns ShapeId.Invalid on any failure so the caller can fall back. Also
        // stashes a characterization of the RAW mesher output (_lastMeshStats) for the diagnostic read-out.
        private ShapeId CookMeshShape(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, bool bodyIsPhysical,
                                      bool useHullList, bool share, out string kind, out int hullCount, out double massVolume)
        {
            massVolume = 0.0;
            // A mesh prim's shape is a function of its asset, size and how it is cooked, so prims that agree on all three can
            // hold one cooked shape. Only a real cook is kept: a box for want of the asset, or for size, is not.
            bool isMesh = IsMeshPrim(pbs);
            bool listWanted = isMesh && isPhysical && useHullList;
            // A prim made from its own profile and path (not a mesh, not a sculpt) is cooked as convex cells when it is
            // physical and of shape type PRIM (useHullList). Prims of one set of shape parameters share one compound, cooked at
            // unit size and scaled to each prim's size (CookPrimCellsShared); a prim that may not share cooks its own.
            bool cellsWanted = !isMesh && isPhysical && useHullList && !IsSculptPrim(pbs);
            if (cellsWanted && share && CellShapeCacheEnabled && ScaleReusable(size, true) && m_mesher is IMesherShapeData)
            {
                ShapeId shared = CookPrimCellsShared(backend, pbs, size, isPhysical, bodyIsPhysical, out kind, out hullCount, out massVolume);
                if (shared.IsValid)
                    return shared;
            }
            if (!share || !isMesh || pbs.SculptTexture.IsZero())
            {
                // A prim that may not share (a part of a linkset that needs a density of its own) still skips the search for its
                // cells when prims of its parameters have been cooked: its own compound is made from their points.
                if (cellsWanted && !share && CellShapeCacheEnabled && ScaleReusable(size, true))
                {
                    ShapeId copy = CookPrimCellsCopy(backend, pbs, size, out kind, out hullCount, out massVolume);
                    if (copy.IsValid)
                        return copy;
                }
                return CookMeshShapeCore(backend, pbs, size, isPhysical, bodyIsPhysical, listWanted, cellsWanted, out kind, out hullCount, out massVolume, out _);
            }

            SharedShapeKind kindWanted = listWanted ? SharedShapeKind.HullList : isPhysical ? SharedShapeKind.Hull : SharedShapeKind.Mesh;
            var key = new SharedShapeKey(pbs.SculptTexture, size, kindWanted);
            var baseKey = new BaseKey(pbs.SculptTexture, kindWanted);
            // The lock is held across the cook so that prims asking for the same shape at once get one cook, not two. The
            // threads that come here are the scene's own (adding a prim, changing its size or physical state) and the asset
            // service's callback thread (a mesh arriving); the cook is milliseconds and the native calls take the backend's
            // own lock, which never calls back into this class, so the lock order is this lock, then the backend's.
            lock (_sharedShapeLock)
            {
                if (_sharedShapes.TryGetValue(key, out SharedShape held))
                {
                    held.Users++;
                    kind = held.Kind;
                    hullCount = held.HullCount;
                    return held.Shape;
                }

                // Another size of a mesh already cooked: the shape of that cook, at this prim's scale. No fetch, no decode, no
                // cook. A size the size rules treat differently (a non-physical mesh under SmallMeshSide on every side is a
                // box; the mesher makes no side under MesherMinSide) is cooked as it was.
                bool scalable = ScaleReusable(size, bodyIsPhysical);
                if (scalable && _bases.TryGetValue(baseKey, out BaseShape basis))
                {
                    ShapeId scaled = ScaledFromBase(backend, basis, size);
                    if (scaled.IsValid)
                    {
                        var wrapped = new SharedShape { Key = key, Shape = scaled, Kind = basis.Kind, HullCount = basis.HullCount, Users = 1, Basis = basis };
                        basis.Entries++;
                        _sharedShapes[key] = wrapped;
                        _sharedByShape[scaled.Value] = wrapped;
                        kind = basis.Kind;
                        hullCount = basis.HullCount;
                        return scaled;
                    }
                }

                ShapeId shape = CookMeshShapeCore(backend, pbs, size, isPhysical, bodyIsPhysical, listWanted, false, out kind, out hullCount, out _, out _);
                if (shape.IsValid && kind != BoxFallbackKind && !kind.StartsWith("box", StringComparison.Ordinal))
                {
                    var entry = new SharedShape { Key = key, Shape = shape, Kind = kind, HullCount = hullCount, Users = 1 };
                    if (scalable && !_bases.ContainsKey(baseKey))
                    {
                        // This cook becomes the shape the asset's other sizes are made from. The table keeps a reference of its
                        // own, so it outlives this size's prims while other sizes use it.
                        backend.AddShapeRef(shape);
                        var basis2 = new BaseShape { Key = baseKey, Shape = shape, Size = size, Kind = kind, HullCount = hullCount, Entries = 1 };
                        _bases[baseKey] = basis2;
                        entry.Basis = basis2;
                    }
                    _sharedShapes[key] = entry;
                    _sharedByShape[shape.Value] = entry;
                }
                return shape;
            }
        }

        // ---------------------------------------------------------------------
        // Shared compounds of cells. A concave prim's cells are a function of its shape parameters alone: they are cooked once
        // at unit size and a prim of any size holds a scaled shape over that compound (a hull compound scales exactly on each
        // axis, IsScaleValid), so prims with the same parameters share one cooked compound, and a change to any parameter
        // is a different key. The real volume of the prim's mesh is kept with it (unit size, times the size's three sides).
        // The compound and its sizes are released as a mesh asset's are (ReleaseShape).
        // ---------------------------------------------------------------------

        // The shape parameters the mesher reads for a prim made from its own profile and path (Meshmerizer.ExtrudePrimShape),
        // and the level of detail it is asked for: two prims with the same string have the same unit mesh.
        internal static string PrimShapeParameters(PrimitiveBaseShape pbs)
            => string.Join("|", MeshLod.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                pbs.PathCurve, pbs.ProfileCurve, pbs.HollowShape, pbs.ProfileBegin, pbs.ProfileEnd, pbs.ProfileHollow,
                pbs.PathBegin, pbs.PathEnd, pbs.PathScaleX, pbs.PathScaleY, pbs.PathShearX, pbs.PathShearY,
                pbs.PathTwist, pbs.PathTwistBegin, pbs.PathRadiusOffset, pbs.PathTaperX, pbs.PathTaperY,
                pbs.PathRevolutions, pbs.PathSkew);

        /// <summary>Whether prims of one set of shape parameters share one cooked compound of cells. On: a test turns it off to
        /// measure what the cache saves.</summary>
        internal bool CellShapeCacheEnabled = true;

        // Parameters of prims that came out as no cells (convex already, or a split that cannot be cooked): asked again, they go
        // straight to the cook of their own. Small, and emptied if it ever grows past the limit.
        private readonly HashSet<string> _notCells = new HashSet<string>();
        private const int NotCellsLimit = 4096;

        private ShapeId CookPrimCellsShared(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, bool bodyIsPhysical,
                                            out string kind, out int hullCount, out double massVolume)
        {
            kind = BoxFallbackKind;
            hullCount = 1;
            massVolume = 0.0;
            string parameters = PrimShapeParameters(pbs);
            var key = new SharedShapeKey(UUID.Zero, size, SharedShapeKind.PrimCells, parameters);
            var baseKey = new BaseKey(UUID.Zero, SharedShapeKind.PrimCells, parameters);
            double sides = (double)size.X * size.Y * size.Z;
            lock (_sharedShapeLock)
            {
                if (_sharedShapes.TryGetValue(key, out SharedShape held))
                {
                    held.Users++;
                    kind = held.Kind;
                    hullCount = held.HullCount;
                    massVolume = held.MassVolume;
                    return held.Shape;
                }
                if (_notCells.Contains(parameters))
                    return ShapeId.Invalid;

                if (!_bases.TryGetValue(baseKey, out BaseShape basis))
                {
                    ShapeId unit = CookMeshShapeCore(backend, pbs, Vector3.One, isPhysical, bodyIsPhysical, false, true, out string unitKind, out int unitCount, out double unitVolume, out SVector3[][] unitCells);
                    if (!unit.IsValid || !unitKind.StartsWith(PrimCellsKind, StringComparison.Ordinal))
                    {
                        if (unit.IsValid)
                            backend.ReleaseShape(unit);
                        if (_notCells.Count >= NotCellsLimit)
                            _notCells.Clear();
                        _notCells.Add(parameters);
                        return ShapeId.Invalid;
                    }
                    // The table's own reference is the one the cook returned; it is dropped with the last size.
                    basis = new BaseShape { Key = baseKey, Shape = unit, Size = Vector3.One, Kind = unitKind, HullCount = unitCount, UnitMassVolume = unitVolume, UnitCells = unitCells, Entries = 0 };
                    _bases[baseKey] = basis;
                }

                ShapeId scaled = ScaledFromBase(backend, basis, size);
                if (!scaled.IsValid)
                {
                    if (basis.Entries <= 0)
                    {
                        _bases.Remove(baseKey);
                        backend.ReleaseShape(basis.Shape);
                    }
                    return ShapeId.Invalid;
                }
                var entry = new SharedShape { Key = key, Shape = scaled, Kind = basis.Kind, HullCount = basis.HullCount, MassVolume = basis.UnitMassVolume * sides, Users = 1, Basis = basis };
                basis.Entries++;
                _sharedShapes[key] = entry;
                _sharedByShape[scaled.Value] = entry;
                kind = entry.Kind;
                hullCount = entry.HullCount;
                massVolume = entry.MassVolume;
                return scaled;
            }
        }

        /// <summary>How many compounds of cells have been made for a prim of its own from the points of the cells of prims with the same parameters.</summary>
        internal int PrimCellCopiesMade => Volatile.Read(ref _primCellCopiesMade);
        private int _primCellCopiesMade;

        // A compound of cells of a prim's own, for a prim that may not share one (CookPrimCellsShared): made from the points of
        // the cells the scene holds for its parameters, scaled to its size, so no mesh is read and no split is searched for. An
        // invalid shape (and the caller cooks in full) when none is held or a cell does not cook.
        private ShapeId CookPrimCellsCopy(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, out string kind, out int hullCount, out double massVolume)
        {
            kind = BoxFallbackKind;
            hullCount = 1;
            massVolume = 0.0;
            var baseKey = new BaseKey(UUID.Zero, SharedShapeKind.PrimCells, PrimShapeParameters(pbs));
            BaseShape basis;
            lock (_sharedShapeLock)
            {
                if (!_bases.TryGetValue(baseKey, out basis) || basis.UnitCells == null)
                    return ShapeId.Invalid;
            }
            var scaled = new SVector3[basis.UnitCells.Length][];
            for (int c = 0; c < scaled.Length; c++)
            {
                SVector3[] unit = basis.UnitCells[c];
                scaled[c] = new SVector3[unit.Length];
                for (int i = 0; i < unit.Length; i++)
                    scaled[c][i] = new SVector3(unit[i].X * size.X, unit[i].Y * size.Y, unit[i].Z * size.Z);
            }
            ShapeId compound = PrimHullCells.CookCompound(backend, scaled, out int failed);
            if (failed > 0 || !compound.IsValid)
            {
                if (compound.IsValid)
                    backend.ReleaseShape(compound);
                return ShapeId.Invalid;
            }
            Interlocked.Increment(ref _primCellCopiesMade);
            kind = basis.Kind;
            hullCount = basis.HullCount;
            massVolume = basis.UnitMassVolume * size.X * size.Y * size.Z;
            return compound;
        }

        /// <summary>How many compounds of cells the scene holds as bases that prims of one set of shape parameters are scaled from.</summary>
        internal int PrimCellBaseCount { get { lock (_sharedShapeLock) { int n = 0; foreach (BaseKey k in _bases.Keys) if (k.IsPrimCells) n++; return n; } } }

        // Whether a size can be made by scaling the shape cooked at another size. A mesh prim under 0.05 m on every side that is
        // not physical is a box, and the mesher raises a side under 0.01 m to 0.01 m, so those sizes are not a scale of
        // another: they are cooked on their own.
        private static bool ScaleReusable(Vector3 size, bool bodyIsPhysical)
        {
            if (!(size.X >= MesherMinSide && size.Y >= MesherMinSide && size.Z >= MesherMinSide))
                return false;
            return bodyIsPhysical || size.X >= SmallMeshSide || size.Y >= SmallMeshSide || size.Z >= SmallMeshSide;
        }

        // The base shape at another size: the shape itself at the same size, otherwise a scaled shape over it. Invalid when
        // Jolt would not scale it by exactly that (then the caller cooks it).
        private ShapeId ScaledFromBase(IPhysicsBackend backend, BaseShape basis, Vector3 size)
        {
            if (size == basis.Size)
            {
                backend.AddShapeRef(basis.Shape);
                return basis.Shape;
            }
            var scale = new SVector3(size.X / basis.Size.X, size.Y / basis.Size.Y, size.Z / basis.Size.Z);
            if (!backend.IsScaleValid(basis.Shape, scale))
                return ShapeId.Invalid;
            try
            {
                ShapeId scaled = backend.CreateScaledShape(basis.Shape, scale);
                Interlocked.Increment(ref _scaledShapesMade);
                return scaled;
            }
            catch (Exception e)
            {
                m_log.LogWarning($"{LogHeader} scaling the shape of a mesh asset threw ({e.GetType().Name}: {e.Message}); cooked at its own size.");
                return ShapeId.Invalid;
            }
        }

        // The size at which each mesh asset was last decoded by the mesher, which keeps that mesh for the life of the process.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, Vector3> _decodedAt = new System.Collections.Concurrent.ConcurrentDictionary<UUID, Vector3>();

        /// <summary>The smallest side (m) the mesher makes a mesh with; it raises a smaller one to this (Meshmerizer.MakeMesh).</summary>
        internal const float MesherMinSide = 0.01f;

        /// <summary>How many scaled shapes have been made over a cooked mesh shape (a resize or another size of the same asset).</summary>
        internal int ScaledShapesMade => Volatile.Read(ref _scaledShapesMade);
        private int _scaledShapesMade;

        private ShapeId CookMeshShapeCore(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, bool bodyIsPhysical,
                                          bool listWanted, bool cellsWanted, out string kind, out int hullCount, out double massVolume,
                                          out SVector3[][] cellPoints)
        {
            kind = BoxFallbackKind;
            hullCount = 1;
            massVolume = 0.0;
            cellPoints = null;
            bool isMesh = IsMeshPrim(pbs);

            // A non-physical mesh smaller than SmallMeshSide on every side is a box, which needs no asset. Halcyon's rule
            // (ShapeDeterminer.CanUseLowDetailShape: a static prim under 0.05 m on every side takes its low-detail shape);
            // the Second Life wiki says nothing about a size limit. A physical mesh is never boxed for its size.
            if (isMesh && !bodyIsPhysical && size.X < SmallMeshSide && size.Y < SmallMeshSide && size.Z < SmallMeshSide)
            {
                kind = "box(small mesh)";
                return backend.CreateBoxShape(new SVector3(size.X * 0.5f, size.Y * 0.5f, size.Z * 0.5f));
            }
            if (m_mesher == null)
            {
                m_log.LogWarning($"{LogHeader} no IMesher - cannot cook mesh; bounding-box fallback.");
                return ShapeId.Invalid;
            }

            // ---- Extract geometry. CRITICAL: the Meshmerizer CACHES and SHARES the Mesh object,
            // keyed on GetMeshKey(size, lod), and returns the SAME instance for every identical prim
            // (the key has no isPhysical/convex part; the one answer that depends on isPhysical, the 8-corner box
            // given to a small non-physical mesh, is kept in a cache of its own, so a physical request for the
            // same mesh is never handed that box). getIndexListAsInt()/getVertexListAsFloat() throw
            // NotSupportedException once m_triangles/m_vertices are null, and releaseSourceMeshData()
            // nulls exactly those - so calling it POISONS the cache and makes the NEXT identical prim's
            // extraction throw. Both accessors already return FRESH COPIES, so we own the arrays and must
            // NOT mutate/release the shared mesh (ReleaseMesh is a no-op anyway; the mesher owns eviction).
            // Everything the mesher/extraction can throw is inside ONE guard -> a clean bbox fallback,
            // never a propagating exception that could abort a prim rez or a whole region load.
            SVector3[] points;
            int[] indices;
            IMesh mesh;
            double meshVolume = 0.0;   // of the mesher's own mesh: what the cells are measured against (cellsWanted only)
            PrimHullCells.MeshAssessment assessment = null;   // what the same mesh says about the prim's real volume (cellsWanted only)
            try
            {
                // The mesher's isPhysical argument is true to mean "keep the real triangles": with false it replaces a
                // mesh whose three sides are all under 0.2 m by its 8-corner box (Meshmerizer.CreateMesh). A mesh prim
                // passes true, because the size rule is applied above, at Halcyon's 0.05 m. Another prim keeps passing
                // false, so its behaviour is as it was.
                // A mesh asset already decoded at another size is read at that size (the mesher keeps it) and its points scaled
                // to this one: a mesh's points are linear in the size, and the asset's bytes are gone once decoded, so asking
                // the mesher for a new size would mean fetching the asset again.
                Vector3 meshSize = size;
                if (isMesh && !pbs.SculptTexture.IsZero() && size.X >= MesherMinSide && size.Y >= MesherMinSide && size.Z >= MesherMinSide
                    && _decodedAt.TryGetValue(pbs.SculptTexture, out Vector3 decoded) && decoded != size)
                    meshSize = decoded;
                mesh = m_mesher.CreateMesh("jolt-prim", pbs, meshSize, MeshLod, isMesh, false, false);
                if (mesh == null && meshSize != size)
                {
                    meshSize = size;
                    mesh = m_mesher.CreateMesh("jolt-prim", pbs, size, MeshLod, isMesh, false, false);
                }
                if (mesh != null && isMesh && !pbs.SculptTexture.IsZero())
                    _decodedAt.TryAdd(pbs.SculptTexture, meshSize);
                if (mesh == null)
                {
                    // A sculpt whose asset (texture) has not been fetched meshes to null - it needs the
                    // async asset path (the request-asset delegate) first. Bounding box for now.
                    m_log.LogDebug($"{LogHeader} IMesher returned null (unfetched sculpt asset or empty geometry); bounding-box fallback.");
                    return ShapeId.Invalid;
                }

                indices = mesh.getIndexListAsInt();          // fresh copy - do NOT release the shared mesh
                float[] verts = mesh.getVertexListAsFloat(); // fresh copy (flattened x,y,z,...)
                if (verts == null || indices == null || verts.Length < 12 || indices.Length < 3 || (indices.Length % 3) != 0)
                {
                    m_log.LogWarning($"{LogHeader} mesher geometry unusable (verts={verts?.Length ?? 0}, indices={indices?.Length ?? 0}); bounding-box fallback.");
                    return ShapeId.Invalid;
                }

                if (cellsWanted)
                {
                    // What the cells are measured against is the prim's real mesh. For a prim under 0.2 m on every side the
                    // mesh above is its bounding box (the mesher is asked for a non-physical one), so ask for the real
                    // triangles; the mesher keeps both, and the one hull below is still made from the points above.
                    IMesh whole = m_mesher.CreateMesh("jolt-prim", pbs, size, MeshLod, true, false, false);
                    if (whole != null)
                    {
                        float[] wholeVertices = whole.getVertexListAsFloat();
                        int[] wholeIndices = whole.getIndexListAsInt();
                        meshVolume = PrimHullCells.MeshVolume(wholeVertices, wholeIndices);
                        // The assessment reads the mesh at the size it was made at; a prim is its unit shape scaled on each axis, so
                        // the checks hold at every size and the volume is the unit one times the size's three sides.
                        assessment = PrimHullCells.AssessMesh(wholeVertices, wholeIndices);
                    }
                }

                points = new SVector3[verts.Length / 3];
                var toSize = new SVector3(size.X / meshSize.X, size.Y / meshSize.Y, size.Z / meshSize.Z);
                for (int i = 0; i < points.Length; i++)
                    points[i] = meshSize == size
                        ? new SVector3(verts[3 * i], verts[3 * i + 1], verts[3 * i + 2])
                        : new SVector3(verts[3 * i] * toSize.X, verts[3 * i + 1] * toSize.Y, verts[3 * i + 2] * toSize.Z);

                // The mesher builds an inverted sculpt inside out, as the viewer draws it. The physics engine meets a
                // triangle from its front only, so an inside-out sculpt would let a ray or a body from outside pass
                // through it: put the triangles back the way round the unflagged shape has them.
                if (IsSculptPrim(pbs) && (pbs.SculptType & SculptInvertFlag) != 0)
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                        (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
            catch (Exception e)
            {
                m_log.LogWarning($"{LogHeader} mesher geometry extraction threw ({e.GetType().Name}: {e.Message}); bounding-box fallback.");
                return ShapeId.Invalid;
            }

            _lastMeshStats = CharacterizeMesh(points, indices);   // honest read-out of REAL mesher output

            // An out-of-range index would be a native out-of-bounds read in Jolt's Sanitize (the
            // backend rejects it too). Name the counts once and take the bbox fallback.
            if (_lastMeshStats.OutOfRangeIndices > 0)
            {
                m_log.LogWarning($"{LogHeader} mesher output has {_lastMeshStats.OutOfRangeIndices} out-of-range indices (verts={_lastMeshStats.Verts}, tris={_lastMeshStats.Tris}); bounding-box fallback.");
                return ShapeId.Invalid;
            }

            // A physical mesh whose asset carries a hull list collides as those hulls (Second Life's behaviour for a physical
            // mesh of shape type PRIM). An asset without one, or whose list cannot be used, falls through to one hull of the
            // mesh's points below.
            if (listWanted)
            {
                ShapeId list = CookHullList(backend, mesh, pbs.SculptTexture, size, out string listKind, out int listCount);
                if (list.IsValid)
                {
                    kind = listKind;
                    hullCount = listCount;
                    return list;
                }
            }

            // A physical prim of shape type PRIM that is not convex (hollow, cut, twisted, a torus, tube, ring...) collides as
            // a compound of convex cells. A prim that is one cell, and any failure, falls through to the one hull below, as
            // every physical prim always was.
            if (cellsWanted)
            {
                ShapeId cells = CookPrimCells(backend, pbs, size, meshVolume, assessment, out int cellCount, out double realVolume, out SVector3[][] cellSet);
                if (cells.IsValid)
                {
                    kind = $"{PrimCellsKind} x{cellCount})";
                    hullCount = cellCount;
                    massVolume = realVolume;
                    cellPoints = cellSet;
                    Interlocked.Increment(ref _primCellShapesCooked);
                    return cells;
                }
            }

            // Cook the Jolt shape. No shape/body exists until one of these RETURNS a handle, so a throw
            // here creates nothing to leak - caller falls back to a full bbox.
            try
            {
                ShapeId shape = isPhysical
                    ? backend.CreateConvexHullShape(points)   // physical: hull (mesh Volume=0 -> mass 0)
                    : backend.CreateMeshShape(points, indices); // non-physical: real triangle mesh
                kind = isPhysical ? "hull(mesher)" : "mesh(mesher)";
                if (isMesh)
                    Interlocked.Increment(ref _meshShapesCooked);
                return shape;
            }
            catch (Exception e)
            {
                m_log.LogWarning($"{LogHeader} backend cook of mesher output threw ({e.GetType().Name}: {e.Message}); bounding-box fallback.");
                return ShapeId.Invalid;   // kind stays "bbox(fallback)"
            }
        }

        // ---------------------------------------------------------------------
        // Mesh assets. A mesh prim's shape comes from its asset, which nothing in core loads for the physics engine: a prim
        // whose bytes are not in its shape and whose mesh the mesher has not cached cooks as its bounding box, asks the
        // scene's request-asset delegate for the asset (as ubODE does, ODEMeshWorker.cs, and BulletSim, BSShapes.cs), and is
        // cooked again when the asset arrives. Prims waiting for one asset share one request. The bytes are put on a copy
        // of the prim's shape for the cook only, and dropped with it.
        // ---------------------------------------------------------------------

        /// <summary>How many shapes have been cooked natively for mesh prims (a mesh, a hull or a hull list: one count each,
        /// whatever the number of hulls in it). A shape shared by several prims is cooked, and counted, once.</summary>
        internal int MeshShapesCooked => Volatile.Read(ref _meshShapesCooked);
        private int _meshShapesCooked;

        /// <summary>How many cooked mesh shapes the scene holds for sharing between prims.</summary>
        internal int SharedShapeCount { get { lock (_sharedShapeLock) return _sharedShapes.Count; } }

        // ---------------------------------------------------------------------
        // Shared shapes. Prims of one mesh asset, one size and one way of cooking (triangle mesh, one hull, hull list) take one
        // cooked shape between them. The scene counts the prims using each (Users); the shape is released to the backend when
        // the last gives it up. Every release of a shape a prim holds goes through ReleaseShape below. A shape holds no state
        // of one prim except the density a linkset gives its parts (SetShapeDensity), so a prim that joins a linkset first
        // takes a shape of its own (JoltPrim.MakeShapePrivate).
        // ---------------------------------------------------------------------

        internal enum SharedShapeKind { Mesh, Hull, HullList, PrimCells }

        private readonly struct SharedShapeKey : IEquatable<SharedShapeKey>
        {
            private readonly UUID _asset;
            private readonly string _parameters;   // a prim made from its own profile and path: its shape parameters; else null
            private readonly int _x, _y, _z;      // the size's bits: only an identical size shares
            private readonly SharedShapeKind _kind;

            public SharedShapeKey(UUID asset, Vector3 size, SharedShapeKind kind, string parameters = null)
            {
                _asset = asset;
                _parameters = parameters;
                _x = BitConverter.SingleToInt32Bits(size.X);
                _y = BitConverter.SingleToInt32Bits(size.Y);
                _z = BitConverter.SingleToInt32Bits(size.Z);
                _kind = kind;
            }

            public bool Equals(SharedShapeKey o) => _asset == o._asset && _x == o._x && _y == o._y && _z == o._z && _kind == o._kind
                                                    && string.Equals(_parameters, o._parameters, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is SharedShapeKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(_asset, _x, _y, _z, _kind, _parameters);
        }

        private sealed class SharedShape
        {
            public SharedShapeKey Key;
            public ShapeId Shape;
            public string Kind;
            public int HullCount;
            public double MassVolume;   // the real volume of a shape of cells at this size (see CookPrimCells); 0 when its own volume is used
            public int Users;
            public BaseShape Basis;   // the cooked shape this one is, or is scaled from; null when it is neither
        }

        // The shape cooked for a mesh asset (at the size of the first prim that needed one), which every other size of the same
        // asset and kind is a scaled shape over. Kept while any size of it is in use.
        private readonly struct BaseKey : IEquatable<BaseKey>
        {
            private readonly UUID _asset;
            private readonly SharedShapeKind _kind;
            private readonly string _parameters;
            public BaseKey(UUID asset, SharedShapeKind kind, string parameters = null) { _asset = asset; _kind = kind; _parameters = parameters; }
            public bool IsPrimCells => _kind == SharedShapeKind.PrimCells;
            public bool Equals(BaseKey o) => _asset == o._asset && _kind == o._kind && string.Equals(_parameters, o._parameters, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is BaseKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(_asset, _kind, _parameters);
        }

        private sealed class BaseShape
        {
            public BaseKey Key;
            public ShapeId Shape;
            public Vector3 Size;
            public string Kind;
            public int HullCount;
            public double UnitMassVolume;   // for a shape of cells: the real volume at unit size (see CookPrimCells)
            public SVector3[][] UnitCells;  // and the points of its cells at unit size, from which a prim's own compound is made
            public int Entries;   // sizes (SharedShape entries) in use
        }

        private readonly Dictionary<BaseKey, BaseShape> _bases = new Dictionary<BaseKey, BaseShape>();

        private readonly object _sharedShapeLock = new object();
        private readonly Dictionary<SharedShapeKey, SharedShape> _sharedShapes = new Dictionary<SharedShapeKey, SharedShape>();
        private readonly Dictionary<uint, SharedShape> _sharedByShape = new Dictionary<uint, SharedShape>();

        /// <summary>Gives back a shape a prim holds: a shared one is released to the backend when its last prim gives it up,
        /// any other at once.</summary>
        internal void ReleaseShape(IPhysicsBackend backend, ShapeId shape)
        {
            if (!shape.IsValid)
                return;
            lock (_sharedShapeLock)
            {
                if (_sharedByShape.TryGetValue(shape.Value, out SharedShape held))
                {
                    if (--held.Users > 0)
                        return;
                    _sharedByShape.Remove(shape.Value);
                    _sharedShapes.Remove(held.Key);
                    if (held.Basis != null && --held.Basis.Entries <= 0)
                    {
                        _bases.Remove(held.Basis.Key);
                        backend.ReleaseShape(held.Basis.Shape);   // the table's own reference
                    }
                }
            }
            backend.ReleaseShape(shape);
        }

        /// <summary>True when the shape is one the scene shares between prims.</summary>
        internal bool IsSharedShape(ShapeId shape)
        {
            lock (_sharedShapeLock)
                return shape.IsValid && _sharedByShape.ContainsKey(shape.Value);
        }

        // One notice per asset and reason about a hull list that cannot be used as it is. Logged, and kept for the tests.
        private void HullNotice(UUID asset, string reason, string text)
        {
            string line = $"{LogHeader} mesh asset {asset}: {text}";
            lock (_hullNotices)
            {
                if (!_hullNoticeKeys.Add(asset + "|" + reason))
                    return;
                if (_hullNotices.Count < 1000)
                    _hullNotices.Add(line);
            }
            m_log.LogWarning(line);
        }

        /// <summary>The kind of a shape cooked from a concave prim's convex cells, up to the number of cells: "hulls(prim x12)".</summary>
        internal const string PrimCellsKind = "hulls(prim";

        /// <summary>How many shapes have been cooked as convex cells for prims made from their own profile and path.</summary>
        internal int PrimCellShapesCooked => Volatile.Read(ref _primCellShapesCooked);
        private int _primCellShapesCooked;

        // A prim made from its own profile and path as a compound of convex cells (PrimHullCells): one hull for each convex
        // piece of its profile over each span of its path, cut from the rings the mesher extrudes it from. Returns
        // ShapeId.Invalid, and the caller cooks one hull of the mesh's points, when the prim has no rings, is one cell (it is
        // convex already), or no split can be cooked. A cell the hull builder refuses is never left out: a missing cell would
        // leave a hole in a wall, so the whole prim keeps the one hull.
        //
        // This runs where every shape is cooked (the thread that adds the prim or changes its shape, never the step: see
        // JoltPrim.CookOwnShape). The cell points come from the mesher and the native hull builds take no lock the step holds,
        // so a step in progress never waits for it; the one call here that takes the step's lock is GetShapeVolume, which
        // is a read of a number and which waits for the step, not the other way round.
        //
        // `realVolume` is what the prim weighs per unit of density: the volume of the mesher's closed mesh when
        // PrimHullCells.AssessMesh trusts it and it is not above the cells' (the cells hold the whole solid, so a mesh volume
        // above theirs is a mesh that lies); otherwise 0, and the cells' own volume is the weight. A prim that keeps the
        // cells' volume is named once for each reason.
        private ShapeId CookPrimCells(IPhysicsBackend backend, PrimitiveBaseShape pbs, Vector3 size, double meshVolume,
                                      PrimHullCells.MeshAssessment assessment, out int cellCount, out double realVolume, out SVector3[][] cellPoints)
        {
            cellCount = 1;
            realVolume = 0.0;
            cellPoints = null;
            // The mesher is asked for its shape data through the interface, not its class: the region server loads the mesher in a
            // context of its own, where the class this module was built against is another type (see IMesherShapeData).
            if (!(m_mesher is IMesherShapeData meshmerizer))
            {
                PrimCellsNotice("mesher", $"the mesher ({m_mesher?.GetType().FullName ?? "none"}) gives no shape data; physical prims that are not convex keep one hull.");
                return ShapeId.Invalid;
            }
            try
            {
                if (!meshmerizer.TryGetPrimProfileRings("jolt-prim", pbs, size, MeshLod, out PrimProfileRings rings))
                    return ShapeId.Invalid;
                PrimHullCellSet set = PrimHullCells.Build(backend, rings, meshVolume);
                if (!set.Success)
                {
                    PrimCellsNotice("build", $"a physical prim could not be split into convex cells ({set.Failure}); one hull of its points.");
                    return ShapeId.Invalid;
                }
                if (set.CellCount < 2)
                    return ShapeId.Invalid;     // convex already: the one hull is exactly it

                ShapeId compound = PrimHullCells.CookCompound(backend, set.Cells, out int failed);
                if (failed > 0 || !compound.IsValid)
                {
                    PrimCellsNotice("cook", $"{failed} of {set.CellCount} convex cells of a physical prim could not be built; one hull of its points.");
                    if (compound.IsValid)
                        backend.ReleaseShape(compound);
                    return ShapeId.Invalid;
                }
                cellCount = set.CellCount;
                realVolume = RealVolumeOf(assessment, set.CellsVolume);
                cellPoints = set.Cells;
                return compound;
            }
            catch (Exception e)
            {
                PrimCellsNotice("threw", $"splitting a physical prim into convex cells threw ({e.GetType().Name}: {e.Message}); one hull of its points.");
                return ShapeId.Invalid;
            }
        }

        /// <summary>A mesh volume above the cells' volume by more than this fraction is not believed: the cells hold the whole solid.</summary>
        internal const double MeshOverCellsTolerance = 0.005;

        // The weight volume of a prim cooked as cells, from the mesher's mesh, or 0 when the cells' volume is to be used.
        internal double RealVolumeOf(PrimHullCells.MeshAssessment assessment, double cellsVolume)
        {
            if (assessment == null)
                return 0.0;
            if (!assessment.Trusted)
            {
                PrimCellsNotice("mass-" + assessment.Reason, $"a physical prim's mesh volume cannot be trusted ({MeshReasonText(assessment.Reason)}); it weighs the volume of its convex cells.");
                return 0.0;
            }
            if (assessment.SignedVolume > cellsVolume * (1.0 + MeshOverCellsTolerance))
            {
                PrimCellsNotice("mass-over-cells", "a physical prim's mesh volume is above that of its convex cells, which hold the whole solid; it weighs the volume of its cells.");
                return 0.0;
            }
            return assessment.SignedVolume;
        }

        private static string MeshReasonText(string reason)
            => reason == "self-crossing" ? "its surface crosses itself" : reason == "open" ? "its surface has a hole" : "its surface encloses no volume or winds inside out";

        // One notice per reason about a prim that kept its one hull for want of cells. Logged, and kept for the tests.
        private void PrimCellsNotice(string reason, string text)
        {
            string line = $"{LogHeader} {text}";
            lock (_hullNotices)
            {
                if (!_hullNoticeKeys.Add("prim|" + reason))
                    return;
                if (_hullNotices.Count < 1000)
                    _hullNotices.Add(line);
            }
            m_log.LogWarning(line);
        }

        // The asset's hull list as one shape: a compound of its hulls (one hull is the hull itself), each at the prim's scale.
        // Returns ShapeId.Invalid, and the caller cooks one hull of the mesh's points, when the asset has no list, a list
        // over the viewer's limits, or no hull in it that can be built. A hull of fewer than four points, or one Jolt cannot
        // build, is left out. Nothing reaches the native code unless it has passed these checks.
        private ShapeId CookHullList(IPhysicsBackend backend, IMesh mesh, UUID asset, Vector3 size, out string kind, out int hullCount)
        {
            kind = BoxFallbackKind;
            hullCount = 1;
            List<List<Vector3>> hulls;
            try { hulls = (m_mesher as IMesherShapeData)?.GetConvexHulls(mesh, size); }
            catch (Exception e)
            {
                m_log.LogWarning($"{LogHeader} reading the hull list of mesh asset {asset} threw ({e.GetType().Name}: {e.Message}); one hull of the points.");
                return ShapeId.Invalid;
            }
            if (hulls == null || hulls.Count == 0)
                return ShapeId.Invalid;   // an asset with no hull list: the points hull, as for any physical mesh

            if (!HullListWithinLimits(hulls))
            {
                int most = 0;
                foreach (List<Vector3> h in hulls) most = Math.Max(most, h.Count);
                HullNotice(asset, "limit", $"hull list of {hulls.Count} hulls, the largest of {most} points, is over the {MaxAssetHulls}-hull, {MaxPointsPerAssetHull}-point limit of the viewer's upload; one hull of the points.");
                return ShapeId.Invalid;
            }

            var ids = new List<ShapeId>(hulls.Count);
            int skipped = 0;
            foreach (List<Vector3> hull in hulls)
            {
                if (hull.Count < 4) { skipped++; continue; }
                var points = new SVector3[hull.Count];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new SVector3(hull[i].X, hull[i].Y, hull[i].Z);
                try { ids.Add(backend.CreateConvexHullShape(points)); }
                catch (Exception) { skipped++; }   // not finite, coplanar, collinear...: Jolt's own check, before any native hull exists
            }

            if (ids.Count == 0)
            {
                HullNotice(asset, "none", $"none of the {hulls.Count} hulls in its hull list can be used (fewer than 4 points, or Jolt cannot build them); one hull of the points.");
                return ShapeId.Invalid;
            }
            if (skipped > 0)
                HullNotice(asset, "skipped", $"{skipped} of {hulls.Count} hulls in its hull list skipped (fewer than 4 points, or Jolt cannot build them); the other {ids.Count} are used.");

            kind = $"{HullListKind} x{ids.Count})";
            hullCount = ids.Count;
            if (ids.Count == 1)
            {
                Interlocked.Increment(ref _meshShapesCooked);
                return ids[0];
            }
            try
            {
                ShapeId compound = backend.CreateHullCompoundShape(ids.ToArray());
                Interlocked.Increment(ref _meshShapesCooked);
                return compound;
            }
            catch (Exception e)
            {
                HullNotice(asset, "compound", $"its {ids.Count} hulls could not be joined into one shape ({e.GetType().Name}: {e.Message}); one hull of the points.");
                kind = BoxFallbackKind;
                hullCount = 1;
                return ShapeId.Invalid;
            }
            finally
            {
                // The compound holds its own reference to each hull. Nothing is left to release when it was one hull.
                if (ids.Count > 1)
                    foreach (ShapeId id in ids)
                        backend.ReleaseShape(id);
            }
        }

        /// <summary>The kind of a shape cooked from an asset's hull list, up to the number of hulls: "hulls(asset x3)".</summary>
        internal const string HullListKind = "hulls(asset";

        // Each notice about an asset's hull list that was logged (one per asset and reason), for the tests.
        private readonly List<string> _hullNotices = new List<string>();
        private readonly HashSet<string> _hullNoticeKeys = new HashSet<string>();
        internal string[] HullNotices { get { lock (_hullNotices) return _hullNotices.ToArray(); } }

        // The limits the Second Life viewer's upload puts on a convex decomposition: at most 256 hulls, of at most 256
        // points each (indra/llphysicsextensionsos/llconvexdecompositionvhacd.cpp MAX_HULLS and MAX_VERTICES_PER_HULL; the
        // uploader warns above 256 points in a hull, indra/newview/llmodelpreview.cpp). An asset over either is not trusted.
        internal const int MaxAssetHulls = 256;
        internal const int MaxPointsPerAssetHull = 256;

        internal static bool HullListWithinLimits(List<List<OpenMetaverse.Vector3>> hulls)
        {
            if (hulls.Count > MaxAssetHulls)
                return false;
            foreach (List<OpenMetaverse.Vector3> hull in hulls)
                if (hull.Count > MaxPointsPerAssetHull)
                    return false;
            return true;
        }

        /// <summary>The shape kind of a prim cooked as its bounding box.</summary>
        internal const string BoxFallbackKind = "bbox(fallback)";

        // A non-physical mesh smaller than this (m) on every side is cooked as a box.
        internal const float SmallMeshSide = 0.05f;

        // How long a failed asset is left alone before the next prim that needs it asks again (a grid's asset service can
        // be down for a while; the prims are still boxes meanwhile).
        internal const long MeshAssetRetryMs = 10 * 60 * 1000;

        /// <summary>The clock the retry wait reads, in milliseconds. A test sets it.</summary>
        internal Func<long> MeshAssetClockMs = () => Environment.TickCount64;

        /// <summary>How many assets have been logged as missing or unreadable (one line per asset).</summary>
        internal int MeshAssetFailuresLogged => Volatile.Read(ref _meshAssetFailuresLogged);
        private int _meshAssetFailuresLogged;

        private readonly object _meshAssetLock = new object();
        private readonly Dictionary<UUID, List<JoltPrim>> _meshAssetWaiting = new Dictionary<UUID, List<JoltPrim>>();
        private readonly Dictionary<UUID, long> _meshAssetFailedAt = new Dictionary<UUID, long>();
        private readonly HashSet<UUID> _meshAssetFailureLogged = new HashSet<UUID>();

        internal static bool IsMeshPrim(PrimitiveBaseShape pbs) =>
            pbs != null && pbs.SculptEntry && pbs.SculptType == (byte)SculptType.Mesh;

        /// <summary>The bit of a sculpt's type byte that draws it inside out (the mirror bit is 128).</summary>
        internal const byte SculptInvertFlag = 64;

        // A sculpted prim (a sculpt map, a JPEG 2000 texture) is read by the same mesher from the same field as a mesh, so it
        // is fetched the same way.
        internal static bool IsSculptPrim(PrimitiveBaseShape pbs) =>
            pbs != null && pbs.SculptEntry && pbs.SculptType != (byte)SculptType.Mesh;

        /// <summary>True when a mesh or sculpted prim's shape cannot be cooked until its asset is fetched and the scene can fetch it.</summary>
        internal bool WantsMeshAsset(PrimitiveBaseShape pbs) =>
            m_mesher != null && RequestAssetMethod != null && (IsMeshPrim(pbs) || IsSculptPrim(pbs)) && !pbs.SculptTexture.IsZero()
            && (pbs.SculptData == null || pbs.SculptData.Length == 0);

        /// <summary>Ask for a mesh asset for a prim, unless it is on its way for another prim (the prim joins them) or was
        /// found missing or unreadable a short while ago (the prim stays a box).</summary>
        internal void RequestMeshAsset(JoltPrim prim, UUID assetId)
        {
            RequestAssetDelegate request = RequestAssetMethod;
            if (request == null || assetId.IsZero())
                return;
            lock (_meshAssetLock)
            {
                if (_meshAssetFailedAt.TryGetValue(assetId, out long failedAt))
                {
                    if (MeshAssetClockMs() - failedAt < MeshAssetRetryMs)
                        return;
                    _meshAssetFailedAt.Remove(assetId);
                }
                if (_meshAssetWaiting.TryGetValue(assetId, out List<JoltPrim> waiting))
                {
                    if (!waiting.Contains(prim))
                        waiting.Add(prim);
                    return;
                }
                _meshAssetWaiting[assetId] = new List<JoltPrim> { prim };
            }
            try
            {
                request(assetId, asset => MeshAssetReceived(assetId, asset));
            }
            catch (Exception e)
            {
                MeshAssetFailed(assetId, $"the request threw ({e.GetType().Name}: {e.Message})");
            }
        }

        // The answer to a request, on whatever thread the asset service uses. Each prim still waiting, and still holding
        // this mesh, is cooked from the bytes; a prim removed meanwhile is left alone.
        private void MeshAssetReceived(UUID assetId, AssetBase asset)
        {
            List<JoltPrim> waiting;
            lock (_meshAssetLock)
                _meshAssetWaiting.Remove(assetId, out waiting);
            if (waiting == null || _backend == null)
                return;

            byte[] data = asset?.Data;
            if (data == null || data.Length == 0)
            {
                MeshAssetFailed(assetId, asset == null ? "the asset service has no such asset" : "the asset has no data");
                return;
            }

            bool anyCooked = false, anyBox = false;
            foreach (JoltPrim prim in waiting)
            {
                try
                {
                    switch (prim.MeshAssetArrived(assetId, data))
                    {
                        case JoltPrim.MeshArrival.Cooked: anyCooked = true; break;
                        case JoltPrim.MeshArrival.Unreadable: anyBox = true; break;
                    }
                }
                catch (Exception e)
                {
                    m_log.LogWarning($"{LogHeader} cooking mesh asset {assetId} for prim {prim.LocalID} threw ({e.GetType().Name}: {e.Message}); it stays a bounding box.");
                }
            }
            if (anyBox && !anyCooked)
                MeshAssetFailed(assetId, "its mesh could not be read");
        }

        private void MeshAssetFailed(UUID assetId, string why)
        {
            bool first;
            lock (_meshAssetLock)
            {
                _meshAssetWaiting.Remove(assetId);
                _meshAssetFailedAt[assetId] = MeshAssetClockMs();
                first = _meshAssetFailureLogged.Add(assetId);
            }
            if (!first)
                return;
            Interlocked.Increment(ref _meshAssetFailuresLogged);
            m_log.LogWarning($"{LogHeader} mesh asset {assetId}: {why}; prims that use it stay bounding boxes.");
        }

        // Characterize RAW mesher output (counts, bounds, enclosed volume). Duplicate-vertex count uses mm-quantized coords (O(n)); degenerate = topological
        // (shared index) or near-zero area.
        private static MeshStats CharacterizeMesh(SVector3[] points, int[] indices)
        {
            var s = new MeshStats { Verts = points.Length, Tris = indices.Length / 3 };
            var min = new SVector3(float.MaxValue); var max = new SVector3(float.MinValue);
            foreach (var p in points) { min = SVector3.Min(min, p); max = SVector3.Max(max, p); }
            s.Min = min; s.Max = max;

            var seen = new HashSet<(int, int, int)>();
            foreach (var p in points)
                seen.Add(((int)MathF.Round(p.X * 1000f), (int)MathF.Round(p.Y * 1000f), (int)MathF.Round(p.Z * 1000f)));
            s.DuplicateVerts = points.Length - seen.Count;

            double vol6 = 0.0;   // 6x the signed enclosed volume: sum of dot(v0, cross(v1,v2)) over tris
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                bool bad = a < 0 || b < 0 || c < 0 || a >= points.Length || b >= points.Length || c >= points.Length;
                if (bad) { s.OutOfRangeIndices++; continue; }
                if (a == b || b == c || a == c) { s.DegenerateTris++; continue; }
                float area2 = SVector3.Cross(points[b] - points[a], points[c] - points[a]).Length();
                if (area2 < 1e-9f) s.DegenerateTris++;
                vol6 += SVector3.Dot(points[a], SVector3.Cross(points[b], points[c]));
            }
            // For a closed, consistently-wound mesh (prim mesher output) this is the exact enclosed volume,
            // which equals the convex-hull volume for a convex shape (prism) - i.e. the physical hull mass basis.
            s.Volume = (float)(Math.Abs(vol6) / 6.0);
            return s;
        }

        // BulletSim's cut test, verbatim: an un-cut basic shape has no profile/path cut, hollow, twist,
        // taper, non-100 path scale, or shear. (PathScaleX/Y are stored as 100 = "1.0".)
        private static bool PrimHasNoCuts(PrimitiveBaseShape p) =>
            p.ProfileBegin == 0 && p.ProfileEnd == 0 && p.ProfileHollow == 0 &&
            p.PathTwist == 0 && p.PathTwistBegin == 0 && p.PathBegin == 0 && p.PathEnd == 0 &&
            p.PathTaperX == 0 && p.PathTaperY == 0 && p.PathScaleX == 100 && p.PathScaleY == 100 &&
            p.PathShearX == 0 && p.PathShearY == 0;

        private static bool Approx(float a, float b) =>
            Math.Abs(a - b) <= 1e-4f * Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));

        // ---------------------------------------------------------------------
        // Query wiring: this is the path a SCRIPT llCastRay takes.
        // llCastRay -> Scene.RayCastFiltered -> PhysicsScene.RaycastWorld (here) -> backend.RayCast.
        // Returning true from SupportsRaycastWorldFiltered flips llCastRay onto the physics engine
        // instead of OpenSim's own geometry intersection, so a script ray genuinely tests Jolt's
        // shapes. (The rest of the query family - RaycastActor, Sphere/BoxProbe - is not overridden here.)
        // ---------------------------------------------------------------------

        // llCastRay runs on SCRIPT threads, so routing it here issues a native Jolt NarrowPhaseQuery off
        // the heartbeat thread, concurrent with _system.Update. That is SAFE: the backend's RayCast/
        // RayCastAll take _simLock, the same gate that wraps the whole Step (Update + ExtendedUpdate), so a
        // script raycast and the physics step can never be inside Jolt's non-thread-safe LIFO TempAllocator
        // at once. The lock, not disabling the feature, is what makes this safe. Returning true keeps llCastRay
        // testing Jolt's real cooked shapes rather than falling back to OpenSim's own geometry intersection.
        public override bool SupportsRaycastWorldFiltered() => true;

        // TWO llCastRay entry points route here, and BOTH must be overridden or llCastRay returns 0:
        //  - the 5-arg (RayFilterFlags) overload is what OpenSim's XEngine/YEngine LSL_Api.llCastRay calls
        //    (it maps RC_* -> RayFilterFlags, then we -> QueryFilter);
        //  - the 4-arg (no filter) overload is what a Halcyon-derived llCastRay calls - it does the
        //    reject-physical/agent/land TYPE filtering on the returned list itself, so we hand it ALL solid
        //    layers + avatars (QueryFilter.Default = Terrain|Static|Dynamic|Avatar; phantom/Sensor excluded,
        //    which that caller neither requests nor filters). Without this override every such cast falls
        //    through to the base (empty list) = 0 hits.
        //
        // What a cast costs is bounded (the backend's RayCastLimited, [Jolt] RayCastBudgetMs / RayCastSimulatorBudgetMs /
        // RayCastMaxTestedHits / RayCastMaxHits). A cast the region has no time left for, or that ran out of it, fails as
        // Second Life documents: "RCERR_CAST_TIME_EXCEEDED -3: The raycast failed because the parcel or agent has
        // exceeded the maximum time allowed for raycasting. This resource pool is continually replenished, so waiting a
        // few frames and retrying is likely to succeed." (llCastRay, wiki.secondlife.com). The 4-argument caller (Phlox)
        // turns an exception from this call into that status code, so this overload throws RayCastTimeExceededException.
        // The 5-argument callers have no way to be told, so they get no hits.
        //
        // The two entry points are charged to separate budgets, so running out of one never refuses a cast on the other:
        //  - the 4-argument entry is called only by a script's llCastRay (Phlox): RayCastBudget.Script;
        //  - the 5-argument entry is called by the simulator's rez placement (Scene.GetNewRezLocation) and its landing
        //    ray after login or teleport (ScenePresence.MakeRootAgent), and by OpenSim's LSL_Api.llCastRay (YEngine)
        //    through Scene.RayCastFiltered. Its flags and hit count do not tell a script's cast from the simulator's (a
        //    script can ask for exactly the same ones), so it is charged to RayCastBudget.Simulator; see BudgetFor.
        // The viewer camera's collision ray and the sit ray do not come here: this module does not override
        // SupportsRayCast() or the sit query, so the base class answers them.
        public override List<ContactResult> RaycastWorld(Vector3 position, Vector3 direction, float length, int Count)
        {
            List<ContactResult> results = CastAll(position, direction, length, Count, QueryFilter.Default,
                                                  _warmingUpRays ? RayCastBudget.None : RayCastBudget.Script, out RayCastStatus status);
            if (status != RayCastStatus.Ok)
                throw new RayCastTimeExceededException(status);
            return results;
        }

        public override object RaycastWorld(Vector3 position, Vector3 direction, float length, int Count, RayFilterFlags filter)
            => CastAll(position, direction, length, Count, ToQueryFilter(filter), _warmingUpRays ? RayCastBudget.None : BudgetFor(filter), out _);

        // The budget a cast on the 5-argument entry is charged to. Nothing in RayFilterFlags marks a script's cast today,
        // so every such cast goes to the simulator's budget. When the caller can mark a script cast (a flag set by
        // LSL_Api.llCastRay), this is the one place to send it to RayCastBudget.Script instead.
        internal static RayCastBudget BudgetFor(RayFilterFlags filter) => RayCastBudget.Simulator;

        /// <summary>Tells core that a physical prim's actor turns its body toward the rotation target it is given
        /// (JoltPrim.AngularTarget.cs), so core hands llRotLookAt, llLookAt and the rotate-drag to the actor instead of
        /// steering the body's angular velocity itself.</summary>
        public override bool SupportsRotationTarget() => true;

        /// <summary>
        /// The clock the ray cast budgets are measured with (PhysicsBackendSettings.RayCastClock); null is the wall
        /// clock. Tests set it before the region is initialised.
        /// </summary>
        internal TimeProvider RayCastClock { get; set; }

        /// <summary>How long the warm-up casts took when this region loaded (ms).</summary>
        internal double RayWarmUpMs { get; private set; }

        // The first ray cast in a process pays one-time costs: the JIT of the cast path here and in the backend, the
        // binding of the native cast call, and the first use of the binding's layer filter, surface normal and sort
        // code. Measured on a Release build: about 3-4 ms inside the backend (4-8 ms in all) for the first cast and
        // 0.2 ms for the second, against a few microseconds for every later one. The first casts after a start are the
        // landing casts of the first login, charged to the simulator's budget ([Jolt] RayCastSimulatorBudgetMs), so
        // they could run that budget out within one heartbeat. These casts pay the costs while the region loads,
        // through the same path as a real cast (both entry points, a hit on the terrain, a sort), charged to no budget
        // and counted nowhere: while _warmingUpRays is set, both entry points charge RayCastBudget.None. The process pays them once: the first region's warm-up takes about 8 ms, a
        // later region's about 0.02 ms.
        private double WarmUpRayCasts()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var above = new Vector3(_regionSizeX * 0.5f, _regionSizeY * 0.5f, RayClipMaxZ - 1f);
            var down = new Vector3(0f, 0f, -1f);
            float length = RayClipMaxZ - RayClipMinZ;
            _warmingUpRays = true;
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    try { RaycastWorld(above, down, length, 2); }
                    catch (RayCastTimeExceededException) { }   // a warm-up cast cut short at the time limit: nothing to report
                    RaycastWorld(above, down, length, 2, RayFilterFlags.BackFaceCull | RayFilterFlags.PrimsNonPhantomAgents);
                    BudgetFor(RayFilterFlags.BackFaceCull);   // the 5-argument entry's budget choice, skipped above
                }
            }
            finally { _warmingUpRays = false; }
            return (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        // Set only while WarmUpRayCasts runs, on the thread that loads the region, before any other cast can arrive.
        private bool _warmingUpRays;

        // Second Life: "The random failures seem to happen if the ray begins or ends more than 8 meters outside of
        // current region bounds" (llCastRay). A ray is cut to the region widened by this much on each side, and to the
        // heights a region holds objects at, so a script cannot hand the engine a ray kilometres long.
        internal const float RayClipMargin = 8f;
        internal const float RayClipMinZ = -128f;
        internal const float RayClipMaxZ = 10000f;

        internal List<ContactResult> CastAll(Vector3 position, Vector3 direction, float length, int Count, QueryFilter qf, RayCastBudget budget,
                                             out RayCastStatus status)
        {
            status = RayCastStatus.Ok;
            var results = new List<ContactResult>();
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null || qf == QueryFilter.None || !(length > 0f) || !float.IsFinite(length) || !position.IsFinite() || !direction.IsFinite())
                return results;

            Vector3 dn = direction;
            dn.Normalize();
            if (!dn.IsFinite() || dn == Vector3.Zero)
                return results;
            if (!ClipRay(position, dn, length, out float from, out float to))
                return results;
            Vector3 start = position + dn * from;
            var origin = new SVector3(start.X, start.Y, start.Z);
            var dir = new SVector3(dn.X, dn.Y, dn.Z);

            int want = Math.Clamp(Count, 1, _joltConfig.RayCastMaxHits);
            var hits = new RayHit[want];
            int n = backend.RayCastLimited(origin, dir, to - from, qf, budget, hits, out status);
            for (int i = 0; i < n; i++)
            {
                var cr = new ContactResult
                {
                    // The struck part's SceneObjectPart.LocalId (0 = terrain): a linkset's child where the ray hit it, not the
                    // root whose body it is welded into. The script engines look the part up by it and replace it with the
                    // root only when asked: RC_GET_ROOT_KEY replaces the hit's uuid with its object's root, and RC_GET_LINK_NUM
                    // adds the number of the link that was hit (llCastRay, wiki.secondlife.com/wiki/LlCastRay).
                    ConsumerID = hits[i].ChildUserData != 0 ? hits[i].ChildUserData : hits[i].UserData,
                    Pos = new Vector3(hits[i].Point.X, hits[i].Point.Y, hits[i].Point.Z),
                    Normal = new Vector3(hits[i].Normal.X, hits[i].Normal.Y, hits[i].Normal.Z),
                    Depth = from + hits[i].Distance,         // from the caller's start, not the clipped one
                };
                results.Add(cr);
            }
            return results;
        }

        /// <summary>The backend's counters (default when the region has no backend): the harness and tests read them.</summary>
        internal PhysicsCapacityStats CapacityStats() => _backend?.GetCapacityStats() ?? default;

        /// <summary>The region's backend (null before it is initialised or after teardown): tests compare against it.</summary>
        internal IPhysicsBackend Backend => _backend;

        // The part [from, to] of the ray (start p, unit direction d, length len) inside the region's box widened by
        // RayClipMargin, between RayClipMinZ and RayClipMaxZ. False when no part of it is.
        internal bool ClipRay(Vector3 p, Vector3 d, float len, out float from, out float to)
        {
            from = 0f;
            to = len;
            return Slab(p.X, d.X, -RayClipMargin, _regionSizeX + RayClipMargin, ref from, ref to)
                && Slab(p.Y, d.Y, -RayClipMargin, _regionSizeY + RayClipMargin, ref from, ref to)
                && Slab(p.Z, d.Z, RayClipMinZ, RayClipMaxZ, ref from, ref to);
        }

        private static bool Slab(float p, float d, float min, float max, ref float from, ref float to)
        {
            if (MathF.Abs(d) < 1e-9f)
                return p >= min && p <= max;
            float a = (min - p) / d, b = (max - p) / d;
            if (a > b) (a, b) = (b, a);
            from = MathF.Max(from, a);
            to = MathF.Min(to, b);
            return from < to;
        }

        // llCastRay's reject-type flags -> our layer filter. water has no body; phantom/volumedetect
        // map to the Sensor layer.
        private static QueryFilter ToQueryFilter(RayFilterFlags f)
        {
            QueryFilter q = QueryFilter.None;
            if ((f & RayFilterFlags.land) != 0) q |= QueryFilter.Terrain;
            if ((f & RayFilterFlags.nonphysical) != 0) q |= QueryFilter.Static;
            if ((f & RayFilterFlags.physical) != 0) q |= QueryFilter.Dynamic;
            if ((f & RayFilterFlags.agent) != 0) q |= QueryFilter.Avatar;
            if ((f & (RayFilterFlags.phantom | RayFilterFlags.volumedtc)) != 0) q |= QueryFilter.Sensor;
            return q;
        }


        // Linkset roots whose compound needs a (re)build, coalesced and applied once per frame in
        // Simulate. link()/unlink() add to this instead of rebuilding inline (which can hang a region load).
        private readonly HashSet<JoltPrim> _dirtyLinksets = new HashSet<JoltPrim>();

        // ---------------------------------------------------------------------
        // Vehicles: active vehicle prims, driven per-frame from Simulate BEFORE the physics
        // step (the Jolt equivalent of BulletSim's BeforeStep event). JoltPrim registers itself when
        // its controller's type is set and unregisters on TYPE_NONE/destroy.
        // ---------------------------------------------------------------------
        private readonly HashSet<JoltPrim> _vehicles = new HashSet<JoltPrim>();

        internal void RegisterVehicle(JoltPrim prim)
        {
            lock (_vehicles)
            {
                if (!prim.IsRemoved) _vehicles.Add(prim);   // checked under the lock Destroy's removal takes, after IsRemoved is set
            }
        }

        internal void UnregisterVehicle(JoltPrim prim)
        {
            lock (_vehicles) _vehicles.Remove(prim);
        }

        private void StepVehicles(float timeStep)
        {
            JoltPrim[] vehicles;
            lock (_vehicles)
            {
                if (_vehicles.Count == 0) return;
                vehicles = new JoltPrim[_vehicles.Count];
                _vehicles.CopyTo(vehicles);
            }
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (JoltPrim v in vehicles)
            {
                try { v.StepVehicle(timeStep); }
                catch (Exception e)
                {
                    // Never let one vehicle's math wedge the heartbeat.
                    m_log.LogError($"{LogHeader} vehicle step EXCEPTION for prim {v.LocalID}: {e}");
                }
            }
            Interlocked.Add(ref _vehicleStepTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref _vehicleStepCalls, vehicles.Length);
        }

        // The time the per-Update vehicle drive has taken (Stopwatch ticks) and the vehicle steps it made, for the cost
        // figures of the tests.
        private long _vehicleStepTicks, _vehicleStepCalls;
        internal long VehicleStepTicks => Interlocked.Read(ref _vehicleStepTicks);
        internal long VehicleStepCalls => Interlocked.Read(ref _vehicleStepCalls);
        // The time the vehicles' step force providers have taken inside the collision steps (Stopwatch ticks).
        private long _vehicleOnStepTicks, _vehicleOnStepCalls;
        internal void AddVehicleStepTicks(long ticks)
        {
            Interlocked.Add(ref _vehicleOnStepTicks, ticks);
            Interlocked.Increment(ref _vehicleOnStepCalls);
        }
        // The time the target controllers' step provider (move to target, hover, look-at) has taken inside the collision steps
        // (Stopwatch ticks), and the calls.
        private long _basicForcesOnStepTicks, _basicForcesOnStepCalls;
        internal void AddBasicForcesStepTicks(long ticks)
        {
            Interlocked.Add(ref _basicForcesOnStepTicks, ticks);
            Interlocked.Increment(ref _basicForcesOnStepCalls);
        }
        internal long BasicForcesOnStepTicks => Interlocked.Read(ref _basicForcesOnStepTicks);
        internal long BasicForcesOnStepCalls => Interlocked.Read(ref _basicForcesOnStepCalls);
        internal long VehicleOnStepTicks => Interlocked.Read(ref _vehicleOnStepTicks);
        internal long VehicleOnStepCalls => Interlocked.Read(ref _vehicleOnStepCalls);

        // Everything the vehicle drive costs: the Update-rate part and the collision steps' part.
        internal long VehicleDriveTicks => VehicleStepTicks + VehicleOnStepTicks;

        // Prims with a script force or torque set (llSetForce, llSetTorque): each is applied before every backend step,
        // next to the vehicle drive, until the script sets it back to zero.
        private readonly HashSet<JoltPrim> _scriptForced = new HashSet<JoltPrim>();

        internal void SetScriptForced(JoltPrim prim, bool on)
        {
            lock (_scriptForced)
            {
                if (on) { if (!prim.IsRemoved) _scriptForced.Add(prim); }
                else _scriptForced.Remove(prim);
            }
        }

        private void StepScriptForces(float timeStep)
        {
            JoltPrim[] prims;
            lock (_scriptForced)
            {
                if (_scriptForced.Count == 0) return;
                prims = new JoltPrim[_scriptForced.Count];
                _scriptForced.CopyTo(prims);
            }
            foreach (JoltPrim p in prims)
            {
                try { p.StepScriptForces(timeStep); }
                catch (Exception e)
                {
                    m_log.LogError($"{LogHeader} script force EXCEPTION for prim {p.LocalID}: {e}");
                }
            }
        }

        // Prims with llMoveToTarget or llSetHoverHeight asked for: each controller acts before every backend step. A prim
        // stays here while either is asked for (it acts again once the prim is physical) and leaves once it has let go.
        private readonly HashSet<JoltPrim> _targeted = new HashSet<JoltPrim>();

        internal void SetTargeted(JoltPrim prim, bool on)
        {
            lock (_targeted)
            {
                if (on) { if (!prim.IsRemoved) _targeted.Add(prim); }
                else _targeted.Remove(prim);
            }
        }

        private void StepTargets(float timeStep)
        {
            JoltPrim[] prims;
            lock (_targeted)
            {
                if (_targeted.Count == 0) return;
                prims = new JoltPrim[_targeted.Count];
                _targeted.CopyTo(prims);
            }
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (JoltPrim p in prims)
            {
                bool keep = true;
                try { keep = p.StepTargets(timeStep); }
                catch (Exception e)
                {
                    m_log.LogError($"{LogHeader} move-to-target or hover EXCEPTION for prim {p.LocalID}: {e}");
                }
                if (!keep)
                    lock (_targeted) _targeted.Remove(p);
            }
            Interlocked.Add(ref _basicForcesUpdateTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref _basicForcesUpdateCalls, prims.Length);
        }

        // The time the Update-rate part of the target controllers has taken (Stopwatch ticks) and the prims it made a step for.
        private long _basicForcesUpdateTicks, _basicForcesUpdateCalls;
        internal long BasicForcesUpdateTicks => Interlocked.Read(ref _basicForcesUpdateTicks);
        internal long BasicForcesUpdateCalls => Interlocked.Read(ref _basicForcesUpdateCalls);

        internal void MarkLinksetDirty(JoltPrim root)
        {
            lock (_dirtyLinksets) _dirtyLinksets.Add(root);
        }

        // Wake the physical bodies created inert since the last Simulate (deferred activation - the BulletSim
        // configure-before-step barrier). Runs on the step thread so ActivateBody reliably reaches the active
        // set. A body only created inert becomes a normal live body here; a vehicle wakes with gravity already
        // cancelled, so it never free-falls. Errors are swallowed so one bad body can't wedge the heartbeat.
        private void DrainPendingActivation()
        {
            JoltPrim[] pend;
            lock (_pendingActivation)
            {
                if (_pendingActivation.Count == 0) return;
                pend = _pendingActivation.ToArray();
                _pendingActivation.Clear();
            }
            foreach (JoltPrim p in pend)
            {
                try { p.ActivatePending(); }
                catch (Exception e) { m_log.LogError($"{LogHeader} pending-activation EXCEPTION for prim {p.LocalID}: {e}"); }
            }
        }

        private void DrainDirtyLinksets()
        {
            // WELD AT LOAD: rebuild dirty linkset roots at the TOP of Simulate, BEFORE StepOnce - so a
            // persisted linkset's child parts are welded into the compound (their individual bodies removed)
            // BEFORE they ever step. This matches BulletSim's model (one compound body from the start); the
            // children never exist as separate physics-active overlapping bodies that penetrate + fling.
            JoltPrim[] dirty;
            lock (_dirtyLinksets)
            {
                if (_dirtyLinksets.Count == 0) return;
                dirty = new JoltPrim[_dirtyLinksets.Count];
                _dirtyLinksets.CopyTo(dirty);
                _dirtyLinksets.Clear();
            }
            foreach (JoltPrim root in dirty)
                root.RebuildCompoundNow();   // re-entrancy-, destroyed-, and exception-guarded internally
        }

        public override float Simulate(float timeStep)
        {
            // Read once: a teardown on another thread nulls _backend, and may do so between a check and a second read.
            IPhysicsBackend backend = _backend;
            if (backend == null)
                return 1f;
            NoteHeartbeatGap(timeStep);
            SimTime += timeStep;
            backend.BeginRayCastBudget();   // [Jolt] RayCastBudgetMs and RayCastSimulatorBudgetMs are per heartbeat, at every PhysicsStepRate
            if (_substeps != null)
                return SimulateSubsteps(timeStep);

            // (Re)build changed linkset compounds ONCE per frame, here on the step thread before
            // the step. link()/unlink() only mark the root dirty (they do not rebuild inline); this
            // coalesces a whole linkset's worth of child-links into a single rebuild - rebuilding on every
            // child's link() churns the root and can hang the region load of a persisted physical linkset.
            DrainDirtyLinksets();

            // Mirrors BulletSim's taint-deferred creation: activate physical bodies that were
            // created INERT (asleep) now, AFTER the linkset weld and any load-time property/vehicle setup have
            // completed, and BEFORE StepVehicles/StepOnce. So a body enters the engine step already fully
            // configured - a reloaded vehicle wakes with its gravity already cancelled (StepVehicles asserts it
            // just below, before StepOnce), so it can NEVER free-fall during load or the reload stall. Mirrors
            // BulletSim draining ALL taints before PE.PhysicsStep().
            DrainPendingActivation();

            // Keyframed prims take the pose their object was last given, over the time this step covers.
            StepKinematics(timeStep);

            // Run each active vehicle's controller BEFORE the physics step, so its
            // velocity changes/forces/torques are consumed by THIS step (BulletSim's BeforeStep model).
            LastTimeStep = timeStep;
            StepVehicles(timeStep);
            StepScriptForces(timeStep);
            StepTargets(timeStep);

            // ONE backend Step per frame at OpenSim's ~11 fps cadence (Scene.FrameTime 0.0909 s). The
            // character is stepped exactly once per frame, which keeps avatar motion smooth.
            // Fast-body tunnelling through the terrain is handled NOT by sub-
            // stepping the whole Simulate (that 6x's the character/drain/terse pipeline and shows up as
            // avatar bounce/jitter and a performance cost), but by CollisionSteps=6 set at Initialize: Jolt sub-
            // steps the RIGID-BODY solver INSIDE _system.Update without re-running the character step, so a
            // dropped prim integrates in solver sub-slices and rests, while the avatar stays at 1 step/frame.
            StepOnce(timeStep, backend);

            TraceCharFrame(backend);
            return 1f;
        }

        // [charframe] live trace (toggle: `jolt charframe`): per-frame avatar Z / support / vertical
        // velocity, so avatar bounce or sinking shows up in the numbers.
        private void TraceCharFrame(IPhysicsBackend backend)
        {
            if (_stepCount <= _charFrameUntil)
            {
                List<JoltCharacter> avs;
                lock (_avatars) avs = new List<JoltCharacter>(_avatars.Values);
                foreach (JoltCharacter a in avs)
                {
                    // Identify the ground body by its UserData: 0 = TERRAIN (expected), == the avatar's own
                    // LocalID = its query marker (a bug), any other id = a prim/box. The terrain body
                    // IS a registered body, so "has a ground body" alone does NOT mean the marker.
                    string ground = "none";
                    if (a.GroundBody.IsValid && backend.TryGetBodyState(a.GroundBody, out BodyState gb))
                        ground = gb.UserData == 0 ? "TERRAIN"
                               : gb.UserData == a.LocalID ? $"OWN-MARKER({gb.UserData})"
                               : $"prim({gb.UserData})";
                    // Terrain surface directly under the avatar (raycast) vs where the feet actually are -
                    // negative & shrinking feetAboveTerrain = sinking THROUGH the collision surface.
                    Vector3 p = a.Position;
                    float terrZ = float.NaN;
                    if (backend.RayCast(new SVector3(p.X, p.Y, p.Z + 50f), new SVector3(0f, 0f, -1f), 300f, QueryFilter.Terrain, out RayHit th))
                        terrZ = th.Point.Z;
                    // FIXED-POINT terrain probe at the region centre - INDEPENDENT of the avatar's position.
                    // If this descends while the avatar stands still, the terrain surface is genuinely moving
                    // (terrain bug). If it holds constant but the avatar's own terrainZ descends, the avatar is
                    // drifting horizontally onto lower ground (a slide, not a sinking terrain).
                    float fixZ = float.NaN;
                    float cx = _regionSizeX * 0.5f, cy = _regionSizeY * 0.5f;
                    if (backend.RayCast(new SVector3(cx, cy, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit fh))
                        fixZ = fh.Point.Z;
                    m_log.LogDebug($"{LogHeader} [charframe] step={_stepCount} id={a.LocalID} XY=({p.X:0.00},{p.Y:0.00}) Z={p.Z:0.000} " +
                                   $"sup={(a.IsSupported ? "Y" : "N")} sliding={(a.IsSliding ? "Y" : "N")} vZ={a.Velocity.Z:0.000} flying={(a.Flying ? "Y" : "N")} ground={ground} " +
                                   $"terrainZ@avatar={terrZ:0.000} terrainZ@centre({cx:0},{cy:0})={fixZ:0.000} feetAboveTerrain={p.Z - a.StandHalf - a.FeetOffset - terrZ:0.000}");
                }
            }
            else if (CharJumpTrace)
                CharJumpTrace = false;   // window elapsed -> stop the [charjump] trace too
        }

        // One backend Step + drain (bodies -> prims, characters -> avatars) + the [dropframe] diagnostic. `backend` is the
        // one Simulate read: a teardown on another thread may have nulled _backend since, and it disposes the backend,
        // whose Step then returns nothing.
        private void StepOnce(float timeStep, IPhysicsBackend backend)
        {
            // Step, then DRAIN: the backend fills _bodyBuf with a BodyState per ACTIVE body (moving prims)
            // plus one final JustDeactivated state per body that slept this step. For each, push the new
            // transform/velocity into the matching actor (by UserData = LocalID) and fire its terse update
            // so the viewer sees motion; the JustDeactivated state is the settle update that stops a rested
            // object drifting. Sleeping bodies aren't reported, so idle prims cost nothing.
            StepResult r = backend.Step(timeStep, _bodyBuf, _charBuf, _contactBuf);
            _stepCount++;
            var timing = new HeartbeatTiming();
            timing.Add(in r);
            ReportStep(backend, in r, timeStep, r.ContactCount, r.ContactBufferOverflowed, r.PhysicsMilliseconds, mergeSubsteps: false, in timing);
        }

        // The time between two heartbeats' physics calls (Simulate), for the metrics' longest gap. A heartbeat that runs
        // no physics step leaves its gap pending; the next heartbeat that reports keeps the longer one.
        private long _lastSimulateTimestamp;
        private long _pendingGapTicks;
        private void NoteHeartbeatGap(float timeStep)
        {
            long now = HeartbeatClock != null ? HeartbeatClock() : System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastSimulateTimestamp != 0)
            {
                long gap = now - _lastSimulateTimestamp;
                if (gap > _pendingGapTicks)
                    _pendingGapTicks = gap;
                NoteDilationSample(timeStep, gap);
            }
            _lastSimulateTimestamp = now;
        }

        // Time dilation (PhysicsScene.TimeDilation; llGetRegionTimeDilation): the simulated seconds of the last
        // DilationWindowHeartbeats heartbeats over the real seconds between them, at most 1. A heartbeat is asked to
        // simulate a fixed time; one that arrives late loses the difference, so the ratio falls below 1 while the region
        // runs behind and returns to 1 when it catches up. Averaging over about a second keeps the timer's jitter from
        // showing as dilation. The Second Life wiki page for llGetRegionTimeDilation: 0.0 is full dilation and 1.0 none.
        private const int DilationWindowHeartbeats = 11;
        private readonly double[] _dilationStepSeconds = new double[DilationWindowHeartbeats];
        private readonly double[] _dilationGapSeconds = new double[DilationWindowHeartbeats];
        private int _dilationCount;
        private int _dilationNext;
        private volatile float _timeDilation = 1f;

        public override float TimeDilation => _timeDilation;

        private void NoteDilationSample(float timeStep, long gapTicks)
        {
            _dilationStepSeconds[_dilationNext] = timeStep;
            _dilationGapSeconds[_dilationNext] = gapTicks / (double)System.Diagnostics.Stopwatch.Frequency;
            _dilationNext = (_dilationNext + 1) % DilationWindowHeartbeats;
            if (_dilationCount < DilationWindowHeartbeats)
                _dilationCount++;
            double steps = 0, gaps = 0;
            for (int i = 0; i < _dilationCount; i++)
            {
                steps += _dilationStepSeconds[i];
                gaps += _dilationGapSeconds[i];
            }
            _timeDilation = gaps <= steps ? 1f : (float)(steps / gaps);
        }

        // Bodies kept inside the region's border (JoltPrim.KeepInsideBorder) that this step carried across it. The body is put
        // back and the avatars standing on it are moved with it once the character drain has run, so that the avatars' cached
        // state is the one they are moved from.
        private readonly List<(JoltPrim Prim, Vector3 Before)> _borderPins = new List<(JoltPrim, Vector3)>();

        internal void QueueBorderPin(JoltPrim prim, Vector3 before)
        {
            lock (_borderPins) _borderPins.Add((prim, before));
        }

        private void FlushBorderPins()
        {
            (JoltPrim Prim, Vector3 Before)[] pins;
            lock (_borderPins)
            {
                if (_borderPins.Count == 0) return;
                pins = _borderPins.ToArray();
                _borderPins.Clear();
            }
            foreach ((JoltPrim prim, Vector3 before) in pins)
            {
                try { prim.FinishBorderPin(before); }
                catch (Exception e) { m_log.LogError($"{LogHeader} keeping prim {prim.LocalID} inside the border EXCEPTION: {e}"); }
            }
        }

        // Puts the avatars that stand on this body back on it after it was set back from `before` to `now`.
        internal void MoveCharactersStandingOn(BodyId body, Vector3 before, Vector3 now)
        {
            List<JoltCharacter> avs;
            lock (_avatars)
                avs = new List<JoltCharacter>(_avatars.Values);
            foreach (JoltCharacter a in avs)
            {
                BodyId ground = a.GroundBody;
                if (ground.IsValid && ground.Value == body.Value)
                    a.PlaceBackOnGround(before, now);
            }
        }

        // Everything after the backend step: metrics, capacity, the body / character drains and the collision
        // dispatch. `r` is the step whose body and character states are reported; the contact count, overflow,
        // physics time and timing cover every backend step of this heartbeat (one, unless substepping).
        private void ReportStep(IPhysicsBackend backend, in StepResult r, float timeStep, int contactCount, bool contactsOverflowed, float physicsMs, bool mergeSubsteps,
                                in HeartbeatTiming timing)
        {
            _lastActiveBodyCount = r.ActiveBodyCount;
            double gapMs = _pendingGapTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _pendingGapTicks = 0;
            JoltMetrics.RecordStep(RegionName, physicsMs, r.ActiveBodyCount, in timing, gapMs, timeStep * 1000.0,   // step-time instrumentation (`jolt metrics`)
                backend.RayCastsRefused(RayCastBudget.Script), backend.RayCastsRefused(RayCastBudget.Simulator));

            _diagnostics?.StepReported(backend, _stepCount, timeStep, in r);   // the test commands' per-frame drop log

            if (r.BodyBufferOverflowed) _bodyOverflowFrames++;
            if (r.CharacterUpdateCount >= _charBuf.Length) _charFullFrames++;
            if (contactsOverflowed) _contactOverflowFrames++;
            CheckCapacity(backend, timeStep);

            int n = r.BodyUpdateCount;
            for (int i = 0; i < n; i++)
            {
                BodyState bs = _bodyBuf[i];
                JoltPrim p;
                lock (_prims)
                    _prims.TryGetValue(bs.UserData, out p);
                p?.ApplyStepState(in bs);
                _diagnostics?.BodyUpdated(in bs);   // the test commands' drop tracker
            }

            // Character drain: the avatar equivalent of the body drain above. The backend stepped
            // every CharacterVirtual BEFORE _system.Update and filled _charBuf with each one's post-step
            // position + ground state; push it into the matching JoltCharacter (by CharacterId handle) so
            // ScenePresence sees the new transform and the viewer gets a smooth per-frame terse update.
            int cn = r.CharacterUpdateCount;
            for (int i = 0; i < cn; i++)
            {
                CharacterState cs = _charBuf[i];
                JoltCharacter a;
                lock (_avatars)
                    _avatars.TryGetValue(cs.Character.Value, out a);
                a?.ApplyCharacterState(in cs);
            }

            FlushBorderPins();

            DispatchContacts(_contactBuf, contactCount, contactsOverflowed, mergeSubsteps);

            // Grow any buffer this frame filled, now that it has been read. Overflow does not lose
            // updates (the backend carries them over), so the warning is rate-limited like the capacity one.
            bool charFull = r.CharacterUpdateCount >= _charBuf.Length;
            if (r.BodyBufferOverflowed || charFull || contactsOverflowed)
            {
                string grew = "";
                if (r.BodyBufferOverflowed) grew += GrowBuffer(ref _bodyBuf, _bodyBufMax, "body");
                if (charFull) grew += GrowBuffer(ref _charBuf, _charBufMax, "character");
                if (contactsOverflowed) grew += GrowBuffer(ref _contactBuf, _contactBufMax, "contact");
                long now = System.DateTime.UtcNow.Ticks;
                if (_overflowLastWarnTicks == 0 || now - _overflowLastWarnTicks >= _capacityLogIntervalTicks)
                {
                    _overflowLastWarnTicks = now;
                    m_log.LogWarning($"{LogHeader} {RegionName}: step buffer overflow (bodies {r.BodyUpdateCount}/{r.ActiveBodyCount} active, contacts overflowed={contactsOverflowed});{grew} updates carry over to the next step.");
                }
            }
        }

        // [Jolt] PhysicsStepRate on: one heartbeat runs several backend steps of exactly 1 / rate seconds (as many
        // as SubstepAccumulator hands out) inside this call, on the heartbeat thread.
        //   Once per heartbeat, first: the linkset and activation drains (as on the single-step path).
        //   Every step: the vehicle controllers (with that step's dt) and the script forces, then the backend step, which steps every
        //   avatar before the solver; queued body changes and forces from other threads land before the next step.
        //   Once per heartbeat, last: the body and avatar reports (from the last step) and the collision dispatch.
        // The steps before the last hand the backend empty body and avatar buffers, so it reports nothing for them:
        // a body that sleeps in an earlier step keeps its settle state queued in the backend (it never drops one that
        // does not fit) and the last step reports it, unless the body woke again. Contacts from every step of the
        // heartbeat are drained into one buffer and dispatched once, a touching pair counted once (see
        // DispatchContacts). Each step takes and releases the job pool's gate and _simLock itself, as one step does.
        private float SimulateSubsteps(float timeStep)
        {
            DrainDirtyLinksets();
            DrainPendingActivation();

            int n = _substeps.Advance(timeStep);
            float dt = _substeps.StepSeconds;
            LastTimeStep = dt;   // AddForce: a non-push force acts for one step, so its impulse stays the force given
            PushForceScale = timeStep / dt;
            if (n == 0)
                return 1f;   // nothing stepped: no reports, and no collision_end for contacts that were not re-checked
            StepKinematics(n * dt);   // the time the steps of this heartbeat cover; a heartbeat that steps nothing leaves the pose for the next

            // Held for the whole heartbeat: a teardown that lands between two steps disposes the backend (whose steps
            // then return nothing) but cannot null it out from under this loop. A teardown that landed after Simulate
            // checked it and before this line has already nulled it: nothing is stepped.
            IPhysicsBackend backend = _backend;
            if (backend == null)
                return 1f;
            if (Interlocked.Read(ref _physicsClockTicks) == 0)
                Interlocked.Exchange(ref _physicsClockTicks, (VehicleClock?.Invoke() ?? DateTime.Now).Ticks);
            long stepTicks = (long)Math.Round(dt * (double)TimeSpan.TicksPerSecond);

            StepResult r = default;
            int contactCount = 0;
            bool contactsOverflowed = false;
            float physicsMs = 0f;
            var timing = new HeartbeatTiming();
            for (int k = 0; k < n; k++)
            {
                bool last = k == n - 1;
                Interlocked.Add(ref _physicsClockTicks, stepTicks);
                StepVehicles(dt);
                StepScriptForces(dt);
                StepTargets(dt);
                r = backend.Step(dt,
                    last ? _bodyBuf : Span<BodyState>.Empty,
                    last ? _charBuf : Span<CharacterState>.Empty,
                    _contactBuf.AsSpan(contactCount));
                if (k == 0)
                    _stepCount++;   // counts heartbeats, as on the single-step path
                contactCount += r.ContactCount;
                contactsOverflowed |= r.ContactBufferOverflowed;
                physicsMs += r.PhysicsMilliseconds;
                timing.Add(in r);
            }
            ReportStep(backend, in r, timeStep, contactCount, contactsOverflowed, physicsMs, mergeSubsteps: true, in timing);
            TraceCharFrame(backend);
            return 1f;
        }

        // Double a step buffer up to its cap. Returns a note for the overflow warning.
        private static string GrowBuffer<T>(ref T[] buf, int max, string what)
        {
            if (buf.Length >= max)
                return $" {what} buffer at its max {buf.Length};";
            int size = (int)System.Math.Min((long)max, buf.Length * 2L);
            buf = new T[size];
            return $" {what} buffer grown to {size};";
        }

        // After each Step, warn (at most once per [Jolt] CapacityLogIntervalSeconds per region) when the update
        // reported a capacity error or CreateBody was refused since the last warning, naming the [Jolt] key to raise.
        private void CheckCapacity(IPhysicsBackend backend, float timeStep)
        {
            PhysicsCapacityStats s = backend.GetCapacityStats();
            long now = System.DateTime.UtcNow.Ticks;
            CheckGateWait(in s, now, timeStep);
            if (!_capBaseSet)
            {
                _capBase = s;
                _capBaseTicks = now;
                _capBaseSet = true;
                return;
            }
            bool worse = s.BodyCreateFailures > _capBase.BodyCreateFailures
                || s.BodyPairCacheFullSteps > _capBase.BodyPairCacheFullSteps
                || s.ManifoldCacheFullSteps > _capBase.ManifoldCacheFullSteps
                || s.ContactConstraintsFullSteps > _capBase.ContactConstraintsFullSteps;
            if (!worse)
            {
                _capBase = s;          // nothing to report: keep the window anchored at "now"
                _capBaseTicks = now;
                return;
            }
            if (_capLastWarnTicks != 0 && now - _capLastWarnTicks < _capacityLogIntervalTicks)
                return;                // quiet period: let the counts accumulate into the next line
            string msg = CapacityReport.Warning(RegionName,
                _capBase, s, System.Math.Max(1.0, (now - _capBaseTicks) / (double)System.TimeSpan.TicksPerSecond));
            if (msg != null)
                m_log.LogWarning(msg);
            _capLastWarnTicks = now;
            _capBase = s;
            _capBaseTicks = now;
        }
        private long _capLastWarnTicks;

        // Once per capacity-log interval, compare this region's time waiting at its job pool's gate with
        // its frame time over that interval; more than 20% means too few pools for this many busy regions.
        private long _gateWindowStartTicks;
        private double _gateWindowStartWaitMs;
        private double _gateWindowFrameMs;
        private void CheckGateWait(in PhysicsCapacityStats s, long now, float timeStep)
        {
            _gateWindowFrameMs += timeStep * 1000.0;
            if (_gateWindowStartTicks == 0)
            {
                _gateWindowStartTicks = now;
                _gateWindowStartWaitMs = s.UpdateGateWaitMsTotal;
                _gateWindowFrameMs = 0;
                return;
            }
            if (now - _gateWindowStartTicks < _capacityLogIntervalTicks)
                return;
            string msg = CapacityReport.GateWarning(RegionName, s.UpdateGateWaitMsTotal - _gateWindowStartWaitMs, _gateWindowFrameMs, s.PoolIndex, s.JobPools);
            if (msg != null)
                m_log.LogWarning(msg);
            _gateWindowStartTicks = now;
            _gateWindowStartWaitMs = s.UpdateGateWaitMsTotal;
            _gateWindowFrameMs = 0;
        }

        private void JoltCapacity()
        {
            PhysicsCapacityStats s = CapacityStats();   // default after teardown
            MainConsole.Instance.Output(CapacityReport.Render(RegionName, s,
                _bodyBuf.Length, _bodyOverflowFrames, _charBuf.Length, _charFullFrames, _contactBuf.Length, _contactOverflowFrames, _substeps,
                JoltMetrics.LastIntervalOf(RegionName)));
        }

        // Collision dispatch: turn this frame's ContactReports into OpenSim collision events. Each
        // subscribed prim gets ONE CollisionEventUpdate listing the LocalIDs it is touching this frame
        // (terrain = 0); OpenSim's SceneObjectPart.PhysicsCollision diffs that against last frame to fire
        // collision_start / collision / collision_end, and llDetected* off the collider list. Runs on the
        // heartbeat thread right after the drain (same thread SOP.PhysicsCollision expects).
        //
        // Contacts carry Begin (first touch) + Persist (each frame while touching, gated on a subscribed
        // body) + End (separation). The "currently touching" set OpenSim wants = Begin|Persist this frame, plus
        // last frame's contacts between bodies that are now both at rest (held below: the engine reports none for them);
        // End is implicit (a pair that drops out of the set). A prim that touched last frame but not now
        // still needs one (empty) update so collision_end can fire - _collidedLastFrame drives that flush.
        // Per-child: each contact is delivered to the STRUCK part on each side (ChildUserData - the compound child hit,
        // resolved from the contact sub-shape), so llDetectedLinkNumber returns that child's link, and it names the other
        // side by its body (UserData): a physical linkset by its root (see the AddCollider block below).
        //
        // With several backend steps in one heartbeat (mergeSubsteps), the buffer holds every step's reports. A
        // touching pair reports in each step, so only its first Begin/Persist report of the heartbeat counts: the
        // collision score and the collider set are what one step per heartbeat gives. A contact that begins and
        // ends inside one heartbeat has a Begin report, so it is touching for that heartbeat (collision_start) and
        // absent from the next (collision_end); one that spans heartbeats reports in each.
        internal void DispatchContacts(ContactReport[] contacts, int contactCount, bool contactsOverflowed, bool mergeSubsteps)
        {
            // Resolve every prim this frame can touch - both sides of each contact, plus last frame's colliders
            // (the collision_end candidates) - under ONE lock(_prims), into a reused per-frame map.
            _frameIds.Clear();
            for (int i = 0; i < contactCount; i++)
            {
                ref ContactReport c = ref contacts[i];
                if (c.Phase == ContactPhase.End)
                    continue;
                _frameIds.Add(c.ChildUserDataA);
                _frameIds.Add(c.ChildUserDataB);
            }
            foreach (uint id in _collisions.CollidedLastFrame)
                _frameIds.Add(id);
            foreach (CollisionEventUpdate set in _collisions.Current.Values)   // what last frame's prims touched (held if asleep)
                foreach (uint id in set.m_objCollisionList.Keys)
                    _frameIds.Add(id);
            foreach (uint id in _collisions.Scores.Keys)   // last frame's scored prims (zeroed if gone)
                _frameIds.Add(id);
            _framePrims.Clear();
            lock (_prims)
            {
                foreach (uint id in _frameIds)
                    if (_prims.TryGetValue(id, out JoltPrim p))
                        _framePrims[id] = p;
            }
            // Avatars by LocalID (_avatars is keyed by the backend handle). Only subscribed ones get events;
            // ScenePresence subscribes every root avatar.
            _frameAvatars.Clear();
            lock (_avatars)
            {
                foreach (JoltCharacter a in _avatars.Values)
                    if (a.SubscribedEvents())
                        _frameAvatars[a.LocalID] = a;
            }

            _collisions.BeginFrame();
            _collisions.Now = SimTime;
            if (mergeSubsteps)
                _framePairs.Clear();
            for (int i = 0; i < contactCount; i++)
            {
                ref ContactReport c = ref contacts[i];
                if (c.Phase == ContactPhase.End)
                    continue;   // OpenSim derives "ended" from absence in the current set
                if (mergeSubsteps && !_framePairs.Add(PairKey(c.ChildUserDataA, c.ChildUserDataB)))
                    continue;   // this pair already reported in an earlier step of this heartbeat

                // Every Begin/Persist report scores both struck parts, subscribed or not.
                _collisions.CountContact(c.ChildUserDataA);
                _collisions.CountContact(c.ChildUserDataB);

                // Dispatch to the STRUCK part on each side (ChildUserData - the compound child hit, or the body itself for a
                // single prim), and name the OTHER side by its body (UserData): a physical linkset by its root, any other prim
                // as itself. Delivering to child N's PhysicsActor makes OpenSim run child N's PhysicsCollision, so
                // llDetectedLinkNumber == N (and core passes it to the root's script by its own rule,
                // SceneObjectPart.SendCollisionEvent). Core names whatever id it is given, by part (CreateColliderArgs), so
                // the name is the engine's to choose. ubODE names every collider by its root
                // (ODEScene.Collision_accounting_events: ParentActor.m_baseLocalID), which is a part's own id unless core
                // linked it, as it does a physical linkset alone; Second Life's llCollisionFilter example
                // (wiki.secondlife.com/wiki/LlCollisionFilter) matches a child prim's collision by its object's name (a filter on
                // the object "Post" detects a hit by a child prim named "Object" of that object). So a prim, a linkset and an
                // avatar name an object alike.
                // Jolt's normal points A -> B; give each side the surface normal pointing back at it.
                // ContactReport carries System.Numerics vectors (SVector3); OpenSim's ContactPoint is OMV.
                // A volume detector's contacts go to the detector alone: what passes through it is not told, as ubODE
                // does (ODEScene.Collision_accounting_events) and as Second Life documents for attachments ("Attachments
                // do not receive collision events for avatar collisions with VolumeDetect objects", llVolumeDetect).
                bool detectorA = IsVolumeDetectPrim(c.ChildUserDataA);
                bool detectorB = IsVolumeDetectPrim(c.ChildUserDataB);
                Vector3 pt = new Vector3(c.Point.X, c.Point.Y, c.Point.Z);
                // RelativeSpeed is how fast the two part along the normal, below zero while they close: the same for either
                // side. Core's collision sounds (above 0.2 m/s) and an avatar's impact damage (below -5 m/s) read it, as
                // ubODE fills it (ODEScene.Collision_accounting_events, ODECharacter for the ground).
                if (!detectorB && IsSubscribedPrim(c.ChildUserDataA))
                    _collisions.AddCollider(c.ChildUserDataA, c.UserDataB,
                        new ContactPoint(pt, new Vector3(c.Normal.X, c.Normal.Y, c.Normal.Z), 0f) { RelativeSpeed = c.RelativeSpeed });
                if (!detectorA && IsSubscribedPrim(c.ChildUserDataB))
                    _collisions.AddCollider(c.ChildUserDataB, c.UserDataA,
                        new ContactPoint(pt, new Vector3(-c.Normal.X, -c.Normal.Y, -c.Normal.Z), 0f) { RelativeSpeed = c.RelativeSpeed });

                // An avatar's own contacts: the backend reports them with the avatar as side A (no body) and the
                // touched body, the terrain (0) or another avatar as side B; another avatar reports its own side.
                // ScenePresence.PhysicsCollisionUpdate reads collider 0 as land (land_collision on its
                // attachments) and any other id as an object (collision), as ubODE (ODECharacter) and BulletS
                // (BSCharacter) report them. The contact normal points from the avatar into what it touches, the
                // same way round as ubODE's avatar SurfaceNormal; ScenePresence turns it over to make the collision
                // plane under the feet. A contact whose normal points down is at the feet.
                // The avatar names an object by its root (UserData, the body's own id), not the part it touched: ubODE names
                // every collider by its root (ParentActor.m_baseLocalID), so an attachment's llDetectedKey and llDetectedName
                // are the object's, its collision filter matches the object's name, and an avatar touching two parts of one
                // object has one collision with it, carrying the harder strike. The touched part still names the avatar.
                if (!detectorB && !c.BodyA.IsValid && _frameAvatars.ContainsKey(c.ChildUserDataA))
                    _collisions.AddCollider(c.ChildUserDataA, c.UserDataB,
                        new ContactPoint(pt, new Vector3(c.Normal.X, c.Normal.Y, c.Normal.Z), 0f, c.Normal.Z < -AvatarFeetNormalZ)
                        { RelativeSpeed = c.RelativeSpeed });
                // Two avatars meeting: the other is told too, as ubODE tells both (ODECollision: each side's
                // AddCollisionEvent). Jolt finds the contact only in the update of the avatar that meets the other, and the
                // first to update may move away ahead of the other, taking its velocity, so the other's own update can find
                // nothing: at one step per heartbeat an avatar walking into another never heard of it.
                if (!c.BodyA.IsValid && !c.BodyB.IsValid && _frameAvatars.ContainsKey(c.ChildUserDataA) && _frameAvatars.ContainsKey(c.ChildUserDataB))
                    _collisions.AddCollider(c.ChildUserDataB, c.UserDataA,
                        new ContactPoint(pt, new Vector3(-c.Normal.X, -c.Normal.Y, -c.Normal.Z), 0f, -c.Normal.Z < -AvatarFeetNormalZ)
                        { RelativeSpeed = c.RelativeSpeed });
            }

            // A contact of last frame that is missing now only because neither body is awake is still there: the engine
            // looks for contacts only where a body is awake, so two bodies at rest on each other report nothing. Without
            // this a resting object's collision and land_collision would end when it falls asleep, and a volume detector
            // would lose a body that falls asleep inside it. ubODE keeps them too (ODEScene re-feeds each sleeping prim's
            // last contacts, ODEPrim.SleeperAddCollisionEvents). Only subscribed prims have sets, so nothing else pays.
            foreach (KeyValuePair<uint, CollisionEventUpdate> kv in _collisions.Previous)
            {
                if (!_framePrims.TryGetValue(kv.Key, out JoltPrim prim) || !prim.SubscribedEvents())
                    continue;
                if (!_collisions.Current.ContainsKey(kv.Key) && AllStillTouchingAsleep(prim, kv.Value))
                {
                    _collisions.HoldAll(kv.Key, kv.Value);   // at rest as it was: keep last frame's set itself
                    continue;
                }
                foreach (KeyValuePair<uint, ContactPoint> c in kv.Value.m_objCollisionList)
                    if (!_collisions.Touches(kv.Key, c.Key) && StillTouchingAsleep(prim, c.Key))
                        _collisions.Hold(kv.Key, c.Key, c.Value);
            }

            // Deliver this frame's sets (outside the lock).
            foreach (KeyValuePair<uint, CollisionEventUpdate> kv in _collisions.Current)
            {
                if (_framePrims.TryGetValue(kv.Key, out JoltPrim p))
                {
                    // A part's repeats keep the collision event's documented rate; an avatar's updates (the collision
                    // plane under the feet, impact damage) go every heartbeat.
                    if (_collisions.ShouldDeliverToPart(kv.Key, kv.Value))
                        p.SendCollisionUpdate(kv.Value);
                }
                else if (_collisions.ShouldDeliver(kv.Key, kv.Value) && _frameAvatars.TryGetValue(kv.Key, out JoltCharacter a))
                    a.SendCollisionUpdate(kv.Value);
            }

            // Flush an EMPTY update to prims and avatars that collided last frame but not now (fires collision_end
            // and land_collision_end). On a frame whose contact buffer overflowed the tracker ends nobody: absence
            // is not proof.
            foreach (uint id in _collisions.EndFrame(contactsOverflowed))
            {
                if (_framePrims.TryGetValue(id, out JoltPrim p) && p.SubscribedEvents())
                    p.SendCollisionUpdate(new CollisionEventUpdate());
                else if (_frameAvatars.TryGetValue(id, out JoltCharacter a))
                    a.SendCollisionUpdate(new CollisionEventUpdate());
            }

            // Publish this frame's scores; a prim scored last frame and not now drops back to 0.
            foreach (uint id in _collisions.PreviouslyScored)
                if (!_collisions.Scores.ContainsKey(id) && _framePrims.TryGetValue(id, out JoltPrim p))
                    p.CollisionScore = 0f;
            foreach (KeyValuePair<uint, int> kv in _collisions.Scores)
                if (_framePrims.TryGetValue(kv.Key, out JoltPrim p))
                    p.CollisionScore = kv.Value;
        }

        private readonly HashSet<ulong> _framePairs = new HashSet<ulong>();
        private static ulong PairKey(uint a, uint b)
            => a <= b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

        // An avatar contact whose normal (from the avatar into the surface) points down by more than this is under
        // the feet: a surface tilted less than 60 degrees from level. Walls and ceilings are not.
        internal const float AvatarFeetNormalZ = 0.5f;

        // A LocalID resolves (this frame) to a prim that currently has a collision-script subscription. Avatars
        // are resolved separately (_frameAvatars).
        private bool IsSubscribedPrim(uint localID)
            => _framePrims.TryGetValue(localID, out JoltPrim p) && p.SubscribedEvents();

        private bool IsVolumeDetectPrim(uint localID)
            => _framePrims.TryGetValue(localID, out JoltPrim p) && p.IsVolumeDetectPart;

        // How far past a moved fixed prim's box a resting body is still woken: the engine's speculative contact distance
        // (0.02 m) with room to spare.
        private const float SupportWakeMargin = 0.05f;

        // Wakes every physical prim whose body overlaps the box at `center` (half extents `half`, grown by
        // SupportWakeMargin), except the prim `except`. Used when a fixed prim is moved: see JoltPrim.WakeWhatRestsOn.
        internal void WakePrimsAround(SVector3 center, SVector3 half, SQuaternion orientation, uint except)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null)
                return;
            // The query stops at the length of the span it is given, so a full span may mean more bodies overlap: ask again
            // with a span twice as long until one comes back with room to spare.
            Span<BodyId> found = stackalloc BodyId[64];
            SVector3 reach = half + new SVector3(SupportWakeMargin);
            int n = backend.OverlapBox(center, reach, orientation, QueryFilter.Dynamic, found);
            while (n == found.Length)
            {
                found = new BodyId[found.Length * 2];
                n = backend.OverlapBox(center, reach, orientation, QueryFilter.Dynamic, found);
            }
            for (int i = 0; i < n; i++)
            {
                if (!backend.TryGetBodyUserData(found[i], out uint id) || id == except)
                    continue;
                JoltPrim prim;
                lock (_prims)
                    _prims.TryGetValue(id, out prim);
                prim?.WakeAfterSupportMoved();
            }
        }

        // Whether a physical prim or an avatar stands on or against the box (within SupportWakeMargin of it), other than the prim
        // `except`: something that a move of the box would leave behind or throw. Used when a fixed prim is set to a new place
        // and [Jolt] MovedPrimsCarryRiders is on: see JoltPrim.NoteMoveSet.
        internal bool AnythingRestsOn(SVector3 center, SVector3 half, SQuaternion orientation, uint except)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null)
                return false;
            Span<BodyId> found = stackalloc BodyId[16];
            int n = backend.OverlapBox(center, half + new SVector3(SupportWakeMargin), orientation, QueryFilter.Dynamic | QueryFilter.Avatar, found);
            if (n == found.Length)
                return true;   // a crowd: some of it is not the prim itself
            for (int i = 0; i < n; i++)
                if (!backend.TryGetBodyUserData(found[i], out uint id) || id != except)
                    return true;
            return false;
        }

        // A child prim of a fixed linkset has been set from (fromPos, fromRot) to (toPos, toRot), a box of `size`: what stands on it
        // or rests on it (a physical prim, an avatar; not the prim `except`, the linkset's root, whose body is the compound) is
        // moved with it, by the same move and turn. The compound is rebuilt with the child in its new place at the next heartbeat; a
        // body of its own for the child would be needed to give its riders the child's velocity, so they take the child's steps
        // instead, one with each set. Used under [Jolt] MovedPrimsCarryRiders: see JoltPrim.MoveWithinLinkset.
        internal void CarryRidersOfChild(SVector3 fromPos, SQuaternion fromRot, SVector3 toPos, SQuaternion toRot, SVector3 size, uint except)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null)
                return;
            SQuaternion turn = SQuaternion.Normalize(toRot * SQuaternion.Inverse(fromRot));
            SVector3 half = size * 0.5f;
            Span<BodyId> found = stackalloc BodyId[32];
            int n = backend.OverlapBox(fromPos, half + new SVector3(SupportWakeMargin), fromRot, QueryFilter.Dynamic, found);
            for (int i = 0; i < n; i++)
            {
                if (!backend.TryGetBodyUserData(found[i], out uint id) || id == except)
                    continue;
                JoltPrim prim;
                lock (_prims)
                    _prims.TryGetValue(id, out prim);
                if (prim == null || !backend.TryGetBodyState(prim.BodyHandle, out BodyState st))
                    continue;
                SVector3 at = toPos + SVector3.Transform(st.Position - fromPos, turn);
                backend.SetBodyPoseAndVelocity(prim.BodyHandle, at, SQuaternion.Normalize(turn * st.Orientation), st.LinearVelocity, st.AngularVelocity);
                prim.WakeAfterSupportMoved();
            }

            // An avatar stands on the child when its feet are within SupportWakeMargin plus a step of the child's top and its middle is over
            // the child (the backend's avatar bodies do not tell which avatar they are, so the avatars are tested by where they are).
            JoltCharacter[] avatars;
            lock (_avatars)
                avatars = new List<JoltCharacter>(_avatars.Values).ToArray();
            SQuaternion unturn = SQuaternion.Inverse(fromRot);
            foreach (JoltCharacter avatar in avatars)
            {
                Vector3 now = avatar.Position;
                SVector3 rel = SVector3.Transform(new SVector3(now.X, now.Y, now.Z) - fromPos, unturn);
                float feet = rel.Z - avatar.Size.Z * 0.5f;
                if (MathF.Abs(rel.X) > half.X || MathF.Abs(rel.Y) > half.Y || feet < half.Z - 0.3f || feet > half.Z + 0.3f)
                    continue;
                SVector3 at = toPos + SVector3.Transform(rel, turn);
                avatar.Position = new Vector3(at.X, at.Y, at.Z);
            }
        }

        private bool AllStillTouchingAsleep(JoltPrim prim, CollisionEventUpdate set)
        {
            foreach (uint collider in set.m_objCollisionList.Keys)
                if (!StillTouchingAsleep(prim, collider))
                    return false;
            return true;
        }

        // A prim's contact with `collider` (0 = the ground) that the engine did not report this frame still holds when
        // neither side is awake and at least one is a physical body asleep: neither has moved since they last touched.
        // An avatar, or a prim that has gone, ends it.
        private bool StillTouchingAsleep(JoltPrim prim, uint collider)
        {
            if (prim.BodyAwake)
                return false;
            if (collider == 0)
                return prim.PhysicalAndAsleep;
            if (!_framePrims.TryGetValue(collider, out JoltPrim other) || other.BodyAwake)
                return false;
            return prim.PhysicalAndAsleep || other.PhysicalAndAsleep;
        }

        public override void SetTerrain(float[] heightMap)
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend == null || heightMap == null)
                return;
            int sx = _regionSizeX, sy = _regionSizeY;
            if (sx <= 0 || sy <= 0 || heightMap.Length < sx * sy)
            {
                m_log.LogWarning($"{LogHeader} SetTerrain: heightMap length {heightMap?.Length ?? 0} < {sx}x{sy}; ignoring.");
                return;
            }

            // Build the (N+1)-square sample field (also for var regions). A region of N metres ->
            // N+1 samples at 1 m spacing spans exactly [0, N] metres, so the far EDGE is covered (a
            // heightfield spans (samples - 1) * spacing, so an N-sample field would fall 1 m short). The extra row/column
            // duplicate the last real sample (fetching the neighbour region's row 0 is the later
            // refinement). Non-square regions pad to max(sx,sy) square by edge replication.
            // OpenSim serialises heightMap[y*sx + x] = height at (x,y) - the SAME convention as
            // CreateHeightFieldShape, so it feeds through with no transpose (the Z-up wrapper + row-mirror
            // fix inside the backend do the rest).
            int m = Math.Max(sx, sy) + 1;
            float[] field = new float[m * m];
            for (int y = 0; y < m; y++)
            {
                int srcRow = Math.Min(y, sy - 1) * sx;
                int dstRow = y * m;
                for (int x = 0; x < m; x++)
                    field[dstRow + x] = heightMap[srcRow + Math.Min(x, sx - 1)];
            }

            // 1 m sample spacing, heights already in metres (unit height scale), origin at the region
            // corner (physics runs in region-local coords).
            ShapeId newShape = backend.CreateHeightFieldShape(field, m, m, new SVector3(1f, 1f, 1f));
            backend.SetTerrain(newShape, SVector3.Zero);
            // At MaxBodies the engine refuses the terrain body - a region with no terrain collision is broken.
            if (backend.GetCapacityStats().TerrainBodyMissing)
                m_log.LogError($"{LogHeader} region '{RegionName}': the physics engine refused the terrain body (MaxBodies reached); " +
                               "this region has NO terrain collision - raise [Jolt] MaxBodies.");

            // Retain the cooked samples for TerrainHeightAt (vehicle hover/ground inputs) - the
            // exact field the collision surface was built from, so heights agree with contacts.
            _terrainField = field;
            _terrainFieldM = m;

            // Release the previous terrain shape: SetTerrain already replaced its body (dropping that
            // native ref), so releasing our handle frees it.
            if (_terrainShape.IsValid)
                backend.ReleaseShape(_terrainShape);
            _terrainShape = newShape;

            // Un-bury any avatar the raise left below the new surface (the terrain body was swapped out
            // from under its CharacterVirtual, which keeps its old Z). Runs only here, on a real terrain
            // edit (~5 s tainted cadence), and only lifts avatars now below the surface - a lowered terrain
            // leaves them above, to settle by normal gravity.
            ReGroundAvatarsOnTerrainChange();

            // Step-stamp + a couple of height samples so a [charframe] session can see whether SetTerrain
            // is re-firing during a walk (it should NOT - TerrainModule only ticks it every ~5 s when the
            // heightmap is tainted) and whether the heights it re-cooks are drifting downward.
            m_log.LogInformation($"{LogHeader} terrain set: step={_stepCount} {sx}x{sy} region -> {m}x{m} heightfield " +
                                 $"(spans {m - 1} m/side; sample[centre]={heightMap[(sy / 2) * sx + (sx / 2)]:0.000} sample[0]={heightMap[0]:0.000}).");
        }

        // Distance (m) a capsule centre must be below its seat before we treat it as buried and lift it.
        // Small enough that any real raise lifts, large enough to ignore float noise / a normal grounded
        // avatar sitting exactly at seatZ. Only ever lifts UP, so a modest value is safe either way.
        private const float TerrainUnburyEps = 0.05f;

        /// <summary>
        /// Pure un-bury decision (no physics state), isolated so it is unit-testable and used by the
        /// live pass (ReGroundAvatarsOnTerrainChange). Given a capsule centre Z, the new terrain
        /// surface at its XY, and its seat geometry, returns true + the seat Z it should snap to when the
        /// avatar is below the new surface (buried); false (leave it) when it is at or above the surface -
        /// so a LOWERED terrain never triggers a snap (avatar settles by gravity), and a prim-stander high
        /// above ground is never yanked down.
        /// </summary>
        internal static bool TryComputeUnbury(float currentCentreZ, float terrainZ, float standHalf, float feetOffset, float buriedEps, out float seatZ)
        {
            seatZ = terrainZ + standHalf + feetOffset;   // capsule centre that seats the feet ON the surface
            return currentCentreZ < seatZ - buriedEps;
        }

        /// <summary>
        /// Load-time position sanity (prim analog of <see cref="TryComputeUnbury"/>). A PHYSICAL prim
        /// whose centre is BELOW where it would rest on the terrain surface (terrainZ + halfHeightZ) is buried;
        /// return true + the rest Z to snap it to. A prim resting on the surface, or FLOATING above it (a boat
        /// on water), is at/above restZ, so this returns false and leaves it exactly where it is - the land
        /// box (control) and a floating boat are never touched. Pure (no physics state) so it is unit-testable.
        /// </summary>
        internal static bool TryComputeUnburyPrim(float currentCentreZ, float terrainZ, float halfHeightZ, float buriedEps, out float restZ)
        {
            restZ = terrainZ + halfHeightZ;   // prim centre resting ON the surface
            return currentCentreZ < restZ - buriedEps;
        }

        /// <summary>
        /// Load-time entry used by JoltPrim just before a physical body goes active: if <paramref name="pos"/> is
        /// below the terrain surface for a prim of <paramref name="size"/>, hand back the lifted position so the
        /// body is created RESTING on terrain instead of penetrating it. Applying this BEFORE the body is created
        /// stops (1) the bad position ever draining back to the SOP + persisting, and (2) the native solver
        /// churning on a deep-penetration load (a multi-second stall that can trip the watchdog on reload). No terrain yet -> no lift.
        /// </summary>
        internal bool TryUnburyPhysicalLoad(Vector3 pos, Vector3 size, out Vector3 lifted)
        {
            lifted = pos;
            if (_terrainField == null || _terrainFieldM < 2)
                return false;
            float terrainZ = TerrainHeightAt(pos.X, pos.Y);
            if (!TryComputeUnburyPrim(pos.Z, terrainZ, size.Z * 0.5f, TerrainUnburyEps, out float restZ))
                return false;
            lifted = new Vector3(pos.X, pos.Y, restZ);
            return true;
        }

        // After a live terrain edit, lift any avatar now below the new surface onto it (see SetTerrain).
        // Flying avatars are lifted too - a buried flyer can't rise through the solid heightfield above it.
        // Uses the same seat formula as spawn (groundZ + StandHalf + FeetOffset) and
        // the just-cooked _terrainField (via TerrainHeightAt), so the avatar lands exactly on the contact
        // surface. The reposition is gated in the backend, so it cannot race the per-step character update.
        private void ReGroundAvatarsOnTerrainChange()
        {
            List<JoltCharacter> avs;
            lock (_avatars)
                avs = new List<JoltCharacter>(_avatars.Values);

            foreach (JoltCharacter a in avs)
            {
                Vector3 p = a.Position;
                float terrainZ = TerrainHeightAt(p.X, p.Y);
                if (TryComputeUnbury(p.Z, terrainZ, a.StandHalf, a.FeetOffset, TerrainUnburyEps, out float seatZ))
                {
                    a.ReGround(new Vector3(p.X, p.Y, seatZ));
                    m_log.LogInformation($"{LogHeader} terrain-unbury: avatar {a.LocalID} lifted z={p.Z:0.000} -> seatZ={seatZ:0.000} " +
                                         $"(terrain now {terrainZ:0.000}, flying={a.Flying}).");
                }
            }
        }

        public override void SetWaterLevel(float baseheight)
        {
            WaterLevel = baseheight;   // vehicle hover (HoverWaterOnly) reads this
            _backend?.SetWaterHeight(baseheight);
        }

        public override void DeleteTerrain()
        {
            IPhysicsBackend backend = _backend;   // read once: a teardown on another thread nulls it
            if (backend != null && _terrainShape.IsValid)
                backend.ReleaseShape(_terrainShape);
            _terrainShape = ShapeId.Invalid;
        }

        // Up to 25 prims with a non-zero CollisionScore (this frame's contact count), highest first,
        // keyed by LocalID - ubODE's model. Note: Persist contacts are only generated for subscribed pairs, so a
        // resting pile of unscripted objects scores only on its Begin frames.
        public override Dictionary<uint, float> GetTopColliders()
        {
            var scored = new List<KeyValuePair<uint, float>>();
            lock (_prims)
                foreach (KeyValuePair<uint, JoltPrim> kv in _prims)
                    if (kv.Value.CollisionScore > 0f)
                        scored.Add(new KeyValuePair<uint, float>(kv.Key, kv.Value.CollisionScore));
            var top = new Dictionary<uint, float>();
            foreach (KeyValuePair<uint, float> kv in CollisionFrameTracker.TopColliders(scored, 25))
                top[kv.Key] = kv.Value;
            return top;
        }

        public override void Dispose()
        {
            // The backend's teardown frees every shape it still holds, the shared ones with the rest.
            lock (_sharedShapeLock)
            {
                _sharedShapes.Clear();
                _sharedByShape.Clear();
                _bases.Clear();
            }
            _backend?.Dispose();   // backend teardown -> Foundation.Shutdown
            _backend = null;
        }
    }
}
