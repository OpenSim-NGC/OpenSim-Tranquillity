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

using System.Collections;
using System.Collections.Specialized;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// The four signature values carried on the wire (Design Brief §5). <see cref="Key"/> is the
/// base64 Ed25519 public key of the sender (ADR-003: the public key, not the URI, is identity);
/// the receiver derives the fingerprint from it.
///
/// Two transports carry these:
///   * XML-RPC (Gatekeeper, UserAgent, HGFriends): well-known keys in the param Hashtable —
///     <c>tg_key</c>, <c>tg_ts</c>, <c>tg_nonce</c>, <c>tg_sig</c>.
///   * HTTP (HGAssetService, HGInventoryService): headers <c>X-TG-Key</c>, <c>X-TG-Timestamp</c>,
///     <c>X-TG-Nonce</c>, <c>X-TG-Signature</c>.
///
/// A stock grid never sends these; extraction then yields a material that is not
/// <see cref="HasAll"/>, which the verifier classifies as Open with no failure path.
/// </summary>
public sealed class SignatureMaterial
{
    // XML-RPC param Hashtable keys.
    public const string XmlRpcKey = "tg_key";
    public const string XmlRpcTimestamp = "tg_ts";
    public const string XmlRpcNonce = "tg_nonce";
    public const string XmlRpcSignature = "tg_sig";
    /// <summary>
    /// Fifth key (Slice 3, LEDGER D-5): the sender's own gatekeeper URI, so first contact can be
    /// recorded against a URI (§3). It is a label the sender claims, not an identity (identity is
    /// the key, ADR-003) — but it IS signed: signer and verifier both fold it into the parameter
    /// digest as <c>tg_uri=&lt;uri&gt;</c>, so a value rewritten on the wire fails verification
    /// (R-2). Absent → nothing is digested and the Slice 2 form is unchanged.
    /// </summary>
    public const string XmlRpcUri = "tg_uri";

    // HTTP header names.
    public const string HeaderKey = "X-TG-Key";
    public const string HeaderTimestamp = "X-TG-Timestamp";
    public const string HeaderNonce = "X-TG-Nonce";
    public const string HeaderSignature = "X-TG-Signature";
    public const string HeaderUri = "X-TG-Uri";

    /// <summary>Base64 Ed25519 public key (32 bytes) of the sender.</summary>
    public string Key { get; set; }

    /// <summary>
    /// The sender's claimed gatekeeper URI (advisory, unsigned); null/empty when not sent. Never
    /// required: <see cref="HasAll"/> ignores it.
    /// </summary>
    public string Uri { get; set; }

    /// <summary>UTC timestamp, ISO-8601 seconds.</summary>
    public string Timestamp { get; set; }

    /// <summary>Base64 nonce (16 random bytes).</summary>
    public string Nonce { get; set; }

    /// <summary>Base64 Ed25519 signature over the canonical payload.</summary>
    public string Signature { get; set; }

    /// <summary>All four values present. Absence means "no signature material" → Open tier.</summary>
    public bool HasAll =>
        !string.IsNullOrEmpty(Key) &&
        !string.IsNullOrEmpty(Timestamp) &&
        !string.IsNullOrEmpty(Nonce) &&
        !string.IsNullOrEmpty(Signature);

    // ---- XML-RPC transport (param Hashtable) -------------------------------

    public static SignatureMaterial FromHashtable(Hashtable h)
    {
        if (h == null)
            return null;
        return new SignatureMaterial
        {
            Key = h[XmlRpcKey] as string,
            Timestamp = h[XmlRpcTimestamp] as string,
            Nonce = h[XmlRpcNonce] as string,
            Signature = h[XmlRpcSignature] as string,
            Uri = h[XmlRpcUri] as string,
        };
    }

    public void WriteTo(Hashtable h)
    {
        h[XmlRpcKey] = Key;
        h[XmlRpcTimestamp] = Timestamp;
        h[XmlRpcNonce] = Nonce;
        h[XmlRpcSignature] = Signature;
        if (!string.IsNullOrEmpty(Uri))
            h[XmlRpcUri] = Uri;        // absent when unknown, so the request stays byte-identical to Slice 2
    }

    // ---- HTTP transport (headers) ------------------------------------------

    public static SignatureMaterial FromHeaders(NameValueCollection headers)
    {
        if (headers == null)
            return null;
        return new SignatureMaterial
        {
            Key = headers[HeaderKey],
            Timestamp = headers[HeaderTimestamp],
            Nonce = headers[HeaderNonce],
            Signature = headers[HeaderSignature],
            Uri = headers[HeaderUri],
        };
    }

    public void WriteTo(NameValueCollection headers)
    {
        headers[HeaderKey] = Key;
        headers[HeaderTimestamp] = Timestamp;
        headers[HeaderNonce] = Nonce;
        headers[HeaderSignature] = Signature;
        if (!string.IsNullOrEmpty(Uri))
            headers[HeaderUri] = Uri;
    }
}
