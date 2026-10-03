/*
 * Copyright (c) Legion Builds
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
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.CoreModules.Scripting.HttpRequest;
using OpenSim.Region.CoreModules.Scripting.LoadImageURL;
using OpenSim.Region.CoreModules.Scripting.VectorRender;
using OpenSim.Region.CoreModules.Scripting.XMLRPC;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;

/// <summary>
/// Each module that sends a script's HTTP request connects only to an address its filter allows. The filter inside
/// the module (or, for XML-RPC, the one the request carries) is replaced by one with a stand-in name lookup, so a
/// host can answer an allowed address at the early check and a blocked one at the connect. The destination is a
/// listener on 127.0.0.1; a refused request must leave it with no connection, and a control request whose
/// connect-time answer is allowed must reach it, which shows the stand-in lookup is the one the module used.
/// </summary>
/// <remarks>
/// Serial with the other classes in its collection: they share the HttpRequestModule filter, the shared
/// WebUtil handlers and HttpClient.DefaultProxy, all of which this class swaps and restores.
/// </remarks>
[Collection("OutboundProcessWideState")]
public class OutboundWiringTests : OpenSimTestCase
{
    // Outside the default blocked ranges, and never connected to.
    private const string Public = "93.184.215.14";
    private const string PrivateTarget = "10.0.0.2";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static readonly FieldInfo s_httpModuleFilter =
        typeof(HttpRequestModule).GetField("m_outboundUrlFilter", BindingFlags.NonPublic | BindingFlags.Static);

    private readonly RecordingHttpServer m_server = new();
    private readonly RecordingHttpServer m_proxyServer = new();
    private readonly OutboundUrlFilter m_savedHttpModuleFilter;
    private readonly IWebProxy m_savedDefaultProxy;
    private readonly ICommandConsole m_savedConsole;
    private readonly SocketsHttpHandler m_savedRedir;
    private readonly SocketsHttpHandler m_savedNoRedir;
    private HttpRequestModule m_httpModule;
    private Scene m_scene;

    public OutboundWiringTests()
    {
        m_savedHttpModuleFilter = (OutboundUrlFilter)s_httpModuleFilter.GetValue(null);
        m_savedDefaultProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy();   // no proxy: every URL is bypassed

        m_savedConsole = MainConsole.Instance;
        MainConsole.Instance ??= new MockConsole();
        m_savedRedir = WebUtil.SharedSocketsHttpHandler;
        m_savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        WebUtil.SetupHTTPClients(false, false, null, 4);
    }

    public override void Dispose()
    {
        if (m_httpModule is not null)
        {
            m_httpModule.RemoveRegion(m_scene);
            m_httpModule.Close();
        }
        s_httpModuleFilter.SetValue(null, m_savedHttpModuleFilter);
        HttpClient.DefaultProxy = m_savedDefaultProxy;

        SocketsHttpHandler ours = WebUtil.SharedSocketsHttpHandler;
        SocketsHttpHandler oursNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        WebUtil.SharedSocketsHttpHandler = m_savedRedir;
        WebUtil.SharedSocketsHttpHandlerNoRedir = m_savedNoRedir;
        ours?.Dispose();
        oursNoRedir?.Dispose();
        MainConsole.Instance = m_savedConsole;

        m_server.Dispose();
        m_proxyServer.Dispose();
        base.Dispose();
    }

    /// <summary>Stand-in name server: the nth lookup of a host gets the nth answer, the last one repeating.</summary>
    private sealed class Lookup
    {
        private readonly Dictionary<string, IPAddress[][]> m_answers = new();
        private readonly Dictionary<string, int> m_calls = new();

        public Lookup Host(string host, params string[][] answers)
        {
            m_answers[host] = answers.Select(a => a.Select(IPAddress.Parse).ToArray()).ToArray();
            return this;
        }

        public ValueTask<IPAddress[]> Resolve(string host, CancellationToken ct)
        {
            lock (m_calls)
            {
                m_calls.TryGetValue(host, out int n);
                m_calls[host] = n + 1;
                IPAddress[][] answers = m_answers[host];
                return new ValueTask<IPAddress[]>(answers[Math.Min(n, answers.Length - 1)]);
            }
        }
    }

    private static OutboundUrlFilter Filter(Lookup lookup, string except = null)
    {
        IConfigSource config = new IniConfigSource();
        IConfig network = config.AddConfig("Network");
        if (except is not null)
            network.Set("OutboundDisallowForUserScriptsExcept", except);
        return new OutboundUrlFilter("Test", config, lookup.Resolve);
    }

    private static IConfigSource ModuleConfig()
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup");
        config.AddConfig("Network");
        return config;
    }

    private string Url(string host, string path = "/") => $"http://{host}:{m_server.Port}{path}";

    // The rebinding host is "localhost": a request that does not use the stand-in lookup is answered by the machine's
    // own lookup with 127.0.0.1, reaches the listener and fails the test, with no name server involved.

    /// <summary>A host that is allowed at the early check and blocked at the connect.</summary>
    private static Lookup Rebinding() =>
        new Lookup().Host("localhost", new[] { Public }, new[] { "127.0.0.1" });

    /// <summary>
    /// As <see cref="Rebinding"/> for a path that looks the host up once more before the connect: the module's own
    /// early check, then the redirect handler's check of the first URL, then the connect.
    /// </summary>
    private static Lookup RebindingAfterTwoChecks() =>
        new Lookup().Host("localhost", new[] { Public }, new[] { Public }, new[] { "127.0.0.1" });

    /// <summary>A host whose answer is the listener, with an exception that allows it.</summary>
    private static Lookup Control() => new Lookup().Host("localhost", new[] { "127.0.0.1" });

    /// <summary>First hop answers the listener (allowed); the second hop is allowed early and blocked at connect.</summary>
    private static Lookup RedirectToRebinding() => new Lookup()
        .Host("127.0.0.1", new[] { "127.0.0.1" })
        .Host("localhost", new[] { Public }, new[] { PrivateTarget });

    private void RedirectFromListener() =>
        m_server.Responder = _ => RecordingHttpServer.Reply.Redirect(302, Url("localhost", "/next"));

    // ---- llHTTPRequest: HttpRequestModule, both shared clients ----

    /// <summary>
    /// The module builds its shared clients once per process; another test class may have left them (and a job
    /// engine) behind, so a test that needs its own configuration starts them afresh.
    /// </summary>
    private static void ResetHttpModuleStatics()
    {
        HttpRequestModule.m_jobEngine?.Stop();
        HttpRequestModule.m_jobEngine = null;
        foreach (string name in new[] { "VeriFyCertClient", "VeriFyNoCertClient" })
        {
            FieldInfo field = typeof(HttpRequestModule).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            (field.GetValue(null) as HttpClient)?.Dispose();
            field.SetValue(null, null);
        }
        typeof(HttpRequestModule).GetField("m_numberScenes", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, 0);
    }

    /// <param name="viaProxy">Configure the listener in <c>m_proxyServer</c> as the proxy, bypassing "localhost".</param>
    private void StartHttpModule(OutboundUrlFilter filter, bool viaProxy = false)
    {
        ResetHttpModuleStatics();
        IConfigSource config = ModuleConfig();
        if (viaProxy)
        {
            config.Configs["Startup"].Set("HttpProxy", m_proxyServer.BaseUri.AbsoluteUri);
            config.Configs["Startup"].Set("HttpProxyExceptions", "localhost");
        }
        m_scene = new SceneHelpers().SetupScene();
        m_httpModule = new HttpRequestModule();
        m_httpModule.Initialise(config);
        m_httpModule.AddRegion(m_scene);
        m_httpModule.RegionLoaded(m_scene);
        s_httpModuleFilter.SetValue(null, filter);
    }

    private IHttpServiceRequest SendHttp(string url, bool verifyCert)
    {
        List<string> parameters = new()
        {
            ((int)HttpRequestConstants.HTTP_VERIFY_CERT).ToString(), verifyCert ? "1" : "0",
        };
        UUID id = m_httpModule.StartHttpRequest(1, UUID.Random(), url, parameters, new Dictionary<string, string>(), "");
        Assert.NotEqual(UUID.Zero, id);

        DateTime end = DateTime.UtcNow + Limit;
        while (DateTime.UtcNow < end)
        {
            IHttpServiceRequest done = m_httpModule.GetNextCompletedRequest();
            if (done is not null)
                return done;
            Thread.Sleep(20);
        }
        Assert.Fail("the request never completed");
        return null;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HttpRequestModuleRefusesAHostThatTurnsBlockedAtTheConnect(bool verifyCert)
    {
        StartHttpModule(Filter(Rebinding()));
        Uri url = new(Url("localhost"));
        Assert.True(m_httpModule.CheckAllowed(url), "the early check should see the allowed answer");

        IHttpServiceRequest done = SendHttp(url.AbsoluteUri, verifyCert);

        Assert.Equal(499, done.Status);
        Assert.Equal("Request to localhost disallowed by filter", done.ResponseBody);
        Assert.Equal(0, m_server.Connections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HttpRequestModuleConnectsToTheAddressItsFilterAllows(bool verifyCert)
    {
        StartHttpModule(Filter(Control(), except: "127.0.0.1/32"));

        IHttpServiceRequest done = SendHttp(Url("localhost"), verifyCert);

        Assert.Equal(200, done.Status);
        Assert.Single(m_server.Requests);
    }

    [Fact]
    public void HttpRequestModuleRefusesARedirectHopThatTurnsBlockedAtTheConnect()
    {
        StartHttpModule(Filter(RedirectToRebinding(), except: "127.0.0.1/32"));
        RedirectFromListener();

        IHttpServiceRequest done = SendHttp(Url("127.0.0.1", "/start"), verifyCert: true);

        Assert.Equal(499, done.Status);
        Assert.Contains("disallowed by filter", done.ResponseBody);
        Assert.Single(m_server.Requests);
    }

    // A proxy is configured but bypasses "localhost": a request to it goes straight out, so it is judged.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HttpRequestModuleWithAProxyRefusesABypassedHostThatTurnsBlockedAtTheConnect(bool verifyCert)
    {
        StartHttpModule(Filter(Rebinding()), viaProxy: true);
        Assert.True(m_httpModule.CheckAllowed(new Uri(Url("localhost"))), "the early check should see the allowed answer");

        IHttpServiceRequest done = SendHttp(Url("localhost"), verifyCert);

        Assert.Equal(499, done.Status);
        Assert.Equal("Request to localhost disallowed by filter", done.ResponseBody);
        Assert.Equal(0, m_server.Connections);
        Assert.Equal(0, m_proxyServer.Connections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HttpRequestModuleWithAProxyConnectsABypassedHostStraightToItsAllowedAddress(bool verifyCert)
    {
        StartHttpModule(Filter(Control(), except: "127.0.0.1/32"), viaProxy: true);

        IHttpServiceRequest done = SendHttp(Url("localhost"), verifyCert);

        Assert.Equal(200, done.Status);
        Assert.Single(m_server.Requests);
        Assert.Equal(0, m_proxyServer.Connections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HttpRequestModuleSendsARequestTheProxyCarriesToTheProxyWithOnlyTheEarlyCheck(bool verifyCert)
    {
        StartHttpModule(Filter(Rebinding()), viaProxy: true);

        IHttpServiceRequest done = SendHttp("http://93.184.215.14/path", verifyCert);

        Assert.Equal(200, done.Status);
        Assert.Equal("http://93.184.215.14/path", Assert.Single(m_proxyServer.Requests).Target);
        Assert.Equal(0, m_server.Connections);
    }

    // ---- llSendRemoteData: the XML-RPC request ----

    private SendRemoteDataRequest SendXmlRpc(OutboundUrlFilter filter, string dest)
    {
        SendRemoteDataRequest req = new(1, UUID.Random(), UUID.Random().ToString(), dest, 3, "ping") { UrlFilter = filter };
        req.Process();

        DateTime end = DateTime.UtcNow + Limit;
        while (!req.Finished && DateTime.UtcNow < end)
            Thread.Sleep(20);
        Assert.True(req.Finished, "the XML-RPC request never finished");
        return req;
    }

    [Fact]
    public void XmlRpcRefusesAHostThatTurnsBlockedAtTheConnect()
    {
        // The redirect handler's check of the first URL is the early check; the connect is the next lookup.
        SendRemoteDataRequest req = SendXmlRpc(Filter(Rebinding()), Url("localhost", "/rpc"));

        Assert.Contains("disallowed by filter", req.Sdata);
        Assert.Equal(0, m_server.Connections);
    }

    [Fact]
    public void XmlRpcConnectsToTheAddressItsFilterAllows()
    {
        SendXmlRpc(Filter(Control(), except: "127.0.0.1/32"), Url("localhost", "/rpc"));

        Assert.Single(m_server.Requests);
    }

    [Fact]
    public void XmlRpcRefusesARedirectHopThatTurnsBlockedAtTheConnect()
    {
        RedirectFromListener();

        SendRemoteDataRequest req = SendXmlRpc(Filter(RedirectToRebinding(), except: "127.0.0.1/32"), Url("127.0.0.1", "/start"));

        Assert.Contains("disallowed by filter", req.Sdata);
        Assert.Single(m_server.Requests);
    }

    [Fact]
    public void XmlRpcWithAProxyRefusesABypassedHostThatTurnsBlockedAtTheConnect()
    {
        WebUtil.SetupHTTPClients(false, false, new WebProxy(m_proxyServer.BaseUri, true, new[] { "localhost" }), 4);

        SendRemoteDataRequest req = SendXmlRpc(Filter(Rebinding()), Url("localhost", "/rpc"));

        Assert.Contains("disallowed by filter", req.Sdata);
        Assert.Equal(0, m_server.Connections);
        Assert.Equal(0, m_proxyServer.Connections);
    }

    [Fact]
    public void XmlRpcSendsARequestTheProxyCarriesToTheProxy()
    {
        WebUtil.SetupHTTPClients(false, false, new WebProxy(m_proxyServer.BaseUri, true, new[] { "localhost" }), 4);

        SendXmlRpc(Filter(Rebinding()), "http://93.184.215.14/rpc");

        Assert.Equal("http://93.184.215.14/rpc", Assert.Single(m_proxyServer.Requests).Target);
        Assert.Equal(0, m_server.Connections);
    }

    // ---- vector-render image fetch ----

    private VectorRenderModule StartVectorRender(OutboundUrlFilter filter)
    {
        VectorRenderModule module = new();
        module.Initialise(ModuleConfig());
        typeof(VectorRenderModule).GetField("m_outboundUrlFilter", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(module, filter);
        return module;
    }

    private static void Draw(VectorRenderModule module, string url)
    {
        Assert.NotNull(module.ConvertData($"MoveTo 0,0; Image 16,16,{url};", "width:64,height:64"));
    }

    [Fact]
    public void VectorRenderRefusesAHostThatTurnsBlockedAtTheConnect()
    {
        VectorRenderModule module = StartVectorRender(Filter(RebindingAfterTwoChecks()));

        Draw(module, Url("localhost", "/img.png"));

        Assert.Equal(0, m_server.Connections);
        module.Close();
    }

    [Fact]
    public void VectorRenderConnectsToTheAddressItsFilterAllows()
    {
        VectorRenderModule module = StartVectorRender(Filter(Control(), except: "127.0.0.1/32"));

        Draw(module, Url("localhost", "/img.png"));

        Assert.Single(m_server.Requests);
        module.Close();
    }

    [Fact]
    public void VectorRenderRefusesARedirectHopThatTurnsBlockedAtTheConnect()
    {
        VectorRenderModule module = StartVectorRender(Filter(RedirectToRebinding(), except: "127.0.0.1/32"));
        RedirectFromListener();

        Draw(module, Url("127.0.0.1", "/start"));

        Assert.Single(m_server.Requests);
        module.Close();
    }

    [Fact]
    public void VectorRenderRefusesARedirectHopTheProxyBypassesThatTurnsBlockedAtTheConnect()
    {
        // The first URL is carried by the default proxy, which redirects to "localhost", a host it bypasses.
        HttpClient.DefaultProxy = new WebProxy(m_proxyServer.BaseUri, true, new[] { "localhost" });
        m_proxyServer.Responder = _ => RecordingHttpServer.Reply.Redirect(302, Url("localhost", "/next"));
        Lookup lookup = new Lookup()
            .Host(Public, new[] { Public })
            .Host("localhost", new[] { Public }, new[] { "127.0.0.1" });
        VectorRenderModule module = StartVectorRender(Filter(lookup));

        Draw(module, $"http://{Public}/start");

        Assert.Single(m_proxyServer.Requests);
        Assert.Equal(0, m_server.Connections);
        module.Close();
    }

    [Fact]
    public void VectorRenderConnectsARedirectHopTheProxyBypassesStraightToItsAllowedAddress()
    {
        HttpClient.DefaultProxy = new WebProxy(m_proxyServer.BaseUri, true, new[] { "localhost" });
        m_proxyServer.Responder = _ => RecordingHttpServer.Reply.Redirect(302, Url("localhost", "/next"));
        Lookup lookup = new Lookup()
            .Host(Public, new[] { Public })
            .Host("localhost", new[] { Public }, new[] { "127.0.0.1" });
        VectorRenderModule module = StartVectorRender(Filter(lookup, except: "127.0.0.1/32"));

        Draw(module, $"http://{Public}/start");

        Assert.Single(m_proxyServer.Requests);
        Assert.Single(m_server.Requests);
        module.Close();
    }

    // ---- dynamic texture URL loader ----

    private sealed class ReturnedTextures : IDynamicTextureManager
    {
        public readonly TaskCompletionSource Returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReturnData(UUID id, IDynamicTexture texture) => Returned.TrySetResult();
        public void RegisterRender(string handleType, IDynamicTextureRender render) { }
        public UUID AddDynamicTextureURL(UUID simID, UUID primID, string contentType, string url, string extraParams) => throw new NotImplementedException();
        public UUID AddDynamicTextureURL(UUID simID, UUID primID, string contentType, string url, string extraParams, bool SetBlending, byte AlphaValue) => throw new NotImplementedException();
        public UUID AddDynamicTextureURL(UUID simID, UUID primID, string contentType, string url, string extraParams, bool SetBlending, int disp, byte AlphaValue, int face) => throw new NotImplementedException();
        public UUID AddDynamicTextureData(UUID simID, UUID primID, string contentType, string data, string extraParams) => throw new NotImplementedException();
        public UUID AddDynamicTextureData(UUID simID, UUID primID, string contentType, string data, string extraParams, bool SetBlending, byte AlphaValue) => throw new NotImplementedException();
        public UUID AddDynamicTextureData(UUID simID, UUID primID, string contentType, string data, string extraParams, bool SetBlending, int disp, byte AlphaValue, int face) => throw new NotImplementedException();
        public void GetDrawStringSize(string contentType, string text, string fontName, int fontSize, out double xSize, out double ySize) => throw new NotImplementedException();
    }

    /// <summary>Starts a load and waits for the module to hand back its result, however the fetch ended. A null filter keeps the module's own.</summary>
    private static void LoadImage(OutboundUrlFilter filter, string url)
    {
        LoadImageURLModule module = new();
        module.Initialise(ModuleConfig());
        ReturnedTextures textures = new();
        if (filter is not null)
        {
            typeof(LoadImageURLModule).GetField("m_outboundUrlFilter", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(module, filter);
        }
        typeof(LoadImageURLModule).GetField("m_textureManager", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(module, textures);

        Assert.True(module.AsyncConvertUrl(UUID.Random(), url, ""), "the early check should have let the load start");
        Assert.True(textures.Returned.Task.Wait(Limit), "the loader never returned a result");
    }

    [Fact]
    public void ImageUrlLoaderRefusesAHostThatTurnsBlockedAtTheConnect()
    {
        LoadImage(Filter(RebindingAfterTwoChecks()), Url("localhost", "/img.png"));

        Assert.Equal(0, m_server.Connections);
    }

    [Fact]
    public void ImageUrlLoaderConnectsToTheAddressItsFilterAllows()
    {
        LoadImage(Filter(Control(), except: "127.0.0.1/32"), Url("localhost", "/img.png"));

        Assert.Single(m_server.Requests);
    }

    [Fact]
    public void ImageUrlLoaderRefusesARedirectHopThatTurnsBlockedAtTheConnect()
    {
        RedirectFromListener();

        LoadImage(Filter(RedirectToRebinding(), except: "127.0.0.1/32"), Url("127.0.0.1", "/start"));

        Assert.Single(m_server.Requests);
    }

    [Fact]
    public void ImageUrlLoaderLeavesOnlyTheEarlyCheckWhenTheDefaultProxyCarriesTheUrl()
    {
        // The default proxy is the listener, which the module's own filter blocks (127.0.0.1): were the connection
        // to the proxy judged, it would be refused. The request arrives in absolute form.
        HttpClient.DefaultProxy = new WebProxy(m_server.BaseUri, false);

        LoadImage(null, "http://93.184.215.14/img.png");

        Assert.Equal("http://93.184.215.14/img.png", Assert.Single(m_server.Requests).Target);
    }
}
