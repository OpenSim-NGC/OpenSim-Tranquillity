/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// llWind with the region's real wind module: the weather volumes' horizontal wind is in what the script reads, a
    /// vertical part never is (llWind has none, in Second Life and in Halcyon), and with weather off the script reads
    /// the wind model's wind as before.
    /// </summary>
    // Each test builds its own scene and wind module and reads no clock, so the class runs in parallel.
    public class LSL_ApiWindTests : OpenSimTestCase
    {
        private readonly Scene m_scene;
        private readonly SceneObjectGroup m_group;
        private readonly LSL_Api m_api;
        private readonly WindModule m_wind;

        public LSL_ApiWindTests()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");

            m_scene = new SceneHelpers().SetupScene();
            m_group = SceneHelpers.AddSceneObject(m_scene, 1, UUID.Random(), "wind-", 0x10);
            m_group.AbsolutePosition = new Vector3(100f, 100f, 50f);

            MockScriptEngine engine = new MockScriptEngine();
            engine.Initialise(config);
            engine.AddRegion(m_scene);

            m_api = new LSL_Api();
            m_api.Initialize(engine, m_group.RootPart, null);

            var windConfig = new IniConfigSource();
            windConfig.AddConfig("Wind").Set("wind_plugin", "ConfigurableWind");
            windConfig.AddConfig("Weather").Set("enabled", "true");
            m_wind = new WindModule();
            m_wind.Initialise(windConfig);
            m_wind.AddRegion(m_scene);
        }

        private int AddVolume(Vector3 velocity)
            => m_wind.WindVolumes.Create(m_group.UUID, UUID.Random(), new WindVolumeSpec
            {
                Shape = WindVolumeShape.Sphere, Center = new Vector3(100f, 100f, 50f), Radius = 20f, FalloffStart = 1f,
                LifetimeSeconds = 600f, Velocity = velocity,
            });

        [Fact]
        public void llWind_includes_a_volumes_horizontal_wind_and_keeps_Z_zero()
        {
            LSL_Types.Vector3 before = m_api.llWind(new LSL_Types.Vector3(0, 0, 0));
            Assert.Equal(0.0, before.z);

            Assert.True(AddVolume(new Vector3(5f, -2f, 3f)) >= 1);
            LSL_Types.Vector3 after = m_api.llWind(new LSL_Types.Vector3(0, 0, 0));

            Assert.Equal(before.x + 5.0, after.x, 4);
            Assert.Equal(before.y - 2.0, after.y, 4);
            Assert.Equal(0.0, after.z);
        }

        [Fact]
        public void llWind_with_a_volume_that_has_only_a_vertical_wind_changes_nothing_a_script_can_read()
        {
            LSL_Types.Vector3 before = m_api.llWind(new LSL_Types.Vector3(0, 0, 0));

            Assert.True(AddVolume(new Vector3(0f, 0f, 9f)) >= 1);
            LSL_Types.Vector3 after = m_api.llWind(new LSL_Types.Vector3(0, 0, 0));

            Assert.Equal(before.x, after.x);
            Assert.Equal(before.y, after.y);
            Assert.Equal(0.0, after.z);
        }

        [Fact]
        public void llWind_reads_the_same_with_weather_on_and_no_volume_as_the_wind_model_alone()
        {
            Vector3 model = m_wind.WindSpeed(100, 100, 50);
            LSL_Types.Vector3 seen = m_api.llWind(new LSL_Types.Vector3(0, 0, 0));

            Assert.Equal((double)model.X, seen.x);
            Assert.Equal((double)model.Y, seen.y);
            Assert.Equal(0.0, seen.z);
        }
    }
}
