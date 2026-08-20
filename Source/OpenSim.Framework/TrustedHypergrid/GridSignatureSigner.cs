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
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// Produces the outbound signature material (Design Brief §5) for a call, using the local grid's
/// Ed25519 keypair. Uses <see cref="HGSignatureEnvelope"/> for canonicalisation — the same path
/// the verifier uses — so the two never drift.
/// </summary>
public sealed class GridSignatureSigner
{
    private readonly GridKeypair m_keypair;

    public GridSignatureSigner(GridKeypair keypair)
    {
        m_keypair = keypair ?? throw new ArgumentNullException(nameof(keypair));
    }

    /// <summary>
    /// Sign a call and return the transport material. <paramref name="parameters"/> are the
    /// method's own parameters (the XML-RPC param Hashtable, or null for a params-less HTTP call);
    /// the transport signature fields, if already present, are excluded from the digest.
    /// </summary>
    public SignatureMaterial Sign(string method, IDictionary parameters, DateTime nowUtc)
    {
        string timestamp = HGSignatureEnvelope.FormatTimestamp(nowUtc);
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(HGSignatureEnvelope.NonceByteLength));
        string paramsDigest = HGSignatureEnvelope.ParametersDigest(parameters);

        byte[] payload = HGSignatureEnvelope.BuildCanonicalPayload(
            method, m_keypair.Fingerprint, timestamp, nonce, paramsDigest);

        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(m_keypair.PrivateKey, 0));
        signer.BlockUpdate(payload, 0, payload.Length);
        byte[] signature = signer.GenerateSignature();

        return new SignatureMaterial
        {
            Key = Convert.ToBase64String(m_keypair.PublicKey),
            Timestamp = timestamp,
            Nonce = nonce,
            Signature = Convert.ToBase64String(signature),
        };
    }

    /// <summary>Sign and attach the four <c>tg_*</c> keys to an XML-RPC param Hashtable in place.</summary>
    public void SignInto(Hashtable parameters, string method, DateTime nowUtc)
    {
        Sign(method, parameters, nowUtc).WriteTo(parameters);
    }
}
