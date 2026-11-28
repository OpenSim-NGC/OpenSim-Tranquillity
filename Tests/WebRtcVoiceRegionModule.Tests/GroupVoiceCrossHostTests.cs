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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void TheCarrierRoundTrips()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine a) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, a, RegionA, out _);
            OSDMap body = GroupVoiceInvite.BuildBody(a.Store.Get(grp), a.IssueToken(grp, Alice), Alice, "Test User", "G");

            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Test User", grp, RegionA, body);

            Assert.True(GroupVoiceRingTransport.TryParse(im, out UUID target, out UUID gid, out OSDMap got));
            Assert.Equal(Bob, target);
            Assert.Equal(grp, gid);
            Assert.Equal(grp, got["session_id"].AsUUID());
            Assert.Equal(a.Store.Get(grp).RoomKey, ((OSDMap)got["voice"])["channel_uri"].AsString());
            {
                var assertedValue = im.offline;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "a ring must not be stored and replayed later");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheCarrierUsesSessionSendWithFromGroupFalse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The one combination no other OnIncomingInstantMessage subscriber claims; the group
            // messaging module takes SessionSend only when fromGroup is true.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine a) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, a, RegionA, out _);
            OSDMap body = GroupVoiceInvite.BuildBody(a.Store.Get(grp), a.IssueToken(grp, Alice), Alice, "Test User", "G");

            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Test User", grp, RegionA, body);

            Assert.Equal(GroupVoiceRingTransport.DialogSessionSend, im.dialog);
            Assert.False(im.fromGroup);
        }

        [Fact(Timeout = 60000)]
        public void ForeignMessagesAreNotOurs()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(GroupVoiceRingTransport.TryParse(null, out _, out _, out _));
            Assert.False(GroupVoiceRingTransport.TryParse(new GridInstantMessage { dialog = 17, fromGroup = true }, out _, out _, out _), "group chat text must never be taken for a ring");
            Assert.False(GroupVoiceRingTransport.TryParse(new GridInstantMessage { dialog = 17, fromGroup = false, binaryBucket = null }, out _, out _, out _));
            Assert.False(GroupVoiceRingTransport.TryParse(new GridInstantMessage { dialog = 17, fromGroup = false, binaryBucket = System.Text.Encoding.UTF8.GetBytes("not ours at all") }, out _, out _, out _), "the magic header is what identifies us, not the dialog");
            Assert.False(GroupVoiceRingTransport.TryParse(new GridInstantMessage { dialog = 1, fromGroup = false }, out _, out _, out _));
        }

        // ---- the remote accept -------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ARemoteInstanceDerivesTheSameRoomKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            {
                var assertedValue = keyA;
                Assert.NotNull(assertedValue);
                Assert.NotEmpty(assertedValue);
            }
            {
                var assertedValue = keyB;
                TestAssert.WithMessage(() => Assert.Equal(keyA, assertedValue), "no coordination between the instances, same room");
            }
            Assert.Equal(NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, grp), keyA);
            {
                var assertedValue = NonSpatialRoomKey.Derive("other-grid", NonSpatialSessionType.Group, grp);
                TestAssert.WithMessage(() => Assert.NotEqual(keyA, assertedValue), "the grid id is part of the key");
            }
        }

        [Fact(Timeout = 60000)]
        public void AdoptingARingLetsTheRemoteInstanceAdmitTheAccept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp); g.Join(Bob, grp);
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);   // instance A: the call starts here
            (GroupVoicePolicy pb, NonSpatialVoiceSessionEngine b) = Instance(g);   // instance B: Bob is here

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out _);
            string room = a.Store.Get(grp).RoomKey;
            string token = a.IssueToken(grp, Alice);

            // B has never heard of this voice session until the ring lands
            Assert.False(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, pb, b, out _), "before the ring, B cannot admit an accept - this is why AdoptRemoteRing exists");

            SessionOutcome adopted = b.AdoptRemoteRing(grp, room, token, 50, Alice, new[] { Bob });
            Assert.True(adopted.Ok, adopted.Decision);
            {
                var assertedValue = b.Store.Get(grp).RoomKey;
                TestAssert.WithMessage(() => Assert.Equal(room, assertedValue), "same room key on both instances");
            }
            Assert.Equal(token, b.Store.Get(grp).Token);

            Assert.True(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, pb, b, out ChatSessionOutcome o));
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), o.Instrument);
            }
            Assert.True(b.Store.Get(grp).Find(Bob).HoldsSeat);
        }

        [Fact(Timeout = 60000)]
        public void AMismatchedGridIdIsRefusedLoudly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Two instances disagreeing about the grid id would put the halves of one call in
            // DIFFERENT mixer rooms and be silently inaudible. Refuse instead.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            (_, NonSpatialVoiceSessionEngine b) = Instance(g, grid: "a-different-grid");

            SessionOutcome r = b.AdoptRemoteRing(grp, NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, grp),
                                                 "tok", 50, Alice, null);

            Assert.False(r.Ok);
            Assert.Equal("refused-room-key-mismatch", r.Decision);
        }

        // ---- send-once across instances ------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AnAdoptedSessionNeverRingsAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // B must not fan out for a call it did not start: the ring latch is set at adoption.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            foreach (UUID x in new[] { Alice, Bob, Carol }) g.Join(x, grp);
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);
            (GroupVoicePolicy pb, NonSpatialVoiceSessionEngine b) = Instance(g);

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out _);
            b.AdoptRemoteRing(grp, a.Store.Get(grp).RoomKey, a.IssueToken(grp, Alice), 50, Alice, new[] { Bob });

            Assert.True(b.Store.Get(grp).RingSent, "latched at adoption");
            GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, pb, b, out ChatSessionOutcome o);
            Assert.False(o.StartedRinging, "B's first seat must not re-ring the group");
        }

        [Fact(Timeout = 60000)]
        public void ARungMemberIsNotAlsoATargetOfALaterFanOut()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            foreach (UUID x in new[] { Alice, Bob, Carol }) g.Join(x, grp);
            (GroupVoicePolicy pa, NonSpatialVoiceSessionEngine a) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, pa, a, RegionA, out _);

            a.MarkInvited(grp, new[] { Bob });   // rung cross-instance

            {
                var assertedValue = GroupVoiceInvite.Targets(new[] { Alice, Bob, Carol }, a.Store.Get(grp), Alice, pa);
                TestAssert.WithMessage(() => Assert.Equivalent(new[] { Carol }, assertedValue, strict: true), "a member rung remotely is not rung again locally");
            }
        }

        [Fact(Timeout = 60000)]
        public void LIMITATION_APhoneIconJoinOnAnotherInstanceRingsTheGroupASecondTime()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.True(first.StartedRinging, "A rings, correctly");

            // Bob never got the ring; he clicks the phone icon on instance B.
            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, pb, b, RegionB, out ChatSessionOutcome second);

            Assert.True(second.StartedRinging, "KNOWN LIMITATION: B rings the group a second time because it cannot see A's session");
            {
                var assertedValue = a.Store.Get(grp).RoomKey;
                TestAssert.WithMessage(() => Assert.Equal(b.Store.Get(grp).RoomKey, assertedValue), "the audio is still correct - both land in the same room; only the ring is duplicated");
            }
        }

        // ---- pre-ship check: never ring an offline member, never leave an offline IM --------------

        [Fact(Timeout = 60000)]
        public void OnlyMembersThePresenceServiceReportsOnlineAreRung()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // (a) An offline member cannot answer, and a ring that surfaced at their next login would
            // announce a call that ended hours ago.
            UUID here = UUID.Random();
            List<UUID> candidates = new List<UUID> { Alice, Bob, Carol, Dave };
            UUID Online(UUID a) => a == Bob || a == Dave ? here : UUID.Zero;   // Alice and Carol offline

            Assert.Equivalent(new[] { Bob, Dave }, GroupVoiceInvite.OnlineOnly(candidates, Online), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void NoPresenceServiceMeansNoCrossInstanceRingAtAll()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Fail closed: if we cannot establish that a member is online, we do not ring them.
            Assert.Empty(GroupVoiceInvite.OnlineOnly(new[] { Alice, Bob }, _ => UUID.Zero));
            Assert.Empty(GroupVoiceInvite.OnlineOnly(new[] { Alice }, null));
            Assert.Empty(GroupVoiceInvite.OnlineOnly(null, _ => UUID.Random()));
        }

        [Fact(Timeout = 60000)]
        public void APresenceLookupThatThrowsIsTreatedAsOffline()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equivalent(new[] { Bob }, GroupVoiceInvite.OnlineOnly(new[] { Alice, Bob },
                        a => a == Alice ? throw new InvalidOperationException("presence service down") : UUID.Random()), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void ARingIsNeverStorableAsAnOfflineIM()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            {
                var assertedValue = storable;
                TestAssert.WithMessage(() => Assert.DoesNotContain(GroupVoiceRingTransport.DialogSessionSend, assertedValue), "if SessionSend ever joins an offline allowlist, this test fails and the guard below becomes load-bearing");
            }

            // And the belt: a ring is marked non-offline at the source.
            Grid_ g = new Grid_();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine a) = Instance(g);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, a, RegionA, out _);
            OSDMap body = GroupVoiceInvite.BuildBody(a.Store.Get(grp), a.IssueToken(grp, Alice), Alice, "Test User", "G");

            Assert.Equal(0, GroupVoiceRingTransport.Build(Bob, Alice, "Test User", grp, RegionA, body).offline);
        }

        [Fact(Timeout = 60000)]
        public void LIMITATION_SeatsAndTheCapArePerInstance()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            {
                var assertedValue = a.Store.Get(grp).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "A sees only its own two");
            }
            {
                var assertedValue = b.Store.Get(grp).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "B sees only its own one");
            }
            {
                var assertedValue = NonSpatialCaps.MixerRoomCap;
                TestAssert.WithMessage(() => Assert.Equal(110, assertedValue), "and the mixer is the real ceiling");
            }
        }
    }
}
