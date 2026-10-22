/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The per-frame collision set for the Jolt module.
//
// OpenSim's SceneObjectPart.PhysicsCollision diffs each CollisionEventUpdate against the previous one to fire
// collision_start / collision / collision_end. So a subscribed prim gets ONE update per frame listing what it
// touches, and a prim that touched last frame but not this one needs one EMPTY update - that is its
// collision_end. This class owns that accumulate / hold / end decision, with no Scene, so it can be tested.

using System.Collections.Generic;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class CollisionFrameTracker
    {
        private Dictionary<uint, CollisionEventUpdate> _accum = new Dictionary<uint, CollisionEventUpdate>();
        private Dictionary<uint, CollisionEventUpdate> _previous = new Dictionary<uint, CollisionEventUpdate>();
        private readonly HashSet<uint> _fresh = new HashSet<uint>();
        private readonly HashSet<uint> _collidedLastFrame = new HashSet<uint>();
        private readonly List<uint> _ended = new List<uint>();

        /// <summary>This frame's accumulated updates, keyed by the struck prim's LocalID.</summary>
        internal Dictionary<uint, CollisionEventUpdate> Current => _accum;

        /// <summary>Last frame's sets, delivered or not (valid after <see cref="BeginFrame"/>): the contacts a sleeping
        /// body may still have.</summary>
        internal Dictionary<uint, CollisionEventUpdate> Previous => _previous;

        /// <summary>The prims that reported collisions last frame (the collision_end candidates).</summary>
        internal HashSet<uint> CollidedLastFrame => _collidedLastFrame;

        internal bool IsTracked(uint localId) => _collidedLastFrame.Contains(localId);

        /// <summary>The simulated time of the frame being built, in seconds; the scene sets it each heartbeat.</summary>
        internal double Now;

        /// <summary>The shortest time between two sends of an unchanged set to one part. The Second Life wiki page for the
        /// collision event: "Smallest repeat rate is approximatively 0.13 seconds."</summary>
        internal const double MinRepeatSeconds = 0.13;

        private readonly Dictionary<uint, double> _lastSent = new Dictionary<uint, double>();

        /// <summary>
        /// <see cref="ShouldDeliver"/> for a prim (a part with a script), with the collision event's repeat rate: a set that
        /// names the same colliders as last frame's goes again only once <see cref="MinRepeatSeconds"/> have passed since
        /// the last one sent. A set that differs (a new collider is collision_start, a missing one must be told) goes at
        /// once, and so does the first after a gap; collision_end is the empty update <see cref="EndFrame"/> reports and is
        /// never held back. A set naming only the ground (land_collision, whose wiki page gives no repeat rate) is not
        /// held back either. Avatars do not use this: the scene reads their updates for the collision plane under the feet
        /// and for impact damage, which must not lag.
        /// </summary>
        internal bool ShouldDeliverToPart(uint prim, CollisionEventUpdate set)
        {
            if (!ShouldDeliver(prim, set))
                return false;
            if (!NamesAnObject(set))
                return true;   // the ground alone is land_collision, whose wiki page gives no repeat rate: every heartbeat, as ubODE
            if (_previous.TryGetValue(prim, out CollisionEventUpdate last) && SameColliders(set, last)
                && _lastSent.TryGetValue(prim, out double sent) && Now - sent < MinRepeatSeconds - 1e-9)
                return false;
            _lastSent[prim] = Now;
            return true;
        }

        private static bool NamesAnObject(CollisionEventUpdate set)
        {
            foreach (uint id in set.m_objCollisionList.Keys)
                if (id != 0)
                    return true;
            return false;
        }

        private static bool SameColliders(CollisionEventUpdate a, CollisionEventUpdate b)
        {
            Dictionary<uint, ContactPoint> x = a.m_objCollisionList, y = b.m_objCollisionList;
            if (x.Count != y.Count)
                return false;
            foreach (uint id in x.Keys)
                if (!y.ContainsKey(id))
                    return false;
            return true;
        }

        // Top Colliders, ubODE's model - a prim's CollisionScore is the number of Begin/Persist
        // contact reports that named it this frame, counted BEFORE the subscription filter, and reset every frame.
        // _prevScored is last frame's scored set, so the module can zero the prims that dropped out.
        private readonly Dictionary<uint, int> _scores = new Dictionary<uint, int>();
        private readonly List<uint> _prevScored = new List<uint>();

        /// <summary>This frame's contact count per struck prim.</summary>
        internal Dictionary<uint, int> Scores => _scores;

        /// <summary>The prims that had a score last frame (valid after <see cref="BeginFrame"/>).</summary>
        internal List<uint> PreviouslyScored => _prevScored;

        internal void BeginFrame()
        {
            (_previous, _accum) = (_accum, _previous);
            _accum.Clear();
            _fresh.Clear();
            _prevScored.Clear();
            foreach (uint id in _scores.Keys)
                _prevScored.Add(id);
            _scores.Clear();
        }

        /// <summary>One Begin/Persist report named <paramref name="struckPrim"/> (0 = terrain, not scored).</summary>
        internal void CountContact(uint struckPrim)
        {
            if (struckPrim == 0)
                return;
            _scores.TryGetValue(struckPrim, out int n);
            _scores[struckPrim] = n + 1;
        }

        /// <summary>The <paramref name="cap"/> highest-scored entries, highest first.</summary>
        internal static List<KeyValuePair<uint, float>> TopColliders(List<KeyValuePair<uint, float>> scored, int cap)
        {
            var sorted = new List<KeyValuePair<uint, float>>(scored);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (sorted.Count > cap)
                sorted.RemoveRange(cap, sorted.Count - cap);
            return sorted;
        }

        /// <summary>Record that subscribed prim <paramref name="prim"/> touches <paramref name="collider"/> this frame.</summary>
        internal void AddCollider(uint prim, uint collider, ContactPoint contact)
        {
            _fresh.Add(prim);
            Hold(prim, collider, contact);
        }

        /// <summary>Keep a contact of last frame that the engine did not report this frame because neither body is awake:
        /// the two still touch.</summary>
        internal void Hold(uint prim, uint collider, ContactPoint contact)
        {
            if (!_accum.TryGetValue(prim, out CollisionEventUpdate u))
            {
                u = new CollisionEventUpdate();
                _accum[prim] = u;
            }
            u.AddCollider(collider, contact);
        }

        /// <summary>Keep the whole of last frame's set for <paramref name="prim"/>, which no contact report named this frame
        /// and every contact of which is held: the same object, so a sleeping prim costs no new set each frame.</summary>
        internal void HoldAll(uint prim, CollisionEventUpdate lastSet) => _accum[prim] = lastSet;

        /// <summary>Whether this frame's set for <paramref name="prim"/> already names <paramref name="collider"/>.</summary>
        internal bool Touches(uint prim, uint collider)
            => _accum.TryGetValue(prim, out CollisionEventUpdate u) && u.m_objCollisionList.ContainsKey(collider);

        /// <summary>
        /// Whether this frame's set goes to the prim. A set with a contact the engine reported this frame always does. A set
        /// made only of held contacts (everything it touches is asleep) goes only when it differs from last frame's, so the
        /// scene ends what has gone, or when it is the ground alone. Second Life documents that a resting object "does not
        /// continuously trigger collisions but for a few times, unless there is movement" (the collision event's wiki page),
        /// so nothing more is sent for objects at rest; the wiki says nothing of land_collision at rest, and ubODE keeps
        /// sending a sleeping prim's contacts (ODEPrim.SendCollisions does not clear them while the body is disabled), so
        /// the ground alone keeps being sent.
        /// </summary>
        internal bool ShouldDeliver(uint prim, CollisionEventUpdate set)
        {
            if (_fresh.Contains(prim) || !_previous.TryGetValue(prim, out CollisionEventUpdate last))
                return true;
            if (ReferenceEquals(set, last))   // all of last frame's set held as it was
                return set.Count == 1 && set.m_objCollisionList.ContainsKey(0);
            Dictionary<uint, ContactPoint> now = set.m_objCollisionList;
            if (now.Count == 1 && now.ContainsKey(0))
                return true;
            if (now.Count != last.m_objCollisionList.Count)
                return true;
            foreach (uint id in now.Keys)
                if (!last.m_objCollisionList.ContainsKey(id))
                    return true;
            return false;
        }

        /// <summary>
        /// Close the frame. Returns the prims that collided last frame and not this one - each gets one empty update
        /// (collision_end) - and rolls the last-frame set forward. The list is reused; read it before the next call.
        /// </summary>
        internal List<uint> EndFrame(bool contactsOverflowed)
        {
            _ended.Clear();

            // The contact buffer overflowed, so absence this frame proves nothing - the contact may
            // simply not have fit. End nobody (no false collision_end, and no false collision_start next frame
            // when the contact reappears); keep last frame's prims tracked alongside this frame's.
            if (contactsOverflowed)
            {
                foreach (uint id in _accum.Keys)
                    _collidedLastFrame.Add(id);
                return _ended;
            }

            foreach (uint id in _collidedLastFrame)
                if (!_accum.ContainsKey(id))
                {
                    _ended.Add(id);
                    _lastSent.Remove(id);
                }
            _collidedLastFrame.Clear();
            foreach (uint id in _accum.Keys)
                _collidedLastFrame.Add(id);
            return _ended;
        }
    }
}
