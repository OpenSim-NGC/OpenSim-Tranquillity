/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenSim.Framework;
using OpenSim.Services.FSAssetService;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// FSAssets' writer, the thread that moves stored assets from the spool to the store: a file it cannot write
/// stays in the spool and is written once it can be. An error used to be rethrown on the writer thread and end
/// the whole server. The writer also leaves files in the spool directory that are not its own alone; it used to
/// move every file there into the store and delete it, which with the default spool, /tmp, meant other programs'
/// files.
///
/// Each test runs the writer thread as the service does. It never ends, so it outlives the test, waiting on a
/// spool that is gone.
/// </summary>
public class FSAssetWriterTests : IDisposable
{
    private readonly string m_dir = Path.Combine(Path.GetTempPath(), "fsassets-writer-" + Guid.NewGuid().ToString("N"));
    private readonly string m_base;
    private readonly string m_spool;

    public FSAssetWriterTests()
    {
        m_base = Path.Combine(m_dir, "data");
        m_spool = Path.Combine(m_dir, "spool");
        Directory.CreateDirectory(m_base);
        Directory.CreateDirectory(Path.Combine(m_spool, "spool"));
    }

    public void Dispose()
    {
        try { Directory.Delete(m_dir, true); } catch { /* the writer may be writing */ }
    }

    [Fact]
    public void AFileThatCannotBeWrittenStaysInTheSpoolUntilItCanBe()
    {
        FSAssetConnector service = Service();
        byte[] data = Content(4096);
        string spoolFile = Spool(data);
        string storeFile = StoreFile(service, data);
        string storeDir = Path.GetDirectoryName(storeFile)!;

        // A directory where the file goes: every write of it fails, as on a full disk, until it is removed.
        Directory.CreateDirectory(storeFile);
        StartWriter(service);
        Thread.Sleep(2500);

        Assert.True(File.Exists(spoolFile));
        Assert.Empty(Directory.GetFiles(storeDir));

        Directory.Delete(storeFile);

        Assert.True(WaitFor(() => !File.Exists(spoolFile), TimeSpan.FromSeconds(30)), "the spooled file was not written");
        Assert.Equal(data, Gunzip(storeFile));
        Assert.Equal(new[] { storeFile }, Directory.GetFiles(storeDir));
    }

    [Fact]
    public void OtherFilesInTheSpoolAreLeftAlone()
    {
        FSAssetConnector service = Service();
        string other = Path.Combine(m_spool, "someone-elses.txt");
        File.WriteAllText(other, "not an asset");
        byte[] data = Content(4096);
        string spoolFile = Spool(data);

        StartWriter(service);

        Assert.True(WaitFor(() => !File.Exists(spoolFile), TimeSpan.FromSeconds(30)), "the spooled file was not written");
        Assert.Equal(data, Gunzip(StoreFile(service, data)));
        Assert.Equal("not an asset", File.ReadAllText(other));
    }

    private static byte[] Content(int length)
    {
        byte[] data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }

    /// <summary>Puts <paramref name="data"/> in the spool as Store does, and answers the spool file.</summary>
    private string Spool(byte[] data)
    {
        string file = Path.Combine(m_spool, Util.SHA256Hash(data) + ".asset");
        File.WriteAllBytes(file, data);
        return file;
    }

    private string StoreFile(FSAssetConnector service, byte[] data) =>
        Path.Combine(m_base, service.HashToFile(Util.SHA256Hash(data))) + ".gz";

    private static byte[] Gunzip(string file)
    {
        using GZipStream gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        using MemoryStream ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout)
                return false;
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Starts the service's writer on a thread of its own, as the service's constructor does.</summary>
    private static void StartWriter(FSAssetConnector service)
    {
        MethodInfo writer = typeof(FSAssetConnector).GetMethod("Writer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(FSAssetConnector), "Writer");
        new Thread((ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), service, writer)) { IsBackground = true }.Start();
    }

    /// <summary>A service with only what the writer reads: the folders.</summary>
    private FSAssetConnector Service()
    {
        var service = (FSAssetConnector)RuntimeHelpers.GetUninitializedObject(typeof(FSAssetConnector));
        Set(service, "m_SpoolDirectory", m_spool);
        Set(service, "m_FSBase", m_base);
        return service;
    }

    private static void Set(FSAssetConnector service, string field, object value) =>
        (typeof(FSAssetConnector).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(FSAssetConnector), field)).SetValue(service, value);
}
