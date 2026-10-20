/*
 * Copyright (c) 2026 Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
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
            // The ensure asks the mixer every time (since 0.8i every select does).
            JanusRoom room = await svc.AudioBridge.EnsureRoom(pSceneID.ToString(), "local", true, pParcelLocalID,
                string.Empty).ConfigureAwait(false);
            if (room is not null)
                return room.RoomId;
        }
        return null;
    }
}
