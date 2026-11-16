/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Reflection;

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests
{
    /// <summary>
    /// The region's SimulatorFeatures reply lists the script engines the region runs, by each IScriptModule's
    /// ScriptEngineName, under OpenSimExtras["ScriptEngines"], so a viewer's script editor can offer only those.
    /// </summary>
    // No test reaches a network service. Each test builds its own scene and modules and touches no process-wide
    // state (the class does not derive from OpenSimTestCase), so it runs in parallel.
    public class SimulatorFeaturesScriptEnginesTests
    {
        /// <summary>A script engine that answers with the name it is given.</summary>
        private sealed class NamedEngine : MockScriptEngine, IScriptModule
        {
            private readonly string m_engineName;

            public NamedEngine(string engineName)
            {
                m_engineName = engineName;
            }

            string IScriptModule.ScriptEngineName => m_engineName;
        }

        /// <summary>
        /// Builds a scene, adds the given engines to it the way a region does (each stacks IScriptModule in
        /// AddRegion), then loads a SimulatorFeaturesModule, whose RegionLoaded runs after every AddRegion.
        /// </summary>
        private static SimulatorFeaturesModule LoadRegion(params string[] engineNames)
        {
            Scene scene = new SceneHelpers().SetupScene();
            foreach (string name in engineNames)
            {
                NamedEngine engine = new NamedEngine(name);
                engine.Initialise(new IniConfigSource());
                engine.AddRegion(scene);
            }

            SimulatorFeaturesModule features = new SimulatorFeaturesModule();
            SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), features);
            return features;
        }

        /// <summary>A listener that is never started; it only keeps the handlers the caps give it.</summary>
        private sealed class Listener : BaseHttpServer
        {
            public Listener() : base(0) { }

            public ISimpleStreamHandler Handler(string path) => m_simpleStreamHandlers[path];
        }

        /// <summary>A GET request; the SimulatorFeatures handler reads nothing else from it.</summary>
        public class GetRequest : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "get_HttpMethod")
                    return "GET";
                Type type = method.ReturnType;
                return type != typeof(void) && type.IsValueType ? Activator.CreateInstance(type) : null;
            }
        }

        /// <summary>Asks the region's SimulatorFeatures cap, as a viewer does, and reads the LLSD it answers.</summary>
        private static OSDMap AskTheCap(SimulatorFeaturesModule features)
        {
            Listener listener = new Listener();
            Caps caps = new Caps(listener, "example.org", 9000, "/CAPS/" + UUID.Random(), UUID.Random(), "Example Region");
            features.RegisterCaps(caps.AgentID, caps);

            string path = caps.CapsHandlers.GetCapsLocalPaths()["SimulatorFeatures"];
            TestOSHttpResponse response = new TestOSHttpResponse();
            listener.Handler(path).Handle(DispatchProxy.Create<IOSHttpRequest, GetRequest>(), response);

            Assert.Equal(200, response.StatusCode);
            return Assert.IsType<OSDMap>(OSDParser.DeserializeLLSDXml(response.RawBuffer));
        }

        private static string[] Names(OSD list)
        {
            OSDArray array = Assert.IsType<OSDArray>(list);
            string[] names = new string[array.Count];
            for (int i = 0; i < array.Count; i++)
                names[i] = array[i].AsString();
            return names;
        }

        [Fact]
        public void TheRegionListsEachScriptEngineItRuns()
        {
            SimulatorFeaturesModule features = LoadRegion("Example.EngineA", "Example.EngineB");

            Assert.True(features.TryGetOpenSimExtraFeature("ScriptEngines", out OSD list));
            Assert.Equal(new[] { "Example.EngineA", "Example.EngineB" }, Names(list));
        }

        [Fact]
        public void TheViewerIsSentTheList()
        {
            SimulatorFeaturesModule features = LoadRegion("Example.EngineA");

            OSDMap sent = AskTheCap(features);
            OSDMap extras = Assert.IsType<OSDMap>(sent["OpenSimExtras"]);
            Assert.True(extras.ContainsKey("ScriptEngines"));
            Assert.Equal(new[] { "Example.EngineA" }, Names(extras["ScriptEngines"]));
        }

        [Fact]
        public void ARegionWithNoScriptEngineListsNone()
        {
            SimulatorFeaturesModule features = LoadRegion();

            Assert.True(features.TryGetOpenSimExtraFeature("ScriptEngines", out OSD list));
            Assert.Empty(Names(list));
        }

        [Fact]
        public void TheOtherExtrasStay()
        {
            SimulatorFeaturesModule features = LoadRegion("Example.EngineA");

            Assert.True(features.TryGetOpenSimExtraFeature("AvatarSkeleton", out OSD skeleton));
            Assert.True(skeleton.AsBoolean());
            Assert.True(features.OpenSimExtraFeatureContains("MaxSimHeight"));
        }
    }
}
