/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Regression test for a crash: CalcRoomNumber computed the Janus room number as
 * Math.Abs(hash.GetHashCode()), and Math.Abs(int.MinValue) throws OverflowException. Because the
 * hash inputs are stable per agent+parcel, one unlucky (region,parcel) combination would crash
 * voice provisioning on that parcel forever.
 *
 * The fix is JanusAudioBridge.FoldHashToRoom: int.MinValue -> int.MaxValue (a valid positive room),
 * Math.Abs kept verbatim for EVERY other value so no existing room assignment changes. These tests
 * prove exactly that: the crashing input no longer throws and yields a positive room, and a handful
 * of ordinary hash values fold to the SAME number the old Math.Abs expression produced (both are
 * computed here). A CalcRoomNumber end-to-end check confirms the real derivation is preserved.
 */

using System;
using NUnit.Framework;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class RoomNumberFoldTests
    {
        [Test]
        public void FoldHashToRoom_IntMinValue_DoesNotThrow_AndIsPositive()
        {
            int room = 0;
            Assert.That(() => room = JanusAudioBridge.FoldHashToRoom(int.MinValue), Throws.Nothing,
                "int.MinValue must not throw (Math.Abs(int.MinValue) would)");
            Assert.That(room, Is.GreaterThan(0), "the fallback room is a valid positive integer");
            Assert.That(room, Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void FoldHashToRoom_OrdinaryValues_MatchOldMathAbs()
        {
            // Every value EXCEPT int.MinValue: the fold must equal the OLD expression, computed here.
            int[] values =
            {
                0, 1, -1, 2, -2, 42, -42, 1000, -1000,
                int.MaxValue, int.MinValue + 1, 123456789, -123456789, 0x7FFFFFFE, unchecked((int)0x80000001)
            };
            foreach (int v in values)
            {
                int expectedOld = Math.Abs(v);                       // the pre-fix expression
                int actualNew = JanusAudioBridge.FoldHashToRoom(v);  // the fix
                Assert.That(actualNew, Is.EqualTo(expectedOld),
                    $"FoldHashToRoom({v}) must equal the old Math.Abs({v}) = {expectedOld}");
            }
        }

        [Test]
        public void CalcRoomNumber_OrdinaryInputs_PreserveTheOldNumber()
        {
            // Rebuild the hash the way CalcRoomNumber does, compute BOTH the old Math.Abs form and the
            // live CalcRoomNumber, and assert they agree (they must, unless the hash is exactly
            // int.MinValue, which is astronomically unlikely for these fixed inputs and asserted away).
            (string region, int parcel)[] cases =
            {
                ("11111111-1111-1111-1111-111111111111", 0),
                ("11111111-1111-1111-1111-111111111111", 7),
                ("22222222-2222-2222-2222-222222222222", 42),
                ("abcdef01-0000-0000-0000-000000000000", -999),
            };
            foreach ((string region, int parcel) in cases)
            {
                var hasher = new BHasherMdjb2();
                hasher.Add(region);
                hasher.Add("local");
                hasher.Add(parcel);
                int hc = hasher.Finish().GetHashCode();
                Assume.That(hc, Is.Not.EqualTo(int.MinValue), "fixed-input hash is not the 1-in-2^32 crash value");

                int oldRoom = Math.Abs(hc);
                int liveRoom = JanusAudioBridge.CalcRoomNumber(string.Empty, region, "local", parcel, string.Empty);
                Assert.That(liveRoom, Is.EqualTo(oldRoom),
                    $"CalcRoomNumber(local, {region}, {parcel}) must be unchanged by the fix");
                Assert.That(liveRoom, Is.GreaterThanOrEqualTo(0), "a room number is non-negative");
            }
        }
    }
}
