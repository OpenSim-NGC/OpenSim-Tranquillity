/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A region's HTTP client for the grid call session service, in the shape of the mute list services
 * connector (OpenSim.Services.Connectors MuteListServicesConnector): form fields with a METHOD,
 * BaseServiceConnector for the credential. A call that fails for any reason (service down, slow,
 * refused, unreadable reply) answers null, never an exception, so the caller refuses the request
 * rather than falling back to some other store.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Connectors;

namespace osWebRtcVoice.NonSpatial
{
    public class VoiceSessionServicesConnector : BaseServiceConnector, IVoiceSessionService
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly string m_url;
        private readonly int m_timeoutSeconds;

        /// <param name="authSection">The section AuthType, HttpAuthUsername and HttpAuthPassword are read from, after [Network].</param>
        public VoiceSessionServicesConnector(IConfigSource config, string authSection, string serverUri, int timeoutMs)
        {
            m_url = serverUri.TrimEnd('/') + VoiceSessionPostHandler.HandlerPath;
            m_timeoutSeconds = Math.Max(1, (timeoutMs + 999) / 1000);
            Initialise(config, authSection);
        }

        public string Url => m_url;

        public SessionReply Start(int type, UUID creator, UUID otherOrOwner, UUID tempSessionId, UUID originRegion, int requestedCap)
            => Call("start", ("type", type), ("creator", creator), ("other", otherOrOwner), ("temp", tempSessionId), ("origin", originRegion), ("cap", requestedCap));

        public SessionReply StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion, IReadOnlyList<UUID> invitees, int requestedCap)
            => Call("startadhoc", ("creator", creator), ("temp", tempSessionId), ("origin", originRegion), ("invitees", Join(invitees)), ("cap", requestedCap));

        public SessionReply CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyList<UUID> asked)
            => Call("checkinvitecount", ("session", sessionId), ("inviter", inviter), ("asked", Join(asked)));

        public SessionReply Invite(UUID sessionId, UUID inviter, UUID invitee)
            => Call("invite", ("session", sessionId), ("inviter", inviter), ("invitee", invitee));

        public SessionReply Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession)
            => Call("accept", ("session", sessionId), ("agent", agent), ("origin", originRegion), ("viewer", viewerSession ?? string.Empty));

        public SessionReply MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession)
            => Call("markpresent", ("session", sessionId), ("agent", agent), ("origin", originRegion), ("viewer", viewerSession ?? string.Empty));

        public SessionReply Decline(UUID sessionId, UUID agent)
            => Call("decline", ("session", sessionId), ("agent", agent));

        public SessionReply Depart(UUID sessionId, UUID agent, int reason)
            => Call("depart", ("session", sessionId), ("agent", agent), ("reason", reason));

        public SessionReply LeaveChat(UUID sessionId, UUID agent)
            => Call("leavechat", ("session", sessionId), ("agent", agent));

        public SessionReply DepartByViewerSession(UUID agent, string viewerSession, int reason)
            => Call("departbyviewer", ("agent", agent), ("viewer", viewerSession ?? string.Empty), ("reason", reason));

        public SessionReply DepartAll(UUID agent, int reason)
            => Call("departall", ("agent", agent), ("reason", reason));

        public SessionReply DepartRegion(UUID region)
            => Call("departregion", ("region", region));

        public SessionReply RenewSeats(IReadOnlyList<SeatKey> seats)
            => Call("renewseats", ("seats", JoinSeats(seats)));

        public SessionReply MarkInvited(UUID sessionId, IReadOnlyList<UUID> agents)
            => Call("markinvited", ("session", sessionId), ("agents", Join(agents)));

        public SessionReply VerifyRing(UUID sessionId, UUID target, UUID claimedCaller, int type)
            => Call("verifyring", ("session", sessionId), ("target", target), ("caller", claimedCaller), ("type", type));

        public SessionReply CarrierKey(UUID sessionId, UUID agent)
            => Call("carrierkey", ("session", sessionId), ("agent", agent));

        public SessionReply IssueToken(UUID sessionId, UUID agent)
            => Call("issuetoken", ("session", sessionId), ("agent", agent));

        public SessionReply TakeConferenceRequest(UUID agent)
            => Call("takeconferencerequest", ("agent", agent));

        public SessionReply Get(UUID sessionId, UUID agent = default)
            => Call("get", ("session", sessionId), ("agent", agent));

        public SessionReply GetByRoomKey(string roomKey, UUID agent = default)
            => Call("getbyroomkey", ("roomkey", roomKey ?? string.Empty), ("agent", agent));

        // session:agent pairs, comma separated
        private static string JoinSeats(IEnumerable<SeatKey> seats)
        {
            if (seats == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (SeatKey seat in seats)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(seat.Session.ToString()).Append(':').Append(seat.Agent.ToString());
            }
            return sb.ToString();
        }

        private static string Join(IEnumerable<UUID> ids)
        {
            if (ids == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (UUID id in ids)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(id.ToString());
            }
            return sb.ToString();
        }

        private SessionReply Call(string method, params (string Key, object Value)[] fields)
        {
            Dictionary<string, object> send = new Dictionary<string, object> { ["METHOD"] = method };
            foreach ((string key, object value) in fields)
                send[key] = value?.ToString() ?? string.Empty;
            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", m_url, ServerUtils.BuildQueryString(send), m_timeoutSeconds, m_Auth);
                if (string.IsNullOrEmpty(reply))
                    return null;
                Dictionary<string, object> data = ServerUtils.ParseXmlResponse(reply);
                return data.TryGetValue("result", out object json) ? SessionReply.FromJson(json?.ToString()) : null;
            }
            catch (Exception e)
            {
                m_log.LogDebug("[VOICE SESSION CONNECTOR]: {Method} to {Url} failed: {Message}", method, m_url, e.Message);
                return null;
            }
        }
    }
}
