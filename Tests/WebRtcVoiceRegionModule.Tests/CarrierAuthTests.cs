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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class CarrierAuthTests : IAsyncLifetime
        {
        private const string Secret = "example-carrier-secret-0123456789-abcdef";
        private const string OtherSecret = "another-carrier-secret-0123456789-ghij";
        private const long T0 = 1_800_000_000;
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");

        private long _now;
        private void SetUp() => _now = T0;

        private CarrierAuth Auth(string secret = Secret) => new CarrierAuth(secret, () => _now);

        private static NonSpatialVoiceSessionEngine Engine() => NewEngine();

        /// <summary>A conference Alice started on engine <paramref name="a"/>, with these people invited.</summary>
        private static NonSpatialVoiceSession Conference(NonSpatialVoiceSessionEngine a, params UUID[] invitees)
            => a.StartAdhoc(Alice, UUID.Random(), RegionA, invitees).Session;

        private static OSDMap ConferenceBody(NonSpatialVoiceSessionEngine a, NonSpatialVoiceSession s)
            => AdhocVoiceInvite.BuildBody(s, a.IssueToken(s.SessionId, Alice), Alice, "Test User");

        private static GridInstantMessage Ring(UUID target, NonSpatialVoiceSession s, OSDMap body, CarrierAuth auth)
            => GroupVoiceRingTransport.Build(target, Alice, "Test User", s.SessionId, RegionA, body, auth);

        private static RingIntake Offer(GridInstantMessage im, CarrierAuth auth, NonSpatialVoiceSessionEngine b)
            => CarrierIntake.AcceptRing(im, auth, b, groupUsable: true, groupCap: 50, adhocEnabled: true);

        /// <summary>Edit the JSON payload of a carrier after it was signed, the way a forger or a hop in the middle could.</summary>
        private static void Rewrite(GridInstantMessage im, string magic, Action<OSDMap> edit)
        {
            string raw = Encoding.UTF8.GetString(im.binaryBucket);
            OSDMap map = (OSDMap)OSDParser.DeserializeJson(raw.Substring(magic.Length));
            edit(map);
            im.binaryBucket = Encoding.UTF8.GetBytes(magic + OSDParser.SerializeJsonString(map));
        }

        // ---- the signer ---------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ASecretUnderThirtyTwoCharactersIsNotUsable()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("not-set", CarrierAuth.CheckSecret(null));
            Assert.Equal("not-set", CarrierAuth.CheckSecret(""));
            Assert.Equal("too-short", CarrierAuth.CheckSecret(new string('x', 31)));
            Assert.Null(CarrierAuth.CheckSecret(new string('x', 32)));
            Assert.False(Auth(new string('x', 31)).Enabled);
            Assert.True(Auth(new string('x', 32)).Enabled);
        }

        [Fact(Timeout = 60000)]
        public void WithNoSecretNothingIsSigned()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            CarrierAuth off = CarrierAuth.Off();
            Assert.False(off.Enabled);
            Assert.Null(off.Seal(new CarrierFields { Kind = CarrierAuth.KindRing }));

            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            GridInstantMessage im = Ring(Bob, s, ConferenceBody(a, s), off);
            {
                var assertedValue = Encoding.UTF8.GetString(im.binaryBucket);
                TestAssert.WithMessage(() => Assert.DoesNotContain(CarrierAuth.AuthKey, assertedValue), "an unsigned build carries no auth block");
            }
        }

        [Fact(Timeout = 60000)]
        public void WithNoSecretEveryIncomingCarrierIsRefusedAndNothingIsAdopted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            // A carrier signed by a region that does have the secret.
            GridInstantMessage im = Ring(Bob, s, ConferenceBody(a, s), Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            CarrierAuth off = CarrierAuth.Off();
            RingIntake r = Offer(im, off, b);

            Assert.True(r.Claimed);
            Assert.False(r.Ok);
            Assert.Equal(CarrierAuth.NoSecret, r.Reason);
            Assert.Empty(b.Store.All());
            Assert.Equal(1, off.RefusalCount(CarrierAuth.NoSecret));

            NonSpatialVoiceSessionEngine c = Engine();
            ThreeWayText(c, out _, out NonSpatialVoiceSession sc);
            TextIntake t = CarrierIntake.AcceptText(Text(sc, Alice, Bob, "hello", Auth()), off, c);
            Assert.False(t.Ok);
            Assert.Equal(CarrierAuth.NoSecret, t.Reason);
        }

        [Fact(Timeout = 60000)]
        public void TheMacCoversEveryFieldAndTheKind()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
                Assert.Equal(CarrierAuth.BadMac, Auth().Verify(other, sealedBlock));
            {
                var assertedValue = Auth().Verify(f, sealedBlock);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the unaltered fields verify");
            }
        }

        [Fact(Timeout = 60000)]
        public void FieldsCannotRunIntoEachOther()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // "ab" + "c" and "a" + "bc" must not sign alike.
            CarrierAuth auth = Auth();
            CarrierFields one = new CarrierFields { Kind = "ring", RoomKey = "ab", Credential = "c" };
            CarrierFields two = new CarrierFields { Kind = "ring", RoomKey = "a", Credential = "bc" };
            Assert.Equal(CarrierAuth.BadMac, Auth().Verify(two, auth.Seal(one)));
        }

        [Fact(Timeout = 60000)]
        public void RefusalsAreCountedByReason_AndLoggedOnTheFirstThenEveryHundredth()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            CarrierAuth auth = Auth();
            Assert.Equal(1, auth.RecordRefusal(CarrierAuth.BadMac));
            Assert.Equal(2, auth.RecordRefusal(CarrierAuth.BadMac));
            Assert.Equal(1, auth.RecordRefusal(CarrierAuth.Stale));
            Assert.Equal(2, auth.RefusalCount(CarrierAuth.BadMac));
            Assert.True(CarrierAuth.ShouldLog(1));
            Assert.False(CarrierAuth.ShouldLog(2));
            Assert.True(CarrierAuth.ShouldLog(100));
            Assert.False(CarrierAuth.ShouldLog(101));
        }

        // ---- a ring ---------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AGenuineConferenceRingIsAcceptedAndTheAcceptIsAdmittedOnTheFarSide()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            GridInstantMessage im = Ring(Bob, s, ConferenceBody(a, s), Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(im, Auth(), b);

            Assert.True(r.Ok, r.Decision + " " + r.Reason);
            Assert.Equal("ring-adopted", r.Decision);
            Assert.False(r.Body.ContainsKey(CarrierAuth.AuthKey), "the viewer is handed the body without the auth block");
            Assert.True(b.Store.Get(s.SessionId).RingSent);
            Assert.True(b.Accept(s.SessionId, Bob, RegionB).Ok);
        }

        [Fact(Timeout = 60000)]
        public void AGenuineGroupRingIsAccepted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            UUID grp = UUID.Random();
            NonSpatialVoiceSession g = a.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA).Session;
            OSDMap body = GroupVoiceInvite.BuildBody(g, a.IssueToken(grp, Alice), Alice, "Test User", "Example Group");
            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Test User", grp, RegionA, body, Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(im, Auth(), b);

            Assert.True(r.Ok, r.Decision + " " + r.Reason);
            Assert.Equal(NonSpatialSessionType.Group, b.Store.Get(grp).Type);
        }

        [Fact(Timeout = 60000)]
        public void ARingWithNoMacIsRefusedAndAdoptsNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            GridInstantMessage im = Ring(Bob, s, ConferenceBody(a, s), auth: null);

            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(im, Auth(), b);

            Assert.False(r.Ok);
            Assert.Equal(CarrierAuth.MissingMac, r.Reason);
            Assert.Empty(b.Store.All());
        }

        [Fact(Timeout = 60000)]
        public void ARingWithAWrongMacIsRefusedAndAdoptsNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            NonSpatialVoiceSessionEngine b = Engine();

            // Signed with a different secret.
            RingIntake other = Offer(Ring(Bob, s, ConferenceBody(a, s), Auth(OtherSecret)), Auth(), b);
            Assert.Equal(CarrierAuth.BadMac, other.Reason);

            // A genuine ring retargeted at someone else.
            GridInstantMessage im = Ring(Bob, s, ConferenceBody(a, s), Auth());
            im.toAgentID = Carol.Guid;
            Assert.Equal(CarrierAuth.BadMac, Offer(im, Auth(), b).Reason);

            // A genuine ring whose session id was swapped.
            im = Ring(Bob, s, ConferenceBody(a, s), Auth());
            im.imSessionID = UUID.Random().Guid;
            Assert.Equal(CarrierAuth.BadMac, Offer(im, Auth(), b).Reason);

            // A genuine conference ring turned into a group ring.
            im = Ring(Bob, s, ConferenceBody(a, s), Auth());
            Rewrite(im, GroupVoiceRingTransport.BucketMagic,
                    m => ((OSDMap)m["voice"])["invitation_type"] = OSD.FromInteger(0));
            Assert.Equal(CarrierAuth.BadMac, Offer(im, Auth(), b).Reason);

            // A genuine ring with another credential put in.
            im = Ring(Bob, s, ConferenceBody(a, s), Auth());
            Rewrite(im, GroupVoiceRingTransport.BucketMagic,
                    m => ((OSDMap)m["voice"])["channel_credentials"] = OSD.FromString("swapped"));
            Assert.Equal(CarrierAuth.BadMac, Offer(im, Auth(), b).Reason);

            // A garbage MAC.
            im = Ring(Bob, s, ConferenceBody(a, s), Auth());
            Rewrite(im, GroupVoiceRingTransport.BucketMagic,
                    m => ((OSDMap)m[CarrierAuth.AuthKey])["mac"] = OSD.FromString(new string('0', 64)));
            Assert.Equal(CarrierAuth.BadMac, Offer(im, Auth(), b).Reason);

            // A MAC that is not hexadecimal at all.
            im = Ring(Bob, s, ConferenceBody(a, s), Auth());
            Rewrite(im, GroupVoiceRingTransport.BucketMagic,
                    m => ((OSDMap)m[CarrierAuth.AuthKey])["mac"] = OSD.FromString("not hex"));
            Assert.Equal(CarrierAuth.MalformedMac, Offer(im, Auth(), b).Reason);

            {
                var assertedValue = b.Store.All();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "none of them adopted anything");
            }
        }

        [Fact(Timeout = 60000)]
        public void AStaleRingIsRefusedAndAnAgedButInWindowOneIsNot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            OSDMap body = ConferenceBody(a, s);
            GridInstantMessage im = Ring(Bob, s, body, Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            _now = T0 + CarrierAuth.WindowSeconds + CarrierAuth.SkewSeconds + 1;
            RingIntake stale = Offer(im, Auth(), b);
            Assert.Equal(CarrierAuth.Stale, stale.Reason);
            Assert.Empty(b.Store.All());

            _now = T0 + CarrierAuth.WindowSeconds;
            Assert.True(Offer(im, Auth(), b).Ok, "inside the window");
        }

        [Fact(Timeout = 60000)]
        public void ARingIssuedFarInTheFutureIsRefusedButSmallClockSkewIsTolerated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            OSDMap body = ConferenceBody(a, s);
            NonSpatialVoiceSessionEngine b = Engine();

            _now = T0 + 500;
            GridInstantMessage future = Ring(Bob, s, body, Auth());
            _now = T0;
            Assert.Equal(CarrierAuth.Stale, Offer(future, Auth(), b).Reason);

            _now = T0 + CarrierAuth.SkewSeconds;
            GridInstantMessage skewed = Ring(Bob, s, body, Auth());
            _now = T0;
            Assert.True(Offer(skewed, Auth(), b).Ok);
        }

        [Fact(Timeout = 60000)]
        public void AReplayedRingIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            GridInstantMessage im = Ring(Bob, s, ConferenceBody(a, s), Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            CarrierAuth receiver = Auth();
            Assert.True(Offer(im, receiver, b).Ok);
            RingIntake again = Offer(im, receiver, b);

            Assert.False(again.Ok);
            Assert.Equal(CarrierAuth.Replay, again.Reason);
        }

        [Fact(Timeout = 60000)]
        public void ARingWithoutARoomKeyIsRefusedEvenWhenItIsSigned()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            OSDMap body = ConferenceBody(a, s);
            ((OSDMap)body["voice"]).Remove("channel_uri");
            GridInstantMessage im = Ring(Bob, s, body, Auth());

            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(im, Auth(), b);

            Assert.False(r.Ok);
            {
                var assertedValue = r.Reason;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the signature was fine");
            }
            Assert.Equal("refused-room-key-missing", r.Decision);
            Assert.Empty(b.Store.All());
        }

        [Fact(Timeout = 60000)]
        public void AWrongRoomKeyIsStillRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            OSDMap body = ConferenceBody(a, s);
            ((OSDMap)body["voice"])["channel_uri"] = OSD.FromString("not-the-derived-key");
            NonSpatialVoiceSessionEngine b = Engine();
            RingIntake r = Offer(Ring(Bob, s, body, Auth()), Auth(), b);
            Assert.False(r.Ok);
            Assert.Equal("refused-room-key-mismatch", r.Decision);
            Assert.Empty(b.Store.All());
        }

        [Fact(Timeout = 60000)]
        public void ARefusedCarrierNeverReachesTheNonceStore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            NonSpatialVoiceSessionEngine b = Engine();
            CarrierAuth receiver = Auth();
            for (int i = 0; i < 50; i++)
                Offer(Ring(Bob, s, ConferenceBody(a, s), Auth(OtherSecret)), receiver, b);
            {
                var assertedValue = receiver.NonceCount;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "only a valid MAC may spend a nonce slot");
            }
        }

        [Fact(Timeout = 60000)]
        public void ANonceStoreThatIsFullRefusesNewCarriersUntilOldNoncesAge()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            CarrierAuth signer = Auth();
            CarrierAuth receiver = Auth();
            CarrierFields f = new CarrierFields { Kind = CarrierAuth.KindRing, Target = Bob, SessionId = UUID.Random(), RoomKey = "k" };
            for (int i = 0; i < CarrierAuth.MaxNonces; i++)
                Assert.Null(receiver.Verify(f, signer.Seal(f)));

            Assert.Equal(CarrierAuth.NonceStoreFull, receiver.Verify(f, signer.Seal(f)));

            _now = T0 + CarrierAuth.WindowSeconds + 2 * CarrierAuth.SkewSeconds + 1;
            {
                var assertedValue = receiver.Verify(f, signer.Seal(f));
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the old nonces aged out");
            }
            Assert.Equal(1, receiver.NonceCount);
        }

        // ---- a forged ring and a session this instance already holds ---------------------------

        [Fact(Timeout = 60000)]
        public void AForgedRingCannotLatchRingSentOnASessionThisInstanceHolds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine b = Engine();
            NonSpatialVoiceSession mine = Conference(b, Bob);   // started here
            string tokenBefore = b.IssueToken(mine.SessionId, Alice);
            OSDMap body = ConferenceBody(b, mine);
            // The last seat goes, which ends the ring cycle: the next real call here must ring again.
            b.Depart(mine.SessionId, Alice, DepartureReason.VoiceTeardown);
            Assert.False(mine.RingSent);
            int membersBefore = mine.Members.Count;

            Assert.False(Offer(Ring(Carol, mine, body, null), Auth(), b).Ok);
            Assert.False(Offer(Ring(Carol, mine, body, Auth(OtherSecret)), Auth(), b).Ok);

            Assert.False(mine.RingSent, "a forged ring must not suppress the real ring");
            Assert.Equal(tokenBefore, mine.Token);
            Assert.Equal(membersBefore, mine.Members.Count());
            Assert.Null(mine.Find(Carol));
        }

        [Fact(Timeout = 60000)]
        public void AGenuineRingNamingASessionStartedHereChangesNothingAboutIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine b = Engine();
            NonSpatialVoiceSession mine = Conference(b, Bob);
            string tokenBefore = b.IssueToken(mine.SessionId, Alice);
            OSDMap body = ConferenceBody(b, mine);
            ((OSDMap)body["voice"])["channel_credentials"] = OSD.FromString("a-different-credential");
            b.Depart(mine.SessionId, Alice, DepartureReason.VoiceTeardown);   // ends the ring cycle
            Assert.False(mine.RingSent);

            RingIntake r = Offer(Ring(Carol, mine, body, Auth()), Auth(), b);

            Assert.True(r.Ok, "the viewer still gets the invitation");
            Assert.Equal("ring-session-held", r.Decision);
            Assert.False(mine.RingSent);
            Assert.Equal(tokenBefore, mine.Token);
            {
                var assertedValue = mine.Find(Carol);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no member was added to a session started here");
            }
            Assert.False(mine.WasInvited(Carol));
        }

        [Fact(Timeout = 60000)]
        public void ASecondGenuineRingMayInviteAnotherTargetIntoAnAdoptedConferenceAndNothingElse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob, Carol);
            OSDMap body = ConferenceBody(a, s);

            NonSpatialVoiceSessionEngine b = Engine();
            Assert.True(Offer(Ring(Bob, s, body, Auth()), Auth(), b).Ok);
            NonSpatialVoiceSession adopted = b.Store.Get(s.SessionId);
            string tokenBefore = adopted.Token;
            DateTime seenBefore = adopted.LastSeenUtc;

            ((OSDMap)body["voice"])["channel_credentials"] = OSD.FromString("a-different-credential");
            RingIntake second = Offer(Ring(Carol, s, body, Auth()), Auth(), b);

            Assert.True(second.Ok);
            Assert.Equal("ring-adopt-idempotent", second.Decision);
            {
                var assertedValue = adopted.Find(Carol)?.State;
                TestAssert.WithMessage(() => Assert.Equal(MemberState.Invited, assertedValue), "so Carol's accept is admitted here");
            }
            Assert.True(adopted.WasInvited(Carol));
            {
                var assertedValue = adopted.Token;
                TestAssert.WithMessage(() => Assert.Equal(tokenBefore, assertedValue), "the credential is not replaced");
            }
            Assert.Equal(seenBefore, adopted.LastSeenUtc);
            {
                var assertedValue = adopted.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "a ring seats nobody");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARingOfTheOtherTypeCannotTouchAnExistingSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine b = Engine();
            UUID id = UUID.Random();
            b.AdoptRemoteRing(id, NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, id), "tok", 50,
                              Alice, new[] { Bob }, NonSpatialSessionType.Adhoc);

            SessionOutcome o = b.AdoptRemoteRing(id, NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Group, id),
                                                 "tok2", 50, Alice, new[] { Carol }, NonSpatialSessionType.Group);

            Assert.False(o.Ok);
            Assert.Null(b.Store.Get(id).Find(Carol));
        }

        // ---- conference text -------------------------------------------------------------------------

        /// <summary>Alice's conference with Bob and Carol seated, on one engine.</summary>
        private static void ThreeWayText(NonSpatialVoiceSessionEngine e, out UUID sessionId, out NonSpatialVoiceSession session)
        {
            sessionId = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob, Carol }).Session.SessionId;
            e.Accept(sessionId, Bob, RegionA, "vs-bob");
            e.Accept(sessionId, Carol, RegionA, "vs-carol");
            session = e.Store.Get(sessionId);
        }

        private static GridInstantMessage Text(NonSpatialVoiceSession s, UUID from, UUID to, string line, CarrierAuth auth,
                                               string roomKey = "use-session")
            => AdhocTextTransport.Build(to, from, "Test User", s.SessionId, RegionA, line, null,
                                        roomKey == "use-session" ? s.RoomKey : roomKey, auth);

        [Fact(Timeout = 60000)]
        public void AGenuineTextLineIsAccepted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession s);

            TextIntake t = CarrierIntake.AcceptText(Text(s, Alice, Bob, "hello there", Auth()), Auth(), e);

            Assert.True(t.Ok, t.Decision + " " + t.Reason);
            Assert.Equal("hello there", t.Message);
            Assert.Equal(Alice, t.Sender);
            Assert.Equal(Bob, t.Target);
        }

        // ---- the accepted-carrier log line --------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AnAcceptedTextLineIsMarkedAuthenticatedAndNamesItsOrigin()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession s);

            TextIntake t = CarrierIntake.AcceptText(Text(s, Alice, Bob, "hello there", Auth()), Auth(), e);

            Assert.True(t.Authenticated);
            Assert.Equal(RegionA, t.OriginRegion);
            string line = CarrierAuth.AcceptedLine(CarrierAuth.KindText, t.SessionId, NonSpatialSessionType.Adhoc, t.OriginRegion);
            Assert.Contains("kind=text", line);
            Assert.Contains("session=" + s.SessionId, line);
            Assert.Contains("type=Adhoc", line);
            Assert.Contains("from=" + RegionA, line);
            {
                var assertedValue = line;
                TestAssert.WithMessage(() => Assert.DoesNotContain("hello there", assertedValue), "contents never reach the log");
            }
        }

        [Fact(Timeout = 60000)]
        public void AnAcceptedRingCarriesItsTypeAndOrigin()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            GridInstantMessage im = Ring(Bob, s, ConferenceBody(a, s), Auth());

            RingIntake r = Offer(im, Auth(), Engine());

            Assert.True(r.Authenticated);
            Assert.Equal(NonSpatialSessionType.Adhoc, r.Type);
            Assert.Equal(RegionA, r.OriginRegion);
            string line = CarrierAuth.AcceptedLine(CarrierAuth.KindRing, r.GroupId, r.Type, r.OriginRegion);
            Assert.Contains("kind=ring", line);
            Assert.Contains("session=" + s.SessionId, line);
            Assert.Contains("from=" + RegionA, line);
        }

        [Fact(Timeout = 60000)]
        public void ARefusedCarrierIsNotMarkedAuthenticated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            GridInstantMessage forged = Ring(Bob, s, ConferenceBody(a, s), Auth(OtherSecret));
            Assert.False(Offer(forged, Auth(), Engine()).Authenticated);

            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession ts);
            Assert.False(CarrierIntake.AcceptText(Text(ts, Alice, Bob, "forged", null), Auth(), e).Authenticated);
        }

        [Fact(Timeout = 60000)]
        public void ATextLineWithNoMacIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession s);

            TextIntake t = CarrierIntake.AcceptText(Text(s, Alice, Bob, "forged", null), Auth(), e);

            Assert.False(t.Ok);
            Assert.Equal(CarrierAuth.MissingMac, t.Reason);
        }

        [Fact(Timeout = 60000)]
        public void ATextLineSignedWithAnotherSecretIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession s);
            Assert.Equal(CarrierAuth.BadMac, CarrierIntake.AcceptText(Text(s, Alice, Bob, "forged", Auth(OtherSecret)), Auth(), e).Reason);
        }

        [Fact(Timeout = 60000)]
        public void AForgedTextBodyOrSenderIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession s);

            // Words put in a signed line.
            GridInstantMessage im = Text(s, Alice, Bob, "meet at noon", Auth());
            Rewrite(im, AdhocTextTransport.BucketMagic, m => m[AdhocTextTransport.KeyMessage] = OSD.FromString("send me money"));
            TextIntake edited = CarrierIntake.AcceptText(im, Auth(), e);
            Assert.False(edited.Ok);
            Assert.Equal(CarrierAuth.BadMac, edited.Reason);

            // A signed line attributed to another member.
            im = Text(s, Alice, Bob, "meet at noon", Auth());
            im.fromAgentID = Carol.Guid;
            Assert.Equal(CarrierAuth.BadMac, CarrierIntake.AcceptText(im, Auth(), e).Reason);

            // A signed line aimed at another member.
            im = Text(s, Alice, Bob, "meet at noon", Auth());
            im.toAgentID = Carol.Guid;
            Assert.Equal(CarrierAuth.BadMac, CarrierIntake.AcceptText(im, Auth(), e).Reason);
        }

        [Fact(Timeout = 60000)]
        public void AStaleOrReplayedTextLineIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession s);
            GridInstantMessage im = Text(s, Alice, Bob, "once", Auth());
            CarrierAuth receiver = Auth();

            Assert.True(CarrierIntake.AcceptText(im, receiver, e).Ok);
            Assert.Equal(CarrierAuth.Replay, CarrierIntake.AcceptText(im, receiver, e).Reason);

            GridInstantMessage old = Text(s, Alice, Bob, "old", Auth());
            _now = T0 + 1000;
            Assert.Equal(CarrierAuth.Stale, CarrierIntake.AcceptText(old, Auth(), e).Reason);
        }

        [Fact(Timeout = 60000)]
        public void ASignedTextLineWithAMissingOrWrongRoomKeyIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession s);

            TextIntake missing = CarrierIntake.AcceptText(Text(s, Alice, Bob, "hi", Auth(), roomKey: ""), Auth(), e);
            Assert.False(missing.Ok);
            Assert.Equal(CarrierIntake.DecisionRoomKeyMismatch, missing.Decision);

            TextIntake wrong = CarrierIntake.AcceptText(Text(s, Alice, Bob, "hi", Auth(), roomKey: "other"), Auth(), e);
            Assert.False(wrong.Ok);
            Assert.Equal(CarrierIntake.DecisionRoomKeyMismatch, wrong.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ASignedLineFromANonMemberIsStillRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession s);
            UUID stranger = new UUID("99999999-9999-9999-9999-999999999999");

            TextIntake t = CarrierIntake.AcceptText(Text(s, stranger, Bob, "hi", Auth()), Auth(), e);

            Assert.False(t.Ok);
            Assert.Equal(AdhocTextSession.DecisionNotMember, t.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ARingIsNotReadAsTextAndTextIsNotReadAsARing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            NonSpatialVoiceSession s = Conference(a, Bob);
            GridInstantMessage ring = Ring(Bob, s, ConferenceBody(a, s), Auth());
            Assert.False(CarrierIntake.AcceptText(ring, Auth(), a).Claimed);

            NonSpatialVoiceSessionEngine e = Engine();
            ThreeWayText(e, out _, out NonSpatialVoiceSession ts);
            Assert.False(Offer(Text(ts, Alice, Bob, "hi", Auth()), Auth(), e).Claimed);
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
