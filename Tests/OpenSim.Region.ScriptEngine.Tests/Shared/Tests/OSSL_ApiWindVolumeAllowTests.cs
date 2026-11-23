/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// osWindVolumeCreate and osWindVolumeRemove under the Allow_ entries that osslDefaultEnable.ini ships, with the
    /// threat level below High: the OSSL gate admits the same owners as the functions' own rule (estate owner, estate
    /// manager or god) and as Phlox's iwWindVolumeCreate and iwWindVolumeRemove, and refuses everyone else.
    /// </summary>
    // OSSL_Api keeps its settings (threat level, Allow_ entries) in statics read once; each test resets them before and
    // after, as OSSL_ApiSetScriptEngineTests does. The assembly runs its tests one at a time (LSL_ApiHttpTests.cs:
    // DisableTestParallelization), so the reset cannot race another class.
    public class OSSL_ApiWindVolumeAllowTests : OpenSimTestCase
    {
        private class StoreWind : DispatchProxy
        {
            public IWindVolumeStore? Store;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod!.Name == "get_WindVolumes") return Store;
                var rt = targetMethod.ReturnType;
                return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
            }
        }

        private Scene m_scene = null!;
        private MockScriptEngine m_engine = null!;
        private UUID m_owner;
        private SceneObjectGroup m_group = null!;
        private OSSL_Api m_api = null!;
        private IWindVolumeStore m_store = null!;

        private static void ResetOsslSharedState()
        {
            Type api = typeof(OSSL_Api);
            void Set(string name, object? value)
                => api.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(null, value);
            Set("m_doneSharedInit", false);
            Set("m_OSFunctionsEnabled", false);
            Set("m_PermissionErrortoOwner", false);
            Set("m_MaxThreatLevel", ThreatLevel.VeryLow);
            Set("m_osslconfig", null);
            ((System.Collections.IDictionary)api.GetField("m_FunctionPerms", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Clear();
        }

        public override void Dispose()
        {
            ResetOsslSharedState();
            base.Dispose();
        }

        // The value of an Allow_ line of the shipped osslDefaultEnable.ini, read from the source tree.
        private static string ShippedAllow(string function)
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Tranquillity.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            string ini = Path.Combine(dir!, "Source", "OpenSim.Server.RegionServer", "AppData", "config-include", "osslDefaultEnable.ini");
            string line = File.ReadLines(ini).Single(l => l.TrimStart().StartsWith("Allow_" + function + " ", StringComparison.Ordinal)
                || l.TrimStart().StartsWith("Allow_" + function + "=", StringComparison.Ordinal));
            return line.Substring(line.IndexOf('=') + 1).Trim();
        }

        // Threat level VeryLow, so the High functions pass the OSSL gate only through their shipped Allow_ entries.
        private void Setup(bool manager, bool god)
        {
            ResetOsslSharedState();
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");
            IConfig ossl = config.AddConfig("OSSL");
            ossl.Set("DebuggerSafe", false);
            ossl.Set("AllowOSFunctions", "true");
            ossl.Set("OSFunctionThreatLevel", "VeryLow");
            ossl.Set("Allow_osWindVolumeCreate", ShippedAllow("osWindVolumeCreate"));
            ossl.Set("Allow_osWindVolumeRemove", ShippedAllow("osWindVolumeRemove"));

            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, config);
            m_engine = new MockScriptEngine();
            m_engine.Initialise(config);
            m_engine.AddRegion(m_scene);

            m_owner = TestHelpers.ParseTail(0x1);
            if (manager)
                m_scene.RegionInfo.EstateSettings.EstateOwner = m_owner;
            m_group = SceneHelpers.CreateSceneObject(1, m_owner, 0x10);
            m_scene.AddSceneObject(m_group);
            m_scene.Permissions.OnIsAdministrator += id => god && id == m_owner;
            m_scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => manager && user == m_owner;

            var proxy = DispatchProxy.Create<IWindModule, StoreWind>();
            m_store = ((StoreWind)(object)proxy).Store = new WindVolumeStore(true);
            m_scene.RegisterModuleInterface<IWindModule>(proxy);

            // The script's item: the Allow_ entries are checked against its owner (OSSL_Api.CheckThreatLevelTest).
            var item = new TaskInventoryItem { ItemID = UUID.Random(), OwnerID = m_owner, Name = "wind volume script" };
            m_api = new OSSL_Api();
            m_api.Initialize(m_engine, m_group.RootPart, item);
        }

        private static LSL_Types.list Rules()
            => new LSL_Types.list(
                (LSL_Types.LSLInteger)WindVolumeRules.WV_CENTER, new LSL_Types.Vector3(128, 128, 60),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_RADIUS, new LSL_Types.LSLFloat(10),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_VELOCITY, new LSL_Types.Vector3(5, 0, 2),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_LIFETIME, new LSL_Types.LSLFloat(60));

        [Fact]
        public void TheShippedEntryLetsAGodOutsideTheEstateCreateAVolume()
        {
            TestHelpers.InMethod();
            Setup(manager: false, god: true);

            int handle = 0;
            Exception thrown = Record.Exception(() => handle = m_api.osWindVolumeCreate(Rules()));

            Assert.Null(thrown);
            Assert.True(handle >= 1, $"got {handle}");
            Assert.Equal(1, m_store.Count);
        }

        [Fact]
        public void TheShippedEntryLetsAGodOutsideTheEstateRemoveItsVolume()
        {
            TestHelpers.InMethod();
            Setup(manager: false, god: true);
            int handle = m_store.Create(m_group.UUID, m_owner, new WindVolumeSpec
            {
                Shape = WindVolumeShape.Sphere, Center = new Vector3(128, 128, 60), Radius = 10f, FalloffStart = 0.5f,
                LifetimeSeconds = 60f, Velocity = new Vector3(5, 0, 0),
            });

            int result = -100;
            Exception thrown = Record.Exception(() => result = m_api.osWindVolumeRemove(handle));

            Assert.Null(thrown);
            Assert.Equal(WindVolumeError.Ok, result);
            Assert.Equal(0, m_store.Count);
        }

        [Fact]
        public void TheShippedEntryLetsTheEstateOwnerCreateAndRemove()
        {
            TestHelpers.InMethod();
            Setup(manager: true, god: false);

            int handle = m_api.osWindVolumeCreate(Rules());
            Assert.True(handle >= 1, $"got {handle}");
            Assert.Equal(WindVolumeError.Ok, (int)m_api.osWindVolumeRemove(handle));
            Assert.Equal(0, m_store.Count);
        }

        [Fact]
        public void TheShippedEntryRefusesAnOwnerOutsideTheEstateWhoIsNotAGod()
        {
            TestHelpers.InMethod();
            Setup(manager: false, god: false);

            Exception create = Assert.ThrowsAny<Exception>(() => m_api.osWindVolumeCreate(Rules()));
            Assert.Contains("osWindVolumeCreate permission denied", create.Message);
            Assert.Equal(0, m_store.Count);

            int existing = m_store.Create(m_group.UUID, m_owner, new WindVolumeSpec
            {
                Shape = WindVolumeShape.Sphere, Center = new Vector3(128, 128, 60), Radius = 10f, FalloffStart = 0.5f,
                LifetimeSeconds = 60f, Velocity = new Vector3(5, 0, 0),
            });
            Exception remove = Assert.ThrowsAny<Exception>(() => m_api.osWindVolumeRemove(existing));
            Assert.Contains("osWindVolumeRemove permission denied", remove.Message);
            Assert.Equal(1, m_store.Count);
        }
    }
}
