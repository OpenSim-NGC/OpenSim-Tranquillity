/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes;

public partial class Scene
{
    /// <summary>How long a released hold is remembered, so that a late recall of it is refused (ms).</summary>
    private const int ReleasedHoldMemoryMs = 60000;

    /// <summary>
    /// An object that arrived together with avatars, kept at rest here (scripts and keyframed motion not started)
    /// until they have all arrived or the time ran out.
    /// </summary>
    private sealed class ArrivalHold
    {
        public SceneObjectGroup Group;
        public GridRegion Source;
        public HashSet<UUID> Waiting;
        public double DeadlineMs;
        public bool AnyArrived;
        public List<SceneObjectGroup> DeferredAttachments = new();
        public bool PhysicsHeld;
        public bool BorderKept;   // the engine was told to keep the object inside the border, and has not been told the wait is over
        public double StartedMs;
        public bool Released;
        public double ReleasedAtMs;
    }

    private readonly object m_crossingHoldLock = new();
    private readonly Dictionary<UUID, ArrivalHold> m_arrivalHolds = new();
    private readonly Dictionary<UUID, SceneObjectGroup> m_pendingCrossings = new();
    private readonly Dictionary<UUID, double> m_pendingSince = new();

    /// <summary>The clock (ms) the arrival hold of a crossing object is measured on; a test can replace it.</summary>
    public Func<double> CrossingClock { get; set; } = Util.GetTimeStampMS;

    /// <summary>
    /// Called with a short label at each step of an object's crossing from the freeze to the end, so that a test can
    /// make the step fail. Null in a running region.
    /// </summary>
    public Action<SceneObjectGroup, string> CrossingStepHook { get; set; }

    /// <summary>True while an object that arrived with riders is held here, waiting for them.</summary>
    public bool HoldsArrival(UUID objectID)
    {
        lock (m_crossingHoldLock)
            return m_arrivalHolds.TryGetValue(objectID, out ArrivalHold hold) && !hold.Released;
    }

    /// <summary>True while this region keeps the frozen original of an object it sent, until the other region reports it complete.</summary>
    public bool HasPendingCrossing(UUID objectID)
    {
        lock (m_crossingHoldLock)
            return m_pendingCrossings.ContainsKey(objectID);
    }

    /// <summary>
    /// Takes in an object that crossed with riders, held. With no riders listed it is taken in as any crossing object.
    /// </summary>
    public bool IncomingCreateObject(Vector3 newPosition, ISceneObject sog, CrossingHoldRequest hold)
    {
        if (hold is null || hold.ExpectedRiders.Count == 0 || sog is not SceneObjectGroup newObject)
            return IncomingCreateObject(newPosition, sog);

        // A copy of this object that an earlier try left here, with no rider arrived, is the same object: the new
        // arrival replaces it, so that a retried crossing never meets its own leftover.
        if (HoldsArrival(newObject.UUID))
            RecallHeldObject(newObject.UUID);

        newObject.HeldForRiders = true;
        if (!IncomingCreateObject(newPosition, sog))
        {
            newObject.HeldForRiders = false;
            return false;
        }

        // A physical object is held still by its engine, not left to fall or drift while the riders arrive
        bool physicsHeld = false;
        try
        {
            if (newObject.UsesPhysics && newObject.RootPart.PhysActor is PhysicsActor actor)
            {
                actor.HoldStill();
                physicsHeld = true;
            }
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem holding the arrived object {newObject.UUID} still");
        }

        // An engine that keeps the object from crossing out again for a while is also told to keep it inside the border
        // for that time: left to cross out, its body would be taken out of the simulation under the riders.
        bool borderKept = false;
        try
        {
            if (newObject.UsesPhysics && newObject.RootPart.PhysActor is PhysicsActor waiting && waiting.WaitsBeforeCrossingBack)
            {
                waiting.KeepInsideBorder(true);
                borderKept = true;
            }
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem keeping the arrived object {newObject.UUID} inside the border");
        }

        double now = CrossingClock();
        newObject.ArrivedWithRidersAtMs = now;
        lock (m_crossingHoldLock)
        {
            m_arrivalHolds[newObject.UUID] = new ArrivalHold
            {
                Group = newObject,
                Source = hold.Source,
                Waiting = new HashSet<UUID>(hold.ExpectedRiders),
                StartedMs = now,
                DeadlineMs = now + RideOnLimits.ArriveHoldMs,
                PhysicsHeld = physicsHeld,
                BorderKept = borderKept
            };
        }
        return true;
    }

