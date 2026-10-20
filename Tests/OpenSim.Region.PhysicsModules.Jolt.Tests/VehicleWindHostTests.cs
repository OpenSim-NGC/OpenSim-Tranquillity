/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How the Jolt scene gives a vehicle the wind: it asks the region's wind module (IWindModule.WindSpeed) for the position,
/// and a region without one gives none. Serial with the other native tests: a JoltScene loads the native library.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleWindHostTests
{

    // A stand-in wind module made at run time: a class written out for the interface would be taken by the host's region-module
    // discovery (NotSelectedTests counts the modules of this assembly) as one more module.
    public class StubWindModule : System.Reflection.DispatchProxy
    {
        public readonly List<(int X, int Y, int Z)> Asked = new();

        protected override object Invoke(System.Reflection.MethodInfo method, object[] args)
        {
            if (method.Name != nameof(IWindModule.WindSpeed))
                return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
            int x = (int)args[0], y = (int)args[1], z = (int)args[2];
            Asked.Add((x, y, z));
            return new Vector3(x, y, 0f);
        }

        public static (IWindModule Module, StubWindModule Log) Make()
        {
            IWindModule module = Create<IWindModule, StubWindModule>();
            return (module, (StubWindModule)(object)module);
        }
    }

    private static JoltScene NewScene()
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt");
        var scene = new JoltScene();
        scene.Initialise(config);
        return scene;
    }

    [Fact]
    public void The_scene_asks_its_wind_module_for_the_wind_at_the_whole_metre_below_the_position()
    {
        JoltScene scene = NewScene();
        try
        {
            var (module, log) = StubWindModule.Make();
            scene.UseWindModule(module);
            Assert.True(scene.TryGetWind(new Vector3(40.9f, 17.2f, 23.5f), out Vector3 wind));
            Assert.Equal((40, 17, 23), log.Asked.Single());
            Assert.Equal(new Vector3(40f, 17f, 0f), wind);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_scene_with_no_wind_module_gives_no_wind_and_a_position_that_is_not_finite_is_not_asked()
    {
        JoltScene scene = NewScene();
        try
        {
            Assert.False(scene.TryGetWind(new Vector3(40f, 17f, 23f), out Vector3 none));
            Assert.Equal(Vector3.Zero, none);

            var (module, log) = StubWindModule.Make();
            scene.UseWindModule(module);
            Assert.False(scene.TryGetWind(new Vector3(float.NaN, 17f, 23f), out _));
            Assert.Empty(log.Asked);
        }
        finally { scene.Dispose(); }
    }
}
