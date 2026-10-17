/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// osWindVolumeCreate, osWindVolumeRemove and osWindVolumes (who may use them, what they return with weather off,
    /// that they reach the region's store) and osSetWindParam, which with weather on needs an estate owner, estate
    /// manager or god and with weather off is as before.
    /// </summary>
    // Each test builds its own scene and store and reads no clock, so the class runs in parallel.
    public class OSSL_ApiWindVolumeTests : OpenSimTestCase
    {
        private class StoreWind : DispatchProxy
        {
            public IWindVolumeStore Store;
            public readonly List<(string Plugin, string Param, float Value)> ParamSets = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_WindVolumes") return Store;
                if (targetMethod.Name == "WindParamSet")
                    lock (ParamSets) ParamSets.Add(((string)args[0], (string)args[1], (float)args[2]));
                var rt = targetMethod.ReturnType;
                return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
            }
        }

        private Scene m_scene;
        private MockScriptEngine m_engine;
        private UUID m_owner;
        private SceneObjectGroup m_group;
        private OSSL_Api m_api;
        private StoreWind m_wind;

        private void Setup(bool weatherOn, bool manager, bool god = false, bool withStore = true)
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");
            IConfig ossl = config.AddConfig("OSSL");
            ossl.Set("DebuggerSafe", false);
            ossl.Set("AllowOSFunctions", "true");
            ossl.Set("OSFunctionThreatLevel", "Severe");

            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, config);
            m_engine = new MockScriptEngine();
            m_engine.Initialise(config);
            m_engine.AddRegion(m_scene);

            m_owner = TestHelpers.ParseTail(0x1);
            m_group = SceneHelpers.CreateSceneObject(1, m_owner, 0x10);
            m_scene.AddSceneObject(m_group);
            m_scene.Permissions.OnIsAdministrator += id => god && id == m_owner;
            m_scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => manager && user == m_owner;

            var proxy = DispatchProxy.Create<IWindModule, StoreWind>();
            m_wind = (StoreWind)(object)proxy;
            m_wind.Store = withStore ? new WindVolumeStore(weatherOn) : null;
            m_scene.RegisterModuleInterface<IWindModule>(proxy);

            m_api = new OSSL_Api();
            m_api.Initialize(m_engine, m_group.RootPart, null);
        }

        private static LSL_Types.list Rules(float radius = 10f, float lifetime = 60f)
            => new LSL_Types.list(
                (LSL_Types.LSLInteger)WindVolumeRules.WV_CENTER, new LSL_Types.Vector3(128, 128, 60),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_RADIUS, new LSL_Types.LSLFloat(radius),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_VELOCITY, new LSL_Types.Vector3(5, 0, 2),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_LIFETIME, new LSL_Types.LSLFloat(lifetime));

        [Fact]
        public void AnEstateManagersObjectCanCreateListAndRemoveAVolume()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true);

            int handle = m_api.osWindVolumeCreate(Rules());
            Assert.True(handle >= 1, $"got {handle}");

            LSL_Types.list listed = m_api.osWindVolumes();
            Assert.Equal(1, listed.Length);
            Assert.Equal(handle, (int)(LSL_Types.LSLInteger)listed.Data[0]);

            Assert.Equal(0, (int)m_api.osWindVolumeRemove(handle));
            Assert.Equal(0, m_api.osWindVolumes().Length);
            Assert.Equal(0, m_wind.Store.Count);
        }

        [Fact]
        public void AGodsObjectCanCreateAVolume()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: false, god: true);
            Assert.True(m_api.osWindVolumeCreate(Rules()) >= 1);
        }

        [Fact]
        public void AnyoneElseIsRefusedOnEveryFunctionAndNothingIsStored()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: false);

            Assert.Equal(WindVolumeError.NotAllowed, (int)m_api.osWindVolumeCreate(Rules()));
            Assert.Equal(0, m_wind.Store.Count);

            int existing = m_wind.Store.Create(m_group.UUID, UUID.Random(), Spec());
            Assert.Equal(WindVolumeError.NotAllowed, (int)m_api.osWindVolumeRemove(existing));
            Assert.Equal(0, m_api.osWindVolumes().Length);
            Assert.Equal(1, m_wind.Store.Count);
        }

        [Fact]
        public void WithWeatherOffEveryFunctionAnswersDisabledAndNothingIsStored()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: false, manager: true);

            Assert.Equal(WindVolumeError.Disabled, (int)m_api.osWindVolumeCreate(Rules()));
            Assert.Equal(WindVolumeError.Disabled, (int)m_api.osWindVolumeRemove(1));
            Assert.Equal(0, m_api.osWindVolumes().Length);
            Assert.Equal(0, m_wind.Store.Count);
        }

        [Fact]
        public void ACreatedVolumeIsInTheStoreWithTheScriptsValues()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true);

            int handle = m_api.osWindVolumeCreate(new LSL_Types.list(
                (LSL_Types.LSLInteger)WindVolumeRules.WV_SHAPE, (LSL_Types.LSLInteger)WindVolumeRules.WV_CYLINDER,
                (LSL_Types.LSLInteger)WindVolumeRules.WV_CENTER, new LSL_Types.Vector3(100, 90, 40),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_RADIUS, new LSL_Types.LSLFloat(12),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_HEIGHT, new LSL_Types.LSLFloat(30),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_FALLOFF, new LSL_Types.LSLFloat(0.25),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_VELOCITY, new LSL_Types.Vector3(-3, 4, 1),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_LIFETIME, (LSL_Types.LSLInteger)90));

            WindVolume v = Assert.Single(m_wind.Store.List(UUID.Zero));
            Assert.Equal(handle, v.Id);
            Assert.Equal(m_group.UUID, v.CreatorObject);
            Assert.Equal(m_owner, v.Owner);
            Assert.Equal(WindVolumeShape.Cylinder, v.Shape);
            Assert.Equal(new Vector3(100, 90, 40), v.Center);
            Assert.Equal(12f, v.Radius);
            Assert.Equal(30f, v.Height);
            Assert.Equal(0.25f, v.FalloffStart);
            Assert.Equal(new Vector3(-3, 4, 1), v.Velocity);
        }

        [Fact]
        public void BadRulesAndOverCapValuesReturnTheirErrorAndStoreNothing()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true);

            Assert.Equal(WindVolumeError.InvalidParameter, (int)m_api.osWindVolumeCreate(new LSL_Types.list()));
            Assert.Equal(WindVolumeError.TooLarge, (int)m_api.osWindVolumeCreate(Rules(radius: 129f)));
            Assert.Equal(WindVolumeError.LifetimeTooLong, (int)m_api.osWindVolumeCreate(Rules(lifetime: 3601f)));
            Assert.Equal(0, m_wind.Store.Count);
        }

        [Fact]
        public void OsSetWindParamWithWeatherOnNeedsAnEstateOwnerManagerOrGod()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: false);
            m_api.osSetWindParam("ZephyrWind", "avg_strength", 9.0);
            Assert.Empty(m_wind.ParamSets);
        }

        [Fact]
        public void OsSetWindParamWithWeatherOnWorksForAnEstateManagerAndForAGod()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true);
            m_api.osSetWindParam("ZephyrWind", "avg_strength", 9.0);
            Assert.Equal(("ZephyrWind", "avg_strength", 9f), Assert.Single(m_wind.ParamSets));

            Setup(weatherOn: true, manager: false, god: true);
            m_api.osSetWindParam("ZephyrWind", "avg_strength", 7.0);
            Assert.Single(m_wind.ParamSets);
        }

        [Fact]
        public void OsSetWindParamWithWeatherOffIsAsBeforeForEveryone()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: false, manager: false);
            m_api.osSetWindParam("ZephyrWind", "avg_strength", 9.0);
            Assert.Equal(("ZephyrWind", "avg_strength", 9f), Assert.Single(m_wind.ParamSets));
        }

        [Fact]
        public void OsSetWindParamWithAModuleThatHasNoWeatherStoreIsAsBefore()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: false, withStore: false);
            m_api.osSetWindParam("ZephyrWind", "avg_strength", 9.0);
            Assert.Single(m_wind.ParamSets);
        }

        private static WindVolumeSpec Spec() => new WindVolumeSpec
        {
            Shape = WindVolumeShape.Sphere, Center = new Vector3(128, 128, 60), Radius = 10f, FalloffStart = 0.5f,
            LifetimeSeconds = 60f, Velocity = new Vector3(1, 0, 0),
        };
    }
}
