/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The room a connector belongs in.
 *
 * A connector that always recorded the ESTATE room (VoiceConnectorModule.StartRecord) would, on a parcel that
 * runs its own voice channel, have the sim arm the NPC, mint its capability and address its batches at a room no
 * avatar standing beside it would ever be in — and, where nobody had provisioned the estate channel, at a room that
 * did not exist at all.
 *
 * The rule here is the one the provisioning path applies to an avatar, extracted so it can be asked without a viewer
 * request: the parcel's own channel unless the parcel says UseEstateVoiceChan, then the estate/local channel
 * (WebRtcVoiceRegionModule.cs:708 for the flag, :743-752 for what reaches parcel_local_id), hashed by the same
 * CalcRoomNumber the mixer side computes (JanusAudioBridge.cs:266, via WebRtcJanusService.cs:424).
 *
 * Pure and Scene-free: the caller resolves the parcel, this decides the channel and the number.
 */

using OpenMetaverse;
using OpenSim.Framework;

namespace osWebRtcVoice;

public static class ConnectorRoomResolver
{
    /// <summary>The mixer room an avatar at this parcel in this region would be provisioned into. The grid id is
    /// deliberately empty: the "local" arm of CalcRoomNumber ignores it (JanusAudioBridge.cs:266-276), exactly as
    /// the provisioning path leaves it out.</summary>
    public static int RoomFor(UUID pRegionId, LandData pLand)
        => JanusAudioBridge.CalcRoomNumber(string.Empty, pRegionId.ToString(), "local",
            FeederWorldFromScene.ParcelLocalIdFor(pLand), string.Empty);

    /// <summary>True when the parcel runs the estate channel, so the connector's room is the estate room.</summary>
    public static bool IsEstateChannel(LandData pLand) => FeederWorldFromScene.ParcelLocalIdFor(pLand) == JanusAudioBridge.REGION_ROOM_ID;

    /// <summary>Run the ensure and, only if it answered with a room, tell the visibility
    /// authority that room EXISTS. A null answer proves nothing, so it resets nothing. Returns the ensure's answer.</summary>
    public static int? EnsureAndProve(System.Func<int?> pEnsure, System.Action<int> pRoomExists)
    {
        int? ensured = pEnsure();
        if (ensured.HasValue)
            pRoomExists?.Invoke(ensured.Value);
        return ensured;
    }
}
