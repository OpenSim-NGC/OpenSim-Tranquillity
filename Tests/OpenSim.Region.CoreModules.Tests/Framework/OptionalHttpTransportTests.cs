using System.Collections;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Gloebit.GloebitMoneyModule;
using Nwc.XmlRpc;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;
using OpenSim.Region.OptionalModules.Avatar.Concierge;
using OpenSim.Region.OptionalModules.Avatar.Voice.FreeSwitchVoice;
using OpenSim.Region.OptionalModules.Avatar.Voice.VivoxVoice;
using OpenSim.Region.OptionalModules.World.Currency;
using OpenSim.Services.Connectors;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Framework;

[Collection("OutboundProcessWideState")]
public sealed class OptionalHttpTransportTests : IDisposable
{
    private const string RpcReply = "<methodResponse><params><param><value><string>reply</string></value></param></params></methodResponse>";
    private readonly RecordingHttpServer m_server = new();
    private readonly SocketsHttpHandler m_savedRedir = WebUtil.SharedSocketsHttpHandler;
    private readonly SocketsHttpHandler m_savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;

    public OptionalHttpTransportTests()
    {
        WebUtil.SetupHTTPClients(false, false, null, 4);
    }

    public void Dispose()
    {
        m_server.Dispose();
        WebUtil.SharedSocketsHttpHandler.Dispose();
        WebUtil.SharedSocketsHttpHandlerNoRedir.Dispose();
        WebUtil.SharedSocketsHttpHandler = m_savedRedir;
        WebUtil.SharedSocketsHttpHandlerNoRedir = m_savedNoRedir;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GroupsRpc_PreservesMethodPayloadAndKeepAliveOption(bool disableKeepAlive)
    {
        m_server.Responder = _ => RecordingHttpServer.Reply.Text(RpcReply);
        ConfigurableKeepAliveXmlRpcRequest request = new("test.method", new ArrayList { "payload" }, disableKeepAlive);
        Assert.Equal("reply", request.Send(m_server.BaseUri.ToString()).Value);
        RecordingHttpServer.Received received = Assert.Single(m_server.Requests);
        Assert.Contains("<methodName>test.method</methodName>", Encoding.ASCII.GetString(received.Body));
        Assert.Contains("Content-Type: text/xml", received.HeaderBlock);
        Assert.Equal(disableKeepAlive, received.HeaderBlock.Contains("Connection: close", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GroupsRpc_FailureRetainsRawResponseForDiagnostics()
    {
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("invalid XML");
        ConfigurableKeepAliveXmlRpcRequest request = new("test.method", new ArrayList(), true);
        Assert.ThrowsAny<Exception>(() => request.Send(m_server.BaseUri.ToString()));
        Assert.Equal("invalid XML", request.RequestResponse);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MoneyRpc_PreservesUtf8UserAgentAndVerificationHeader(bool checkCertificate)
    {
        m_server.Responder = _ => RecordingHttpServer.Reply.Text(RpcReply);
        NSLXmlRpcRequest request = new("test.method", new ArrayList { "caf\u00e9" });
        Assert.Equal("reply", request.certSend(m_server.BaseUri.ToString(), null, checkCertificate, 1000).Value);
        RecordingHttpServer.Received received = Assert.Single(m_server.Requests);
        Assert.Contains("caf\u00e9", Encoding.UTF8.GetString(received.Body));
        Assert.Contains("User-Agent: NSLXmlRpcRequest", received.HeaderBlock);
        Assert.Equal(!checkCertificate, received.HeaderBlock.Contains("NoVerifyCert: true"));
    }

    [Fact]
    public void MoneyRpc_FailedHttpStatusPropagatesInsteadOfDereferencingNullResponse()
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 500 };
        NSLXmlRpcRequest request = new("test.method", new ArrayList());
        Assert.Throws<HttpRequestException>(() => request.certSend(m_server.BaseUri.ToString(), null, true, 1000));
    }

    [Fact]
    public async Task Concierge_PostsUtf8WithCorrectByteLengthAndObservesErrors()
    {
        TestConcierge module = new();
        const string payload = "<avatars>caf\u00e9</avatars>";
        await module.Post(m_server.BaseUri.ToString(), payload);
        RecordingHttpServer.Received received = Assert.Single(m_server.Requests);
        Assert.Equal(payload, Encoding.UTF8.GetString(received.Body));
        Assert.Contains("Content-Length: " + Encoding.UTF8.GetByteCount(payload), received.HeaderBlock);
        Assert.Contains("User-Agent: OpenSim.Concierge", received.HeaderBlock);
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 500 };
        await module.Post(m_server.BaseUri.ToString(), payload);
        Assert.Equal(2, m_server.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FreeSwitch_UsesExplicitOrSystemProxyAndPreservesResponseAndBody(bool explicitProxy)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using X509Certificate2 certificate = CreateCertificate("server");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task<(string Headers, byte[] Body, string? ClientCertificate)> server =
            ServeTls(listener, certificate, "voice reply", proxy: true, requireClientCertificate: false, cts.Token);
        IWebProxy savedProxy = HttpClient.DefaultProxy;
        WebProxy proxy = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", false);
        HttpClient.DefaultProxy = proxy;
        WebUtil.SetupHTTPClients(false, false, explicitProxy ? proxy : null, 4);
        FreeSwitchVoiceModule module = new();
        try
        {
            Hashtable response = module.ForwardProxyRequest(new Hashtable
            {
                ["body"] = "voice=request", ["http-method"] = "POST",
                ["content-type"] = "application/x-www-form-urlencoded", ["uri"] = "/api/test"
            });
            Assert.Equal("voice reply", response["str_response_string"]);
            Assert.Equal(200, response["int_response_code"]);
            Assert.Equal("text/xml", response["content_type"]);
            var received = await server;
            Assert.Contains("POST /api2/test", received.Headers);
            Assert.Equal("voice=request", Encoding.UTF8.GetString(received.Body));
        }
        finally
        {
            module.Close();
            HttpClient.DefaultProxy = savedProxy;
        }
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("money")]
    [InlineData("concierge")]
    [InlineData("gloebit")]
    public async Task OptionalLegacyRequests_UseSystemProxyWithoutEnablingSharedProxy(string transport)
    {
        IWebProxy savedProxy = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy(m_server.BaseUri, false);
            const string url = "http://unresolvable.invalid/";
            m_server.Responder = _ => RecordingHttpServer.Reply.Text(RpcReply);
            switch (transport)
            {
                case "groups":
                    Assert.Equal("reply", new ConfigurableKeepAliveXmlRpcRequest(
                        "test.method", new ArrayList(), false).Send(url).Value);
                    break;
                case "money":
                    Assert.Equal("reply", new NSLXmlRpcRequest(
                        "test.method", new ArrayList()).certSend(url, null, true, 1000).Value);
                    break;
                case "concierge":
                    await new TestConcierge().Post(url, "<avatars/>");
                    break;
                case "gloebit":
                    m_server.Responder = _ => RecordingHttpServer.Reply.Text("{\"success\":true,\"balance\":42.5}");
                    GloebitAPI api = new("test-app", "", "test-secret", new Uri(url), null);
                    Assert.Equal(42.5, api.GetBalance(new GloebitUser { GloebitToken = "" }, out _));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(transport));
            }
            Assert.Contains("http://unresolvable.invalid/", Assert.Single(m_server.Requests).RequestLine);
            Assert.False(WebUtil.SharedSocketsHttpHandler.UseProxy);
        }
        finally
        {
            HttpClient.DefaultProxy = savedProxy;
        }
    }

    [Fact]
    public void LegacyCertificatePolicies_KeepStrictFetchesSeparateFromSharedBypasses()
    {
        WebUtil.SetupHTTPClients(true, true, null, 4);
        var sharedCallback = WebUtil.SharedSocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback;
        using var legacy = WebUtil.CreateLegacyHttpHandler();
        using var strict = WebUtil.CreateLegacyHttpHandler(verifyCertificate: true);
        Assert.Same(sharedCallback, legacy.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(strict.SslOptions.RemoteCertificateValidationCallback);
        Assert.Same(sharedCallback, WebUtil.SharedSocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public async Task Helo_RejectsUntrustedCertificateEvenWhenSharedTransportBypassesVerification()
    {
        WebUtil.SetupHTTPClients(true, true, null, 4);
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using X509Certificate2 certificate = CreateCertificate("wrong-host");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task server = RejectTls();
        string url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        Assert.Equal(string.Empty, new HeloServicesConnector(url).Helo());
        await server;

        async Task RejectTls()
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
            using SslStream stream = new(client.GetStream());
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12
                }, cts.Token);
                await ReadHeaders(stream, cts.Token);
            });
        }
    }

    [Fact]
    public async Task MoneyRpc_SendsClientCertificateWithoutChangingSharedCertificatePolicy()
    {
        WebUtil.SetupHTTPClients(true, true, null, 4);
        var sharedCallback = WebUtil.SharedSocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using X509Certificate2 serverCertificate = CreateCertificate("server");
        using X509Certificate2 clientCertificate = CreateCertificate("client");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        var server = ServeTls(listener, serverCertificate, RpcReply, proxy: false, requireClientCertificate: true, cts.Token);
        NSLXmlRpcRequest request = new("test.method", new ArrayList());
        string url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        Assert.Equal("reply", request.certSend(url, clientCertificate, true, 5000).Value);
        Assert.Equal(clientCertificate.GetCertHashString(), (await server).ClientCertificate);
        Assert.Same(sharedCallback, WebUtil.SharedSocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(WebUtil.SharedSocketsHttpHandler.SslOptions.ClientCertificates);
    }

    [Fact]
    public void Vivox_ParsesResponseAgainstLocalEndpoint()
    {
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("<response><status>OK</status></response>");
        VivoxVoiceModule module = new();
        try
        {
            typeof(VivoxVoiceModule).GetField("m_Lock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(module, new object());
            MethodInfo method = typeof(VivoxVoiceModule).GetMethod("VivoxCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var response = Assert.IsType<System.Xml.XmlElement>(method.Invoke(module, new object[] { m_server.BaseUri.ToString(), false }));
            Assert.Equal("response", response.Name);
            Assert.Equal("OK", response["status"]!.InnerText);
        }
        finally
        {
            module.Close();
        }
    }

    [Fact]
    public void GloebitBalance_ParsesSuccessAndPropagatesHttpFailure()
    {
        GloebitAPI api = new("test-app", "", "test-secret", m_server.BaseUri, null);
        GloebitUser user = new() { GloebitToken = "" };
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("{\"success\":true,\"balance\":42.5}");
        Assert.Equal(42.5, api.GetBalance(user, out bool invalidated));
        Assert.False(invalidated);
        Assert.Equal("/balance", Assert.Single(m_server.Requests).Target);
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 503 };
        Assert.Throws<HttpRequestException>(() => api.GetBalance(user, out _));
    }

    [Theory]
    [InlineData(200, "{\"success\":false,\"reason\":\"declined\",\"description\":\"caf\u00e9\"}", 1)]
    [InlineData(500, "{\"success\":true}", 0)]
    [InlineData(200, "not JSON", 0)]
    [InlineData(200, "[]", 0)]
    [InlineData(200, "", 0)]
    public async Task GloebitTransport_InvokesCompletionExactlyOnceOnlyForValidSuccessfulHttpResponse(
        int status, string body, int expectedCallbacks)
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = status, Body = Encoding.UTF8.GetBytes(body) };
        GloebitAPI api = new("test-app", "", "test-secret", m_server.BaseUri, null);
        Type stateType = typeof(GloebitAPI).GetNestedType("GloebitRequestState", BindingFlags.NonPublic)!;
        Type callbackType = typeof(GloebitAPI).GetNestedType("CompletionCallback", BindingFlags.NonPublic)!;
        int callbacks = 0;
        OSDMap? result = null;
        Action<OSDMap> callback = map => { callbacks++; result = map; };
        Delegate continuation = Delegate.CreateDelegate(callbackType, callback.Target, callback.Method);
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(m_server.BaseUri, "test-payment"))
        {
            Content = new StringContent("{\"amount\":10}", Encoding.UTF8, "application/json")
        };
        object state = Activator.CreateInstance(stateType, request, continuation)!;
        MethodInfo method = typeof(GloebitAPI).GetMethod("SendGloebitRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(method.Invoke(api, new[] { state }));
        Assert.Equal(expectedCallbacks, callbacks);
        if (expectedCallbacks == 1)
        {
            Assert.NotNull(result);
            Assert.False(result["success"].AsBoolean());
            Assert.Equal("caf\u00e9", result["description"].AsString());
        }
    }

    [Fact]
    public async Task GloebitBuilder_PreservesFormAndJsonBodiesWithoutStartingNetworkIo()
    {
        GloebitAPI api = new("test-app", "", "test-secret", m_server.BaseUri, null);
        MethodInfo build = typeof(GloebitAPI).GetMethod("BuildGloebitRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        OSDMap parameters = new() { ["message"] = OSD.FromString("a&b caf\u00e9") };
        foreach (string contentType in new[] { "application/json", "application/x-www-form-urlencoded" })
        {
            using var request = Assert.IsType<HttpRequestMessage>(build.Invoke(api,
                new object?[] { "local-payment", "POST", null, contentType, parameters }));
            Assert.Equal(contentType, request.Content!.Headers.ContentType!.MediaType);
            string body = await request.Content.ReadAsStringAsync();
            if (contentType == "application/json")
                Assert.Equal("a&b caf\u00e9", Assert.IsType<OSDMap>(OSDParser.DeserializeJson(body))["message"].AsString());
            else
                Assert.Contains("message=a%26b+caf", body);
        }
        Assert.Empty(m_server.Requests);
    }

    private sealed class TestConcierge : ConciergeModule
    {
        public Task Post(string uri, string payload) => PostBrokerUpdateAsync(uri, payload);
    }

    private static X509Certificate2 CreateCertificate(string name)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    private static async Task<(string Headers, byte[] Body, string? ClientCertificate)> ServeTls(
        TcpListener listener, X509Certificate2 certificate, string reply, bool proxy,
        bool requireClientCertificate, CancellationToken cancellation)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellation);
        using NetworkStream network = client.GetStream();
        if (proxy)
        {
            string connect = await ReadHeaders(network, cancellation);
            Assert.Contains("CONNECT www.bhr.vivox.com:443", connect);
            await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancellation);
        }
        using SslStream stream = new(network, leaveInnerStreamOpen: true,
            (_, _, _, _) => true);
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls12,
            ClientCertificateRequired = requireClientCertificate
        }, cancellation);
        string headers = await ReadHeaders(stream, cancellation);
        int length = 0;
        foreach (string line in headers.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[15..].Trim());
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellation);
        byte[] data = Encoding.UTF8.GetBytes(reply);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/xml\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"), cancellation);
        await stream.WriteAsync(data, cancellation);
        return (headers, body, stream.RemoteCertificate?.GetCertHashString());
    }

    private static async Task<string> ReadHeaders(Stream stream, CancellationToken cancellation)
    {
        List<byte> bytes = new();
        byte[] buffer = new byte[1];
        while (bytes.Count < 65536)
        {
            await stream.ReadExactlyAsync(buffer, cancellation);
            bytes.Add(buffer[0]);
            int count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 &&
                bytes[count - 2] == 13 && bytes[count - 1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Test request headers exceeded the limit.");
    }
}
