/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Loader tests for the voice-connector policy
 * registry. The record is the authorisation and the NpcNameToken check is
 * the disclosure layer, so every refusal path is pinned here: missing token, non-estate
 * scope, unparsable position, empty names, duplicates. Disabled records are skipped (parked), not
 * refused. IsConnectorIdentity is vacuously false after loading (NpcId slots are only populated at
 * registration).
 */
using NUnit.Framework;
using Nini.Config;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceConnectorRegistryTests
    {
        private const string Token = "NPC";

        private static IConfigSource Source(params (string Section, (string Key, string Value)[] Keys)[] sections)
        {
            IniConfigSource cfg = new IniConfigSource();
            foreach ((string section, (string, string)[] keys) in sections)
            {
                IConfig s = cfg.AddConfig(section);
                foreach ((string k, string v) in keys)
                    s.Set(k, v);
            }
            return cfg;
        }

        private static (string, string)[] ValidKeys(string first = "Recorder", string last = "NPC") => new[]
        {
            ("Enabled", "true"),
            ("NpcFirstName", first),
            ("NpcLastName", last),
            ("Position", "<128, 128, 25>"),
            ("Scope", "estate"),
            ("MayInject", "false"),
            ("AuthorisedBy", "Operator"),
        };

        [Test]
        public void ValidRecord_Loads()
        {
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", ValidKeys())), Token);
            Assert.That(r.Refusals, Is.Empty);
            Assert.That(r.SkippedDisabled, Is.Empty);
            Assert.That(r.Registry.Count, Is.EqualTo(1));
            VoiceConnectorRecord rec = r.Registry.Snapshot()[0];
            Assert.That(rec.Name, Is.EqualTo("Recorder"));
            Assert.That(rec.NpcFullName, Is.EqualTo("Recorder NPC"));
            Assert.That(rec.Position, Is.EqualTo(new Vector3(128, 128, 25)));
            Assert.That(rec.Scope, Is.EqualTo(VoiceConnectorScope.Estate));
            Assert.That(rec.MayInject, Is.False);
            Assert.That(rec.AuthorisedBy, Is.EqualTo("Operator"));
            Assert.That(rec.InjectSourceUrl, Is.Null, "absent optional key stays null");
            Assert.That(rec.NpcId, Is.EqualTo(UUID.Zero), "no NPC exists until the record is registered");
        }

        [Test]
        public void MissingToken_IsRefused()
        {
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Sneaky", ValidKeys("Rec", "Order"))), Token);
            Assert.That(r.Registry.Count, Is.EqualTo(0));
            Assert.That(r.Refusals, Has.Count.EqualTo(1));
            Assert.That(r.Refusals[0].SectionName, Is.EqualTo("VoiceConnector.Sneaky"));
            Assert.That(r.Refusals[0].Reason, Does.Contain(Token), "the reason names the token");
        }

        [Test]
        public void Token_AsFirstOrLastName_IsAccepted()
        {
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(Source(
                ("VoiceConnector.A", ValidKeys("NPC", "Recorder")),
                ("VoiceConnector.B", ValidKeys("Scribe", "NPC"))), Token);
            Assert.That(r.Refusals, Is.Empty);
            Assert.That(r.Registry.Count, Is.EqualTo(2));
        }

        [Test]
        public void NonEstateScope_IsRefused()
        {
            (string, string)[] keys = ValidKeys();
            keys[4] = ("Scope", "parcel");
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", keys)), Token);
            Assert.That(r.Registry.Count, Is.EqualTo(0));
            Assert.That(r.Refusals, Has.Count.EqualTo(1));
            Assert.That(r.Refusals[0].Reason, Does.Contain("estate"));
        }

        [Test]
        public void BadPosition_IsRefused()
        {
            (string, string)[] keys = ValidKeys();
            keys[3] = ("Position", "not-a-vector");
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", keys)), Token);
            Assert.That(r.Registry.Count, Is.EqualTo(0));
            Assert.That(r.Refusals, Has.Count.EqualTo(1));
            Assert.That(r.Refusals[0].Reason, Does.Contain("Position"));
        }

        [Test]
        public void EmptyName_IsRefused()
        {
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", ValidKeys("", "NPC"))), Token);
            Assert.That(r.Registry.Count, Is.EqualTo(0));
            Assert.That(r.Refusals, Has.Count.EqualTo(1));
            Assert.That(r.Refusals[0].Reason, Does.Contain("non-empty"));
        }

        [Test]
        public void Disabled_IsSkipped_NotRefused()
        {
            (string, string)[] keys = ValidKeys();
            keys[0] = ("Enabled", "false");
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Parked", keys)), Token);
            Assert.That(r.Registry.Count, Is.EqualTo(0));
            Assert.That(r.Refusals, Is.Empty, "disabled is parked, not an error");
            Assert.That(r.SkippedDisabled, Is.EqualTo(new[] { "VoiceConnector.Parked" }));
        }

        [Test]
        public void DuplicateNpcName_IsRefused()
        {
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(Source(
                ("VoiceConnector.A", ValidKeys("Recorder", "NPC")),
                ("VoiceConnector.B", ValidKeys("Recorder", "NPC"))), Token);
            Assert.That(r.Registry.Count, Is.EqualTo(1), "the first record stands");
            Assert.That(r.Refusals, Has.Count.EqualTo(1));
            Assert.That(r.Refusals[0].SectionName, Is.EqualTo("VoiceConnector.B"));
            Assert.That(r.Refusals[0].Reason, Does.Contain("duplicate"));
        }

        [Test]
        public void IsConnectorIdentity_FalseWhenEmpty()
        {
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", ValidKeys())), Token);
            Assert.That(r.Registry.IsConnectorIdentity(UUID.Random()), Is.False);
            Assert.That(r.Registry.IsConnectorIdentity(UUID.Zero), Is.False, "Zero is never a connector");
        }
    }
}
