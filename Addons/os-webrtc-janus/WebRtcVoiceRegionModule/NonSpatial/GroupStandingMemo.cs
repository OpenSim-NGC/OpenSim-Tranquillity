/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Remembers what the groups service said about an agent in a group, for the length of one pass over many members.
 * The call moderation re-check asks two questions about each member (are they in the group, and what powers do they
 * hold) for two decisions (may they stay in the call, may they moderate it); with this each is asked once.
 */

using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class GroupStandingMemo
    {
        private readonly Func<UUID, UUID, bool> _isMember;
        private readonly Func<UUID, UUID, ulong> _powers;
        private readonly Dictionary<(UUID Agent, UUID Group), bool> _members = new();
        private readonly Dictionary<(UUID Agent, UUID Group), ulong> _held = new();

        public GroupStandingMemo(Func<UUID, UUID, bool> isMember, Func<UUID, UUID, ulong> powers)
        {
            _isMember = isMember;
            _powers = powers;
        }

        public bool IsMember(UUID agent, UUID group)
        {
            if (!_members.TryGetValue((agent, group), out bool member))
                _members[(agent, group)] = member = _isMember(agent, group);
            return member;
        }

        public ulong Powers(UUID agent, UUID group)
        {
            if (!_held.TryGetValue((agent, group), out ulong held))
                _held[(agent, group)] = held = _powers(agent, group);
            return held;
        }
    }
}
