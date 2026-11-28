/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * JanusTls.CreateHandler with the session client (gateway and admin POSTs). The service-level cases are in
 * JanusTlsTests; these cover the session client over https and a CA given as a DER file or a bundle.
 */

using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    public class JanusTlsClientTests : IAsyncLifetime
        {
        private string _dir;
        private void SetUp()
        {
            _dir = Path.Combine(Directory.GetCurrentDirectory(), "tls-client-" + Guid.NewGuid().ToString("N"));
        }

        private static async Task<bool> SessionReaches(TlsTestServer server, string caFile)
        {
            using var session = new JanusSession(server.VoiceUri, "api-secret", server.AdminUri, "admin-secret", false,
                                                 JanusTls.CreateHandler(caFile));
            bool created = await session.CreateSession();
            if (created)
                await session.DestroySession();
            return created;
        }

        private static async Task<AdminSendResult> SendAdminMessage(TlsTestServer server, string caFile)
        {
            using var client = new JanusAdminClient(server.AdminUri, "admin-secret", TimeSpan.FromSeconds(5), JanusTls.CreateHandler(caFile));
            return await client.SendPluginMessageAsync("janus.plugin.slvoice", new OSDMap { ["request"] = new OSDString("ping") });
        }

        [Fact(Timeout = 60000)]
        public async Task TheAdminClientReachesAnHttpsAdminApiWhenTheKeyNamesTheCa()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            string caFile = TlsTestCerts.WritePem(ca, _dir, "ca.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.Equal(AdminSendResult.Ok, await SendAdminMessage(server, caFile));
            Assert.Equal(1, server.Requests);
        }

        [Fact(Timeout = 60000)]
        public async Task TheAdminClientRefusesAnHttpsAdminApiWhoseCertificateNobodyTrusts()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.Equal(AdminSendResult.TransportError, await SendAdminMessage(server, caFile: null));
            {
                var assertedValue = server.Requests;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no admin secret was sent to an untrusted server");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task TheAdminClientStillWorksOverPlainHttp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var server = new TlsTestServer(null);

            Assert.Equal(AdminSendResult.Ok, await SendAdminMessage(server, caFile: null));
        }

        [Fact(Timeout = 60000)]
        public async Task TheSessionClientRefusesAnHttpsGatewayWhoseCertificateNobodyTrusts()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.False(await SessionReaches(server, caFile: null));
            {
                var assertedValue = server.Requests;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no API secret was sent to an untrusted server");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task TheSessionClientStillWorksOverPlainHttp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var server = new TlsTestServer(null);

            Assert.True(await SessionReaches(server, caFile: null));
        }

        [Fact(Timeout = 60000)]
        public async Task ACaGivenAsADerFileIsTrusted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            Directory.CreateDirectory(_dir);
            string derFile = Path.Combine(_dir, "ca.der");
            File.WriteAllBytes(derFile, ca.Export(X509ContentType.Cert));
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.True(await SessionReaches(server, derFile));
        }

        [Fact(Timeout = 60000)]
        public async Task ACaInsideABundleOfSeveralCertificatesIsTrusted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 other = TlsTestCerts.NewCa("Example Other CA");
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            Directory.CreateDirectory(_dir);
            string bundle = Path.Combine(_dir, "bundle.pem");
            File.WriteAllText(bundle, other.ExportCertificatePem() + "\n" + ca.ExportCertificatePem());
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.True(await SessionReaches(server, bundle));
        }

        [Fact(Timeout = 60000)]
        public async Task TheSessionClientReachesAnHttpsGatewayWhenTheKeyNamesTheCa()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            string caFile = TlsTestCerts.WritePem(ca, _dir, "ca.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));
            using var session = new JanusSession(server.VoiceUri, "api-secret", server.AdminUri, "admin-secret", false,
                                                 JanusTls.CreateHandler(caFile));

            Assert.True((await session.CreateSession()), "the gateway API answered over https");
            var admin = await session.SendToJanusAdmin(new JanusMessageReq("info"));
            {
                var assertedValue = admin.ReturnCode;
                TestAssert.WithMessage(() => Assert.Equal("success", assertedValue), "the admin API answered over https");
            }
            await session.DestroySession();
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
