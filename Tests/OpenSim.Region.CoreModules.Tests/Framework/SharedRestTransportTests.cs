using System.Net;
using System.Text;
using System.Xml.Serialization;
using Nini.Config;
using OpenSim.ConsoleClient;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Framework;

[Collection("OutboundProcessWideState")]
public sealed class SharedRestTransportTests : IDisposable
{
    private readonly RecordingHttpServer m_server = new();
    private readonly SocketsHttpHandler m_savedRedir = WebUtil.SharedSocketsHttpHandler;
    private readonly SocketsHttpHandler m_savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;

    public SharedRestTransportTests()
    {
        WebUtil.SetupHTTPClients(false, false, null, 4);
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("<string>reply</string>");
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
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task AsyncRequester_PreservesAuthorizationBodyAndExactlyOneCallback(string verb)
    {
        IniConfigSource config = new();
        config.AddConfig("Network");
        config.Configs["Network"].Set("HttpAuthUsername", "test-user");
        config.Configs["Network"].Set("HttpAuthPassword", "test-password");
        BasicHttpAuthentication auth = new(config, "Network");
        int callbacks = 0;
        string? reply = null;

        await AsynchronousRestObjectRequester.MakeRequestAsync<string, string>(
            verb, m_server.BaseUri.ToString(), "payload", value => { callbacks++; reply = value; }, 8, auth);

        Assert.Equal(1, callbacks);
        Assert.Equal("reply", reply);
        RecordingHttpServer.Received request = Assert.Single(m_server.Requests);
        Assert.Equal(verb, request.Method);
        Assert.Contains("Authorization: Basic " + auth.Credentials, request.HeaderBlock);
        if (verb == "POST")
        {
            Assert.Contains("Content-Type: text/xml", request.HeaderBlock);
            Assert.Equal("payload", Deserialize<string>(request.Body));
        }
        else
            Assert.Empty(request.Body);
    }

    [Theory]
    [InlineData("GET", 404)]
    [InlineData("GET", 500)]
    [InlineData("POST", 500)]
    public async Task AsyncRequester_FailedStatusCompletesWithOneDefaultCallback(string verb, int status)
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = status };
        int callbacks = 0;
        string? reply = "not default";
        await AsynchronousRestObjectRequester.MakeRequestAsync<string, string>(
            verb, m_server.BaseUri.ToString(), "payload", value => { callbacks++; reply = value; });
        Assert.Equal(1, callbacks);
        Assert.Null(reply);
    }

    [Fact]
    public async Task AsyncRequester_InvalidXmlAndCallbackExceptionAreObserved()
    {
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("not XML");
        int callbacks = 0;
        string? reply = "not default";
        await AsynchronousRestObjectRequester.MakeRequestAsync<string, string>(
            "POST", m_server.BaseUri.ToString(), "payload", value =>
            {
                callbacks++;
                reply = value;
                throw new InvalidOperationException("callback test failure");
            });
        Assert.Equal(1, callbacks);
        Assert.Null(reply);
    }

    [Fact]
    public async Task XmlHelper_DebugLoggingSupportsResponsesLargerThanOneBlock()
    {
        string value = new('a', 10000);
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("<string>" + value + "</string>");
        int savedLevel = WebUtil.DebugLevel;
        try
        {
            WebUtil.DebugLevel = 5;
            Assert.Equal(value, await WebUtil.SendXmlRequestAsync<string, string>(
                "POST", m_server.BaseUri.ToString(), "payload", 1000));
        }
        finally
        {
            WebUtil.DebugLevel = savedLevel;
        }
    }

    [Fact]
    public async Task XmlHelper_TimeoutIncludesResponseBody()
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply
        {
            Body = Encoding.UTF8.GetBytes("<string>late</string>"),
            BodyDelayMilliseconds = 500
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WebUtil.SendXmlRequestAsync<string, string>("POST", m_server.BaseUri.ToString(), "payload", 100));
    }

    [Fact]
    public async Task RestPosters_PreserveXmlAndCallback()
    {
        await RestObjectPoster.PostObjectAsync("PUT", m_server.BaseUri.ToString(), "payload");
        Assert.Equal("PUT", Assert.Single(m_server.Requests).Method);
        string? reply = null;
        RestObjectPosterResponse<string> poster = new() { ResponseCallback = value => reply = value };
        await poster.PostObjectAsync("POST", m_server.BaseUri.ToString(), "payload");
        Assert.Equal("reply", reply);
        Assert.Equal(2, m_server.Requests.Count);
    }

    [Theory]
    [InlineData("<string>reply</string>", 500)]
    [InlineData("invalid XML", 200)]
    public async Task RestPoster_DoesNotCallSuccessCallbackOnFailure(string body, int status)
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = status, Body = Encoding.UTF8.GetBytes(body) };
        int callbacks = 0;
        RestObjectPosterResponse<string> poster = new() { ResponseCallback = _ => callbacks++ };
        await poster.PostObjectAsync("POST", m_server.BaseUri.ToString(), "payload");
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task SessionPosters_PreserveSessionAndAvatarEnvelope()
    {
        string? reply = null;
        RestSessionObjectPosterResponse<string, string> poster = new() { ResponseCallback = value => reply = value };
        await poster.PostObjectAsync("POST", m_server.BaseUri.ToString(), "payload", "session", "avatar");
        Assert.Equal("reply", reply);
        RestSessionObject<string> envelope = Deserialize<RestSessionObject<string>>(Assert.Single(m_server.Requests).Body);
        Assert.Equal("session", envelope.SessionID);
        Assert.Equal("avatar", envelope.AvatarID);
        Assert.Equal("payload", envelope.Body);

        Assert.Equal("reply", SynchronousRestSessionObjectPoster<string, string>.BeginPostObject(
            "POST", m_server.BaseUri.ToString(), "payload", "session", "avatar"));
    }

    [Fact]
    public void SynchronousSessionPoster_PropagatesFailedHttpStatus()
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 403 };
        HttpRequestException error = Assert.Throws<HttpRequestException>(() =>
            SynchronousRestSessionObjectPoster<string, string>.BeginPostObject(
                "POST", m_server.BaseUri.ToString(), "payload", "session", "avatar"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task ConsoleRequester_PreservesFormEncodingAndCallbackArguments()
    {
        string url = m_server.BaseUri.ToString();
        const string data = "ID=session&COMMAND=show%20stats";
        int callbacks = 0;
        string? receivedUrl = null, receivedData = null, receivedReply = null;
        await Requester.MakeRequestAsync(url, data, (requestUrl, requestData, reply) =>
        {
            callbacks++;
            receivedUrl = requestUrl;
            receivedData = requestData;
            receivedReply = reply;
        });
        Assert.Equal(1, callbacks);
        Assert.Equal(url, receivedUrl);
        Assert.Equal(data, receivedData);
        Assert.Equal("<string>reply</string>", receivedReply);
        RecordingHttpServer.Received request = Assert.Single(m_server.Requests);
        Assert.Equal(data, Encoding.ASCII.GetString(request.Body));
        Assert.Contains("Content-Type: application/x-www-form-urlencoded", request.HeaderBlock);
    }

    [Fact]
    public async Task LegacyTransport_UsesSystemProxyWithoutChangingDirectTransport()
    {
        IWebProxy savedProxy = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy(m_server.BaseUri, false);
            using var legacy = WebUtil.CreateLegacyHttpHandler();
            Assert.True(legacy.UseProxy);
            Assert.Same(HttpClient.DefaultProxy, legacy.Proxy);
            Assert.False(WebUtil.SharedSocketsHttpHandler.UseProxy);
            Assert.False(WebUtil.SharedSocketsHttpHandlerNoRedir.UseProxy);
            Assert.Equal("reply", await WebUtil.SendXmlRequestAsync<string, string>(
                "POST", "http://unresolvable.invalid/resource", "payload", 1000));
            Assert.Contains("http://unresolvable.invalid/resource", Assert.Single(m_server.Requests).RequestLine);
        }
        finally
        {
            HttpClient.DefaultProxy = savedProxy;
        }
    }

    [Theory]
    [InlineData("/StartSession/", "Login failed")]
    [InlineData("/ReadResponses/session/", "Polling stopped")]
    [InlineData("/SessionCommand/", "Request failed")]
    public async Task ConsoleRequester_FailuresAreVisibleWithoutLoggerAndNeverInvokeSuccess(string path, string message)
    {
        var savedFactory = LoggerProvider.LoggerFactory;
        TextWriter savedError = Console.Error;
        using StringWriter error = new();
        try
        {
            LoggerProvider.LoggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
            Console.SetError(error);
            m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 503 };
            int callbacks = 0;
            await Requester.MakeRequestAsync(new Uri(m_server.BaseUri, path).ToString(),
                "PASS=do-not-print-this", (_, _, _) => callbacks++);
            Assert.Equal(0, callbacks);
            Assert.Contains(message, error.ToString());
            Assert.Contains("503", error.ToString());
            Assert.DoesNotContain("do-not-print-this", error.ToString());
            Assert.Single(m_server.Requests);
        }
        finally
        {
            Console.SetError(savedError);
            LoggerProvider.LoggerFactory = savedFactory;
        }
    }

    [Fact]
    public async Task XmlHelper_UsesConfiguredProxy()
    {
        WebUtil.SetupHTTPClients(false, false, new WebProxy(m_server.BaseUri, false), 4);
        Assert.Equal("reply", await WebUtil.SendXmlRequestAsync<string, string>(
            "POST", "http://unresolvable.invalid/resource", "payload", 1000));
        Assert.Contains("http://unresolvable.invalid/resource", Assert.Single(m_server.Requests).RequestLine);
    }

    [Fact]
    public async Task AsyncRequester_HonorsHigherConnectionLimitWithoutChangingSharedHandler()
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply
        {
            Body = Encoding.UTF8.GetBytes("<string>reply</string>"),
            BodyDelayMilliseconds = 5000
        };
        int callbacks = 0;
        Task[] requests = Enumerable.Range(0, 6).Select(_ =>
            AsynchronousRestObjectRequester.MakeRequestAsync<string, string>(
                "POST", m_server.BaseUri.ToString(), "payload",
                value => { if (value == "reply") Interlocked.Increment(ref callbacks); }, 6)).ToArray();
        bool allConnectedBeforeBodiesArrived = m_server.WaitForRequests(6, 3000);
        await Task.WhenAll(requests);
        Assert.True(allConnectedBeforeBodiesArrived);
        Assert.Equal(6, callbacks);
        Assert.Equal(4, WebUtil.SharedSocketsHttpHandler.MaxConnectionsPerServer);
    }

    private static T Deserialize<T>(byte[] body)
    {
        using MemoryStream stream = new(body);
        return (T)new XmlSerializer(typeof(T)).Deserialize(stream)!;
    }
}