    /// <summary>
    /// The sending region asks to give a held object back. It is discarded, unstarted, unless a rider has arrived or
    /// it was released; an object that is not here (never was, or was recalled before) counts as given back.
    /// </summary>
    public bool RecallHeldObject(UUID objectID)
    {
        ArrivalHold hold;
        lock (m_crossingHoldLock)
        {
            if (!m_arrivalHolds.TryGetValue(objectID, out hold))
                return true;
            if (hold.Released || hold.AnyArrived)
                return false;
            m_arrivalHolds.Remove(objectID);
        }

        try
        {
            hold.Group.HeldForRiders = false;
            DeleteSceneObject(hold.Group, false, true);
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem discarding the recalled object {objectID}");
        }
        return true;
    }

    /// <summary>The receiving region reports the crossing of an object this region sent as complete: this region lets go of it.</summary>
    public void CrossingCompleted(UUID objectID, IList<UUID> missingRiders)
    {
        SceneObjectGroup group;
        lock (m_crossingHoldLock)
        {
            if (!m_pendingCrossings.Remove(objectID, out group))
                return;
            m_pendingSince.Remove(objectID);
        }

        try
        {
            DeleteSceneObject(group, true, true);
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem removing the object {objectID} after its crossing completed");
        }
    }

    /// <summary>This region now keeps the frozen original of an object it sent, until the other region reports it complete.</summary>
    internal void AddPendingCrossing(SceneObjectGroup group)
    {
        lock (m_crossingHoldLock)
        {
            m_pendingCrossings[group.UUID] = group;
            m_pendingSince[group.UUID] = CrossingClock();
        }
    }

    internal bool RemovePendingCrossing(UUID objectID)
    {
        lock (m_crossingHoldLock)
        {
            m_pendingSince.Remove(objectID);
            return m_pendingCrossings.Remove(objectID);
        }
    }

    /// <summary>
    /// Lets go of the frozen copy of an object whose crossing was never reported complete: the other region's hold ended
    /// long ago, so the report was lost (a region on another simulator can be unreachable at that moment).
    /// </summary>
    private void ReleaseLostPendingCrossings()
    {
        List<SceneObjectGroup> lost = null;
        lock (m_crossingHoldLock)
        {
            if (m_pendingCrossings.Count == 0)
                return;

            double now = CrossingClock();
            List<UUID> ids = null;
            foreach (KeyValuePair<UUID, double> entry in m_pendingSince)
            {
                if (now - entry.Value >= RideOnLimits.ArriveHoldMs + RideOnLimits.PendingGraceMs)
                    (ids ??= new List<UUID>()).Add(entry.Key);
            }

            if (ids is null)
                return;

            lost = new List<SceneObjectGroup>();
            foreach (UUID id in ids)
            {
                m_pendingSince.Remove(id);
                if (m_pendingCrossings.Remove(id, out SceneObjectGroup group))
                    lost.Add(group);
            }
        }

        foreach (SceneObjectGroup group in lost)
        {
            m_log.LogWarning($"[SCENE]: The crossing of object {group.UUID} was never reported complete; letting go of the copy kept here");
            try
            {
                DeleteSceneObject(group, true, true);
            }
            catch (Exception e)
            {
                m_log.LogError(e, $"[SCENE]: Problem removing the object {group.UUID} whose crossing was never reported complete");
            }
        }
    }

    /// <summary>Releases the held arrivals whose time has run out, and the copies kept for crossings never reported complete.</summary>
    public void ReleaseExpiredCrossingHolds()
    {
        ReleaseLostPendingCrossings();

        List<ArrivalHold> due = null;
        List<ArrivalHold> waitOver = null;
        lock (m_crossingHoldLock)
        {
            if (m_arrivalHolds.Count == 0)
                return;

            double now = CrossingClock();
            List<UUID> forgotten = null;
            foreach (KeyValuePair<UUID, ArrivalHold> entry in m_arrivalHolds)
            {
                ArrivalHold hold = entry.Value;
                if (hold.BorderKept && now - hold.StartedMs >= RideOnLimits.RecrossWaitMs)
                {
                    hold.BorderKept = false;
                    (waitOver ??= new List<ArrivalHold>()).Add(hold);
                }

                if (hold.Released)
                {
                    if (now - hold.ReleasedAtMs >= ReleasedHoldMemoryMs)
                        (forgotten ??= new List<UUID>()).Add(entry.Key);
                }
                else if (now >= hold.DeadlineMs)
                    (due ??= new List<ArrivalHold>()).Add(hold);
            }

            if (forgotten is not null)
            {
                foreach (UUID id in forgotten)
                    m_arrivalHolds.Remove(id);
            }
        }

        if (waitOver is not null)
        {
            foreach (ArrivalHold hold in waitOver)
                EndBorderKeeping(hold);
        }

        if (due is not null)
        {
            foreach (ArrivalHold hold in due)
                ReleaseArrivalHold(hold);
        }
    }

