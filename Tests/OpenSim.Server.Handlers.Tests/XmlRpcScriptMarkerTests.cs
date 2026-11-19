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

using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nwc.XmlRpc;
using OpenSim.Framework.Servers.HttpServer;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// An XML-RPC handler is given the caller of a request that carries the script HTTP marker as a
/// <see cref="ScriptRequestEndPoint"/>: the grid instant message and Hypergrid logout handlers trusted any caller
/// from a trusted address, and a script's llHTTPRequest to its own host's public address comes from one.
/// </summary>
public class XmlRpcScriptMarkerTests : IDisposable
{
    private readonly BaseHttpServer m_server;
    private readonly uint m_port;
    private readonly List<IPEndPoint> m_callers = new();

    public XmlRpcScriptMarkerTests()
    {
        TcpListener free = new(IPAddress.Loopback, 0);
        free.Start();
        m_port = (uint)((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        m_server = new BaseHttpServer(m_port);
        m_server.AddXmlRPCHandler("marker_probe", (request, caller) =>
        {
            lock (m_callers)
            {
                m_callers.Add(caller);
                m_callers.Add((IPEndPoint)request.Params[1]);
            }
            return new XmlRpcResponse { Value = new Hashtable { ["result"] = "ok" } };
        });
        m_server.Start(false, false);
    }

    public void Dispose()
    {
        m_server.Stop();
    }

    private IPEndPoint[] Call(bool fromScript)
    {
        string body = new XmlRpcRequest("marker_probe", new ArrayList { new Hashtable() }).ToString();
        using HttpClient client = new();
        using HttpRequestMessage message = new(HttpMethod.Post, $"http://127.0.0.1:{m_port}/")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        if (fromScript)
            message.Headers.Add("X-SecondLife-Shard", "OpenSim");

        using HttpResponseMessage response = client.Send(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        lock (m_callers)
        {
            IPEndPoint[] seen = m_callers.ToArray();
            m_callers.Clear();
            return seen;
        }
    }

    [Fact]
    public void AScriptsRequestReachesTheHandlerMarked()
    {
        IPEndPoint[] seen = Call(fromScript: true);

        Assert.Equal(2, seen.Length);
        Assert.All(seen, c => Assert.IsType<ScriptRequestEndPoint>(c));
        Assert.True(IPAddress.IsLoopback(seen[0].Address));
    }

    [Fact]
    public void AnyOtherRequestReachesTheHandlerUnmarked()
    {
        IPEndPoint[] seen = Call(fromScript: false);

        Assert.Equal(2, seen.Length);
        Assert.All(seen, c => Assert.IsNotType<ScriptRequestEndPoint>(c));
    }
}
