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
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using OpenSim.Framework.TrustedHypergrid;

namespace OpenSim.Framework.ServiceAuth;

/// <summary>
/// The Trusted Hypergrid <see cref="IServiceAuth"/> (Design Brief D5, §6; ADR-005). This is the
/// ONLY component that may reject, and it rejects ONLY a Blocked-tier caller. Verification itself
/// is done separately by <see cref="GridSignatureVerifier"/>, which never rejects; this reads the
/// resulting <see cref="GridTrustContext"/> and applies the single hard-fail rule.
///
/// Because <see cref="IServiceAuth.Authenticate(NameValueCollection, AddHeaderDelegate, out HttpStatusCode)"/>
/// returns bool where false is an HTTP rejection, it MUST NOT be used to express
/// "unverified → Open": unsigned, malformed, unknown, replayed and expired requests all return
/// TRUE here. In Slice 2 no grid can be Blocked, so this always returns true — byte-identical
/// behaviour to today for every caller.
/// </summary>
public class TrustedGridAuthentication : IServiceAuth
{
    public string Name { get { return "TrustedGrid"; } }

    /// <summary>Optional outbound signer. Null until the local keypair is operationalised, in
    /// which case <see cref="AddAuthorization(NameValueCollection)"/> is a no-op and outbound
    /// behaviour is unchanged.</summary>
    private readonly GridSignatureSigner m_signer;

    public TrustedGridAuthentication() : this((GridSignatureSigner)null) { }

    public TrustedGridAuthentication(GridSignatureSigner signer)
    {
        m_signer = signer;
    }

    public bool Authenticate(string data)
    {
        // The string form carries no trust context; it is never the Blocked-rejection path.
        return true;
    }

    public bool Authenticate(NameValueCollection requestHeaders, AddHeaderDelegate d, out HttpStatusCode statusCode)
    {
        GridTrustContext ctx = GridTrustContext.Current;

        // Absence of a context means Open; only an explicit Blocked tier is refused.
        if (ctx != null && ctx.Tier == GridTrustContext.TierBlocked)
        {
            statusCode = HttpStatusCode.Forbidden;
            return false;
        }

        statusCode = HttpStatusCode.OK;
        return true;
    }

    public void AddAuthorization(NameValueCollection headers)
    {
        if (m_signer == null || headers == null)
            return;

        // No request body/params are visible at this layer, so bind a freshness envelope
        // (key possession + timestamp + nonce). Full param binding rides the XML-RPC transport.
        m_signer.Sign(string.Empty, null, DateTime.UtcNow).WriteTo(headers);
    }

    public void AddAuthorization(HttpRequestHeaders headers)
    {
        if (m_signer == null || headers == null)
            return;

        SignatureMaterial m = m_signer.Sign(string.Empty, null, DateTime.UtcNow);
        headers.TryAddWithoutValidation(SignatureMaterial.HeaderKey, m.Key);
        headers.TryAddWithoutValidation(SignatureMaterial.HeaderTimestamp, m.Timestamp);
        headers.TryAddWithoutValidation(SignatureMaterial.HeaderNonce, m.Nonce);
        headers.TryAddWithoutValidation(SignatureMaterial.HeaderSignature, m.Signature);
    }
}
