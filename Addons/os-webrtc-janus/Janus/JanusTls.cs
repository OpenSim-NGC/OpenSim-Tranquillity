/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice;

/// <summary>
/// The HTTP handler the sim uses to reach the Janus gateway and its admin API, so that an
/// <c>https://</c> URL is validated like any other TLS connection: the server's certificate must chain to a
/// root the operating system trusts and must name the host the sim connected to. There is no setting that
/// turns that check off.
///
/// An operator whose mixer uses a certificate from a private CA names that CA's certificate file in
/// <c>[JanusWebRtcVoice] JanusGatewayCACertificateFile</c>. The CA is trusted for the mixer connection in
/// addition to the system roots; the host name must still match, and it vouches for nothing else the
/// region server connects to. An <c>http://</c> URL never reaches this check.
/// </summary>
public static class JanusTls
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(JanusTls));
    private static readonly string LogHeader = "[JANUS TLS]";

    /// <summary>The ini key (in <c>[JanusWebRtcVoice]</c>) that names the private CA certificate file.</summary>
    public const string CaCertificateFileKey = "JanusGatewayCACertificateFile";

    /// <summary>
    /// Builds the handler for one HttpClient. <paramref name="caCertificateFile"/> is the configured
    /// private CA certificate file (PEM or DER, one or several certificates), or null or empty for none.
    /// A file that cannot be used is reported once at error level and ignored, so the connection is then
    /// validated against the system roots alone.
    /// </summary>
    public static HttpMessageHandler CreateHandler(string caCertificateFile)
    {
        X509Certificate2Collection extraRoots = LoadCaCertificates(caCertificateFile);
        var validator = new Validator(extraRoots);
        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = validator.Validate
            }
        };
    }

    private static X509Certificate2Collection LoadCaCertificates(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return null;
        try
        {
            var certs = new X509Certificate2Collection();
            byte[] bytes = File.ReadAllBytes(file);
            string text = System.Text.Encoding.ASCII.GetString(bytes);
            if (text.Contains("-----BEGIN CERTIFICATE-----"))
                certs.ImportFromPem(text);
            else
                certs.Add(X509CertificateLoader.LoadCertificate(bytes));
            if (certs.Count == 0)
                throw new InvalidDataException("no certificate found in the file");
            return certs;
        }
        catch (Exception e)
        {
            m_log.LogError("{0} [JanusWebRtcVoice] {1} could not be used ({2}); only the system's trusted roots will be used for the mixer connection",
                LogHeader, CaCertificateFileKey, e.Message);
            return null;
        }
    }

    private sealed class Validator
    {
        private readonly X509Certificate2Collection _extraRoots;
        private readonly ConcurrentDictionary<string, bool> _reported = new();

        public Validator(X509Certificate2Collection extraRoots)
        {
            _extraRoots = extraRoots;
        }

        public bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
                return true;   // trusted by the system and the name matches

            // A private CA is trusted only to vouch for the chain. A name that does not match, or no
            // certificate at all, is refused whatever the CA.
            if (_extraRoots is not null && certificate is not null
                && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0
                && ChainsToExtraRoots(certificate, chain))
                return true;

            Report(sender, certificate, errors);
            return false;
        }

        private bool ChainsToExtraRoots(X509Certificate certificate, X509Chain presented)
        {
            using var leaf = new X509Certificate2(certificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(_extraRoots);
            // A private CA publishes no revocation list the sim could reach.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (presented is not null)
                foreach (X509ChainElement element in presented.ChainElements)
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            return chain.Build(leaf);
        }

        private void Report(object sender, X509Certificate certificate, SslPolicyErrors errors)
        {
            string host = (sender as SslStream)?.TargetHostName ?? "the gateway";
            string subject = certificate?.Subject ?? "(none)";
            if (!_reported.TryAdd(host + "|" + subject + "|" + errors, true))
                return;
            m_log.LogError("{0} the certificate presented by {1} ({2}) was rejected: {3}. " +
                "If the mixer's certificate comes from a private CA, set [JanusWebRtcVoice] {4} to that CA's certificate file.",
                LogHeader, host, subject, errors, CaCertificateFileKey);
        }
    }
}
