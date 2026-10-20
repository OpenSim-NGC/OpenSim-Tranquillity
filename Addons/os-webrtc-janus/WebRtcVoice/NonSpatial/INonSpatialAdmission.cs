/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Pluggable admission.
 *
 * The engine decides nothing about who may start, invite or join. It asks an admission policy, one
 * per session type, exactly as the A2A path keeps its decision in A2AProvisionAdmission.Decide
 * rather than in the registry (A2ASessionRegistry.cs, "This class makes NO authorization
 * decision itself"), which A2ACalls has replaced. Keeping the same separation is what lets the A2A calls
 * run on this engine by supplying an admission that defers to the existing predicate, with no change to
 * the engine.
 *
 * The verdict carries a DECISION WORD, matching the house instrument convention
 * (A2AProvisionAdmission.cs) so one greppable line per decision stays possible.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public readonly struct AdmissionVerdict
    {
        public bool Admitted { get; }
        public string Decision { get; }

        private AdmissionVerdict(bool admitted, string decision)
        {
            Admitted = admitted;
            Decision = decision;
        }

        public static AdmissionVerdict Allow(string decision = "admitted") => new AdmissionVerdict(true, decision);
        public static AdmissionVerdict Deny(string decision) => new AdmissionVerdict(false, decision);

        public const string NotAParty = "refused-not-party";
        public const string NotAMember = "refused-not-a-member";
        public const string NoPower = "refused-no-power";
        public const string BadToken = "refused-bad-token";
        public const string WrongType = "refused-wrong-type";
    }

    /// <summary>One policy per <see cref="NonSpatialSessionType"/>. Pure: no Scene, no HTTP, no clock.</summary>
    public interface INonSpatialAdmission
    {
        NonSpatialSessionType Type { get; }

        /// <summary>May <paramref name="creator"/> open this non-spatial voice session at all?</summary>
        AdmissionVerdict CanStart(UUID creator, UUID owner, IReadOnlyList<UUID> invitees);

        /// <summary>May <paramref name="inviter"/> invite <paramref name="invitee"/> to a live session?</summary>
        AdmissionVerdict CanInvite(NonSpatialVoiceSession session, UUID inviter, UUID invitee);

        /// <summary>May <paramref name="agent"/> take a seat? The cap is the engine's, not the policy's.</summary>
        AdmissionVerdict CanJoin(NonSpatialVoiceSession session, UUID agent);
    }

    /// <summary>
    /// P2P: both parties are fixed at start and nobody else may ever join. Mirrors the live rule --
    /// admission is party membership (A2AProvisionAdmission.cs) -- without touching it.
    /// </summary>
    public sealed class P2PAdmission : INonSpatialAdmission
    {
        public NonSpatialSessionType Type => NonSpatialSessionType.P2P;

        public AdmissionVerdict CanStart(UUID creator, UUID owner, IReadOnlyList<UUID> invitees)
            => invitees != null && invitees.Count == 1 && invitees[0] != UUID.Zero && invitees[0] != creator
                ? AdmissionVerdict.Allow("p2p-admitted")
                : AdmissionVerdict.Deny(AdmissionVerdict.WrongType);

        public AdmissionVerdict CanInvite(NonSpatialVoiceSession session, UUID inviter, UUID invitee)
            => AdmissionVerdict.Deny(AdmissionVerdict.WrongType);   // a P2P session is never widened

        public AdmissionVerdict CanJoin(NonSpatialVoiceSession session, UUID agent)
            => session != null && session.Find(agent) != null
                ? AdmissionVerdict.Allow("p2p-party")
                : AdmissionVerdict.Deny(AdmissionVerdict.NotAParty);
    }

    /// <summary>
    /// ADHOC: the creator opens it; anyone already in it may invite; only someone invited may take a
    /// seat. That last rule is what keeps a conference room from being joinable by anyone who learns
    /// its room key.
    /// </summary>
    public sealed class AdhocAdmission : INonSpatialAdmission
    {
        public NonSpatialSessionType Type => NonSpatialSessionType.Adhoc;

        public AdmissionVerdict CanStart(UUID creator, UUID owner, IReadOnlyList<UUID> invitees)
            => creator != UUID.Zero ? AdmissionVerdict.Allow("adhoc-admitted") : AdmissionVerdict.Deny(AdmissionVerdict.NotAParty);

        public AdmissionVerdict CanInvite(NonSpatialVoiceSession session, UUID inviter, UUID invitee)
        {
            if (session == null || invitee == UUID.Zero) return AdmissionVerdict.Deny(AdmissionVerdict.NotAParty);
            if (inviter == session.Creator) return AdmissionVerdict.Allow("adhoc-creator");
            NonSpatialMember m = session.Find(inviter);
            return m != null && m.HoldsSeat
                ? AdmissionVerdict.Allow("adhoc-member")
                : AdmissionVerdict.Deny(AdmissionVerdict.NotAMember);
        }

        public AdmissionVerdict CanJoin(NonSpatialVoiceSession session, UUID agent)
        {
            NonSpatialMember m = session?.Find(agent);
            return m != null ? AdmissionVerdict.Allow("adhoc-invited") : AdmissionVerdict.Deny(AdmissionVerdict.NotAMember);
        }
    }

    /// <summary>
    /// GROUP: membership of the group IS the invitation, so there is no invite step to police. The
    /// two questions are whether the agent is in the group and whether it holds GP_SESSION_JOIN
    /// (roles_constants.h:144, 0x1 &lt;&lt; 16) -- the "can join session" power the viewer itself gates
    /// the group-chat button on (fsfloatercontacts.cpp:255). Both are asked through injected
    /// delegates so this stays pure and testable; the region module supplies the real group service.
    /// </summary>
    public sealed class GroupAdmission : INonSpatialAdmission
    {
        private readonly Func<UUID, UUID, bool> _isMember;      // (agent, group)
        private readonly Func<UUID, UUID, bool> _hasJoinPower;  // (agent, group)

        public GroupAdmission(Func<UUID, UUID, bool> isMember, Func<UUID, UUID, bool> hasJoinPower)
        {
            _isMember = isMember ?? throw new ArgumentNullException(nameof(isMember));
            _hasJoinPower = hasJoinPower ?? throw new ArgumentNullException(nameof(hasJoinPower));
        }

        public NonSpatialSessionType Type => NonSpatialSessionType.Group;

        public AdmissionVerdict CanStart(UUID creator, UUID owner, IReadOnlyList<UUID> invitees)
        {
            if (owner == UUID.Zero) return AdmissionVerdict.Deny(AdmissionVerdict.WrongType);
            if (!_isMember(creator, owner)) return AdmissionVerdict.Deny(AdmissionVerdict.NotAMember);
            return _hasJoinPower(creator, owner)
                ? AdmissionVerdict.Allow("group-admitted")
                : AdmissionVerdict.Deny(AdmissionVerdict.NoPower);
        }

        /// <summary>A group session is not invited into; the roster is the membership.</summary>
        public AdmissionVerdict CanInvite(NonSpatialVoiceSession session, UUID inviter, UUID invitee)
            => AdmissionVerdict.Deny(AdmissionVerdict.WrongType);

        public AdmissionVerdict CanJoin(NonSpatialVoiceSession session, UUID agent)
        {
            if (session == null) return AdmissionVerdict.Deny(AdmissionVerdict.NotAMember);
            if (!_isMember(agent, session.Owner)) return AdmissionVerdict.Deny(AdmissionVerdict.NotAMember);
            return _hasJoinPower(agent, session.Owner)
                ? AdmissionVerdict.Allow("group-member")
                : AdmissionVerdict.Deny(AdmissionVerdict.NoPower);
        }
    }
}
