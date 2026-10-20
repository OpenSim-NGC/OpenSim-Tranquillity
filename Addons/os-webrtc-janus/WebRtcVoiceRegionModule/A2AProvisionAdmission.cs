/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Provision admission for voice: the channel-type predicate is extended for
 * "multiagent", plus recognition of the viewer's teardown body.
 *
 * Kinds, in the order the region module needs them:
 *  - Logout: {logout:true, viewer_session, voice_server_type} and NO channel_type
 *    (llvoicewebrtc.cpp:2809-2811). A guard that required a channel_type would refuse it with 403 before the
 *    service could see `logout` (the log line 'refusing provision with channel_type ""' at every teardown).
 *    It is routed to the service by viewer_session; it needs no channel authorization because it can
 *    only end the caller's own voice session (viewer_session is bound to the requesting agent).
 *  - Local: channel_type == "local" -> IsProvisionableChannelType true -> the parcel/estate checks, unchanged.
 *  - Multiagent: channel_type == "multiagent" -> admitted iff the body's `channel` (NOT channel_id)
 *    names a live registry session AND the requesting agent is one of its two parties AND `credentials`
 *    equals the session token. Any other outcome is refused 403.
 *  - Refused: anything else (absent / other channel_type).
 *
 * Pure and Scene-free so it is unit-testable; the region module applies the result.
 */
