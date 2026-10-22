/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

// How loudly to log a plugin event that no request is waiting for.
public partial class JanusSession
{
    /// <summary>
    /// The level for a plugin event that arrived with no request waiting for it.
    /// A room broadcast (another participant joined, finished setup or left) is pushed by the plugin to every
    /// participant's handle with no transaction, so having no request waiting is its normal case: Debug.
    /// An event that names a transaction nobody waits for (a reply that came after its request gave up), or one that
    /// reports an error, is not normal and stays at Error.
    /// </summary>
    public static LogLevel UnclaimedEventLevel(JanusMessageResp pResp)
    {
        if (pResp is null)
            return LogLevel.Error;
        if (!string.IsNullOrEmpty(pResp.TransactionId))
            return LogLevel.Error;
        PluginMsgResp plugin = new PluginMsgResp(pResp);
        if (plugin.PluginRespData is not null && plugin.PluginRespDataInt("error_code") != 0)
            return LogLevel.Error;
        return LogLevel.Debug;
    }
}
