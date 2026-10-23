/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Shared server-side authorisation for parcel voice moderation: may this avatar moderate voice on
 * this parcel? Composes SL's three cases — land owner, estate manager/owner, or a member with
 * GroupPowers.ModerateChat on a group-owned parcel — the same pieces the ban path uses.
 *
 * Scene-coupled (it needs EstateSettings + IGroupsModule), so it lives here in the adapter assembly,
 * NOT in the Scene-free Visibility engine. One definition, two callers:
 *   - the SpatialVoiceModerationRequest CAP handler, to gate a moderation request; and
 *   - FeederWorldFromScene, to EXEMPT a moderator from being muted by their own / a peer's mute.
 * Sharing keeps "who may moderate" in exactly one place.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public static class VoiceModerationAuth
    {
        public static bool MayModerate(Scene scene, LandData land, UUID agentID)
        {
            if (land == null)
                return false;
            if (agentID.Equals(land.OwnerID))
                return true;
            if (scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(agentID))
                return true;
            if (land.IsGroupOwned && land.GroupID.IsNotZero())
            {
                IGroupsModule groups = scene.RequestModuleInterface<IGroupsModule>();
                GroupMembershipData gmd = groups?.GetMembershipData(land.GroupID, agentID);
                if (gmd is not null && (gmd.GroupPowers & (ulong)GroupPowers.ModerateChat) != 0)
                    return true;
            }
            return false;
        }
    }
}
