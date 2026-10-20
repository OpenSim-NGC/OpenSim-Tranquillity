/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Loads the grid call session service into Robust. Add it to [ServiceList] as
 *     VoiceSessionConnector = "${Const|PrivatePort}/WebRtcVoice.dll:VoiceSessionServerConnector"
 * and configure [WebRtcVoiceSessionService]. Nothing is served unless Enabled is true, and an enabled
 * service refuses to load without HTTP basic authentication and a password of at least
 * MinPasswordLength characters: the records it keeps authorise calls, so it never runs open.
 */
using System;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

namespace osWebRtcVoice.NonSpatial
{
    public class VoiceSessionServerConnector : ServiceConnector
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string SectionName = "WebRtcVoiceSessionService";

        /// <summary>Shortest accepted service password, the same minimum the voice connector secret enforces.</summary>
        public const int MinPasswordLength = 32;

        private readonly VoiceSessionService m_service;
        private Timer m_sweepTimer;

        public VoiceSessionServerConnector(IConfigSource config, IHttpServer server, string configName)
            : base(config, server, configName)
        {
            IConfig section = config.Configs[SectionName];
            if (section == null || !section.GetBoolean("Enabled", false))
            {
                m_log.LogInformation("[VOICE SESSION SERVICE]: not enabled ([{Section}] Enabled is not true)", SectionName);
                return;
            }

            string problem = CheckAuthConfig(config);
            if (problem != null)
                throw new Exception($"[{SectionName}] is enabled but cannot load: {problem}");

            m_service = CreateService(config);
            IServiceAuth auth = ServiceAuth.Create(config, SectionName);
            server.AddStreamHandler(new VoiceSessionPostHandler(m_service, auth));

            int sweepSeconds = Math.Max(1, section.GetInt("SweepSeconds", DefaultSweepSeconds));
            m_sweepTimer = new Timer(_ => RunSweep(), null, TimeSpan.FromSeconds(sweepSeconds), TimeSpan.FromSeconds(sweepSeconds));
            m_log.LogInformation("[VOICE SESSION SERVICE]: serving {Path}; grid id \"{GridId}\"; limits {Sessions} sessions, "
                + "{PerAgent} per agent, burst {Burst}", VoiceSessionPostHandler.HandlerPath, GridIdOf(config),
                m_service.Limits.MaxNonSpatialSessions, m_service.Limits.MaxSessionsPerAgent, m_service.Limits.ConferenceRequestBurst);
        }

        public const int DefaultSweepSeconds = 30;
        public const int DefaultInviteTtlSeconds = 120;
        public const int DefaultIdleTtlHours = 8;
        public const int DefaultSeatLeaseSeconds = 180;

        /// <summary>Why an enabled service may not load, or null when its authentication is acceptable.</summary>
        public static string CheckAuthConfig(IConfigSource config, string section = SectionName)
        {
            string[] sections = { "Network", section };
            string authType = Util.GetConfigVarFromSections<string>(config, "AuthType", sections, "None");
            if (authType != "BasicHttpAuthentication")
                return "AuthType must be BasicHttpAuthentication";
            string user = Util.GetConfigVarFromSections<string>(config, "HttpAuthUsername", sections, string.Empty);
            if (string.IsNullOrEmpty(user))
                return "HttpAuthUsername is not set";
            string password = Util.GetConfigVarFromSections<string>(config, "HttpAuthPassword", sections, string.Empty);
            if (password.Length < MinPasswordLength)
                return $"HttpAuthPassword must be at least {MinPasswordLength} characters";
            return null;
        }

        public static VoiceSessionService CreateService(IConfigSource config)
        {
            IConfig section = config.Configs[SectionName];
            NonSpatialLimits limits = new NonSpatialLimits
            {
                MaxNonSpatialSessions = Math.Max(1, section.GetInt("MaxSessions", NonSpatialLimits.DefaultMaxNonSpatialSessions)),
                MaxSessionsPerAgent = Math.Max(1, section.GetInt("MaxSessionsPerAgent", NonSpatialLimits.DefaultMaxSessionsPerAgent)),
                ConferenceRequestBurst = Math.Max(1, section.GetInt("ConferenceRequestBurst", NonSpatialLimits.DefaultConferenceRequestBurst)),
                ConferenceRequestRefillSeconds = section.GetDouble("ConferenceRequestRefillSeconds", NonSpatialLimits.DefaultConferenceRequestRefillSeconds),
            };
            VoiceSessionService service = new VoiceSessionService(
                GridIdOf(config), limits,
                inviteTtl: TimeSpan.FromSeconds(Math.Max(1, section.GetInt("InviteTtlSeconds", DefaultInviteTtlSeconds))),
                idleTtl: TimeSpan.FromHours(Math.Max(1, section.GetInt("IdleTtlHours", DefaultIdleTtlHours))),
                seatLease: TimeSpan.FromSeconds(Math.Max(10, section.GetInt("SeatLeaseSeconds", DefaultSeatLeaseSeconds))));
            service.LimitRefused += (agent, decision, detail) =>
                m_log.LogWarning("[VOICE SESSION SERVICE]: limit: agent={Agent} decision={Decision} {Detail}", agent, decision, detail);
            return service;
        }

        /// <summary>
        /// The grid id room keys are derived from. [WebRtcVoiceSessionService] GridId wins when set; otherwise it
        /// is read through the same chain a region reads it (GatekeeperURI in [Const], [Startup] or [Hypergrid],
        /// then [GatekeeperService] ExternalName, then [GridService] Gatekeeper), normalised the same way, so
        /// the service and every region derive the same room key.
        /// </summary>
        public static string GridIdOf(IConfigSource config)
        {
            string explicitId = config.Configs[SectionName]?.GetString("GridId", string.Empty);
            string raw = explicitId;
            if (string.IsNullOrEmpty(raw))
            {
                string[] sections = { "Const", "Startup", "Hypergrid" };
                raw = Util.GetConfigVarFromSections<string>(config, "GatekeeperURI", sections, string.Empty);
                if (string.IsNullOrEmpty(raw))
                    raw = config.Configs["GatekeeperService"]?.GetString("ExternalName", string.Empty);
                if (string.IsNullOrEmpty(raw))
                    raw = config.Configs["GridService"]?.GetString("Gatekeeper", string.Empty);
            }
            return string.IsNullOrEmpty(raw) ? string.Empty : raw.Trim().TrimEnd('/').ToLowerInvariant();
        }

        private void RunSweep()
        {
            try
            {
                VoiceSessionService.SweepResult s = m_service.Sweep();
                if (s.LeasesExpired + s.InvitationsExpired + s.SessionsRemoved > 0)
                    m_log.LogDebug("[VOICE SESSION SERVICE]: sweep seat-leases={Leases} invitations={Invitations} sessions={Sessions} seats={Seats}",
                        s.LeasesExpired, s.InvitationsExpired, s.SessionsRemoved, s.SeatsReleased);
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "[VOICE SESSION SERVICE]: sweep failed");
            }
        }
    }
}
