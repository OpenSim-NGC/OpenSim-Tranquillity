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
using System.Net.Sockets;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Connectors;
using OpenSim.Services.Interfaces;
using Xunit;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Grid.Tests;

/// <summary>
/// A grid service that answers an HTTP error. SynchronousRestFormsRequester.MakePostRequest read the body whatever
/// the status, and every GridServicesConnector call goes through it: a 500 whose body still said
/// &lt;Result&gt;Success&lt;/Result&gt; was taken for a registration that worked.
/// </summary>
public class GridRegistrationErrorTests
{
    private const string SuccessBody =
        "<?xml version=\"1.0\"?><ServerResponse><Result>Success</Result></ServerResponse>";

    /// <summary>A server on a free local port that answers every POST with this status and body.</summary>
    private sealed class Answering : IDisposable
    {
        private readonly HttpListener m_listener = new();
        public string Url { get; }

        public Answering(HttpStatusCode status, string body)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            m_listener.Prefixes.Add(Url);
            m_listener.Start();
            _ = Task.Run(async () =>
            {
                while (m_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await m_listener.GetContextAsync(); }
                    catch { return; }
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = (int)status;
                    ctx.Response.ContentType = "text/xml";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => m_listener.Close();
    }

    private static GridRegion Region() => new()
    {
        RegionID = UUID.Random(),
        RegionName = "Error test",
        RegionLocX = 256000,
        RegionLocY = 256000,
        ExternalHostName = "127.0.0.1",
        HttpPort = 9000,
        ServerURI = "http://127.0.0.1:9000/",
        InternalEndPoint = new IPEndPoint(IPAddress.Loopback, 9001),
        RegionSecret = string.Empty,
    };

    [Fact]
    public void AnErrorStatusIsAnErrorWhateverTheBodySays()
    {
        using var server = new Answering(HttpStatusCode.InternalServerError, SuccessBody);
        Assert.Throws<HttpRequestException>(() => SynchronousRestFormsRequester.MakePostRequest(server.Url, "METHOD=register"));

        var grid = new GridServicesConnector(server.Url);
        Assert.NotEqual(string.Empty, grid.RegisterRegion(UUID.Zero, Region()));
        Assert.False(grid.DeregisterRegion(UUID.Random()));
    }

    [Fact]
    public void ASuccessIsStillASuccess()
    {
        using var server = new Answering(HttpStatusCode.OK, SuccessBody);
        Assert.Contains("Success", SynchronousRestFormsRequester.MakePostRequest(server.Url, "METHOD=register"));

        var grid = new GridServicesConnector(server.Url);
        Assert.Equal(string.Empty, grid.RegisterRegion(UUID.Zero, Region()));
        Assert.True(grid.DeregisterRegion(UUID.Random()));
    }
}
