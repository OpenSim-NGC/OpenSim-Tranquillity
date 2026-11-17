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
using System.Net.Sockets;
using OpenSim.Framework.Servers.HttpServer;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Framework;

/// <summary>
/// Every HTTP listener in the process sends its responses through one shared thread
/// (OSHttpServer's ContextTimeoutManager). Stopping one listener must not stop it for the others, and
/// a listener started after all of them stopped must get a working thread again.
///
/// Each test starts real servers on free localhost ports and checks that a request gets an answer
/// (404: no handler is registered) within a few seconds. The assembly runs its tests one at a time
/// (AssemblyInfo.cs); each test stops the servers it starts.
/// </summary>
public class HttpServerSharedSendThreadTests
{
    [Fact]
    public async Task StoppingOneListenerLeavesTheOthersAnswering()
    {
        BaseHttpServer first = Start();
        BaseHttpServer second = Start();
        try
        {
            Assert.Equal(HttpStatusCode.NotFound, await Get(first));
            Assert.Equal(HttpStatusCode.NotFound, await Get(second));

            first.Stop();
            first = null;

            Assert.Equal(HttpStatusCode.NotFound, await Get(second));
        }
        finally
        {
            first?.Stop();
            second.Stop();
        }
    }

    [Fact]
    public async Task AListenerStartedAfterAllOthersStoppedAnswers()
    {
        BaseHttpServer before = Start();
        Assert.Equal(HttpStatusCode.NotFound, await Get(before));
        before.Stop();

        BaseHttpServer after = Start();
        try
        {
            Assert.Equal(HttpStatusCode.NotFound, await Get(after));
        }
        finally
        {
            after.Stop();
        }
    }

    private static BaseHttpServer Start()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        uint port = (uint)((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseHttpServer server = new(port) { ListenIPAddress = IPAddress.Loopback };
        server.Start(false, false);
        return server;
    }

    private static async Task<HttpStatusCode> Get(BaseHttpServer server)
    {
        // A new client each time, so no connection is kept from one server to the next.
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{server.Port}/no-such-handler");
        return response.StatusCode;
    }
}
