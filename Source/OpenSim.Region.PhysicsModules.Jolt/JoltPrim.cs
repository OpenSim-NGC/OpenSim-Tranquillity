/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A prim as a Jolt rigid body.
//
// This is the PhysicsActor OpenSim hands back from AddPrimShape. NON-PHYSICAL prims become STATIC
// Jolt bodies (collision citizens that never move); physical prims become DYNAMIC bodies. Simple
// box / sphere / cylinder shapes cook straight to a Jolt primitive via the fixed-shape fast path (no
// meshmerizer); other shapes go through the region's IMesher. PhysicsActor members this class does not
// support are deliberately inert.
//
// Types: PhysicsActor speaks OpenMetaverse.Vector3/Quaternion (unqualified here); the backend speaks
// System.Numerics (SVector3/SQuaternion). Body orientation is composed in System.Numerics because the
// cylinder axis-correction ordering must be unambiguous (left operand applied first).

using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;
using VehicleCode = OpenSim.Region.PhysicsModules.Jolt.Vehicles.Vehicle;   // OpenSim.Region.PhysicsModules.Jolt.Vehicles' copy of the LSL wire codes (SharedBase also has a Vehicle enum)

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed partial class JoltPrim : PhysicsActor
    {
        private readonly JoltScene _module;
        private readonly IPhysicsBackend _backend;

        private PrimitiveBaseShape _pbs;
        private Vector3 _position;
        // The position this prim was CONSTRUCTED at; on a region load that is the DB-saved position. Read by
        // the `jolt reloadcheck` console command to report load-time displacement (saved vs settled).
        private readonly Vector3 _birthPos;
        private Vector3 _size;
        private Quaternion _orientation;
        private bool _isPhysical;
        // Set first thing in Destroy. The scene's per-frame lists refuse a prim once it is set, so a script's late write
        // to a removed prim's actor cannot put it back on a list the scene keeps.
        internal volatile bool IsRemoved;
        // Phantom (STATUS_PHANTOM) and volume detect (llVolumeDetect), and PRIM_PHYSICS_SHAPE_TYPE. The scene adds a
        // volume detector as phantom and then calls SetVolumeDetect(1); volume detect wins over phantom. On a welded
        // linkset child these are stored but the root's body carries the linkset's flags.
        private bool _isPhantom;
        private bool _isVolumeDetect;
        private byte _shapeType;
        private Vector3 _velocity;              // last drained linear velocity (the SOP reads this for terse updates)
        private Vector3 _rotationalVelocity;    // last drained angular velocity

        // SceneObjectPart.Density (simulator units, default 1000) x DensityScaleFactor = PHYSICAL density
        // (kg/m^3). The same 0.01 conversion BulletSim's BSParam.DensityScaleFactor applies; forwarding it
        // makes Jolt's mass match BulletSim (a 0.5^3 box: 0.125 x 10 = 1.25 kg, not 0.125 x 1000 = 125). 0
        // until AddToPhysics sets pa.Density.
        private const float DensityScaleFactor = 0.01f;
        private float _simDensity;

        // The physics material: llSetPhysicsMaterial and PRIM_MATERIAL. Ranges from the SL wiki (llSetPhysicsMaterial,
        // wiki.secondlife.com/wiki/LlSetPhysicsMaterial): gravity multiplier -1.0 to +28.0 (default 1.0), restitution 0.0 to
        // 1.0, friction 0.0 to 255.0, density 1.0 to 22587.0 kg/m^3 (default 1000.0). A value outside its range is clamped to it; a
        // non-finite one is refused. Core sets all four on every new actor (SceneObjectPart.AddToPhysics); until then a
        // prim has BodyDesc's friction and restitution.
        private float _friction = BodyDesc.Default.Friction;
        private float _restitution = BodyDesc.Default.Restitution;
        private float _gravityModifier = 1f;
        internal const float MaxFriction = 255f;
        internal const float MinGravityModifier = -1f;
        internal const float MaxGravityModifier = 28f;
        internal const float MinDensity = 1f;
        internal const float MaxDensity = 22587f;

        // PRIM_MATERIAL_STONE (0) to PRIM_MATERIAL_LIGHT (7): friction and restitution as the SL wiki lists them
        // (llSetPhysicsMaterial's notes and PRIM_MATERIAL's constants table). "Using PRIM_MATERIAL to set the material type
        // will reset the values for friction and restitution to that material's defaults."
        internal static readonly (float Friction, float Restitution)[] MaterialTable =
        {
            (0.8f, 0.4f),   // stone
            (0.3f, 0.4f),   // metal
            (0.2f, 0.7f),   // glass
            (0.6f, 0.5f),   // wood
            (0.9f, 0.3f),   // flesh
            (0.4f, 0.7f),   // plastic
            (0.9f, 0.9f),   // rubber
            (0.6f, 0.5f),   // light
        };

        // The same two numbers under [Jolt] BasicForcesBehaviour = InWorldz (the default): InWorldz's own preset table, from
        // Halcyon's InWorldz.PhysxPhysics/Material.cs (BuiltinMaterialInit, the first value of each CreateMaterial call; PhysX
        // has a static and a dynamic friction, Jolt one, so the static one is used). PRIM_MATERIAL_LIGHT is mapped to wood there
        // ("which makes no sense is remapped to wood").
        internal static readonly (float Friction, float Restitution)[] InWorldzMaterialTable =
        {
            (0.5f, 0.05f),  // stone, made "mostly like ground" for navigable roads
            (0.3f, 0.4f),   // metal
            (0.2f, 0.7f),   // glass
            (0.6f, 0.5f),   // wood
            (0.9f, 0.3f),   // flesh
            (0.4f, 0.7f),   // plastic
            (0.9f, 0.9f),   // rubber
            (0.6f, 0.5f),   // light, as wood
        };

        /// <summary>The preset table <paramref name="behaviour"/> selects.</summary>
        internal static (float Friction, float Restitution)[] MaterialTableFor(JoltConfig.BasicForces behaviour)
            => behaviour == JoltConfig.BasicForces.SecondLife ? MaterialTable : InWorldzMaterialTable;

        // The linear and angular damping of a physical prim, vehicles included, per second: [Jolt] PrimLinearDamping and
        // PrimAngularDamping, 0.02 and 0.05 by default (Halcyon's, PhysxActorFactory.cs DEFAULT_LINEAR_DAMPING and
        // DEFAULT_ANGULAR_DAMPING, applied to every physical actor). The SL wiki gives no figure (llSetPhysicsMaterial notes that two
        // objects of restitution 1.0 still do not bounce perfectly, because the engine damps; llSetAngularVelocity, that a
        // spun cube with gravity 0 slows down; wiki.secondlife.com). ubODE's, as a rate, are 0.1001 and 0.0250
        // (ODEPrim: dBodySetDamping(Body, .002f, .0005f), which ODE applies as v x (1 - scale) every 0.020 s step). Jolt
        // applies v x (1 - rate x step) each step, the same decay per second at any step rate. A vehicle's controller
        // does its own friction on top.
        private float PrimLinearDamping => _module.PrimLinearDamping;
        private float PrimAngularDamping => _module.PrimAngularDamping;

        // Set while a new physical body waits for its first activation (RegisterPendingActivation): a property set before
        // then must not wake it, or it would step before everything about it is set up.
        private bool _activationPending;

        private ShapeId _shape = ShapeId.Invalid;   // one handle-ref held for the prim's life
        private BodyId _body = BodyId.Invalid;
        // Assert buoyancy on restart: re-assert the vehicle's body params (gravity-cancellation,
        // no-sleep, ...) on every LIVE StepVehicle step that starts within this many seconds (the time left of
        // ReassertVehicleSeconds). The load-path assertion in the
        // VehicleType restore can be lost because the body was created (GravityFactor=1) with its activation
        // DEFERRED to the step thread, which drains the creation settings AFTER the load-thread SetGravityFactor
        // - so the body starts stepping under full gravity and sinks. Re-asserting from the step thread, on the
        // live body, makes it stick. Set when the vehicle becomes active; runtime llSetVehicleType sets it too
        // (harmless - the body is already live so it sticks first time).
        private float _reassertVehicleTime;
        // Whether that re-assertion also zeroes the body's velocity (below, in StepVehicle): for a vehicle made while the region
        // is loading, by a script or from its saved record; not for one made with the region running (a script's type call, or a
        // vehicle crossing in at speed, which keeps the velocity the simulator replays onto it), as on ubODE.
        private bool _reassertZeroesVelocity;

        // How long after a vehicle becomes active its body params are re-asserted and, with no linear motor set, its
        // velocity zeroed: the steps that start within this time, the same span at any step rate. 0.27 s is the three
        // heartbeats it was at the default FrameTime (0.0909 s).
        internal const float ReassertVehicleSeconds = 0.27f;

        // Maps the cooked shape's local axis onto SL's convention. Identity for box/sphere; a +90 deg
        // rotation about X for a cylinder (Jolt's CylinderShape axis is Y, SL cylinders are Z-height).
        // Composed as (correction * primOrientation) so the prim's own rotation still applies.
        private SQuaternion _axisCorrection = SQuaternion.Identity;
        private string _shapeKind = "?";

        private int _subscribedMs;   // collision-event subscription window; gates Persist forwarding

        // This prim's last non-finite-rejection log line (NonFiniteGuard rate limit, one per 10 s).
        private long _nonFiniteLogTicks;

        // Linksets. OpenSim adds each prim as its own PhysicsActor then calls child.link(root) per
        // child (SceneObjectGroup). We weld the children into the ROOT's body as a StaticCompoundShape:
        // one rigid body whose sub-shapes are the root + each child at its root-relative offset. The root
        // owns _linkChildren + _compoundShape; a welded child holds _linkRoot and has no body of its own.
        private System.Collections.Generic.List<JoltPrim> _linkChildren;   // root: welded children
        private JoltPrim _linkRoot;                                        // child: our compound root, if welded
        private ShapeId _compoundShape = ShapeId.Invalid;                  // root: the built compound (Invalid = single shape)

        internal BodyId BodyHandle => _body;
        internal string ShapeKind => _shapeKind;
        internal ShapeId ShapeHandle => _shape;
        // Read by the `jolt reloadcheck` / `jolt vehiclestatus` console commands.
        internal Vector3 BirthPos => _birthPos;
        internal Vector3 CurrentPos => _position;
        internal bool IsPhysicalBody => _isPhysical;
        internal bool IsVehicle => _vehicle != null;
        // The vehicle controller, null when the prim is not a vehicle. For tests.
        internal VehicleController VehicleControl => _vehicle;
        // The mass of this prim's body, the one the engine moves (a welded linkset's root: the whole linkset's); 0 without
        // a body. Read by the console test commands.
        internal float BodyMass => _body.IsValid ? _backend.GetBodyMass(_body) : 0f;
        /// <summary>The compound shape a linkset's root body is made of (invalid for anything else), for the tests.</summary>
        internal ShapeId CompoundHandle => _compoundShape;

        // Live vehicle state for the `jolt vehiclestatus` console command (type / buoyancy / active).
        internal string VehicleInfo()
        {
            if (_vehicle == null) return "no-vehicle";
            return $"type={_vehicle.Type} active={(_vehicle.IsActive ? "Y" : "N")} " +
                   $"buoyancy={_vehicle.GetFloatParam(VehFloatParam.Buoyancy):0.00} " +
                   $"hoverHeight={_vehicle.GetFloatParam(VehFloatParam.HoverHeight):0.00}";
        }

        // Vehicles: the backend-agnostic vehicle controller + its Jolt seam. Created lazily on
        // the first Vehicle* call; ACTIVE (stepped per-frame, body params applied) only while the
        // controller's type != NONE and the prim is physical. Setting TYPE_NONE destroys it (spec).
        private VehicleController _vehicle;
        private JoltVehicleBody _vehicleBody;

        // Body orientation -> PRIM orientation (undo the cylinder axis-correction; identity for
        // box/sphere/mesh). Same conversion the drain does in ApplyStepState.
        internal Quaternion PrimOrientationOf(SQuaternion bodyOrient)
        {
            SQuaternion prim = SQuaternion.Multiply(SQuaternion.Conjugate(_axisCorrection), bodyOrient);
            return new Quaternion(prim.X, prim.Y, prim.Z, prim.W);
        }

        internal JoltPrim(JoltScene module, IPhysicsBackend backend, uint localid, string name,
                          PrimitiveBaseShape pbs, Vector3 position, Vector3 size, Quaternion rotation, bool isPhysical,
                          bool isPhantom = false, byte shapeType = (byte)PhysShapeType.prim)
        {
            _module = module;
            _backend = backend;
            LocalID = localid;
            Name = name;
            _pbs = pbs;
            _position = position;
            _birthPos = position;   // saved pos on load; read by `jolt reloadcheck`
            _size = size;
            _orientation = rotation;
            _isPhysical = isPhysical;
            _isPhantom = isPhantom;
            _shapeType = shapeType;

            Build();
        }

        private SQuaternion BodyOrientationOf(Quaternion primRot)
            => SQuaternion.Multiply(_axisCorrection, ToS(primRot));   // correction first, then prim

        // What the body collides with: a volume detector is a sensor, a phantom touches only the terrain, and
        // otherwise a physical prim is dynamic and a non-physical one static.
        private PhysicsLayer BodyLayer
            => _isVolumeDetect ? PhysicsLayer.Sensor
             : _isPhantom ? PhysicsLayer.Phantom
             : _isPhysical ? PhysicsLayer.Dynamic
             : _kinematicBody ? PhysicsLayer.MovingStatic : PhysicsLayer.Static;

        // A mesher shape is cooked as a convex hull when the prim is physical (a triangle mesh has no volume, so no
        // mass), or when a non-mesh prim asks for PRIM_PHYSICS_SHAPE_CONVEX: "Use the convex hull formulas for
        // generating the prim's physics-shape" (PRIM_PHYSICS_SHAPE_TYPE, wiki.secondlife.com). One hull of every mesh
        // point would close the inside of every mesh building, so a non-physical mesh keeps its triangle mesh, unless
        // [Jolt] HonourConvexMesh is on: then a non-physical mesh marked CONVEX is one hull. A physical mesh of shape
        // type PRIM is cooked from its asset's hull list when it has one (UseHullList); this is the fallback hull.
        private bool CookAsHull
            => _isPhysical || (_shapeType == (byte)PhysShapeType.convex && (_pbs?.SculptType != (byte)SculptType.Mesh || _module.HonourConvexMesh));

        // A physical mesh prim of shape type PRIM collides as the hull list its asset carries, one convex hull per entry
        // (Second Life's behaviour for a physical mesh); the asset's list is cooked in JoltScene.CookHullList. A mesh without
        // one, a CONVEX mesh and every other prim are cooked as before.
        // A physical prim of shape type PRIM that is made from its own profile and path (not a mesh, not a sculpt) collides as
        // a compound of convex cells when it is not convex (JoltScene.CookPrimCells), so a hollow or cut prim keeps its
        // cavities; CONVEX stays one hull.
        private bool UseHullList => _isPhysical && _shapeType == (byte)PhysShapeType.prim;

        /// <summary>How many convex hulls this prim's shape is made of: its cells or its asset's hulls, else 1. The count a
        /// physics complexity limit adds up (1 for a simple shape plus one for each hull).</summary>
        internal int HullCount => _hullCount;
        private int _hullCount = 1;

        /// <summary>How many collision shapes this prim adds to its object: 1 for a plain box, sphere or cylinder, its hull count
        /// for any other shape (<see cref="HullCount"/>), none when its physics shape type is NONE. The sum over a physical
        /// object's parts is what [Jolt] MaxPhysicalComplexity limits and what the object reports as its physics cost. The same
        /// count Halcyon's PhysxPrim.TotalComplexity gives (one per simple shape plus one per convex hull).</summary>
        internal int ComplexityCount => _shapeType == (byte)PhysShapeType.none ? 0 : _hullCount;

        /// <summary>The prim's collision shape count, reported as its physics cost (the scene does not scale it by size).</summary>
        public override float PhysicsCost => ComplexityCount;
        public override bool PhysicsCostIsShapeCount => true;

        // Set while the object is being put together (core sets it on the root, then every part, then clears it): a check made
        // before it is cleared would only see part of the object.
        private bool _building;

        public override bool Building
        {
            get => _building;
            set
            {
                _building = value;
                if (!value)
                    CheckComplexity();
            }
        }

        /// <summary>The collision shapes in this prim's whole object: its root's and every part linked to the root.</summary>
        internal int ObjectComplexity()
        {
            JoltPrim root = _linkRoot ?? this;
            int total = root.ComplexityCount;
            System.Collections.Generic.List<JoltPrim> parts = root._linkChildren;
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                    total += parts[i].ComplexityCount;
            return total;
        }

        // [Jolt] MaxPhysicalComplexity: when this prim's object is physical and has more collision shapes than the limit, tell
        // core (as Halcyon's PhysxPrim does with TriggerComplexityError), which alerts the owner and makes the object non-physical.
        // Called wherever a count can grow: a prim made physical, a part linked in, a shape or size changed, a mesh arriving, the
        // end of building. Never inside the shape lock: core's answer takes the object out of physics.
        private void CheckComplexity()
        {
            // Halcyon checks the complexity before it builds the actor (DynamicsPrecheck) and the sizes after (DynamicsPostcheck):
            // an object refused for its complexity is not checked for its size, so the owner is told one reason.
            if (!CheckComplexityLimit())
                CheckPhysicalSize();
        }

        // True when the object was reported as too complex.
        private bool CheckComplexityLimit()
        {
            int limit = _module.MaxPhysicalComplexity;
            if (limit <= 0 || IsRemoved)
                return false;
            JoltPrim root = _linkRoot ?? this;
            if (!root._isPhysical || root._building)
                return false;
            int total = ObjectComplexity();
            if (total <= limit)
                return false;
            root.RaiseComplexityError(string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0} of {1} convex hulls)", total, limit));
            return true;
        }

        // Under [Jolt] BasicForcesBehaviour = InWorldz, Halcyon's limits on how small or large a physical object may be
        // (HalcyonPhysicalLimits; PhysxPrim.stability.cs DynamicsPostcheck): checked at the same moments as the complexity,
        // on the box that holds the whole object, lined up with the region's axes. Halcyon leaves the object inert with its
        // physical flag still set and tells nobody; so does this.
        private void CheckPhysicalSize()
        {
            if (_module.BasicForcesBehaviour != JoltConfig.BasicForces.InWorldz || IsRemoved)
                return;
            JoltPrim root = _linkRoot ?? this;
            if (!root._isPhysical || root._building)
                return;
            var min = new SVector3(float.PositiveInfinity);
            var max = new SVector3(float.NegativeInfinity);
            HalcyonPhysicalLimits.Include(ToS(root.Position), Unit(root.Orientation), ToS(root._size), ref min, ref max);
            System.Collections.Generic.List<JoltPrim> parts = root._linkChildren;
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                    HalcyonPhysicalLimits.Include(ToS(parts[i].Position), Unit(parts[i].Orientation), ToS(parts[i]._size), ref min, ref max);
            SVector3 extent = max - min;
            HalcyonPhysicalLimits.Verdict verdict = HalcyonPhysicalLimits.Check(extent.X, extent.Y, extent.Z);
            // Halcyon makes the actor kinematic, tells nobody and leaves the physical flag set (PhysxPrim.stability.cs,
            // EnforceMinimumDynamicDimensions and EnforceMaximumDynamicDimensions): the object is held where it is, like a
            // selected one, until a resize brings it inside the limits.
            bool refused = verdict != HalcyonPhysicalLimits.Verdict.Ok;
            if (refused != root._sizeHeld)
            {
                root._sizeHeld = refused;
                root.SetKeptStill(root._userSelected || root._held || refused);
            }
        }

        // The real volume (m^3, at the prim's size) a physical concave prim weighs when it is cooked as cells: the volume of the
        // mesher's closed mesh, which the cells (they hold the whole solid, a little over) stand in for in collision, the
        // centre of mass and how the mass is spread. 0 when the shape's own volume is the weight: every other shape, and a
        // concave prim whose mesh volume cannot be trusted (JoltScene.CookPrimCells). Set where the shape is cooked.
        private double _massVolume;
        private float _massFactor = 1f;   // _massVolume over the volume of the cooked cells: 1 for every other shape

        // A prim may take a cooked shape another prim of the same asset holds (at its size or scaled to it). A part of a linkset
        // may too, unless its density differs from the root's: the linkset gives each part's shape that part's own density
        // relative to the root's (SetShapeDensity), and a shape in use elsewhere keeps the engine's default. A part of the
        // root's own density needs none set, so it can use the shared shape (or a scaled shape over it, which takes no density).
        // A concave prim that weighs less than its cells' volume (_massFactor under 1) is given a density of its own in a
        // linkset as well (SetShapeDensity, see ShapeMassFactor), so in a linkset it takes a shape of its own too.
        private bool MayShareShape
            => _linkRoot != null ? PhysicalDensity == _linkRoot.PhysicalDensity && !NeedsOwnDensity
                                 : !NeedsOwnDensity || _linkChildren == null || _linkChildren.Count == 0;

        // Under this much (2 %) of difference between what a concave prim weighs and its cells' volume, its place in a linkset's
        // compound is the same to the eye as with the shared shape's default density.
        private const float OwnDensityMargin = 0.02f;
        private bool NeedsOwnDensity => MathF.Abs(_massFactor - 1f) > OwnDensityMargin;

        // Takes a shape of its own in place of a shared one, before this prim becomes a part of a linkset's compound, when
        // the part's density has to be written into its shape.
        private void MakeShapePrivate()
        {
            if (!_shape.IsValid || !_module.IsSharedShape(_shape) || MayShareShape)
                return;
            ShapeId old = _shape;
            _shape = CookOwnShape(out _axisCorrection, out _shapeKind);
            _ownMass = null;
            _module.ReleaseShape(_backend, old);
        }

        // The bytes of a mesh asset that has just arrived, set only while this prim is cooked from them (MeshAssetArrived).
        private byte[] _meshBytes;
        // Set when the last cook left a mesh prim a bounding box for want of its asset (RequestMeshAssetIfNeeded asks).
        private bool _meshAssetWanted;
        // Held while the shape is swapped or the prim is removed, so an asset arriving on another thread never cooks for,
        // or puts a shape on, a prim that is being destroyed.
        private readonly object _shapeLock = new object();

        // Cook this prim's shape as it is now. A mesh asset that has just arrived goes on a copy of the shape for the cook,
        // so the part's own shape never holds the bytes.
        private ShapeId CookOwnShape(out SQuaternion axisCorrection, out string kind)
        {
            PrimitiveBaseShape pbs = _pbs;
            byte[] bytes = _meshBytes;
            if (bytes != null && pbs != null)
            {
                pbs = pbs.Copy();
                pbs.SculptData = bytes;
            }
            // Cooked on the thread that built or changed the prim, never on the step: the one cook the step reaches
            // (RebuildCompoundNow -> MakeShapePrivate) is of a shared shape, and only a mesh asset's shape is shared, so a
            // prim cooked as cells is never cooked there.
            ShapeId shape = _module.CookPrimShape(_backend, pbs, _size, CookAsHull, out axisCorrection, out kind, out int hullCount, out double massVolume, _isPhysical, UseHullList, MayShareShape);
            _hullCount = hullCount;
            _massVolume = massVolume;
            _massFactor = massVolume > 0.0 ? ShapeMassFactorOf(shape, massVolume) : 1f;
            _meshAssetWanted = bytes == null && kind == JoltScene.BoxFallbackKind && _module.WantsMeshAsset(_pbs);
            return shape;
        }

        /// <summary>Asks the scene for this prim's mesh asset if the last cook was a box for want of it. Called once the prim
        /// is in the scene and after a re-cook, never inside a cook, because the answer can come before the call returns.</summary>
        internal void RequestMeshAssetIfNeeded()
        {
            PrimitiveBaseShape pbs = _pbs;
            if (!_meshAssetWanted || IsRemoved || pbs == null)
                return;
            _meshAssetWanted = false;
            _module.RequestMeshAsset(this, pbs.SculptTexture);
        }

        internal enum MeshArrival { Skipped, Cooked, Unreadable }

        /// <summary>The mesh asset this prim asked for has arrived: cook the shape from it, on the same body. Skipped when the
        /// prim has been removed or no longer uses that mesh.</summary>
        internal MeshArrival MeshAssetArrived(UUID assetId, byte[] data)
        {
            MeshArrival arrival = CookArrivedMesh(assetId, data);
            if (arrival == MeshArrival.Cooked && _isPhysical)
                CheckComplexity();
            return arrival;
        }

        private MeshArrival CookArrivedMesh(UUID assetId, byte[] data)
        {
            lock (_shapeLock)
            {
                PrimitiveBaseShape pbs = _pbs;
                if (IsRemoved || pbs == null || !(JoltScene.IsMeshPrim(pbs) || JoltScene.IsSculptPrim(pbs)) || pbs.SculptTexture != assetId)
                    return MeshArrival.Skipped;
                _meshBytes = data;
                try { Rebuild(); }
                finally { _meshBytes = null; }
                if (_shapeKind == JoltScene.BoxFallbackKind)
                    return MeshArrival.Unreadable;
                // What rested on the box now meets a different surface, and a body whose shape changed has contacts to find
                // again: wake it (a physical one at the next step, as a new body is) or what rests on a fixed one.
                if (_linkRoot != null)
                    _module.RegisterPendingActivation(_linkRoot);
                else if (_isPhysical)
                    _module.RegisterPendingActivation(this);
                else
                    WakeWhatRestsOn(_position, _orientation);
                return MeshArrival.Cooked;
            }
        }

        // Cook the shape and create the body. Also reached when a prim has lost its body (the engine refused one at
        // MaxBodies), so the shape it held is released once the new body is made.
        private void Build()
        {
            ShapeId old = _shape;
            _shape = CookOwnShape(out _axisCorrection, out _shapeKind);
            _ownMass = null;
            CreateBodyInternal();
            if (old.IsValid)
                _module.ReleaseShape(_backend, old);
        }

        // A part of a welded linkset: a child with no body of its own, or the root whose body is the compound. Such a part
        // never gets a body of its own while it is one; a change to its size, shape or place in the linkset rebuilds the
        // root's compound (RebuildCompoundNow), once, at the top of the next Simulate.
        private bool IsWeldedChild => _linkRoot != null && _welded;
        private bool IsCompoundRoot => _linkRoot == null && _compoundShape.IsValid;

        // Re-cook a welded part's shape for its new size, shape or physics state, and have its root rebuild the compound
        // with it. The compound holds its own reference to the old native shape until that rebuild releases it, so the
        // old handle is released now.
        private void RecookLinkedShape()
        {
            lock (_shapeLock)
            {
                ShapeId old = _shape;
                _shape = CookOwnShape(out _axisCorrection, out _shapeKind);
                _ownMass = null;
                if (old.IsValid)
                    _module.ReleaseShape(_backend, old);
                _module.MarkLinksetDirty(_linkRoot ?? this);
            }
            RequestMeshAssetIfNeeded();
        }

        // Create the Jolt body for the CURRENT _isPhysical / _shape / _axisCorrection and cached
        // transform + velocity. Non-physical -> Static (no MotionProperties: the 65k-prim startup guard).
        // Physical -> Dynamic + StartActive (wakes so it falls); mass computed Volume*Density (Density
        // from BodyDesc.Default = 1000). A body that may go physical is created movable ONLY when it is
        // physical (a Static-born body can't be promoted - the toggle recreates instead).
        //
        // Every physical prim has continuous collision detection when it is fast (ContinuousCollision.WhenFast), so a
        // small fast object does not pass through a thin wall, another object or the ground between two collision
        // steps. It is cast along its motion in the collision steps in which it would move further than Jolt's cast
        // threshold: PhysicsSettings.mLinearCastThreshold (0.75) times its shape's inner radius, the radius of the
        // largest sphere that fits inside it (PhysicsSystem::JobIntegrateVelocity). A 0.2 m ball is cast from 5 m/s at
        // one physics step per 11 Hz heartbeat (6 collision steps), a 1 m box from 25 m/s. In every other step it is a
        // Discrete body, so an object that is never that fast moves exactly as before. A cast that meets nothing it is
        // moving into (a box sliding along the ground) ends as a Discrete step would; one that does is stopped at the
        // hit and given the contact's impulse, from the two bodies' combined friction and restitution
        // (JobResolveCCDContacts). Jolt does not cast against sensors, so a volume-detect prim still lets a fast object
        // through, and the object layers keep a phantom one passing everything but the ground. A vehicle is cast all
        // the time (ApplyVehicleBodyParams).
        private const ContinuousCollision PhysicalPrimCcd = ContinuousCollision.WhenFast;

        private void CreateBodyInternal()
        {
            // Away from the region (crossing, or out of bounds) the prim has no body; a reshape or relink meanwhile keeps
            // its shapes for when it comes back.
            if (_away != Away.None)
                return;

            // Load-time position sanity: never bring a PHYSICAL body up penetrating the terrain. If
            // the saved/current centre is below where it rests on the surface, lift it there and zero its
            // velocity BEFORE the body goes active - so (1) the bad position never drains back + persists, and
            // (2) the native solver doesn't churn resolving a deep penetration (a multi-second load
            // stall). Only lifts BELOW-terrain bodies: a resting box or a boat FLOATING on water (above the
            // surface) is left exactly where it is. Zeroing velocity also stops the post-lift slide (a boat's
            // horizontal drift). The compound root lifts its whole welded set uniformly (sub-shape
            // offsets are relative to the body origin), so a linkset keeps its shape.
            if (_isPhysical && _module.TryUnburyPhysicalLoad(_position, _size, out Vector3 lifted))
            {
                _position = lifted;
                _velocity = Vector3.Zero;
                _rotationalVelocity = Vector3.Zero;
            }

            // A prim keyframed motion moves is made a kinematic body, which can be moved to a pose over a time and so carries
            // what stands on it; every other non-physical prim stays a body with no motion at all.
            _kinematicBody = KinematicWanted;
            _module.SetKinematicTracked(this, _kinematicBody);
            _kinMoving = false;
            _segActive = false;
            lock (_kinLock)
            {
                _kinTarget = _position;
                _kinRotation = _orientation;
                _kinPending = false;
            }

            BodyDesc desc = BodyDesc.Default;
            desc.Shape = _compoundShape.IsValid ? _compoundShape : _shape;   // linkset root -> the compound
            desc.Position = ToS(_position);
            desc.Orientation = BodyOrientationOf(_orientation);
            desc.LinearVelocity = ToS(_velocity);
            desc.AngularVelocity = ToS(_rotationalVelocity);
            desc.UserData = LocalID;                   // echoed back in every RayHit/contact/update - no lookup
            desc.WantsContactEvents = _subscribedMs > 0;   // keep the Persist gate across a body recreate (weld/reshape)
            desc.Layer = BodyLayer;
            desc.IsSensor = _isVolumeDetect;
            desc.Friction = _friction;                 // a compound's children get their own just below
            desc.Restitution = _restitution;
            if (_isPhysical)
            {
                desc.MotionType = BodyMotionType.Dynamic;
                desc.Mass = 0f;                        // <=0 -> backend computes Volume*Density
                desc.Density = PhysicalDensity;        // honour the SOP density (BulletSim mass parity)
                // A concave prim weighs its real volume: the body's inertia is the cells' scaled to that mass (a linkset's mass is
                // set just below from its parts).
                if (_massVolume > 0.0 && !_compoundShape.IsValid)
                    desc.Mass = LoneMass();
                desc.LinearDamping = PrimLinearDamping;
                desc.AngularDamping = PrimAngularDamping;
                desc.GravityFactor = ScriptGravityFactor;   // llSetBuoyancy (a vehicle sets its own just below)
                desc.Ccd = PhysicalPrimCcd;
                // Same structure as BulletSim's taint-deferred creation: create the body INERT (asleep), never
                // active-on-insert. BulletSim never lets a body be stepped by the engine until ALL taints
                // (create + MakeDynamic + SetVehicle/SetPhysicalGravity) have drained (ProcessTaints runs
                // BEFORE PE.PhysicsStep). Our equivalent barrier: create asleep, then ACTIVATE at the TOP of the
                // next Simulate (RegisterPendingActivation) - by which point AddToPhysics has fully run
                // (AddPrimShape + SetVehicle are synchronous) AND StepVehicles has asserted the vehicle's
                // gravity-cancellation for this frame. So a reloaded boat is NEVER a live gravity body before
                // its buoyancy is in force -> it cannot free-fall during the load / the reload stall.
                desc.StartActive = false;
            }
            else if (_kinematicBody)
            {
                desc.MotionType = BodyMotionType.Kinematic;
                desc.LinearVelocity = SVector3.Zero;   // it moves only when StepKinematic moves it
                desc.AngularVelocity = SVector3.Zero;
                desc.Mass = 0f;
                desc.Ccd = ContinuousCollision.Off;
                desc.StartActive = false;
            }
            else
            {
                desc.MotionType = BodyMotionType.Static;
                desc.Mass = 0f;
                desc.StartActive = false;              // never wake on insert (startup-stall guard)
            }
            _body = _backend.CreateBody(desc);

            // A linkset: each child's own friction and restitution, and a mass that sums each part's volume x its own
            // density (the SL wiki's Physics Material Settings test page allows different physics settings for each prim in
            // a linked set).
            if (_body.IsValid && _compoundShape.IsValid && _linkChildren != null)
            {
                foreach (JoltPrim c in _linkChildren)
                    _backend.SetBodyPartMaterial(_body, c.LocalID, c._friction, c._restitution);
                if (_isPhysical)
                    _backend.SetBodyMass(_body, LinksetMass());
            }

            if (_isPhysical)
            {
                _activationPending = true;
                // Defer activation to the top of the next Simulate (step thread). ActivateBody on a NON-step
                // thread does not reliably reach the step-thread active-set; enqueuing it via the pending set
                // (drained in Simulate before StepVehicles/StepOnce) both fixes that AND gives us BulletSim's
                // configure-before-step barrier - the body is asleep until every load-time property (incl. the
                // vehicle) is applied, so it never free-falls before its buoyancy is asserted.
                _module.RegisterPendingActivation(this);
                if (_backend.TryGetBodyState(_body, out BodyState st))
                    JoltScene.m_log.LogDebug(
                        $"{JoltScene.LogHeader} physical body id={LocalID} created (deferred activation): active={((st.Flags & BodyStateFlags.Active) != 0)} posZ={st.Position.Z:0.00} shape={_shapeKind}");

                // A recreate (reposition/reshape/weld/physical-toggle) makes a FRESH body with default
                // params; an active vehicle must re-assert its no-friction/no-damping/manual-gravity/
                // never-sleep setup on it.
                ApplyVehicleBodyParams();
                // A new body starts free to turn about every axis: re-apply the script's STATUS_ROTATE locks.
                if (_rotationLocks != 0)
                    ApplyRotationLocks();
                // A new body of an object that is selected (a relink, resize, physics toggle or unlink while it is being
                // edited) starts held, as the old one was.
                if (_selected && _linkRoot == null)
                    HoldSelected();
            }
        }

        // Called by JoltScene at the top of Simulate (step thread) to activate a physical body that was
        // created inert (deferred activation - the BulletSim configure-before-step barrier). By now every
        // load-time property is applied and the vehicle drive is about to run this frame, so waking the body
        // here means it enters the engine step already configured (gravity cancelled for a vehicle) - it never
        // free-falls. No-op for a non-physical/destroyed body or one already awake.
        internal void ActivatePending()
        {
            _activationPending = false;
            if (_body.IsValid && _isPhysical && !_selected)
                _backend.ActivateBody(_body);
        }

        // Drain: the backend reports this body's post-step transform + velocity. Update the cached values
        // the SOP reads (Position/Orientation/Velocity/RotationalVelocity) and fire the terse update so the
        // viewer sees the motion. The drained Orientation is the BODY orientation (= axisCorrection *
        // primOrientation); undo the correction to hand OpenSim the PRIM orientation (identity for
        // box/sphere/mesh - only cylinders carry a correction). Called once per active body per step, plus
        // one final time when the body sleeps (JustDeactivated) - which is the settle update that stops the
        // viewer interpolating a rested object.
        internal void ApplyStepState(in BodyState s)
        {
            // A kinematic prim's pose is the one its object was given (the body only follows it), and a drain that wrote the
            // body's place back could overwrite a pose set since the step.
            if (_kinematicBody)
                return;
            var newPos = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            // A position that is not a number is dropped (the last good transform is kept): handed to the simulator it
            // would become the object's position.
            if (!(float.IsFinite(newPos.X) && float.IsFinite(newPos.Y) && float.IsFinite(newPos.Z)))
            {
                JoltScene.m_log.LogWarning($"{JoltScene.LogHeader} [physglitch] body {LocalID} implausible pos {newPos} vel {s.LinearVelocity} - update dropped (no crossing)");
                return;
            }
            SQuaternion prim = SQuaternion.Multiply(SQuaternion.Conjugate(_axisCorrection), s.Orientation);
            var newRot = new Quaternion(prim.X, prim.Y, prim.Z, prim.W);
            var newVel = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            var newAngVel = new Vector3(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z);
            bool outOfBounds = false;
            lock (_awayLock)
            {
                // The body left the engine (CrossingStart on another thread) after this state was taken.
                if (_away != Away.None || !_body.IsValid)
                    return;
                if (_isPhysical && (newPos.Z < MinBodyZ || newPos.Z > MaxBodyZ))
                {
                    LeaveOutOfBounds(newPos, newRot);
                    outOfBounds = true;
                }
                else if (_isPhysical && !InRegion(newPos))
                {
                    if (_keptInsideBorder)
                        KeepAtBorder(newPos, newRot, newVel, newAngVel);
                    else
                        LeaveAcrossEdge(newPos, newRot, newVel, newAngVel);
                }
                else
                {
                    _position = newPos;
                    _orientation = newRot;
                    _velocity = newVel;
                    _rotationalVelocity = newAngVel;
                }
            }
            RequestPhysicsterseUpdate();
            if (outOfBounds)
                RaiseOutOfBounds(_position);
        }

        // ---------------------------------------------------------------------
        // Leaving the region. The SL wiki documents only what core does: an object with STATUS_DIE_AT_EDGE or
        // STATUS_RETURN_AT_EDGE "goes off world" (llSetStatus), which core decides (SceneObjectGroup.CrossAsync). For
        // the rest this is ubODE's (ODEPrim.UpdatePositionAndVelocity, CrossingStart, CrossingFailure):
        //   - Past the region's edge in x or y, the object waits 0.1 to 2 m outside at the height it crossed, with the
        //     velocity it left with, until core has crossed it (and removed the actor) or calls CrossingFailure. Core is
        //     told by the terse update (SceneObjectPart.PhysicsRequestingTerseUpdate) and hands that velocity to the
        //     neighbour (CrossPrimGroupIntoNewRegion).
        //   - A failed crossing puts it back half a metre inside (core moves it), 0.2 m higher, at rest, its vehicle
        //     motors off.
        //   - Below -100 m or above 100 000 m it is stopped there and OutOfBounds is raised once; core makes the object
        //     non-physical (SceneObjectPart.PhysicsOutOfBounds).
        // While it waits, and after it went out of bounds until core makes it non-physical, it has no body: nothing steps
        // it, nothing touches it and it sends no updates (ubODE disables its body and its collisions).
        // ---------------------------------------------------------------------
        internal const float MinBodyZ = -100f;      // ODEPrim.UpdatePositionAndVelocity
        internal const float MaxBodyZ = 100000f;
        private const float EdgeWaitMin = 0.1f, EdgeWaitMax = 2f;   // how far outside the edge it waits (ODEPrim)
        private enum Away : byte { None, Crossing, OutOfBounds }
        private volatile Away _away;
        private readonly object _awayLock = new object();
        internal bool HasLeftRegion => _away != Away.None;

        private bool InRegion(Vector3 p)   // Scene.PositionIsInCurrentRegion
            => p.X >= 0f && p.X < _module.RegionSizeX && p.Y >= 0f && p.Y < _module.RegionSizeY;

        // While an object that crossed in with riders waits before it may cross out again (PhysicsActor.KeepInsideBorder), the
        // body stays in the engine at the border. Taken out as for any crossing, it would leave the avatars standing on it with
        // nothing under them and the speed it gave them, and core would put it back at rest and higher, once a step for as long
        // as its script pushes it. Halcyon leaves the object in its scene and sets it back inside the region, stopped
        // (PhysxPrim.CrossingFailure). Here the body is set back to half a metre inside, as core puts a stayed object back
        // (SceneObjectGroup.CrossAsyncCompleted), loses its speed outward, and the avatars on it are set back with it.
        private volatile bool _keptInsideBorder;

        public override void KeepInsideBorder(bool keep) => _keptInsideBorder = keep && InWorldzBasicForces;

        private const float BorderKeepMargin = 0.5f;

        private void KeepAtBorder(Vector3 pos, Quaternion rot, Vector3 velocity, Vector3 angularVelocity)
        {
            float sx = _module.RegionSizeX, sy = _module.RegionSizeY;
            Vector3 inside = pos;
            inside.X = Math.Clamp(pos.X, BorderKeepMargin, sx - BorderKeepMargin);
            inside.Y = Math.Clamp(pos.Y, BorderKeepMargin, sy - BorderKeepMargin);
            if (inside.X != pos.X && Math.Sign(velocity.X) == Math.Sign(pos.X - inside.X)) velocity.X = 0f;
            if (inside.Y != pos.Y && Math.Sign(velocity.Y) == Math.Sign(pos.Y - inside.Y)) velocity.Y = 0f;
            Vector3 before = _position;
            _position = inside;
            _orientation = rot;
            _velocity = velocity;
            _rotationalVelocity = angularVelocity;
            _module.QueueBorderPin(this, before);
        }

        // The second half, after the avatars' step state is in (JoltScene.FlushBorderPins).
        internal void FinishBorderPin(Vector3 before)
        {
            lock (_awayLock)
            {
                if (_away != Away.None || !_body.IsValid)
                    return;
                _backend.SetBodyTransform(_body, ToS(_position), BodyOrientationOf(_orientation), activate: true);
                _backend.SetBodyLinearVelocity(_body, ToS(_velocity));
                _module.MoveCharactersStandingOn(_body, before, _position);
            }
        }

        private void LeaveAcrossEdge(Vector3 pos, Quaternion rot, Vector3 velocity, Vector3 angularVelocity)
        {
            float sx = _module.RegionSizeX, sy = _module.RegionSizeY;
            if (pos.X < 0f) pos.X = Math.Clamp(pos.X, -EdgeWaitMax, -EdgeWaitMin);
            else if (pos.X >= sx) pos.X = Math.Clamp(pos.X, sx + EdgeWaitMin, sx + EdgeWaitMax);
            if (pos.Y < 0f) pos.Y = Math.Clamp(pos.Y, -EdgeWaitMax, -EdgeWaitMin);
            else if (pos.Y >= sy) pos.Y = Math.Clamp(pos.Y, sy + EdgeWaitMin, sy + EdgeWaitMax);
            _position = pos;
            _orientation = rot;
            _velocity = velocity;
            _rotationalVelocity = angularVelocity;
            LeaveEngine(Away.Crossing);
        }

        private void LeaveOutOfBounds(Vector3 pos, Quaternion rot)
        {
            _position = new Vector3(pos.X, pos.Y, Math.Clamp(pos.Z, MinBodyZ, MaxBodyZ));
            _orientation = rot;
            _velocity = Vector3.Zero;
            _rotationalVelocity = Vector3.Zero;
            LeaveEngine(Away.OutOfBounds);
        }

        // Takes the body out of the engine. CreateBodyInternal makes none while the prim is away; CrossingFailure, or the
        // object made non-physical after it went out of bounds, ends that and makes a new one.
        private void LeaveEngine(Away why)
        {
            _away = why;
            if (_body.IsValid)
            {
                _backend.RemoveBody(_body);
                _body = BodyId.Invalid;
            }
        }

        // Core calls this when it starts crossing the object (SceneObjectGroup.CrossAsync), which is also how an object
        // moved or teleported out of the region by a script starts: the body leaves the engine with the velocity it had
        // (ODEPrim.CrossingStart). A body already outside has left already.
        public override void CrossingStart()
        {
            lock (_awayLock)
            {
                if (_away != Away.None || _linkRoot != null || !_isPhysical || !_body.IsValid)
                    return;
                if (_backend.TryGetBodyState(_body, out BodyState s))
                {
                    _velocity = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
                    _rotationalVelocity = new Vector3(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z);
                }
                LeaveEngine(Away.Crossing);
            }
        }

        // No neighbour took the object: core has put it back half a metre inside the edge and stopped it
        // (SceneObjectGroup.CrossAsyncCompleted). It comes back 0.2 m higher, at rest, with its vehicle motors off, and is
        // simulated again (ODEPrim.CrossingFailure, ODEDynamics.Stop).
        public override void CrossingFailure()
        {
            lock (_awayLock)
            {
                if (_away != Away.Crossing)
                    return;
                _position.X = Math.Clamp(_position.X, 0.5f, _module.RegionSizeX - 0.5f);
                _position.Y = Math.Clamp(_position.Y, 0.5f, _module.RegionSizeY - 0.5f);
                _position.Z = Math.Clamp(_position.Z + 0.2f, Constants.MinSimulationHeight, Constants.MaxSimulationHeight);
                _velocity = Vector3.Zero;
                _vehicle?.StopMotors();
                _away = Away.None;
                CreateBodyInternal();
            }
            RequestPhysicsterseUpdate();
        }

        // Re-cook the shape (resize / shape swap) keeping the same body. Release order mirrors the
        // terrain path: swap the body onto the new shape first, then release the old handle-ref.
        private void Rebuild()
        {
            if (IsWeldedChild || IsCompoundRoot) { RecookLinkedShape(); return; }
            lock (_shapeLock)
            {
                if (!_body.IsValid)
                    Build();
                else
                {
                    ShapeId old = _shape;
                    _shape = CookOwnShape(out _axisCorrection, out _shapeKind);
                    _ownMass = null;
                    _backend.SetBodyShape(_body, _shape, recomputeMass: false);   // keeps the body at its current transform
                    // The new shape's mass (and inertia, keeping any rotation locks): volume x density, as at creation. Without
                    // this a resized physical prim kept the mass of its old size, and llGetMass with it.
                    if (_isPhysical)
                        SetLoneBodyMass();
                    if (old.IsValid)
                        _module.ReleaseShape(_backend, old);
                }
            }
            RequestMeshAssetIfNeeded();
        }

        // Called by JoltScene.RemovePrim. RemoveBody drops the body's native shape ref; releasing
        // our handle-ref then frees the shape - no leak, no premature free.
        internal void Destroy()
        {
            IsRemoved = true;
            lock (_shapeLock)
                DestroyLocked();
        }

        private void DestroyLocked()
        {
            // Drop out of the scene's per-frame vehicle drive (no-op if never a vehicle).
            if (_vehicle != null) { _module.UnregisterVehicle(this); DetachVehicleStepProvider(); _vehicle = null; _vehicleBody = null; _vehicleStepProvider = null; }
            _module.SetScriptForced(this, false);
            _scriptForceProvider?.Detach();
            _module.SetTargeted(this, false);
            _module.SetKinematicTracked(this, false);
            // If welded into a parent compound, detach first (parent rebuilds without us).
            _welded = false;
            if (_linkRoot != null) { JoltPrim r = _linkRoot; _linkRoot = null; r.UnlinkChild(this); }
            // If we are a compound root, orphan our welded children (group teardown removes them anyway).
            if (_linkChildren != null)
            {
                foreach (JoltPrim c in _linkChildren) if (ReferenceEquals(c._linkRoot, this)) c._linkRoot = null;
                _linkChildren.Clear();
            }
            if (_body.IsValid)
                _backend.RemoveBody(_body);
            _body = BodyId.Invalid;
            if (_compoundShape.IsValid) { _backend.ReleaseShape(_compoundShape); _compoundShape = ShapeId.Invalid; }
            if (_shape.IsValid)
                _module.ReleaseShape(_backend, _shape);
            _shape = ShapeId.Invalid;
        }

        private static SVector3 ToS(Vector3 v) => new SVector3(v.X, v.Y, v.Z);
        private static SQuaternion ToS(Quaternion q) => new SQuaternion(q.X, q.Y, q.Z, q.W);

        // ---------------------------------------------------------------------
        // PhysicsActor contract. Real state: Position / Orientation / Size (pushed to the body).
        // Members not wired to the body below are inert.
        // ---------------------------------------------------------------------

        // A welded child is wherever its root's body has carried it: its root's pose with its offset in the linkset.
        public override Vector3 Position
        {
            get { if (IsWeldedChild) SyncFromLink(); return _position; }
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Position", value.ToString()); return; }
                if (IsWeldedChild) { MoveWithinLinkset(value, null); return; }
                if (_position == value) return;   // the drain writes _position directly; only a real move recreates
                Vector3 was = _position;
                _position = value;
                if (_body.IsValid)
                {
                    // Keyframed motion sets the pose about every 50 ms: the body is moved to the last one at the next step.
                    if (_kinematicBody && !_module.IsRegionLoading) NoteKinematicPose(value, null);
                    else if (WatchesMoves && NoteMoveSet(was, _orientation)) { }   // a series of plain sets began: the body is kinematic
                    else { RepositionBody(); WakeWhatRestsOn(was, _orientation); }
                }
            }
        }

        public override Quaternion Orientation
        {
            get { if (IsWeldedChild) SyncFromLink(); return _orientation; }
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Orientation", value.ToString()); return; }
                if (IsWeldedChild) { MoveWithinLinkset(null, value); return; }
                if (_orientation == value) return;
                Quaternion was = _orientation;
                _orientation = value;
                if (_body.IsValid)
                {
                    if (_kinematicBody && !_module.IsRegionLoading) NoteKinematicPose(null, value);
                    else if (WatchesMoves && NoteMoveSet(_position, was)) { }
                    else { RepositionBody(); WakeWhatRestsOn(_position, was); }
                }
            }
        }

        // Move the body IN PLACE to the current cached transform via the backend's real reposition
        // (BodyInterface.SetPositionAndRotation, JoltPhysicsSharp 2.19.1). This preserves velocity,
        // contacts, and the BodyID - it does NOT destroy+recreate. That recreate was the root of the
        // every-frame rebuild loop: OpenSim's SOP->physics sync pushes the transform each frame for a
        // moving object, and the old remove+recreate zeroed a never-settling vehicle's velocity + re-inerted
        // it every ~30 ms (an airplane vehicle became uncontrollable / fell through). Only PURE position/orientation
        // moves reach here (Size/Shape -> Rebuild, physical toggle -> RecreateBody still do full rebuilds).
        //
        // A move or turn wakes a sleeping physical body, as ubODE's does (ODEPrim changePosition and changeOrientation
        // enable a disabled body), so it carries on from where it was put and is never left hanging asleep in the air.
        // Not while the region loads or before the body's first activation: a load-time body created inert (deferred
        // activation) stays inert until DrainPendingActivation, preserving the configure-before-step barrier (a reloaded
        // vehicle never free-falls before its gravity-cancel is asserted). Not while the object is selected either: it is
        // held where the build tool puts it. Orientation goes through BodyOrientationOf (axis-correction applied),
        // matching CreateBodyInternal.
        private void RepositionBody()
        {
            if (!_body.IsValid) { Build(); return; }

            // Load-time un-bury. A remove+recreate reposition would re-run CreateBodyInternal, so a load-time
            // Position push carrying a saved BELOW-terrain position would be lifted (TryUnburyPhysicalLoad)
            // before the body went active. In-place
            // SetBodyTransform skips CreateBodyInternal, so without this a penetrating load position would be
            // pushed straight into the terrain -> the CollisionSteps=6 solver churns resolving the deep
            // penetration -> a multi-second boot stall. Re-apply the SAME un-bury here, reusing the shared helper.
            //
            // GATED on IsRegionLoading ONLY: a LIVE vehicle repositioning (e.g. a car dipping below terrain on
            // a bump mid-drive) must NOT be snapped up - that is real runtime motion, not a bad load position.
            if (_isPhysical && _module.IsRegionLoading
                && _module.TryUnburyPhysicalLoad(_position, _size, out Vector3 lifted))
            {
                _position = lifted;
                _velocity = Vector3.Zero;              // no post-lift slide (matches CreateBodyInternal's un-bury)
                _rotationalVelocity = Vector3.Zero;
                _backend.SetBodyLinearVelocity(_body, ToS(_velocity));
                _backend.SetBodyAngularVelocity(_body, ToS(_rotationalVelocity));
            }

            _backend.SetBodyTransform(_body, ToS(_position), BodyOrientationOf(_orientation), activate: MayWake);
        }

        // A fixed prim moved or turned wakes the physical objects resting on or against it, where it was and where it is
        // now. The engine wakes nothing when a static body moves, so without this an object left asleep on a platform that
        // is moved away would hang in the air until something else woke it. Not while the region loads.
        private void WakeWhatRestsOn(Vector3 wasAt, Quaternion wasTurned)
        {
            if (_isPhysical || !_body.IsValid || _module.IsRegionLoading)
                return;
            SVector3 half = ToS(_size * 0.5f);
            _module.WakePrimsAround(ToS(wasAt), half, ToS(wasTurned), LocalID);
            _module.WakePrimsAround(ToS(_position), half, ToS(_orientation), LocalID);
        }

        // Wakes this prim's body after something it rested on moved (see WakeWhatRestsOn).
        internal void WakeAfterSupportMoved() => (_linkRoot ?? this).WakeBody();

        public override Vector3 Size
        {
            get => _size;
            set
            {
                if (!NonFiniteGuard.OkSize(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Size", value.ToString()); return; }
                if (_size == value) return;
                _size = value;
                Rebuild();
                if (_isPhysical)
                    CheckComplexity();
            }
        }

        public override PrimitiveBaseShape Shape
        {
            set
            {
                _pbs = value;
                Rebuild();
                if (_isPhysical)
                    CheckComplexity();
            }
        }

        public override int PhysicsActorType { get => (int)ActorTypes.Prim; set { } }

        public override bool IsPhysical
        {
            get => _isPhysical;
            set
            {
                if (_isPhysical == value) return;
                _isPhysical = value;
                // Out of bounds, core makes the object non-physical (SceneObjectPart.PhysicsOutOfBounds): it gets its
                // fixed body where it stopped.
                if (!value && _away == Away.OutOfBounds)
                    lock (_awayLock) _away = Away.None;
                // A Static-born body has no MotionProperties and CANNOT be promoted
                // (SetBodyMotionType throws), so the toggle RECREATES the body. It also re-cooks the shape:
                // a physical MESH must become a convex hull (mesh Volume=0 -> mass 0), non-physical reverts
                // to a triangle mesh. Transform + velocity carry over. No taint (Jolt is concurrent).
                RecreateBody();
                if (value)
                    CheckComplexity();
            }
        }

        // Recreate the body for a changed _isPhysical (mesh<->hull, static<->dynamic), preserving
        // transform + velocity. Order mirrors Rebuild: cook new shape, drop old body, create new body,
        // release old shape handle - leak-free.
        //
        // A welded child stays in the compound while its root is physical and it is too (core turns a whole linkset
        // physical root first, then each child, then links each child). Turned non-physical, it gets a static body of its
        // own where it is now on the object (core turns the root non-physical first and delinks each child after).
        private void RecreateBody()
        {
            if (IsWeldedChild)
            {
                SyncFromLink();
                if (_isPhysical && _linkRoot._isPhysical)
                {
                    RecookLinkedShape();
                    return;
                }
                _welded = false;
                _module.MarkLinksetDirty(_linkRoot);
            }
            lock (_shapeLock)
            {
                ShapeId old = _shape;
                _shape = CookOwnShape(out _axisCorrection, out _shapeKind);
                _ownMass = null;
                if (_body.IsValid)
                    _backend.RemoveBody(_body);
                CreateBodyInternal();
                if (old.IsValid)
                    _module.ReleaseShape(_backend, old);
            }
            RequestMeshAssetIfNeeded();
        }

        // This prim's own mass: core adds up every part's (SceneObjectGroup.GetMass) for llGetMass and llGetObjectMass, as
        // ubODE's per-part mass (ODEPrim.Mass, density x volume for every prim) is added up. A lone physical prim reads its
        // body's mass, the one the engine moves; any other prim (non-physical, a linkset's root or a welded child) its own
        // volume x density. The SL wiki gives a non-physical object a mass like any other (llGetMass returns the object's
        // mass, wiki.secondlife.com/wiki/LlGetMass), and mass is density times volume (its Physics Material Settings test).
        public override float Mass
        {
            get
            {
                if (_isPhysical && _body.IsValid && !_compoundShape.IsValid && _linkRoot == null)
                    return _backend.GetBodyMass(_body);
                OwnMassData own = OwnMass();
                return own == null ? 0f : MathF.Max(own.Volume * PhysicalDensity, 1e-3f);
            }
        }

        // The volume and centre of mass of this prim's shape as it is when physical, so neither reading changes when
        // physics is switched on or off. A non-physical prim that is not a box, sphere or cylinder collides as its triangle
        // mesh, which has no volume; its physical shape (the convex hull) is cooked once to read them, then released.
        private sealed class OwnMassData
        {
            public float Volume;
            public SVector3 CenterOfMass;          // in the shape's frame
            public SQuaternion AxisCorrection;     // that shape's axis correction
        }
        private OwnMassData _ownMass;              // null until read; cleared whenever the shape is cooked again

        private OwnMassData OwnMass()
        {
            OwnMassData own = _ownMass;
            if (own != null)
                return own;
            ShapeId shape = _shape;
            if (!shape.IsValid)
                return null;
            own = new OwnMassData { AxisCorrection = _axisCorrection };
            if (_isPhysical || _backend.GetShapeVolume(shape) > 0f)
            {
                own.Volume = OwnVolume;
                own.CenterOfMass = _backend.GetShapeCenterOfMass(shape);
            }
            else
            {
                // The shape it would have if it were physical: for shape type PRIM that is its convex cells, so llGetMass reads
                // the same before and after physics is switched on. Cooked on the caller's thread (a script's), once: the
                // result is kept in _ownMass until the shape is cooked again.
                ShapeId hull = _module.CookPrimShape(_backend, _pbs, _size, true, out SQuaternion correction, out _, out _, out double hullMassVolume,
                                                     null, _shapeType == (byte)PhysShapeType.prim);
                try
                {
                    own.Volume = hullMassVolume > 0.0 ? (float)hullMassVolume : _backend.GetShapeVolume(hull);
                    own.CenterOfMass = _backend.GetShapeCenterOfMass(hull);
                    own.AxisCorrection = correction;
                }
                finally
                {
                    if (hull.IsValid)
                        _module.ReleaseShape(_backend, hull);
                }
            }
            _ownMass = own;
            return own;
        }

        // OpenSim sets pa.Density = SceneObjectPart.Density in AddToPhysics (default 1000). Store it and
        // forward the PHYSICAL density (x DensityScaleFactor) so Jolt's mass matches BulletSim. A live
        // physical body recomputes immediately; a not-yet-physical prim applies it at CreateBodyInternal.
        // The SL wiki: mass is density times the true volume of the shape (its Physics Material Settings
        // test), and llGetMass is in lindograms, kg / 100. A welded child's density counts in its root's mass.
        public override float Density
        {
            get => _simDensity > 0f ? _simDensity : BodyDesc.Default.Density;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Density", value.ToString()); return; }
                value = Math.Clamp(value, MinDensity, MaxDensity);
                if (_simDensity == value) return;
                _simDensity = value;
                // A part of a linkset changes where the linkset's mass is, so its compound is rebuilt with the new
                // density (RebuildCompoundNow), which also wakes it.
                if (_linkRoot != null || IsCompoundRoot)
                    _module.MarkLinksetDirty(_linkRoot ?? this);
                else
                    ApplyMass();
            }
        }

        // The density the backend works in (kg/m^3): the SOP density x DensityScaleFactor.
        private float PhysicalDensity => _simDensity > 0f ? _simDensity * DensityScaleFactor : BodyDesc.Default.Density;

        // The one place a prim's mass source is decided: the volume it weighs (m^3), times its density. For most shapes that is the
        // volume of the cooked shape. A physical concave prim cooked as convex cells weighs the real volume of its shape, the
        // volume of the mesher's closed mesh (_massVolume), as the SL wiki's mass test pages measure it; the cells give its
        // collision shape, its centre of mass and how the mass is spread, and their inertia is scaled to the real mass
        // (SetBodyMass). Where the mesh volume cannot be trusted _massVolume is 0 and the cells' volume is the weight, as a
        // physical mesh with a hull list weighs its hulls. Read by OwnMass (llGetMass, the centre of mass of a prim that is not
        // a lone body) and LinksetMass (a welded linkset's body); a lone body is given LoneMass by SetLoneBodyMass.
        private float OwnVolume => _massVolume > 0.0 ? (float)_massVolume : _backend.GetShapeVolume(_shape);

        private float LoneMass() => MathF.Max((float)_massVolume * PhysicalDensity, 1e-3f);

        // A lone physical body's mass: its real volume x density when it has one, else its density, from which the backend
        // takes the shape's own volume.
        private void SetLoneBodyMass()
        {
            if (_massVolume > 0.0)
                _backend.SetBodyMass(_body, LoneMass());
            else
                _backend.SetBodyDensity(_body, PhysicalDensity);
        }

        // How much of its cells' volume a prim weighs: the real volume over the cells' (1 for every other shape). A linkset gives
        // each part's cells a density times this, so the part's centre of mass and inertia in the compound are weighted by
        // what it weighs, not by the cells' volume, which is over the real one most for thin walls.
        private float ShapeMassFactor() => _massFactor;

        private float ShapeMassFactorOf(ShapeId shape, double massVolume)
        {
            float cells = _backend.GetShapeVolume(shape);
            return cells > 0f ? (float)massVolume / cells : 1f;
        }

        // A welded linkset's mass: each part's volume x its own density.
        private float LinksetMass()
        {
            float mass = OwnVolume * PhysicalDensity;
            foreach (JoltPrim c in _linkChildren)
                mass += c.OwnVolume * c.PhysicalDensity;
            return MathF.Max(mass, 1e-3f);
        }

        // The body's mass from its density (its parts' densities for a welded linkset), and the body woken: a heavier or
        // lighter body moves differently under the same forces.
        private void ApplyMass()
        {
            if (!_body.IsValid || !_isPhysical)
                return;
            if (_compoundShape.IsValid && _linkChildren != null && _linkChildren.Count > 0)
                _backend.SetBodyMass(_body, LinksetMass());
            else
                SetLoneBodyMass();
            WakeBody();
        }

        // Wakes the body after a script changed how it moves. Not while the region loads, nor before a new body's first
        // activation: it is woken then, once everything about it is set up (CreateBodyInternal). Not while the object is
        // selected: it wakes when it is let go.
        private void WakeBody()
        {
            if (MayWake && _body.IsValid)
                _backend.ActivateBody(_body);
        }

        private bool MayWake => _isPhysical && !_selected && !_activationPending && !_module.IsRegionLoading;

        // llSetPhysicsMaterial FRICTION and RESTITUTION, and PRIM_MATERIAL (SetMaterial). Each prim of a linkset has its own
        // (SL wiki, Physics Material Settings test: "Can individual prims in a linked set have different Physics settings?
        // Yes."); a contact uses the struck prim's. Two touching surfaces combine by [Jolt] FrictionCombine and
        // RestitutionCombine (JoltPhysicsBackend.CombineMaterials); the SL wiki gives no rule.
        public override float Friction
        {
            get => _friction;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Friction", value.ToString()); return; }
                SetContactMaterial(Math.Clamp(value, 0f, MaxFriction), _restitution);
            }
        }

        public override float Restitution
        {
            get => _restitution;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Restitution", value.ToString()); return; }
                SetContactMaterial(_friction, Math.Clamp(value, 0f, 1f));
            }
        }

        // PRIM_MATERIAL: the material's friction and restitution (MaterialTable). Core calls this when the material changes,
        // and on every new actor before it sets Friction and Restitution (SceneObjectPart.AddToPhysics). Any other value is
        // ignored.
        public override void SetMaterial(int material)
        {
            if (material < 0 || material >= MaterialTable.Length)
                return;
            (float friction, float restitution) = MaterialTableFor(_module.BasicForcesBehaviour)[material];
            SetContactMaterial(friction, restitution);
        }

        private void SetContactMaterial(float friction, float restitution)
        {
            if (_friction == friction && _restitution == restitution)
                return;
            _friction = friction;
            _restitution = restitution;
            JoltPrim root = _linkRoot ?? this;
            if (!root._body.IsValid)
                return;
            if (root._vehicle != null && root._vehicle.IsActive)
            {
                // A vehicle keeps its parts' materials (unless a friction override is set), so the new one reaches the body.
                root.ApplyVehicleContactMaterial();
                return;
            }
            _backend.SetBodyPartMaterial(root._body, LocalID, friction, restitution);
            root.WakeBody();
            // What rests on a non-physical prim now touches a different surface: wake it, as when the prim is moved.
            WakeWhatRestsOn(_position, _orientation);
        }

        // llSetPhysicsMaterial GRAVITY_MULTIPLIER: the body's gravity is (1 - buoyancy) x the multiplier x the region's, as
        // ubODE computes it (ODEPrim.Move). The root prim's applies to the whole linkset, as in ubODE; a child's is kept.
        public override float GravModifier
        {
            get => _gravityModifier;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "GravModifier", value.ToString()); return; }
                value = Math.Clamp(value, MinGravityModifier, MaxGravityModifier);
                if (_gravityModifier == value) return;
                _gravityModifier = value;
                if (_linkRoot == null)
                    ApplyScriptGravity();
            }
        }
        public override bool Stopped => true;

        public override Vector3 GeometricCenter => _position;

        // llGetCenterOfMass, in region coordinates. The SL wiki (wiki.secondlife.com/wiki/LlGetCenterOfMass): it gives the
        // object's centre of mass, and a call from a child prim gives that child's instead.
        // Core reads a physical object's from its root's actor and a child's from the child's actor; for a non-physical
        // object it takes the mass-weighted mean of every part's (SceneObjectGroup.GetCenterOfMass). So a physical
        // linkset's root reports the whole linkset's, each part's weighted by its own volume x density, and every other
        // prim its own part's, as ubODE does (ODEPrim.CenterOfMass: the body's position, or the prim's own centre).
        public override Vector3 CenterOfMass
        {
            get
            {
                if (_isPhysical && _linkRoot == null && _compoundShape.IsValid && _linkChildren != null && _linkChildren.Count > 0)
                    return LinksetCenterOfMass();
                return OwnCenterOfMass(out _);
            }
        }

        private Vector3 LinksetCenterOfMass()
        {
            Vector3 sum = OwnCenterOfMass(out float total);
            sum *= total;
            JoltPrim[] children = _linkChildren.ToArray();
            foreach (JoltPrim c in children)
            {
                Vector3 com = c.OwnCenterOfMass(out float m);
                sum += com * m;
                total += m;
            }
            return total > 0f ? sum / total : _position;
        }

        // This prim's own centre of mass in region coordinates, and its own mass. A welded child is placed by its root's
        // body, which is all the engine moves: the child sits where it was welded (WeldOffset) in the root's frame.
        private Vector3 OwnCenterOfMass(out float mass)
        {
            OwnMassData own = OwnMass();
            mass = own == null ? 0f : MathF.Max(own.Volume * PhysicalDensity, 1e-3f);
            SVector3 bodyPos;
            SQuaternion primRot;
            if (IsWeldedChild)
                LinkedPose(out bodyPos, out primRot);
            else
            {
                bodyPos = ToS(_position);
                primRot = ToS(_orientation);
            }
            if (own == null)
                return new Vector3(bodyPos.X, bodyPos.Y, bodyPos.Z);
            SVector3 com = bodyPos + SVector3.Transform(own.CenterOfMass, SQuaternion.Multiply(own.AxisCorrection, primRot));
            return new Vector3(com.X, com.Y, com.Z);
        }

        // A welded child: its place in the linkset, in its root prim's frame (position and prim rotation relative to the
        // root prim's), as core last put it (SceneObjectPart.OffsetPosition and RotationOffset hand the child's actor its
        // region position and rotation) or as it was when welded. The compound is built from these, so a rebuild after the
        // linkset has moved keeps every part where it is on the object.
        private bool _welded;
        private SVector3 _linkOffset;
        private SQuaternion _linkRotation = SQuaternion.Identity;

        // How far a welded child must be moved or turned within its linkset before the compound is rebuilt: core hands the
        // child its region position and rotation, worked out from the root's, whenever any part of the object is moved,
        // and those carry float rounding.
        internal const float LinkMoveTolerance = 1e-3f;          // m
        internal const float LinkTurnTolerance = 1e-3f;          // rad

        private static SQuaternion Unit(Quaternion q) => SQuaternion.Normalize(ToS(q));

        // A welded child's region position and prim rotation now: its root's pose with its offset.
        private void LinkedPose(out SVector3 position, out SQuaternion primRotation)
        {
            JoltPrim root = _linkRoot;
            SQuaternion rootRot = Unit(root._orientation);
            position = ToS(root._position) + SVector3.Transform(_linkOffset, rootRot);
            primRotation = SQuaternion.Normalize(SQuaternion.Multiply(rootRot, _linkRotation));
        }

        // Brings a welded child's cached position and rotation up to where its root has carried it.
        private void SyncFromLink()
        {
            if (!IsWeldedChild)
                return;
            LinkedPose(out SVector3 p, out SQuaternion q);
            _position = new Vector3(p.X, p.Y, p.Z);
            _orientation = new Quaternion(q.X, q.Y, q.Z, q.W);
        }

        // The offset of a child at region position `position` and prim rotation `rotation`, from its root's pose now.
        private void LinkOffsetOf(Vector3 position, Quaternion rotation, out SVector3 offset, out SQuaternion relative)
        {
            JoltPrim root = _linkRoot;
            SQuaternion inv = SQuaternion.Conjugate(Unit(root._orientation));
            offset = SVector3.Transform(ToS(position) - ToS(root._position), inv);
            relative = SQuaternion.Normalize(SQuaternion.Multiply(inv, Unit(rotation)));
        }

        // Core moved or turned a welded child within its linkset (the build tool's edit-linked-parts, PRIM_POSITION or
        // PRIM_ROTATION on a child), or moved the whole object and handed every part its new region place. Only a change of
        // the child's place on the object rebuilds the compound; the child never gets a body of its own.
        private void MoveWithinLinkset(Vector3? position, Quaternion? rotation)
        {
            SyncFromLink();
            LinkOffsetOf(position ?? _position, rotation ?? _orientation, out SVector3 offset, out SQuaternion relative);
            bool moved = SVector3.Distance(offset, _linkOffset) > LinkMoveTolerance;
            bool turned = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(SQuaternion.Dot(relative, _linkRotation)))) > LinkTurnTolerance;
            Vector3 wasAt = _position;
            Quaternion wasTurned = _orientation;
            if (position.HasValue) _position = position.Value;
            if (rotation.HasValue) _orientation = rotation.Value;
            if (!moved && !turned)
                return;
            CarryRidersOfMove(wasAt, wasTurned);
            if (moved) _linkOffset = offset;
            if (turned) _linkRotation = relative;
            _module.MarkLinksetDirty(_linkRoot);
        }

        // Linear/angular velocity: cached from the drain (SOP reads these for terse updates); a set on a
        // live physical body pushes through so a script llSetVelocity takes effect.
        public override Vector3 Velocity
        {
            get => _velocity;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Velocity", value.ToString()); return; }
                Vector3 v = value;
                // Drop restored HORIZONTAL velocity on region LOAD. OpenSim's AddToPhysics
                // replays the DB-saved velocity onto the actor; a vehicle body runs frictionless + never-
                // sleep, so a small saved X/Y coast never bleeds off and drifts the boat metres - and
                // compounds, since that drift is persisted and replayed next reload. Zeroing it
                // only while the region is LOADING (before the first Simulate) leaves a runtime llSetVelocity
                // untouched. Vertical is left alone (a genuinely falling load body keeps its descent).
                if (_isPhysical && _module.IsRegionLoading)
                {
                    v.X = 0f;
                    v.Y = 0f;
                }
                if (_selected) return;   // held still while selected, as ubODE's changevelocity
                _velocity = v;
                if (_body.IsValid && _isPhysical) _backend.SetBodyLinearVelocity(_body, ToS(v));
            }
        }
        public override Vector3 RotationalVelocity
        {
            get => _rotationalVelocity;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "RotationalVelocity", value.ToString()); return; }
                if (_selected) return;
                _rotationalVelocity = value;
                Volatile.Write(ref _apidSpunByScript, 1);   // a rotation target takes this as the speed it starts from
                if (_body.IsValid && _isPhysical) _backend.SetBodyAngularVelocity(_body, ToS(value));
            }
        }
        // Script forces: llSetForce and llSetTorque (persistent, region axes: core turns a local vector into region axes
        // before it gets here), llSetBuoyancy and the STATUS_ROTATE_* locks. Core hands these to the root prim's actor; a
        // welded child hands any that reach it on to its root, so a linkset is one object (the SL wiki, llSetBuoyancy:
        // "The most recent call of llSetBuoyancy in any child prim appears to set the global buoyancy level for the
        // object."). They are kept on the prim and survive a body recreate (resize, relink, physics toggle).
        private readonly object _scriptForceLock = new object();
        private Vector3 _force;        // N, region axes; applied before every backend step until set to zero
        private Vector3 _torque;       // N m, region axes; the same
        private float _buoyancy;       // the body feels (1 - buoyancy) of the region's gravity
        private byte _rotationLocks;   // SceneObjectGroup.axisSelect bits: 0x02 X, 0x04 Y, 0x08 Z locked

        // A force or torque the script gave in the object's own axes (llSetForce, llSetTorque with local = TRUE): the vector
        // in the prim's frame, turned with the body in every collision step (ScriptForceStepProvider). _force and _torque
        // above stay the region-axes vectors as of the call. Under _scriptForceLock.
        private bool _forceLocal, _torqueLocal;
        private Vector3 _forceInBody, _torqueInBody;
        private ScriptForceStepProvider _scriptForceProvider;

        public override Vector3 Torque
        {
            get { if (_linkRoot != null) return _linkRoot.Torque; lock (_scriptForceLock) return _torque; }
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Torque", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.Torque = value; return; }
                bool on;
                lock (_scriptForceLock) { _torque = value; _torqueLocal = false; on = _force != Vector3.Zero || _torque != Vector3.Zero; }
                _module.SetScriptForced(this, on);
                if (!on) _scriptForceProvider?.Detach();   // no per-Update call will come for a prim that is out of the set
            }
        }

        // llSetTorque and llSetForce as the script layer hands them over (PhysicsActor.SetScriptForce / SetScriptTorque): the
        // region-axes vector as of the call. A local one is also kept in the root's own axes, so it turns with the body.
        public override void SetScriptTorque(Vector3 torque, bool local)
        {
            if (!NonFiniteGuard.Ok(torque)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Torque", torque.ToString()); return; }
            if (_linkRoot != null) { _linkRoot.SetScriptTorque(torque, local); return; }
            Quaternion rotation = Orientation;
            bool on;
            lock (_scriptForceLock)
            {
                _torque = torque;
                _torqueLocal = local;
                _torqueInBody = local ? torque * Quaternion.Inverse(rotation) : Vector3.Zero;
                on = _force != Vector3.Zero || _torque != Vector3.Zero;
            }
            _module.SetScriptForced(this, on);
            if (!on) _scriptForceProvider?.Detach();   // no per-Update call will come for a prim that is out of the set
        }

        public override void SetScriptForce(Vector3 force, bool local)
        {
            if (!NonFiniteGuard.Ok(force)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Force", force.ToString()); return; }
            if (_linkRoot != null) { _linkRoot.SetScriptForce(force, local); return; }
            Quaternion rotation = Orientation;
            bool on;
            lock (_scriptForceLock)
            {
                _force = force;
                _forceLocal = local;
                _forceInBody = local ? force * Quaternion.Inverse(rotation) : Vector3.Zero;
                on = _force != Vector3.Zero || _torque != Vector3.Zero;
            }
            _module.SetScriptForced(this, on);
            if (!on) _scriptForceProvider?.Detach();   // no per-Update call will come for a prim that is out of the set
        }

        public override Vector3 Force
        {
            get { if (_linkRoot != null) return _linkRoot.Force; lock (_scriptForceLock) return _force; }
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Force", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.Force = value; return; }
                bool on;
                lock (_scriptForceLock) { _force = value; _forceLocal = false; on = _force != Vector3.Zero || _torque != Vector3.Zero; }
                _module.SetScriptForced(this, on);
                if (!on) _scriptForceProvider?.Detach();   // no per-Update call will come for a prim that is out of the set
            }
        }

        // Called by JoltScene before every backend step for a prim with a force or torque set: the force and torque act
        // for that step, so they hold at every step rate until the script sets them to zero. Jolt's AddForce and
        // AddTorque wake a sleeping body. Vehicles keep their own forces: ubODE does not add a vehicle's set force or
        // torque (ODEPrim.Move hands a vehicle to its controller before it adds them) and core does not hand them to a
        // vehicle when it sets its physics up (SceneObjectPart.ApplyPhysics).
        //
        // A force or torque in the world's axes is applied here, as an engine force: Jolt adds it in every collision step of
        // the Update, so it holds at any step rate. One the script gave in the object's own axes turns with the body, so it
        // is handed to the body's step provider (ScriptForceStepProvider), which turns it by the body's rotation at the start
        // of each collision step and applies it as a change of velocity.
        internal void StepScriptForces(float timeStep)
        {
            if (!_isPhysical || !_body.IsValid || _selected || (_vehicle != null && _vehicle.IsActive))
            {
                _scriptForceProvider?.Disarm();
                return;
            }
            Vector3 force, torque, forceInBody = Vector3.Zero, torqueInBody = Vector3.Zero;
            bool forceLocal, torqueLocal;
            lock (_scriptForceLock)
            {
                force = _force; torque = _torque;
                forceLocal = _forceLocal; torqueLocal = _torqueLocal;
                if (forceLocal) forceInBody = _forceInBody;
                if (torqueLocal) torqueInBody = _torqueInBody;
            }
            if (force != Vector3.Zero && !forceLocal)
                _backend.ApplyForce(_body, LimitForce(ToS(force), timeStep));
            if (torque != Vector3.Zero && !torqueLocal)
                _backend.ApplyTorque(_body, LimitTorque(ToS(torque), timeStep));

            if (forceInBody == Vector3.Zero && torqueInBody == Vector3.Zero)
            {
                _scriptForceProvider?.Detach();
                return;
            }
            _scriptForceProvider ??= new ScriptForceStepProvider(this, _backend);
            SVector3 inertia = _backend.GetBodyInertiaDiagonal(_body);
            float smallest = float.MaxValue;
            if (inertia.X > 0f) smallest = MathF.Min(smallest, inertia.X);
            if (inertia.Y > 0f) smallest = MathF.Min(smallest, inertia.Y);
            if (inertia.Z > 0f) smallest = MathF.Min(smallest, inertia.Z);
            // The impulses over one collision step that bring the body from rest to the engine's speed cap, as LimitForce
            // and LimitTorque do for a whole Update: an absurd force is cut to that, so the engine's arithmetic stays finite.
            float maxImpulse = _module.BodyMaxLinearSpeed * _backend.GetBodyMass(_body);
            float maxAngularImpulse = smallest == float.MaxValue ? 0f : _module.BodyMaxAngularSpeed * smallest;
            if (_scriptForceProvider.Arm(_body, ToS(forceInBody), ToS(torqueInBody), maxImpulse, maxAngularImpulse))
            {
                // The engine calls a provider only for an awake body; AddForce wakes a sleeping one, and so does this.
                _backend.ActivateBody(_body);
                return;
            }
            // A backend that takes no step providers: the vector turned by the body's rotation now, once per Update.
            Quaternion rotation = Orientation;
            if (forceInBody != Vector3.Zero)
                _backend.ApplyForce(_body, LimitForce(ToS(forceInBody * rotation), timeStep));
            if (torqueInBody != Vector3.Zero)
                _backend.ApplyTorque(_body, LimitTorque(ToS(torqueInBody * rotation), timeStep));
        }

        // An absurd but finite force or torque is cut to the one that brings the body from rest to the engine's speed cap
        // ([Jolt] BodyMaxLinearSpeed, BodyMaxAngularSpeed) in one step. The engine caps the speed there anyway, so this
        // changes nothing a script can see; it keeps the engine's arithmetic finite (a 1e30 N force on a 1 g body would
        // overflow the velocity to infinity, and the cap would then turn it into NaN).
        private SVector3 LimitForce(SVector3 force, float timeStep)
        {
            float max = _module.BodyMaxLinearSpeed * _backend.GetBodyMass(_body) / timeStep;
            float len = force.Length();
            return len > max && len > 0f ? force * (max / len) : force;
        }

        private SVector3 LimitTorque(SVector3 torque, float timeStep)
        {
            SVector3 inertia = _backend.GetBodyInertiaDiagonal(_body);
            float smallest = float.MaxValue;
            if (inertia.X > 0f) smallest = MathF.Min(smallest, inertia.X);
            if (inertia.Y > 0f) smallest = MathF.Min(smallest, inertia.Y);
            if (inertia.Z > 0f) smallest = MathF.Min(smallest, inertia.Z);
            if (smallest == float.MaxValue)
                return torque;   // every axis locked: nothing turns it
            float max = _module.BodyMaxAngularSpeed * smallest / timeStep;
            float len = torque.Length();
            return len > max && len > 0f ? torque * (max / len) : torque;
        }

        public override Vector3 Acceleration { get => Vector3.Zero; set { } }
        public override float CollisionScore { get; set; }
        public override bool Kinematic { get => false; set { } }

        // llSetBuoyancy as the SL wiki describes it (wiki.secondlife.com/wiki/LlSetBuoyancy): 0.0 disables the effect, below
        // 1.0 the object sinks, at 1.0 it floats, above 1.0 it rises. The
        // body's gravity is (1 - buoyancy) of the region's, as ubODE computes it (ODEPrim.Move). A vehicle decides its own
        // gravity (VEHICLE_BUOYANCY); ubODE leaves llSetBuoyancy out for a vehicle too, and the value is kept for when the
        // vehicle is removed.
        public override float Buoyancy
        {
            get => _linkRoot != null ? _linkRoot.Buoyancy : _buoyancy;
            set
            {
                if (!float.IsFinite(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "Buoyancy", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.Buoyancy = value; return; }
                // InWorldz: a buoyancy from 0.977 up to 1 is taken as 1 (HalcyonBasicForces.LegacyBuoyancy).
                if (_module.BasicForcesBehaviour == JoltConfig.BasicForces.InWorldz)
                    value = HalcyonBasicForces.LegacyBuoyancy(value);
                if (_buoyancy == value) return;
                _buoyancy = value;
                ApplyScriptGravity();
            }
        }

        // The script's gravity (buoyancy and gravity multiplier) on the body. A changed gravity wakes the body, as ubODE's
        // changeBuoyancy does.
        private void ApplyScriptGravity()
        {
            if (!_isPhysical || !_body.IsValid || (_vehicle != null && _vehicle.IsActive))
                return;
            _backend.SetBodyGravityFactor(_body, ScriptGravityFactor);
            WakeBody();
        }

        // The body's gravity factor for the script's buoyancy and gravity multiplier. Its size is cut to what takes a body
        // from rest to the engine's speed cap in one 1 ms step: no larger value gives a different motion, and the engine's
        // arithmetic stays finite for any finite buoyancy.
        private float ScriptGravityFactor
        {
            get
            {
                float max = _module.BodyMaxLinearSpeed * JoltConfig.MaxPhysicsStepRate / MathF.Max(_module.DefaultGravity.Length(), 1f);
                return Math.Clamp((1f - _buoyancy) * _gravityModifier, -max, max);
            }
        }
        public override bool Flying { get => false; set { } }
        public override bool SetAlwaysRun { get => false; set { } }
        public override bool ThrottleUpdates { get => false; set { } }
        public override bool IsColliding { get; set; }
        public override bool CollidingGround { get; set; }
        public override bool CollidingObj { get; set; }
        // A mouse drag of the object (core's SteerGrab tells the root's actor: before it switches its move target on, after it
        // switches it off). The drag's target is the move target; what the actor learns from this is that the target is a
        // drag's, for InWorldz's rules for a grab (PhysxPrim.pid.cs DoGrabTarget, VehicleDynamics.Simulate): a vehicle's own
        // controller does not run, a hover does not act, and the spin is taken away. Under SecondLife nothing is done with it.
        private volatile bool _grabbed;

        public override bool Grabbed
        {
            set
            {
                if (_linkRoot != null) { _linkRoot.Grabbed = value; return; }
                if (_grabbed == value) return;
                _grabbed = value;
                lock (_targetLock) _targetsChanged = true;
                _module.SetTargeted(this, true);   // the Update-rate path takes up the change, also when the drag ends
            }
        }

        /// <summary>A mouse drag of a vehicle is allowed under <c>[Jolt] VehicleBehaviour = InWorldz</c>, as Halcyon allows it.
        /// Under SecondLife core refuses it (Second Life's pages do not say what a drag of a vehicle does).</summary>
        public override bool AllowsVehicleGrab => _module.VehicleSettings.Behaviour == VehicleBehaviour.InWorldz;

        /// <summary>Setting a vehicle type from a script ends the object's look-at and move-to target under
        /// <c>VehicleBehaviour = InWorldz</c> (VehicleDynamics.VehicleTypeChanged: "StopRotLookAt(); SetMoveToTarget(0, 0)").</summary>
        public override bool VehicleTypeStopsTargets => _module.VehicleSettings.Behaviour == VehicleBehaviour.InWorldz;

        // The drag of a vehicle runs the drag's target in place of the vehicle controller.
        private bool GrabOverridesVehicle => _grabbed && _module.VehicleSettings.Behaviour == VehicleBehaviour.InWorldz;

        // Selection in the build tool. Core hands the same value to the root's actor and every part's
        // (SceneObjectGroup.IsSelected). A selected physical object stops where it is and stays: no gravity, no drift, no
        // script force, impulse, velocity or vehicle step, and nothing that hits it moves it. Let go, it carries on from
        // rest. That is ubODE's (ODEPrim.DoSelectedStatus stops and disables the body; Move skips a selected prim). ubODE
        // also stops a selected object colliding; here it stays solid, as BulletSim's selected object (BSPrim.IsStatic).
        // The root holds the whole welded linkset; a welded child's flag is kept for when it is unlinked.
        //
        // An object held for riders that crossed with it (HoldStill) is kept still the same way, so _selected means "kept
        // still" for either reason; _userSelected and _held say which, and the object is let go only when neither holds it.
        private volatile bool _selected;
        private bool _userSelected;
        private bool _held;
        // Set under BasicForcesBehaviour = InWorldz while the object is outside Halcyon's size limits (CheckPhysicalSize).
        private bool _sizeHeld;
        internal bool IsHeldBySizeLimit => _sizeHeld;
        private Vector3 _heldVelocity;
        private Vector3 _heldRotationalVelocity;

        public override bool Selected
        {
            set
            {
                _userSelected = value;
                SetKeptStill(value || _held || _sizeHeld);
            }
        }

        private void SetKeptStill(bool still)
        {
            if (_selected == still)
                return;
            _selected = still;
            if (_linkRoot != null || !_isPhysical || !_body.IsValid)
                return;
            if (still)
                HoldSelected();
            else
                ReleaseSelected();
        }

        // Held for the riders that cross with the object (PhysicsActor.HoldStill): kept still like a selected one, with the
        // velocity it arrived with remembered for ReleaseHold. The body stays solid, so an avatar standing on it is held up.
        public override void HoldStill()
        {
            if (_held || !_isPhysical || _linkRoot != null)
                return;

            _heldVelocity = _velocity;
            _heldRotationalVelocity = _rotationalVelocity;
            _held = true;
            SetKeptStill(true);
        }

        // The hold ends after `heldSeconds`. Under InWorldz basic forces the object first goes on by what it would have travelled
        // in that time (Halcyon: PhysxPrim.DoResumeInterpolation): its velocity times the time, lifted onto the ground if that ends
        // under it, and left where it was held if that ends inside another body. Core caps the time. Under SecondLife it only lets go.
        public override void ReleaseHold(float heldSeconds)
        {
            if (_held && heldSeconds > 0f && float.IsFinite(heldSeconds) && !_userSelected && _isPhysical && _linkRoot == null
                && _body.IsValid && InWorldzBasicForces)
                MoveOnAfterHold(heldSeconds);
            ReleaseHold();
        }

        private bool InWorldzBasicForces => _module.BasicForcesBehaviour == JoltConfig.BasicForces.InWorldz;

        // How far into another body the object may end up and still count as not in it: a body resting on another is touching it.
        private const float ResumePenetrationTolerance = 0.02f;

        private void MoveOnAfterHold(float heldSeconds)
        {
            Vector3 held = _position;
            Vector3 target = held + _heldVelocity * heldSeconds;
            if (_module.TryUnburyPhysicalLoad(target, _size, out Vector3 lifted))
                target = lifted;
            if (target == held)
                return;

            _backend.SetBodyTransform(_body, ToS(target), BodyOrientationOf(_orientation), activate: false);
            if (_backend.BodyPenetratesOthers(_body, ResumePenetrationTolerance))
            {
                _backend.SetBodyTransform(_body, ToS(held), BodyOrientationOf(_orientation), activate: false);
                return;
            }
            _position = target;
        }

        // Whether an object that crossed in with riders is kept from crossing out again at once (Halcyon: SceneObjectGroup.cs,
        // TestForAndBeginCrossing). The Second Life pages say nothing of it, so SecondLife keeps the old behaviour.
        public override bool WaitsBeforeCrossingBack => InWorldzBasicForces;

        // The hold ends: dynamic again with the velocity it was held with. An object that is selected stays still (it gets no
        // velocity), and falls from rest when it is let go.
        public override void ReleaseHold()
        {
            if (!_held)
                return;

            _held = false;
            SetKeptStill(_userSelected || _sizeHeld);
            if (_userSelected || _sizeHeld || !_body.IsValid || !_isPhysical)
                return;

            _velocity = _heldVelocity;
            _rotationalVelocity = _heldRotationalVelocity;
            _backend.SetBodyLinearVelocity(_body, ToS(_heldVelocity));
            _backend.SetBodyAngularVelocity(_body, ToS(_heldRotationalVelocity));
            _vehicle?.ResumeAfterHold();
        }

        // Held: the body is made kinematic (it keeps its place, nothing it touches moves it, gravity does not act) and
        // stopped, and the simulator is told it stopped.
        private void HoldSelected()
        {
            _backend.SetBodyLinearVelocity(_body, SVector3.Zero);
            _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
            _backend.SetBodyMotionType(_body, BodyMotionType.Kinematic, activate: false);
            _backend.DeactivateBody(_body);
            _velocity = Vector3.Zero;
            _rotationalVelocity = Vector3.Zero;
            RequestPhysicsterseUpdate();
        }

        // Let go: dynamic again with its mass (and rotation locks) as before, at rest, and awake so it falls or settles
        // from where it was left. A vehicle gets its body setup back.
        private void ReleaseSelected()
        {
            _backend.SetBodyMotionType(_body, BodyMotionType.Dynamic, activate: false);
            _backend.SetBodyLinearVelocity(_body, SVector3.Zero);
            _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
            ApplyMass();
            ApplyVehicleBodyParams();
            WakeBody();
        }

        // OpenSim calls child.link(root) per child when a physical linkset is formed. Weld this child into
        // the root's compound body.
        public override void link(PhysicsActor obj)
        {
            if (obj is JoltPrim root && !ReferenceEquals(root, this))
                root.LinkChild(this);
        }

        // Detach from the compound and become an independent body again.
        // A welded child takes its own body where its root has carried it, moving as the root moved.
        public override void delink()
        {
            if (_linkRoot != null)
            {
                JoltPrim root = _linkRoot;
                if (_welded)
                {
                    SyncFromLink();
                    // The part goes on at the velocity its point had: the root's, and the spin's share at the part's offset.
                    _velocity = root._velocity + Vector3.Cross(root._rotationalVelocity, _position - root._position);
                    _rotationalVelocity = root._rotationalVelocity;
                }
                _welded = false;
                _linkRoot = null;
                root.UnlinkChild(this);
            }
            _welded = false;
            if (!_body.IsValid && _shape.IsValid)
                CreateBodyInternal();   // restore our own body (we were welded into the root)
        }

        // Link/unlink only RECORD membership and mark the root dirty - the compound is (re)built ONCE per
        // frame in RebuildCompoundNow, drained from Simulate. Rebuilding inline per child HUNG the boot-load
        // of a persisted physical linkset: OpenSim fires child.link(root) for every child as the whole set
        // loads at once, and each inline rebuild churned RemoveBody/CreateBody on the active root body while
        // the load + heartbeat ran concurrently. Deferring coalesces
        // N child-links into ONE rebuild at a controlled point - off the load path, O(N) not O(N^2).
        internal void LinkChild(JoltPrim child)
        {
            _linkChildren ??= new System.Collections.Generic.List<JoltPrim>();
            if (!_linkChildren.Contains(child))
                _linkChildren.Add(child);
            child._linkRoot = this;
            _module.MarkLinksetDirty(this);
            CheckComplexity();
        }

        internal void UnlinkChild(JoltPrim child)
        {
            if (_linkChildren != null) _linkChildren.Remove(child);
            _module.MarkLinksetDirty(this);
        }

        private bool _rebuilding;

        // Jolt's default shape density (ConvexShapeSettings::mDensity), what every cooked shape starts with.
        private const float JoltShapeDensity = 1000f;

        // (Re)build the root's body from its own shape + all welded children at their root-relative offsets,
        // ONCE. Called from the module's per-frame dirty-linkset drain (step thread, before the step - safe
        // body ops, no per-child churn). StaticCompoundShape (fast query + per-child UserData for per-child collision identity).
        // Sub-shape transforms are composed in the ROOT BODY frame (BodyOrientationOf carries any cylinder
        // axis-correction); mass/COM/inertia come out of Jolt's compound assembly (mass = sum of child
        // Volume x density). Re-entrancy- and destroyed-guarded so it can never loop or touch
        // a torn-down prim. No children -> revert to the plain single root shape (never a 1-child compound).
        internal void RebuildCompoundNow()
        {
            if (_rebuilding || !_shape.IsValid) return;   // guard: no re-entrancy, skip a destroyed root
            _rebuilding = true;
            try
            {
                ShapeId oldCompound = _compoundShape;

                if (_linkChildren == null || _linkChildren.Count == 0)
                {
                    _compoundShape = ShapeId.Invalid;   // single member -> plain body, not a degenerate compound
                }
                else
                {
                    // Weld: drop each child's independent body (it lives as a sub-shape of the compound now).
                    foreach (JoltPrim c in _linkChildren)
                        if (c._body.IsValid) { _backend.RemoveBody(c._body); c._body = BodyId.Invalid; }

                    // A shape shared with other prims must not be given this part's density (SetShapeDensity below).
                    lock (_shapeLock) MakeShapePrivate();
                    foreach (JoltPrim c in _linkChildren)
                        lock (c._shapeLock) c.MakeShapePrivate();

                    SQuaternion rootBody = BodyOrientationOf(_orientation);
                    SQuaternion invRoot = SQuaternion.Conjugate(rootBody);
                    var kids = new CompoundChild[1 + _linkChildren.Count];
                    kids[0] = new CompoundChild { Shape = _shape, Position = SVector3.Zero, Orientation = SQuaternion.Identity, UserData = LocalID };
                    // Each part's own density in the compound's mass properties, so its centre of mass and inertia follow
                    // where the mass is (the SL wiki's Physics Material Settings test page allows different physics settings
                    // for each prim in a linked set), as ubODE adds each part's own mass at its own place
                    // (ODEPrim.MakeBody). The densities are relative to the root's, which keeps Jolt's default: the body's
                    // mass is set to the parts' sum just after (CreateBodyInternal), and a linkset of one density is built
                    // exactly as before.
                    _backend.SetShapeDensity(_shape, JoltShapeDensity * ShapeMassFactor());
                    for (int i = 0; i < _linkChildren.Count; i++)
                    {
                        JoltPrim c = _linkChildren[i];
                        // A part already welded keeps its place on the object (its offset); a newly linked one is placed
                        // where core put it, relative to the root now.
                        c.SyncFromLink();
                        var dWorld = new SVector3(c._position.X - _position.X, c._position.Y - _position.Y, c._position.Z - _position.Z);
                        kids[i + 1] = new CompoundChild
                        {
                            Shape = c._shape,
                            Position = SVector3.Transform(dWorld, invRoot),                                 // world delta -> root frame
                            Orientation = SQuaternion.Multiply(invRoot, c.BodyOrientationOf(c._orientation)),
                            UserData = c.LocalID,
                        };
                        if (!c._welded)
                            c.LinkOffsetOf(c._position, c._orientation, out c._linkOffset, out c._linkRotation);
                        c._welded = true;
                        _backend.SetShapeDensity(c._shape, JoltShapeDensity * c.PhysicalDensity / PhysicalDensity * c.ShapeMassFactor());
                    }
                    _compoundShape = _backend.CreateCompoundShape(kids);
                }

                if (_body.IsValid) { _backend.RemoveBody(_body); _body = BodyId.Invalid; }
                CreateBodyInternal();

                if (oldCompound.IsValid) _backend.ReleaseShape(oldCompound);
            }
            catch (Exception e)
            {
                // Never let a linkset rebuild propagate into the heartbeat and wedge the region.
                JoltScene.m_log.LogError($"{JoltScene.LogHeader} linkset rebuild EXCEPTION for root {LocalID}: {e}");
            }
            finally { _rebuilding = false; }
        }
        // llSetStatus STATUS_ROTATE_X/_Y/_Z: whether the object can turn about the axis, for physical objects only
        // (wiki.secondlife.com/wiki/LlSetStatus).
        // Core hands the root's actor SceneObjectGroup.axisSelect bits, set = locked (0x02 X, 0x04 Y, 0x08 Z), the bits
        // ubODE reads (ODEPrim.createAMotor). Each locked axis is one of the prim's own axes; the body gets an infinite
        // inertia about it (SetBodyRotationLocks), so nothing turns the object about that axis, and locking stops its
        // turning, as ubODE's does. ubODE fixes the locked axes in the region where they point when they are locked; here
        // they turn with the object about its free axes. The two agree with all three locked and with one axis free.
        public override void LockAngularMotion(byte axislocks)
        {
            if (_linkRoot != null) { _linkRoot.LockAngularMotion(axislocks); return; }
            _rotationLocks = (byte)(axislocks & 0x0E);
            ApplyRotationLocks();
        }

        private void ApplyRotationLocks()
        {
            if (!_isPhysical || !_body.IsValid)
                return;
            // The prim's axes in the body's own frame: the same axes, but for a cylinder, whose body is turned by the axis
            // correction.
            SQuaternion toBody = SQuaternion.Multiply(SQuaternion.Conjugate(BodyOrientationOf(_orientation)), ToS(_orientation));
            bool lockX = false, lockY = false, lockZ = false;
            for (int axis = 0; axis < 3; axis++)
            {
                if ((_rotationLocks & (0x02 << axis)) == 0)
                    continue;
                SVector3 v = SVector3.Transform(axis == 0 ? SVector3.UnitX : axis == 1 ? SVector3.UnitY : SVector3.UnitZ, toBody);
                float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
                if (ax >= ay && ax >= az) lockX = true;
                else if (ay >= az) lockY = true;
                else lockZ = true;
            }
            _bodyLockX = lockX; _bodyLockY = lockY; _bodyLockZ = lockZ;
            _backend.SetBodyRotationLocks(_body, lockX, lockY, lockZ);
        }

        // Forces: wired to the backend's accumulate-until-next-Step Apply* (Jolt AddForce/AddTorque
        // == Bullet ApplyCentralForce/ApplyTorque; both auto-activate a sleeping body). BulletSim treats
        // a NON-push AddForce as force-per-second and divides by the frame dt before applying - mirror
        // that so llApplyImpulse/llPushObject land with the same magnitude on both engines. The force acts for the
        // next backend step only: with [Jolt] PhysicsStepRate on, LastTimeStep is that step's length and a push is
        // scaled by heartbeat / step, so both impulses are the same as with one step per heartbeat.
        public override void AddForce(Vector3 force, bool pushforce)
        {
            if (_linkRoot != null) { _linkRoot.AddForce(force, pushforce); return; }
            if (!_body.IsValid || !_isPhysical || _selected || !force.IsFinite())
                return;
            Vector3 f = pushforce ? force * _module.PushForceScale : force / _module.LastTimeStep;
            _backend.ApplyForce(_body, ToS(f));   // wakes a sleeping body
            _pushedSinceVehicleStep = true;
        }

        // A non-push angular force is llApplyRotationalImpulse's impulse (SceneObjectGroup.ApplyAngularImpulse): it acts
        // as a torque for the next backend step, divided by that step, so the angular impulse delivered is the one given
        // at every step rate, as ubODE delivers it (ODEPrim.changeAddAngularImpulse divides by its step). A push (the
        // viewer's grab spin) is applied as it comes, as before.
        public override void AddAngularForce(Vector3 force, bool pushforce)
        {
            if (_linkRoot != null) { _linkRoot.AddAngularForce(force, pushforce); return; }
            if (!_body.IsValid || !_isPhysical || _selected || !force.IsFinite())
                return;
            if (pushforce)
                _backend.ApplyTorque(_body, ToS(force));
            else
                _backend.ApplyTorque(_body, LimitTorque(ToS(force) / _module.LastTimeStep, _module.LastTimeStep));
        }
        public override void AvatarJump(float forceZ) { }
        public override void SetMomentum(Vector3 momentum) { }

        // Volume detect: physical objects and avatars pass through the object, and it raises collision_start and
        // collision_end while they interpenetrate it (wiki.secondlife.com/wiki/LlVolumeDetect). The body becomes a sensor on the
        // Sensor layer; SceneObjectPart.PhysicsCollision turns its touching set into those two events only.
        public override void SetVolumeDetect(int param)
        {
            bool on = param != 0;
            if (_isVolumeDetect == on) return;   // the scene calls this on every flag update
            _isVolumeDetect = on;
            ApplyCollisionLayer();
        }

        public override bool IsVolumeDtc
        {
            get => _isVolumeDetect;
            set { }
        }

        public override bool Phantom
        {
            get => _isPhantom;
            set
            {
                if (_isPhantom == value) return;
                _isPhantom = value;
                ApplyCollisionLayer();
            }
        }

        // PRIM_PHYSICS_SHAPE_TYPE. NONE never reaches here (the scene takes the prim out of physics); PRIM and CONVEX
        // re-cook the shape on the same body when that changes how it is cooked (see CookAsHull).
        public override byte PhysicsShapeType
        {
            get => _shapeType;
            set
            {
                if (_shapeType == value) return;
                bool wasHull = CookAsHull, wasList = UseHullList;
                _shapeType = value;
                if ((CookAsHull != wasHull || UseHullList != wasList) && _shape.IsValid)
                    Rebuild();
                if (_isPhysical)
                    CheckComplexity();
            }
        }

        // Puts the live body on the layer its flags ask for, in place: the body keeps its id, transform and velocity.
        // A welded child has no body; its root's body carries the linkset's flags. A physical body is woken so one
        // resting on what it may now pass through falls (the step thread activates it, as for a new body).
        private void ApplyCollisionLayer()
        {
            if (!_body.IsValid) return;
            _backend.SetBodyLayer(_body, BodyLayer);
            _backend.SetBodySensor(_body, _isVolumeDetect);
            if (_isPhysical)
                _module.RegisterPendingActivation(this);
            SyncKinematicBody();   // a phantom or volume-detect prim is not moved as a kinematic one, and is again when that ends
        }

        // For the contact dispatch: true when this part, or the linkset root its body belongs to, is a volume detector.
        internal bool IsVolumeDetectPart => (_linkRoot ?? this)._isVolumeDetect;

        // For the contact dispatch: the body this part is in (the linkset root's) is awake; is physical and asleep.
        internal bool BodyAwake
        {
            get { JoltPrim root = _linkRoot ?? this; return root._body.IsValid && _backend.IsBodyAwake(root._body); }
        }
        internal bool PhysicalAndAsleep
        {
            get { JoltPrim root = _linkRoot ?? this; return root._isPhysical && root._body.IsValid && !_backend.IsBodyAwake(root._body); }
        }

        // Collision-event subscription.
        // A script with a collision handler -> OpenSim calls SubscribeEvents(50). Flip the LIVE body's
        // Persist gate so the ongoing-touch stream (the script `collision` event) reaches the module drain.
        public override void SubscribeEvents(int ms)
        {
            _subscribedMs = ms;
            if (_body.IsValid) _backend.SetBodyWantsContactEvents(_body, ms > 0);
        }
        public override void UnSubscribeEvents()
        {
            _subscribedMs = 0;
            if (_body.IsValid) _backend.SetBodyWantsContactEvents(_body, false);
        }
        public override bool SubscribedEvents() => _subscribedMs > 0;

        // Vehicles: forward the LSL wire params into the backend-agnostic controller. OpenSim's
        // SOP hands us raw ints; the controller routes them as Halcyon did and holds each value to the
        // region's VehicleSettings limits. Setting a
        // type registers with the scene's per-frame drive + applies the vehicle body params; setting
        // TYPE_NONE unwinds both and destroys the controller.
        public override int VehicleType
        {
            get => _vehicle == null ? 0 : (int)_vehicle.Type;
            set
            {
                EnsureVehicle();
                _vehicle.ProcessTypeChange((VehicleCode)value);
                if (_vehicle.IsActive)
                {
                    _module.RegisterVehicle(this);
                    ApplyVehicleBodyParams();
                    // Assert buoyancy on restart: the assertion just above can be lost on the load
                    // path (body created GravityFactor=1 with deferred activation drained on the step thread
                    // AFTER this set) - so re-assert it on the step-thread steps of the next
                    // ReassertVehicleSeconds, where it sticks.
                    _reassertVehicleTime = ReassertVehicleSeconds;
                    // Held only while the region loads. With the region running, a type call gives the type's presets and
                    // nothing else: Second Life's page for llSetVehicleType describes no change to the velocity, and
                    // Halcyon's type change copies the actor's velocity into the vehicle's state without zeroing it.
                    _reassertZeroesVelocity = _module.IsRegionLoading;
                }
                else
                {
                    _module.UnregisterVehicle(this);
                    RestoreVehicleBodyParams();
                    _vehicle = null;
                    _vehicleBody = null;
                    _vehicleStepProvider = null;
                }
            }
        }

        public override void VehicleFloatParam(int param, float value)
        {
            EnsureVehicle();
            _vehicle.ProcessFloatVehicleParam((VehicleCode)param, value);
            WakeVehicle();
        }

        public override void VehicleVectorParam(int param, Vector3 value)
        {
            EnsureVehicle();
            _vehicle.ProcessVectorVehicleParam((VehicleCode)param, value);
            WakeVehicle();
        }

        public override void VehicleRotationParam(int param, Quaternion rotation)
        {
            EnsureVehicle();
            _vehicle.ProcessRotationVehicleParam((VehicleCode)param, rotation);
            WakeVehicle();
        }

        public override void VehicleFlags(int param, bool remove)
        {
            EnsureVehicle();
            _vehicle.ProcessVehicleFlags(param, remove);
            WakeVehicle();
        }

        // A saved vehicle handed back: rezzed from inventory, loaded with the region, copied, arriving from another region,
        // dropped as an attachment, or a phantom prim's physics switched back on (SceneObjectPart.AddToPhysics, through
        // SOPVehicle.SetVehicle). PhysicsActor's own SetVehicle replays the record through the script calls: it starts with
        // VehicleFlags(-1, false), which sets every flag (as on ubODE: -1 with remove false sets them all, -1 with remove true
        // removes them all), and its motor directions start both motors. ubODE overrides it (ODEPrim.SetVehicle,
        // ODEDynamics.DoSetVehicle), and so does this. The vehicle gets exactly its record: the type and the type's
        // preset, the record's flags in place of the preset's, each setting held to the region's limits as the same value
        // from a script is, and the reference frame. Its motors keep their saved directions and do not run until a script
        // sets them, as on ubODE.
        //
        // With the InWorldz presets (VehiclePresetSet.Legacy) the record is not what the script's vehicle held: the
        // record starts from the simulator's preset for the type (SOPVehicle.ProcessTypeChange: one value for each motor
        // timescale and decay, a type's own flags; no preset at all for the two InWorldz types) and holds what the script
        // changed, where the vehicle starts from the InWorldz preset (per-axis timescales, the type's flags). So a value
        // that still equals the simulator's preset is not the script's and keeps the InWorldz preset's; one that differs
        // is the script's and is applied as the same script call is (a float timescale on all three axes). A flag is
        // taken from the record only where the record differs from the simulator's preset flags. A script that sets a
        // value to exactly the simulator's preset value cannot be told from one that set nothing; the InWorldz value
        // stays. With the documented presets the record is applied whole.
        public override void SetVehicle(object pvdata)
        {
            if (pvdata is not VehicleData vd)
                return;
            VehicleType = (int)vd.m_type;
            if (_vehicle == null)
                return;
            _reassertZeroesVelocity = _module.IsRegionLoading;

            // Also for the two InWorldz-only types (sailboat 10001, motorcycle 10002): the simulator has no preset for
            // them, so their record is the unset type's bare values, while a script's llSetVehicleType gives them the
            // InWorldz preset under every behaviour and presets setting. Every type the simulator has a preset for
            // keeps the documented path (the record applied whole).
            bool overPreset = _vehicle.Settings.Presets == VehiclePresetSet.Legacy
                || (int)vd.m_type == (int)VehicleCode.TYPE_SAILBOAT || (int)vd.m_type == (int)VehicleCode.TYPE_MOTORCYCLE;
            VehicleData simulatorPreset = default;
            if (overPreset)
            {
                var preset = new OpenSim.Region.Framework.Scenes.SOPVehicle();
                preset.ProcessTypeChange(vd.m_type);
                simulatorPreset = preset.vd;
                _vehicle.RestoreFlagsOverPreset((int)vd.m_flags, (int)simulatorPreset.m_flags);
            }
            else
                _vehicle.RestoreFlags((int)vd.m_flags);

            // Whether the record's value is the script's: always with the documented presets, otherwise when it differs.
            bool Script(float record, float preset) => !overPreset || record != preset;
            bool ScriptVector(Vector3 record, Vector3 preset) => !overPreset || record != preset;

            if (ScriptVector(vd.m_linearFrictionTimescale, simulatorPreset.m_linearFrictionTimescale))
                _vehicle.ProcessVectorVehicleParam(VehicleCode.LINEAR_FRICTION_TIMESCALE, vd.m_linearFrictionTimescale);
            if (Script(vd.m_linearMotorDecayTimescale, simulatorPreset.m_linearMotorDecayTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.LINEAR_MOTOR_DECAY_TIMESCALE, vd.m_linearMotorDecayTimescale);
            if (Script(vd.m_linearMotorTimescale, simulatorPreset.m_linearMotorTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.LINEAR_MOTOR_TIMESCALE, vd.m_linearMotorTimescale);
            if (ScriptVector(vd.m_linearMotorOffset, simulatorPreset.m_linearMotorOffset))
                _vehicle.ProcessVectorVehicleParam(VehicleCode.LINEAR_MOTOR_OFFSET, vd.m_linearMotorOffset);

            if (Script(vd.m_angularMotorTimescale, simulatorPreset.m_angularMotorTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.ANGULAR_MOTOR_TIMESCALE, vd.m_angularMotorTimescale);
            if (Script(vd.m_angularMotorDecayTimescale, simulatorPreset.m_angularMotorDecayTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.ANGULAR_MOTOR_DECAY_TIMESCALE, vd.m_angularMotorDecayTimescale);
            if (ScriptVector(vd.m_angularFrictionTimescale, simulatorPreset.m_angularFrictionTimescale))
                _vehicle.ProcessVectorVehicleParam(VehicleCode.ANGULAR_FRICTION_TIMESCALE, vd.m_angularFrictionTimescale);

            if (Script(vd.m_angularDeflectionEfficiency, simulatorPreset.m_angularDeflectionEfficiency))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.ANGULAR_DEFLECTION_EFFICIENCY, vd.m_angularDeflectionEfficiency);
            if (Script(vd.m_angularDeflectionTimescale, simulatorPreset.m_angularDeflectionTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.ANGULAR_DEFLECTION_TIMESCALE, vd.m_angularDeflectionTimescale);
            if (Script(vd.m_linearDeflectionEfficiency, simulatorPreset.m_linearDeflectionEfficiency))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.LINEAR_DEFLECTION_EFFICIENCY, vd.m_linearDeflectionEfficiency);
            if (Script(vd.m_linearDeflectionTimescale, simulatorPreset.m_linearDeflectionTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.LINEAR_DEFLECTION_TIMESCALE, vd.m_linearDeflectionTimescale);

            if (Script(vd.m_bankingEfficiency, simulatorPreset.m_bankingEfficiency))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.BANKING_EFFICIENCY, vd.m_bankingEfficiency);
            if (Script(vd.m_bankingMix, simulatorPreset.m_bankingMix))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.BANKING_MIX, vd.m_bankingMix);
            if (Script(vd.m_bankingTimescale, simulatorPreset.m_bankingTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.BANKING_TIMESCALE, vd.m_bankingTimescale);

            if (Script(vd.m_VhoverHeight, simulatorPreset.m_VhoverHeight))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.HOVER_HEIGHT, vd.m_VhoverHeight);
            if (Script(vd.m_VhoverEfficiency, simulatorPreset.m_VhoverEfficiency))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.HOVER_EFFICIENCY, vd.m_VhoverEfficiency);
            if (Script(vd.m_VhoverTimescale, simulatorPreset.m_VhoverTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.HOVER_TIMESCALE, vd.m_VhoverTimescale);
            if (Script(vd.m_VehicleBuoyancy, simulatorPreset.m_VehicleBuoyancy))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.BUOYANCY, vd.m_VehicleBuoyancy);

            if (Script(vd.m_verticalAttractionEfficiency, simulatorPreset.m_verticalAttractionEfficiency))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.VERTICAL_ATTRACTION_EFFICIENCY, vd.m_verticalAttractionEfficiency);
            if (Script(vd.m_verticalAttractionTimescale, simulatorPreset.m_verticalAttractionTimescale))
                _vehicle.ProcessFloatVehicleParam(VehicleCode.VERTICAL_ATTRACTION_TIMESCALE, vd.m_verticalAttractionTimescale);

            RestoreMotorAxes(vd);
            RestoreWindEfficiency(vd);
            _vehicle.RestoreMotorDirections(vd.m_linearMotorDirection, vd.m_angularMotorDirection);
            _vehicle.ProcessRotationVehicleParam(VehicleCode.REFERENCE_FRAME, vd.m_referenceFrame);
            // A record saved by a region crossing carries the motors' running state: they go on from where they were.
            _vehicle.RestoreRunningMotors(
                vd.m_linearMotorRunAge.HasValue && vd.m_linearMotorRunDirection.HasValue && vd.m_linearMotorRunTarget.HasValue
                    ? new MotorRunningState(vd.m_linearMotorRunAge.Value, vd.m_linearMotorRunDirection.Value, vd.m_linearMotorRunTarget.Value)
                    : (MotorRunningState?)null,
                vd.m_angularMotorRunAge.HasValue && vd.m_angularMotorRunDirection.HasValue && vd.m_angularMotorRunTarget.HasValue
                    ? new MotorRunningState(vd.m_angularMotorRunAge.Value, vd.m_angularMotorRunDirection.Value, vd.m_angularMotorRunTarget.Value)
                    : (MotorRunningState?)null);
            WakeVehicle();
        }

        // A physical object's move-to target, hover and look-at go on in the next region (core carries them in the saved object)
        // under InWorldz basic forces: Halcyon keeps them across a crossing (PhysxPrim.cs:3131-3185, SuspendPhysicsSync). The Second
        // Life pages for llMoveToTarget, llSetHoverHeight and llRotLookAt say nothing of a crossing, so SecondLife keeps the old
        // behaviour: they end at the border.
        public override bool CarriesTargetsAcrossCrossing => _module.BasicForcesBehaviour == JoltConfig.BasicForces.InWorldz;

        // Core calls this as it starts to cross a vehicle into another region: the motors' running state goes into the record
        // that is sent, so that the vehicle goes on driving on the other side (SetVehicle takes it up). A motor that is off
        // writes nothing.
        public override void CaptureVehicleRunningState(ref VehicleData vd)
        {
            if (_vehicle == null)
                return;
            _vehicle.GetRunningMotors(out MotorRunningState? linear, out MotorRunningState? angular);
            vd.m_linearMotorRunAge = linear?.Age;
            vd.m_linearMotorRunDirection = linear?.Direction;
            vd.m_linearMotorRunTarget = linear?.Target;
            vd.m_angularMotorRunAge = angular?.Age;
            vd.m_angularMotorRunDirection = angular?.Direction;
            vd.m_angularMotorRunTarget = angular?.Target;
        }

        // A value for each axis of the four motor timescales, where the record holds one (a script's vector call writes it;
        // the record keeps the single values beside it, which the calls above have just applied to all three axes). It goes on
        // top of them. The controller takes a vector for these four only under VehicleBehaviour = InWorldz and holds a decay to
        // the region's limit, so under SecondLife this changes nothing, as the same vector from a script changes nothing.
        private void RestoreMotorAxes(VehicleData vd)
        {
            if (vd.m_linearMotorTimescaleAxes is Vector3 lt)
                _vehicle.ProcessVectorVehicleParam(VehicleCode.LINEAR_MOTOR_TIMESCALE, lt);
            if (vd.m_linearMotorDecayTimescaleAxes is Vector3 ld)
                _vehicle.ProcessVectorVehicleParam(VehicleCode.LINEAR_MOTOR_DECAY_TIMESCALE, ld);
            if (vd.m_angularMotorTimescaleAxes is Vector3 at)
                _vehicle.ProcessVectorVehicleParam(VehicleCode.ANGULAR_MOTOR_TIMESCALE, at);
            if (vd.m_angularMotorDecayTimescaleAxes is Vector3 ad)
                _vehicle.ProcessVectorVehicleParam(VehicleCode.ANGULAR_MOTOR_DECAY_TIMESCALE, ad);
        }

        // The two wind efficiency vectors, where the record holds them (a script's vector call wrote them). Where it holds none the
        // type's own, set with the type above, stand. The controller takes these two codes only under VehicleBehaviour = InWorldz,
        // so under SecondLife this changes nothing, as the same vector from a script changes nothing.
        private void RestoreWindEfficiency(VehicleData vd)
        {
            if (vd.m_linearWindEfficiency is Vector3 lw)
                _vehicle.ProcessVectorVehicleParam(VehicleCode.LINEAR_WIND_EFFICIENCY, lw);
            if (vd.m_angularWindEfficiency is Vector3 aw)
                _vehicle.ProcessVectorVehicleParam(VehicleCode.ANGULAR_WIND_EFFICIENCY, aw);
        }

        private void EnsureVehicle()
        {
            if (_vehicle == null)
            {
                _vehicleBody = new JoltVehicleBody(_module, _backend, this);
                _vehicle = new VehicleController(_vehicleBody);
                _vehicle.GroundGravityFactor = _module.VehicleGroundGravityFactor;
                _vehicle.Settings = _module.VehicleSettings;
                Func<DateTime> clock = _module.ControllerClock;
                if (clock != null)
                    _vehicle.Clock = clock;
                _vehicle.DriverCamera = DriverCameraAxis;
                // A script's motor set, a type change and the stop and resume calls wait for the next Update, so none changes the
                // motors' running state while the engine steps; the controller's steps run in the engine's collision steps.
                _vehicle.DeferStateChanges = true;
                _vehicleStepProvider = new VehicleStepProvider(_module, this, _vehicle);
            }
        }

        // The driver's camera for the mouselook vehicle flags: where it looks (world) and whether the driver is in
        // mouselook, as the simulator hands it to the vehicle's root actor (ScenePresence answers
        // OnPhysicsRequestingCameraData once a script takes the seated driver's controls, with MouseLook from the
        // driver's control flags); null when no driver's camera is hooked up.
        private DriverView? DriverCameraAxis() => TryGetCameraData() is CameraData c ? new DriverView(c.CameraAtAxis, c.MouseLook) : null;

        // The vehicle's bounding box in its own (prim) frame, for the motor-height rule's down rays: the box of the root
        // prim's size, grown over each welded part's own size box at the part's place. A box, not the hull, as the rule
        // only needs where the vehicle's lower edges are.
        internal bool TryGetVehicleBounds(out Vector3 center, out Vector3 halfSize)
        {
            SVector3 min = ToS(_size * -0.5f), max = ToS(_size * 0.5f);
            if (_linkChildren != null)
            {
                foreach (JoltPrim c in _linkChildren)
                {
                    if (!c._welded)
                        continue;
                    SVector3 half = ToS(c._size * 0.5f);
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new SVector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
                        SVector3 p = c._linkOffset + SVector3.Transform(corner, c._linkRotation);
                        min = SVector3.Min(min, p);
                        max = SVector3.Max(max, p);
                    }
                }
            }
            SVector3 mid = (min + max) * 0.5f, h = (max - min) * 0.5f;
            center = new Vector3(mid.X, mid.Y, mid.Z);
            halfSize = new Vector3(h.X, h.Y, h.Z);
            return halfSize.X > 0f && halfSize.Y > 0f && halfSize.Z > 0f;
        }

        // Per-frame drive, called by JoltScene.Simulate BEFORE the physics step (the Jolt
        // equivalent of BulletSim's BeforeStep event): snapshot the live body, run the controller,
        // which pushes velocity changes/forces/torques back through the backend for this step.
        internal void StepVehicle(float timeStep)
        {
            _vehicleStepProvider?.Disarm();   // armed for one Update at a time, and only by the branch below that steps the vehicle
            if (_vehicle == null || !_vehicle.IsActive || !_isPhysical || !_body.IsValid || _selected)
                return;
            // InWorldz: a drag overrides the vehicle, "otherwise severe squirming and tossing will happen" (VehicleDynamics.Simulate,
            // GrabTargetTau != 0 returns before anything else). The controller's clock does not run, so a drag of more than a second
            // ends with the motors reset, as a vehicle that has not been stepped for that long always is (CheckResetMotors).
            if (GrabOverridesVehicle)
                return;
            // Assert buoyancy on restart: re-assert the vehicle body params on the step-thread steps
            // of the first ReassertVehicleSeconds after (re)activation, so the gravity-cancellation that the load-path restore
            // set (but that the deferred body activation clobbered back to GravityFactor=1) actually takes -
            // otherwise a restored boat steps under full engine gravity and sinks despite vehicle=True.
            if (_reassertVehicleTime > 0f)
            {
                _reassertVehicleTime -= timeStep;
                ApplyVehicleBodyParams();
                // ★ ZERO the accumulated velocity when buoyancy is (re)asserted. A boat reloaded into the
                // region is born ACTIVE with gravity and can FREE-FALL for the whole load window (incl. the
                // multi-second reload stall) before the controller first steps here - reaching ~60 m/s. Buoyancy
                // only cancels gravity; it does NOT remove that already-accumulated velocity, so without this the
                // boat keeps plunging to the seabed despite vehicle=True. Zeroing (before BeginFrame/hover below)
                // arrests the fall; hover then lifts it from rest to the water surface. A live boat re-activated
                // at runtime is at rest anyway, so zeroing is a no-op for it.
                // Not once a script has set the linear motor: the vehicle is then being driven, and zeroing would
                // throw away the motor's first frames, a span that is longer the slower the step rate. Not for a saved
                // vehicle handed back with the region running (_reassertZeroesVelocity).
                if (_reassertZeroesVelocity && !_vehicle.LinearMotorSet)
                {
                    _backend.SetBodyLinearVelocity(_body, SVector3.Zero);
                    _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
                    _velocity = Vector3.Zero;
                    _rotationalVelocity = Vector3.Zero;
                }
            }
            if (!_vehicleBody.BeginFrame())
                return;
            // The vehicle's ground check (BulletSim's HasSomeCollision): touching anything in the last step. The engine
            // reports no contacts for a sleeping body, which has not moved since it last touched what it rests on, so
            // asleep it keeps the state it went to sleep with (otherwise a car parked slightly tilted would stop being
            // idle, its attractor no longer held by the ground, and wake on the next step).
            if (_vehicleBody.IsAwake)
                IsColliding = _backend.BodyHadContact(_body);
            // A parked vehicle sleeps like any other body. The engine may put it to sleep only while nothing in the
            // vehicle would move it (a vehicle drifting slowly toward its hover height must not be stopped: the engine
            // zeroes a body's velocity when it sleeps). Asleep and idle, the controller only lets time pass for it
            // (forces or velocity writes would wake it). A script setting a motor or any vehicle param wakes it
            // (WakeVehicle), as does a collision.
            bool idle = _vehicle.IsIdle;
            // A push or impulse since the last step (AddForce) is not yet in the body's velocity: the rest rule must not
            // hold the vehicle still against it, or put it to sleep with the force still waiting.
            bool pushed = _pushedSinceVehicleStep;
            _pushedSinceVehicleStep = false;
            if (idle != _vehicleMaySleep)
            {
                _backend.SetBodyAllowSleeping(_body, idle);
                _vehicleMaySleep = idle;
            }
            if (!_vehicleBody.IsAwake && idle)
            {
                _vehicleHeldTime = 0f;
                _vehicle.Rest(timeStep);
            }
            else if (idle && !pushed && _vehicle.HoldsAtRest)
            {
                // The rest rule ([Jolt] VehicleRestSpeed): nothing in the vehicle would move it and the equation would
                // not start it moving from where it is, so it is held still. Without this, a vehicle with no contact
                // friction resting a fraction of a degree tilted inside the engine's penetration allowance creeps
                // along the tilt at a few centimetres a second and never sleeps.
                // Held for the engine's time before sleep, it is put to sleep. Held still, there is nothing left for the
                // engine's own sleep test to wait for (in the harness it took about 2.5 s longer), and the time is then
                // the same at every step rate and on every platform.
                _vehicle.Hold(timeStep);
                _vehicleHeldTime += timeStep;
                if (_vehicleHeldTime >= HeldBeforeSleep)
                    _backend.DeactivateBody(_body);
            }
            else
            {
                _vehicleHeldTime = 0f;
                StepVehicleInEngine(timeStep);
            }
        }

        // The vehicle's step: its Update-rate part now, and the rest in the engine's collision steps (VehicleStepProvider).
        // A backend that takes no step force providers gets the whole step now, as the controller always ran.
        private void StepVehicleInEngine(float timeStep)
        {
            if (!EnsureVehicleStepProvider())
            {
                _vehicle.Step(timeStep);
                return;
            }
            // The engine calls a provider only for an awake body, and a vehicle that is not idle is awake: the Update-rate
            // path wakes it as it always has (a velocity written to a sleeping body woke it).
            if (!_vehicleBody.IsAwake)
                _backend.ActivateBody(_body);
            _vehicle.BeginUpdate(timeStep);
            Vector3 inertia = _vehicleBody.InertiaDiagonal;
            _vehicleStepProvider.Arm(_vehicle.BodyMass, inertia, IsColliding);
        }

        // The vehicle's step force provider and its attachment to the body it was made for. A body that is recreated
        // (reposition, reshape, weld) takes its providers with it, so the attachment is made again for the new one.
        private VehicleStepProvider _vehicleStepProvider;
        private IDisposable _vehicleStepAttachment;
        private BodyId _vehicleStepBody;
        private bool _vehicleStepUnavailable;
        private readonly object _vehicleStepGate = new object();

        // True when the provider is attached to the current body.
        private bool EnsureVehicleStepProvider()
        {
            if (_vehicleStepProvider == null || _vehicleStepUnavailable || !_body.IsValid)
                return false;
            lock (_vehicleStepGate)
            {
                if (_vehicleStepAttachment != null && _vehicleStepBody.Equals(_body))
                    return true;
                _vehicleStepAttachment?.Dispose();
                _vehicleStepAttachment = null;
                try
                {
                    _vehicleStepAttachment = _backend.AttachStepForceProvider(_body, _vehicleStepProvider);
                    _vehicleStepBody = _body;
                    return true;
                }
                catch (Exception e) when (e is NotSupportedException || e is NotImplementedException)
                {
                    _vehicleStepUnavailable = true;
                    return false;
                }
                catch (Exception e) when (e is ArgumentException || e is ObjectDisposedException)
                {
                    return false;   // the body is gone from the backend (removed meanwhile): nothing to step this Update
                }
            }
        }

        private void DetachVehicleStepProvider()
        {
            lock (_vehicleStepGate)
            {
                _vehicleStepProvider?.Disarm();
                _vehicleStepAttachment?.Dispose();
                _vehicleStepAttachment = null;
            }
        }

        // How long the rest rule has held the vehicle still (s), and how long it holds it before putting it to sleep:
        // Jolt's own time before sleep (PhysicsSettings.mTimeBeforeSleep, 0.5 s).
        private float _vehicleHeldTime;
        private const float HeldBeforeSleep = 0.5f;

        // Whether the vehicle body is allowed to sleep: only while its controller is idle (StepVehicle).
        private bool _vehicleMaySleep;

        // AddForce since the last StepVehicle (set on a script thread, read on the heartbeat).
        private volatile bool _pushedSinceVehicleStep;

        // A vehicle param, flag or type set by a script: the body wakes so the controller steps with it.
        private void WakeVehicle()
        {
            if (_isPhysical && _body.IsValid)
                _backend.ActivateBody(_body);
        }

        // The BulletSim vehicle body setup (BSDynamics.SetPhysicalParameters), translated: the vehicle decides its own
        // gravity itself (engine gravity off until its first step sets the vehicle's share).
        // It starts unable to sleep (BulletSim's DISABLE_DEACTIVATION); StepVehicle lets it sleep while nothing in the
        // vehicle would move it.
        // It has continuous collision detection: a car at 18 m/s moves 1.6 m in an 11 Hz step, more than its own height,
        // and two cars closing head on 3.2 m. Without it the engine can miss the contact before the step by a few
        // millimetres, end the step with the bodies deeply overlapped and push them apart along the shortest way out,
        // which is up: one car rode over the other and drove on at its motor's speed.
        // Re-applied after every body recreate (reposition/reshape/weld) while the vehicle is active.
        private void ApplyVehicleBodyParams()
        {
            if (_vehicle == null || !_vehicle.IsActive || !_isPhysical || !_body.IsValid)
                return;
            ApplyVehicleContactMaterial();
            _backend.SetBodyDamping(_body, PrimLinearDamping, PrimAngularDamping);
            _backend.SetBodyGravityFactor(_body, 0f);
            _backend.SetBodyAllowSleeping(_body, false);
            _backend.SetBodyContinuousCollision(_body, ContinuousCollision.On);
            _vehicleMaySleep = false;
            _backend.ActivateBody(_body);
            EnsureVehicleStepProvider();
        }

        // Contact friction and restitution of an active vehicle's body: each part keeps its own material's (Havok 4 and later,
        // and Halcyon for every vehicle type but the sled, VehicleDynamics.cs SetVehicleDefaultActions), so while the vehicle
        // slides in contact its friction is the material's plus the controller's friction timescales, and airborne only the
        // timescales act. [Jolt] VehicleContactFriction replaces every part's friction when set; a sled's parts all take
        // [Jolt] SledContactFriction (0 by default). Halcyon's sled zeroes the root's material and gives each child the
        // stone preset (Material.Duplicate), so only a single-prim sled was frictionless there; here every part is, and
        // each keeps its own restitution. Called again whenever the type or a part's material changes, so a vehicle
        // changed from a sled to another type gets its material friction back.
        private void ApplyVehicleContactMaterial()
        {
            _backend.SetBodyFriction(_body, _friction);
            _backend.SetBodyRestitution(_body, _restitution);
            if (_compoundShape.IsValid && _linkChildren != null)
                foreach (JoltPrim c in _linkChildren)
                    _backend.SetBodyPartMaterial(_body, c.LocalID, c._friction, c._restitution);
            bool sled = _vehicle!.Type == Vehicles.Vehicle.TYPE_SLED;
            float? friction = sled ? _vehicle.Settings.SledContactFriction : _vehicle.Settings.ContactFriction;
            if (friction.HasValue)
                _backend.SetBodyFriction(_body, friction.Value);   // every part's friction; restitutions stay
            // InWorldz: a sled takes the smaller friction of the two surfaces, so a part at 0 stays at 0 on a prim or the
            // land. Halcyon sets the sled's friction to 0 without choosing a combine rule; Jolt's average would turn it into
            // half of what it slides on, which holds a sled still on a 15 degree ramp.
            _backend.SetBodyFrictionMin(_body, sled && _vehicle.Settings.Behaviour == Vehicles.VehicleBehaviour.InWorldz);
        }

        private void RestoreVehicleBodyParams()
        {
            DetachVehicleStepProvider();
            if (!_body.IsValid)
                return;
            // Back to the prims' own physics material, and a prim's damping.
            _backend.SetBodyFriction(_body, _friction);
            _backend.SetBodyFrictionMin(_body, false);
            _backend.SetBodyRestitution(_body, _restitution);
            if (_compoundShape.IsValid && _linkChildren != null)
                foreach (JoltPrim c in _linkChildren)
                    _backend.SetBodyPartMaterial(_body, c.LocalID, c._friction, c._restitution);
            _backend.SetBodyDamping(_body, PrimLinearDamping, PrimAngularDamping);
            _backend.SetBodyGravityFactor(_body, ScriptGravityFactor);   // back to the script's buoyancy and gravity multiplier
            _backend.SetBodyAllowSleeping(_body, true);
            _backend.SetBodyContinuousCollision(_body, PhysicalPrimCcd);
        }

        // Move to target and hover are in JoltPrim.Targets.cs, the rotation target (APIDTarget, APIDStrength, APIDDamping,
        // APIDActive) in JoltPrim.AngularTarget.cs. Core does not hand the rotation target to the actor: it turns a physical
        // object toward llLookAt / llRotLookAt itself (SceneObjectPart.RotLookAt), as it does on ubODE.
    }
}
