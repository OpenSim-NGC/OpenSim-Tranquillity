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
    public class JanusTlsTests : IAsyncLifetime
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
        private LogCapture _logging;
        private string _dir;
        private void SetUp()
        {
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
            _dir = Path.Combine(Directory.GetCurrentDirectory(), "tls-" + Guid.NewGuid().ToString("N"));
        }
        private void TearDown()
        {
            _logging.Dispose();
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

        [Fact(Timeout = 60000)]
        public void AnHttpUrlBehavesAsItAlwaysDid()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var server = new TlsTestServer(null);

            StartService(server, caFile: null);

            Assert.True(server.Requests > 0, "the session was created over plain http");
            Assert.Empty(CertificateRejections());
        }

        [Fact(Timeout = 60000)]
        public void ASelfSignedCertificateIsRejectedWhenTheKeyIsUnsetAndTheLogSaysHowToFixIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", null));

            StartService(server, caFile: null);

            {
                var assertedValue = server.Requests;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no request may pass a certificate nobody trusts");
            }
            var lines = CertificateRejections();
            {
                var assertedValue = lines;
                TestAssert.WithMessage(() => Assert.NotEmpty(assertedValue), "a clear log line says the certificate was rejected");
            }
            {
                var assertedValue = lines[0].Text;
                TestAssert.WithMessage(() => Assert.Contains(CaKey, assertedValue), "the line names the key that would fix a private CA");
            }
        }

        [Fact(Timeout = 60000)]
        public void ACertificateSignedByThePrivateCaIsAcceptedWhenTheKeyNamesTheCa()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            string caFile = TlsTestCerts.WritePem(ca, _dir, "ca.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            StartService(server, caFile);

            Assert.True(server.Requests > 0, "the session was created over https");
            Assert.Empty(CertificateRejections());
        }

        [Fact(Timeout = 60000)]
        public void TheSameCertificateIsRejectedWhenTheKeyIsUnset()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            StartService(server, caFile: null);

            Assert.Equal(0, server.Requests);
            Assert.NotEmpty(CertificateRejections());
        }

        [Fact(Timeout = 60000)]
        public void ASelfSignedCertificateIsAcceptedWhenTheKeyNamesItself()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            X509Certificate2 cert = TlsTestCerts.NewServer("127.0.0.1", null);
            string file = TlsTestCerts.WritePem(cert, _dir, "self.pem");
            using var server = new TlsTestServer(cert);

            StartService(server, file);

            Assert.True(server.Requests > 0, "the certificate the file names is trusted");
            Assert.Empty(CertificateRejections());
        }

        [Fact(Timeout = 60000)]
        public void NamingOneCertificateDoesNotTrustAnother()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 named = TlsTestCerts.NewServer("127.0.0.1", null);
            string file = TlsTestCerts.WritePem(named, _dir, "named.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", null));

            StartService(server, file);

            {
                var assertedValue = server.Requests;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "only the certificate in the file is trusted");
            }
            Assert.NotEmpty(CertificateRejections());
        }

        [Fact(Timeout = 60000)]
        public void TheCaDoesNotExcuseAHostNameThatDoesNotMatch()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            string caFile = TlsTestCerts.WritePem(ca, _dir, "ca.pem");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("mixer.example.org", ca));

            StartService(server, caFile);   // the sim connects to 127.0.0.1, which the certificate does not name

            {
                var assertedValue = server.Requests;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "a trusted CA vouches for the issuer, not for the wrong host");
            }
            Assert.NotEmpty(CertificateRejections());
        }

        [Fact(Timeout = 60000)]
        public void ACaFileThatCannotBeReadIsReportedAndTrustsNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using X509Certificate2 ca = TlsTestCerts.NewCa("Example Private CA");
            using var server = new TlsTestServer(TlsTestCerts.NewServer("127.0.0.1", ca));

            StartService(server, Path.Combine(_dir, "missing.pem"));

            Assert.True(_log.Snapshot().Any(l => l.Level == LogLevel.Error && l.Text.Contains(CaKey)), "an error names the key whose file could not be used");
            {
                var assertedValue = server.Requests;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the connection is still validated against the system store only");
            }
        }

        [Fact(Timeout = 60000)]
        public void NothingInTheAddonCanSwitchCertificateValidationOff()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Addons")))
                dir = dir.Parent;
            {
                var assertedValue = dir;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "could not find the repository root from " + AppContext.BaseDirectory);
            }

            string addon = Path.Combine(dir.FullName, "Addons", "os-webrtc-janus");
            var sources = Directory.EnumerateFiles(addon, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToList();

            {
                var assertedValue = sources.Where(f => File.ReadAllText(f).Contains("DangerousAcceptAnyServerCertificateValidator"));
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no handler may accept every certificate");
            }
            {
                var assertedValue = sources.Where(f => File.ReadAllText(f).Contains("ServerCertificateCustomValidationCallback"));
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "certificate validation is customised in JanusTls only, through SslOptions");
            }
            {
                var assertedValue = sources.Where(f => File.ReadAllText(f).Contains("RemoteCertificateValidationCallback")).Select(Path.GetFileName);
                TestAssert.WithMessage(() => Assert.Equal(new[] { "JanusTls.cs" }, assertedValue), "the one validation callback lives in JanusTls.cs");
            }
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            TearDown();
            return ValueTask.CompletedTask;
        }
    }
}
