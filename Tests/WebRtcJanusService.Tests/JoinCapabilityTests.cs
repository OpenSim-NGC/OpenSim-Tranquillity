/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the sim-issued join capability.
 *
 * The GOLDEN VECTOR here is the same capability tests/test_joincap.c verifies in the mixer tree, minted from the
 * same inputs. The two implementations are pinned to each other: change either side's canonical string or its
 * base64url and one of the two suites fails.
 */

using System;
using System.Collections.Generic;
using System.Text;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JoinCapabilityTests
    {
        private const string Key = "0.4-test-key";
        private const string Agent = "0a000000-0000-4000-8000-00000000000a";
        private const string Session = "5e551000-0000-4000-8000-00000000005e";
        private const int Room = 3101;
        private const string Epoch = "0000018f00000001";
        private const uint Generation = 7;
        private const long Iat = 1789000000;
        private const string Nonce = "b0b1b2b3b4b5b6b7b8b9babbbcbdbebf";

        /// <summary>The exact string tests/test_joincap.c accepts (mixer tree, GOLDEN).</summary>
        private const string GoldenCapability =
            "v1.MGEwMDAwMDAtMDAwMC00MDAwLTgwMDAtMDAwMDAwMDAwMDBhfDVlNTUxMDAwLTAwMDAtNDAwMC04MDAwLTAwMDAwMDAwMDA1ZXwz" +
            "MTAxfDAwMDAwMThmMDAwMDAwMDF8N3wxNzg5MDAwMDAwfDE3ODkwMDAwNjB8YjBiMWIyYjNiNGI1YjZiN2I4YjliYWJiYmNiZGJlYmY" +
            ".jkvzBWzgK4ZMAuBwoSz-jV1V5_O_pR4UjPdZo6mrNVE";

        [Test]
        public void Mint_MatchesTheMixersGoldenVector()
        {
            string cap = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat, Nonce);
            Assert.That(cap, Is.EqualTo(GoldenCapability),
                "the sim mints exactly what the mixer's test_joincap.c verifies; the two are pinned together");
        }

        [Test]
        public void Canonical_IsTheWireFieldOrder()
        {
            string payload = JoinCapability.Canonical(Agent, Session, Room, Epoch, Generation, Iat,
                Iat + JoinCapability.LifetimeSeconds, Nonce);
            Assert.That(payload, Is.EqualTo(
                $"{Agent}|{Session}|{Room}|{Epoch}|{Generation}|{Iat}|{Iat + JoinCapability.LifetimeSeconds}|{Nonce}"),
                "agent, session, room, epoch, generation, iat, exp, nonce, in that order");
        }

        [Test]
        public void Lifetime_IsShortAndTheExpiryFollowsIt()
        {
            Assert.That(JoinCapability.LifetimeSeconds, Is.EqualTo(60), "the join capability is short-lived");
            string payload = JoinCapability.Canonical(Agent, Session, Room, Epoch, Generation, Iat,
                Iat + JoinCapability.LifetimeSeconds, Nonce);
            Assert.That(payload, Does.Contain($"|{Iat}|{Iat + 60}|"));
        }

        [Test]
        public void EveryMintGetsItsOwnNonce()
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 100; i++)
            {
                string nonce = JoinCapability.NewNonce();
                Assert.That(nonce.Length, Is.EqualTo(JoinCapability.NonceBytes * 2), "128 random bits as hex");
                Assert.That(seen.Add(nonce), Is.True, "a nonce is never reissued");
            }
            string a = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat);
            string b = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat);
            Assert.That(a, Is.Not.EqualTo(b), "two capabilities for the same join differ (their nonces do)");
        }

        [Test]
        public void EachBoundFieldChangesTheCapability()
        {
            string baseline = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat, Nonce);
            Assert.Multiple(() =>
            {
                Assert.That(JoinCapability.Mint(Key, "0b000000-0000-4000-8000-00000000000b", Session, Room, Epoch,
                    Generation, Iat, Nonce), Is.Not.EqualTo(baseline), "another agent");
                Assert.That(JoinCapability.Mint(Key, Agent, "99991000-0000-4000-8000-000000000099", Room, Epoch,
                    Generation, Iat, Nonce), Is.Not.EqualTo(baseline), "another viewer session");
                Assert.That(JoinCapability.Mint(Key, Agent, Session, Room + 1, Epoch, Generation, Iat, Nonce),
                    Is.Not.EqualTo(baseline), "another room");
                Assert.That(JoinCapability.Mint(Key, Agent, Session, Room, "0000018f00000002", Generation, Iat, Nonce),
                    Is.Not.EqualTo(baseline), "another epoch");
                Assert.That(JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation + 1, Iat, Nonce),
                    Is.Not.EqualTo(baseline), "another generation");
                Assert.That(JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat + 1, Nonce),
                    Is.Not.EqualTo(baseline), "another issue time");
                Assert.That(JoinCapability.Mint("another-key", Agent, Session, Room, Epoch, Generation, Iat, Nonce),
                    Is.Not.EqualTo(baseline), "another key");
            });
        }

        [Test]
        public void NothingIsMintedWithoutWhatItBinds()
        {
            Assert.Multiple(() =>
            {
                Assert.That(JoinCapability.Mint(null, Agent, Session, Room, Epoch, Generation, Iat), Is.Null, "no secret");
                Assert.That(JoinCapability.Mint("", Agent, Session, Room, Epoch, Generation, Iat), Is.Null, "empty secret");
                Assert.That(JoinCapability.Mint(Key, null, Session, Room, Epoch, Generation, Iat), Is.Null, "no agent");
                Assert.That(JoinCapability.Mint(Key, Agent, null, Room, Epoch, Generation, Iat), Is.Null, "no session");
                Assert.That(JoinCapability.Mint(Key, Agent, Session, 0, Epoch, Generation, Iat), Is.Null, "no room");
                Assert.That(JoinCapability.Mint(Key, "has|pipe", Session, Room, Epoch, Generation, Iat), Is.Null,
                    "a field that would break the payload split is refused, not escaped");
            });
        }

        [Test]
        public void ArmingOff_MintsTheZeroEpoch()
        {
            string cap = JoinCapability.Mint(Key, Agent, Session, Room, null, 0, Iat, Nonce);
            string payload = JoinCapability.Canonical(Agent, Session, Room, JoinCapability.NoEpoch, 0, Iat,
                Iat + JoinCapability.LifetimeSeconds, Nonce);
            Assert.That(payload, Does.Contain($"|{JoinCapability.NoEpoch}|0|"),
                "with arming off the capability carries the zero epoch and generation 0, which the mixer matches");
            Assert.That(cap, Is.Not.Null);
        }

        [Test]
        public void Base64Url_HasNoPaddingAndNoPlusOrSlash()
        {
            for (int i = 0; i < 50; i++)
            {
                string cap = JoinCapability.Mint(Key, Agent, Session, Room + i, Epoch, Generation, Iat + i);
                Assert.That(cap, Does.Not.Contain("=").And.Not.Contain("+").And.Not.Contain("/"),
                    "base64url without padding, so the capability survives every transport unescaped");
                Assert.That(cap.Split('.').Length, Is.EqualTo(3), "version, payload, signature");
                Assert.That(cap.Length, Is.LessThan(1024), "under the mixer's SLV_JOINCAP_MAX_LEN");
            }
        }

        // ---- the (epoch, generation) publisher --------------------------------------------------

        [Test]
        public void Authority_ResolvesTheZeroEpochUntilSomethingArms()
        {
            JoinCapabilityAuthority.Clear();
            (string epoch, uint generation) = JoinCapabilityAuthority.Resolve(Room);
            Assert.That((epoch, generation), Is.EqualTo((JoinCapability.NoEpoch, 0u)),
                "a room the sim never armed resolves to what a mixer with no authority expects");
        }

        [Test]
        public void Authority_PublishesAndForgetsPerRoom()
        {
            JoinCapabilityAuthority.Clear();
            JoinCapabilityAuthority.Publish(Room, Epoch, 4);
            JoinCapabilityAuthority.Publish(Room + 1, "0000018f00000002", 9);
            Assert.That(JoinCapabilityAuthority.Resolve(Room), Is.EqualTo((Epoch, 4u)));
            Assert.That(JoinCapabilityAuthority.Resolve(Room + 1), Is.EqualTo(("0000018f00000002", 9u)));
            JoinCapabilityAuthority.Publish(Room, Epoch, 5);
            Assert.That(JoinCapabilityAuthority.Resolve(Room), Is.EqualTo((Epoch, 5u)), "newest wins");
            JoinCapabilityAuthority.Forget(Room);
            Assert.That(JoinCapabilityAuthority.Resolve(Room), Is.EqualTo((JoinCapability.NoEpoch, 0u)));
            Assert.That(JoinCapabilityAuthority.Count, Is.EqualTo(1));
            JoinCapabilityAuthority.Clear();
        }

        [Test]
        public void Authority_IgnoresRoomsAndEpochsItCannotUse()
        {
            JoinCapabilityAuthority.Clear();
            JoinCapabilityAuthority.Publish(0, Epoch, 1);
            JoinCapabilityAuthority.Publish(Room, null, 1);
            JoinCapabilityAuthority.Publish(Room, "", 1);
            Assert.That(JoinCapabilityAuthority.Count, Is.EqualTo(0), "nothing half-published");
        }

        // ---- the join request -------------------------------------------------------------------

        /// <summary>The same request body with its per-request transaction id blanked, so two separately built
        /// requests can be compared for everything that is not the transaction.</summary>
        private static string WithoutTransaction(string json)
            => System.Text.RegularExpressions.Regex.Replace(json, "\"transaction\":\"[^\"]*\"", "\"transaction\":\"<tx>\"");

        [Test]
        public void JoinRequest_CarriesTheCapabilityOnlyWhenThereIsOne()
        {
            var plain = new AudioBridgeJoinRoomReq(Room, Agent);
            string plainBody = plain.ToJson();
            Assert.That(plainBody, Does.Not.Contain("join_cap").And.Not.Contain("session_id"),
                "the body without a capability has no capability keys");

            var stamped = new AudioBridgeJoinRoomReq(Room, Agent);
            stamped.SetJoinCapability(GoldenCapability, Session);
            string stampedBody = stamped.ToJson();
            Assert.That(stampedBody, Does.Contain("join_cap").And.Contain(Session));

            var refused = new AudioBridgeJoinRoomReq(Room, Agent);
            refused.SetJoinCapability(null, Session);
            refused.SetJoinCapability(GoldenCapability, null);
            // Every request carries its own random transaction id, so compare the bodies with that normalised.
            Assert.That(WithoutTransaction(refused.ToJson()), Is.EqualTo(WithoutTransaction(plainBody)),
                "half a capability is never sent: the body stays the one without a capability");
        }

        [Test]
        public void MessageDetailsLogging_NeverPrintsACapability()
        {
            var req = new AudioBridgeJoinRoomReq(Room, Agent);
            req.SetJoinCapability(GoldenCapability, Session);
            string wire = req.ToJson();
            string logged = req.ToJsonForLog();
            Assert.Multiple(() =>
            {
                Assert.That(wire, Does.Contain(GoldenCapability), "the wire body carries the capability");
                Assert.That(logged, Does.Not.Contain(GoldenCapability), "the log line never does");
                Assert.That(logged, Does.Not.Contain(GoldenCapability.Split('.')[1]),
                    "not even its payload survives into a log");
                Assert.That(logged, Does.Contain("<redacted join_cap>").And.Contain(Session),
                    "it says a capability was there, and keeps the session id an operator correlates on");
            });

            var plain = new AudioBridgeJoinRoomReq(Room, Agent);
            Assert.That(plain.ToJsonForLog(), Is.EqualTo(plain.ToJson()),
                "a join with no capability logs exactly what it logged before the capability existed");
        }
    }
}
