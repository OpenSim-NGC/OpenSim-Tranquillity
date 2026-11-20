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

using System.Data.SQLite;
using System.Xml.Serialization;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using Xunit;
using Assert = Xunit.Assert;
using Theory = Xunit.TheoryAttribute;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// An asset whose stored flags hold a bit AssetFlags does not name. Regions and Robust exchange assets as XML
/// (XmlSerializer of AssetBase), which refuses such a value, so the asset could not be sent at all. The stores now
/// keep the known bits when they read a row; the XML is unchanged.
/// </summary>
public class UnknownAssetFlagsTests
{
    private static string Serialize(AssetBase asset)
    {
        using var w = new StringWriter();
        new XmlSerializer(typeof(AssetBase)).Serialize(w, asset);
        return w.ToString();
    }

    private static AssetBase Texture() =>
        new(UUID.Random(), "flags test", (sbyte)AssetType.Texture, UUID.Zero.ToString()) { Data = new byte[] { 1, 2, 3 } };

    [Fact]
    public void AnUnknownFlagCannotBeSerialized()
    {
        AssetBase asset = Texture();
        asset.Flags = (AssetFlags)9;
        Assert.Throws<InvalidOperationException>(() => Serialize(asset));
    }

    [Theory]
    [InlineData(0, AssetFlags.Normal)]
    [InlineData(7, AssetFlags.Maptile | AssetFlags.Rewritable | AssetFlags.Collectable)]
    [InlineData(9, AssetFlags.Maptile)]
    [InlineData(-1, AssetFlags.Maptile | AssetFlags.Rewritable | AssetFlags.Collectable)]
    public void TheKnownBitsAreKept(int stored, AssetFlags expected)
    {
        Assert.Equal(expected, StoredAssetFlags.FromStored(stored, UUID.Random().ToString()));
    }

    [Fact]
    public void ASqliteRowWithAnUnknownFlagIsReadAndSent()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "assetflags-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SQLiteAssetData();
        store.Initialise("URI=file:" + file);
        AssetBase asset = Texture();
        Assert.True(store.StoreAsset(asset));

        // a bad row: a bit no version writes
        using (var conn = new SQLiteConnection("URI=file:" + file))
        {
            conn.Open();
            using var cmd = new SQLiteCommand("update assets set asset_flags = 9 where UUID = :id", conn);
            cmd.Parameters.Add(new SQLiteParameter(":id", asset.ID));
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        AssetBase read = store.GetAsset(asset.FullID);
        Assert.Equal(AssetFlags.Maptile, read.Flags);
        Assert.Contains("<Flags>Maptile</Flags>", Serialize(read));
    }
}
