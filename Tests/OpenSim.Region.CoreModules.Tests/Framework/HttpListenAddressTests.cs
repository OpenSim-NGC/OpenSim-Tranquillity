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

using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Framework;

/// <summary>
/// [Network] bind_ip_address: the address every HTTP and HTTPS listener binds to.
///
/// Each listener test starts a real server on a free port and asks the operating system which
/// address that port is listening on (IPGlobalProperties.GetActiveTcpListeners), so it checks the
/// socket, not the field. The assembly runs its tests one at a time (AssemblyInfo.cs), so a test may
/// change MainServer's process-wide default; each restores it.
/// </summary>
public class HttpListenAddressTests
{
    [Theory]
    [InlineData("", "0.0.0.0")]
    [InlineData("   ", "0.0.0.0")]
    [InlineData(null, "0.0.0.0")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData(" 127.0.0.1 ", "127.0.0.1")]
    [InlineData("::1", "::1")]
    public void BindAddressIsReadAsAnIpAddress(string value, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), NetworkServersInfo.ParseBindAddress(value));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:9000")]
    [InlineData("not an address")]
    public void ABindAddressThatIsNotAnIpAddressIsRefused(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => NetworkServersInfo.ParseBindAddress(value));
        Assert.Contains("bind_ip_address", error.Message);
    }

    [Fact]
    public void NetworkSectionSetsTheListenerAddress()
    {
        IniConfigSource config = new();
        config.AddConfig("Network").Set("bind_ip_address", "127.0.0.1");
        NetworkServersInfo info = new();

        info.loadFromConfiguration(config);

        Assert.Equal(IPAddress.Loopback, info.HttpListenerAddress);
    }

    [Fact]
    public void WithoutTheSettingTheListenerAddressIsEveryInterface()
    {
        IniConfigSource config = new();
        config.AddConfig("Network");
        NetworkServersInfo info = new();

        info.loadFromConfiguration(config);

        Assert.Equal(IPAddress.Any, info.HttpListenerAddress);
    }

    [Fact]
    public void HttpServerListensOnlyOnItsAddress()
    {
        uint port = FreePort();
        BaseHttpServer server = new(port) { ListenIPAddress = IPAddress.Loopback };
        try
        {
            server.Start(false, false);

            Assert.Equal(new[] { IPAddress.Loopback }, ListeningAddresses(port));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void HttpsServerListensOnlyOnItsAddress()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        string path = Path.GetTempFileName();
        uint port = FreePort();
        BaseHttpServer server = null;
        try
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, "listen-test"));
            server = new BaseHttpServer(port, true, "localhost", path, "listen-test") { ListenIPAddress = IPAddress.Loopback };
            server.Start(false, false);

            Assert.Equal(new[] { IPAddress.Loopback }, ListeningAddresses(port));
        }
        finally
        {
            server?.Stop();
            File.Delete(path);
        }
    }

    [Fact]
    public void AServerAModuleOpensFollowsTheDefaultAddress()
    {
        uint port = FreePort();
        IPAddress before = MainServer.Instance.DefaultListenIPAddress;
        ICommandConsole savedConsole = InstallConsole();
        MainServer.Instance.DefaultListenIPAddress = IPAddress.Loopback;
        IHttpServer server = null;
        try
        {
            server = MainServer.Instance.GetHttpServer(port);

            Assert.Equal(new[] { IPAddress.Loopback }, ListeningAddresses(port));
        }
        finally
        {
            MainServer.Instance.DefaultListenIPAddress = before;
            MainConsole.Instance = savedConsole;
            MainServer.Instance.RemoveHttpServer(port);
            (server as BaseHttpServer)?.Stop();
        }
    }

    [Fact]
    public void AnAddressTheModuleNamesWinsOverTheDefault()
    {
        uint port = FreePort();
        IPAddress before = MainServer.Instance.DefaultListenIPAddress;
        ICommandConsole savedConsole = InstallConsole();
        MainServer.Instance.DefaultListenIPAddress = IPAddress.Any;
        IHttpServer server = null;
        try
        {
            server = MainServer.Instance.GetHttpServer(port, IPAddress.Loopback);

            Assert.Equal(new[] { IPAddress.Loopback }, ListeningAddresses(port));
        }
        finally
        {
            MainServer.Instance.DefaultListenIPAddress = before;
            MainConsole.Instance = savedConsole;
            MainServer.Instance.RemoveHttpServer(port);
            (server as BaseHttpServer)?.Stop();
        }
    }

    /// <summary>
    /// MainServer.GetHttpServer starts the server with its poll pool, whose WorkManager registers console
    /// commands when first used, as a running simulator always has a console. Returns the console it replaced.
    /// </summary>
    private static ICommandConsole InstallConsole()
    {
        ICommandConsole saved = MainConsole.Instance;
        MainConsole.Instance ??= new MockConsole();
        return saved;
    }

    private static uint FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return (uint)port;
    }

    private static IPAddress[] ListeningAddresses(uint port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(e => e.Port == port)
            .Select(e => e.Address)
            .Distinct()
            .ToArray();
}
