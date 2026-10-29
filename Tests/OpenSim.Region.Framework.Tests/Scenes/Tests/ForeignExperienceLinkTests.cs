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

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization.External;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Inventory;
using OpenSim.Services.Connectors;
using OpenSim.Services.HypergridService;
using OpenSim.Services.Interfaces;
using OpenSim.Services.InventoryService;
using OpenSim.Tests.Common;
using Xunit;

using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A script's Experience link held in inventory data this grid does not control is not trusted: an item read from
/// another grid's inventory server, an item another grid's region writes into this grid's Hypergrid inventory
/// service, and an item loaded from an inventory archive come with no link. An item from this grid's own
/// inventory service keeps it.
///
/// The other grid's inventory server is a listener on 127.0.0.1 answering with the reply this grid's own server
/// handler builds. WebUtil's shared HTTP handlers are process-wide; this project runs its test classes one at a
/// time (AssemblyInfo.cs) and the test puts the previous handlers back.
/// </summary>
public class ForeignExperienceLinkTests : OpenSimTestCase
{
    private static readonly UUID s_experience = new UUID("c41e7b29-0d63-4a85-b9f2-6e8d13a5c07f");
    private static readonly UUID s_owner = new UUID("5b2f8e14-a3c7-4d90-86e1-f47a0c92d6b3");

    private readonly SocketsHttpHandler m_savedRedir;
    private readonly SocketsHttpHandler m_savedNoRedir;
    private readonly HttpListener m_listener = new();
    private readonly string m_url;
    private InventoryItemBase m_served;
    private int m_requests;

    public ForeignExperienceLinkTests()
    {
        m_savedRedir = WebUtil.SharedSocketsHttpHandler;
        m_savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        WebUtil.SetupHTTPClients(false, false, null, 4);

        int port;
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        m_url = $"http://127.0.0.1:{port}/";
        m_listener.Prefixes.Add(m_url);
        m_listener.Start();
        _ = Task.Run(Serve);
    }

    public override void Dispose()
    {
        m_listener.Close();
        SocketsHttpHandler ours = WebUtil.SharedSocketsHttpHandler;
        SocketsHttpHandler oursNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        WebUtil.SharedSocketsHttpHandler = m_savedRedir;
        WebUtil.SharedSocketsHttpHandlerNoRedir = m_savedNoRedir;
        ours?.Dispose();
        oursNoRedir?.Dispose();
        base.Dispose();
    }

