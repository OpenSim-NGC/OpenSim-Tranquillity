/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// PhysicsScene.MaximumLinearVelocity is the largest speed the engine will give a body. For Jolt that is the body speed cap,
/// [Jolt] BodyMaxLinearSpeed, so a module that bounds how far something can travel asks the engine and gets the number the
/// engine really enforces.
/// Serial: Initialise loads the native library through JoltScene.NativeLoader, which is process-wide.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class MaximumLinearVelocityTests
{
    private static JoltScene SceneWith(string key, string value)
    {
        var src = new IniConfigSource();
        src.AddConfig("Startup").Set("physics", "Jolt");
        src.Configs["Startup"].Set("meshing", "Meshmerizer");
        IConfig jolt = src.AddConfig("Jolt");
        if (key != null)
            jolt.Set(key, value);
        var scene = new JoltScene();
        scene.Initialise(src);
        return scene;
    }

    [Fact]
    public void With_no_key_the_scene_reports_the_engine_default_cap()
    {
        JoltScene scene = SceneWith(null, null);
        try
        {
            PhysicsScene asBase = scene;
            Assert.Equal(PhysicsBackendSettings.JoltMaxLinearSpeed, asBase.MaximumLinearVelocity);
            Assert.True(asBase.MaximumLinearVelocity > 0f);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_scene_reports_the_configured_cap()
    {
        JoltScene scene = SceneWith("BodyMaxLinearSpeed", "1000");
        try
        {
            PhysicsScene asBase = scene;
            Assert.Equal(1000f, asBase.MaximumLinearVelocity);
        }
        finally { scene.Dispose(); }
    }
}
