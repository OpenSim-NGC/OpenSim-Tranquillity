/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.RegularExpressions;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The MySQL and PostgreSQL region stores read a region's prims in link order: each linkset's root first, then its
/// children by LinkNumber. The loaders add each child to its group in the order the rows are read, and that is the
/// order the group's parts are later sent to viewers in. Without an ORDER BY a server returns the rows in whatever
/// order it finds them, often primary-key (prim UUID) order.
///
/// There is no MySQL or PostgreSQL server in the test run, so the load queries are checked as text.
/// No state is shared; the class can run beside any other.
/// </summary>
public class LinksetLoadOrderTests
{
    [Fact]
    public void MySQL_LoadQuery_OrdersThePrimsByLinkNumber()
    {
        Assert.Matches(
            new Regex(@"\bORDER\s+BY\s+prims\.LinkNumber\s*$", RegexOptions.IgnoreCase),
            MySQLSimulationData.LoadObjectsQuery);
    }

    [Fact]
    public void PgSQL_LoadQuery_OrdersEachLinksetRootFirstThenByLinkNumber()
    {
        Assert.Matches(
            new Regex(@"\bORDER\s+BY\s+""SceneGroupID""\s+asc\s*,\s*sort\s+asc\s*,\s*""LinkNumber""\s+asc\s*$",
                RegexOptions.IgnoreCase),
            PGSQLSimulationData.LoadObjectsQuery);
    }
}
