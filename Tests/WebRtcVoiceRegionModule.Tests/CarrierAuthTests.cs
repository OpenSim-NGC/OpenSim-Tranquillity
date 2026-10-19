/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The cross-process ring and conference-text carriers are grid instant messages, and the grid IM
 * endpoint authenticates nothing, so each carrier is signed with a secret shared by the grid's region
 * servers. These tests offer forged and genuine carriers to the receiving side (CarrierIntake) and check
 * that a forged one is refused and adopts or changes nothing, and that a genuine one is accepted.
 *
 * Two region servers are two CarrierAuth instances (each with its own nonce store) over two engines
 * (each with its own session store), sharing one test clock.
 */
using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class CarrierAuthTests
    {
        private const string Secret = "example-carrier-secret-0123456789-abcdef";
        private const string OtherSecret = "another-carrier-secret-0123456789-ghij";
        private const long T0 = 1_800_000_000;
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");

        private long _now;

        [SetUp]
        public void SetUp() => _now = T0;

        private CarrierAuth Auth(string secret = Secret) => new CarrierAuth(secret, () => _now);

        private static NonSpatialVoiceSessionEngine Engine() => NewEngine();

        /// <summary>A group voice session Alice started on engine <paramref name="a"/>.</summary>
        private static NonSpatialVoiceSession GroupSession(NonSpatialVoiceSessionEngine a)
        {
            UUID grp = UUID.Random();
            return a.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA).Session;
        }

        private static OSDMap GroupBody(NonSpatialVoiceSessionEngine a, NonSpatialVoiceSession s)
            => GroupVoiceInvite.BuildBody(s, a.IssueToken(s.SessionId, Alice), Alice, "Test User", "Example Group");

        private static GridInstantMessage Ring(UUID target, NonSpatialVoiceSession s, OSDMap body, CarrierAuth auth)
            => GroupVoiceRingTransport.Build(target, Alice, "Test User", s.SessionId, RegionA, body, auth);

        private static RingIntake Offer(GridInstantMessage im, CarrierAuth auth, NonSpatialVoiceSessionEngine b)
            => CarrierIntake.AcceptRing(im, auth, b, groupUsable: true, groupCap: 50);

        /// <summary>Edit the JSON payload of a carrier after it was signed, the way a forger or a hop in the middle could.</summary>
        private static void Rewrite(GridInstantMessage im, string magic, Action<OSDMap> edit)
        {
            string raw = Encoding.UTF8.GetString(im.binaryBucket);
            OSDMap map = (OSDMap)OSDParser.DeserializeJson(raw.Substring(magic.Length));
            edit(map);
            im.binaryBucket = Encoding.UTF8.GetBytes(magic + OSDParser.SerializeJsonString(map));
        }

        // ---- the signer ---------------------------------------------------------------------------

        [Test]
        public void ASecretUnderThirtyTwoCharactersIsNotUsable()
        {
            Assert.That(CarrierAuth.CheckSecret(null), Is.EqualTo("not-set"));
            Assert.That(CarrierAuth.CheckSecret(""), Is.EqualTo("not-set"));
            Assert.That(CarrierAuth.CheckSecret(new string('x', 31)), Is.EqualTo("too-short"));
            Assert.That(CarrierAuth.CheckSecret(new string('x', 32)), Is.Null);
            Assert.That(Auth(new string('x', 31)).Enabled, Is.False);
            Assert.That(Auth(new string('x', 32)).Enabled, Is.True);
        }

        [Test]
        public void WithNoSecretNothingIsSigned()
        {
            CarrierAuth off = CarrierAuth.Off();
            Assert.That(off.Enabled, Is.False);
            Assert.That(off.Seal(new CarrierFields { Kind = CarrierAuth.KindRing }), Is.Null);

            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            GridInstantMessage im = Ring(Bob, s, GroupBody(a, s), off);
            Assert.That(Encoding.UTF8.GetString(im.binaryBucket), Does.Not.Contain(CarrierAuth.AuthKey),
                        "an unsigned build carries no auth block");
        }

        [Test]
        public void WithNoSecretEveryIncomingCarrierIsRefusedAndNothingIsAdopted()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            // A carrier signed by a region that does have the secret.
            GridInstantMessage im = Ring(Bob, s, GroupBody(a, s), Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            CarrierAuth off = CarrierAuth.Off();
            RingIntake r = Offer(im, off, b);

            Assert.That(r.Claimed, Is.True);
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Reason, Is.EqualTo(CarrierAuth.NoSecret));
            Assert.That(b.Store.All(), Is.Empty);
            Assert.That(off.RefusalCount(CarrierAuth.NoSecret), Is.EqualTo(1));

        }

        [Test]
        public void TheMacCoversEveryFieldAndTheKind()
        {
            CarrierAuth auth = Auth();
            UUID session = UUID.Random();
            CarrierFields f = new CarrierFields
            {
                Kind = CarrierAuth.KindRing, Target = Bob, SessionId = session, SessionType = "Adhoc",
                RoomKey = "k", Caller = Alice, Credential = "c", Text = null,
            };
            OSDMap sealedBlock = auth.Seal(f);

            CarrierFields[] altered =
            {
                new CarrierFields { Kind = CarrierAuth.KindText, Target = Bob, SessionId = session, SessionType = "Adhoc", RoomKey = "k", Caller = Alice, Credential = "c" },
                new CarrierFields { Kind = CarrierAuth.KindRing, Target = Carol, SessionId = session, SessionType = "Adhoc", RoomKey = "k", Caller = Alice, Credential = "c" },
                new CarrierFields { Kind = CarrierAuth.KindRing, Target = Bob, SessionId = UUID.Random(), SessionType = "Adhoc", RoomKey = "k", Caller = Alice, Credential = "c" },
                new CarrierFields { Kind = CarrierAuth.KindRing, Target = Bob, SessionId = session, SessionType = "Group", RoomKey = "k", Caller = Alice, Credential = "c" },
                new CarrierFields { Kind = CarrierAuth.KindRing, Target = Bob, SessionId = session, SessionType = "Adhoc", RoomKey = "k2", Caller = Alice, Credential = "c" },
                new CarrierFields { Kind = CarrierAuth.KindRing, Target = Bob, SessionId = session, SessionType = "Adhoc", RoomKey = "k", Caller = Carol, Credential = "c" },
                new CarrierFields { Kind = CarrierAuth.KindRing, Target = Bob, SessionId = session, SessionType = "Adhoc", RoomKey = "k", Caller = Alice, Credential = "c2" },
                new CarrierFields { Kind = CarrierAuth.KindRing, Target = Bob, SessionId = session, SessionType = "Adhoc", RoomKey = "k", Caller = Alice, Credential = "c", Text = "x" },
            };
            foreach (CarrierFields other in altered)
                Assert.That(Auth().Verify(other, sealedBlock), Is.EqualTo(CarrierAuth.BadMac));
            Assert.That(Auth().Verify(f, sealedBlock), Is.Null, "the unaltered fields verify");
        }

        [Test]
        public void FieldsCannotRunIntoEachOther()
        {
            // "ab" + "c" and "a" + "bc" must not sign alike.
            CarrierAuth auth = Auth();
            CarrierFields one = new CarrierFields { Kind = "ring", RoomKey = "ab", Credential = "c" };
            CarrierFields two = new CarrierFields { Kind = "ring", RoomKey = "a", Credential = "bc" };
            Assert.That(Auth().Verify(two, auth.Seal(one)), Is.EqualTo(CarrierAuth.BadMac));
        }

        [Test]
        public void RefusalsAreCountedByReason_AndLoggedOnTheFirstThenEveryHundredth()
        {
            CarrierAuth auth = Auth();
            Assert.That(auth.RecordRefusal(CarrierAuth.BadMac), Is.EqualTo(1));
            Assert.That(auth.RecordRefusal(CarrierAuth.BadMac), Is.EqualTo(2));
            Assert.That(auth.RecordRefusal(CarrierAuth.Stale), Is.EqualTo(1));
            Assert.That(auth.RefusalCount(CarrierAuth.BadMac), Is.EqualTo(2));
            Assert.That(CarrierAuth.ShouldLog(1), Is.True);
            Assert.That(CarrierAuth.ShouldLog(2), Is.False);
            Assert.That(CarrierAuth.ShouldLog(100), Is.True);
            Assert.That(CarrierAuth.ShouldLog(101), Is.False);
        }

        // ---- a ring ---------------------------------------------------------------------------------

        [Test]
        public void AGenuineGroupRingIsAccepted()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            UUID grp = UUID.Random();
            NonSpatialVoiceSession g = a.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA).Session;
            OSDMap body = GroupVoiceInvite.BuildBody(g, a.IssueToken(grp, Alice), Alice, "Test User", "Example Group");
            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Test User", grp, RegionA, body, Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(im, Auth(), b);

            Assert.That(r.Ok, Is.True, r.Decision + " " + r.Reason);
            Assert.That(b.Store.Get(grp).Type, Is.EqualTo(NonSpatialSessionType.Group));
        }

        [Test]
        public void ARingWithNoMacIsRefusedAndAdoptsNothing()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            GridInstantMessage im = Ring(Bob, s, GroupBody(a, s), auth: null);

            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(im, Auth(), b);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Reason, Is.EqualTo(CarrierAuth.MissingMac));
            Assert.That(b.Store.All(), Is.Empty);
        }

        [Test]
        public void ARingWithAWrongMacIsRefusedAndAdoptsNothing()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            NonSpatialVoiceSessionEngine b = Engine();

            // Signed with a different secret.
            RingIntake other = Offer(Ring(Bob, s, GroupBody(a, s), Auth(OtherSecret)), Auth(), b);
            Assert.That(other.Reason, Is.EqualTo(CarrierAuth.BadMac));

            // A genuine ring retargeted at someone else.
            GridInstantMessage im = Ring(Bob, s, GroupBody(a, s), Auth());
            im.toAgentID = Carol.Guid;
            Assert.That(Offer(im, Auth(), b).Reason, Is.EqualTo(CarrierAuth.BadMac));

            // A genuine ring whose session id was swapped.
            im = Ring(Bob, s, GroupBody(a, s), Auth());
            im.imSessionID = UUID.Random().Guid;
            Assert.That(Offer(im, Auth(), b).Reason, Is.EqualTo(CarrierAuth.BadMac));

            // A genuine ring with another credential put in.
            im = Ring(Bob, s, GroupBody(a, s), Auth());
            Rewrite(im, GroupVoiceRingTransport.BucketMagic,
                    m => ((OSDMap)m["voice"])["channel_credentials"] = OSD.FromString("swapped"));
            Assert.That(Offer(im, Auth(), b).Reason, Is.EqualTo(CarrierAuth.BadMac));

            // A garbage MAC.
            im = Ring(Bob, s, GroupBody(a, s), Auth());
            Rewrite(im, GroupVoiceRingTransport.BucketMagic,
                    m => ((OSDMap)m[CarrierAuth.AuthKey])["mac"] = OSD.FromString(new string('0', 64)));
            Assert.That(Offer(im, Auth(), b).Reason, Is.EqualTo(CarrierAuth.BadMac));

            // A MAC that is not hexadecimal at all.
            im = Ring(Bob, s, GroupBody(a, s), Auth());
            Rewrite(im, GroupVoiceRingTransport.BucketMagic,
                    m => ((OSDMap)m[CarrierAuth.AuthKey])["mac"] = OSD.FromString("not hex"));
            Assert.That(Offer(im, Auth(), b).Reason, Is.EqualTo(CarrierAuth.MalformedMac));

            Assert.That(b.Store.All(), Is.Empty, "none of them adopted anything");
        }

        [Test]
        public void AStaleRingIsRefusedAndAnAgedButInWindowOneIsNot()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            OSDMap body = GroupBody(a, s);
            GridInstantMessage im = Ring(Bob, s, body, Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            _now = T0 + CarrierAuth.WindowSeconds + CarrierAuth.SkewSeconds + 1;
            RingIntake stale = Offer(im, Auth(), b);
            Assert.That(stale.Reason, Is.EqualTo(CarrierAuth.Stale));
            Assert.That(b.Store.All(), Is.Empty);

            _now = T0 + CarrierAuth.WindowSeconds;
            Assert.That(Offer(im, Auth(), b).Ok, Is.True, "inside the window");
        }

        [Test]
        public void ARingIssuedFarInTheFutureIsRefusedButSmallClockSkewIsTolerated()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            OSDMap body = GroupBody(a, s);
            NonSpatialVoiceSessionEngine b = Engine();

            _now = T0 + 500;
            GridInstantMessage future = Ring(Bob, s, body, Auth());
            _now = T0;
            Assert.That(Offer(future, Auth(), b).Reason, Is.EqualTo(CarrierAuth.Stale));

            _now = T0 + CarrierAuth.SkewSeconds;
            GridInstantMessage skewed = Ring(Bob, s, body, Auth());
            _now = T0;
            Assert.That(Offer(skewed, Auth(), b).Ok, Is.True);
        }

        [Test]
        public void AReplayedRingIsRefused()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            GridInstantMessage im = Ring(Bob, s, GroupBody(a, s), Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            CarrierAuth receiver = Auth();
            Assert.That(Offer(im, receiver, b).Ok, Is.True);
            RingIntake again = Offer(im, receiver, b);

            Assert.That(again.Ok, Is.False);
            Assert.That(again.Reason, Is.EqualTo(CarrierAuth.Replay));
        }

        [Test]
        public void ARingWithoutARoomKeyIsRefusedEvenWhenItIsSigned()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            OSDMap body = GroupBody(a, s);
            ((OSDMap)body["voice"]).Remove("channel_uri");
            GridInstantMessage im = Ring(Bob, s, body, Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(im, Auth(), b);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Reason, Is.Null, "the signature was fine");
            Assert.That(r.Decision, Is.EqualTo("refused-room-key-missing"));
            Assert.That(b.Store.All(), Is.Empty);
        }

        [Test]
        public void AWrongRoomKeyIsStillRefused()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            OSDMap body = GroupBody(a, s);
            ((OSDMap)body["voice"])["channel_uri"] = OSD.FromString("not-the-derived-key");
            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(Ring(Bob, s, body, Auth()), Auth(), b);
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Decision, Is.EqualTo("refused-room-key-mismatch"));
            Assert.That(b.Store.All(), Is.Empty);
        }

        [Test]
        public void ARefusedCarrierNeverReachesTheNonceStore()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            NonSpatialVoiceSessionEngine b = Engine();
            CarrierAuth receiver = Auth();
            for (int i = 0; i < 50; i++)
                Offer(Ring(Bob, s, GroupBody(a, s), Auth(OtherSecret)), receiver, b);
            Assert.That(receiver.NonceCount, Is.EqualTo(0), "only a valid MAC may spend a nonce slot");
        }

        [Test]
        public void ANonceStoreThatIsFullRefusesNewCarriersUntilOldNoncesAge()
        {
            CarrierAuth signer = Auth();
            CarrierAuth receiver = Auth();
            CarrierFields f = new CarrierFields { Kind = CarrierAuth.KindRing, Target = Bob, SessionId = UUID.Random(), RoomKey = "k" };
            for (int i = 0; i < CarrierAuth.MaxNonces; i++)
                Assert.That(receiver.Verify(f, signer.Seal(f)), Is.Null);

            Assert.That(receiver.Verify(f, signer.Seal(f)), Is.EqualTo(CarrierAuth.NonceStoreFull));

            _now = T0 + CarrierAuth.WindowSeconds + 2 * CarrierAuth.SkewSeconds + 1;
            Assert.That(receiver.Verify(f, signer.Seal(f)), Is.Null, "the old nonces aged out");
            Assert.That(receiver.NonceCount, Is.EqualTo(1));
        }

        // ---- a forged ring and a session this instance already holds ---------------------------

        [Test]
        public void AForgedRingCannotLatchRingSentOnASessionThisInstanceHolds()
        {
            NonSpatialVoiceSessionEngine b = Engine();
            NonSpatialVoiceSession mine = GroupSession(b);   // started here
            string tokenBefore = b.IssueToken(mine.SessionId, Alice);
            OSDMap body = GroupBody(b, mine);
            // The last seat goes, which ends the ring cycle: the next real call here must ring again.
            b.Depart(mine.SessionId, Alice, DepartureReason.VoiceTeardown);
            Assert.That(mine.RingSent, Is.False);
            int membersBefore = mine.Members.Count;

            Assert.That(Offer(Ring(Carol, mine, body, null), Auth(), b).Ok, Is.False);
            Assert.That(Offer(Ring(Carol, mine, body, Auth(OtherSecret)), Auth(), b).Ok, Is.False);

            Assert.That(mine.RingSent, Is.False, "a forged ring must not suppress the real ring");
            Assert.That(mine.Token, Is.EqualTo(tokenBefore));
            Assert.That(mine.Members, Has.Count.EqualTo(membersBefore));
            Assert.That(mine.Find(Carol), Is.Null);
        }

        [Test]
        public void AGenuineRingNamingASessionStartedHereChangesNothingAboutIt()
        {
            NonSpatialVoiceSessionEngine b = Engine();
            NonSpatialVoiceSession mine = GroupSession(b);
            string tokenBefore = b.IssueToken(mine.SessionId, Alice);
            OSDMap body = GroupBody(b, mine);
            ((OSDMap)body["voice"])["channel_credentials"] = OSD.FromString("a-different-credential");
            b.Depart(mine.SessionId, Alice, DepartureReason.VoiceTeardown);   // ends the ring cycle
            Assert.That(mine.RingSent, Is.False);

            RingIntake r = Offer(Ring(Carol, mine, body, Auth()), Auth(), b);

            Assert.That(r.Ok, Is.True, "the viewer still gets the invitation");
            Assert.That(r.Decision, Is.EqualTo("ring-session-held"));
            Assert.That(mine.RingSent, Is.False);
            Assert.That(mine.Token, Is.EqualTo(tokenBefore));
            Assert.That(mine.Find(Carol), Is.Null, "no member was added to a session started here");
            Assert.That(mine.WasInvited(Carol), Is.False);
        }

        // ---- the accepted-carrier log line --------------------------------------------------------

        [Test]
        public void AnAcceptedRingCarriesItsTypeAndOrigin()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            GridInstantMessage im = Ring(Bob, s, GroupBody(a, s), Auth());

            RingIntake r = Offer(im, Auth(), Engine());

            Assert.That(r.Authenticated, Is.True);
            Assert.That(r.Type, Is.EqualTo(NonSpatialSessionType.Group));
            Assert.That(r.OriginRegion, Is.EqualTo(RegionA));
            string line = CarrierAuth.AcceptedLine(CarrierAuth.KindRing, r.GroupId, r.Type, r.OriginRegion);
            Assert.That(line, Does.Contain("kind=ring"));
            Assert.That(line, Does.Contain("type=Group"));
            Assert.That(line, Does.Contain("session=" + s.SessionId));
            Assert.That(line, Does.Contain("from=" + RegionA));
        }

        [Test]
        public void ARefusedCarrierIsNotMarkedAuthenticated()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = GroupSession(a);
            GridInstantMessage forged = Ring(Bob, s, GroupBody(a, s), Auth(OtherSecret));
            Assert.That(Offer(forged, Auth(), Engine()).Authenticated, Is.False);
        }
    }
}
