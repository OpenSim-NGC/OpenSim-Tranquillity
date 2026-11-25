/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.FSAssetService;
using OpenSim.Tests.Common;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// FSAssets: an asset whose file is missing, empty, cut short or otherwise does not match its hash is answered as
/// missing, and storing the same content again writes the file again. It used to be served as the bytes that
/// could be read (none, or the part left of a file cut short), and a new store of the content was skipped because
/// a file for the hash existed.
/// </summary>
public class FSAssetDamagedFileTests : IDisposable
{
    private readonly string m_dir = Path.Combine(Path.GetTempPath(), "fsassets-damaged-" + Guid.NewGuid().ToString("N"));
    private readonly string m_base;
    private readonly string m_spool;

    /// <summary>The rows: id to (hash, type), as the database would hold them.</summary>
    private readonly Dictionary<string, (string Hash, sbyte Type)> m_rows = new();

    public FSAssetDamagedFileTests()
    {
        m_base = Path.Combine(m_dir, "data");
        m_spool = Path.Combine(m_dir, "spool");
        Directory.CreateDirectory(m_base);
        Directory.CreateDirectory(Path.Combine(m_spool, "spool"));
    }

    public void Dispose() => Directory.Delete(m_dir, true);

    [Fact]
    public void AnIntactFileIsServed()
    {
        FSAssetConnector service = Service();
        byte[] data = Content(4096);
        string id = Row(data, AssetType.Texture);
        WriteStored(service, data);

        Assert.Equal(data, service.Get(id)?.Data);
        Assert.Equal(data, service.GetData(id));
    }

    [Fact]
    public void AFileCutShortIsAnsweredAsMissing()
    {
        FSAssetConnector service = Service();
        byte[] data = Content(4096);
        string id = Row(data, AssetType.Texture);
        string file = WriteStored(service, data);
        Truncate(file);

        Assert.Null(service.Get(id));
        Assert.Null(service.GetData(id));
    }

    [Fact]
    public void AnEmptyFileIsAnsweredAsMissing()
    {
        FSAssetConnector service = Service();
        byte[] data = Content(4096);
        string id = Row(data, AssetType.Texture);
        string file = WriteStored(service, data);
        File.WriteAllBytes(file, Array.Empty<byte>());

        Assert.Null(service.Get(id));
    }

    [Fact]
    public void NoFileIsAnsweredAsMissing()
    {
        FSAssetConnector service = Service();
        string id = Row(Content(4096), AssetType.Texture);

        Assert.Null(service.Get(id));
        Assert.Null(service.GetData(id));
    }

    [Fact]
    public void AFileWithOtherContentIsAnsweredAsMissing()
    {
        FSAssetConnector service = Service();
        byte[] data = Content(4096);
        string id = Row(data, AssetType.Notecard);
        string file = WriteStored(service, data);
        Gzip(file, Content(4096));

        Assert.Null(service.Get(id));
    }

    [Fact]
    public void AnAssetOfNoBytesIsServedEmpty()
    {
        FSAssetConnector service = Service();
        string withFile = Row(Array.Empty<byte>(), AssetType.Notecard);
        WriteStored(service, Array.Empty<byte>());
        string another = Row(Array.Empty<byte>(), AssetType.Notecard);

        Assert.Empty(service.Get(withFile)!.Data);
        Assert.Empty(service.Get(another)!.Data);
    }

    [Fact]
    public void AnObjectIsServedAsStoreWroteIt()
    {
        // Store writes an object's XML ending in a NUL, cleaned of the old "xmlns:xmlns:" fault, while its hash
        // is that of the bytes it was given.
        FSAssetConnector service = Service();
        byte[] given = Encoding.UTF8.GetBytes("<SceneObjectGroup><RootPart /></SceneObjectGroup>");
        string plain = Row(given, AssetType.Object);
        WriteStored(service, given, Terminated(given));

        byte[] faulty = Encoding.UTF8.GetBytes("<SceneObjectGroup xmlns:xmlns:xsi=\"x\"><RootPart /></SceneObjectGroup>");
        string cleaned = Row(faulty, AssetType.Object);
        byte[] written = Encoding.UTF8.GetBytes("<SceneObjectGroup xmlns:xsi=\"x\"><RootPart /></SceneObjectGroup>\0");
        string cleanedFile = WriteStored(service, faulty, written);

        Assert.NotNull(service.Get(plain));
        Assert.NotNull(service.Get(cleaned));

        Truncate(cleanedFile);
        Assert.Null(service.Get(cleaned));
    }

