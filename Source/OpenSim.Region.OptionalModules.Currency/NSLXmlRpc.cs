/* 
 * Copyright (c) Contributors, http://www.nsl.tuis.ac.jp
 *
 */


using System.Collections;
using System.Xml;
using System.Net;
using System.Text;

using System.Security.Cryptography.X509Certificates;

using Nwc.XmlRpc;
using Microsoft.Extensions.Logging;



namespace NSL.Network.XmlRpc
{
    public class NSLXmlRpcRequest : XmlRpcRequest
    {
        private Encoding _encoding = new UTF8Encoding();
        private XmlRpcRequestSerializer _serializer = new XmlRpcRequestSerializer();
        private XmlRpcResponseDeserializer _deserializer = new XmlRpcResponseDeserializer();

        private readonly ILogger<NSLXmlRpcRequest> _logger;

        public NSLXmlRpcRequest(ILogger<NSLXmlRpcRequest> logger)
        {
            _logger = logger;

            _params = new ArrayList();
        }


        public NSLXmlRpcRequest(ILogger<NSLXmlRpcRequest> logger, String methodName, IList parameters)
        {
            _logger = logger;

            MethodName = methodName;
            _params = parameters;
        }


        public XmlRpcResponse certSend(String url, X509Certificate2 myClientCert, bool checkServerCert, Int32 timeout)
        {
            _logger.LogInformation($"[MONEY NSL RPC]: XmlRpcResponse certSend: connect to {url}");

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);

            if (request == null)
            {
                throw new XmlRpcException(XmlRpcErrorCodes.TRANSPORT_ERROR, XmlRpcErrorCodes.TRANSPORT_ERROR_MSG + ": Could not create request with " + url);
            }

            request.Method = "POST";
            request.ContentType = "text/xml";
            request.AllowWriteStreamBuffering = true;
            request.Timeout = timeout;
            request.UserAgent = "NSLXmlRpcRequest";

            if (myClientCert != null)
            {
                request.ClientCertificates.Add(myClientCert);   // Own certificate
                _logger.LogError("[MONEY NSL RPC]: 111111111111111111111111111");
            }

            if (!checkServerCert) request.Headers.Add("NoVerifyCert", "true");    // Do not verify the certificate of the other party

            Stream? stream = null;
            try
            {
                stream = request.GetRequestStream();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[MONEY NSL RPC]: GetRequestStream Error.");
                stream = null;
            }

            if (stream == null)
            {
                return null;
            }

            XmlTextWriter xml = new XmlTextWriter(stream, _encoding);

            _serializer.Serialize(xml, this);
            xml.Flush();
            xml.Close();

            HttpWebResponse? response = null;

            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[MONEY NSL RPC]: XmlRpcResponse certSend: GetResponse Error.");
            }

            StreamReader input = new StreamReader(response.GetResponseStream());

            string inputXml = input.ReadToEnd();
            XmlRpcResponse resp = (XmlRpcResponse)_deserializer.Deserialize(inputXml);

            input.Close();
            response.Close();

            return resp;
        }
    }
}
