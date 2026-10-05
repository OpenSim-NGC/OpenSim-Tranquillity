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
using System.Net.Http;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.CoreModules.Scripting.XMLRPC;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;

/// <summary>
/// llSendRemoteData goes through the same outbound URL filter as llHTTPRequest: [Network]
/// OutboundDisallowForUserScripts / OutboundDisallowForUserScriptsExcept, same defaults, redirects included.
/// </summary>
/// <remarks>
/// WebUtil's shared HTTP handlers are pointed at a recording proxy on 127.0.0.1 for the duration of each test and
/// restored afterwards. Whatever the module tries to reach - 10.0.0.1, a public address - arrives at that proxy and
/// is answered there, never forwarded: no test here, and no version of the module under test, sends a packet off
/// this machine.
/// </remarks>
[Collection("OutboundProcessWideState")]
public class XmlRpcOutboundFilterTests : OpenSimTestCase
{
    private const string PortToken = "PORT";

    private readonly RecordingHttpServer m_proxy = new();
    private readonly SocketsHttpHandler m_savedRedir;
    private readonly SocketsHttpHandler m_savedNoRedir;
    private readonly ICommandConsole m_savedConsole;
    private XMLRPCModule m_module;

    public XmlRpcOutboundFilterTests()
    {
        // SendRemoteDataRequest runs on a WorkManager thread; WorkManager's type initializer registers a console
        // command, so it needs a console to exist once.
        m_savedConsole = MainConsole.Instance;
        MainConsole.Instance ??= new MockConsole();

        m_savedRedir = WebUtil.SharedSocketsHttpHandler;
        m_savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        WebUtil.SetupHTTPClients(false, false, new WebProxy(m_proxy.BaseUri, false), 4);

        m_proxy.Responder = rec =>
        {
            if (rec.Target.EndsWith("/redir-private", StringComparison.Ordinal))
                return RecordingHttpServer.Reply.Redirect(302, "http://10.0.0.2/x");
            if (rec.Target.EndsWith("/redir-ok", StringComparison.Ordinal))
                return RecordingHttpServer.Reply.Redirect(302, $"http://127.0.0.1:{m_proxy.Port}/final");
            return new RecordingHttpServer.Reply { ContentType = "text/xml", Body = Encoding.UTF8.GetBytes(XmlRpcReply) };
        };
    }

    public override void Dispose()
    {
        m_module?.Close();
        SocketsHttpHandler ours = WebUtil.SharedSocketsHttpHandler;
        SocketsHttpHandler oursNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        WebUtil.SharedSocketsHttpHandler = m_savedRedir;
        WebUtil.SharedSocketsHttpHandlerNoRedir = m_savedNoRedir;
        ours?.Dispose();
        oursNoRedir?.Dispose();
        m_proxy.Dispose();
        MainConsole.Instance = m_savedConsole;
        base.Dispose();
    }

    private const string XmlRpcReply =
        "<?xml version=\"1.0\"?><methodResponse><params><param><value><struct>" +
        "<member><name>StringValue</name><value><string>pong</string></value></member>" +
        "<member><name>IntValue</name><value><i4>7</i4></value></member>" +
        "</struct></value></param></params></methodResponse>";

    private void StartModule(string except = null)
    {
        IConfigSource config = new IniConfigSource();
        IConfig network = config.AddConfig("Network");
        if (except is not null)
            network.Set("OutboundDisallowForUserScriptsExcept", except);
        m_module = new XMLRPCModule();
        m_module.Initialise(config);
    }

    private string ProxyEndpoint => $"127.0.0.1:{m_proxy.Port}";

    private UUID Send(string dest) =>
        m_module.SendRemoteData(1, UUID.Random(), UUID.Random().ToString(), dest.Replace(PortToken, m_proxy.Port.ToString()), 3, "ping");

