/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.Api.Plugins;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// With no SensorMaxResults in the configuration, a sensor of the shared plugin YEngine uses reports up to 32
    /// objects, Second Life's limit since its server release 2024-03-18 (https://wiki.secondlife.com/wiki/LlSensor).
    /// </summary>
    public class SensorMaxResultsTests
    {
        [Fact]
        public void A_sensor_with_no_configured_limit_reports_the_nearest_32_of_40_targets()
        {
            Scene scene = new SceneHelpers().SetupScene();
            MockScriptEngine engine = new MockScriptEngine();
            engine.Initialise(new IniConfigSource());
            engine.AddRegion(scene);

            SceneObjectGroup sensor = SceneHelpers.AddSceneObject(scene, "sensor", UUID.Random());
            sensor.AbsolutePosition = new Vector3(128, 128, 30);
            for (int i = 0; i < 40; i++)
            {
                SceneObjectGroup target = SceneHelpers.AddSceneObject(scene, "Target", UUID.Random());
                target.AbsolutePosition = new Vector3(128 + (i % 8), 130 + (i / 8), 30);
            }

            SensorRepeat plugin = new AsyncCommandManager(engine).SensorRepeatPlugin;
            UUID script = UUID.Random();
            // PASSIVE (4): objects that are neither physical nor scripted, within 20 m, all round.
            plugin.SenseOnce(sensor.RootPart.LocalId, script, "Target", UUID.Zero, 4, 20.0, Math.PI, sensor.RootPart);

            // The command manager's thread runs the sweep, as it does for llSensor.
            EventParams? sensed = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (engine.PostedEvents)
                    sensed = engine.PostedEvents.TryGetValue(script, out List<EventParams>? posted) ? posted.FirstOrDefault() : null;
                return sensed != null;
            }, TimeSpan.FromSeconds(10)));
            Assert.NotNull(sensed);
            Assert.Equal("sensor", sensed.EventName);
            Assert.Equal(32, sensed.DetectParams.Length);
            Assert.Equal(32, ((LSL_Types.LSLInteger)sensed.Params[0]).value);
        }
    }
}
