/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Runtime.CompilerServices;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.FSAssetService;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// FSAssets: an error while getting one asset answers that asset as missing. It used to stop the whole server.
/// </summary>
public class FSAssetReadErrorTests : IDisposable
{
    private readonly string m_dir = Path.Combine(Path.GetTempPath(), "fsassets-read-" + Guid.NewGuid().ToString("N"));

    public FSAssetReadErrorTests() => Directory.CreateDirectory(m_dir);

    public void Dispose() => Directory.Delete(m_dir, true);

    [Fact]
    public void AnErrorFromTheFallbackServiceAnswersTheAssetAsMissing()
    {
        UUID id = UUID.Random();
        IFSAssetDataPlugin rows = StubProxy.Create<IFSAssetDataPlugin>((method, args) =>
        {
            if (method.Name != nameof(IFSAssetDataPlugin.Get))
                return (false, null);
            // a row whose file is not on disk
            args[1] = new string('a', 64);
            return (true, new AssetMetadata { FullID = id, Type = (sbyte)AssetType.Texture });
        });
        IAssetService fallback = StubProxy.Create<IAssetService>((method, args) =>
            method.Name == nameof(IAssetService.Get) ? throw new InvalidOperationException("fallback is down") : (false, null));

        FSAssetConnector service = Service(rows, fallback);

        Assert.Null(service.Get(id.ToString()));
    }

    /// <summary>A service with only what Get reads: the rows, the fallback and the folders.</summary>
    private FSAssetConnector Service(IFSAssetDataPlugin rows, IAssetService fallback)
    {
        var service = (FSAssetConnector)RuntimeHelpers.GetUninitializedObject(typeof(FSAssetConnector));
        Set(service, "m_DataConnector", rows);
        Set(service, "m_FallbackService", fallback);
        Set(service, "m_SpoolDirectory", m_dir);
        Set(service, "m_FSBase", m_dir);
        Set(service, "m_readLock", new object());
        Set(service, "m_statsLock", new object());
        Set(service, "m_showStats", false);
        return service;
    }

    private static void Set(FSAssetConnector service, string field, object value) =>
        (typeof(FSAssetConnector).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(FSAssetConnector), field)).SetValue(service, value);
}
