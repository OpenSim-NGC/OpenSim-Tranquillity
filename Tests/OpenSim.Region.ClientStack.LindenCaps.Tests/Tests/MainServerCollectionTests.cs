/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;

using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenCaps.Tests;

/// <summary>
/// OpenSimTestCase's constructor removes MainServer's default server, which is process-wide. A class that derives from
/// it and runs beside the event queue tests can take their server away before an agent's capabilities are made: the
/// EventQueueGet poll handler is then never registered. Every such class in this project has to be in the one
/// collection whose classes never run at once.
/// </summary>
public class MainServerCollectionTests
{
    private const string Collection = "EventQueueMainServer";

    [Fact]
    public void EveryClassThatClearsMainServerIsInTheMainServerCollection()
    {
        string[] outside = typeof(MainServerCollectionTests).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(OpenSimTestCase)))
            .Where(t => !t.GetCustomAttributesData().Any(a =>
                a.AttributeType == typeof(CollectionAttribute)
                && a.ConstructorArguments.Count == 1
                && Collection.Equals(a.ConstructorArguments[0].Value)))
            .Select(t => t.Name)
            .Order()
            .ToArray();

        Assert.Empty(outside);
    }
}
