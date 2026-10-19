/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the [WebRtcVoice] StunServers setting: the list is advertised to viewers as the
 * "stun-servers" simulator feature, and nothing is advertised when the key is unset.
 */

using System.Reflection;

using Nini.Config;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Interfaces;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class StunServersTests
    {
        // Records the features the module adds.
        private sealed class RecordingFeatures : ISimulatorFeaturesModule
        {
            public readonly Dictionary<string, OSD> Added = new Dictionary<string, OSD>();

            public event SimulatorFeaturesRequestDelegate OnSimulatorFeaturesRequest { add { } remove { } }
            public void AddFeature(string name, OSD value) => Added[name] = value;
            public void AddOpenSimExtraFeature(string name, OSD value) { }
            public bool RemoveFeature(string name) => Added.Remove(name);
            public bool TryGetFeature(string name, out OSD value) => Added.TryGetValue(name, out value);
            public bool TryGetOpenSimExtraFeature(string name, out OSD value) { value = null; return false; }
            public bool OpenSimExtraFeatureContains(string name) => false;
            public OSDMap GetFeatures() => new OSDMap();
        }

        // Initialises a module from a [WebRtcVoice] section and advertises to a recording features module.
        private static Dictionary<string, OSD> Advertised(string stunServers)
        {
            var source = new IniConfigSource();
            IConfig section = source.AddConfig("WebRtcVoice");
            section.Set("Enabled", "true");
            if (stunServers is not null)
                section.Set("StunServers", stunServers);

            var module = new WebRtcVoiceRegionModule();
            module.Initialise(source);

            var features = new RecordingFeatures();
            typeof(WebRtcVoiceRegionModule)
                .GetMethod("AdvertiseStunServers", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(module, new object[] { features });
            return features.Added;
        }

        [Test]
        public void StunServers_Unset_AdvertisesNothing()
        {
            Assert.That(Advertised(null), Does.Not.ContainKey("stun-servers"));
        }

        [Test]
        public void StunServers_Blank_AdvertisesNothing()
        {
            Assert.That(Advertised("   "), Does.Not.ContainKey("stun-servers"));
        }

        [Test]
        public void StunServers_Set_IsAdvertisedAsGiven()
        {
            const string list = "stun:stun.example.org:3478,stun:stun2.example.org:3478";
            Dictionary<string, OSD> added = Advertised(list);
            Assert.That(added, Does.ContainKey("stun-servers"));
            Assert.That(added["stun-servers"].AsString(), Is.EqualTo(list));
        }
    }
}
