/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Pure builders for the three ProvisionVoiceAccountRequest response maps. No Janus session,
 * no I/O, no async — extracted from WebRtcJanusService.ProvisionVoiceAccountRequestBAD so the
 * response SHAPE (the keys the region module serialises to the viewer, and the viewer reads by
 * name) can be asserted in a unit test without a live mixer.
 *
 * These reproduce the former inline literals exactly — same keys, same insertion order, same
 * OSD types via the same implicit conversions — and ProvisionResponseShapeTests pins that
 * equivalence byte-for-byte on both serialisation paths. Change the shape here and nowhere else.
 */

using OpenMetaverse.StructuredData;

namespace osWebRtcVoice;

public static class ProvisionResponseBuilder
{
    /// <summary>The JSEP-offer success map: the answer, the viewer session id, and, as an additive
    /// field, the mixer room the Janus session actually joined. Success path only.</summary>
    public static OSDMap BuildSuccess(OSDMap jsepAnswer, string viewerSessionId, int room)
    {
        return new OSDMap
        {
            { "jsep", jsepAnswer },
            { "viewer_session", viewerSessionId },
            { "room", room }
        };
    }

    /// <summary>The failure map. <c>error_code</c> is carried only when non-zero: today only
    /// the mixer's ROOM_FULL sets it, and the CAP handler maps that to HTTP 409. Every other
    /// failure leaves it 0 and the key absent.</summary>
    public static OSDMap BuildFailure(string errorMsg, int errorCode)
    {
        OSDMap ret = new OSDMap
        {
            { "response", "failed" },
            { "error", errorMsg }
        };
        if (errorCode != 0)
            ret["error_code"] = errorCode;
        return ret;
    }

    /// <summary>The logout acknowledgement.</summary>
    public static OSDMap BuildClosed()
    {
        return new OSDMap
        {
            { "response", "closed" }
        };
    }
}
