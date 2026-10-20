/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.Framework.Scenes
{
    /// <summary>
    /// What a physical object's scripts asked of its physics engine and that goes on in the next region: the move-to target
    /// (llMoveToTarget), the hover (llSetHoverHeight, llGroundRepel) and the look-at (llRotLookAt). Core fills it as it starts
    /// to cross the object, for an actor that asks for it (<see cref="PhysicsActor.CarriesTargetsAcrossCrossing"/>), and the
    /// saved object carries it as optional elements of the root part, written only when set. A simulator that does not know
    /// them skips them. The neighbour hands the targets to the new actor through the usual setters and clears the record
    /// (<see cref="SceneObjectGroup.TakeUpCrossingTargets"/>); a crossing that fails clears it as well.
    ///
    /// The move-to target is kept as a point in the next region's coordinates: the crossing moves it by the same shift as the
    /// object (where it arrives less where it left), so it names the same place in the world, whatever the size or the place
    /// of the two regions. A hover is a height above ground or water, and a
    /// look-at is a rotation of the world, so neither changes at the border.
    ///
    /// A mouse drag in progress (click-and-drag of a physical object) is kept as the point it steers to, moved like the
    /// move-to target, the grabbed point's offset from the root and the agent that holds it.
    /// </summary>
    public sealed class CrossingTargets
    {
        public Vector3? MoveTarget;
        public float? MoveTau;

        public float? HoverHeight;
        public int? HoverType;
        public float? HoverTau;
        public bool? HoverRepel;

        public Quaternion? LookTarget;
        public float? LookStrength;
        public float? LookDamping;

        public UUID? DragAgent;
        public Vector3? DragAnchor;
        public Vector3? DragTarget;

        public bool HasMove => MoveTarget.HasValue && MoveTau.HasValue;
        public bool HasHover => HoverHeight.HasValue && HoverType.HasValue && HoverTau.HasValue;
        public bool HasLook => LookTarget.HasValue && LookStrength.HasValue && LookDamping.HasValue;
        public bool HasDrag => DragAgent.HasValue && DragAnchor.HasValue && DragTarget.HasValue;

        /// <summary>True when nothing is set, so nothing is written.</summary>
        public bool IsEmpty => !HasMove && !HasHover && !HasLook && !HasDrag;
    }
}
