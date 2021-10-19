/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Mono.Addins;
using MimeKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.Framework.EMail
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SMTPModule")]
    public class SMTPModule : ISharedRegionModule, ISMTPModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // SMTP configuration
        private string SMTP_SERVER_HOSTNAME = string.Empty;
        private int    SMTP_SERVER_PORT = 25;
        private string SMTP_SERVER_LOGIN = string.Empty;
        private string SMTP_SERVER_PASSWORD = string.Empty;
        private string SMTP_SERVER_FROM = string.Empty;
        private string SMTP_SERVER_REPLYTO = string.Empty;

        // This hostname, if we need it for object email.
        private string SMTP_HOSTNAME = string.Empty;

        // Scenes by Region Handle
        private readonly Dictionary<ulong, Scene> m_Scenes = new Dictionary<ulong, Scene>();
        private bool m_Enabled = false;

        #region ISharedRegionModule

        public void Initialise(IConfigSource config)
        {
            IConfig SMTPConfig;

            //Load SMTP SERVER config
            try
            {
                if ((SMTPConfig = config.Configs["SMTP"]) == null)
                {
                    m_Enabled = false;
                    return;
                }

                if (!SMTPConfig.GetBoolean("enabled", false))
                {
                    m_Enabled = false;
                    return;
                }

                var m_HostName = SMTPConfig.GetString("host_domain_header_from");

                // SMTP Configuration
                SMTP_SERVER_HOSTNAME = SMTPConfig.GetString("SMTP_SERVER_HOSTNAME", SMTP_SERVER_HOSTNAME);
                SMTP_SERVER_PORT = SMTPConfig.GetInt("SMTP_SERVER_PORT", SMTP_SERVER_PORT);
                SMTP_SERVER_LOGIN = SMTPConfig.GetString("SMTP_SERVER_LOGIN", SMTP_SERVER_LOGIN);
                SMTP_SERVER_PASSWORD = SMTPConfig.GetString("SMTP_SERVER_PASSWORD", SMTP_SERVER_PASSWORD);
                SMTP_SERVER_FROM = SMTPConfig.GetString("SMTP_SERVER_FROM", SMTP_SERVER_FROM);
                SMTP_SERVER_REPLYTO = SMTPConfig.GetString("SMTP_SERVER_REPLYTO", SMTP_SERVER_REPLYTO); 

                // Hostname
                SMTP_HOSTNAME = SMTPConfig.GetString("SMTP_HOSTNAME", m_HostName);
            }
            catch (Exception e)
            {
                m_log.Error("[SMTP]: SMTPModule not configured: " + e.Message);
                m_Enabled = false;
                return;
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            // It's a go!
            lock (m_Scenes)
            {
                // Claim the interface slot
                scene.RegisterModuleInterface<ISMTPModule>(this);

                // Add to scene list
                if (m_Scenes.ContainsKey(scene.RegionInfo.RegionHandle))
                {
                    m_Scenes[scene.RegionInfo.RegionHandle] = scene;
                }
                else
                {
                    m_Scenes.Add(scene.RegionInfo.RegionHandle, scene);
                }
            }

            m_log.Info("[SMTP]: Activated SMTPModule");
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "SMTPModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void RegionLoaded(Scene scene)
        {
        }

        #endregion

        private SceneObjectPart LocateObject(UUID objectID)
        {
            lock (m_Scenes)
            {
                foreach (var scene in m_Scenes.Values)
                {
                    var part = scene.GetSceneObjectPart(objectID);
                    if (part != null)
                        return part;
                }
            }

            return null;
        }

        private UserAccount LocateUser(UUID AgentId)
        {
            lock (m_Scenes)
            {
                foreach (var scene in m_Scenes.Values)
                {
                    var account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, AgentId);
                    if (account != null)
                        return account;
                }
            }

            return null;
        }

        #region ISMTPModule

        public string FormatAgentAddress(UUID agentID)
        {
            var user = LocateUser(agentID);
            if (user != null)
                return (user.Email);
            else
                return string.Empty;
        }

        public string FormatObjectAddress(UUID objectID)
        {
            var sop = LocateObject(objectID);
            if (sop != null)
                return (objectID.ToString() + "@" + SMTP_HOSTNAME);
            else
                return string.Empty;
        }

        /// <summary>
        /// Format a MimeMessage using the string values provided.  Validate the from and to using a 
        /// Regex to make sure they are a valid email address.
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="subject"></param>
        /// <param name="body"></param>
        public MimeMessage FormatMessage(string from, string to, string subject, string body)
        {
            //Check if address is empty
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) ||
                string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(body))
                return null;

            //FIXED:Check the email is correct form in REGEX
            var EMailpatternStrict = @"^(([^<>()[\]\\.,;:\s@\""]+"
                + @"(\.[^<>()[\]\\.,;:\s@\""]+)*)|(\"".+\""))@"
                + @"((\[[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}"
                + @"\.[0-9]{1,3}\])|(([a-zA-Z\-0-9]+\.)+"
                + @"[a-zA-Z]{2,}))$";

            var EMailreStrict = new Regex(EMailpatternStrict);

            // Verify the from and to addresses are valid
            if (EMailreStrict.IsMatch(from) == false)
            {
                m_log.Error("[SMTP]: REGEX Problem in From EMail Address: " + from);
                return null;
            }

            if (EMailreStrict.IsMatch(to) == false)
            {
                m_log.Error("[SMTP]: REGEX Problem in To EMail Address: " + to);
                return null;
            }

            var emailMessage = new MimeMessage();

            emailMessage.From.Add(MailboxAddress.Parse(from));
            emailMessage.To.Add(MailboxAddress.Parse(to));
            emailMessage.Subject = subject;
            emailMessage.Body = new TextPart("plain") { Text = body };

            if (string.IsNullOrEmpty(SMTP_SERVER_REPLYTO) == false)
            {
                emailMessage.ReplyTo.Add(MailboxAddress.Parse(SMTP_SERVER_REPLYTO));
            }

            return emailMessage;
        }

        /// <summary>
        /// Send one or more formatted MimeMessages given the SMTP parameters configured
        /// </summary>
        /// <param name="messages"></param>
        public void SendMessages(IList<MimeMessage> messages)
        {
            using (var client = new SmtpClient())
            {
                client.Connect(SMTP_SERVER_HOSTNAME, SMTP_SERVER_PORT, SecureSocketOptions.SslOnConnect);

                if (SMTP_SERVER_LOGIN != String.Empty && SMTP_SERVER_PASSWORD != String.Empty)
                {
                    client.Authenticate(SMTP_SERVER_LOGIN, SMTP_SERVER_PASSWORD);
                }

                foreach (var message in messages)
                {
                    client.Send(message);
                    m_log.InfoFormat("[SMTP]: Email sent to {} from {}", message.To.ToString(), message.From.ToString());
                }

                client.Disconnect(true);
            }
        }

        #endregion
    }
}
