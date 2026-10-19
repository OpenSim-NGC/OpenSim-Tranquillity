/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * TLS from the sim to the mixer, driven through the real WebRtcJanusService constructor and its config.
 *
 * https:// gateway URLs work with normal certificate validation: a certificate the operating system
 * trusts is accepted and nothing else is, unless [JanusWebRtcVoice] JanusGatewayCACertificateFile names a
 * private CA certificate, which is then trusted for the mixer connection in addition. There is no switch
 * that turns validation off. An http:// URL behaves as it always did.
 *
 * Each test starts an in-process server (TlsTestServer) with a certificate made for the test. "Reached the
 * server" means the TLS handshake finished and a request arrived. The ambient logger factory is
 * process-wide, so the class runs on its own.
 */

using System.IO;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using System.Security.Cryptography.X509Certificates;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class JanusTlsTests
    {
        private const string CaKey = "JanusGatewayCACertificateFile";
        private static readonly string Token = new string('t', 40);

        private sealed class Capture : ILoggerProvider, ILogger
        {
            private readonly List<(LogLevel Level, string Text)> _lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (_lines)
                    _lines.Add((logLevel, formatter(state, exception)));
            }
            public void Dispose() { }
            public List<(LogLevel Level, string Text)> Snapshot()
            {
                lock (_lines)
                    return new List<(LogLevel, string)>(_lines);
            }
        }

        private Capture _log;
        private ILoggerFactory _previous;
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _log = new Capture();
            _previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(_log).SetMinimumLevel(LogLevel.Trace));
            _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "tls-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = _previous;
            ours.Dispose();
            _log.Dispose();
        }

        private void StartService(TlsTestServer server, string caFile)
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", server.VoiceUri);
            janus.Set("APIToken", Token);
            janus.Set("JanusGatewayAdminURI", server.AdminUri);
            janus.Set("AdminAPIToken", Token);
            janus.Set("PluginName", "janus.plugin.slvoice");
            janus.Set("RequestTimeoutMs", "2000");
            if (caFile is not null)
                janus.Set(CaKey, caFile);
            _ = new WebRtcJanusService(config);   // connects to the gateway in its constructor
        }

        private List<(LogLevel Level, string Text)> CertificateRejections() =>
            _log.Snapshot().Where(l => l.Level >= LogLevel.Warning && l.Text.Contains("certificate") && l.Text.Contains("rejected")).ToList();

        [Test]
        public void AnHttpUrlBehavesAsItAlwaysDid()
        {
            using var server = new TlsTestServer(null);

            StartService(server, caFile: null);

            Assert.That(server.Requests, Is.GreaterThan(0), "the session was created over plain http");
            Assert.That(CertificateRejections(), Is.Empty);
        }

        [Test]
        public void ASelfSignedCertificateIsRejectedWhenTheKeyIsUnsetAndTheLogSaysHowToFixIt()
        {
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", null));

            StartService(server, caFile: null);

            Assert.That(server.Requests, Is.EqualTo(0), "no request may pass a certificate nobody trusts");
            var lines = CertificateRejections();
            Assert.That(lines, Is.Not.Empty, "a clear log line says the certificate was rejected");
            Assert.That(lines[0].Text, Does.Contain(CaKey), "the line names the key that would fix a private CA");
        }

        [Test]
        public void ACertificateSignedByThePrivateCaIsAcceptedWhenTheKeyNamesTheCa()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            string caFile = TlsTestCerts.WritePem(ca, _dir, "ca.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            StartService(server, caFile);

            Assert.That(server.Requests, Is.GreaterThan(0), "the session was created over https");
            Assert.That(CertificateRejections(), Is.Empty);
        }

        [Test]
        public void TheSameCertificateIsRejectedWhenTheKeyIsUnset()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            StartService(server, caFile: null);

            Assert.That(server.Requests, Is.EqualTo(0));
            Assert.That(CertificateRejections(), Is.Not.Empty);
        }

        [Test]
        public void ASelfSignedCertificateIsAcceptedWhenTheKeyNamesItself()
        {
            X509Certificate2 cert = TlsTestCerts.NewServer("127.0.0.1", null);
            string file = TlsTestCerts.WritePem(cert, _dir, "self.pem");
            using var server = new TlsTestServer(cert);

            StartService(server, file);

            Assert.That(server.Requests, Is.GreaterThan(0), "the certificate the file names is trusted");
            Assert.That(CertificateRejections(), Is.Empty);
        }

        [Test]
        public void NamingOneCertificateDoesNotTrustAnother()
        {
            using X509Certificate2 named = TlsTestCerts.NewServer("127.0.0.1", null);
            string file = TlsTestCerts.WritePem(named, _dir, "named.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", null));

            StartService(server, file);

            Assert.That(server.Requests, Is.EqualTo(0), "only the certificate in the file is trusted");
            Assert.That(CertificateRejections(), Is.Not.Empty);
        }

        [Test]
        public void TheCaDoesNotExcuseAHostNameThatDoesNotMatch()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            string caFile = TlsTestCerts.WritePem(ca, _dir, "ca.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("mixer.example.org", ca));

            StartService(server, caFile);   // the sim connects to 127.0.0.1, which the certificate does not name

            Assert.That(server.Requests, Is.EqualTo(0), "a trusted CA vouches for the issuer, not for the wrong host");
            Assert.That(CertificateRejections(), Is.Not.Empty);
        }

        [Test]
        public void ACaFileThatCannotBeReadIsReportedAndTrustsNothing()
        {
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            StartService(server, Path.Combine(_dir, "missing.pem"));

            Assert.That(_log.Snapshot().Any(l => l.Level == LogLevel.Error && l.Text.Contains(CaKey)), Is.True,
                "an error names the key whose file could not be used");
            Assert.That(server.Requests, Is.EqualTo(0), "the connection is still validated against the system store only");
        }

        [Test]
        public void NothingInTheAddonCanSwitchCertificateValidationOff()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Addons")))
                dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "could not find the repository root from " + AppContext.BaseDirectory);

            string addon = Path.Combine(dir.FullName, "Addons", "os-webrtc-janus");
            var sources = Directory.EnumerateFiles(addon, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToList();

            Assert.That(sources.Where(f => File.ReadAllText(f).Contains("DangerousAcceptAnyServerCertificateValidator")), Is.Empty,
                "no handler may accept every certificate");
            Assert.That(sources.Where(f => File.ReadAllText(f).Contains("ServerCertificateCustomValidationCallback")), Is.Empty,
                "certificate validation is customised in JanusTls only, through SslOptions");
            Assert.That(sources.Where(f => File.ReadAllText(f).Contains("RemoteCertificateValidationCallback"))
                               .Select(Path.GetFileName), Is.EqualTo(new[] { "JanusTls.cs" }),
                "the one validation callback lives in JanusTls.cs");
        }
    }
}
