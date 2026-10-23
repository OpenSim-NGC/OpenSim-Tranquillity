/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Minimal client for the Janus ADMIN API `message_plugin` request — used to push
 * server-to-server control messages (e.g. the visibility `peer_ctl_batch`) into a
 * plugin's handle_admin_message.
 *
 * Deliberately NOT a JanusSession: the admin path has no session semantics at all —
 * no CreateSession, no long-poll, no keepalive, no OnDisconnect. It is a stateless
 * authenticated POST. Modelling it as its own type (rather than reusing/partialing
 * JanusSession) states that structurally and keeps the shared session machinery
 * untouched. See docs/voice/mixer-feed-protocol.md.
 */

using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    /// <summary>Outcome of a Janus Admin-API plugin send.</summary>
    public enum AdminSendResult
    {
        /// <summary>
        /// Janus accepted the request and dispatched it to the plugin (janus:"success").
        /// CEILING: this does NOT mean the batch was applied. An unknown room still returns
        /// janus:"success" with {"slvoice":"applied"} (verified against 0.7.0-debug), and a
        /// plugin-level reject (e.g. malformed) also rides inside janus:"success". A caller
        /// CANNOT detect a wrong room, or a plugin-level error, from this value — confirm room
        /// identity out of band (the same CalcRoomNumber the mixer uses).
        /// </summary>
        Ok,

        /// <summary>
        /// The transport failed: an HTTP/socket exception, the per-call timeout, or a non-2xx
        /// status. "Janus is unreachable" and "HTTP 5xx" are indistinguishable here by design.
        /// </summary>
        TransportError,

        /// <summary>
        /// Janus responded (HTTP 2xx) but rejected the request at the protocol level
        /// (janus:"error") — e.g. a wrong/missing admin_secret (403 in the body), an unknown
        /// plugin, or a 2xx body Janus/we could not interpret.
        /// </summary>
        ProtocolError,
    }

    /// <summary>
    /// Sends Janus Admin-API <c>message_plugin</c> requests. One instance per region sink is
    /// expected; it owns one long-lived <see cref="HttpClient"/> for its lifetime (not one per
    /// call, not JanusSession's client). Authentication is <c>admin_secret</c> in the message
    /// BODY — the Admin API rejects <c>apisecret</c> with 403 (verified against 0.7.0-debug).
    /// </summary>
    public sealed class JanusAdminClient : IDisposable
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[JANUS ADMIN CLIENT]";

        private readonly string _adminUri;
        private readonly string _adminSecret;
        private readonly TimeSpan _timeout;
        private readonly HttpClient _httpClient;   // long-lived, one per client

        /// <summary>
        /// Construct from the same config WebRtcJanusService already reads
        /// (JanusGatewayAdminURI, AdminAPIToken) plus a per-send deadline.
        /// </summary>
        public JanusAdminClient(string adminUri, string adminSecret, TimeSpan timeout, HttpMessageHandler handler = null)
        {
            _adminUri = adminUri;
            _adminSecret = adminSecret;
            _timeout = timeout;
            // This is JanusAdminClient's OWN HttpClient (constructed and disposed here), NOT
            // JanusSession's shared _HttpClient. The absolute Timeout below is a coarse BACKSTOP to
            // the per-call CancellationTokenSource in SendWithTimeoutAsync: the CTS (1x admin timeout)
            // is the normal deadline; this fires only if the token failed to cancel (e.g. a half-dead
            // pooled connection stalling the response-body read). Set well beyond the per-call
            // deadline so it never pre-empts the normal cancellation path.
            // A null handler is the default one; the sink passes JanusTls's, which also validates https certificates.
            _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
            _httpClient.Timeout = ComputeClientTimeout(timeout);
        }

        /// <summary>The absolute <see cref="HttpClient.Timeout"/> backstop: 4x the admin timeout. The
        /// per-call token (1x) is the normal cancellation path; this only fires when it didn't. 4x
        /// leaves clear headroom so a legitimately slow request the CTS would already have bounded
        /// never trips it. Falls back to 20s if the admin timeout is non-positive.</summary>
        public static TimeSpan ComputeClientTimeout(TimeSpan adminTimeout)
            => adminTimeout > TimeSpan.Zero ? TimeSpan.FromTicks(adminTimeout.Ticks * 4) : TimeSpan.FromSeconds(20);

        /// <summary>Test-observability: the absolute timeout set on the owned HttpClient.</summary>
        public TimeSpan ClientTimeout => _httpClient.Timeout;

        public void Dispose() => _httpClient?.Dispose();

        /// <summary>
        /// Send one <c>message_plugin</c> to <paramref name="plugin"/>'s handle_admin_message,
        /// wrapping <paramref name="request"/> verbatim under "request" with admin_secret in the
        /// body. Reads the response inline (no ack/long-poll wait) and can never block past the
        /// client timeout. See <see cref="AdminSendResult.Ok"/> for the success ceiling.
        /// </summary>
        public async Task<AdminSendResult> SendPluginMessageAsync(string plugin, OSDMap request)
            => (await SendPluginMessageWithReplyAsync(plugin, request).ConfigureAwait(false)).Result;

        /// <summary>As <see cref="SendPluginMessageAsync"/>, but also returns the raw response BODY so a
        /// voice-specific caller (the sink) can parse the plugin's inner reply (slvoice / entries /
        /// mute_entries / skipped / deferred_listeners). This client stays plugin-agnostic and reads only
        /// the janus-level status (Interpret); the voice semantics live in the sink. Body is null on a
        /// transport failure (nothing was received).</summary>
        public Task<(AdminSendResult Result, string Body)> SendPluginMessageWithReplyAsync(string plugin, OSDMap request)
        {
            string body = BuildEnvelope(plugin, _adminSecret, request, Guid.NewGuid().ToString());
            return SendWithTimeoutWithReplyAsync(async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _adminUri)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("Accept", "application/json");
                using HttpResponseMessage resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (resp.StatusCode, respBody);
            }, _timeout);
        }

        // ---- Pure, public, static helpers (directly unit-testable, no HTTP) ------------------

        /// <summary>
        /// Build the Admin-API message_plugin envelope JSON. <c>admin_secret</c> goes in the BODY
        /// (apisecret is 403). <paramref name="request"/> is nested verbatim under "request".
        /// Serialized with OSDMap.ToString() — the same path the rest of the addon POSTs to Janus.
        /// </summary>
        public static string BuildEnvelope(string plugin, string adminSecret, OSDMap request, string transaction)
        {
            var env = new OSDMap
            {
                ["janus"] = new OSDString("message_plugin"),
                ["transaction"] = new OSDString(transaction ?? string.Empty),
                ["admin_secret"] = new OSDString(adminSecret ?? string.Empty),
                ["plugin"] = new OSDString(plugin ?? string.Empty),
                ["request"] = request ?? new OSDMap()
            };
            return env.ToString();
        }

        /// <summary>
        /// Map an HTTP (status, body) to an <see cref="AdminSendResult"/>: 2xx + janus:"success" ->
        /// Ok; 2xx + janus:"error" (or a 2xx body we cannot interpret) -> ProtocolError; any non-2xx
        /// -> TransportError. Janus's HTTP Admin API returns API-level errors (including a wrong
        /// admin_secret) as HTTP 200 with janus:"error", so auth failure surfaces as ProtocolError;
        /// a genuine non-2xx (proxy / server down) is TransportError.
        /// </summary>
        public static AdminSendResult Interpret(HttpStatusCode status, string body)
        {
            if ((int)status < 200 || (int)status >= 300)
                return AdminSendResult.TransportError;
            try
            {
                if (OSDParser.DeserializeJson(body ?? string.Empty) is OSDMap map
                    && map.ContainsKey("janus")
                    && map["janus"].AsString() == "success")
                {
                    return AdminSendResult.Ok;
                }
            }
            catch
            {
                // A 2xx body we cannot parse is a protocol surprise, not transport-dead.
            }
            return AdminSendResult.ProtocolError;
        }

        /// <summary>
        /// Run <paramref name="send"/> under a fresh per-call CancellationTokenSource(timeout) and
        /// map the outcome. The timeout or ANY exception (HttpRequestException, socket, cancellation)
        /// -> TransportError, so a hung/slow send can never wedge the caller. Kept separate and
        /// Func-injected so it unit-tests without HTTP (inject a delaying or throwing send).
        /// </summary>
        public static async Task<AdminSendResult> SendWithTimeoutAsync(
            Func<CancellationToken, Task<(HttpStatusCode status, string body)>> send, TimeSpan timeout)
            => (await SendWithTimeoutWithReplyAsync(send, timeout).ConfigureAwait(false)).Result;

        /// <summary>As <see cref="SendWithTimeoutAsync"/>, but also returns the raw response body (null
        /// on timeout/exception). The body is carried so a voice-specific caller can parse the inner
        /// slvoice reply; the transport mapping (<see cref="Interpret"/>) is unchanged.</summary>
        public static async Task<(AdminSendResult Result, string Body)> SendWithTimeoutWithReplyAsync(
            Func<CancellationToken, Task<(HttpStatusCode status, string body)>> send, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                (HttpStatusCode status, string respBody) = await send(cts.Token).ConfigureAwait(false);
                return (Interpret(status, respBody), respBody);
            }
            catch (Exception)
            {
                return (AdminSendResult.TransportError, null);
            }
        }
    }
}
