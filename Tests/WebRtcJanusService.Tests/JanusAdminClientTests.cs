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
        [Fact(Timeout = 60000)]
        public void BuildEnvelope_MatchesProvenWorkingShape_AdminSecretInBody_NestedRequest()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string json = JanusAdminClient.BuildEnvelope(
                "janus.plugin.slvoice", "sekret", SamplePeerCtlBatch(), "tX");

            var env = OSDParser.DeserializeJson(json) as OSDMap;
            {
                var assertedValue = env;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "envelope is a JSON object");
            }
            Assert.Equal("message_plugin", env["janus"].AsString());
            {
                var assertedValue = env["admin_secret"].AsString();
                TestAssert.WithMessage(() => Assert.Equal("sekret", assertedValue), "admin_secret is in the BODY");
            }
            Assert.False(env.ContainsKey("apisecret"), "apisecret must NOT be present (Admin API 403s it)");
            Assert.Equal("janus.plugin.slvoice", env["plugin"].AsString());
            Assert.Equal("tX", env["transaction"].AsString());

            var req = env["request"] as OSDMap;
            {
                var assertedValue = req;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "request is a nested object");
            }
            Assert.Equal("peer_ctl_batch", req["request"].AsString());
            Assert.Equal("add", req["op"].AsString());
            Assert.Equal(999111, req["room"].AsInteger());
            {
                var assertedValue = (req["excl"] as OSDMap);
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "excl is carried through");
            }
        }

        // ---- Interpret mapping for each observed response class ------------------------------
        [Fact(Timeout = 60000)]
        public void Interpret_Success_Applied_IsOk()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AdminSendResult.Ok, JanusAdminClient.Interpret(HttpStatusCode.OK, SuccessApplied));
        }

        [Fact(Timeout = 60000)]
        public void Interpret_Success_SlvoiceError_IsOk_TheCeiling()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The plugin-level "slvoice":"error" (malformed) rides inside janus:"success",
            // so the transport sees Ok — exactly the ceiling: Ok != applied.
            Assert.Equal(AdminSendResult.Ok, JanusAdminClient.Interpret(HttpStatusCode.OK, SuccessSlvoiceError));
        }

        [Fact(Timeout = 60000)]
        public void Interpret_403MissingSecret_IsProtocolError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AdminSendResult.ProtocolError, JanusAdminClient.Interpret(HttpStatusCode.OK, Error403MissingSecret));
        }

        [Fact(Timeout = 60000)]
        public void Interpret_403ApiSecret_IsProtocolError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AdminSendResult.ProtocolError, JanusAdminClient.Interpret(HttpStatusCode.OK, Error403ApiSecret));
        }

        [Fact(Timeout = 60000)]
        public void Interpret_Non2xx_IsTransportError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = JanusAdminClient.Interpret(HttpStatusCode.ServiceUnavailable, "");
                TestAssert.WithMessage(() => Assert.Equal(AdminSendResult.TransportError, assertedValue), "503 -> TransportError");
            }
            // A non-2xx maps to TransportError regardless of body (e.g. if a proxy returned 403).
            {
                var assertedValue = JanusAdminClient.Interpret(HttpStatusCode.Forbidden, Error403MissingSecret);
                TestAssert.WithMessage(() => Assert.Equal(AdminSendResult.TransportError, assertedValue), "HTTP 403 status -> TransportError per spec");
            }
        }

        [Fact(Timeout = 60000)]
        public void Interpret_Uninterpretable2xx_IsProtocolError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AdminSendResult.ProtocolError, JanusAdminClient.Interpret(HttpStatusCode.OK, "not json at all"));
        }

        // ---- SendWithTimeoutAsync (HTTP abstracted; never blocks) ----------------------------
        [Fact(Timeout = 60000)]
        public async Task SendWithTimeout_FastSuccess_IsOk()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            AdminSendResult r = await JanusAdminClient.SendWithTimeoutAsync(
                _ => Task.FromResult((HttpStatusCode.OK, SuccessApplied)),
                TimeSpan.FromSeconds(1));
            Assert.Equal(AdminSendResult.Ok, r);
        }

        [Fact(Timeout = 60000)]
        public async Task SendWithTimeout_SendThrows_IsTransportError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            AdminSendResult r = await JanusAdminClient.SendWithTimeoutAsync(
                async _ => { await Task.Yield(); throw new HttpRequestException("connection refused"); },
                TimeSpan.FromSeconds(1));
            Assert.Equal(AdminSendResult.TransportError, r);
        }

        [Fact(Timeout = 60000)]
        public async Task SendWithTimeout_HungSend_ReturnsTransportError_DoesNotBlock()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AdminSendResult r = await JanusAdminClient.SendWithTimeoutAsync(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return (HttpStatusCode.OK, "{}"); },
                TimeSpan.FromMilliseconds(50));
            sw.Stop();
            {
                var assertedValue = r;
                TestAssert.WithMessage(() => Assert.Equal(AdminSendResult.TransportError, assertedValue), "timeout -> TransportError");
            }
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "returned on the ~50ms deadline, not after the 30s hang");
        }

        // ---- absolute HttpClient.Timeout backstop -------------------------------------
        [Fact(Timeout = 60000)]
        public void ComputeClientTimeout_IsFourTimesTheAdminTimeout()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.FromSeconds(20), JanusAdminClient.ComputeClientTimeout(TimeSpan.FromSeconds(5)));
            Assert.Equal(TimeSpan.FromMilliseconds(1000), JanusAdminClient.ComputeClientTimeout(TimeSpan.FromMilliseconds(250)));
        }

        [Fact(Timeout = 60000)]
        public void ComputeClientTimeout_NonPositive_FallsBackTo20s()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.FromSeconds(20), JanusAdminClient.ComputeClientTimeout(TimeSpan.Zero));
            Assert.Equal(TimeSpan.FromSeconds(20), JanusAdminClient.ComputeClientTimeout(TimeSpan.FromSeconds(-1)));
        }

        [Fact(Timeout = 60000)]
        public void Ctor_SetsAbsoluteTimeoutOnItsOwnHttpClient()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var client = new JanusAdminClient("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5));
            {
                var assertedValue = client.ClientTimeout;
                TestAssert.WithMessage(() => Assert.Equal(TimeSpan.FromSeconds(20), assertedValue), "JanusAdminClient's own HttpClient must carry the 4x backstop timeout (not JanusSession's shared client)");
            }
        }
    }
}
