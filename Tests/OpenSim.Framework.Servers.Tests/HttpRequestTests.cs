using System.Net;
using System.Net.Sockets;
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