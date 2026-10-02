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

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Scripting.HttpRequest;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;

/// <summary>
/// HTTP_MIMETYPE becomes the request's Content-Type line. A value that is not a media type - above all one with
/// a line break, which would add header lines of the script's choosing - must refuse the request; a valid one must
/// reach the wire exactly as before.
/// </summary>
/// <remarks>
/// The destination is a listener this test runs on 127.0.0.1, which the test's configuration excepts from the
/// outbound filter because the module connects only to addresses the filter allows.
/// </remarks>
[Collection("OutboundProcessWideState")]
public class HttpMimeTypeTests : OpenSimTestCase
{
    private const string ForgedOwner = "00000000-0000-0000-0000-00000000dead";

    private readonly RecordingHttpServer m_server = new();
    private readonly HttpRequestModule m_module = new();
    private readonly Scene m_scene;

    public HttpMimeTypeTests()
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup");
        config.AddConfig("Network").Set("OutboundDisallowForUserScriptsExcept", "127.0.0.1/32");
        m_scene = new SceneHelpers().SetupScene();
        m_module.Initialise(config);
        m_module.AddRegion(m_scene);
        m_module.RegionLoaded(m_scene);
    }

    public override void Dispose()
    {
        m_module.RemoveRegion(m_scene);
        m_module.Close();
        m_server.Dispose();
        base.Dispose();
    }

    private UUID Start(string mimeType, string body = "hello")
    {
        List<string> parameters = new()
        {
            ((int)HttpRequestConstants.HTTP_METHOD).ToString(), "POST",
        };
        if (mimeType is not null)
        {
            parameters.Add(((int)HttpRequestConstants.HTTP_MIMETYPE).ToString());
            parameters.Add(mimeType);
        }
        return m_module.StartHttpRequest(1, UUID.Random(), m_server.BaseUri + "mime", parameters,
            new Dictionary<string, string>(), body);
    }

    private string ContentTypeLineSent()
    {
        Assert.True(m_server.WaitForRequests(1), "the request never reached the test listener");
        m_server.Requests.TryPeek(out RecordingHttpServer.Received rec);
        return rec!.HeaderLines.Single(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
    }

    private void AssertRefusedAndNothingSent(UUID id)
    {
        Assert.Equal(UUID.Zero, id);
        Thread.Sleep(500);
        Assert.Equal(0, m_server.Connections);
    }

    [Theory]
    [InlineData("text/plain\r\nX-SecondLife-Owner-Key: " + ForgedOwner)]
    [InlineData("text/plain\nX-SecondLife-Owner-Key: " + ForgedOwner)]
    [InlineData("text/plain\rX-SecondLife-Owner-Key: " + ForgedOwner)]
    [InlineData("text/plain;charset=utf-8\r\n\r\nsmuggled body")]
    public void ALineBreakInTheMimeTypeRefusesTheRequest(string mimeType)
    {
        AssertRefusedAndNothingSent(Start(mimeType));
    }

    [Fact]
    public void TheInjectionPayloadNeverReachesTheWire()
    {
        UUID id = Start("text/plain\r\nX-SecondLife-Owner-Key: " + ForgedOwner);
        Thread.Sleep(500);
        foreach (RecordingHttpServer.Received rec in m_server.Requests)
            Assert.DoesNotContain(rec.HeaderLines, l => l.Contains(ForgedOwner));
        Assert.Equal(UUID.Zero, id);
    }

    [Theory]
    [InlineData("text/plain\0")]
    [InlineData("text/plain;\u0001x=1")]
    [InlineData("text/plain\u007f")]
    [InlineData("text/plain\u0085")]
    [InlineData("")]
    [InlineData("json")]
    [InlineData("/plain")]
    [InlineData("text/")]
    [InlineData("te xt/plain")]
    [InlineData("text/plain x")]
    public void AValueThatIsNotAMediaTypeRefusesTheRequest(string mimeType)
    {
        AssertRefusedAndNothingSent(Start(mimeType));
    }

    [Theory]
    [InlineData("text/plain;charset=utf-8")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/json")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("text/xml;charset=UTF-8;version=1")]
    [InlineData("application/vnd.api+json")]
    [InlineData("text/plain;\tcharset=utf-8")]
    public void AValidMediaTypeIsSentUnchanged(string mimeType)
    {
        UUID id = Start(mimeType);
        Assert.NotEqual(UUID.Zero, id);
        Assert.Equal("Content-Type: " + mimeType, ContentTypeLineSent());
    }

    [Fact]
    public void TheDefaultMimeTypeIsSentUnchanged()
    {
        UUID id = Start(null);
        Assert.NotEqual(UUID.Zero, id);
        Assert.Equal("Content-Type: text/plain;charset=utf-8", ContentTypeLineSent());
    }

    [Fact]
    public void AnOrdinaryRequestCompletesWithItsResponse()
    {
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("pong");
        UUID id = Start("text/plain;charset=utf-8");
        Assert.NotEqual(UUID.Zero, id);

        IHttpServiceRequest done = null;
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (done is null && DateTime.UtcNow < end)
        {
            done = m_module.GetNextCompletedRequest();
            if (done is null)
                Thread.Sleep(20);
        }
        Assert.NotNull(done);
        Assert.Equal(id, done.ReqID);
        Assert.Equal(200, done.Status);
        Assert.Equal("pong", done.ResponseBody);
        Assert.Equal("POST", m_server.Requests.Single().Method);
    }
}
