/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for JanusAdminClient (the Janus Admin-API message_plugin transport).
 *
 * HTTP is abstracted the way the room-race tests abstracted Janus: the pure helpers
 * (BuildEnvelope / Interpret / SendWithTimeoutAsync) are exercised directly, and the
 * send is Func-injected — no live Janus is contacted. Response bodies are the literal
 * ones observed against the 0.7.0-debug throwaway.
 */

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusAdminClientTests
    {
        // Literal response bodies captured from the 0.7.0-debug run.
        private const string SuccessApplied =
            "{\"janus\":\"success\",\"transaction\":\"tA\",\"response\":{\"slvoice\":\"applied\",\"op\":\"add\",\"room\":999111,\"entries\":1,\"skipped\":0}}";
        private const string Error403MissingSecret =
            "{\"janus\":\"error\",\"transaction\":\"tB\",\"error\":{\"code\":403,\"reason\":\"Unauthorized request (wrong or missing secret/token)\"}}";
        private const string Error403ApiSecret =
            "{\"janus\":\"error\",\"transaction\":\"tC\",\"error\":{\"code\":403,\"reason\":\"Unauthorized request (wrong or missing secret/token)\"}}";
        private const string SuccessSlvoiceError =
            "{\"janus\":\"success\",\"transaction\":\"tD\",\"response\":{\"slvoice\":\"error\",\"reason\":\"malformed\"}}";

        private static OSDMap SamplePeerCtlBatch()
        {
            var excl = new OSDMap
            {
                ["11111111-1111-1111-1111-111111111111"] = new OSDArray
                {
                    new OSDString("22222222-2222-2222-2222-222222222222")
                }
            };
            return new OSDMap
            {
                ["request"] = new OSDString("peer_ctl_batch"),
                ["op"] = new OSDString("add"),
                ["room"] = new OSDInteger(999111),
                ["excl"] = excl
            };
        }

        // ---- Envelope shape (must match the proven-working literal) --------------------------
        [Test]
        public void BuildEnvelope_MatchesProvenWorkingShape_AdminSecretInBody_NestedRequest()
        {
            string json = JanusAdminClient.BuildEnvelope(
                "janus.plugin.slvoice", "sekret", SamplePeerCtlBatch(), "tX");

            var env = OSDParser.DeserializeJson(json) as OSDMap;
            Assert.That(env, Is.Not.Null, "envelope is a JSON object");
            Assert.That(env["janus"].AsString(), Is.EqualTo("message_plugin"));
            Assert.That(env["admin_secret"].AsString(), Is.EqualTo("sekret"), "admin_secret is in the BODY");
            Assert.That(env.ContainsKey("apisecret"), Is.False, "apisecret must NOT be present (Admin API 403s it)");
            Assert.That(env["plugin"].AsString(), Is.EqualTo("janus.plugin.slvoice"));
            Assert.That(env["transaction"].AsString(), Is.EqualTo("tX"));

            var req = env["request"] as OSDMap;
            Assert.That(req, Is.Not.Null, "request is a nested object");
            Assert.That(req["request"].AsString(), Is.EqualTo("peer_ctl_batch"));
            Assert.That(req["op"].AsString(), Is.EqualTo("add"));
            Assert.That(req["room"].AsInteger(), Is.EqualTo(999111));
            Assert.That((req["excl"] as OSDMap), Is.Not.Null, "excl is carried through");
        }

        // ---- Interpret mapping for each observed response class ------------------------------
        [Test]
        public void Interpret_Success_Applied_IsOk()
        {
            Assert.That(JanusAdminClient.Interpret(HttpStatusCode.OK, SuccessApplied),
                Is.EqualTo(AdminSendResult.Ok));
        }

        [Test]
        public void Interpret_Success_SlvoiceError_IsOk_TheCeiling()
        {
            // The plugin-level "slvoice":"error" (malformed) rides inside janus:"success",
            // so the transport sees Ok — exactly the ceiling: Ok != applied.
            Assert.That(JanusAdminClient.Interpret(HttpStatusCode.OK, SuccessSlvoiceError),
                Is.EqualTo(AdminSendResult.Ok));
        }

        [Test]
        public void Interpret_403MissingSecret_IsProtocolError()
        {
            Assert.That(JanusAdminClient.Interpret(HttpStatusCode.OK, Error403MissingSecret),
                Is.EqualTo(AdminSendResult.ProtocolError));
        }

        [Test]
        public void Interpret_403ApiSecret_IsProtocolError()
        {
            Assert.That(JanusAdminClient.Interpret(HttpStatusCode.OK, Error403ApiSecret),
                Is.EqualTo(AdminSendResult.ProtocolError));
        }

        [Test]
        public void Interpret_Non2xx_IsTransportError()
        {
            Assert.That(JanusAdminClient.Interpret(HttpStatusCode.ServiceUnavailable, ""),
                Is.EqualTo(AdminSendResult.TransportError), "503 -> TransportError");
            // A non-2xx maps to TransportError regardless of body (e.g. if a proxy returned 403).
            Assert.That(JanusAdminClient.Interpret(HttpStatusCode.Forbidden, Error403MissingSecret),
                Is.EqualTo(AdminSendResult.TransportError), "HTTP 403 status -> TransportError per spec");
        }

        [Test]
        public void Interpret_Uninterpretable2xx_IsProtocolError()
        {
            Assert.That(JanusAdminClient.Interpret(HttpStatusCode.OK, "not json at all"),
                Is.EqualTo(AdminSendResult.ProtocolError));
        }

        // ---- SendWithTimeoutAsync (HTTP abstracted; never blocks) ----------------------------
        [Test]
        public async Task SendWithTimeout_FastSuccess_IsOk()
        {
            AdminSendResult r = await JanusAdminClient.SendWithTimeoutAsync(
                _ => Task.FromResult((HttpStatusCode.OK, SuccessApplied)),
                TimeSpan.FromSeconds(1));
            Assert.That(r, Is.EqualTo(AdminSendResult.Ok));
        }

        [Test]
        public async Task SendWithTimeout_SendThrows_IsTransportError()
        {
            AdminSendResult r = await JanusAdminClient.SendWithTimeoutAsync(
                async _ => { await Task.Yield(); throw new HttpRequestException("connection refused"); },
                TimeSpan.FromSeconds(1));
            Assert.That(r, Is.EqualTo(AdminSendResult.TransportError));
        }

        [Test]
        public async Task SendWithTimeout_HungSend_ReturnsTransportError_DoesNotBlock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AdminSendResult r = await JanusAdminClient.SendWithTimeoutAsync(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return (HttpStatusCode.OK, "{}"); },
                TimeSpan.FromMilliseconds(50));
            sw.Stop();
            Assert.That(r, Is.EqualTo(AdminSendResult.TransportError), "timeout -> TransportError");
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
                "returned on the ~50ms deadline, not after the 30s hang");
        }

        // ---- FIX 1: absolute HttpClient.Timeout backstop -------------------------------------
        [Test]
        public void ComputeClientTimeout_IsFourTimesTheAdminTimeout()
        {
            Assert.That(JanusAdminClient.ComputeClientTimeout(TimeSpan.FromSeconds(5)),
                Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(JanusAdminClient.ComputeClientTimeout(TimeSpan.FromMilliseconds(250)),
                Is.EqualTo(TimeSpan.FromMilliseconds(1000)));
        }

        [Test]
        public void ComputeClientTimeout_NonPositive_FallsBackTo20s()
        {
            Assert.That(JanusAdminClient.ComputeClientTimeout(TimeSpan.Zero), Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(JanusAdminClient.ComputeClientTimeout(TimeSpan.FromSeconds(-1)), Is.EqualTo(TimeSpan.FromSeconds(20)));
        }

        [Test]
        public void Ctor_SetsAbsoluteTimeoutOnItsOwnHttpClient()
        {
            using var client = new JanusAdminClient("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5));
            Assert.That(client.ClientTimeout, Is.EqualTo(TimeSpan.FromSeconds(20)),
                "JanusAdminClient's own HttpClient must carry the 4x backstop timeout (not JanusSession's shared client)");
        }
    }
}
