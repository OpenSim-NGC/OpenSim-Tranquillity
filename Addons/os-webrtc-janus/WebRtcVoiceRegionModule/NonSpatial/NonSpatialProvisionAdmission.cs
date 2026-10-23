/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Admission for a GROUP provision.
 *
 * A group voice provision is indistinguishable from an A2A one at the wire: the viewer's group
 * channel goes through setNonSpatialChannel -> startAdHocSession (llvoicewebrtc.h:153-156), which
 * posts channel_type "multiagent" with `channel` set to whatever channel_uri it was handed
 * (llvoicewebrtc.cpp, startAdHocSession and the multiagent provision body). The discriminator is therefore the CHANNEL STRING, and that is
 * precisely what NonSpatialRoomKey's prefix was built to provide: a group room key is
 * "nsv1:group:<uuid>", which can never parse as a bare UUID and so can never be an A2A channel
 * (A2ACalls.TryGetByChannel is UUID.TryParse).
 *
 * THE A2A PATH IS NOT EDITED. This is a wrapper: it takes the group arm only when group voice
 * is enabled AND the channel is one of our room keys, and otherwise calls
 * A2AProvisionAdmission.Decide with the same arguments it would have received anyway. That function
 * is not changed by group voice, so every other body is decided exactly as it was.
 *
 * DEFENCE IN DEPTH. The provision re-checks membership AND powers, it does not trust the
 * token alone. In A2A the token is held by exactly two named parties; in a group it is the same
 * string for everyone who can call, so a leaked token must not be sufficient. Order of refusal is
 * deliberate: no-session, then not-a-member, then no-power, then bad-token, then capacity -- each a
 * different operator problem and each its own greppable word.
 *
 * ROOM ALLOCATION needs no code here. The room key IS the `channel`, so the existing
 * multiagent arm of JanusAudioBridge.CalcRoomNumber hashes (gridId, channel, "multiagent")
 * and produces a room number distinct from every parcel room and every A2A room. Nothing
 * in the Janus bridge changes.
 */
