/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A non-physical prim that keyframed motion (llSetKeyframedMotion) is moving carries what stands on it.
//
// Core moves such an object with plain position and rotation sets, about every 50 ms, from its own timer
// (KeyframeMotion.DoOnTimer), and tells the root prim's actor while it does (PhysicsActor.MovedByKeyframedMotion). A
// fixed body that is set to a new place has no velocity, so an avatar or a physical object on it stays where it was
// and the platform leaves from under it. The Second Life wiki says avatars stand on such an object while it moves
// ("... when standing on a moving platform", llSetKeyframedMotion).
//
// While the hint is on, the prim's body is a kinematic one (layer MovingStatic: it collides as a fixed prim does) and
// each set only records the pose. Once per heartbeat, before the step, the body is moved to the last pose over the time
// that step covers (JoltScene.StepKinematics -> StepKinematic), so it carries the linear and angular velocity of the
// motion: an avatar standing on it takes that velocity from it, and a physical object on it is carried by friction.
//   - A move longer than KinematicJumpDistance in one heartbeat is a jump, not a motion (a script or builder set the
//     object somewhere else): the body is put there with no velocity, so nothing is flung, and what rested on it is woken.
//   - Poses arrive about every 50 ms and a heartbeat is about 91 ms, so one heartbeat finds one set, the next two, and a
//     slow one three: moved to the last pose each time, the body's speed would swing by a half or more from one heartbeat
//     to the next and throw what rests on it. While poses come about every heartbeat the body is moved at the mean of
//     the last few heartbeats' speeds instead, kept within KinematicFollowBand of the last pose (StepTracked).
//   - A heartbeat in which no new pose arrived stops the body at the last pose (after at most one heartbeat of coasting at
//     the speed it had, which keeps a heartbeat that comes early from stopping a platform that is still moving): a
//     kinematic body keeps the velocity it was given, and would carry on past where the motion stopped.
//   - When the hint goes off (the motion stopped, was paused or played out, or the object was selected) the body is made
//     a fixed one again where the object is. Core sets the last pose and turns the hint off at its next tick, so a heartbeat
//     can find both: the body is kept kinematic for that heartbeat, which carries the waiting pose out like the ones before it,
//     and is made a fixed one at the start of the next (EndHint). Made a fixed body at once, it would be put at the last pose
//     in one go and what stands on it would be left behind by the whole of that heartbeat's move (1.5 m at 30 m/s).
// Phantom and volume-detect prims, physical prims and linkset children are not moved this way.

