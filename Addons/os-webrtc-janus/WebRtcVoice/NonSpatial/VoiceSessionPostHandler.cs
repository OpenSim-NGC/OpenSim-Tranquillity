/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The Robust-side handler for the grid call session service. The shape follows the mute list service
 * (OpenSim.Server.Handlers MuteListServerPostHandler): a stream handler on one path, a form body with a
 * METHOD field, a thin switch, and a failure reply for anything it cannot read. Calls are accepted
 * only through the handler's IServiceAuth (BaseStreamHandler.Handle checks it before ProcessRequest
 * runs), which VoiceSessionServerConnector requires to be HTTP basic authentication with a long password.
 *
 * The handler holds no logic of its own: each method reads its fields and calls the service.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;

namespace osWebRtcVoice.NonSpatial
{
    public class VoiceSessionPostHandler : BaseStreamHandler
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string HandlerPath = "/voicesessions";

        /// <summary>Largest request body read. A renewal of the most seats it accepts is about 730 KB.</summary>
        public const int MaxBodyChars = 1024 * 1024;

        private readonly IVoiceSessionService m_service;

        public VoiceSessionPostHandler(IVoiceSessionService service, IServiceAuth auth)
            : base("POST", HandlerPath, auth)
        {
            m_service = service ?? throw new ArgumentNullException(nameof(service));
        }

        protected override byte[] ProcessRequest(string path, Stream requestData,
                IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            string method = string.Empty;
            try
            {
                string body;
                using (StreamReader sr = new StreamReader(requestData))
                    body = sr.ReadToEnd();
                if (body.Length > MaxBodyChars)
                    return FailureResult();

                Dictionary<string, object> request = ServerUtils.ParseQueryString(body.Trim());
                if (!request.TryGetValue("METHOD", out object m))
                    return FailureResult();
                method = m.ToString();

                SessionReply reply = Dispatch(method, request);
                return reply == null ? FailureResult() : ResultOf(reply);
            }
            catch (Exception e)
            {
                m_log.LogDebug("[VOICE SESSION HANDLER]: exception in method {Method}: {Message}", method, e.Message);
            }
            return FailureResult();
        }

        /// <summary>Null when the request is malformed or the method unknown; nothing is changed then.</summary>
        public SessionReply Dispatch(string method, Dictionary<string, object> r)
        {
            switch (method)
            {
                case "start":
                    return Ids(r, out UUID[] s, "creator", "other", "temp", "origin") && Int(r, "type", out int type) && Int(r, "cap", out int cap)
                        ? m_service.Start(type, s[0], s[1], s[2], s[3], cap) : null;
                case "startadhoc":
                    return Ids(r, out s, "creator", "temp", "origin") && List(r, "invitees", out List<UUID> inv) && Int(r, "cap", out cap)
                        ? m_service.StartAdhoc(s[0], s[1], s[2], inv, cap) : null;
                case "checkinvitecount":
                    return Ids(r, out s, "session", "inviter") && List(r, "asked", out List<UUID> asked)
                        ? m_service.CheckInviteCount(s[0], s[1], asked) : null;
                case "invite":
                    return Ids(r, out s, "session", "inviter", "invitee") ? m_service.Invite(s[0], s[1], s[2]) : null;
                case "accept":
                    return Ids(r, out s, "session", "agent", "origin") ? m_service.Accept(s[0], s[1], s[2], Str(r, "viewer")) : null;
                case "markpresent":
                    return Ids(r, out s, "session", "agent", "origin") ? m_service.MarkPresent(s[0], s[1], s[2], Str(r, "viewer")) : null;
                case "decline":
                    return Ids(r, out s, "session", "agent") ? m_service.Decline(s[0], s[1]) : null;
                case "depart":
                    return Ids(r, out s, "session", "agent") && Int(r, "reason", out int reason) ? m_service.Depart(s[0], s[1], reason) : null;
                case "leavechat":
                    return Ids(r, out s, "session", "agent") ? m_service.LeaveChat(s[0], s[1]) : null;
                case "departbyviewer":
                    return Ids(r, out s, "agent") && Int(r, "reason", out reason) ? m_service.DepartByViewerSession(s[0], Str(r, "viewer"), reason) : null;
                case "departall":
                    return Ids(r, out s, "agent") && Int(r, "reason", out reason) ? m_service.DepartAll(s[0], reason) : null;
                case "departregion":
                    return Ids(r, out s, "region") ? m_service.DepartRegion(s[0]) : null;
                case "renewseats":
                    return Seats(r, "seats", out List<SeatKey> seats) ? m_service.RenewSeats(seats) : null;
                case "markinvited":
                    return Ids(r, out s, "session") && List(r, "agents", out List<UUID> agents) ? m_service.MarkInvited(s[0], agents) : null;
                case "verifyring":
                    return Ids(r, out s, "session", "target", "caller") && Int(r, "type", out int ringType)
                        ? m_service.VerifyRing(s[0], s[1], s[2], ringType) : null;
                case "carrierkey":
                    return Ids(r, out s, "session", "agent") ? m_service.CarrierKey(s[0], s[1]) : null;
                case "issuetoken":
                    return Ids(r, out s, "session", "agent") ? m_service.IssueToken(s[0], s[1]) : null;
                case "takeconferencerequest":
                    return Ids(r, out s, "agent") ? m_service.TakeConferenceRequest(s[0]) : null;
                case "get":
                    return Ids(r, out s, "session") && OptionalId(r, "agent", out UUID reader) ? m_service.Get(s[0], reader) : null;
                case "getbyroomkey":
                    return Str(r, "roomkey") is string key && key.Length > 0 && key.Length <= 256 && OptionalId(r, "agent", out reader)
                        ? m_service.GetByRoomKey(key, reader) : null;
            }
            m_log.LogDebug("[VOICE SESSION HANDLER]: unknown method request: {Method}", method);
            return null;
        }

