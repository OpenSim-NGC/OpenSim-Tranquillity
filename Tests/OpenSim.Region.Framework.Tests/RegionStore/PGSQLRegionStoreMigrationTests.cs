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
using System.Text.RegularExpressions;
using OpenSim.Data.PGSQL;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// Checks the text of the PostgreSQL region store migrations. No PostgreSQL server is involved.
///
/// A backtick is MySQL's identifier quote; PostgreSQL rejects it (PostgreSQL manual, "Lexical Structure":
/// quoted identifiers use double quotes). Migration.Update catches a failing step, rolls it back and still records the
/// new version, so a step that fails leaves its columns missing for good. PGSQLSimulationData names the prims columns
/// "linksetdata" and "StartStr" in its INSERT, UPDATE and object load; quoted, as PostgreSQL folds unquoted
/// identifiers to lower case.
/// No process-wide state: the class only reads an embedded resource.
/// </summary>
public class PGSQLRegionStoreMigrationTests
{
    private static string ReadRegionStoreMigrations()
    {
        var assembly = typeof(PGSQLSimulationData).Assembly;
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

    [Fact]
    public void NoStepUsesMySqlBackticks()
    {
        foreach ((int version, string text) in Steps(ReadRegionStoreMigrations()))
            Assert.False(text.Contains('`'), $"RegionStore migration {version} quotes with backticks: {text.Trim()}");
    }

    [Theory]
    [InlineData("linksetdata")]
    [InlineData("StartStr")]
    public void PrimsColumnTheStoreUses_IsAddedWithItsQuotedName(string column)
    {
        string pattern = "ADD COLUMN\\s+(IF NOT EXISTS\\s+)?\"" + column + "\"";
        Assert.Contains(Steps(ReadRegionStoreMigrations()), s => Regex.IsMatch(s.Text, pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Databases that ran the backtick version of a step have it recorded as done, so only a later step can add
    /// the column there. It must not fail where the column already exists (fresh databases get it from the
    /// rewritten step).
    /// </summary>
    [Theory]
    [InlineData("linksetdata", 53)]
    [InlineData("StartStr", 58)]
    public void LaterStep_AddsTheColumnIfMissing_ForDatabasesThatRecordedTheFailedStep(string column, int failedVersion)
    {
        string pattern = "ALTER TABLE \"public\"\\.\"prims\"\\s+ADD COLUMN IF NOT EXISTS \"" + column + "\"";
        Assert.Contains(Steps(ReadRegionStoreMigrations()),
            s => s.Version > failedVersion && Regex.IsMatch(s.Text, pattern, RegexOptions.IgnoreCase));
    }
}