using System;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>The group arm's verdict, alongside the A2A one the module already applies.</summary>
    public sealed class NonSpatialProvisionResult
    {
        /// <summary>Non-null when the group arm answered; null means "not ours, use the A2A result".</summary>
        public NonSpatialVoiceSession Session { get; init; }
        public bool Admitted { get; init; }
        public string Decision { get; init; }
        /// <summary>HTTP status for a refusal. 409 for capacity, 403 otherwise.</summary>
        public int Status { get; init; }
    }

    public static class NonSpatialProvisionAdmission
    {
        public const string DecisionAdmitted = "group-provision-admitted";
        public const string DecisionNoSession = "group-refused-no-session";
        public const string DecisionNotMember = "group-refused-not-a-member";
        public const string DecisionNoPower = "group-refused-no-power";
        public const string DecisionBadToken = "group-refused-bad-token";
        public const string DecisionCapacity = "group-refused-capacity";
        public const string DecisionRemoved = "group-refused-removed";

        // The ad-hoc arm's own words. Separate from the group ones so a grep for a conference
        // refusal cannot pick up a group one, and so the two paths can never be confused in a log.
        public const string DecisionAdhocAdmitted = "adhoc-provision-admitted";
        public const string DecisionAdhocNoSession = "adhoc-refused-no-session";
        public const string DecisionAdhocNotMember = "adhoc-refused-not-invited";
        public const string DecisionAdhocBadToken = "adhoc-refused-bad-token";
        public const string DecisionAdhocCapacity = "adhoc-refused-capacity";
        public const string DecisionAdhocDisabled = "adhoc-refused-disabled";
        public const string DecisionAdhocRemoved = "adhoc-refused-removed";

        /// <summary>
        /// True when this provision body is addressed to a non-spatial room key rather than an A2A
        /// channel. Cheap and total: no registry, no group service, no allocation.
        ///
        /// This gate is type-blind -- <see cref="NonSpatialRoomKey.IsRoomKey"/> matches
        /// "nsv1:adhoc:" as readily as "nsv1:group:" -- so a conference provision arrives
        /// here too, and Decide dispatches on the type stored with the voice session. The name IsGroupProvision is kept
        /// because callers and tests use it; <see cref="IsNonSpatialProvision"/> is the accurate one to use.
        /// </summary>
        public static bool IsNonSpatialProvision(OSDMap map) => IsGroupProvision(map);

        /// <inheritdoc cref="IsNonSpatialProvision"/>
        public static bool IsGroupProvision(OSDMap map)
        {
            if (map == null || A2AProvisionAdmission.IsLogout(map)) return false;
            if (!map.TryGetString("channel_type", out string ct) || ct != A2AProvisionAdmission.ChannelTypeMultiagent) return false;
            return map.TryGetString("channel", out string ch) && NonSpatialRoomKey.IsRoomKey(ch);
        }

        /// <summary>
        /// Decide a group provision. Call only when <see cref="IsGroupProvision"/> is true; every
        /// other body belongs to <see cref="A2AProvisionAdmission.Decide"/>, untouched.
        /// </summary>
        public static NonSpatialProvisionResult Decide(OSDMap map, UUID agentID, GroupVoicePolicy policy,
                                                       INonSpatialSessions engine, UUID originRegion,
                                                       bool adhocEnabled = false)
        {
            map.TryGetString("channel", out string channel);

            if (engine == null)
                return Refuse(DecisionNoSession, 403);

            NonSpatialVoiceSession s = engine.GetByRoomKey(channel, agentID, out bool unavailable);
            if (s == null && unavailable)
                return Refuse("group-refused-" + SessionOutcome.StoreUnavailable, NonSpatialCaps.StoreUnavailableHttpStatus);

            // Dispatch on the OWN type of the voice session rather than assuming group. The room key tag
            // and the stored type must agree -- they are both derived from the same Start -- so this
            // is the voice session telling us what it is, not the wire.
            if (s != null && s.Type == NonSpatialSessionType.Adhoc)
                return DecideAdhoc(map, agentID, engine, originRegion, adhocEnabled, s);

            if (policy == null || !policy.IsUsable)
                return Refuse(DecisionNoSession, 403);
            if (s == null || s.Type != NonSpatialSessionType.Group)
                return Refuse(DecisionNoSession, 403);

            // A removal comes first: it needs no groups service and no token, and it is the answer the viewer's retry
            // loop gets every time, so it is the cheapest check there is.
            if (engine.IsRemoved(s, agentID))
                return Refuse(DecisionRemoved, 403, s);

            // Re-checked, not inherited from "call": powers can be revoked between the two, and the
            // token is shared so it cannot be the only thing standing here.
            if (!policy.IsMember(agentID, s.Owner))
                return Refuse(DecisionNotMember, 403, s);
            if (!policy.HasRequiredPowers(agentID, s.Owner))
                return Refuse(DecisionNoPower, 403, s);

            map.TryGetString("credentials", out string presented);
            if (!NonSpatialVoiceSessionEngine.TokenMatches(s, presented))
                return Refuse(DecisionBadToken, 403, s);

            // The agent took its seat at "call"; this re-affirms it and is where a reconnect or a
            // region crossing re-points the membership instead of duplicating it. A seat that is
            // gone -- swept, or released by a departure between call and provision -- is a capacity
            // refusal if the room filled meanwhile, which is the honest answer.
            SessionOutcome seat = engine.Accept(s.SessionId, agentID, originRegion);
            if (!seat.Ok)
                return seat.Decision == SessionOutcome.Capacity
                    ? Refuse(DecisionCapacity, NonSpatialCaps.CapacityHttpStatus, s)
                    : Refuse(seat.Decision == SessionOutcome.Removed ? DecisionRemoved : "group-refused-" + seat.Decision, NonSpatialCaps.RefusalStatus(seat.Decision), s);

            return new NonSpatialProvisionResult { Session = s, Admitted = true, Decision = DecisionAdmitted, Status = 200 };
        }

        /// <summary>
        /// Admission for an AD-HOC CONFERENCE provision.
        ///
        /// Same defence in depth as the group arm, with the membership authority swapped: a
        /// conference has no roster and no powers, so the authority is the invitation list itself --
        /// exactly what AdhocAdmission.CanJoin reads, and exactly what AdhocVoiceInvite.Targets rang.
        /// A leaked token is still not sufficient, because an agent who was never invited has no
        /// member record and is refused before the token is even looked at.
        ///
        /// Order of refusal is deliberate and mirrors the group arm: disabled, no-session,
        /// not-invited, bad-token, capacity -- each a different operator problem, each its own
        /// greppable word.
        /// </summary>
        private static NonSpatialProvisionResult DecideAdhoc(OSDMap map, UUID agentID,
                                                             INonSpatialSessions engine, UUID originRegion,
                                                             bool adhocEnabled, NonSpatialVoiceSession s)
        {
            if (!adhocEnabled)
                return Refuse(DecisionAdhocDisabled, 403, s);

            // A member who was removed has no seat, but is still in the record as departed, so removal is asked first.
            if (engine.IsRemoved(s, agentID))
                return Refuse(DecisionAdhocRemoved, 403, s);

            if (s.Find(agentID) is null)
                return Refuse(DecisionAdhocNotMember, 403, s);

            map.TryGetString("credentials", out string presented);
            if (!NonSpatialVoiceSessionEngine.TokenMatches(s, presented))
                return Refuse(DecisionAdhocBadToken, 403, s);

            // Re-affirms the seat taken at "call" or "accept invitation", and is where a reconnect or
            // a region crossing re-points the membership rather than duplicating it.
            SessionOutcome seat = engine.Accept(s.SessionId, agentID, originRegion);
            if (!seat.Ok)
                return seat.Decision == SessionOutcome.Capacity
                    ? Refuse(DecisionAdhocCapacity, NonSpatialCaps.CapacityHttpStatus, s)
                    : Refuse(seat.Decision == SessionOutcome.Removed ? DecisionAdhocRemoved : "adhoc-refused-" + seat.Decision, NonSpatialCaps.RefusalStatus(seat.Decision), s);

            return new NonSpatialProvisionResult { Session = s, Admitted = true, Decision = DecisionAdhocAdmitted, Status = 200 };
        }

        private static NonSpatialProvisionResult Refuse(string decision, int status, NonSpatialVoiceSession s = null)
            => new NonSpatialProvisionResult { Session = s, Admitted = false, Decision = decision, Status = status };

        /// <summary>
        /// One greppable line per group provision decision, mirroring the A2A instrument
        /// (WebRtcVoiceRegionModule.cs). The token is never logged, only whether one was sent.
        /// </summary>
        public static string Line(UUID agentID, string regionName, string channel, bool credsPresent, string decision)
            => $"{GroupVoiceChatSession.InstrumentTag} [PROVISION] agent={agentID} region=\"{regionName}\" " +
               $"channel={channel} credentials={(credsPresent ? "present" : "absent")} decision={decision}";
    }
}
