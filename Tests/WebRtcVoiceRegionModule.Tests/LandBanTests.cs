/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for LandBan.IsBanned - the ban-list evaluation voice applies itself, on every estate. Pure:
 * exercised against real LandData / LandAccessEntry shapes with no Scene.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class LandBanTests
    {
        private const int Now = 1_000_000;
        private static readonly UUID Owner = new UUID("00000000-0000-0000-0000-0000000000ff");
        private static readonly UUID Banned = new UUID("00000000-0000-0000-0000-00000000000a");
        private static readonly UUID Other = new UUID("00000000-0000-0000-0000-00000000000b");

        private static readonly Func<UUID, bool> NoAdmin = _ => false;
        private static readonly Func<UUID, bool> NoEmOrOwner = _ => false;

        private static LandData LandBannedWith(int expires)
        {
            var ld = new LandData { OwnerID = Owner };
            ld.Flags |= (uint)ParcelFlags.UseBanList;
            ld.ParcelAccessList.Add(new LandAccessEntry { AgentID = Banned, Flags = AccessList.Ban, Expires = expires });
            return ld;
        }

        [Test]
        public void PermanentBan_WithUseBanList_IsBanned()
        {
            LandData ld = LandBannedWith(expires: 0);
            Assert.That(LandBan.IsBanned(ld, NoAdmin, NoEmOrOwner, Banned, Now), Is.True);
        }

        [Test]
        public void LiveTimedBan_IsBanned()
        {
            LandData ld = LandBannedWith(expires: Now + 100);
            Assert.That(LandBan.IsBanned(ld, NoAdmin, NoEmOrOwner, Banned, Now), Is.True);
        }

        [Test]
        public void ExpiredTimedBan_IsNotBanned()
        {
            LandData ld = LandBannedWith(expires: Now - 100);
            Assert.That(LandBan.IsBanned(ld, NoAdmin, NoEmOrOwner, Banned, Now), Is.False);
        }

        [Test]
        public void BanEntry_ButUseBanListFlagOff_IsNotBanned()
        {
            LandData ld = LandBannedWith(expires: 0);
            ld.Flags &= ~(uint)ParcelFlags.UseBanList;   // clear the gate
            Assert.That(LandBan.IsBanned(ld, NoAdmin, NoEmOrOwner, Banned, Now), Is.False);
        }

        [Test]
        public void NonBannedAvatar_IsNotBanned()
        {
            LandData ld = LandBannedWith(expires: 0);
            Assert.That(LandBan.IsBanned(ld, NoAdmin, NoEmOrOwner, Other, Now), Is.False);
        }

        [Test]
        public void Administrator_IsExempt()
        {
            LandData ld = LandBannedWith(expires: 0);
            Func<UUID, bool> isAdmin = a => a.Equals(Banned);
            Assert.That(LandBan.IsBanned(ld, isAdmin, NoEmOrOwner, Banned, Now), Is.False);
        }

        [Test]
        public void EstateManagerOrOwner_IsExempt()
        {
            LandData ld = LandBannedWith(expires: 0);
            Func<UUID, bool> isEmOrOwner = a => a.Equals(Banned);
            Assert.That(LandBan.IsBanned(ld, NoAdmin, isEmOrOwner, Banned, Now), Is.False);
        }

        [Test]
        public void ParcelOwner_IsExempt()
        {
            var ld = new LandData { OwnerID = Banned };   // the banned id also owns the parcel
            ld.Flags |= (uint)ParcelFlags.UseBanList;
            ld.ParcelAccessList.Add(new LandAccessEntry { AgentID = Banned, Flags = AccessList.Ban, Expires = 0 });
            Assert.That(LandBan.IsBanned(ld, NoAdmin, NoEmOrOwner, Banned, Now), Is.False);
        }
    }
}
