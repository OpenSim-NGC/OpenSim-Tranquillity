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
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.FSAssetService;
using OpenSim.Tests.Common;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// FSAssets: AssetsExist answers true only for an asset whose file holds its content. It used to answer from the
/// table alone, so an asset whose file was missing, empty or cut short counted as there, and Hypergrid never sent
/// it again.
/// </summary>
public class FSAssetExistsTests : IDisposable
{
    private readonly string m_dir = Path.Combine(Path.GetTempPath(), "fsassets-exists-" + Guid.NewGuid().ToString("N"));
    private readonly string m_base;
    private readonly string m_spool;

    /// <summary>The rows: id to (hash, type), as the database would hold them.</summary>
    private readonly Dictionary<UUID, (string Hash, sbyte Type)> m_rows = new();

    public FSAssetExistsTests()
    {
        m_base = Path.Combine(m_dir, "data");
        m_spool = Path.Combine(m_dir, "spool");
        Directory.CreateDirectory(m_base);
        Directory.CreateDirectory(Path.Combine(m_spool, "spool"));
    }

    public void Dispose() => Directory.Delete(m_dir, true);

    [Fact]
    public void OnlyAssetsWhoseFileHoldsTheirContentExist()
    {
        FSAssetConnector service = Service();

        byte[] intact = Content(4096);
        UUID intactId = Row(intact);
        WriteStored(service, intact);

        byte[] cut = Content(4096);
        UUID cutId = Row(cut);
        string cutFile = WriteStored(service, cut);
        byte[] raw = File.ReadAllBytes(cutFile);
        File.WriteAllBytes(cutFile, raw[..(raw.Length / 2)]);

        byte[] emptied = Content(4096);
        UUID emptiedId = Row(emptied);
        File.WriteAllBytes(WriteStored(service, emptied), Array.Empty<byte>());

        UUID noFileId = Row(Content(4096));

        byte[] spooled = Content(4096);
        UUID spooledId = Row(spooled);
        File.WriteAllBytes(Path.Combine(m_spool, Util.SHA256Hash(spooled) + ".asset"), spooled);

        UUID noBytesId = Row(Array.Empty<byte>());

        UUID noRowId = UUID.Random();

        bool[] exist = service.AssetsExist(new[] { intactId, cutId, emptiedId, noFileId, spooledId, noBytesId, noRowId }
            .Select(id => id.ToString()).ToArray());

        Assert.Equal(new[] { true, false, false, false, true, true, false }, exist);
    }

    private static byte[] Content(int length)
    {
        byte[] data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }

    private UUID Row(byte[] data)
    {
        UUID id = UUID.Random();
        m_rows[id] = (Util.SHA256Hash(data), (sbyte)AssetType.Texture);
        return id;
    }

    /// <summary>Writes the store's file for the hash of <paramref name="data"/>, as the writer does.</summary>
    private string WriteStored(FSAssetConnector service, byte[] data)
    {
        string file = Path.Combine(m_base, service.HashToFile(Util.SHA256Hash(data))) + ".gz";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (GZipStream gz = new GZipStream(new FileStream(file, FileMode.Create), CompressionMode.Compress))
            gz.Write(data, 0, data.Length);
        return file;
    }

    /// <summary>A service with what AssetsExist uses: the rows and the folders.</summary>
    private FSAssetConnector Service()
    {
        IFSAssetDataPlugin rows = StubProxy.Create<IFSAssetDataPlugin>((method, args) =>
        {
            UUID[] ids = args.Length > 0 ? args[0] as UUID[] ?? Array.Empty<UUID>() : Array.Empty<UUID>();
            switch (method.Name)
            {
                // What the table says, which AssetsExist used to answer.
                case nameof(IFSAssetDataPlugin.AssetsExist):
                    return (true, ids.Select(id => m_rows.ContainsKey(id)).ToArray());
                case "GetHashes":
                    return (true, ids.Select(id => m_rows.TryGetValue(id, out var row) ? row : (null, (sbyte)0)).ToArray());
                default:
                    return (false, null);
            }
        });

        var service = (FSAssetConnector)RuntimeHelpers.GetUninitializedObject(typeof(FSAssetConnector));
        Set(service, "m_DataConnector", rows);
        Set(service, "m_SpoolDirectory", m_spool);
        Set(service, "m_FSBase", m_base);
        return service;
    }

    private static void Set(FSAssetConnector service, string field, object value) =>
        (typeof(FSAssetConnector).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(FSAssetConnector), field)).SetValue(service, value);
}
