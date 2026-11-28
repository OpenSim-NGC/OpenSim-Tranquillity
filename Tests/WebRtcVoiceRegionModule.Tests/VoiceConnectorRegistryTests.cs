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
using Xunit;
using Nini.Config;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void ValidRecord_Loads()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", ValidKeys())), Token);
            Assert.Empty(r.Refusals);
            Assert.Empty(r.SkippedDisabled);
            Assert.Equal(1, r.Registry.Count);
            VoiceConnectorRecord rec = r.Registry.Snapshot()[0];
            Assert.Equal("Recorder", rec.Name);
            Assert.Equal("Recorder NPC", rec.NpcFullName);
            Assert.Equal(new Vector3(128, 128, 25), rec.Position);
            Assert.Equal(VoiceConnectorScope.Estate, rec.Scope);
            Assert.False(rec.MayInject);
            Assert.Equal("Operator", rec.AuthorisedBy);
            {
                var assertedValue = rec.NpcId;
                TestAssert.WithMessage(() => Assert.Equal(UUID.Zero, assertedValue), "no NPC exists until the record is registered");
            }
        }

        [Fact(Timeout = 60000)]
        public void MissingToken_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Sneaky", ValidKeys("Rec", "Order"))), Token);
            Assert.Equal(0, r.Registry.Count);
            Assert.Single(r.Refusals);
            Assert.Equal("VoiceConnector.Sneaky", r.Refusals[0].SectionName);
            {
                var assertedValue = r.Refusals[0].Reason;
                TestAssert.WithMessage(() => Assert.Contains(Token, assertedValue), "the reason names the token");
            }
        }

        [Fact(Timeout = 60000)]
        public void Token_AsFirstOrLastName_IsAccepted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(Source(
                ("VoiceConnector.A", ValidKeys("NPC", "Recorder")),
                ("VoiceConnector.B", ValidKeys("Scribe", "NPC"))), Token);
            Assert.Empty(r.Refusals);
            Assert.Equal(2, r.Registry.Count);
        }

        [Fact(Timeout = 60000)]
        public void NonEstateScope_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (string, string)[] keys = ValidKeys();
            keys[4] = ("Scope", "parcel");
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", keys)), Token);
            Assert.Equal(0, r.Registry.Count);
            Assert.Single(r.Refusals);
            Assert.Contains("estate", r.Refusals[0].Reason);
        }

        [Fact(Timeout = 60000)]
        public void BadPosition_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (string, string)[] keys = ValidKeys();
            keys[3] = ("Position", "not-a-vector");
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", keys)), Token);
            Assert.Equal(0, r.Registry.Count);
            Assert.Single(r.Refusals);
            Assert.Contains("Position", r.Refusals[0].Reason);
        }

        [Fact(Timeout = 60000)]
        public void EmptyName_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", ValidKeys("", "NPC"))), Token);
            Assert.Equal(0, r.Registry.Count);
            Assert.Single(r.Refusals);
            Assert.Contains("non-empty", r.Refusals[0].Reason);
        }

        [Fact(Timeout = 60000)]
        public void Disabled_IsSkipped_NotRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (string, string)[] keys = ValidKeys();
            keys[0] = ("Enabled", "false");
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Parked", keys)), Token);
            Assert.Equal(0, r.Registry.Count);
            {
                var assertedValue = r.Refusals;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "disabled is parked, not an error");
            }
            Assert.Equal(new[] { "VoiceConnector.Parked" }, r.SkippedDisabled);
        }

        [Fact(Timeout = 60000)]
        public void DuplicateNpcName_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(Source(
                ("VoiceConnector.A", ValidKeys("Recorder", "NPC")),
                ("VoiceConnector.B", ValidKeys("Recorder", "NPC"))), Token);
            {
                var assertedValue = r.Registry.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the first record stands");
            }
            Assert.Single(r.Refusals);
            Assert.Equal("VoiceConnector.B", r.Refusals[0].SectionName);
            Assert.Contains("duplicate", r.Refusals[0].Reason);
        }

        [Fact(Timeout = 60000)]
        public void IsConnectorIdentity_FalseWhenEmpty()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorLoadResult r = VoiceConnectorRegistry.LoadFrom(
                Source(("VoiceConnector.Recorder", ValidKeys())), Token);
            Assert.False(r.Registry.IsConnectorIdentity(UUID.Random()));
            Assert.False(r.Registry.IsConnectorIdentity(UUID.Zero), "Zero is never a connector");
        }
    }
}
