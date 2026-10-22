/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// The numbers of the standing-rider rules, in one place.
/// </summary>
public static class RideOnLimits
{
    /// <summary>How long an avatar's feet must touch the same object before it counts as standing on it (ms).</summary>
    public const int StartMs = 250;

    /// <summary>How long an avatar may be off the object it stands on before the link ends (ms). Covers a jump or hop.</summary>
    public const int BreakMs = 1000;

    /// <summary>
    /// A stretch of at least this long between two reports is silence, not evidence: an engine stops reporting for an
    /// avatar whose body is asleep, and that says nothing about whether the avatar still stands on the object (ms).
    /// </summary>
    public const int ReportGapMs = BreakMs;

    /// <summary>How far outside the object's horizontal bounds a standing avatar may be and still be carried (m).</summary>
    public const float PositionGate = 1.0f;

    /// <summary>
    /// How long an avatar that arrived carried by an object may go without being linked to it, counted over contact
    /// reports, before it is an ordinary avatar again and the engine may pull it inside the region (ms).
    /// </summary>
    public const int CarriedGraceMs = 3000;

    /// <summary>How long a region that received a crossing object waits for its riders before it runs it (ms).</summary>
    public const int ArriveHoldMs = 10000;

    /// <summary>
    /// The longest time an object that waited for its riders is moved on by when its hold ends, for an engine that does
    /// that (PhysicsActor.ReleaseHold(float)): longer than this risks carrying it into what stands in its way (ms).
    /// </summary>
    public const int ResumeInterpolationMaxMs = 3000;

    /// <summary>How long an object that crossed in with riders is kept from crossing out again, for an engine that asks for it (ms).</summary>
    public const int RecrossWaitMs = 3000;

    /// <summary>
    /// The speed (m/s) above which the object an avatar stands on counts as moving: it crosses a border with its riders, so the
    /// riders wait for it there instead of crossing on their own.
    /// </summary>
    public const float MovingObjectSpeed = 0.1f;

    /// <summary>
    /// How long after that wait a region that sent a crossing object keeps its frozen copy while it has still not heard that
    /// the crossing is complete, before it takes the report for lost and lets go of the copy (ms).
    /// </summary>
    public const int PendingGraceMs = 60000;
}

/// <summary>
/// The soft link of a standing avatar to the object it stands on: not a physical or a scene-graph link, only a record
/// that the avatar has been standing on that object, so the object's crossing can take the avatar with it.
/// </summary>
public sealed class SoftLink
{
    private UUID m_candidate = UUID.Zero;
    private double m_candidateSince;
    private double m_lastReport;

    // time covered by reports that did not name the object, since the last report that did
    private double m_absentMs;

    /// <summary>The object the avatar is linked to, or <see cref="UUID.Zero"/>.</summary>
    public UUID GroupID { get; private set; } = UUID.Zero;
    public bool IsLinked => GroupID.IsNotZero();

    /// <summary>
    /// One report of the avatar's feet. The link starts when the feet have touched the same object in every report for
    /// <see cref="RideOnLimits.StartMs"/>, and ends when they have touched it in none for <see cref="RideOnLimits.BreakMs"/>
    /// (a jump or a gap in the engine's reports does not end it), or at once when the avatar cannot be standing.
    /// </summary>
    /// <param name="nowMs">time of the report, milliseconds</param>
    /// <param name="contactGroup">the object the lowest feet contact names, or <see cref="UUID.Zero"/> for none or for ground</param>
    /// <param name="canStand">false while flying, sitting, in transit, a child agent or an NPC</param>
    public void Update(double nowMs, UUID contactGroup, bool canStand)
    {
        if (!canStand)
        {
            Clear();
            return;
        }

        // only the time that reports cover counts towards the break time; a long silence before this report is
        // no information about the object
        double sinceLastReport = nowMs - m_lastReport;
        m_lastReport = nowMs;
        bool silence = sinceLastReport >= RideOnLimits.ReportGapMs;

        if (contactGroup.IsZero())
        {
            m_candidate = UUID.Zero;
            if (IsLinked)
            {
                if (!silence)
                    m_absentMs += sinceLastReport;
                if (m_absentMs >= RideOnLimits.BreakMs)
                    Clear();
            }
            return;
        }

        if (contactGroup.Equals(GroupID))
        {
            m_candidate = UUID.Zero;
            m_absentMs = 0;
            return;
        }

        if (!contactGroup.Equals(m_candidate))
        {
            m_candidate = contactGroup;
            m_candidateSince = nowMs;
        }

        if (nowMs - m_candidateSince >= RideOnLimits.StartMs)
        {
            GroupID = contactGroup;
            m_absentMs = 0;
            m_candidate = UUID.Zero;
        }
        else if (IsLinked)
        {
            // standing on something else for a moment, the old object not named for the break time
            if (!silence)
                m_absentMs += sinceLastReport;
            if (m_absentMs >= RideOnLimits.BreakMs)
                Clear();
        }
    }

    public void Clear()
    {
        GroupID = UUID.Zero;
        m_candidate = UUID.Zero;
        m_absentMs = 0;
    }
}