    private SendRemoteDataRequest WaitForCompletion()
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < end)
        {
            if (m_module.GetNextCompletedSRDRequest() is SendRemoteDataRequest done)
                return done;
            Thread.Sleep(20);
        }
        Assert.Fail("the XML-RPC request never completed");
        return null;
    }

    [Theory]
    [InlineData("http://127.0.0.1:" + PortToken + "/rpc")]
    [InlineData("http://localhost:" + PortToken + "/rpc")]
    [InlineData("http://10.0.0.1/rpc")]
    [InlineData("http://10.255.255.254:8002/rpc")]
    [InlineData("http://172.16.0.1/rpc")]
    [InlineData("http://172.31.255.254:8003/rpc")]
    [InlineData("http://192.168.1.1:9000/rpc")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.64.0.1/rpc")]
    [InlineData("https://10.0.0.1/rpc")]
    public void ALoopbackOrPrivateDestinationIsRefusedAndNothingIsSent(string dest)
    {
        StartModule();
        UUID id = Send(dest);
        Thread.Sleep(500);
        Assert.Equal(UUID.Zero, id);
        Assert.Equal(0, m_proxy.Connections);
        Assert.Null(m_module.GetNextCompletedSRDRequest());
    }

    [Fact]
    public void AnExceptEntryAllowsItsEndpointAndTheReplyComesBack()
    {
        StartModule(except: ProxyEndpoint);
        UUID id = Send("http://127.0.0.1:" + PortToken + "/rpc");
        Assert.NotEqual(UUID.Zero, id);

        SendRemoteDataRequest done = WaitForCompletion();
        Assert.Equal(id, done.ReqID);
        Assert.Equal("pong", done.Sdata);
        Assert.Equal(7, done.Idata);

        RecordingHttpServer.Received rec = Assert.Single(m_proxy.Requests);
        Assert.Equal("POST", rec.Method);
        string body = Encoding.UTF8.GetString(rec.Body);
        Assert.Contains("<string>ping</string>", body);
    }

    [Fact]
    public void AnExceptEntryDoesNotOpenOtherPortsOnTheSameAddress()
    {
        StartModule(except: "127.0.0.1:1");
        Assert.Equal(UUID.Zero, Send("http://127.0.0.1:" + PortToken + "/rpc"));
        Thread.Sleep(500);
        Assert.Equal(0, m_proxy.Connections);
    }

    [Fact]
    public void APublicDestinationIsAllowed()
    {
        StartModule();
        UUID id = Send("http://93.184.215.14/rpc");
        Assert.NotEqual(UUID.Zero, id);

        WaitForCompletion();
        // Arrived at the test's proxy as an absolute-form request; the address itself was never contacted.
        Assert.Equal("http://93.184.215.14/rpc", Assert.Single(m_proxy.Requests).Target);
    }

    [Fact]
    public void ARedirectToAPrivateAddressIsNotFollowed()
    {
        StartModule(except: ProxyEndpoint);
        UUID id = Send("http://127.0.0.1:" + PortToken + "/redir-private");
        Assert.NotEqual(UUID.Zero, id);

        SendRemoteDataRequest done = WaitForCompletion();
        Thread.Sleep(200);
        Assert.DoesNotContain(m_proxy.Targets, t => t.Contains("10.0.0.2"));
        Assert.Single(m_proxy.Requests);
        Assert.Equal("URL from HTTP redirect blocked: http://10.0.0.2/x", done.Sdata);
    }

    [Fact]
    public void ARedirectToAnAllowedAddressIsStillFollowed()
    {
        StartModule(except: ProxyEndpoint);
        UUID id = Send("http://127.0.0.1:" + PortToken + "/redir-ok");
        Assert.NotEqual(UUID.Zero, id);

        SendRemoteDataRequest done = WaitForCompletion();
        Assert.Equal("pong", done.Sdata);
        Assert.Contains(m_proxy.Targets, t => t.EndsWith("/final", StringComparison.Ordinal));
    }
}
