/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// The single, shared signature-envelope canonicaliser (Design Brief §5). BOTH the signer and
/// the verifier build the signed bytes through <see cref="BuildCanonicalPayload"/> and derive the
/// parameter digest through <see cref="ParametersDigest"/>. Nothing else may format the payload;
/// a second copy of this logic is exactly the drift the byte-identical done-when test guards against.
///
/// Signed payload = LF-joined canonical concatenation of:
///   method name, sender key fingerprint (SHA-256 hex of the public key), UTC timestamp
///   (ISO-8601, seconds), nonce (base64 of 16 random bytes), SHA-256 hex digest of the method's
///   own parameters in canonical form.
///
/// Any URI that appears as a parameter must be normalised by the caller through
/// <see cref="HGUriNormalizer"/> before signing, so signer and verifier agree byte-for-byte.
/// </summary>
public static class HGSignatureEnvelope
{
    /// <summary>Number of random bytes in a nonce (Design Brief §5).</summary>
    public const int NonceByteLength = 16;

    /// <summary>ISO-8601, seconds precision, UTC. e.g. 2026-08-20T13:30:00Z.</summary>
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>Replay window for the timestamp: ±300 seconds (Design Brief §5).</summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(300);

    /// <summary>Nonce-cache retention window: 600 seconds (Design Brief §5).</summary>
    public static readonly TimeSpan NonceWindow = TimeSpan.FromSeconds(600);

    private const char FieldSeparator = '\n';

    /// <summary>
    /// Build the exact bytes that are Ed25519-signed. Identical inputs yield identical bytes
    /// on both the signer and the verifier side.
    /// </summary>
    public static byte[] BuildCanonicalPayload(
        string method, string keyFingerprint, string isoTimestamp, string nonceBase64, string parametersDigest)
    {
        var sb = new StringBuilder(256);
        sb.Append(method ?? string.Empty).Append(FieldSeparator);
        sb.Append(keyFingerprint ?? string.Empty).Append(FieldSeparator);
        sb.Append(isoTimestamp ?? string.Empty).Append(FieldSeparator);
        sb.Append(nonceBase64 ?? string.Empty).Append(FieldSeparator);
        sb.Append(parametersDigest ?? string.Empty);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// SHA-256 hex digest (lowercase) of the method's parameters in canonical form: entries sorted
    /// by ordinal key, each rendered as <c>key=value</c>, LF-joined. The transport signature keys
    /// (<c>tg_*</c> / <c>X-TG-*</c>) are excluded so the digest is stable whether computed before
    /// signing or after the material has been attached. A null/empty parameter set digests the
    /// empty string.
    /// </summary>
    public static string ParametersDigest(IDictionary parameters) => ParametersDigest(parameters, null);

    /// <summary>
    /// As <see cref="ParametersDigest(IDictionary)"/>, additionally folding the sender's advisory
    /// URI (<c>tg_uri</c> / <c>X-TG-Uri</c>, LEDGER D-5) into the digest as the entry
    /// <c>tg_uri=&lt;uri&gt;</c>. The URI is taken from <paramref name="senderUri"/> — the signer's
    /// own <c>HomeUri</c>, or the value the verifier extracted from the transport — never from the
    /// raw parameter set, so it is digested exactly once whichever transport carried it. A
    /// rewritten <c>tg_uri</c> therefore fails verification (R-2). When
    /// <paramref name="senderUri"/> is null/empty nothing is added and the digest is
    /// byte-identical to the Slice 2 form, so unsigned, stock and Slice 2 callers are unaffected.
    /// </summary>
    public static string ParametersDigest(IDictionary parameters, string senderUri)
    {
        var pairs = new List<string>();
        if (parameters != null)
        {
            foreach (DictionaryEntry e in parameters)
            {
                string key = e.Key?.ToString() ?? string.Empty;
                if (IsSignatureField(key))
                    continue;
                string value = e.Value?.ToString() ?? string.Empty;
                pairs.Add(key + "=" + value);
            }
        }

        if (!string.IsNullOrEmpty(senderUri))
            pairs.Add(SignatureMaterial.XmlRpcUri + "=" + senderUri);

        pairs.Sort(StringComparer.Ordinal);
        string canonical = string.Join(FieldSeparator.ToString(), pairs);
        return Sha256Hex(Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>Lowercase SHA-256 hex of raw bytes (used for the public-key fingerprint too).</summary>
    public static string Sha256Hex(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static string FormatTimestamp(DateTime utc)
    {
        return utc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }

    public static bool TryParseTimestamp(string s, out DateTime utc)
    {
        return DateTime.TryParseExact(
            s, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    /// <summary>True for a transport signature field name (<c>tg_*</c> or <c>x-tg-*</c>).</summary>
    public static bool IsSignatureField(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        return key.StartsWith("tg_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("x-tg-", StringComparison.OrdinalIgnoreCase);
    }
}
