/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;

using OpenSim.Framework;
using OpenSim.Services.Base;

using OpenMetaverse.StructuredData;
using OpenMetaverse;

using Nini.Config;
using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

// Making a parcel's spatial room exist ahead of its first join.
public partial class WebRtcJanusService
{
    // Ensure the spatial room for a parcel exists, through the same service session the console
    // uses. SelectRoom is the provisioning path's own call, so the room is created with the same flags a viewer's
    // would be, including vis_authority when this sim arms (JanusAudioBridge.ShouldDeclareVisAuthority), and its
    // process-wide coalescing makes a second call for a live room a no-op.
    public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID)
    {
        try
        {
            return EnsureSpatialRoomAsync(pSceneID, pParcelLocalID).Result;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, $"{LogHeader} EnsureSpatialRoom failed for scene {pSceneID} parcel {pParcelLocalID}");
            return null;
        }
    }

    private async Task<int?> EnsureSpatialRoomAsync(UUID pSceneID, int pParcelLocalID)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            JanusViewerSession svc = await EnsureServiceSessionAsync(pForceReconnect: attempt > 0).ConfigureAwait(false);
            if (svc?.AudioBridge is null)
                return null;
            // A select asks the mixer every time, so an existing room is reused.
            JanusRoom room = await svc.AudioBridge.EnsureRoom(pSceneID.ToString(), "local", true, pParcelLocalID,
                string.Empty).ConfigureAwait(false);
            if (room is not null)
                return room.RoomId;
        }
        return null;
    }
}
