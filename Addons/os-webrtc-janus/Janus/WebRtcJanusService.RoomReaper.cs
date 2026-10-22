/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading;
using System.Threading.Tasks;

using Nini.Config;
using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

// Removing emptied rooms on the stock AudioBridge plugin.
public partial class WebRtcJanusService
{
    // [JanusWebRtcVoice] RemoveEmptyRooms: remove rooms this service created once they have stood empty for
    // EmptyRoomGraceSeconds. On by default: the stock plugin otherwise keeps every emptied room until Janus
    // restarts. It does nothing with a plugin that removes empty rooms itself (the slvoice mixer).
    public const string RemoveEmptyRoomsKey = "RemoveEmptyRooms";
    public const string EmptyRoomGraceSecondsKey = "EmptyRoomGraceSeconds";

    // How often the reaper looks at the room list.
    private const int RoomReapIntervalMs = 15_000;

    private EmptyRoomReaper _roomReaper;
    private Timer _roomReapTimer;
    private int _roomReapRunning;

    /// <summary>The reaper, or null when it is off (diagnostics and tests).</summary>
    public EmptyRoomReaper RoomReaper => _roomReaper;

    // Builds the reaper from the settings; null when the key turns it off or the plugin removes empty rooms itself.
    public static EmptyRoomReaper CreateRoomReaper(IConfig pJanusConfig, string pPluginName, ILogger pLog, Func<long> pNowMs = null)
    {
        if (!pJanusConfig.GetBoolean(RemoveEmptyRoomsKey, EmptyRoomReaper.DefaultEnabled))
        {
            pLog.LogInformation($"{LogHeader} emptied rooms are left in place ([JanusWebRtcVoice] {RemoveEmptyRoomsKey} is false)");
            return null;
        }
        if (pPluginName != DefaultPluginName)
        {
            // The slvoice mixer destroys its own empty rooms after its grace period; a second remover would only race it.
            pLog.LogInformation($"{LogHeader} not removing emptied rooms: plugin {pPluginName} is not the stock AudioBridge and is expected to remove them itself");
            return null;
        }
        int grace = pJanusConfig.GetInt(EmptyRoomGraceSecondsKey, EmptyRoomReaper.DefaultGraceSeconds);
        if (grace < 0)
        {
            pLog.LogWarning($"{LogHeader} {EmptyRoomGraceSecondsKey} = {grace} is negative; using {EmptyRoomReaper.DefaultGraceSeconds}");
            grace = EmptyRoomReaper.DefaultGraceSeconds;
        }
        return new EmptyRoomReaper(TimeSpan.FromSeconds(grace), pNowMs);
    }

    private void ReadRoomReaperConfig(IConfig pJanusConfig)
    {
        _roomReaper = CreateRoomReaper(pJanusConfig, _JanusPluginName, _log);
    }

    private void StartRoomReaper()
    {
        if (_roomReaper is null)
            return;
        _roomReapTimer = new Timer(_ => _ = ReapEmptyRoomsAsync(), null, RoomReapIntervalMs, RoomReapIntervalMs);
    }

    /// <summary>One reaper pass through the service session. Returns the rooms removed. Never throws.</summary>
    public async Task<int> ReapEmptyRoomsAsync()
    {
        EmptyRoomReaper reaper = _roomReaper;
        if (reaper is null || reaper.TrackedCount == 0 || Interlocked.Exchange(ref _roomReapRunning, 1) == 1)
            return 0;
        try
        {
            JanusViewerSession svc = await EnsureServiceSessionAsync().ConfigureAwait(false);
            JanusAudioBridge bridge = svc?.AudioBridge;
            if (bridge is null)
                return 0;
            return await reaper.SweepAsync(bridge.ListRoomParticipantCounts, bridge.DestroyRoomById).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogDebug($"{LogHeader} empty room sweep failed: {e.Message}");
            return 0;
        }
        finally
        {
            Interlocked.Exchange(ref _roomReapRunning, 0);
        }
    }
}
