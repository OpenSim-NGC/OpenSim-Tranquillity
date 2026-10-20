/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The ways the simulator hands a saved vehicle back to the physics engine, replayed at the PhysicsActor surface.
//
// The record. A script's llSetVehicleType, llSetVehicle*Param and llSetVehicleFlags / llRemoveVehicleFlags reach
// SceneObjectPart.SetVehicleType / SetVehicleFloatParam / SetVehicleVectorParam / SetVehicleRotationParam /
// SetVehicleFlags. Each updates the part's SOPVehicle (its VehicleData) and forwards the same call to the root's actor.
// That SOPVehicle is what the simulator keeps: SOPVehicle.ToXml2 inside the object's XML in inventory, in a region
// crossing and in an attachment, and as the Vehicle column of the region's database.
//
// The routes back. Each ends in SceneObjectPart.AddToPhysics, which builds a new actor and, for a root with a record,
// calls SOPVehicle.SetVehicle(actor), i.e. PhysicsActor.SetVehicle(VehicleData):
// - rez from inventory: the object is read from its XML and added (SceneGraph.AddSceneObject, then
//   SceneObjectGroup.AttachToScene, then ApplyPhysics);
// - region start: each object is loaded from the database (Scene.LoadPrimsFromStorage, AddRestoredSceneObject) and
//   added the same way, before the region's first physics step;
// - a copy: SceneObjectGroup.Copy clones each part (MemberwiseClone, so the copy shares the original's SOPVehicle) and
//   calls ApplyPhysics on it with building on, then turns building off on the root;
// - a region crossing: the object arrives as XML, or as such a copy within one simulator (Scene.IncomingCreateObject,
//   AddSceneObject, AddRestoredSceneObject), and is added the same way;
// - an attachment taken off and dropped: AttachmentsModule.DetachSingleAttachmentToGround clears the attachment and
//   calls ApplyPhysics (an attachment has no actor while worn);
// - physics switched off and on, by llSetStatus or the build tool (both reach SceneObjectPart.UpdatePrimFlags): a solid
//   prim keeps its actor, which is only made non-physical and physical again (DoPhysicsPropertyUpdate); no vehicle call
//   is made. A phantom prim loses its actor when physics goes off (RemoveFromPhysics) and is built anew by AddToPhysics
//   when it comes back on.

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>A way the simulator hands a saved vehicle back to the physics engine.</summary>
public enum VehicleRoute
{
    /// <summary>The vehicle stays as its script set it up.</summary>
    None,
    /// <summary>Rezzed from inventory: the record read back from the object's XML.</summary>
    Rez,
    /// <summary>Loaded with the region: the record read back from the database's Vehicle column.</summary>
    RegionStart,
    /// <summary>A copy (shift-drag, or a crossing within one simulator): the record shared with the original.</summary>
    Copy,
    /// <summary>A region crossing between simulators: the record read back from the object's XML.</summary>
    Crossing,
    /// <summary>An attachment taken off and dropped: the record as it was while worn.</summary>
    Detach,
    /// <summary>Physics switched off and on for a solid prim: the actor is kept and no vehicle call is made.</summary>
    PhysicsOffOn,
    /// <summary>Physics switched off and on for a phantom prim: the actor is removed and built anew.</summary>
    PhantomPhysicsOffOn,
}

public static class VehicleRestore
{
    /// <summary>A part keeping a vehicle record as the simulator's SceneObjectPart does, its actor
    /// <paramref name="actor"/>. Its SetVehicle* calls update the record and forward to the actor.</summary>
    public static SceneObjectPart NewRecord(PhysicsActor actor)
    {
        var part = new SceneObjectPart
        {
            Name = "vehicle",
            UUID = UUID.Random(),
            Shape = PrimitiveBaseShape.CreateBox(),
        };
        part.PhysActor = actor;
        return part;
    }

