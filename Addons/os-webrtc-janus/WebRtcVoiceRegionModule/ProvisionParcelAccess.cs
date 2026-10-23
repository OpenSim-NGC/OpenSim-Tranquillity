/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Decides whether a local voice provision may go ahead on the resolved parcel.
 *
 * Voice applies the parcel's voice-off setting and ban list itself, on every estate, including TaxFree estates:
 *  - A parcel with voice turned off never gets voice.
 *  - A parcel ban is honoured. The ban list is evaluated here (LandBan) as well as taking the simulator's own
 *    answer (coreBars). Access-list restrictions are left to the simulator's answer.
 *
 * Pure and Scene-free so the decision unit-tests without a Scene.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public enum ProvisionAccess
    {
        Allowed,
        /// <summary>The parcel has voice turned off.</summary>
        VoiceOff,
        /// <summary>The avatar is banned from, or restricted from, the parcel.</summary>
        Barred,
    }

    public static class ProvisionParcelAccess
    {
        /// <param name="land">The resolved parcel.</param>
        /// <param name="coreBars">The sim's answer: IsRestrictedFromLand || IsBannedFromLand.</param>
        public static ProvisionAccess Check(
            LandData land,
            bool coreBars,
            Func<UUID, bool> isAdministrator,
            Func<UUID, bool> isEstateManagerOrOwner,
            UUID avatar,
            int nowUnix)
        {
            if ((land.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                return ProvisionAccess.VoiceOff;

            if (coreBars || LandBan.IsBanned(land, isAdministrator, isEstateManagerOrOwner, avatar, nowUnix))
                return ProvisionAccess.Barred;

            return ProvisionAccess.Allowed;
        }
    }
}
