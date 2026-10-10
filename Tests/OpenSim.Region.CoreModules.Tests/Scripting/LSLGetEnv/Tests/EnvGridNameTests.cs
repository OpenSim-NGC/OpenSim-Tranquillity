/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
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

using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.LSLGetEnv.Tests;

/// <summary>
/// LSL_Api.EnvGridName, the answer to llGetEnv("grid") in both script engines: the configured grid name, or ""
/// when none is configured.
/// </summary>
public class EnvGridNameTests : OpenSimTestCase
{
    private static GridInfo Info(string? gridName)
    {
        IniConfigSource config = new IniConfigSource();
        if (gridName is not null)
            config.AddConfig("GridInfo").Set("gridname", gridName);
        // An IP literal as the default gatekeeper, so the constructor needs no DNS lookup.
        return new GridInfo(config, "http://127.0.0.1:9000/");
    }

    [Fact]
    public void AConfiguredNameIsReturned()
    {
        Scene scene = new SceneHelpers().SetupScene();
        scene.SceneGridInfo = Info("Example Grid");

        Assert.Equal("Example Grid", LSL_Api.EnvGridName(scene));
    }

    [Fact]
    public void NoConfiguredNameAnswersEmpty()
    {
        Scene scene = new SceneHelpers().SetupScene();
        scene.SceneGridInfo = Info(null);

        Assert.Equal(string.Empty, LSL_Api.EnvGridName(scene));
    }

    [Fact]
    public void NoGridInfoOrNoSceneAnswersEmpty()
    {
        Scene scene = new SceneHelpers().SetupScene();
        scene.SceneGridInfo = null;

        Assert.Equal(string.Empty, LSL_Api.EnvGridName(scene));
        Assert.Equal(string.Empty, LSL_Api.EnvGridName(null!));
    }
}