using System;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    public enum ProvisionKind
    {
        Refused = 0,
        Local = 1,
        Multiagent = 2,
        Logout = 3,
        /// <summary>
        /// A body whose channel_type is not "local" (a "multiagent" one, or none at all) while no call feature is on.
        /// Nothing in this module issues a channel then, so it is handed to the voice service unexamined, as it was
        /// before the features existed. It records nothing and has no session.
        /// </summary>
        Passthrough,
        /// <summary>
        /// A GROUP provision, admitted by the non-spatial engine. DISTINCT from
        /// <see cref="Multiagent"/> on purpose: a group admission carries no <see cref="A2ASession"/>,
        /// so anything that reads <c>Session</c> must never see this kind.
        /// </summary>
        Group,
        /// <summary>
        /// An AD-HOC CONFERENCE provision. Distinct from <see cref="Group"/> for the same
        /// reason Group is distinct from <see cref="Multiagent"/> -- it carries no
        /// <see cref="A2ASession"/>, and the kind is what the post-provision bookkeeping switches on.
        /// Sharing Group's value would also make a conference seat log itself as a group seat.
        /// </summary>
        Adhoc,
    }

    public sealed class ProvisionAdmission
    {
        public ProvisionKind Kind { get; init; }
        /// <summary>Instrument decision word: local-admitted | multiagent-admitted | refused-no-session | refused-not-party | refused-bad-token | refused-channel-type | logout.</summary>
        public string Decision { get; init; }
        /// <summary>The A2A session for an admitted multiagent provision; null otherwise.</summary>
        public A2ASession Session { get; init; }
        /// <summary>The channel_type seen (empty when absent) for the refusal log.</summary>
        public string ChannelType { get; init; }
        /// <summary>The body's `channel` value ("-" when absent).</summary>
        public string Channel { get; init; }
        public bool Admitted => Kind != ProvisionKind.Refused;
    }

    public static class A2AProvisionAdmission
    {
        public const string ChannelTypeLocal = "local";
        public const string ChannelTypeMultiagent = "multiagent";

        public const string DecisionLocal = "local-admitted";
        public const string DecisionMultiagent = "multiagent-admitted";
        public const string DecisionLogout = "logout";
        public const string DecisionPassthrough = "passthrough-no-feature";
        public const string DecisionNoSession = "refused-no-session";
        public const string DecisionNotParty = "refused-not-party";
        public const string DecisionBadToken = "refused-bad-token";
        public const string DecisionOtherChannelType = "refused-channel-type";

        /// <summary>
        /// Only a spatial ("local") provision records the agent's mixer room in the visibility service's
        /// AgentRoomTable. A call's room is not the agent's spatial room and must never be recorded (the exclusion
        /// batches would be sent to the wrong room); a logout carries no room; a refusal never reaches the service.
        /// </summary>
        public static bool RecordsListenerRoom(ProvisionKind kind) => kind == ProvisionKind.Local;

        public static bool IsLogout(OSDMap map)
            => map != null && map.TryGetBool("logout", out bool lg) && lg;

        /// <summary>
        /// May this admission enter the A2A post-provision bookkeeping (MarkProvisioned, the agent-list
        /// delivery)? ONLY an A2A multiagent admission that actually carries its session may. The null
        /// check is not belt-and-braces: a group admission carries no session and would otherwise throw
        /// here, and it is tested for every ProvisionKind so a new kind cannot quietly re-enter here.
        /// </summary>
        public static bool RecordsA2ASession(ProvisionAdmission admission)
            => admission is not null && admission.Kind == ProvisionKind.Multiagent && admission.Session is not null;

        /// <summary>
        /// True for a body that is not a logout and whose channel_type is "multiagent" or absent: the two requests
        /// that reach the voice service unexamined while no non-spatial voice feature is on, as they did before.
        /// Any other channel type, such as "group", is refused 403 even then: the voice service has no room for it and
        /// answered 500 before.
        /// </summary>
        public static bool IsPassthroughBody(OSDMap map)
        {
            if (map == null || IsLogout(map))
                return false;
            if (!map.TryGetString("channel_type", out string ct))
                return true;
            return ct == ChannelTypeMultiagent;
        }

        public static ProvisionAdmission Passthrough(OSDMap map)
            => new ProvisionAdmission
            {
                Kind = ProvisionKind.Passthrough,
                Decision = DecisionPassthrough,
                ChannelType = map != null && map.TryGetString("channel_type", out string ct) ? ct : string.Empty,
                Channel = map != null && map.TryGetString("channel", out string ch) && !string.IsNullOrEmpty(ch) ? ch : "-",
            };

        public static ProvisionAdmission Decide(OSDMap map, UUID agentID, A2ACalls calls)
        {
            string channelType = map != null && map.TryGetString("channel_type", out string ct) ? ct : string.Empty;
            string channel = map != null && map.TryGetString("channel", out string ch) && !string.IsNullOrEmpty(ch) ? ch : "-";

            // Teardown: recognised by `logout`, never by channel_type (it carries none).
            if (IsLogout(map))
                return new ProvisionAdmission { Kind = ProvisionKind.Logout, Decision = DecisionLogout, ChannelType = channelType, Channel = channel };

            if (channelType == ChannelTypeLocal)
                return new ProvisionAdmission { Kind = ProvisionKind.Local, Decision = DecisionLocal, ChannelType = channelType, Channel = channel };

            if (channelType == ChannelTypeMultiagent)
            {
                A2ASession s = calls?.TryGetByChannel(channel, agentID);
                if (s == null)
                    return Refuse(DecisionNoSession, channelType, channel);
                if (!s.IsParty(agentID))
                    return Refuse(DecisionNotParty, channelType, channel);
                string creds = map.TryGetString("credentials", out string c) ? c : string.Empty;
                if (string.IsNullOrEmpty(s.Token) || !TokenEquals(creds, s.Token))
                    return Refuse(DecisionBadToken, channelType, channel);
                return new ProvisionAdmission { Kind = ProvisionKind.Multiagent, Decision = DecisionMultiagent, Session = s, ChannelType = channelType, Channel = channel };
            }

            return Refuse(DecisionOtherChannelType, channelType, channel);
        }

        private static ProvisionAdmission Refuse(string decision, string channelType, string channel)
            => new ProvisionAdmission { Kind = ProvisionKind.Refused, Decision = decision, ChannelType = channelType, Channel = channel };

        /// <summary>Constant-time comparison; the token is a shared secret.</summary>
        private static bool TokenEquals(string presented, string expected)
        {
            if (presented == null || expected == null || presented.Length != expected.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++)
                diff |= presented[i] ^ expected[i];
            return diff == 0;
        }
    }
}
