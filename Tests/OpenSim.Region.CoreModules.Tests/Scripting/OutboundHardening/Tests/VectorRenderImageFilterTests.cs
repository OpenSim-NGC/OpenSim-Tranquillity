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
using Nini.Config;
using OpenSim.Region.CoreModules.Scripting.VectorRender;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using SkiaSharp;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;

/// <summary>
/// The vector-render "Image w,h,url" draw command (osSetDynamicTextureData* with content type "vector") fetches
/// its URL through the same outbound URL filter as llHTTPRequest, redirects included.
/// </summary>
/// <remarks>
/// HttpClient.DefaultProxy is pointed at a recording proxy on 127.0.0.1 for each test and restored afterwards, so
/// every fetch the module makes - to any address, by any version of the module - lands at that proxy and is
/// answered there. Nothing leaves this machine.
/// </remarks>
[Collection("OutboundProcessWideState")]
public class VectorRenderImageFilterTests : OpenSimTestCase
{
    private const string PortToken = "PORT";

    private readonly RecordingHttpServer m_proxy = new();
    private readonly IWebProxy m_savedDefaultProxy;
    private VectorRenderModule m_module = null!;
    private static readonly byte[] s_png = MakePng();

    public VectorRenderImageFilterTests()
    {
        m_savedDefaultProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy(m_proxy.BaseUri, false);

        m_proxy.Responder = rec =>
        {
            if (rec.Target.EndsWith("/redir-private", StringComparison.Ordinal))
                return RecordingHttpServer.Reply.Redirect(302, "http://10.0.0.2/x.png");
            if (rec.Target.EndsWith("/redir-ok", StringComparison.Ordinal))
                return RecordingHttpServer.Reply.Redirect(302, $"http://127.0.0.1:{m_proxy.Port}/final.png");
            return new RecordingHttpServer.Reply { ContentType = "image/png", Body = s_png };
        };
    }

    public override void Dispose()
    {
        m_module?.Close();
        HttpClient.DefaultProxy = m_savedDefaultProxy;
        m_proxy.Dispose();
        base.Dispose();
    }

    private static byte[] MakePng()
    {
        using SKBitmap bmp = new(4, 4);
        bmp.Erase(SKColors.Red);
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void StartModule(string? except = null)
    {
        IConfigSource config = new IniConfigSource();
        IConfig network = config.AddConfig("Network");
        if (except is not null)
            network.Set("OutboundDisallowForUserScriptsExcept", except);
        m_module = new VectorRenderModule();
        m_module.Initialise(config);
    }

    private string ProxyEndpoint => $"127.0.0.1:{m_proxy.Port}";

    private IDynamicTexture Draw(string url)
    {
        url = url.Replace(PortToken, m_proxy.Port.ToString());
        IDynamicTexture tex = m_module.ConvertData($"MoveTo 0,0; Image 16,16,{url};", "width:64,height:64");
        Assert.NotNull(tex);
        return tex;
    }

    [Theory]
    [InlineData("http://127.0.0.1:" + PortToken + "/img.png")]
    [InlineData("http://localhost:" + PortToken + "/img.png")]
    [InlineData("http://10.0.0.1/img.png")]
    [InlineData("http://172.16.0.1/img.png")]
    [InlineData("http://172.31.255.254:8003/img.png")]
    [InlineData("http://192.168.1.1:9000/img.png")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.64.0.1/img.png")]
    [InlineData("https://10.0.0.1/img.png")]
    public void ALoopbackOrPrivateImageUrlIsNotFetched(string url)
    {
        StartModule();
        Draw(url);
        Thread.Sleep(200);
        Assert.Equal(0, m_proxy.Connections);
    }

    [Fact]
    public void AnExceptEntryAllowsItsEndpoint()
    {
        StartModule(except: ProxyEndpoint);
        Draw("http://127.0.0.1:" + PortToken + "/img.png");
        Assert.Equal("GET", Assert.Single(m_proxy.Requests).Method);
    }

    [Fact]
    public void APublicImageUrlIsFetched()
    {
        StartModule();
        Draw("http://93.184.215.14/img.png");
        // Arrived at the test's proxy as an absolute-form request; the address itself was never contacted.
        Assert.Equal("http://93.184.215.14/img.png", Assert.Single(m_proxy.Requests).Target);
    }

    [Fact]
    public void ARedirectToAPrivateAddressIsNotFollowed()
    {
        StartModule(except: ProxyEndpoint);
        Draw("http://127.0.0.1:" + PortToken + "/redir-private");
        Thread.Sleep(200);
        Assert.DoesNotContain(m_proxy.Targets, t => t.Contains("10.0.0.2"));
        Assert.Single(m_proxy.Requests);
    }

    [Fact]
    public void ARedirectToAnAllowedAddressIsStillFollowed()
    {
        StartModule(except: ProxyEndpoint);
        Draw("http://127.0.0.1:" + PortToken + "/redir-ok");
        Assert.Contains(m_proxy.Targets, t => t.EndsWith("/final.png", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImagelessDrawingIsUnchanged()
    {
        StartModule();
        IDynamicTexture tex = m_module.ConvertData("MoveTo 0,0; LineTo 10,10;", "width:64,height:64");
        Assert.NotNull(tex);
        Assert.NotNull(tex.Data);
        Assert.Equal(0, m_proxy.Connections);
    }
}
