/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// An avatar as a Jolt CharacterVirtual.
//
// This is the PhysicsActor OpenSim hands back from AddAvatar. It is backed by a Jolt CharacterVirtual,
// NOT a solver rigid body: the movement layer keeps control, so stair-stepping / slope handling /
// moving-platform support come from the controller, it collides against dynamic bodies,
// and it carries a kinematic query marker so llCastRay(agent) can find it. The backend already
// implements all of that; this class is the OpenSim-facing wiring.
//
// Drive (ScenePresence -> here): TargetVelocity / Velocity (walk/run intent, scaled by the [Jolt] avatar speed
// factors; see DesiredVelocity), SetAlwaysRun (running), Flying (gravity on/off),
// AvatarJump (one-shot jump), Position (teleport), Size (appearance). All fold into one push through
// SetCharacterMovement / SetCharacterTransform.
// Drain (here -> ScenePresence): ApplyCharacterState, called once per Step from the scene's character
// drain, writes back Position/Velocity + ground state and fires the terse update so the viewer sees
// smooth movement - the avatar equivalent of JoltPrim.ApplyStepState (the body drain).
//
// Types: PhysicsActor speaks OpenMetaverse.Vector3/Quaternion (unqualified here); the backend speaks
// System.Numerics (SVector3/SQuaternion).