        // ---- reading fields ----------------------------------------------------------------------

        private static string Str(Dictionary<string, object> r, string key)
            => r.TryGetValue(key, out object v) ? v?.ToString() : null;

        private static bool Int(Dictionary<string, object> r, string key, out int value)
        {
            value = 0;
            return r.TryGetValue(key, out object v) && int.TryParse(v?.ToString(), out value);
        }

        private static bool Ids(Dictionary<string, object> r, out UUID[] ids, params string[] keys)
        {
            ids = new UUID[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                if (!r.TryGetValue(keys[i], out object v) || !UUID.TryParse(v?.ToString(), out ids[i]))
                    return false;
            return true;
        }

        /// <summary>An id that may be absent or empty (then Zero); present but unreadable refuses the request.</summary>
        private static bool OptionalId(Dictionary<string, object> r, string key, out UUID id)
        {
            id = UUID.Zero;
            string text = Str(r, key);
            return string.IsNullOrEmpty(text) || UUID.TryParse(text, out id);
        }

        /// <summary>A comma separated list of ids; absent or empty is an empty list, a bad entry refuses the request.</summary>
        private static bool List(Dictionary<string, object> r, string key, out List<UUID> ids)
        {
            ids = new List<UUID>();
            string text = Str(r, key);
            if (string.IsNullOrEmpty(text)) return true;
            foreach (string part in text.Split(','))
            {
                if (!UUID.TryParse(part, out UUID id)) return false;
                ids.Add(id);
            }
            return true;
        }

        /// <summary>A comma separated list of session:agent pairs; absent or empty is an empty list, a bad entry refuses the request.</summary>
        private static bool Seats(Dictionary<string, object> r, string key, out List<SeatKey> seats)
        {
            seats = new List<SeatKey>();
            string text = Str(r, key);
            if (string.IsNullOrEmpty(text)) return true;
            foreach (string part in text.Split(','))
            {
                string[] pair = part.Split(':');
                if (pair.Length != 2 || !UUID.TryParse(pair[0], out UUID session) || !UUID.TryParse(pair[1], out UUID agent)) return false;
                seats.Add(new SeatKey(session, agent));
            }
            return true;
        }

        // ---- replies -----------------------------------------------------------------------------

        private static byte[] ResultOf(SessionReply reply)
        {
            Dictionary<string, object> result = new Dictionary<string, object> { ["result"] = reply.ToJson() };
            return Util.UTF8NoBomEncoding.GetBytes(ServerUtils.BuildXmlResponse(result));
        }

        private static byte[] FailureResult()
        {
            Dictionary<string, object> result = new Dictionary<string, object> { ["Result"] = "Failure" };
            return Util.UTF8NoBomEncoding.GetBytes(ServerUtils.BuildXmlResponse(result));
        }
    }
}
