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
using OpenSim.Services.FSAssetService;
using OpenSim.Tests.Common;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// FSAssets: Delete answers what the store answers for the row, so a delete the store could not do is not reported
/// as done. It used to answer true whatever the store said.
/// </summary>
public class FSAssetDeleteAnswerTests
{
    [Fact]
    public void ADeleteTheStoreDidAnswersTrue() => DeleteAnswersWhatTheStoreAnswers(true);

    [Fact]
    public void ADeleteTheStoreCouldNotDoAnswersFalse() => DeleteAnswersWhatTheStoreAnswers(false);

    private static void DeleteAnswersWhatTheStoreAnswers(bool stored)
    {
        string id = UUID.Random().ToString();
        List<string> asked = new();
        IFSAssetDataPlugin rows = StubProxy.Create<IFSAssetDataPlugin>((method, args) =>
        {
            if (method.Name != nameof(IFSAssetDataPlugin.Delete))
                return (false, null);
            asked.Add((string)args[0]);
            return (true, stored);
        });

        Assert.Equal(stored, Service(rows).Delete(id));
        Assert.Equal(new[] { id }, asked);
    }

    /// <summary>A service with only what Delete uses: the rows.</summary>
    private static FSAssetConnector Service(IFSAssetDataPlugin rows)
    {
        var service = (FSAssetConnector)RuntimeHelpers.GetUninitializedObject(typeof(FSAssetConnector));
        (typeof(FSAssetConnector).GetField("m_DataConnector", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(FSAssetConnector), "m_DataConnector")).SetValue(service, rows);
        return service;
    }
}