    /// <summary>Answers every request as an inventory server answers GETITEM.</summary>
    private async Task Serve()
    {
        while (m_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await m_listener.GetContextAsync(); }
            catch { return; }

            Interlocked.Increment(ref m_requests);
            Dictionary<string, object> result = new() { ["item"] = ServerEncodes(m_served) };
            byte[] bytes = Encoding.UTF8.GetBytes(ServerUtils.BuildXmlResponse(result));
            ctx.Response.ContentType = "text/xml";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static Dictionary<string, object> ServerEncodes(InventoryItemBase item)
    {
        object handler = RuntimeHelpers.GetUninitializedObject(typeof(XInventoryConnectorPostHandler));
        MethodInfo encode = typeof(XInventoryConnectorPostHandler).GetMethod("EncodeItem", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Dictionary<string, object>)encode.Invoke(handler, new object[] { item });
    }

    private static InventoryItemBase ScriptItem(UUID experience)
    {
        return new InventoryItemBase(UUID.Random(), s_owner)
        {
            AssetID = UUID.Random(),
            AssetType = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            Name = "script",
            Description = string.Empty,
            CreatorId = s_owner.ToString(),
            CreatorData = string.Empty,
            Folder = UUID.Random(),
            BasePermissions = (uint)PermissionMask.All,
            CurrentPermissions = (uint)PermissionMask.All,
            NextPermissions = (uint)PermissionMask.All,
            ExperienceID = experience
        };
    }

    // ---- reading another grid's inventory ---------------------------------------------------------------------

    [Fact]
    public void AnItemFromThisGridsOwnInventoryServerKeepsItsExperience()
    {
        m_served = ScriptItem(s_experience);
        XInventoryServicesConnector own = new XInventoryServicesConnector(m_url);

        InventoryItemBase item = own.GetItem(s_owner, m_served.ID);

        Assert.NotNull(item);
        Assert.Equal(s_experience, item.ExperienceID);
    }

    [Fact]
    public void AnItemReadFromAnotherGridsInventoryServerHasNoExperience()
    {
        m_served = ScriptItem(s_experience);
        XInventoryServicesConnector foreign = new XInventoryServicesConnector(m_url) { AcceptsExperienceLinks = false };

        InventoryItemBase item = foreign.GetItem(s_owner, m_served.ID);

        Assert.NotNull(item);
        Assert.Equal(m_served.Name, item.Name);
        Assert.Equal(UUID.Zero, item.ExperienceID);
    }

    [Fact]
    public void TheHypergridBrokerReadsAVisitorsHomeInventoryWithoutExperienceLinks()
    {
        m_served = ScriptItem(s_experience);
        TestScene scene = new SceneHelpers().SetupScene();
        HGInventoryBroker broker = new HGInventoryBroker();
        ((List<Scene>)typeof(HGInventoryBroker).GetField("m_Scenes", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(broker)).Add(scene);

        // The connector the broker uses for a Hypergrid visitor's home inventory server.
        IInventoryService connector = (IInventoryService)typeof(HGInventoryBroker)
            .GetMethod("GetConnector", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(broker, new object[] { m_url });

        InventoryItemBase item = connector.GetItem(s_owner, m_served.ID);

        Assert.NotNull(item);
        Assert.Equal(UUID.Zero, item.ExperienceID);
        Assert.Equal(1, m_requests);
    }

    // ---- other grids writing into this grid's Hypergrid inventory service --------------------------------------

    private static IConfigSource InventoryConfig(string section)
    {
        IniConfigSource config = new();
        IConfig inv = config.AddConfig(section);
        inv.Set("StorageProvider", "OpenSim.Tests.Common.dll");
        inv.Set("ConnectionString", string.Empty);
        inv.Set("UserAccountsService", "OpenSim.Services.Connectors.dll:UserAccountServicesConnector");
        inv.Set("AvatarService", "OpenSim.Services.Connectors.dll:AvatarServicesConnector");
        config.AddConfig("UserAccountService").Set("UserAccountServerURI", "http://192.0.2.1:8003");
        config.AddConfig("AvatarService").Set("AvatarServerURI", "http://192.0.2.1:8003");
        return config;
    }

    private static bool Accepts(XInventoryService service)
        => (bool)typeof(XInventoryService).GetField("m_AcceptsExperienceLinks", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(service);

    [Fact]
    public void TheHypergridInventoryServicesDoNotAcceptExperienceLinks()
    {
        Assert.False(Accepts(new HGInventoryService(InventoryConfig("HGInventoryService"), "HGInventoryService")));
        Assert.False(Accepts(new HGSuitcaseInventoryService(InventoryConfig("HGInventoryService"), "HGInventoryService")));
        Assert.True(Accepts(new XInventoryService(InventoryConfig("InventoryService"), "InventoryService")));
    }

    /// <summary>
    /// The base service as a Hypergrid inventory service runs it, without the account lookups. StoreAsThisGrid
    /// writes an item the way this grid's own service stores it.
    /// </summary>
    private sealed class ForeignWrittenInventoryService : XInventoryService
    {
        public ForeignWrittenInventoryService(IConfigSource config) : base(config, "InventoryService")
        {
            m_AcceptsExperienceLinks = false;
        }

        public bool StoreAsThisGrid(InventoryItemBase item) => m_Database.StoreItem(ConvertFromOpenSim(item));
    }

    [Fact]
    public void AnItemAnotherGridAddsHasNoExperienceAndAnUpdateKeepsTheStoredOne()
    {
        ForeignWrittenInventoryService hg = new ForeignWrittenInventoryService(InventoryConfig("InventoryService"));

        InventoryItemBase added = ScriptItem(s_experience);
        Assert.True(hg.AddItem(added));
        Assert.Equal(UUID.Zero, hg.GetItem(s_owner, added.ID).ExperienceID);

        InventoryItemBase stored = ScriptItem(s_experience);
        Assert.True(hg.StoreAsThisGrid(stored));
        InventoryItemBase forged = hg.GetItem(s_owner, stored.ID);
        forged.ExperienceID = UUID.Random();
        forged.Name = "renamed";
        Assert.True(hg.UpdateItem(forged));

        InventoryItemBase after = hg.GetItem(s_owner, stored.ID);
        Assert.Equal("renamed", after.Name);
        Assert.Equal(s_experience, after.ExperienceID);
    }

    // ---- inventory archives -----------------------------------------------------------------------------------

    [Fact]
    public void AnItemLoadedFromAnInventoryArchiveHasNoExperience()
    {
        InventoryItemBase item = ScriptItem(s_experience);
        string xml = UserInventoryItemSerializer.Serialize(item, new Dictionary<string, object>(), null);
        Assert.DoesNotContain("ExperienceID", xml);

        // An archive written by something that adds the element is read without it.
        string withLink = xml.Replace("</InventoryItem>", $"<ExperienceID>{s_experience}</ExperienceID></InventoryItem>");
        InventoryItemBase loaded = UserInventoryItemSerializer.Deserialize(withLink);

        Assert.Equal(item.Name, loaded.Name);
        Assert.Equal(UUID.Zero, loaded.ExperienceID);
    }
}