    /// <summary>The object's wait before crossing out again is over: its engine may take it across the border.</summary>
    private void EndBorderKeeping(ArrivalHold hold)
    {
        try
        {
            if (!hold.Group.IsDeleted)
                hold.Group.RootPart.PhysActor?.KeepInsideBorder(false);
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem letting the object {hold.Group.UUID} cross the border after its wait");
        }
    }

    /// <summary>An avatar has completed its arrival in this region.</summary>
    internal void NoteArrival(UUID agentId)
    {
        List<ArrivalHold> complete = null;
        lock (m_crossingHoldLock)
        {
            if (m_arrivalHolds.Count == 0)
                return;

            foreach (ArrivalHold hold in m_arrivalHolds.Values)
            {
                if (hold.Released || !hold.Waiting.Remove(agentId))
                    continue;

                hold.AnyArrived = true;
                if (hold.Waiting.Count == 0)
                    (complete ??= new List<ArrivalHold>()).Add(hold);
            }
        }

        if (complete is not null)
        {
            foreach (ArrivalHold hold in complete)
                ReleaseArrivalHold(hold);
        }
    }

    /// <summary>
    /// An avatar that an object here is held for is arriving with attachments. Their scripts are not to start at its own
    /// arrival but with the object, when the hold ends: they are noted and true is returned. False when no object
    /// waits for the avatar (the scripts start at once).
    /// </summary>
    internal bool DeferAttachmentScripts(UUID agentId, IEnumerable<SceneObjectGroup> attachments)
    {
        lock (m_crossingHoldLock)
        {
            foreach (ArrivalHold hold in m_arrivalHolds.Values)
            {
                if (hold.Released || !hold.Waiting.Contains(agentId))
                    continue;

                hold.DeferredAttachments.AddRange(attachments);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Ends the engine's hold on an object that waited for its riders. The engine is told how long the object waited, at
    /// most <see cref="RideOnLimits.ResumeInterpolationMaxMs"/>, and one that moves the object on by that time
    /// (Halcyon: PhysxPrim.cs DoResumeInterpolation) takes the riders already here along by the same distance.
    /// </summary>
    private void ReleasePhysicsHold(ArrivalHold hold)
    {
        PhysicsActor actor = hold.Group.RootPart.PhysActor;
        if (actor is null)
            return;

        double waitedMs = Math.Clamp(hold.ReleasedAtMs - hold.StartedMs, 0, RideOnLimits.ResumeInterpolationMaxMs);
        Vector3 before = actor.Position;
        actor.ReleaseHold((float)(waitedMs / 1000.0));
        Vector3 moved = actor.Position - before;
        if (moved == Vector3.Zero)
            return;

        UUID groupId = hold.Group.UUID;
        foreach (ScenePresence rider in GetScenePresences())
        {
            if (!rider.IsChildAgent && rider.CarriedByGroup.Equals(groupId))
                rider.AbsolutePosition += moved;
        }
    }

    /// <summary>
    /// Lets a held object run: its keyframed motion starts and its scripts resume, then the sending region is told
    /// the crossing is complete, with the riders that did not arrive.
    /// </summary>
    private void ReleaseArrivalHold(ArrivalHold hold)
    {
        List<UUID> missing;
        List<SceneObjectGroup> deferred;
        lock (m_crossingHoldLock)
        {
            if (hold.Released)
                return;
            hold.Released = true;
            hold.ReleasedAtMs = CrossingClock();
            missing = new List<UUID>(hold.Waiting);
            deferred = new List<SceneObjectGroup>(hold.DeferredAttachments);
            hold.DeferredAttachments.Clear();
        }

        SceneObjectGroup group = hold.Group;
        try
        {
            if (!group.IsDeleted)
            {
                group.HeldForRiders = false;
                if (hold.PhysicsHeld)
                    ReleasePhysicsHold(hold);
                foreach (SceneObjectPart part in group.Parts)
                    part.KeyframeMotion?.ReleaseHold();
                group.ResumeScripts();
            }
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem starting the arrived object {group.UUID}");
        }

        // the scripts in the attachments of the avatars that arrived start together with the object
        foreach (SceneObjectGroup attachment in deferred)
        {
            try
            {
                if (!attachment.IsDeleted)
                    attachment.ResumeScripts();
            }
            catch (Exception e)
            {
                m_log.LogError(e, $"[SCENE]: Problem starting the scripts of an attachment that arrived with {group.UUID}");
            }
        }

        try
        {
            SimulationService?.CrossingCompleted(hold.Source, group.UUID, missing);
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem reporting the crossing of {group.UUID} complete");
        }
    }
}
