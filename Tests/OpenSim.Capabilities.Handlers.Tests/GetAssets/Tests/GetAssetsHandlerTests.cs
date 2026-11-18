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

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Capabilities.Handlers;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Capabilities.Handlers.GetAssets.Tests;

/// <summary>
/// The ViewerAsset / GetAssets capability, served through a real local HTTP server as a region serves it.
/// A viewer fetches each terrain detail asset both as a texture and as a GLTF material, since the region's
/// terrain slots hold either; a texture asked for as a material is answered not found. Other mismatches
/// between the type asked for and the stored type stay tolerated (served), as grid databases often have
/// asset types wrong.
/// One server for the class (GetAssetsServer), stopped after its last test.
/// </summary>
public class GetAssetsHandlerTests : IClassFixture<GetAssetsHandlerTests.GetAssetsServer>
{
    public sealed class GetAssetsServer : IDisposable
    {
        public readonly IAssetService Assets = new SceneHelpers().SetupScene().AssetService;
        public readonly BaseHttpServer Server;

        public GetAssetsServer()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            uint port = (uint)((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            GetAssetsHandler handler = new(Assets);
            Server = new BaseHttpServer(port) { ListenIPAddress = IPAddress.Loopback };
            Server.AddSimpleStreamHandler(new SimpleStreamHandler("/assets",
                (req, resp) => handler.Handle((OSHttpRequest)req, (OSHttpResponse)resp)));
            Server.Start(false, false);
        }

        public void Dispose() => Server.Stop();
    }

    private readonly IAssetService m_assets;
    private readonly BaseHttpServer m_server;

    public GetAssetsHandlerTests(GetAssetsServer fixture)
    {
        m_assets = fixture.Assets;
        m_server = fixture.Server;
    }

    private UUID Store(AssetType type, byte[] data)
    {
        UUID id = UUID.Random();
        m_assets.Store(new AssetBase(id, "test", (sbyte)type, UUID.Zero.ToString()) { Data = data });
        return id;
    }

    private async Task<(HttpStatusCode Status, byte[] Body)> Get(string query)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{m_server.Port}/assets?{query}");
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ATextureAskedForAsATextureIsServed()
    {
        byte[] data = { 0xFF, 0x4F, 0xFF, 0x51, 1, 2, 3 };
        UUID id = Store(AssetType.Texture, data);

        var (status, body) = await Get("texture_id=" + id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(data, body);
    }

    [Fact]
    public async Task ATextureAskedForAsAMaterialIsNotFound()
    {
        UUID id = Store(AssetType.Texture, new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 1, 2, 3 });

        var (status, body) = await Get("material_id=" + id);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task AMaterialAskedForAsAMaterialIsServed()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("<llsd><map><key>version</key><string>1.1</string></map></llsd>");
        UUID id = Store(AssetType.Material, data);

        var (status, body) = await Get("material_id=" + id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(data, body);
    }

    [Fact]
    public async Task OtherTypeMismatchesAreStillServed()
    {
        byte[] data = { 1, 2, 3, 4 };
        UUID id = Store(AssetType.Notecard, data);

        var (status, body) = await Get("texture_id=" + id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(data, body);
    }
}
