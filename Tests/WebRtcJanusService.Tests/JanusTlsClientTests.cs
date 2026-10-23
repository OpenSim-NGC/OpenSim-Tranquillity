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
    [TestFixture]
    [NonParallelizable]
    public class JanusTlsClientTests
    {
        private string _dir;
        private ILoggerFactory _previous;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "tls-client-" + Guid.NewGuid().ToString("N"));
            _previous = LoggerProvider.LoggerFactory;
        }

        [TearDown]
        public void TearDown() => LoggerProvider.LoggerFactory = _previous;

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

        [Test]
        public async Task TheAdminClientReachesAnHttpsAdminApiWhenTheKeyNamesTheCa()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            string caFile = TlsTestCerts.WritePem(ca, _dir, "ca.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.That(await SendAdminMessage(server, caFile), Is.EqualTo(AdminSendResult.Ok));
            Assert.That(server.Requests, Is.EqualTo(1));
        }

        [Test]
        public async Task TheAdminClientRefusesAnHttpsAdminApiWhoseCertificateNobodyTrusts()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.That(await SendAdminMessage(server, caFile: null), Is.EqualTo(AdminSendResult.TransportError));
            Assert.That(server.Requests, Is.EqualTo(0), "no admin secret was sent to an untrusted server");
        }

        [Test]
        public async Task TheAdminClientStillWorksOverPlainHttp()
        {
            using var server = new TlsTestServer(null);

            Assert.That(await SendAdminMessage(server, caFile: null), Is.EqualTo(AdminSendResult.Ok));
        }

        [Test]
        public async Task TheSessionClientRefusesAnHttpsGatewayWhoseCertificateNobodyTrusts()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.That(await SessionReaches(server, caFile: null), Is.False);
            Assert.That(server.Requests, Is.EqualTo(0), "no API secret was sent to an untrusted server");
        }

        [Test]
        public async Task TheSessionClientStillWorksOverPlainHttp()
        {
            using var server = new TlsTestServer(null);

            Assert.That(await SessionReaches(server, caFile: null), Is.True);
        }

        [Test]
        public async Task ACaGivenAsADerFileIsTrusted()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            Directory.CreateDirectory(_dir);
            string derFile = Path.Combine(_dir, "ca.der");
            File.WriteAllBytes(derFile, ca.Export(X509ContentType.Cert));
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.That(await SessionReaches(server, derFile), Is.True);
        }

        [Test]
        public async Task ACaInsideABundleOfSeveralCertificatesIsTrusted()
        {
            using X509Certificate2 other = TlsTestCerts.NewCa("Example Other CA");
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            Directory.CreateDirectory(_dir);
            string bundle = Path.Combine(_dir, "bundle.pem");
            File.WriteAllText(bundle, other.ExportCertificatePem() + "\n" + ca.ExportCertificatePem());
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            Assert.That(await SessionReaches(server, bundle), Is.True);
        }

        [Test]
        public async Task TheSessionClientReachesAnHttpsGatewayWhenTheKeyNamesTheCa()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            string caFile = TlsTestCerts.WritePem(ca, _dir, "ca.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));
            using var session = new JanusSession(server.VoiceUri, "api-secret", server.AdminUri, "admin-secret", false,
                                                 JanusTls.CreateHandler(caFile));

            Assert.That(await session.CreateSession(), Is.True, "the gateway API answered over https");
            var admin = await session.SendToJanusAdmin(new JanusMessageReq("info"));
            Assert.That(admin.ReturnCode, Is.EqualTo("success"), "the admin API answered over https");
            await session.DestroySession();
        }
    }
}
