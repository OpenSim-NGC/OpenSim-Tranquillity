using System.Net;
using System.Text;
using Nini.Config;
using OpenSim.ApplicationPlugins.LoadRegions;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Services.Connectors;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Framework;

[Collection("OutboundProcessWideState")]
public sealed class HttpDownloadTests : IDisposable
{
    private readonly RecordingHttpServer m_server = new();
    private readonly SocketsHttpHandler m_savedRedir = WebUtil.SharedSocketsHttpHandler;
    private readonly SocketsHttpHandler m_savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;

    public HttpDownloadTests()
    {
        WebUtil.SetupHTTPClients(false, false, null, 1);
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
    [InlineData(false)]
    [InlineData(true)]
    public void ArchiveFetch_StreamsKnownAndUnknownLengthBodies(bool unknownLength)
    {
        byte[] data = Encoding.UTF8.GetBytes(new string('a', 1500000));
        m_server.Responder = _ => new RecordingHttpServer.Reply { Body = data, OmitContentLength = unknownLength };
        using Stream stream = ArchiveHelpers.URIFetch(m_server.BaseUri);
        using MemoryStream result = new();
        stream.CopyTo(result);
        Assert.Equal(data, result.ToArray());
        Assert.Contains("Connection: close", Assert.Single(m_server.Requests).HeaderBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchiveFetch_RejectsEmptyAndFailedResponses()
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply();
        Assert.Throws<IOException>(() => ArchiveHelpers.URIFetch(m_server.BaseUri));
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 404 };
        HttpRequestException error = Assert.Throws<HttpRequestException>(() => ArchiveHelpers.URIFetch(m_server.BaseUri));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task ArchiveFetch_EarlyDisposalReleasesConnectionSlot()
    {
        m_server.Responder = _ => new RecordingHttpServer.Reply
        {
            Body = Encoding.UTF8.GetBytes("body"),
            BodyDelayMilliseconds = 10000
        };
        Stream first = ArchiveHelpers.URIFetch(m_server.BaseUri);
        first.Dispose();
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("second");
        await Task.Run(() =>
        {
            using Stream second = ArchiveHelpers.URIFetch(m_server.BaseUri);
            using StreamReader reader = new(second);
            Assert.Equal("second", reader.ReadToEnd());
        }).WaitAsync(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void DownloadFile_ReplacesOnlyAfterCompleteTransferAndCleansPartialFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "opensim-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string filename = Path.Combine(directory, "tile.jpg");
        try
        {
            File.WriteAllText(filename, "original");
            m_server.Responder = _ => new RecordingHttpServer.Reply
            {
                Body = Encoding.UTF8.GetBytes("truncated"),
                ContentLength = 100
            };
            Assert.Throws<HttpIOException>(() => WebUtil.DownloadFile(m_server.BaseUri.ToString(), filename));
            Assert.Equal("original", File.ReadAllText(filename));
            Assert.Single(Directory.GetFiles(directory));

            m_server.Responder = _ => RecordingHttpServer.Reply.Text("complete");
            WebUtil.DownloadFile(m_server.BaseUri.ToString(), filename);
            Assert.Equal("complete", File.ReadAllText(filename));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            File.Delete(filename);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void DownloadsAndHelo_UseSystemProxyAfterDirectServerSetup()
    {
        IWebProxy savedProxy = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy(m_server.BaseUri, false);
            m_server.Responder = _ => new RecordingHttpServer.Reply
            {
                Body = Encoding.UTF8.GetBytes("download"),
                Headers = new Dictionary<string, string> { ["X-Handlers-Provided"] = "OpenSim" }
            };
            using (Stream stream = ArchiveHelpers.URIFetch(new Uri("http://unresolvable.invalid/archive")))
            using (StreamReader reader = new(stream))
                Assert.Equal("download", reader.ReadToEnd());
            Assert.Equal("OpenSim", new HeloServicesConnector("http://unresolvable.invalid").Helo());
            Assert.Equal(2, m_server.Requests.Count);
            Assert.All(m_server.Requests, request => Assert.Contains("http://unresolvable.invalid/", request.RequestLine));
            Assert.False(WebUtil.SharedSocketsHttpHandler.UseProxy);
        }
        finally
        {
            HttpClient.DefaultProxy = savedProxy;
        }
    }

    [Fact]
    public void Helo_ReadsHandlerHeaderAndHandlesMissingHeaderOrHttpFailure()
    {
        HeloServicesConnector connector = new(m_server.BaseUri.ToString());
        m_server.Responder = _ => new RecordingHttpServer.Reply
        {
            Headers = new Dictionary<string, string> { ["X-Handlers-Provided"] = "OpenSim" }
        };
        Assert.Equal("OpenSim", connector.Helo());
        Assert.Equal("/helo/", Assert.Single(m_server.Requests).Target);
        m_server.Responder = _ => new RecordingHttpServer.Reply();
        Assert.Equal(string.Empty, connector.Helo());
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 503 };
        Assert.Equal(string.Empty, connector.Helo());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegionLoader_NotFoundIsAllowedOnlyWhenRegionless(bool allowRegionless)
    {
        RegionLoaderWebServer loader = CreateRegionLoader(allowRegionless);
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 404 };
        if (allowRegionless)
            Assert.Empty(loader.LoadRegions());
        else
            Assert.Throws<HttpRequestException>(() => loader.LoadRegions());
    }

    [Fact]
    public void RegionLoader_RetriesEmptyConfigAndPropagatesNon404Failures()
    {
        RegionLoaderWebServer loader = CreateRegionLoader(false);
        m_server.Responder = _ => RecordingHttpServer.Reply.Text("<Nini/>");
        Assert.Null(loader.LoadRegions());
        Assert.Equal(3, m_server.Requests.Count);
        m_server.Responder = _ => new RecordingHttpServer.Reply { Status = 500 };
        Assert.Throws<HttpRequestException>(() => loader.LoadRegions());
        Assert.Equal(4, m_server.Requests.Count);
    }

    private RegionLoaderWebServer CreateRegionLoader(bool allowRegionless)
    {
        IniConfigSource config = new();
        config.AddConfig("Startup");
        config.Configs["Startup"].Set("regionload_webserver_url", m_server.BaseUri.ToString());
        config.Configs["Startup"].Set("allow_regionless", allowRegionless);
        RegionLoaderWebServer loader = new();
        loader.SetIniConfigSource(config);
        return loader;
    }
}
