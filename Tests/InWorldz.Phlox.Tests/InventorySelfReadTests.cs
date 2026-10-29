/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading;
using OpenMetaverse;
using OpenSim.Framework;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script finds its own inventory item (llGetScriptName, the permission calls, the attach calls) while the prim's
/// inventory changes on another thread: an item dropped in or deleted goes through the core's writers
/// (SceneObjectPartInventory.AddInventoryItem, RemoveInventoryItem), which take the dictionary's write lock. The read
/// takes the read lock, so it neither throws "Collection was modified" nor misses the script's own item.
/// No process-wide state: runs in parallel.
/// </summary>
public class InventorySelfReadTests
{
    private const int Writes = 2000;

    private static TaskInventoryItem Notecard(string name) => new()
    {
        ItemID = UUID.Random(),
        AssetID = UUID.Random(),
        Name = name,
        Type = (int)AssetType.Notecard,
        InvType = (int)InventoryType.Notecard,
    };

    [Fact]
    public void TheScriptFindsItsOwnItemWhileAnotherThreadAddsAndRemovesItems()
    {
        using var r = new ApiCallRig();
        var api = r.Api;
        string name = r.Self.Name;
        // Enough items that a read and a write overlap often.
        for (int i = 0; i < 50; i++) r.H.Prim.Inventory.AddInventoryItem(Notecard("filler " + i), false);

        Exception writerFault = null;
        int written = 0;
        var started = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            try
            {
                started.Wait();
                for (int i = 0; i < Writes; i++)
                {
                    var item = Notecard("churn " + i);
                    r.H.Prim.Inventory.AddInventoryItem(item, false);
                    r.H.Prim.Inventory.RemoveInventoryItem(item.ItemID);
                    Volatile.Write(ref written, i + 1);
                }
            }
            catch (Exception e) { writerFault = e; }
        }) { IsBackground = true };
        writer.Start();

        long reads = 0;
        started.Set();
        // Reads until every write is done, so the test waits on the writes, not on a clock.
        while (writer.IsAlive)
        {
            Assert.Equal(name, api.llGetScriptName());
            reads++;
        }
        writer.Join();
        Assert.Null(writerFault);
        Assert.Equal(Writes, written);
        Assert.True(reads > 0);
    }
}