    /// <summary>The record as the route reads it back: from the object's XML (rez, crossing), from the database's Vehicle
    /// column (region start), or the part's own (copy, detach, physics off and on).</summary>
    public static SOPVehicle Saved(SceneObjectPart part, VehicleRoute route)
    {
        switch (route)
        {
            case VehicleRoute.Rez:
            {
                // An inventory item's asset: SceneObjectSerializer's original format.
                string xml = SceneObjectSerializer.ToOriginalXmlFormat(new SceneObjectGroup(Detached(part)));
                return SceneObjectSerializer.FromOriginalXmlFormat(xml).RootPart.VehicleParams;
            }
            case VehicleRoute.Crossing:
            {
                // An object sent to another simulator: the XML2 format.
                string xml = SceneObjectSerializer.ToXml2Format(new SceneObjectGroup(Detached(part)));
                return SceneObjectSerializer.FromXml2Format(xml).RootPart.VehicleParams;
            }
            case VehicleRoute.RegionStart:
                // The region's database keeps SOPVehicle.ToXml2() in its Vehicle column and reads it with FromXml2.
                return SOPVehicle.FromXml2(part.VehicleParams.ToXml2());
            default:
                return part.VehicleParams;
        }
    }

    // A copy of the part to serialize, without its actor (serializing reads the actor's live values otherwise).
    private static SceneObjectPart Detached(SceneObjectPart part)
    {
        var copy = new SceneObjectPart
        {
            Name = part.Name,
            UUID = part.UUID,
            Shape = part.Shape,
            VehicleParams = part.VehicleParams,
        };
        return copy;
    }

    /// <summary>
    /// SceneObjectPart.AddToPhysics for a root prim, at the actor: a new actor (the 9-argument AddPrimShape with phantom
    /// and shape type), its material and settings (<paramref name="build"/>, as the caller builds a prim), the vehicle
    /// record (SOPVehicle.SetVehicle, i.e. PhysicsActor.SetVehicle), the velocities replayed when
    /// <paramref name="applyDynamics"/> (a physical root's Velocity and AngularVelocity), and building turned off.
    /// </summary>
    public static PhysicsActor AddToPhysics(PhysicsScene scene, SOPVehicle saved, Vector3 position, Vector3 size, Quaternion rotation,
                                            bool physical, bool phantom, bool applyDynamics, Vector3 velocity, Vector3 angularVelocity,
                                            uint localId, Action<PhysicsActor> build)
    {
        PhysicsActor pa = scene.AddPrimShape("vehicle", PrimitiveBaseShape.CreateBox(), position, size, rotation, physical, phantom,
                                             (byte)PhysShapeType.prim, localId);
        build(pa);
        saved?.SetVehicle(pa);
        if (applyDynamics && physical)
        {
            pa.Velocity = velocity;
            pa.RotationalVelocity = angularVelocity;
        }
        pa.Building = false;
        return pa;
    }

    /// <summary>
    /// Physics switched off and on for a solid prim, at the actor (SceneObjectPart.UpdatePrimFlags with an actor:
    /// DoPhysicsPropertyUpdate). Off: SceneObjectPart.Stop (velocity and angular velocity zeroed while still physical),
    /// non-physical, delinked, volume detect off. On: physical, volume detect off.
    /// </summary>
    public static void PhysicsOff(PhysicsActor pa)
    {
        pa.Velocity = Vector3.Zero;
        pa.RotationalVelocity = Vector3.Zero;
        pa.APIDActive = false;
        pa.IsPhysical = false;
        pa.delink();
        pa.SetVolumeDetect(0);
    }

    public static void PhysicsOn(PhysicsActor pa)
    {
        pa.IsPhysical = true;
        pa.SetVolumeDetect(0);
    }

    /// <summary>
    /// Physics switched off for a phantom prim: SceneObjectPart.Stop, then RemoveFromPhysics (the scene's RemovePrim).
    /// </summary>
    public static void PhantomPhysicsOff(PhysicsScene scene, PhysicsActor pa)
    {
        pa.Velocity = Vector3.Zero;
        pa.RotationalVelocity = Vector3.Zero;
        pa.APIDActive = false;
        scene.RemovePrim(pa);
    }

    /// <summary>
    /// Physics switched on for a phantom prim with no actor: AddToPhysics with no velocities replayed, then the material
    /// again and DoPhysicsPropertyUpdate for a new actor (volume detect off).
    /// </summary>
    public static PhysicsActor PhantomPhysicsOn(PhysicsScene scene, SOPVehicle saved, Vector3 position, Vector3 size, Quaternion rotation,
                                                uint localId, Action<PhysicsActor> build)
    {
        PhysicsActor pa = AddToPhysics(scene, saved, position, size, rotation, true, true, false, Vector3.Zero, Vector3.Zero, localId, build);
        pa.SetMaterial((int)Material.Wood);
        pa.SetVolumeDetect(0);
        return pa;
    }
}
