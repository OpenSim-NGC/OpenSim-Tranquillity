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

        [Fact(Timeout = 60000)]
        public void Mint_MatchesTheMixersGoldenVector()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string cap = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat, Nonce);
            {
                var assertedValue = cap;
                TestAssert.WithMessage(() => Assert.Equal(GoldenCapability, assertedValue), "the sim mints exactly what the mixer's test_joincap.c verifies; the two are pinned together");
            }
        }

        [Fact(Timeout = 60000)]
        public void Canonical_IsTheWireFieldOrder()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string payload = JoinCapability.Canonical(Agent, Session, Room, Epoch, Generation, Iat,
                Iat + JoinCapability.LifetimeSeconds, Nonce);
            {
                var assertedValue = payload;
                TestAssert.WithMessage(() => Assert.Equal($"{Agent}|{Session}|{Room}|{Epoch}|{Generation}|{Iat}|{Iat + JoinCapability.LifetimeSeconds}|{Nonce}", assertedValue), "agent, session, room, epoch, generation, iat, exp, nonce, in that order");
            }
        }

        [Fact(Timeout = 60000)]
        public void Lifetime_IsShortAndTheExpiryFollowsIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = JoinCapability.LifetimeSeconds;
                TestAssert.WithMessage(() => Assert.Equal(60, assertedValue), "the join capability is short-lived");
            }
            string payload = JoinCapability.Canonical(Agent, Session, Room, Epoch, Generation, Iat,
                Iat + JoinCapability.LifetimeSeconds, Nonce);
            Assert.Contains($"|{Iat}|{Iat + 60}|", payload);
        }

        [Fact(Timeout = 60000)]
        public void EveryMintGetsItsOwnNonce()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var seen = new HashSet<string>();
            for (int i = 0; i < 100; i++)
            {
                string nonce = JoinCapability.NewNonce();
                {
                    var assertedValue = nonce.Length;
                    TestAssert.WithMessage(() => Assert.Equal(JoinCapability.NonceBytes * 2, assertedValue), "128 random bits as hex");
                }
                Assert.True(seen.Add(nonce), "a nonce is never reissued");
            }
            string a = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat);
            string b = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat);
            {
                var assertedValue = a;
                TestAssert.WithMessage(() => Assert.NotEqual(b, assertedValue), "two capabilities for the same join differ (their nonces do)");
            }
        }

        [Fact(Timeout = 60000)]
        public void EachBoundFieldChangesTheCapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string baseline = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat, Nonce);
            Assert.Multiple(() =>
            {
                {
                    var assertedValue = JoinCapability.Mint(Key, "0b000000-0000-4000-8000-00000000000b", Session, Room, Epoch, Generation, Iat, Nonce);
                    TestAssert.WithMessage(() => Assert.NotEqual(baseline, assertedValue), "another agent");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, Agent, "99991000-0000-4000-8000-000000000099", Room, Epoch, Generation, Iat, Nonce);
                    TestAssert.WithMessage(() => Assert.NotEqual(baseline, assertedValue), "another viewer session");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, Agent, Session, Room + 1, Epoch, Generation, Iat, Nonce);
                    TestAssert.WithMessage(() => Assert.NotEqual(baseline, assertedValue), "another room");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, Agent, Session, Room, "0000018f00000002", Generation, Iat, Nonce);
                    TestAssert.WithMessage(() => Assert.NotEqual(baseline, assertedValue), "another epoch");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation + 1, Iat, Nonce);
                    TestAssert.WithMessage(() => Assert.NotEqual(baseline, assertedValue), "another generation");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, Agent, Session, Room, Epoch, Generation, Iat + 1, Nonce);
                    TestAssert.WithMessage(() => Assert.NotEqual(baseline, assertedValue), "another issue time");
                }
                {
                    var assertedValue = JoinCapability.Mint("another-key", Agent, Session, Room, Epoch, Generation, Iat, Nonce);
                    TestAssert.WithMessage(() => Assert.NotEqual(baseline, assertedValue), "another key");
                }
            });
        }

        [Fact(Timeout = 60000)]
        public void NothingIsMintedWithoutWhatItBinds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Multiple(() =>
            {
                {
                    var assertedValue = JoinCapability.Mint(null, Agent, Session, Room, Epoch, Generation, Iat);
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "no secret");
                }
                {
                    var assertedValue = JoinCapability.Mint("", Agent, Session, Room, Epoch, Generation, Iat);
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "empty secret");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, null, Session, Room, Epoch, Generation, Iat);
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "no agent");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, Agent, null, Room, Epoch, Generation, Iat);
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "no session");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, Agent, Session, 0, Epoch, Generation, Iat);
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "no room");
                }
                {
                    var assertedValue = JoinCapability.Mint(Key, "has|pipe", Session, Room, Epoch, Generation, Iat);
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "a field that would break the payload split is refused, not escaped");
                }
            });
        }

        [Fact(Timeout = 60000)]
        public void ArmingOff_MintsTheZeroEpoch()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string cap = JoinCapability.Mint(Key, Agent, Session, Room, null, 0, Iat, Nonce);
            string payload = JoinCapability.Canonical(Agent, Session, Room, JoinCapability.NoEpoch, 0, Iat,
                Iat + JoinCapability.LifetimeSeconds, Nonce);
            {
                var assertedValue = payload;
                TestAssert.WithMessage(() => Assert.Contains($"|{JoinCapability.NoEpoch}|0|", assertedValue), "with arming off the capability carries the zero epoch and generation 0, which the mixer matches");
            }
            Assert.NotNull(cap);
        }

        [Fact(Timeout = 60000)]
        public void Base64Url_HasNoPaddingAndNoPlusOrSlash()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < 50; i++)
            {
                string cap = JoinCapability.Mint(Key, Agent, Session, Room + i, Epoch, Generation, Iat + i);
                {
                    var assertedValue = cap;
                    TestAssert.WithMessage(() =>
                    {
                        Assert.DoesNotContain("=", assertedValue);
                        Assert.DoesNotContain("+", assertedValue);
                        Assert.DoesNotContain("/", assertedValue);
                    }, "base64url without padding, so the capability survives every transport unescaped");
                }
                {
                    var assertedValue = cap.Split('.').Length;
                    TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "version, payload, signature");
                }
                Assert.True(cap.Length < 1024, "under the mixer's SLV_JOINCAP_MAX_LEN");
            }
        }

        // ---- the (epoch, generation) publisher --------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Authority_ResolvesTheZeroEpochUntilSomethingArms()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            JoinCapabilityAuthority.Clear();
            (string epoch, uint generation) = JoinCapabilityAuthority.Resolve(Room);
            {
                var assertedValue = (epoch, generation);
                TestAssert.WithMessage(() => Assert.Equal((JoinCapability.NoEpoch, 0u), assertedValue), "a room the sim never armed resolves to what a mixer with no authority expects");
            }
        }

        [Fact(Timeout = 60000)]
        public void Authority_PublishesAndForgetsPerRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            JoinCapabilityAuthority.Clear();
            JoinCapabilityAuthority.Publish(Room, Epoch, 4);
            JoinCapabilityAuthority.Publish(Room + 1, "0000018f00000002", 9);
            Assert.Equal((Epoch, 4u), JoinCapabilityAuthority.Resolve(Room));
            Assert.Equal(("0000018f00000002", 9u), JoinCapabilityAuthority.Resolve(Room + 1));
            JoinCapabilityAuthority.Publish(Room, Epoch, 5);
            {
                var assertedValue = JoinCapabilityAuthority.Resolve(Room);
                TestAssert.WithMessage(() => Assert.Equal((Epoch, 5u), assertedValue), "newest wins");
            }
            JoinCapabilityAuthority.Forget(Room);
            Assert.Equal((JoinCapability.NoEpoch, 0u), JoinCapabilityAuthority.Resolve(Room));
            Assert.Equal(1, JoinCapabilityAuthority.Count);
            JoinCapabilityAuthority.Clear();
        }

        [Fact(Timeout = 60000)]
        public void Authority_IgnoresRoomsAndEpochsItCannotUse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            JoinCapabilityAuthority.Clear();
            JoinCapabilityAuthority.Publish(0, Epoch, 1);
            JoinCapabilityAuthority.Publish(Room, null, 1);
            JoinCapabilityAuthority.Publish(Room, "", 1);
            {
                var assertedValue = JoinCapabilityAuthority.Count;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "nothing half-published");
            }
        }

        // ---- the join request -------------------------------------------------------------------

        /// <summary>The same request body with its per-request transaction id blanked, so two separately built
        /// requests can be compared for everything that is not the transaction.</summary>
        private static string WithoutTransaction(string json)
            => System.Text.RegularExpressions.Regex.Replace(json, "\"transaction\":\"[^\"]*\"", "\"transaction\":\"<tx>\"");

        [Fact(Timeout = 60000)]
        public void JoinRequest_CarriesTheCapabilityOnlyWhenThereIsOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var plain = new AudioBridgeJoinRoomReq(Room, Agent);
            string plainBody = plain.ToJson();
            {
                var assertedValue = plainBody;
                TestAssert.WithMessage(() =>
                {
                    Assert.DoesNotContain("join_cap", assertedValue);
                    Assert.DoesNotContain("session_id", assertedValue);
                }, "the body without a capability has no capability keys");
            }

            var stamped = new AudioBridgeJoinRoomReq(Room, Agent);
            stamped.SetJoinCapability(GoldenCapability, Session);
            string stampedBody = stamped.ToJson();
            {
                var assertedValue = stampedBody;
                Assert.Contains("join_cap", assertedValue);
                Assert.Contains(Session, assertedValue);
            }

            var refused = new AudioBridgeJoinRoomReq(Room, Agent);
            refused.SetJoinCapability(null, Session);
            refused.SetJoinCapability(GoldenCapability, null);
            // Every request carries its own random transaction id, so compare the bodies with that normalised.
            {
                var assertedValue = WithoutTransaction(refused.ToJson());
                TestAssert.WithMessage(() => Assert.Equal(WithoutTransaction(plainBody), assertedValue), "half a capability is never sent: the body stays the one without a capability");
            }
        }

        [Fact(Timeout = 60000)]
        public void MessageDetailsLogging_NeverPrintsACapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var req = new AudioBridgeJoinRoomReq(Room, Agent);
            req.SetJoinCapability(GoldenCapability, Session);
            string wire = req.ToJson();
            string logged = req.ToJsonForLog();
            Assert.Multiple(() =>
            {
                {
                    var assertedValue = wire;
                    TestAssert.WithMessage(() => Assert.Contains(GoldenCapability, assertedValue), "the wire body carries the capability");
                }
                {
                    var assertedValue = logged;
                    TestAssert.WithMessage(() => Assert.DoesNotContain(GoldenCapability, assertedValue), "the log line never does");
                }
                {
                    var assertedValue = logged;
                    TestAssert.WithMessage(() => Assert.DoesNotContain(GoldenCapability.Split('.')[1], assertedValue), "not even its payload survives into a log");
                }
                {
                    var assertedValue = logged;
                    TestAssert.WithMessage(() =>
                    {
                        Assert.Contains("<redacted join_cap>", assertedValue);
                        Assert.Contains(Session, assertedValue);
                    }, "it says a capability was there, and keeps the session id an operator correlates on");
                }
            });

            var plain = new AudioBridgeJoinRoomReq(Room, Agent);
            {
                var assertedValue = plain.ToJsonForLog();
                TestAssert.WithMessage(() => Assert.Equal(plain.ToJson(), assertedValue), "a join with no capability logs exactly what it logged before the capability existed");
            }
        }
    }
}
