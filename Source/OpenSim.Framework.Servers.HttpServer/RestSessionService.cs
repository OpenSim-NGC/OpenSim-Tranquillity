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

using System.Net;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Framework.Servers.HttpServer;

public class RestSessionObject<TRequest>
{
    private string sid;
    private string aid;
    private TRequest request_body;

    public string SessionID
    {
        get { return sid; }
        set { sid = value; }
    }

    public string AvatarID
    {
        get { return aid; }
        set { aid = value; }
    }

    public TRequest Body
    {
        get { return request_body; }
        set { request_body = value; }
    }
}

public class SynchronousRestSessionObjectPoster<TRequest, TResponse>
{
    public static TResponse BeginPostObject(string verb, string requestUrl, TRequest obj, string sid, string aid)
    {
        RestSessionObject<TRequest> sobj = new RestSessionObject<TRequest>();
        sobj.SessionID = sid;
        sobj.AvatarID = aid;
        sobj.Body = obj;

        return WebUtil.SendXmlRequestAsync<RestSessionObject<TRequest>, TResponse>(
            verb, requestUrl, sobj, 20000).GetAwaiter().GetResult();
    }
}

public class RestSessionObjectPosterResponse<TRequest, TResponse>
{
    public ReturnResponse<TResponse> ResponseCallback;

    public void BeginPostObject(string requestUrl, TRequest obj, string sid, string aid)
    {
        BeginPostObject("POST", requestUrl, obj, sid, aid);
    }

    public void BeginPostObject(string verb, string requestUrl, TRequest obj, string sid, string aid)
    {
        _ = PostObjectAsync(verb, requestUrl, obj, sid, aid);
    }

    public Task PostObjectAsync(string verb, string requestUrl, TRequest obj, string sid, string aid)
    {
        RestSessionObject<TRequest> sobj = new() { SessionID = sid, AvatarID = aid, Body = obj };
        return RestObjectPoster.PostObjectAsync(verb, requestUrl, sobj, 10000, stream =>
        {
            TResponse deserial = (TResponse)new XmlSerializer(typeof(TResponse)).Deserialize(stream);
            if (deserial != null)
                ResponseCallback?.Invoke(deserial);
        });
    }
}

public delegate bool CheckIdentityMethod(string sid, string aid);

public class RestDeserialiseSecureHandler<TRequest, TResponse> : BaseOutputStreamHandler, IStreamHandler
    where TRequest : new()
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private RestDeserialiseMethod<TRequest, TResponse> m_method;
    private CheckIdentityMethod m_smethod;

    public RestDeserialiseSecureHandler(
         string httpMethod, string path,
         RestDeserialiseMethod<TRequest, TResponse> method, CheckIdentityMethod smethod)
        : base(httpMethod, path)
    {
        m_smethod = smethod;
        m_method = method;
    }

    protected override void ProcessRequest(string path, Stream request, Stream responseStream,
                       IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        RestSessionObject<TRequest> deserial = default(RestSessionObject<TRequest>);
        bool fail = false;

        using (XmlTextReader xmlReader = new XmlTextReader(request))
        {
            try
            {
                xmlReader.DtdProcessing = DtdProcessing.Ignore;
                XmlSerializer deserializer = new XmlSerializer(typeof(RestSessionObject<TRequest>));
                deserial = (RestSessionObject<TRequest>)deserializer.Deserialize(xmlReader);
            }
            catch (Exception e)
            {
                m_log.LogError("[REST]: Deserialization problem. Ignoring request. " + e);
                fail = true;
            }
        }

        TResponse response = default(TResponse);
        if (!fail && m_smethod(deserial.SessionID, deserial.AvatarID))
        {
            response = m_method(deserial.Body);
        }

        using (XmlWriter xmlWriter = XmlTextWriter.Create(responseStream))
        {
            XmlSerializer serializer = new XmlSerializer(typeof(TResponse));
            serializer.Serialize(xmlWriter, response);
        }
    }
}

public delegate bool CheckTrustedSourceMethod(IPEndPoint peer);

public class RestDeserialiseTrustedHandler<TRequest, TResponse> : BaseOutputStreamHandler, IStreamHandler
    where TRequest : new()
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>
    /// The operation to perform once trust has been established.
    /// </summary>
    private RestDeserialiseMethod<TRequest, TResponse> m_method;

    /// <summary>
    /// The method used to check whether a request is trusted.
    /// </summary>
    private CheckTrustedSourceMethod m_tmethod;

    public RestDeserialiseTrustedHandler(string httpMethod, string path, RestDeserialiseMethod<TRequest, TResponse> method, CheckTrustedSourceMethod tmethod)
        : base(httpMethod, path)
    {
        m_tmethod = tmethod;
        m_method = method;
    }

    protected override void ProcessRequest(string path, Stream request, Stream responseStream,
                       IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        TRequest deserial = default(TRequest);
        bool fail = false;

        using (XmlTextReader xmlReader = new XmlTextReader(request))
        {
            try
            {
                xmlReader.DtdProcessing = DtdProcessing.Ignore;
                XmlSerializer deserializer = new XmlSerializer(typeof(TRequest));
                deserial = (TRequest)deserializer.Deserialize(xmlReader);
            }
            catch (Exception e)
            {
                m_log.LogError("[REST]: Deserialization problem. Ignoring request. " + e);
                fail = true;
            }
        }

        TResponse response = default(TResponse);
        if (!fail && m_tmethod(httpRequest.RemoteIPEndPoint))
        {
            response = m_method(deserial);
        }

        using (XmlWriter xmlWriter = XmlTextWriter.Create(responseStream))
        {
            XmlSerializer serializer = new XmlSerializer(typeof(TResponse));
            serializer.Serialize(xmlWriter, response);
        }
    }
}