using System;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed partial class JoltPrim
    {
        // A move longer than this in one heartbeat is a jump (the pose is taken with no velocity), and so is a move faster
        // than KinematicJumpSpeed over a heartbeat longer than this distance allows. Keyframed motion at 3 m/s moves 0.27 m
        // a heartbeat, a lift at 20 m/s 1.8 m, and a plain position set by a script at most 10 m (llSetPos) in one go; 4 m
        // a heartbeat (44 m/s at the default heartbeat) carries every motion a build is likely to make and still tells a
        // set across the parcel from a motion, which would otherwise throw what stands on it at 100 m/s.
        internal const float KinematicJumpDistance = 4f;
        internal const float KinematicJumpSpeed = 40f;

        // Core's motion timer sets a pose every 50 ms, so a heartbeat holds the poses of a little more than the time its steps
        // cover: one more than its length divides into, and a timer call that came late moves up to three ticks at once
        // (KeyframeMotion.DoOnTimer). While keyframed motion moves the prim, the speed limit above is measured over the heartbeat
        // plus two ticks: at 30 m/s a heartbeat that finds three poses has 4.5 m to carry, which is not a jump.
        internal const float KinematicSetSlack = 0.1f;

        // While a motion's poses arrive about every heartbeat, the body is moved at the mean speed of the last
        // KinematicSpeedWindow heartbeats, never further than KinematicFollowBand from the last pose. A band of 4 cm keeps
        // what rests on the body within 5 cm of where the object's pose says the platform is; the window is a little over a
        // third of a second, long enough to even out a heartbeat that finds one set and the next three.
        internal const int KinematicSpeedWindow = 4;
        internal const float KinematicFollowBand = 0.04f;
        // Heartbeats with no new pose that the body coasts through at its speed before it is stopped at the last pose.
        internal const int KinematicCoastBeats = 1;
        // A series of plain sets whose mean gap is over this many heartbeats glides to each pose (StepMoveSegment); one that
        // comes more often is followed like keyframed motion (StepTracked).
        internal const float KinematicTrackGapBeats = 1.5f;

        // Core's hint: keyframed motion is moving this prim's object now.
        private volatile bool _keyframed;

        // The body is kinematic (see KinematicWanted); only the thread that makes the body writes it.
        private volatile bool _kinematicBody;

        // The last pose the object was given, and whether it is new since the last heartbeat's move. Written by whatever
        // thread sets the pose (core's motion timer), read by the step thread.
        private readonly object _kinLock = new object();
        private Vector3 _kinTarget;
        private Quaternion _kinRotation = Quaternion.Identity;
        private bool _kinPending;

        // The body was moved at its last step (so it has a velocity to take away if no new pose comes).
        private bool _kinMoving;

        // The hint went off while a pose was waiting for the heartbeat's move: the body stays kinematic for that heartbeat, which carries
        // the move out, and is made a fixed one at the start of the next (EndHint). Set by the thread that turns the hint off, cleared
        // on the step thread.
        private volatile bool _hintEnding;

        // The speeds of the last heartbeats in which the body followed a motion (step thread only), the number of them, the
        // next place to write, and the heartbeats since a pose last came.
        private readonly SVector3[] _speedWindow = new SVector3[KinematicSpeedWindow];
        private int _speedCount, _speedNext, _coasted;

        internal bool IsKinematicBody => _kinematicBody;

        public override bool MovedByKeyframedMotion
        {
            get => _keyframed;
            set
            {
                if (value)
                {
                    _hintEnding = false;   // told to move again before the last move was out
                    if (_keyframed)
                        return;
                    _keyframed = true;
                    _moveKinematic = false;   // the hint decides while keyframed motion moves the prim
                    SyncKinematicBody();
                    return;
                }
                if (!_keyframed)
                    return;
                if (_kinematicBody && PosePending())
                {
                    _hintEnding = true;   // the pose that is waiting is carried out by the next heartbeat, then EndHint
                    return;
                }
                _keyframed = false;
                SyncKinematicBody();
            }
        }

        private bool PosePending()
        {
            lock (_kinLock)
                return _kinPending;
        }

        // A single prim or linkset root that is not physical, phantom or a volume detector, while keyframed motion moves it
        // (the hint decides, whatever MovedPrimsCarryRiders says), or while a series of plain sets moves it and
        // [Jolt] MovedPrimsCarryRiders is on.
        private bool KinematicWanted
            => !_isPhysical && !_isPhantom && !_isVolumeDetect && _linkRoot == null
               && (_keyframed ? _module.KeyframedMotionCarriesRiders : _moveKinematic && _module.MovedPrimsCarryRiders);

        // Makes the body the kind the prim now wants: a kinematic one while keyframed motion moves it, a fixed one again
        // after. A static body cannot become kinematic (it has no motion properties; allocating them for every prim is the
        // memory cost the static rule avoids), so the body is made again where the object is. What rests on the prim is woken:
        // the new body has no record of what touched the old one. When a series of plain sets has just begun, the pose before
        // the set is given: the new body is put there and moved to the set's pose by the next StepKinematic, so the set
        // already carries what stands on the prim.
        private void SyncKinematicBody(Vector3? from = null, Quaternion? fromRotation = null)
        {
            if (_hintEnding)
            {
                _hintEnding = false;   // whatever remakes the body, the motion that was ending is over
                _keyframed = false;
            }
            if (IsRemoved || KinematicWanted == _kinematicBody)
                return;
            lock (_shapeLock)
            {
                if (IsRemoved || !_body.IsValid || _away != Away.None || IsWeldedChild)
                    return;
                _backend.RemoveBody(_body);
                CreateBodyInternal();   // reads KinematicWanted: layer, motion type and the scene's list of moving prims
                if (from.HasValue && _kinematicBody)
                {
                    _backend.SetBodyPoseAndVelocity(_body, ToS(from.Value), BodyOrientationOf(fromRotation ?? _orientation), SVector3.Zero, SVector3.Zero);
                    lock (_kinLock) _kinPending = true;   // CreateBodyInternal took the set's pose as the target
                }
            }
            WakeWhatRestsOn(from ?? _position, fromRotation ?? _orientation);
        }

        // A position or rotation set on the kinematic prim: only the pose is kept; StepKinematic moves the body.
        private void NoteKinematicPose(Vector3? position, Quaternion? rotation)
        {
            if (_moveKinematic)
            {
                double now = _module.KinematicClock;
                float gap = (float)(now - _moveLastClock);
                float heartbeat = _module.KinematicHeartbeat;
                if (gap > 0.5f * heartbeat)   // a second set in the same heartbeat is not a gap
                    _moveGap = Math.Clamp(0.5f * _moveGap + 0.5f * gap, heartbeat, (float)MoveMaxGapSeconds + heartbeat);
                _moveLastClock = now;
            }
            lock (_kinLock)
            {
                if (position.HasValue) _kinTarget = position.Value;
                if (rotation.HasValue) _kinRotation = rotation.Value;
                _kinPending = true;
            }
        }

        // ---------------------------------------------------------------------
        // [Jolt] MovedPrimsCarryRiders: a series of plain sets is a motion.
        //
        // A non-physical prim that llSetPos, llSetRegionPos, llSetPrimitiveParams or the build tool moves arrives as position
        // and rotation sets with no hint. Second Life does not carry an avatar over such a move (its wiki pages for the calls
        // say nothing of it), so with the setting off (the default) none of this runs and the prim is a fixed body set to each
        // new place. With it on, the second set of a series makes the body a kinematic one (as the hint does) and each new pose
        // moves it, as above; a quiet time ends the series and the body is fixed again.
        //   - A series: two sets that fall in different heartbeats, at most MoveMaxGapSeconds plus one heartbeat apart, the
        //     second no further than KinematicJumpDistance and no more than MoveJumpAngle from the first. llSetPos waits 0.2 s
        //     after each call, so a script that steps a platform with it is about 0.2 s between sets. Sets are stamped with
        //     the region's simulated time (JoltScene.KinematicClock), which counts whole heartbeats: a true gap of 0.2 s is
        //     seen as two or three heartbeats (0.18 or 0.27 s at 11 Hz), so one heartbeat is allowed over the 0.25 s.
        //   - The quiet time is MoveQuietSeconds with no set: above the longest gap a series allows (0.25 s plus a heartbeat),
        //     so a series is never cut between two of its sets, and short enough that a rider is not carried by a platform
        //     that has stopped for long (its body stops moving after one heartbeat with no new pose in any case).
        //   - The first set of a series begins it too when a physical prim or an avatar rests on the prim (JoltScene.AnythingRestsOn),
        //     so that set already carries it: a single llSetPos of a platform moves its rider. With nothing on the prim a set that
        //     is not part of a series leaves the body fixed.
        //   - A longer move, or a set the same heartbeat as the one before it (several sets of one call), is not a series: the
        //     body is set to the place as before.
        // A prim keyframed motion moves is not touched: the hint path decides.
        // ---------------------------------------------------------------------
        internal const double MoveMaxGapSeconds = 0.25;
        internal const double MoveQuietSeconds = 0.5;
        internal const float MoveFirstSetSpeed = 3f;   // metres a second
        internal const float MoveJumpAngle = MathF.PI * 0.5f;   // a quarter turn in one heartbeat is a jump, not a motion

        // The body is kinematic because a series of plain sets is moving the prim (the hint is not).
        private volatile bool _moveKinematic;
        private double _moveLastClock = double.NegativeInfinity;

        // A plain set may begin a series: the prim is a lone non-physical solid prim and the setting is on.
        private bool WatchesMoves
            => _module.MovedPrimsCarryRiders && !_keyframed && !_isPhysical && !_isPhantom && !_isVolumeDetect
               && _linkRoot == null && !_module.IsRegionLoading;

        // A set of the pose from (fromPos, fromRot) to the one the prim now has, on a fixed body. Returns true when it began a
        // series: the body is now kinematic and will be moved to the new pose by the next step.
        private bool NoteMoveSet(Vector3 fromPos, Quaternion fromRot)
        {
            double now = _module.KinematicClock;
            double gap = now - _moveLastClock;
            _moveLastClock = now;
            float heartbeat = _module.KinematicHeartbeat;
            bool inSeries = gap > 0.5 * heartbeat && gap <= MoveMaxGapSeconds + heartbeat;
            if (Vector3.Distance(fromPos, _position) > KinematicJumpDistance || AngleBetween(ToS(fromRot), ToS(_orientation)) > MoveJumpAngle)
                return false;
            if (inSeries)
                _moveGap = (float)gap;
            else if (gap > 0.5 * heartbeat && _module.AnythingRestsOn(ToS(fromPos), ToS(_size * 0.5f), ToS(fromRot), LocalID))
            {
                // The first set of a series, with a rider on the prim: it carries it too. Nothing says how soon the next set comes, so
                // the body takes the step at no more than MoveFirstSetSpeed (a step of 0.6 m, a floor lifted at 3 m/s, over 0.2 s),
                // and at least a heartbeat; a step under a heartbeat's worth of that speed is taken in one heartbeat.
                _moveGap = Math.Clamp(Vector3.Distance(fromPos, _position) / MoveFirstSetSpeed, heartbeat, (float)MoveMaxGapSeconds);
            }
            else
                return false;
            _moveKinematic = true;
            SyncKinematicBody(fromPos, fromRot);
            if (!_kinematicBody)
            {
                _moveKinematic = false;   // the body could not be made kinematic (it is being removed, or is away)
                return false;
            }
            return true;
        }

        // A child prim of a fixed linkset is set to a new place on its own (llSetLinkPrimitiveParams on a child, the build tool on one
        // child): the root's compound is rebuilt with it there, and what stands on the child would be left behind. With [Jolt]
        // MovedPrimsCarryRiders on, it takes the child's step with it (JoltScene.CarryRidersOfChild). Not for a root that is physical
        // or that keyframed motion is moving (the root's body carries the linkset then), for a phantom or volume-detect child, and not
        // for a jump (over KinematicJumpDistance or a quarter turn), which is a set somewhere else, not a motion.
        private void CarryRidersOfMove(Vector3 wasAt, Quaternion wasTurned)
        {
            JoltPrim root = _linkRoot;
            if (root == null || !_module.MovedPrimsCarryRiders || _module.IsRegionLoading || _isPhantom || _isVolumeDetect
                || root._isPhysical || root._keyframed)
                return;
            if (Vector3.Distance(wasAt, _position) > KinematicJumpDistance || AngleBetween(ToS(wasTurned), ToS(_orientation)) > MoveJumpAngle)
                return;
            _module.CarryRidersOfChild(ToS(wasAt), ToS(wasTurned), ToS(_position), ToS(_orientation), ToS(_size), root.LocalID);
        }

        // A series of plain sets arrives in steps (0.2 m every 0.2 s for llSetPos), and a body that took each step in one
        // heartbeat and then stood still would start and stop what stands on it again and again: a cube on it slides back and
        // falls off. So the body glides from where it is to each new pose over the time the sets are apart (the running mean of
        // the gaps, MoveGap), at the speed the sets add up to, and a rider keeps pace. The body therefore trails the object by
        // at most one step of the series. Written and read on the step thread only.
        private SVector3 _segFrom, _segTo;
        private SQuaternion _segFromRotation, _segToRotation;
        private float _segSeconds, _segElapsed;
        private bool _segActive;

        // The running mean of the time between sets, in simulated seconds (see NoteKinematicPose and NoteMoveSet).
        private volatile float _moveGap;

        private void StepMoveSegment(float seconds)
        {
            _segElapsed += seconds;
            float f = MathF.Min(1f, _segElapsed / _segSeconds);
            SVector3 at = SVector3.Lerp(_segFrom, _segTo, f);
            SQuaternion turned = SQuaternion.Slerp(_segFromRotation, _segToRotation, f);
            _backend.MoveKinematicBody(_body, at, turned, seconds);
            _kinMoving = true;
            if (f >= 1f)
                _segActive = false;
        }

        private void ForgetSpeeds()
        {
            _speedCount = 0;
            _speedNext = 0;
            _coasted = 0;
        }

        private SVector3 MeanSpeed()
        {
            SVector3 sum = SVector3.Zero;
            for (int i = 0; i < _speedCount; i++)
                sum += _speedWindow[i];
            return sum / MathF.Max(1, _speedCount);
        }

        // Moves the body from `from` towards the last pose at the mean of the speeds the last heartbeats asked for, so a
        // heartbeat that happens to find one set where the one before found two does not change the body's speed; the body is
        // kept within KinematicFollowBand of the pose, so it never falls behind the object by more than that.
        private void StepTracked(SVector3 from, SVector3 to, SQuaternion rotation, float seconds)
        {
            _speedWindow[_speedNext] = (to - from) / seconds;
            _speedNext = (_speedNext + 1) % KinematicSpeedWindow;
            _speedCount = Math.Min(_speedCount + 1, KinematicSpeedWindow);
            _coasted = 0;
            SVector3 next = from + MeanSpeed() * seconds;
            SVector3 off = to - next;
            float far = off.Length();
            if (far > KinematicFollowBand)
                next = to - off * (KinematicFollowBand / far);
            _backend.MoveKinematicBody(_body, next, rotation, seconds);
            _kinMoving = true;
        }

        // No new pose this heartbeat, but the body was following a motion a heartbeat ago: it goes on at the speed it had, and
        // stops at the last pose if that comes first.
        private bool CoastTracked(SVector3 to, SQuaternion rotation, float seconds)
        {
            if (_speedCount == 0 || _coasted >= KinematicCoastBeats || !_backend.TryGetBodyState(_body, out BodyState s))
                return false;
            SVector3 v = MeanSpeed();
            SVector3 next = s.Position + v * seconds;
            if (SVector3.Dot(to - next, v) < 0f)
                next = to;   // would pass the last pose
            _coasted++;
            _backend.MoveKinematicBody(_body, next, rotation, seconds);
            _kinMoving = true;
            return true;
        }

        // The series has ended: nothing has set the prim for the quiet time. The body stopped where the last pose put it, and
        // is made a fixed body there.
        private void EndMove()
        {
            _moveKinematic = false;
            SyncKinematicBody();
        }

        // Called by the scene on the step thread, before the step, with the simulated time the step (or the heartbeat's
        // burst of steps) covers.
        internal void StepKinematic(float seconds)
        {
            if (!_kinematicBody || !_body.IsValid || _linkRoot != null || !(seconds > 0f))
                return;
            bool pending;
            Vector3 target;
            Quaternion rotation;
            lock (_kinLock)
            {
                pending = _kinPending;
                target = _kinTarget;
                rotation = _kinRotation;
                _kinPending = false;
            }
            if (!pending && !_segActive && _moveKinematic && _module.KinematicClock - _moveLastClock > MoveQuietSeconds)
            {
                // The body has come to rest at the last pose (below); the fixed body made next is put where the object is.
                EndMove();
                return;
            }
            if (_hintEnding && !pending)
            {
                EndHint();
                return;
            }
            SQuaternion bodyRotation = BodyOrientationOf(rotation);
            if (pending)
            {
                if (!_backend.TryGetBodyState(_body, out BodyState s))
                    return;
                var from = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
                float jump = MathF.Max(KinematicJumpDistance, KinematicJumpSpeed * (_keyframed ? seconds + KinematicSetSlack : seconds));
                if (Vector3.Distance(from, target) > jump
                    || (_moveKinematic && AngleBetween(s.Orientation, bodyRotation) > MoveJumpAngle))
                {
                    _backend.SetBodyPoseAndVelocity(_body, ToS(target), bodyRotation, SVector3.Zero, SVector3.Zero);
                    _kinMoving = false;
                    _segActive = false;
                    ForgetSpeeds();
                    WakeWhatRestsOn(from, _orientation);
                }
                else if (_moveKinematic && _moveGap > KinematicTrackGapBeats * _module.KinematicHeartbeat)
                {
                    // A series of plain sets that come less often than every heartbeat: glide to the new pose over the time the sets are apart (see StepMoveSegment).
                    _segFrom = s.Position;
                    _segFromRotation = s.Orientation;
                    _segTo = ToS(target);
                    _segToRotation = bodyRotation;
                    _segSeconds = MathF.Max(seconds, _moveGap);
                    _segElapsed = 0f;
                    _segActive = true;
                    ForgetSpeeds();
                    StepMoveSegment(seconds);
                }
                else
                {
                    // Keyframed motion, or a series of plain sets that comes about every heartbeat.
                    _segActive = false;
                    StepTracked(s.Position, ToS(target), bodyRotation, seconds);
                }
            }
            else if (_segActive)
            {
                StepMoveSegment(seconds);
            }
            else if (CoastTracked(ToS(target), bodyRotation, seconds))
            {
            }
            else if (_kinMoving)
            {
                _backend.SetBodyPoseAndVelocity(_body, ToS(target), bodyRotation, SVector3.Zero, SVector3.Zero);
                _kinMoving = false;
                ForgetSpeeds();
            }
        }

        // The heartbeat that carried out the last pose of keyframed motion is over: the body, within KinematicFollowBand of that pose,
        // is made a fixed one where the object is, as when the hint goes off with no pose waiting.
        private void EndHint()
        {
            _hintEnding = false;
            _keyframed = false;
            _kinMoving = false;
            _segActive = false;
            ForgetSpeeds();
            SyncKinematicBody();
        }
    }
}
