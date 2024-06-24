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

using System.Reflection;
using System.Xml;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Server.Handlers.Base;
using OpenMetaverse;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.OfflineIM
{
    public class OfflineIMServiceRobustConnector : IServiceConnector
    {
        private IOfflineIMService m_OfflineIMService;
        private static string m_ConfigName = "Messaging";

        private readonly IConfiguration m_configuration;
        private readonly ILogger<OfflineIMServiceRobustConnector> m_logger;

        public string ConfigName => m_ConfigName;

        public IHttpServer HttpServer => throw new NotImplementedException();

        public OfflineIMServiceRobustConnector(
            IConfiguration configuration, 
            ILogger<OfflineIMServiceRobustConnector> logger,
            IHttpServer server) :
            this(configuration, logger, server, m_ConfigName)
        {
        }

        public OfflineIMServiceRobustConnector(
            IConfiguration configuration, 
            ILogger<OfflineIMServiceRobustConnector> logger,
            IHttpServer server, 
            string configName)
        {
            m_configuration = configuration;
            m_logger = logger;

            m_logger.LogDebug($"Starting with config name {configName}");

            m_OfflineIMService = new OfflineIMService(configuration);

            IServiceAuth auth = ServiceAuth.Create(configuration, configName);

            server.AddStreamHandler(new OfflineIMServicePostHandler(m_OfflineIMService, auth));
        }

        public void Initialize(IHttpServer httpServer)
        {
            throw new NotImplementedException();
        }
    }

    public class OfflineIMServicePostHandler : BaseStreamHandler
    {
        private IOfflineIMService m_OfflineIMService;
        public OfflineIMServicePostHandler(IOfflineIMService service, IServiceAuth auth) :
            base("POST", "/offlineim", auth)
        {
            m_OfflineIMService = service;
        }

        protected override byte[] ProcessRequest(string path, Stream requestData,
                IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            StreamReader sr = new StreamReader(requestData);
            string body = sr.ReadToEnd();
            sr.Close();
            body = body.Trim();

            Dictionary<string, object> request =
                    ServerUtils.ParseQueryString(body);

            if (!request.ContainsKey("METHOD"))
                return FailureResult();

            string method = request["METHOD"].ToString();
            request.Remove("METHOD");

            switch (method)
            {
                case "GET":
                    return HandleGet(request);
                case "STORE":
                    return HandleStore(request);
                case "DELETE":
                    return HandleDelete(request);
            }

            return FailureResult();
        }

        byte[] HandleStore(Dictionary<string, object> request)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();

            GridInstantMessage im = OfflineIMDataUtils.GridInstantMessage(request);

            string reason = string.Empty;

            bool success = m_OfflineIMService.StoreMessage(im, out reason);

            result["RESULT"] = success.ToString();
            if (!success)
                result["REASON"] = reason;

            string xmlString = ServerUtils.BuildXmlResponse(result);

            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGet(Dictionary<string, object> request)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();

            if (!request.ContainsKey("PrincipalID"))
                NullResult(result, "Bad network data");
            else
            {
                UUID principalID = new UUID(request["PrincipalID"].ToString());
                List<GridInstantMessage> ims = m_OfflineIMService.GetMessages(principalID);

                Dictionary<string, object> dict = new Dictionary<string, object>();
                int i = 0;
                foreach (GridInstantMessage m in ims)
                    dict["im-" + i++] = OfflineIMDataUtils.GridInstantMessage(m);

                result["RESULT"] = dict;
            }

            string xmlString = ServerUtils.BuildXmlResponse(result);

            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleDelete(Dictionary<string, object> request)
        {
            if (!request.ContainsKey("UserID"))
            {
                return FailureResult();
            }
            else
            {
                UUID userID = new UUID(request["UserID"].ToString());
                m_OfflineIMService.DeleteMessages(userID);

                return SuccessResult();
            }
        }

        #region Helpers

        private void NullResult(Dictionary<string, object> result, string reason)
        {
            result["RESULT"] = "NULL";
            result["REASON"] = reason;
        }

        private byte[] FailureResult()
        {
            return BoolResult(false);
        }

        private byte[] SuccessResult()
        {
            return BoolResult(true);
        }

        private byte[] BoolResult(bool value)
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration, "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse", "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "RESULT", "");
            result.AppendChild(doc.CreateTextNode(value.ToString()));

            rootElement.AppendChild(result);

            return Util.DocToBytes(doc);
        }

        #endregion
    }
}
