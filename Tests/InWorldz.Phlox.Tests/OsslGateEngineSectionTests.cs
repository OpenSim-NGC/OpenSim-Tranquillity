/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The [OSSL] gate for a script engine other than Phlox: it reads [OSSL], and when that section is absent the engine's own
/// section where Phlox reads [InWorldz.Phlox]. Parallel-safe: it builds gates over in-memory configuration only.
/// </summary>
public class OsslGateEngineSectionTests
{
    private static IConfigSource Config(string section, params (string Key, string Value)[] settings)
    {
        var source = new IniConfigSource();
        IConfig c = source.AddConfig(section);
        foreach ((string key, string value) in settings) c.Set(key, value);
        return source;
    }

    [Fact]
    public void WithNoOsslSectionAnotherEnginesOwnSectionSetsTheThreatLevel()
    {
        var gate = new OsslGate(Config("Example", ("OSFunctionThreatLevel", "High")), "Example");
        Assert.Equal(ThreatLevel.High, gate.MaxThreatLevel);
    }

    [Fact]
    public void WithNoOsslSectionAnotherEnginesOwnSectionCanSwitchTheFunctionsOff()
    {
        var gate = new OsslGate(Config("Example", ("AllowOSFunctions", "false")), "Example");
        Assert.False(gate.Enabled);
        var denied = Assert.Throws<InWorldz.Phlox.VM.VMException>(() => gate.Check());
        Assert.Contains("All unsafe OSSL funtions disabled", denied.Message);
    }

    [Fact]
    public void TheOsslSectionWinsOverTheEnginesOwn()
    {
        IConfigSource source = Config("OSSL", ("OSFunctionThreatLevel", "Low"));
        source.AddConfig("Example").Set("OSFunctionThreatLevel", "Severe");
        Assert.Equal(ThreatLevel.Low, new OsslGate(source, "Example").MaxThreatLevel);
    }

    [Fact]
    public void PhloxsOwnSectionIsStillReadWithoutAnEngineSection()
    {
        var gate = new OsslGate(Config("InWorldz.Phlox", ("OSFunctionThreatLevel", "Moderate")));
        Assert.Equal(ThreatLevel.Moderate, gate.MaxThreatLevel);
    }

    [Fact]
    public void AnotherEnginesSectionIsNotReadByPhloxsGate()
    {
        var gate = new OsslGate(Config("Example", ("OSFunctionThreatLevel", "Severe")));
        Assert.Equal(ThreatLevel.VeryLow, gate.MaxThreatLevel);
    }

    [Fact]
    public void WithNoSectionAtAllTheDefaultsHold()
    {
        var gate = new OsslGate(new IniConfigSource(), "Example");
        Assert.True(gate.Enabled);
        Assert.Equal(ThreatLevel.VeryLow, gate.MaxThreatLevel);
    }
}
