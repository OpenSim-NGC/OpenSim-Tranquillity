/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Inventory;
using OpenSim.Services.Connectors;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// XInventoryServicesConnector keeps the items it reads for 30 seconds. After a move, the region reads each moved item
/// back through the connector and sends it to the viewer (Scene.MoveInventoryItem), so a copy cached from before the
/// move puts the item back in its old folder in the viewer. A read that fails is not kept either.
///
/// The inventory server is a listener on 127.0.0.1 holding one item and answering GETITEM and MOVEITEMS as the
/// inventory server handler does. The tests hold a MOVEITEMS or a GETITEM at the server to make a read and a move
/// overlap. WebUtil's shared HTTP handlers are process-wide; this project runs its test classes one at a time
/// (AssemblyInfo.cs) and the test puts the previous handlers back.
/// </summary>
public class XInventoryItemCacheTests : OpenSimTestCase
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);
    private static readonly UUID s_owner = new UUID("2d7a4f0e-8b13-4c59-a6e2-91f3d5c0b847");
    private static readonly UUID s_oldFolder = new UUID("8e1c5b27-3d6f-4a90-b4c8-0f2e7a9d1635");
    private static readonly UUID s_newFolder = new UUID("c3f09a6d-52e4-4b17-8d3a-6b5e0f1c27a9");

    private readonly SocketsHttpHandler m_savedRedir;
    private readonly SocketsHttpHandler m_savedNoRedir;
    private readonly HttpListener m_listener = new();
    private readonly XInventoryServicesConnector m_connector;

    // The server's item, and its folder.
    private readonly InventoryItemBase m_item;
    private UUID m_folder = s_oldFolder;
    private int m_getItems;

    // Hooks the tests set to hold a request at the server.
    private Action m_onMove;
    private Action m_onGetItemRead;
    private bool m_breakNextGetItem;

    public XInventoryItemCacheTests()
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
        string url = $"http://127.0.0.1:{port}/";
        m_listener.Prefixes.Add(url);
        m_listener.Start();
        _ = Task.Run(Serve);

        m_connector = new XInventoryServicesConnector(url);
        m_item = new InventoryItemBase(UUID.Random(), s_owner)
        {
            AssetID = UUID.Random(),
            AssetType = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard,
            Name = "notecard",
            Description = string.Empty,
            CreatorId = s_owner.ToString(),
            CreatorData = string.Empty,
            Folder = s_oldFolder,
        };
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

    private async Task Serve()
    {
        while (m_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await m_listener.GetContextAsync(); }
            catch { return; }

            // Each request on its own, so a held request does not hold the others.
            _ = Task.Run(() => Answer(ctx));
        }
    }

    private void Answer(HttpListenerContext ctx)
    {
        string body;
        using (StreamReader reader = new(ctx.Request.InputStream))
            body = reader.ReadToEnd();
        Dictionary<string, object> request = ServerUtils.ParseQueryString(body);

        Dictionary<string, object> result = new();
        switch (request["METHOD"].ToString())
        {
            case "GETITEM":
                Interlocked.Increment(ref m_getItems);
                if (m_breakNextGetItem)
                {
                    // A reply the connector cannot read an item from.
                    m_breakNextGetItem = false;
                    result["unexpected"] = "reply";
                    break;
                }
                InventoryItemBase copy = (InventoryItemBase)m_item.Clone();
                copy.Folder = m_folder;
                result["item"] = ServerEncodes(copy);
                m_onGetItemRead?.Invoke();
                break;

            case "GETMULTIPLEITEMS":
                InventoryItemBase one = (InventoryItemBase)m_item.Clone();
                one.Folder = m_folder;
                result["item_0"] = ServerEncodes(one);
                break;

            case "MOVEITEMS":
                m_onMove?.Invoke();
                m_folder = s_newFolder;
                result["RESULT"] = "True";
                break;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(ServerUtils.BuildXmlResponse(result));
        ctx.Response.ContentType = "text/xml";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static Dictionary<string, object> ServerEncodes(InventoryItemBase item)
    {
        object handler = RuntimeHelpers.GetUninitializedObject(typeof(XInventoryConnectorPostHandler));
        MethodInfo encode = typeof(XInventoryConnectorPostHandler).GetMethod("EncodeItem", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Dictionary<string, object>)encode.Invoke(handler, new object[] { item });
    }

    private bool Move()
    {
        InventoryItemBase moved = (InventoryItemBase)m_item.Clone();
        moved.Folder = s_newFolder;
        return m_connector.MoveItems(s_owner, new List<InventoryItemBase> { moved });
    }

    [Fact]
    public async Task AnItemReadWhileItIsBeingMovedIsReadInItsNewFolderAfterTheMove()
    {
        Assert.Equal(s_oldFolder, m_connector.GetItem(s_owner, m_item.ID).Folder);

        using ManualResetEventSlim moveArrived = new(false);
        using ManualResetEventSlim releaseMove = new(false);
        m_onMove = () =>
        {
            moveArrived.Set();
            releaseMove.Wait(s_wait);
        };

        Task<bool> move = Task.Run(Move);
        Assert.True(moveArrived.Wait(s_wait), "the move did not reach the server");

        // Read while the server has the move but has not done it yet: the item is still in its old folder.
        Assert.Equal(s_oldFolder, m_connector.GetItem(s_owner, m_item.ID).Folder);

        releaseMove.Set();
        Assert.True(await move.WaitAsync(s_wait));

        Assert.Equal(s_newFolder, m_connector.GetItem(s_owner, m_item.ID).Folder);
    }

    [Fact]
    public async Task AnItemFetchedWhileItIsBeingMovedIsFetchedInItsNewFolderAfterTheMove()
    {
        using ManualResetEventSlim moveArrived = new(false);
        using ManualResetEventSlim releaseMove = new(false);
        m_onMove = () =>
        {
            moveArrived.Set();
            releaseMove.Wait(s_wait);
        };

        Task<bool> move = Task.Run(Move);
        Assert.True(moveArrived.Wait(s_wait), "the move did not reach the server");

        Assert.Equal(s_oldFolder, m_connector.GetMultipleItems(s_owner, new[] { m_item.ID })[0].Folder);

        releaseMove.Set();
        Assert.True(await move.WaitAsync(s_wait));

        Assert.Equal(s_newFolder, m_connector.GetMultipleItems(s_owner, new[] { m_item.ID })[0].Folder);
    }

    [Fact]
    public async Task AnItemReadBeforeAMoveButAnsweredAfterItIsReadInItsNewFolderAfterTheMove()
    {
        using ManualResetEventSlim readDone = new(false);
        using ManualResetEventSlim releaseRead = new(false);
        m_onGetItemRead = () =>
        {
            m_onGetItemRead = null;
            readDone.Set();
            releaseRead.Wait(s_wait);
        };

        // The server reads the item in its old folder; its answer is held until the move is done.
        Task<InventoryItemBase> read = Task.Run(() => m_connector.GetItem(s_owner, m_item.ID));
        Assert.True(readDone.Wait(s_wait), "the read did not reach the server");

        Assert.True(Move());

        releaseRead.Set();
        Assert.Equal(s_oldFolder, (await read.WaitAsync(s_wait)).Folder);

        Assert.Equal(s_newFolder, m_connector.GetItem(s_owner, m_item.ID).Folder);
    }

    [Fact]
    public void AReadThatFailsIsAskedAgain()
    {
        m_breakNextGetItem = true;
        Assert.Null(m_connector.GetItem(s_owner, m_item.ID));

        InventoryItemBase item = m_connector.GetItem(s_owner, m_item.ID);

        Assert.NotNull(item);
        Assert.Equal(m_item.ID, item.ID);
        Assert.Equal(2, m_getItems);
    }
}
