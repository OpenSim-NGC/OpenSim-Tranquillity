/*
 * Copyright (c) Contributors, http://opensimulator.org/
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
// Adapted 2026 by Legion Builds

using System.Reflection;

using OpenSim.Framework;
using OpenSim.Services.Base;

using OpenMetaverse.StructuredData;
using OpenMetaverse;

using Nini.Config;
using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

// Janus request hardening: bounded requests and the self-healing service session.
public partial class WebRtcJanusService
{
    // Mixer's JANUS_SLVOICE_ERROR_ROOM_FULL (janus_slvoice.c). ONLY this join-failure code
    // becomes an HTTP 409 / ERROR_CHANNEL_FULL downstream; every other failure stays generic.
    // Public so the region CAP handler references it (same namespace; a const is compile-time-
    // inlined, so no runtime assembly/ALC crossing) instead of duplicating the 495 literal.
    public const int JANUS_ROOM_FULL_ERROR_CODE = 495;

    /// <summary>Janus core's "No such session" (`janus: error`, code 458): the session id is gone, as it is for every
    /// session after the mixer restarts.</summary>
    public const int JANUS_NO_SUCH_SESSION_ERROR_CODE = 458;

    /// <summary>: the mixer's "No such room" on a join (`Request error 485: No such room (n)`).</summary>
    public const int JANUS_NO_SUCH_ROOM_ERROR_CODE = 485;

    /// <summary>The shortest secret accepted for the join capability key, and the length below which the Janus API
    /// and admin secrets draw a warning. Matches the connector capability secret's minimum.</summary>
    public const int MinSecretLength = 32;

    // A Janus secret shorter than MinSecretLength is easy to guess. The API and admin secrets already exist, so a
    // short one warns and the service starts as before. The warning names the key only: neither the value nor its
    // length goes into the log. An empty value is handled by the missing-fields check, not here.
    private void WarnOnShortJanusSecrets()
    {
        if (!string.IsNullOrEmpty(_JanusAPIToken) && _JanusAPIToken.Length < MinSecretLength)
            _log.LogWarning($"{LogHeader} [JanusWebRtcVoice] APIToken is shorter than {MinSecretLength} characters; use a longer random secret");
        if (!string.IsNullOrEmpty(_JanusAdminToken) && _JanusAdminToken.Length < MinSecretLength)
            _log.LogWarning($"{LogHeader} [JanusWebRtcVoice] AdminAPIToken is shorter than {MinSecretLength} characters; use a longer random secret");
    }

    // [JanusWebRtcVoice] JanusGatewayCACertificateFile — a private CA certificate the sim trusts, in addition to the
    // system's roots, for https gateway and admin URLs. Empty by default; see JanusTls.
    private string _JanusCaCertificateFile = string.Empty;

    private void ReadTlsConfig(IConfig janusConfig)
    {
        _JanusCaCertificateFile = janusConfig.GetString(JanusTls.CaCertificateFileKey, string.Empty);
    }

    // The handler every Janus session's HttpClient uses: the test factory when there is one, otherwise the
    // validating handler for http and https URLs alike.
    private HttpMessageHandler CreateHttpHandler()
        => _httpHandlerFactory?.Invoke() ?? JanusTls.CreateHandler(_JanusCaCertificateFile);

    // [JanusWebRtcVoice] RequestTimeoutMs — how long an ack'd Janus request waits for its event.
    private int _JanusRequestTimeoutMs = JanusSession.DefaultRequestTimeoutMs;

    // Serializes EnsureServiceSessionAsync, so two console commands cannot both reconnect.
    private readonly SemaphoreSlim _serviceSessionGate = new SemaphoreSlim(1, 1);

    // The long poll of a Janus session exited (GETERROR - e.g. the mixer restarted and the session id is
    // gone). A viewer session takes the hangup path as before. The SERVICE session (console commands) was
    // never found by that lookup, so it stayed "connected" and every later console command used a dead
    // session until the region restarted; mark it disconnected so the next console command reconnects.
    private void Handle_Disconnect(EventResp pResp)
    {
        if (MarkServiceSessionDisconnected(pResp))
            return;
        Handle_Hangup(pResp);
    }

    // TRUE when the event names the service session; that Janus session is then marked disconnected.
    private bool MarkServiceSessionDisconnected(EventResp pResp)
    {
        JanusSession service = _ViewerSession?.Session;
        if (pResp is null || service is null || string.IsNullOrEmpty(pResp.sessionId) || pResp.sessionId != service.SessionId)
            return false;
        service.IsConnected = false;
        _log.LogWarning($"{LogHeader} service session {service.SessionId} lost its long poll; the next console command reconnects it");
        return true;
    }

    /// The Janus session id of the service (console) session, or null when there is none (diagnostics and tests).
    public string ServiceSessionId => _ViewerSession?.Session?.SessionId;

    /// Whether the service session is currently marked connected (diagnostics and tests).
    public bool ServiceSessionIsConnected => _ViewerSession?.Session?.IsConnected ?? false;

    // The console-side service session is lazy and self-healing. Returns the live service
    // session, replacing it first when it is missing, marked disconnected (its long poll exited), or
    // pForceReconnect says the caller just saw a request fail on it (a Janus session the restarted mixer no longer
    // knows answers 458 without any disconnect event). The old session is shut down best-effort, and a fresh
    // session + plugin handle is built by ConnectToSessionAndAudioBridge - the provisioning path. Null when
    // Janus cannot be reached.
    public async Task<JanusViewerSession> EnsureServiceSessionAsync(bool pForceReconnect = false)
    {
        await _serviceSessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            JanusViewerSession current = _ViewerSession;
            if (!pForceReconnect && current?.Session is not null && current.Session.IsConnected && current.AudioBridge is not null)
                return current;

            if (current is not null)
            {
                try
                {
                    await current.Shutdown().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log.LogDebug($"{LogHeader} service session shutdown before reconnect threw: {e.Message}");
                }
            }

            var fresh = new JanusViewerSession(this);
            if (!await ConnectToSessionAndAudioBridge(fresh).ConfigureAwait(false))
            {
                _ViewerSession = null;
                _log.LogWarning($"{LogHeader} service session could not be reconnected (Janus unreachable?)");
                return null;
            }
            _ViewerSession = fresh;
            _log.LogInformation($"{LogHeader} service session reconnected");
            return fresh;
        }
        finally
        {
            _serviceSessionGate.Release();
        }
    }

    // "janus list rooms" through the service session. If the request fails on the current session it is
    // retried ONCE on a forced reconnect, so a mixer restart heals on the next console command.
    public async Task<AudioBridgeResp> ServiceListRoomsAsync()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            JanusViewerSession svc = await EnsureServiceSessionAsync(pForceReconnect: attempt > 0).ConfigureAwait(false);
            if (svc?.AudioBridge is null)
                return null;
            AudioBridgeResp resp = await svc.AudioBridge.SendAudioBridgeMsg(new AudioBridgeListRoomsReq()).ConfigureAwait(false);
            if (resp is not null && resp.isSuccess)
                return resp;
        }
        return null;
    }

    // Reads how long an ack'd Janus request waits for its event.
    private void ReadRequestTimeoutConfig(IConfig janusConfig)
    {
        // Bound on every ack'd request (join/leave/create...); the provisioning.Result waits on it.
        _JanusRequestTimeoutMs = janusConfig.GetInt("RequestTimeoutMs", JanusSession.DefaultRequestTimeoutMs);
        if (_JanusRequestTimeoutMs <= 0)
        {
            _log.LogWarning($"{LogHeader} RequestTimeoutMs = {_JanusRequestTimeoutMs} is not positive; using {JanusSession.DefaultRequestTimeoutMs}");
            _JanusRequestTimeoutMs = JanusSession.DefaultRequestTimeoutMs;
        }
    }
}
