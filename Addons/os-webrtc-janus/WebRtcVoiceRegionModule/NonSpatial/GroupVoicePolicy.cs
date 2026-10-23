/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Who may be in a group voice room, and the opt-in knob.
 *
 * THE POWER IS THE GATE, NOT MEMBERSHIP. A group can have a role that may read chat but must not
 * speak, and the viewer already models that with two separate constants:
 *     GP_SESSION_JOIN  = 0x1 << 16   "can join session"   roles_constants.h:144
 *     GP_SESSION_VOICE = 0x1 << 27   "can hear/talk"      roles_constants.h:145
 * Both are required, not only GP_SESSION_JOIN:
 * requiring only JOIN would admit a member whose role has been explicitly denied voice, which is a
 * permission the grid owner has already expressed. Both live in `DefaultEveryonePowers`
 * (GroupsService.cs, `JoinChat | AllowVoiceChat`), so no ordinary group is affected; only a
 * group that has deliberately removed one of them is, and that is the point. To require JOIN
 * alone instead, RequireVoicePower flips to false and one test changes.
 *
 * OpenMetaverse's GroupPowers.JoinChat is 1<<16 and GroupPowers.AllowVoiceChat is 1<<27, the same
 * bits; the enum is asserted against the viewer's literals in the unit tests so a library change
 * cannot silently move the gate.
 *
 * ACROSS REGIONS AND HOSTS. The powers are NOT read from region-local state. IGroupsModule resolves
 * them through the groups service (local or remote connector), so the same answer is produced on any
 * region of the grid and on any host: a member's role powers are grid state, not scene state. That
 * is what lets the engine's voice session be grid-wide without the admission becoming per-region.
 * The delegates below are the seam; the module supplies the real IGroupsModule and the tests supply
 * fakes.
 */
using System;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>
    /// Everything the group arm needs, injected. Null or <see cref="Enabled"/> false means the arm is
    /// inert and every caller falls through to the untouched A2A path -- so with group voice off the
    /// chat session handler answers as it did before.
    /// </summary>
    public sealed class GroupVoicePolicy
    {
        /// <summary>The viewer's GP_SESSION_JOIN (roles_constants.h:144) / GroupPowers.JoinChat.</summary>
        public const ulong PowerJoinSession = 1UL << 16;

        /// <summary>The viewer's GP_SESSION_VOICE (roles_constants.h:145) / GroupPowers.AllowVoiceChat.</summary>
        public const ulong PowerVoice = 1UL << 27;

        /// <summary>[WebRtcVoice] GroupVoiceEnabled. DEFAULT FALSE: no group voice, exactly as today.</summary>
        public bool Enabled { get; init; }

        /// <summary>Require GP_SESSION_VOICE as well as GP_SESSION_JOIN. Default true; see the header.</summary>
        public bool RequireVoicePower { get; init; } = true;

        /// <summary>Is this agent a member of this group? Grid state, via IGroupsModule.</summary>
        public Func<UUID, UUID, bool> IsMember { get; init; }

        /// <summary>This agent's full power bits in this group. Grid state, via IGroupsModule.</summary>
        public Func<UUID, UUID, ulong> Powers { get; init; }

        /// <summary>The seat ceiling for a group room; clamped by <see cref="NonSpatialCaps.Effective"/>.</summary>
        public int Cap { get; init; } = NonSpatialCaps.DefaultConferenceCap;

        public bool IsUsable => Enabled && IsMember != null && Powers != null;

        /// <summary>The mask an agent must hold in full to be admitted.</summary>
        public ulong RequiredMask => RequireVoicePower ? (PowerJoinSession | PowerVoice) : PowerJoinSession;

        public bool HasRequiredPowers(UUID agent, UUID group)
        {
            ulong held = Powers(agent, group);
            return (held & RequiredMask) == RequiredMask;
        }

        /// <summary>
        /// The admission policy the engine takes. Membership and powers are asked separately so the
        /// refusal word can say WHICH failed -- a non-member and a member without the power are
        /// different operator problems.
        /// </summary>
        public GroupAdmission ToAdmission()
            => new GroupAdmission(IsMember, (a, g) => HasRequiredPowers(a, g));

        /// <summary>
        /// The admission when usable, else one that refuses everything. The engine needs A policy
        /// registered for the Group type or it throws; a disabled grid must get a refusal, not an
        /// exception, if anything ever reaches it.
        /// </summary>
        public GroupAdmission ToAdmissionOrNull()
            => IsUsable ? ToAdmission() : new GroupAdmission((a, g) => false, (a, g) => false);

        /// <summary>The inert policy: group voice off. What an unconfigured grid gets.</summary>
        public static GroupVoicePolicy Disabled => new GroupVoicePolicy { Enabled = false };
    }
}
