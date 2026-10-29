using System.Net;
using System.Net.Sockets;
using System.Text;
using OSHttpServer;
using Xunit;

namespace OpenSim.Framework.Servers.Tests;

public class HttpRequestTests
{
    [Fact]
    public void RemoteIPEndPointIgnoresForwardedForHeader()
    {
        IPEndPoint realPeer = new(IPAddress.Parse("198.51.100.10"), 9000);
        HttpRequest request = new(new TestHttpClientContext(realPeer));

        request.AddHeader("X-Forwarded-For", "203.0.113.99");

        Xunit.Assert.Equal(realPeer, request.RemoteIPEndPoint);
    }

    [Fact]
    public void CloneCopiesABodyThatReadsInSmallPieces()
    {
        HttpRequest request = new(new TestHttpClientContext(new IPEndPoint(IPAddress.Loopback, 9000)));
        byte[] body = Encoding.ASCII.GetBytes("POST body that no single read returns in full.");
        request.Body = new OneBytePerReadStream(body);

        HttpRequest clone = (HttpRequest)request.Clone();

        using MemoryStream copied = new();
        clone.Body.CopyTo(copied);
        Xunit.Assert.Equal(body, copied.ToArray());
    }

    private sealed class OneBytePerReadStream : MemoryStream
    {
        public OneBytePerReadStream(byte[] contents) : base(contents, writable: false)
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, 1));
    }

    private sealed class TestHttpClientContext : IHttpClientContext
    {
        public TestHttpClientContext(IPEndPoint remoteEndPoint)
        {
            LocalIPEndPoint = remoteEndPoint;
        }

        public string SSLCommonName => string.Empty;
        public IPEndPoint LocalIPEndPoint { get; set; }
        public bool IsSecured => false;
        public int contextID => 1;
        public int TimeoutKeepAlive { get; set; }
        public int MaxRequests { get; set; }
        public bool IsClosing => false;

        public event EventHandler<DisconnectedEventArgs> Disconnected { add { } remove { } }
        public event EventHandler<RequestEventArgs> RequestReceived { add { } remove { } }

        public bool CanSend() => false;
        public bool IsSending() => false;
        public void Disconnect(SocketError error) { }
        public void Respond(string httpVersion, HttpStatusCode statusCode, string reason, string body, string contentType) { }
        public void Respond(string httpVersion, HttpStatusCode statusCode, string reason) { }
        public bool Send(byte[] buffer) => false;
        public bool Send(byte[] buffer, int offset, int size) => false;
        public bool SendAsyncStart(byte[] buffer, int offset, int size) => false;
        public void Close() { }
        public HTTPNetworkContext GiveMeTheNetworkStreamIKnowWhatImDoing() => null!;
        public void StartSendResponse(HttpResponse response) { }
        public void ContinueSendResponse() { }
        public void EndSendResponse(uint requestID, ConnectionType connection) { }
        public bool TrySendResponse(int limit) => false;
    }
}