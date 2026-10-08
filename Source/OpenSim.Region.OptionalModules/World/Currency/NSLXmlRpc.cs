/* 
 * Copyright (c) Contributors, http://www.nsl.tuis.ac.jp
 *
 */


using System.Collections;
using System.Xml;
using System.Net;
using System.Text;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Nwc.XmlRpc;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Region.OptionalModules.World.Currency;

public class NSLXmlRpcRequest : XmlRpcRequest
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private Encoding _encoding = new UTF8Encoding();
    private XmlRpcRequestSerializer _serializer = new XmlRpcRequestSerializer();
    private XmlRpcResponseDeserializer _deserializer = new XmlRpcResponseDeserializer();


    public NSLXmlRpcRequest()
    {
        _params = new ArrayList();
    }


    public NSLXmlRpcRequest(String methodName, IList parameters)
    {
        MethodName = methodName;
        _params = parameters;
    }


    public XmlRpcResponse certSend(String url, X509Certificate2 myClientCert, bool checkServerCert, Int32 timeout)
    {
        m_log.LogInformation("[MONEY NSL RPC]: XmlRpcResponse certSend: connect to {0}", url);

        using SocketsHttpHandler handler = WebUtil.CreateLegacyHttpHandler();
        if (myClientCert != null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { myClientCert };
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromMilliseconds(timeout) };
        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.UserAgent.ParseAdd("NSLXmlRpcRequest");
        if (!checkServerCert)
            request.Headers.Add("NoVerifyCert", "true");
        using MemoryStream buffer = new();
        using (XmlTextWriter xml = new(buffer, _encoding))
        {
            _serializer.Serialize(xml, this);
            xml.Flush();
            request.Content = new ByteArrayContent(buffer.ToArray());
        }
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml");
        using HttpResponseMessage response = client.Send(request);
        response.EnsureSuccessStatusCode();
        using StreamReader input = new(response.Content.ReadAsStream());
        return (XmlRpcResponse)_deserializer.Deserialize(input.ReadToEnd());
    }
}
