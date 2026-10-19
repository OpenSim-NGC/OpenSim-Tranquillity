/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A minimal in-process stand-in for the Janus HTTP and admin endpoints, plain or over TLS, plus the
 * certificates the TLS tests need. It speaks just enough HTTP/1.1 for the sim's clients: a POST is
 * answered with a Janus "success" carrying the request's transaction (a "create" also gets a session
 * id), and a GET (the session long poll) is held open until the server is disposed.
 */

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public static class TlsTestCerts
    {
        /// <summary>A self-signed CA certificate (with its key) that can sign server certificates.</summary>
        public static X509Certificate2 NewCa(string name)
        {
            using RSA key = RSA.Create(2048);
            var req = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        }

        /// <summary>A server certificate for <paramref name="host"/> (an IP address or a DNS name), signed by
        /// <paramref name="ca"/>, or self-signed (and not a CA) when <paramref name="ca"/> is null.</summary>
        public static X509Certificate2 NewServer(string host, X509Certificate2 ca)
        {
            using RSA key = RSA.Create(2048);
            var req = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder();
            if (IPAddress.TryParse(host, out IPAddress ip))
                san.AddIpAddress(ip);
            else
                san.AddDnsName(host);
            req.CertificateExtensions.Add(san.Build());

            DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1), to = DateTimeOffset.UtcNow.AddDays(30);
            X509Certificate2 cert;
            if (ca is null)
            {
                cert = req.CreateSelfSigned(from, to);
            }
            else
            {
                // inside the issuer's validity, whatever the clock rounding
                from = ca.NotBefore; to = ca.NotAfter.AddDays(-1);
                byte[] serial = new byte[8];
                RandomNumberGenerator.Fill(serial);
                using X509Certificate2 signed = req.Create(ca, from, to, serial);
                cert = signed.CopyWithPrivateKey(key);
            }
            using (cert)
                return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        }

        /// <summary>Write the public part of a certificate as a PEM file and return its path.</summary>
        public static string WritePem(X509Certificate2 cert, string directory, string fileName)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, cert.ExportCertificatePem());
            return path;
        }
    }

    public sealed class TlsTestServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        /// <summary>Starts on a free loopback port; a null certificate serves plain HTTP.</summary>
        public TlsTestServer(X509Certificate2 cert)
        {
            _cert = cert;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Scheme => _cert is null ? "http" : "https";
        public string VoiceUri => $"{Scheme}://127.0.0.1:{Port}/voice";
        public string AdminUri => $"{Scheme}://127.0.0.1:{Port}/admin";

        /// <summary>Requests (POSTs) that reached the server with the TLS handshake complete.</summary>
        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptLoop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch (Exception)
            {
                // disposed
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    Stream stream = client.GetStream();
                    if (_cert is not null)
                    {
                        var ssl = new SslStream(stream);
                        await ssl.AuthenticateAsServerAsync(_cert);
                        stream = ssl;
                    }
                    while (!_stop.IsCancellationRequested)
                    {
                        (string method, string body) = await ReadRequest(stream);
                        if (method is null)
                            return;
                        if (method == "GET")
                        {
                            await Task.Delay(Timeout.Infinite, _stop.Token);   // the long poll waits for an event
                            return;
                        }
                        Interlocked.Increment(ref _requests);
                        await Respond(stream, Answer(body));
                    }
                }
                catch (Exception)
                {
                    // a rejected handshake, a closed connection, or disposal: nothing to report
                }
            }
        }

        private static string Answer(string body)
        {
            var msg = OSDParser.DeserializeJson(body) as OSDMap;
            string janus = msg?["janus"].AsString();
            string txn = msg?["transaction"].AsString() ?? string.Empty;
            return janus == "create" || janus == "attach"
                ? "{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":4242}}"
                : "{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}";
        }

        private static async Task<(string Method, string Body)> ReadRequest(Stream stream)
        {
            var head = new StringBuilder();
            byte[] one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n"))
            {
                if (await stream.ReadAsync(one) == 0)
                    return (null, null);
                head.Append((char)one[0]);
            }
            string[] lines = head.ToString().Split("\r\n");
            string method = lines[0].Split(' ')[0];
            int length = 0;
            foreach (string line in lines)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.Substring("Content-Length:".Length).Trim());
            byte[] body = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(read));
                if (n == 0)
                    return (null, null);
                read += n;
            }
            return (method, Encoding.UTF8.GetString(body));
        }

        private static async Task Respond(Stream stream, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _cert?.Dispose();
        }
    }
}