using System;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class JoltCharacter : PhysicsActor
    {
        private readonly JoltScene _module;
        private readonly IPhysicsBackend _backend;

        private Vector3 _position;
        private Vector3 _velocity;          // last drained linear velocity (ScenePresence reads this for terse updates)
        private Vector3 _targetVelocity;    // desired velocity from ScenePresence (walk/run intent)
        private Vector3 _size;
        private Quaternion _orientation = Quaternion.Identity;
        private bool _flying;
        private bool _setAlwaysRun;

        // Jump is a LATCH, not a one-shot. The animator calls AvatarJump ONCE, but ScenePresence writes
        // TargetVelocity every movement update (and zeroes its Z - the vertical only ever comes through
        // AvatarJump), so a SetCharacterMovement(jump=false) reliably lands before the physics step
        // consumes the jump. Holding the latch true across those pushes - and clearing it only once the
        // drain shows the avatar has actually left the ground - is what makes the jump survive.
        private bool _jumpLatched;

        private readonly float _feetOffset;   // capsule-centre -> visual feet gap; seats the avatar ON ground

        // Every controller tuning knob is held here so feel is a knob-turning problem, not an architecture one.
        private float _capsuleHalfHeight;   // EXCLUDING the caps (Jolt convention)
        private float _capsuleRadius;
        private float _mass;

        private int _subscribedMs;

        // This avatar's last non-finite-rejection log line (NonFiniteGuard rate limit, one per 10 s).
        private long _nonFiniteLogTicks;

        // Cached ground state from the last drain, surfaced by `jolt avatarstatus`.
        private bool _isSupported;
        private bool _isSliding;
        private Vector3 _groundNormal;
        private BodyId _groundBody = BodyId.Invalid;
        private bool _groundIsTerrain;

        private CharacterId _character = CharacterId.Invalid;

        internal CharacterId CharacterHandle => _character;
        internal bool IsSupported => _isSupported;
        internal bool IsSliding => _isSliding;
        internal Vector3 GroundNormal => _groundNormal;
        internal BodyId GroundBody => _groundBody;
        internal bool GroundIsTerrain => _groundIsTerrain;
        internal float CapsuleHalfHeight => _capsuleHalfHeight;
        internal float CapsuleRadius => _capsuleRadius;

        // How far the capsule CENTRE sits above the feet: cylinder half-height + one hemispherical cap.
        // Spawn/rest Z = groundZ + StandHalf so the feet touch the surface (not underground).
        internal float StandHalf => _capsuleHalfHeight + _capsuleRadius;
        internal float FeetOffset => _feetOffset;

        internal JoltCharacter(JoltScene module, IPhysicsBackend backend, uint localid, string name,
                               Vector3 position, Vector3 size, float feetOffset, bool isFlying, bool allowOutsideRegion = false)
        {
            // set before the controller exists: it is stepped from the moment CreateCharacterInternal has made it
            AllowOutsideRegion = allowOutsideRegion;
            _module = module;
            _backend = backend;
            LocalID = localid;
            Name = name;
            _size = size;
            _position = position;
            _feetOffset = feetOffset;
            _flying = isFlying;

            CapsuleFromSize(size, out _capsuleHalfHeight, out _capsuleRadius);
            _mass = MassFor(size);
            CreateCharacterInternal();
        }

        // SL avatar box (X=width, Y=depth, Z=height) -> a standing capsule. Radius from the footprint;
        // the cylinder half-height is what remains after the two hemispherical caps. Clamped so a thin or
        // short appearance still yields a valid capsule.
        private static void CapsuleFromSize(Vector3 size, out float halfHeight, out float radius)
        {
            float r = MathF.Max(0.2f, MathF.Min(size.X, size.Y) * 0.5f);
            float totalHalf = MathF.Max(r + 0.1f, size.Z * 0.5f);   // half-height must exceed a single cap
            radius = r;
            halfHeight = MathF.Max(0.05f, totalHalf - r);
        }

        // The mass of a default-size avatar. Second Life's llGetObjectMass page: avatar mass "was fixed at 1.954922
        // lindogram" and "is now relative to Avatar shape/size". Mass here is in the same units as a prim's (llGetMass).
        internal const float DefaultAvatarMass = 1.954922f;

        // The box core hands AddAvatar for the default appearance (X width, Y depth, Z height): the appearance's 0.45 x 0.6 x 1.9
        // with 0.2 m added to the height (AvatarAppearance AVBOXAJUST).
        private static readonly Vector3 DefaultAvatarSize = new Vector3(0.45f, 0.6f, 2.1f);

        // Mass from shape and size: the default avatar's mass scaled by the volume of the capsule the size gives, against
        // the default size's. Halcyon does the same by volume (PhysxCharacter: density x capsule volume); the density is
        // set so the default avatar weighs the Second Life figure.
        internal static float MassFor(Vector3 size)
        {
            float v = CapsuleVolume(size);
            float v0 = CapsuleVolume(DefaultAvatarSize);
            return DefaultAvatarMass * (v / v0);
        }

        private static float CapsuleVolume(Vector3 size)
        {
            CapsuleFromSize(size, out float hh, out float r);
            return MathF.PI * r * r * (2f * hh + 4f / 3f * r);
        }

        // The stand-half for a given appearance size, WITHOUT constructing a character - the scene needs
        // it to compute the spawn Z (groundZ + StandHalf) before the capsule exists.
        internal static float StandHalfFor(Vector3 size)
        {
            CapsuleFromSize(size, out float hh, out float r);
            return hh + r;
        }

        private void CreateCharacterInternal()
        {
            CharacterDesc desc = CharacterDesc.Default;
            desc.Position = ToS(_position);
            desc.Orientation = ToS(_orientation);
            desc.CapsuleHalfHeight = _capsuleHalfHeight;
            desc.CapsuleRadius = _capsuleRadius;
            desc.Mass = _mass;
            desc.JumpSpeed = _module.AvatarJumpSpeed;   // [Jolt] AvatarJumpSpeed (default 4.0 = CharacterDesc.Default)
            desc.WantsContactEvents = _subscribedMs > 0;
            desc.UserData = LocalID;   // echoed in every query hit / drain - the query-marker identity

            _character = _backend.CreateCharacter(desc);

            // No off-thread activation dance is needed here (unlike a physical prim): CreateCharacter
            // adds the controller to _characterList UNDER _characterGate, and Step iterates that same list
            // under the same lock, so the avatar is stepped from the very next frame. The physical BODY needed
            // an explicit ActivateBody only because the step-thread-owned active-set is fed through a queue.
            // Push the initial intent so frame 1 already has flying/desired set and reports a valid state.
            PushMovement();
            if (_constantForce != Vector3.Zero)
                PushConstantForce();
        }

        // The scene's per-step character drain hands back the CharacterVirtual's post-ExtendedUpdate state.
        // Write the cached values ScenePresence reads (Position/Velocity) + the ground state, then fire the
        // terse update so the viewer sees smooth movement. Ground truth flows one way here: the drain writes
        // _position directly, so it never trips the Position setter (which would force a transform).
        internal void ApplyCharacterState(in CharacterState s)
        {
            _positionBeforeStep = _position;
            _position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            KeepInRegion();
            _velocity = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            _isSupported = s.IsSupported;
            _isSliding = s.IsSliding;
            _groundNormal = new Vector3(s.GroundNormal.X, s.GroundNormal.Y, s.GroundNormal.Z);
            _groundBody = s.GroundBody;

            _groundIsTerrain = s.GroundIsTerrain;

            // The terrain is a body in the solve, so "has a ground body" does not mean "stands on a prim": the
            // backend says which body is the terrain. Supported with no body at all is read as ground, as before.
            IsColliding = s.IsSupported;
            CollidingGround = s.IsSupported && (s.GroundIsTerrain || !s.GroundBody.IsValid);
            CollidingObj = s.IsSupported && s.GroundBody.IsValid && !s.GroundIsTerrain;

            // Release the jump latch once the avatar has actually left the ground (rising, or no longer
            // supported). Until then the latch keeps re-asserting jump=true so a TargetVelocity push can't
            // cancel it before the step consumes it; after takeoff we stop, so there is no bunny-hop.
            if (_jumpLatched && (_velocity.Z > 0.5f || !s.IsSupported))
            {
                _jumpLatched = false;
                if (JoltScene.CharJumpTrace)
                    JoltScene.m_log.LogDebug($"{JoltScene.LogHeader} [charjump] id={LocalID} TAKEOFF vZ={_velocity.Z:0.000} supported={s.IsSupported} -> latch cleared");
            }

            RequestPhysicsterseUpdate();
        }

        // The avatar's x and y are kept 0.01 m inside the region's edges after every step, as ubODE does
        // (ODECharacter: "check outbounds forcing to be in world"). An avatar left outside (stood up from a seat that
        // waits past the edge) would otherwise make ScenePresence.CheckForBorderCrossing start a crossing every frame.
        // A walking or flying avatar still crosses: core reads position plus velocity times the frame time.
        private const float EdgeMargin = 0.01f;

        private void KeepInRegion()
        {
            // an avatar carried in by an object stands where the object puts it, possibly outside the region
            if (AllowOutsideRegion)
                return;
            float maxX = _module.RegionSizeX - EdgeMargin, maxY = _module.RegionSizeY - EdgeMargin;
            float x = Math.Clamp(_position.X, EdgeMargin, maxX), y = Math.Clamp(_position.Y, EdgeMargin, maxY);
            if (x == _position.X && y == _position.Y)
                return;
            _position.X = x;
            _position.Y = y;
            if (_character.IsValid)
                _backend.SetCharacterTransform(_character, ToS(_position), ToS(_orientation));
        }

        // One channel for all movement intent: desired velocity + a one-shot jump + the flying flag. The
        // backend integrates gravity, ground velocity and the jump in StepCharacter; jump is consumed there
        // exactly once, so we clear the local pending flag after pushing it.
        private void PushMovement()
        {
            if (!_character.IsValid)
                return;
            // Send the current latch state. StepCharacter jumps from solid ground when it sees jump=true and
            // clears its own request every step; the drain releases our latch on takeoff.
            _backend.SetCharacterMovement(_character, ToS(DesiredVelocity()), _jumpLatched, _flying);
            if (_jumpLatched && JoltScene.CharJumpTrace)
                JoltScene.m_log.LogDebug($"{JoltScene.LogHeader} [charjump] id={LocalID} sent jump=true to backend (target={_targetVelocity} flying={_flying})");
        }

        // The velocity the controller is driven at: ScenePresence's request times the [Jolt] avatar speed factor for
        // flying, running (always run on) or walking. A walk or run scales the horizontal part only (the controller
        // takes no vertical intent off a flight); a flight scales all three, so straight up goes as fast as level, and
        // its downward part by the fly-down factor, since Second Life documents a faster descent (22.87 m/s against 16.00).
        internal Vector3 DesiredVelocity()
        {
            if (_flying)
            {
                Vector3 v = _targetVelocity * _module.AvatarFlySpeedFactor;
                if (_targetVelocity.Z < 0f)
                    v.Z = _targetVelocity.Z * _module.AvatarFlyDownSpeedFactor;
                return v;
            }
            float f = _setAlwaysRun ? _module.AvatarRunSpeedFactor : _module.AvatarWalkSpeedFactor;
            return new Vector3(_targetVelocity.X * f, _targetVelocity.Y * f, _targetVelocity.Z);
        }

        internal void Destroy()
        {
            if (_character.IsValid)
                _backend.RemoveCharacter(_character);
            _character = CharacterId.Invalid;
        }

        private static SVector3 ToS(Vector3 v) => new SVector3(v.X, v.Y, v.Z);
        private static SQuaternion ToS(Quaternion q) => new SQuaternion(q.X, q.Y, q.Z, q.W);

        // ---------------------------------------------------------------------
        // PhysicsActor contract. Live state: Position / Velocity / TargetVelocity / Flying / Size.
        // ---------------------------------------------------------------------

        public override Vector3 Position
        {
            get => _position;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "Position", value.ToString()); return; }
                // Only ScenePresence writes this (teleport / direct set); the drain writes _position
                // directly, so this force-transform never fights passive physics motion.
                _position = value;
                if (_character.IsValid)
                    _backend.SetCharacterTransform(_character, ToS(value), ToS(_orientation));
            }
        }

        // Un-bury: snap the capsule to `pos` (region coords, capsule centre) and clear velocity, so a live
        // terrain raise that left this avatar below the new surface lifts it back onto the ground without
        // carrying the accumulated fall speed into the next step. Routed through the gated backend
        // (ReGroundCharacter) so it can't race the per-step CharacterVirtual update. Called only from
        // JoltScene's post-SetTerrain re-ground pass. The next drain reads the new position back.
        internal void ReGround(Vector3 pos)
        {
            _position = pos;
            _velocity = Vector3.Zero;
            _targetVelocity = Vector3.Zero;
            if (_character.IsValid)
                _backend.ReGroundCharacter(_character, ToS(pos));
        }

        // The object this avatar stands on was set back (JoltPrim.FinishBorderPin) from where it stood before the step to
        // `objectNow`. The avatar goes to the same place on it that it had before the step, and loses the velocity the object
        // gave it on the way out, so that it is still on the object and not thrown off it. Its walking request stays.
        internal void PlaceBackOnGround(Vector3 objectBefore, Vector3 objectNow)
        {
            _position = objectNow + (_positionBeforeStep - objectBefore);
            _velocity = Vector3.Zero;
            if (_character.IsValid)
                _backend.ReGroundCharacter(_character, ToS(_position));
            RequestPhysicsterseUpdate();
        }

        // Where the last drain before this one left the avatar.
        private Vector3 _positionBeforeStep;

        // Facing is radially symmetric for a vertical capsule, so orientation does not change collision.
        // Cache it (the viewer's display rotation comes from the client's body-rot, not physics) and avoid
        // pushing a transform on every turn, which would re-seat position and fight the drain. A local constant force
        // turns with it.
        public override Quaternion Orientation
        {
            get => _orientation;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "Orientation", value.ToString()); return; }
                _orientation = value;
                if (_constantForceLocal && _constantForce != Vector3.Zero)
                    PushConstantForce();
            }
        }

        // ScenePresence's Velocity setter routes here as well as TargetVelocity; both are walk/run intent.
        public override Vector3 Velocity
        {
            get => _velocity;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "Velocity", value.ToString()); return; }
                _targetVelocity = value;
                PushMovement();
            }
        }

        // The PRIMARY movement command ScenePresence writes each frame.
        public override Vector3 TargetVelocity
        {
            get => _targetVelocity;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "TargetVelocity", value.ToString()); return; }
                _targetVelocity = value;
                PushMovement();
            }
        }

        public override bool Flying
        {
            get => _flying;
            set { if (_flying == value) return; _flying = value; PushMovement(); }
        }

        public override void AvatarJump(float forceZ)
        {
            _jumpLatched = true;   // held until takeoff (see ApplyCharacterState); jump height is the JumpSpeed knob
            if (JoltScene.CharJumpTrace)
                JoltScene.m_log.LogDebug($"{JoltScene.LogHeader} [charjump] id={LocalID} AvatarJump(forceZ={forceZ:0.00}) fired -> latch set (supported={_isSupported} flying={_flying})");
            PushMovement();
        }

        public override void SetMomentum(Vector3 momentum)
        {
            if (!NonFiniteGuard.Ok(momentum)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "Momentum", momentum.ToString()); return; }
            _velocity = momentum;
            // The velocity an avatar arrived with is handed to the backend as momentum, which gives way to whatever its controls ask
            // for: on the ground over a short fade, flying likewise. Held as the movement request, it would go on at that velocity
            // until the avatar's controls next set one, and an avatar that crossed with no key held would never stop. A flight's
            // vertical part is not carried: a flying avatar's climb and descent follow its controls, as they do when a key is let go.
            if (_character.IsValid)
                _backend.SetCharacterCarry(_character, ToS(momentum));
            PushMovement();
        }

        public override Vector3 Size
        {
            get => _size;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "Size", value.ToString()); return; }
                if (_size == value) return;
                _size = value;
                CapsuleFromSize(value, out _capsuleHalfHeight, out _capsuleRadius);
                _mass = MassFor(value);
                if (_character.IsValid)
                {
                    _backend.SetCharacterMass(_character, _mass);
                    _backend.SetCharacterShape(_character, _capsuleHalfHeight, _capsuleRadius);
                }
            }
        }

        public override int PhysicsActorType { get => (int)ActorTypes.Agent; set { } }
        public override bool IsPhysical { get => true; set { } }
        public override float Mass => _mass;
        public override bool Stopped => _velocity == Vector3.Zero;

        public override Vector3 GeometricCenter => _position;
        public override Vector3 CenterOfMass => _position;

        // Running is this flag (the viewer's SetAlwaysRun), not a faster request: ScenePresence asks for the same speed.
        public override bool SetAlwaysRun
        {
            get => _setAlwaysRun;
            set { if (_setAlwaysRun == value) return; _setAlwaysRun = value; PushMovement(); }
        }

        // Collision-event subscription: gates Persist forwarding; the window is stored. ScenePresence subscribes
        // right after AddAvatar, when the controller already exists, so the gate is set on that character too.
        public override void SubscribeEvents(int ms)
        {
            _subscribedMs = ms;
            if (_character.IsValid)
                _backend.SetCharacterWantsContactEvents(_character, ms > 0);
        }
        public override void UnSubscribeEvents()
        {
            _subscribedMs = 0;
            if (_character.IsValid)
                _backend.SetCharacterWantsContactEvents(_character, false);
        }
        public override bool SubscribedEvents() => _subscribedMs > 0;

        // ---- inert for an avatar (the controller owns velocity; no vehicles, no move-to-target PID: core moves an avatar to
        // a target through TargetVelocity; pushes are AddForce below) ----
        public override Vector3 RotationalVelocity { get => Vector3.Zero; set { } }
        public override Vector3 Torque { get => Vector3.Zero; set { } }
        public override Vector3 Force { get => Vector3.Zero; set { } }
        public override Vector3 Acceleration { get => Vector3.Zero; set { } }
        public override float CollisionScore { get; set; }
        public override bool Kinematic { get => false; set { } }
        public override bool ThrottleUpdates { get => false; set { } }
        public override bool IsColliding { get; set; }
        public override bool CollidingGround { get; set; }
        public override bool CollidingObj { get; set; }
        public override bool Grabbed { set { } }
        public override bool Selected { set { } }
        public override PrimitiveBaseShape Shape { set { } }

        public override void CrossingFailure() { }
        public override void link(PhysicsActor obj) { }
        public override void delink() { }
        public override void LockAngularMotion(byte axislocks) { }

        // A push: llPushObject on this avatar, or an attachment's llApplyImpulse on its wearer (both reach here as an
        // impulse). The avatar's velocity changes by impulse / its mass (the shape-and-size mass above), within the push
        // limits the backend holds it to ([Jolt] AvatarPushMaxSpeed, AvatarPushRecovery). Second Life ignores the angular part on an avatar
        // (llPushObject ignores the angular impulse for agents and their attachments, wiki.secondlife.com/wiki/LlPushObject), so AddAngularForce
        // stays inert.
        public override void AddForce(Vector3 force, bool pushforce)
        {
            if (!NonFiniteGuard.Ok(force)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "AddForce", force.ToString()); return; }
            if (_character.IsValid && _mass > 0f)
                _backend.AddCharacterImpulse(_character, ToS(force / _mass));
        }
        public override void AddAngularForce(Vector3 force, bool pushforce) { }

        // ---- an attachment's llSetForce, which core hands to the wearer's actor ----

        private Vector3 _constantForce;
        private bool _constantForceLocal;

        // llSetForce on an attachment applies the force to the avatar (wiki.secondlife.com/wiki/LlSetForce). The force is in newtons and
        // the avatar accelerates by force / its mass on every step until the next call; zero ends it. A local force is in the
        // avatar's own axes: it is turned by the avatar's orientation now, and again whenever that changes, so it turns with
        // the avatar. The actor keeps it and gives it to every backend character it makes; core hands it to a new actor.
        public override void SetConstantForce(Vector3 force, bool local)
        {
            if (!NonFiniteGuard.Ok(force)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "ConstantForce", force.ToString()); return; }
            _constantForce = force;
            _constantForceLocal = local;
            PushConstantForce();
        }

        private void PushConstantForce()
        {
            if (!_character.IsValid || !(_mass > 0f))
                return;
            Vector3 force = _constantForceLocal ? _constantForce * _orientation : _constantForce;
            _backend.SetCharacterConstantForce(_character, ToS(force / _mass));
        }

        // ---- an attachment's llSetBuoyancy and llSetHoverHeight, which core hands to the wearer's actor ----

        private float _buoyancy;
        private float _hoverHeight;
        private float _hoverTau;
        private PIDHoverType _hoverType;
        private bool _hoverActive;

        // llSetBuoyancy (wiki.secondlife.com/wiki/LlSetBuoyancy): below 1.0 the object sinks, at 1.0 it floats, above 1.0 it
        // rises. On the avatar it scales gravity by 1 - buoyancy while it neither flies
        // nor hovers, as ubODE's avatar does (ODECharacter.MoveCharacter).
        public override float Buoyancy
        {
            get => _buoyancy;
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "Buoyancy", value.ToString()); return; }
                _buoyancy = value;
                if (_character.IsValid)
                    _backend.SetCharacterBuoyancy(_character, value);
            }
        }

        public override float PIDHoverHeight
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "PIDHoverHeight", value.ToString()); return; }
                _hoverHeight = Math.Clamp(value, -JoltPrim.MaxHoverHeight, JoltPrim.MaxHoverHeight);
                PushHover();
            }
        }

        public override PIDHoverType PIDHoverType
        {
            set { _hoverType = value; PushHover(); }
        }

        public override float PIDHoverTau
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "avatar", LocalID, "PIDHoverTau", value.ToString()); return; }
                _hoverTau = value;
                PushHover();
            }
        }

        public override bool PIDHoverActive
        {
            get => _hoverActive;
            set { _hoverActive = value; PushHover(); }
        }

        // Hover acts while it is asked for with a height other than 0 ("Assigning height a value of zero will have the
        // same effect as llStopHover", llSetHoverHeight) and a tau above 0, as ubODE's avatar requires
        // (m_PIDHoverTau != 0 && m_PIDHoverHeight != 0). llStopHover ends it and the avatar falls from where it was held.
        private void PushHover()
        {
            if (!_character.IsValid)
                return;
            bool on = _hoverActive && _hoverHeight != 0f && _hoverTau > 0f;
            _backend.SetCharacterHover(_character, on ? HoverHeightAt(_hoverHeight, _hoverType) : null, _hoverTau);
        }

        // Where hover holds the capsule centre over (x, y), as a prim's hover reads its base (JoltPrim.HoverTargetAt):
        // the ground, the higher of ground and water (water TRUE, "hover above water too"), the water, or the region's
        // zero, plus the height. Never lower than standing on the ground: "negative height values when water is FALSE
        // will not move the object below the ground level". The height is the avatar's position above that base, as in
        // ubODE (its _position.Z against terrain + height).
        private Func<float, float, float> HoverHeightAt(float height, PIDHoverType type) => (x, y) =>
        {
            float ground = _module.TerrainHeightAt(x, y);
            float water = _module.WaterLevel;
            float baseZ = type switch
            {
                PIDHoverType.GroundAndWater => MathF.Max(ground, water),
                PIDHoverType.Water => water,
                PIDHoverType.Absolute => 0f,
                _ => ground,
            };
            return MathF.Max(baseZ + height, ground + StandHalf);
        };
        public override void SetVolumeDetect(int param) { }

        public override int VehicleType { get => 0; set { } }
        public override void VehicleFloatParam(int param, float value) { }
        public override void VehicleVectorParam(int param, Vector3 value) { }
        public override void VehicleRotationParam(int param, Quaternion rotation) { }
        public override void VehicleFlags(int param, bool remove) { }

        public override Vector3 PIDTarget { set { } }
        public override bool PIDActive { get => false; set { } }
        public override float PIDTau { set { } }
        public override Quaternion APIDTarget { set { } }
        public override bool APIDActive { set { } }
        public override float APIDStrength { set { } }
        public override float APIDDamping { set { } }
    }
}