    [Fact]
    public void StoringTheContentAgainRewritesADamagedFile()
    {
        FSAssetConnector service = Service();
        byte[] data = Content(4096);
        string first = Row(data, AssetType.Texture);
        string file = WriteStored(service, data);
        Truncate(file);

        AssetBase again = new AssetBase(UUID.Random(), "again", (sbyte)AssetType.Texture, UUID.Random().ToString()) { Data = data };
        Assert.Equal(again.ID, service.Store(again));

        // The content waits in the spool for the writer, and is served from there in the meantime.
        Assert.True(File.Exists(Path.Combine(m_spool, Util.SHA256Hash(data) + ".asset")));
        Assert.Equal(data, service.Get(first)?.Data);
        Assert.Equal(data, service.Get(again.ID)?.Data);
    }

    [Fact]
    public void StoringContentWhoseFileIsIntactWritesNothing()
    {
        FSAssetConnector service = Service();
        byte[] data = Content(4096);
        Row(data, AssetType.Texture);
        WriteStored(service, data);

        AssetBase again = new AssetBase(UUID.Random(), "again", (sbyte)AssetType.Texture, UUID.Random().ToString()) { Data = data };
        Assert.Equal(again.ID, service.Store(again));

        Assert.Empty(Directory.GetFiles(m_spool));
    }

    private static byte[] Content(int length)
    {
        byte[] data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }

    private static byte[] Terminated(byte[] data)
    {
        byte[] withNul = new byte[data.Length + 1];
        data.CopyTo(withNul, 0);
        return withNul;
    }

    /// <summary>Adds a row for new id holding the hash of <paramref name="data"/>, and answers the id.</summary>
    private string Row(byte[] data, AssetType type)
    {
        string id = UUID.Random().ToString();
        m_rows[id] = (Util.SHA256Hash(data), (sbyte)type);
        return id;
    }

    /// <summary>Writes the store's file for the hash of <paramref name="data"/>, as the writer does.</summary>
    private string WriteStored(FSAssetConnector service, byte[] data, byte[]? written = null)
    {
        string file = Path.Combine(m_base, service.HashToFile(Util.SHA256Hash(data))) + ".gz";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Gzip(file, written ?? data);
        return file;
    }

    private static void Gzip(string file, byte[] data)
    {
        using GZipStream gz = new GZipStream(new FileStream(file, FileMode.Create), CompressionMode.Compress);
        gz.Write(data, 0, data.Length);
    }

    private static void Truncate(string file)
    {
        byte[] raw = File.ReadAllBytes(file);
        File.WriteAllBytes(file, raw[..(raw.Length / 2)]);
    }

    /// <summary>A service with what Get and Store use: the rows, the folders, no fallback and no writer.</summary>
    private FSAssetConnector Service()
    {
        IFSAssetDataPlugin rows = StubProxy.Create<IFSAssetDataPlugin>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IFSAssetDataPlugin.Get):
                    if (!m_rows.TryGetValue((string)args[0], out var row))
                        return (true, null);
                    args[1] = row.Hash;
                    return (true, new AssetMetadata { ID = (string)args[0], FullID = new UUID((string)args[0]), Type = row.Type });
                case nameof(IFSAssetDataPlugin.Store):
                    AssetMetadata meta = (AssetMetadata)args[0];
                    m_rows.TryAdd(meta.ID, ((string)args[1], meta.Type));
                    return (true, true);
                default:
                    return (false, null);
            }
        });

        var service = (FSAssetConnector)RuntimeHelpers.GetUninitializedObject(typeof(FSAssetConnector));
        Set(service, "m_DataConnector", rows);
        Set(service, "m_SpoolDirectory", m_spool);
        Set(service, "m_FSBase", m_base);
        Set(service, "m_readLock", new object());
        Set(service, "m_statsLock", new object());
        Set(service, "m_showStats", false);
        return service;
    }

    private static void Set(FSAssetConnector service, string field, object value) =>
        (typeof(FSAssetConnector).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(FSAssetConnector), field)).SetValue(service, value);
}
