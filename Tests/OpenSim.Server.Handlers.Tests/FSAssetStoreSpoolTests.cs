/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
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
/// FSAssets' Store writes the asset to the spool, where the writer takes it to the store, and then writes its row.
/// A spool write that fails, as on a full disk, is answered as not stored and leaves no file behind. It used to
/// throw out of Store, so the HTTP connection closed with no answer, and left the part written in spool/spool.
/// </summary>
public class FSAssetStoreSpoolTests : IDisposable
{
    private readonly string m_dir = Path.Combine(Path.GetTempPath(), "fsassets-spool-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, string> m_rows = new();

    public void Dispose()
    {
        try { Directory.Delete(m_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ASpoolFileThatCannotBeWrittenIsNotStored()
    {
        string spool = Path.Combine(m_dir, "spool");
        FSAssetConnector service = Service(spool);
        AssetBase asset = Asset(Content(4096));
        string spoolFile = Path.Combine(spool, Util.SHA256Hash(asset.Data) + ".asset");

        // A directory where the spool file goes: its write fails until it is removed.
        Directory.CreateDirectory(spoolFile);

        Assert.Equal(UUID.Zero.ToString(), service.Store(asset));
        Assert.Empty(m_rows);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(spool, "spool")));

        Directory.Delete(spoolFile);

        Assert.Equal(asset.ID, service.Store(asset));
        Assert.Equal(asset.Data, File.ReadAllBytes(spoolFile));
        Assert.True(m_rows.ContainsKey(asset.ID));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(spool, "spool")));
    }

    [SmallFileSystemFact]
    public void OnAFullDiskTheAssetIsNotStoredAndNoFileIsLeft()
    {
        string spool = Path.Combine(Environment.GetEnvironmentVariable(SmallFileSystemFactAttribute.EnvVar)!,
            "fsassets-spool-" + Guid.NewGuid().ToString("N"));
        try
        {
            FSAssetConnector service = Service(spool);
            long free = new DriveInfo(spool).AvailableFreeSpace;
            Assert.InRange(free, 0, SmallFileSystemFactAttribute.MaxSize);
            AssetBase tooBig = Asset(Content((int)free + 1024 * 1024));

            Assert.Equal(UUID.Zero.ToString(), service.Store(tooBig));
            Assert.Empty(m_rows);
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(spool, "spool")));
            Assert.Empty(Directory.GetFiles(spool));

            // Nothing of it holds the space: an asset that fits is stored.
            AssetBase fits = Asset(Content(4096));
            Assert.Equal(fits.ID, service.Store(fits));
            Assert.Equal(fits.Data, File.ReadAllBytes(Path.Combine(spool, Util.SHA256Hash(fits.Data) + ".asset")));
            Assert.Equal(new[] { fits.ID }, m_rows.Keys);
        }
        finally
        {
            try { Directory.Delete(spool, true); } catch { /* best effort */ }
        }
    }

    private static byte[] Content(int length)
    {
        byte[] data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }

    private static AssetBase Asset(byte[] data) =>
        new(UUID.Random(), "spooled", (sbyte)AssetType.Notecard, UUID.Random().ToString()) { Data = data };

    /// <summary>A service with what Store uses: the rows, the folders, no fallback and no writer.</summary>
    private FSAssetConnector Service(string spool)
    {
        Directory.CreateDirectory(Path.Combine(spool, "spool"));
        string data = Path.Combine(m_dir, "data");
        Directory.CreateDirectory(data);

        IFSAssetDataPlugin rows = StubProxy.Create<IFSAssetDataPlugin>((method, args) =>
        {
            if (method.Name != nameof(IFSAssetDataPlugin.Store))
                return (false, null);
            m_rows[((AssetMetadata)args[0]).ID] = (string)args[1];
            return (true, true);
        });

        var service = (FSAssetConnector)RuntimeHelpers.GetUninitializedObject(typeof(FSAssetConnector));
        Set(service, "m_DataConnector", rows);
        Set(service, "m_SpoolDirectory", spool);
        Set(service, "m_FSBase", data);
        Set(service, "m_readLock", new object());
        Set(service, "m_statsLock", new object());
        Set(service, "m_showStats", false);
        return service;
    }

    private static void Set(FSAssetConnector service, string field, object value) =>
        (typeof(FSAssetConnector).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(FSAssetConnector), field)).SetValue(service, value);
}

/// <summary>
/// Runs only when <see cref="EnvVar"/> names a directory on a small filesystem, of at most <see cref="MaxSize"/>
/// bytes, that the test may fill, such as a tmpfs mounted for it
/// (<c>unshare -rm sh -c 'mount -t tmpfs -o size=16m tmpfs DIR; OPENSIM_TEST_SMALL_FS=DIR dotnet test ...'</c>).
/// Unset, the test is reported as skipped, never passed.
/// </summary>
public sealed class SmallFileSystemFactAttribute : FactAttribute
{
    public const string EnvVar = "OPENSIM_TEST_SMALL_FS";
    public const long MaxSize = 256L * 1024 * 1024;

    public SmallFileSystemFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Requires a small filesystem to fill; set {EnvVar} to run.";
    }
}
