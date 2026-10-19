/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Carrying a group voice ring to another simulator process.
 *
 * Two instances are modelled as two engines over two SEPARATE in-process stores -- which is exactly
 * what they are today, and the reason the seat and cap limitations below exist. When
 * INonSpatialSessionStore is backed by a grid-scoped service, these same tests run against one
 * shared store and the limitations disappear without the engine changing.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class GroupVoiceCrossHostTests
    {
        private const string Grid = "example-grid";
        private static readonly UUID Dave = new UUID("44444444-4444-4444-4444-444444444444");

        private sealed class Grid_
        {
            public readonly HashSet<(UUID, UUID)> Members = new();
            public readonly Dictionary<(UUID, UUID), ulong> Powers = new();
            public void Join(UUID a, UUID g, ulong p = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice)
            { Members.Add((a, g)); Powers[(a, g)] = p; }
            public bool IsMember(UUID a, UUID g) => Members.Contains((a, g));
            public ulong Of(UUID a, UUID g) => Powers.TryGetValue((a, g), out ulong v) ? v : 0UL;
        }

        /// <summary>One simulator process: its own policy and its own store.</summary>
        private static (GroupVoicePolicy, NonSpatialVoiceSessionEngine) Instance(Grid_ g, string grid = Grid)
        {
            GroupVoicePolicy p = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = true,
                Cap = NonSpatialCaps.DefaultConferenceCap,
                IsMember = g.IsMember,
                Powers = g.Of,
            };
            NonSpatialVoiceSessionEngine e = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), p.ToAdmissionOrNull() },
                grid);
            return (p, e);
        }

        private static OSDMap Call(UUID grp) => new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(grp) };
        private static OSDMap Accept(UUID grp) => new OSDMap { ["method"] = OSD.FromString("accept invitation"), ["session-id"] = OSD.FromUUID(grp) };

        // ---- the carrier ------------------------------------------------------------------

        [Test]
        public void TheCarrierRoundTrips()
        {
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine a) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, a, RegionA, out _);
            OSDMap body = GroupVoiceInvite.BuildBody(a.Store.Get(grp), a.IssueToken(grp, Alice), Alice, "Test User", "G");

            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Test User", grp, RegionA, body);

            Assert.That(GroupVoiceRingTransport.TryParse(im, out UUID target, out UUID gid, out OSDMap got), Is.True);
            Assert.That(target, Is.EqualTo(Bob));
            Assert.That(gid, Is.EqualTo(grp));
            Assert.That(got["session_id"].AsUUID(), Is.EqualTo(grp));
            Assert.That(((OSDMap)got["voice"])["channel_uri"].AsString(), Is.EqualTo(a.Store.Get(grp).RoomKey));
            Assert.That(im.offline, Is.EqualTo(0), "a ring must not be stored and replayed later");
        }

        [Test]
        public void TheCarrierUsesSessionSendWithFromGroupFalse()
        {
            // The one combination no other OnIncomingInstantMessage subscriber claims; the group
            // messaging module takes SessionSend only when fromGroup is true.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine a) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, a, RegionA, out _);
            OSDMap body = GroupVoiceInvite.BuildBody(a.Store.Get(grp), a.IssueToken(grp, Alice), Alice, "Test User", "G");

            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Test User", grp, RegionA, body);

            Assert.That(im.dialog, Is.EqualTo(GroupVoiceRingTransport.DialogSessionSend));
            Assert.That(im.fromGroup, Is.False);
        }

        [Test]
        public void ForeignMessagesAreNotOurs()
        {
            Assert.That(GroupVoiceRingTransport.TryParse(null, out _, out _, out _), Is.False);
            Assert.That(GroupVoiceRingTransport.TryParse(new GridInstantMessage { dialog = 17, fromGroup = true }, out _, out _, out _),
                        Is.False, "group chat text must never be taken for a ring");
            Assert.That(GroupVoiceRingTransport.TryParse(new GridInstantMessage { dialog = 17, fromGroup = false, binaryBucket = null }, out _, out _, out _),
                        Is.False);
            Assert.That(GroupVoiceRingTransport.TryParse(
                        new GridInstantMessage { dialog = 17, fromGroup = false, binaryBucket = System.Text.Encoding.UTF8.GetBytes("not ours at all") },
                        out _, out _, out _), Is.False, "the magic header is what identifies us, not the dialog");
            Assert.That(GroupVoiceRingTransport.TryParse(new GridInstantMessage { dialog = 1, fromGroup = false }, out _, out _, out _), Is.False);
        }

        // ---- the remote accept -------------------------------------------------------------

        [Test]
        public void ARemoteInstanceDerivesTheSameRoomKey()
        {
            // The whole cross-host design rests on this: (gridId, tag, sessionId) with a shared grid id.
            UUID grp = UUID.Random();
            Grid_ g = new Grid_();
            g.Join(Alice, grp);
            g.Join(Bob, grp);
            // Two simulator processes, each with its own store, each creating the group's session itself.
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);
            (GroupVoicePolicy pb, NonSpatialVoiceSessionEngine b) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, pb, b, RegionA, out _);

            string keyA = a.Store.Get(grp).RoomKey;
            string keyB = b.Store.Get(grp).RoomKey;
            Assert.That(keyA, Is.Not.Null.And.Not.Empty);
            Assert.That(keyB, Is.EqualTo(keyA), "no coordination between the instances, same room");
            Assert.That(keyA, Is.EqualTo(NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, grp)));
            Assert.That(NonSpatialRoomKey.Derive("other-grid", NonSpatialSessionType.Group, grp), Is.Not.EqualTo(keyA),
                        "the grid id is part of the key");
        }

        [Test]
        public void AdoptingARingLetsTheRemoteInstanceAdmitTheAccept()
        {
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp); g.Join(Bob, grp);
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);   // instance A: the call starts here
            (GroupVoicePolicy pb, NonSpatialVoiceSessionEngine b) = Instance(g);   // instance B: Bob is here

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out _);
            string room = a.Store.Get(grp).RoomKey;
            string token = a.IssueToken(grp, Alice);

            // B has never heard of this voice session until the ring lands
            Assert.That(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, pb, b, out _), Is.False,
                        "before the ring, B cannot admit an accept - this is why AdoptRemoteRing exists");

            SessionOutcome adopted = b.AdoptRemoteRing(grp, room, token, 50, Alice, new[] { Bob });
            Assert.That(adopted.Ok, Is.True, adopted.Decision);
            Assert.That(b.Store.Get(grp).RoomKey, Is.EqualTo(room), "same room key on both instances");
            Assert.That(b.Store.Get(grp).Token, Is.EqualTo(token));

            Assert.That(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, pb, b, out ChatSessionOutcome o), Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), o.Instrument);
            Assert.That(b.Store.Get(grp).Find(Bob).HoldsSeat, Is.True);
        }

        [Test]
        public void AMismatchedGridIdIsRefusedLoudly()
        {
            // Two instances disagreeing about the grid id would put the halves of one call in
            // DIFFERENT mixer rooms and be silently inaudible. Refuse instead.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            (_, NonSpatialVoiceSessionEngine b) = Instance(g, grid: "a-different-grid");

            SessionOutcome r = b.AdoptRemoteRing(grp, NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, grp),
                                                 "tok", 50, Alice, null);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Decision, Is.EqualTo("refused-room-key-mismatch"));
        }

        // ---- send-once across instances ------------------------------------------------------

        [Test]
        public void AnAdoptedSessionNeverRingsAgain()
        {
            // B must not fan out for a call it did not start: the ring latch is set at adoption.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            foreach (UUID x in new[] { Alice, Bob, Carol }) g.Join(x, grp);
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);
            (GroupVoicePolicy pb, NonSpatialVoiceSessionEngine b) = Instance(g);

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out _);
            b.AdoptRemoteRing(grp, a.Store.Get(grp).RoomKey, a.IssueToken(grp, Alice), 50, Alice, new[] { Bob });

            Assert.That(b.Store.Get(grp).RingSent, Is.True, "latched at adoption");
            GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, pb, b, out ChatSessionOutcome o);
            Assert.That(o.StartedRinging, Is.False, "B's first seat must not re-ring the group");
        }

        [Test]
        public void ARungMemberIsNotAlsoATargetOfALaterFanOut()
        {
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            foreach (UUID x in new[] { Alice, Bob, Carol }) g.Join(x, grp);
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out _);

            a.MarkInvited(grp, new[] { Bob });   // rung cross-instance

            Assert.That(GroupVoiceInvite.Targets(new[] { Alice, Bob, Carol }, a.Store.Get(grp), Alice, pa),
                        Is.EquivalentTo(new[] { Carol }), "a member rung remotely is not rung again locally");
        }

        [Test]
        public void LIMITATION_APhoneIconJoinOnAnotherInstanceRingsTheGroupASecondTime()
        {
            // DOCUMENTED, NOT FIXED. A member on instance B who joins by the PHONE ICON rather than by
            // accepting a ring -- e.g. logged in after the ring went out -- gives B its own 0 -> 1,
            // and B has no way to know A already has the call running. B rings the group again.
            //
            // The latch only covers sessions B ADOPTED from a ring. Closing this needs the
            // grid-scoped store: B must be able to see A's voice session. Like the seat/cap limitation, it is
            // documented here rather than faked.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            foreach (UUID x in new[] { Alice, Bob, Carol }) g.Join(x, grp);
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);
            (GroupVoicePolicy pb, NonSpatialVoiceSessionEngine b) = Instance(g);

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out ChatSessionOutcome first);
            Assert.That(first.StartedRinging, Is.True, "A rings, correctly");

            // Bob never got the ring; he clicks the phone icon on instance B.
            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, pb, b, RegionB, out ChatSessionOutcome second);

            Assert.That(second.StartedRinging, Is.True,
                        "KNOWN LIMITATION: B rings the group a second time because it cannot see A's session");
            Assert.That(a.Store.Get(grp).RoomKey, Is.EqualTo(b.Store.Get(grp).RoomKey),
                        "the audio is still correct - both land in the same room; only the ring is duplicated");
        }

        // ---- pre-ship check: never ring an offline member, never leave an offline IM --------------

        [Test]
        public void OnlyMembersThePresenceServiceReportsOnlineAreRung()
        {
            // (a) An offline member cannot answer, and a ring that surfaced at their next login would
            // announce a call that ended hours ago.
            UUID here = UUID.Random();
            List<UUID> candidates = new List<UUID> { Alice, Bob, Carol, Dave };
            UUID Online(UUID a) => a == Bob || a == Dave ? here : UUID.Zero;   // Alice and Carol offline

            Assert.That(GroupVoiceInvite.OnlineOnly(candidates, Online), Is.EquivalentTo(new[] { Bob, Dave }));
        }

        [Test]
        public void NoPresenceServiceMeansNoCrossInstanceRingAtAll()
        {
            // Fail closed: if we cannot establish that a member is online, we do not ring them.
            Assert.That(GroupVoiceInvite.OnlineOnly(new[] { Alice, Bob }, _ => UUID.Zero), Is.Empty);
            Assert.That(GroupVoiceInvite.OnlineOnly(new[] { Alice }, null), Is.Empty);
            Assert.That(GroupVoiceInvite.OnlineOnly(null, _ => UUID.Random()), Is.Empty);
        }

        [Test]
        public void APresenceLookupThatThrowsIsTreatedAsOffline()
        {
            Assert.That(GroupVoiceInvite.OnlineOnly(new[] { Alice, Bob },
                        a => a == Alice ? throw new InvalidOperationException("presence service down") : UUID.Random()),
                        Is.EquivalentTo(new[] { Bob }));
        }

        [Test]
        public void ARingIsNeverStorableAsAnOfflineIM()
        {
            // (b) Guaranteed at the RECEIVER, not merely by us: both offline modules store only
            // MessageFromObject, MessageFromAgent, GroupNotice, GroupInvitation, InventoryOffered and
            // (core only) TaskInventoryOffered -- OfflineMessageModule.cs and
            // OfflineIMRegionModule.cs. SessionSend is in NEITHER allowlist, so an
            // undeliverable ring is dropped rather than replayed at the next login.
            byte[] storable =
            {
                4,   // MessageFromObject
                0,   // MessageFromAgent
                211, // GroupNotice
                208, // GroupInvitation
                9,   // InventoryOffered
                29,  // TaskInventoryOffered
            };
            Assert.That(storable, Does.Not.Contain(GroupVoiceRingTransport.DialogSessionSend),
                        "if SessionSend ever joins an offline allowlist, this test fails and the guard below becomes load-bearing");

            // And the belt: a ring is marked non-offline at the source.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine a) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, a, RegionA, out _);
            OSDMap body = GroupVoiceInvite.BuildBody(a.Store.Get(grp), a.IssueToken(grp, Alice), Alice, "Test User", "G");

            Assert.That(GroupVoiceRingTransport.Build(Bob, Alice, "Test User", grp, RegionA, body).offline, Is.EqualTo(0));
        }

        [Test]
        public void LIMITATION_SeatsAndTheCapArePerInstance()
        {
            // Each instance counts only its own members, so the engine gate cannot enforce 50 across
            // the grid. The mixer's SLV_MAX_MIX of 110 remains the real backstop: an over-cap join is
            // refused with 495 -> HTTP 409 -> the viewer's ERROR_CHANNEL_FULL.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            foreach (UUID x in new[] { Alice, Bob, Carol, Dave }) g.Join(x, grp);
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);
            (GroupVoicePolicy pb, NonSpatialVoiceSessionEngine b) = Instance(g);

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, pa, a, RegionA, out _);
            b.AdoptRemoteRing(grp, a.Store.Get(grp).RoomKey, a.IssueToken(grp, Alice), 50, Alice, null);
            GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Carol, pb, b, out _);

            Assert.That(a.Store.Get(grp).SeatsHeld, Is.EqualTo(2), "A sees only its own two");
            Assert.That(b.Store.Get(grp).SeatsHeld, Is.EqualTo(1), "B sees only its own one");
            Assert.That(NonSpatialCaps.MixerRoomCap, Is.EqualTo(110), "and the mixer is the real ceiling");
        }
    }
}
