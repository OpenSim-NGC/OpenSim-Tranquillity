/*
 * Copyright (c) Legion Builds
 * All rights reserved.
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


using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Data.SQLite;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// Checks the text of the region store migration step that adds the prims column SitTargetActive, in the MySQL,
/// PostgreSQL and SQLite stores. No database server is involved; the SQLite step is also run by
/// SQLiteSitTargetActiveTests.
///
/// The column must be nullable with no default but NULL: a NULL row reads as before (the state follows the offset and
/// rotation), and the step must not touch existing rows. The step only adds the column.
/// No process-wide state: the class only reads embedded resources.
/// </summary>
public class RegionStoreSitTargetMigrationTests
{
    public static TheoryData<string> Stores => new() { "MySQL", "PGSQL", "SQLite" };

    private static Assembly StoreAssembly(string store) => store switch
    {
        "MySQL" => typeof(MySQLSimulationData).Assembly,
        "PGSQL" => typeof(PGSQLSimulationData).Assembly,
        _ => typeof(SQLiteSimulationData).Assembly,
    };

    private static string ReadRegionStoreMigrations(string store)
    {
        Assembly assembly = StoreAssembly(store);
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".RegionStore.migrations"));
        using Stream stream = assembly.GetManifestResourceStream(name);
        using StreamReader reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The migration steps, keyed by version, as Migration splits them on ":VERSION n".</summary>
    private static (int Version, string Text)[] Steps(string migrations)
    {
        MatchCollection heads = Regex.Matches(migrations, @"^:VERSION\s+(\d+)", RegexOptions.Multiline);
        return heads.Select((m, i) =>
        {
            int end = i + 1 < heads.Count ? heads[i + 1].Index : migrations.Length;
            return (int.Parse(m.Groups[1].Value), migrations.Substring(m.Index, end - m.Index));
        }).ToArray();
    }

    /// <summary>The SQL lines of a step: no ":VERSION" line, comment or blank line, and no BEGIN or COMMIT.</summary>
    private static string[] Statements(string step)
    {
        return step.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith(":VERSION") && !l.StartsWith("#")
                        && !l.Equals("BEGIN;", System.StringComparison.OrdinalIgnoreCase)
                        && !l.Equals("COMMIT;", System.StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private const string AddsTheColumn = @"ADD\s+COLUMN\s+[`""]?SitTargetActive[`""]?\s";

    [Theory]
    [MemberData(nameof(Stores))]
    public void OneStep_AddsSitTargetActiveToPrims(string store)
    {
        var steps = Steps(ReadRegionStoreMigrations(store))
            .Where(s => Regex.IsMatch(s.Text, AddsTheColumn, RegexOptions.IgnoreCase)).ToArray();
        Assert.Single(steps);
        Assert.Matches(new Regex(@"ALTER\s+TABLE\s+(""public""\.)?[`""]?prims[`""]?\s", RegexOptions.IgnoreCase),
            steps[0].Text);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void TheStep_AddsANullableColumnWithNoDefaultButNull_AndNothingElse(string store)
    {
        var all = Steps(ReadRegionStoreMigrations(store));
        var step = all.Single(s => Regex.IsMatch(s.Text, AddsTheColumn, RegexOptions.IgnoreCase));

        // The newest step, so every database reaches it after all the steps it already has.
        Assert.Equal(all.Max(s => s.Version), step.Version);

        string[] statements = Statements(step.Text);
        string sql = Assert.Single(statements);
        Assert.Matches(new Regex(@"^ALTER\s+TABLE\s+\S+\s+" + AddsTheColumn, RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"NOT\s+NULL", RegexOptions.IgnoreCase), sql);
        Match def = Regex.Match(sql, @"DEFAULT\s+(\S+?)\s*;?$", RegexOptions.IgnoreCase);
        if (def.Success)
            Assert.Equal("NULL", def.Groups[1].Value.ToUpperInvariant());
        Assert.DoesNotContain(statements, s => Regex.IsMatch(s, @"\b(UPDATE|DELETE|DROP|INSERT)\b", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void PostgreSQLStep_QuotesTheColumnAndUsesNoBackticks()
    {
        var step = Steps(ReadRegionStoreMigrations("PGSQL"))
            .Single(s => Regex.IsMatch(s.Text, AddsTheColumn, RegexOptions.IgnoreCase));
        Assert.Contains("\"SitTargetActive\"", step.Text);
        Assert.DoesNotContain("`", step.Text);
    }
}
