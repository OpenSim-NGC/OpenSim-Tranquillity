/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Evaluates a parcel's ban list for the voice provisioning check, so that voice applies the ban list itself on
 * every estate. It follows LandObject.IsBannedFromLand: the same exemptions (administrators, estate managers and
 * the estate owner, the parcel owner) and the same scan of the parcel's access list for an unexpired ban entry.
 * Access restriction is not evaluated here; the provisioning check takes the simulator's answer for it.
 *
 * Pure and Scene-free (operates on a LandData snapshot + exemption delegates) so it unit-tests
 * against real LandData/LandAccessEntry shapes without a Scene.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public static class LandBan
    {
        public static bool IsBanned(
            LandData land,
            Func<UUID, bool> isAdministrator,
            Func<UUID, bool> isEstateManagerOrOwner,
            UUID avatar,
            int nowUnix)
        {
            if (land == null)
                return false;

            // The exemptions IsBannedFromLand applies.
            if (isAdministrator != null && isAdministrator(avatar))
                return false;
            if (isEstateManagerOrOwner != null && isEstateManagerOrOwner(avatar))
                return false;
            if (avatar.Equals(land.OwnerID))
                return false;

            // Ban-list scan, as in IsBannedFromLand.
            if ((land.Flags & (uint)ParcelFlags.UseBanList) == 0)
                return false;

            foreach (LandAccessEntry e in land.ParcelAccessList)
            {
                if (e.Flags == AccessList.Ban && e.AgentID.Equals(avatar))
                    return e.Expires == 0 || e.Expires > nowUnix;
            }
            return false;
        }
    }
}
