/*
 * Copyright (c) 2026 Legion Builds
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
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// Classifies an inbound HG request into a <see cref="GridTrustContext"/> (Design Brief §6,
/// ADR-005). This is NOT an <c>IServiceAuth</c> and it NEVER rejects and NEVER throws: every
/// failure path — no material, malformed, unknown key, bad signature, stale timestamp, replayed
/// nonce — degrades to Open and is logged. A verified caller is resolved to its registry tier;
/// an unknown-but-verified caller is Open (§3: first contact is Open/pending).
/// </summary>
public sealed class GridSignatureVerifier
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    // The caller chooses what it sends, so each way a call can fail writes one line a minute and counts the rest.
    private static readonly RateLimitedLog s_malformedLog = new();
    private static readonly RateLimitedLog s_keyLengthLog = new();
    private static readonly RateLimitedLog s_timestampLog = new();
    private static readonly RateLimitedLog s_skewLog = new();
    private static readonly RateLimitedLog s_signatureLog = new();
    private static readonly RateLimitedLog s_replayLog = new();
    private static readonly RateLimitedLog s_faultLog = new();

    private readonly IGridTrustLookup m_lookup;
    private readonly NonceCache m_nonces;

    public GridSignatureVerifier(IGridTrustLookup lookup) : this(lookup, new NonceCache()) { }

    public GridSignatureVerifier(IGridTrustLookup lookup, NonceCache nonces)
    {
        m_lookup = lookup;
        m_nonces = nonces ?? new NonceCache();
    }

    /// <summary>
    /// Verify the material against the method and its parameters at <paramref name="nowUtc"/>.
    /// Returns a classification; never null, never throwing.
    /// </summary>
    public GridTrustContext Verify(SignatureMaterial material, string method, IDictionary parameters, DateTime nowUtc)
    {
        try
        {
            if (material == null || !material.HasAll)
                return GridTrustContext.Open;   // stock grid / no signature → Open, silently.

            byte[] publicKey;
            byte[] signature;
            try
            {
                publicKey = Convert.FromBase64String(material.Key);
                signature = Convert.FromBase64String(material.Signature);
            }
            catch (FormatException)
            {
                s_malformedLog.Log(m_log, LogLevel.Warning, "[TRUSTED HG]: signature material had malformed base64; classifying Open.");
                return GridTrustContext.Open;
            }

            if (publicKey.Length != Ed25519PublicKeyParameters.KeySize)
            {
                s_keyLengthLog.Log(m_log, LogLevel.Warning, "[TRUSTED HG]: presented key was {0} bytes, not {1}; classifying Open.",
                    publicKey.Length, Ed25519PublicKeyParameters.KeySize);
                return GridTrustContext.Open;
            }

            // Freshness: timestamp must be within ±tolerance of now (§5).
            if (!HGSignatureEnvelope.TryParseTimestamp(material.Timestamp, out DateTime ts))
            {
                s_timestampLog.Log(m_log, LogLevel.Warning, "[TRUSTED HG]: unparseable timestamp; classifying Open.");
                return GridTrustContext.Open;
            }

            TimeSpan skew = nowUtc.ToUniversalTime() - ts;
            if (skew < -HGSignatureEnvelope.TimestampTolerance || skew > HGSignatureEnvelope.TimestampTolerance)
            {
                s_skewLog.Log(m_log, LogLevel.Information, "[TRUSTED HG]: timestamp skew {0}s outside ±{1}s; classifying Open.",
                    (int)skew.TotalSeconds, (int)HGSignatureEnvelope.TimestampTolerance.TotalSeconds);
                return GridTrustContext.Open;
            }

            // Crypto: verify the Ed25519 signature over the canonical payload.
            string fingerprint = HGSignatureEnvelope.Sha256Hex(publicKey);
            // The sender's advisory URI is inside the digest (LEDGER D-5 / R-2): a tg_uri rewritten
            // on the wire changes the digest and the signature no longer verifies → Open. Absent
            // URI → the Slice 2 digest, unchanged.
            string paramsDigest = HGSignatureEnvelope.ParametersDigest(parameters, material.Uri);
            byte[] payload = HGSignatureEnvelope.BuildCanonicalPayload(
                method, fingerprint, material.Timestamp, material.Nonce, paramsDigest);

            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(payload, 0, payload.Length);
            if (!verifier.VerifySignature(signature))
            {
                s_signatureLog.Log(m_log, LogLevel.Information, "[TRUSTED HG]: signature did not verify; classifying Open.");
                return GridTrustContext.Open;
            }

            // Replay: a repeated nonce within the window is unverified. Registered only after the
            // signature is otherwise valid, so a bad request cannot burn a nonce.
            if (!m_nonces.TryRegister(material.Nonce, nowUtc))
            {
                s_replayLog.Log(m_log, LogLevel.Information, "[TRUSTED HG]: nonce replay within window; classifying Open.");
                return GridTrustContext.Open;
            }

            // Verified. Resolve tier from the registry; unknown-but-verified is Open (§3).
            int tier = GridTrustContext.TierOpen;
            UUID gridId = UUID.Zero;
            if (m_lookup != null && m_lookup.TryResolveByFingerprint(fingerprint, out UUID resolvedId, out int resolvedTier))
            {
                gridId = resolvedId;
                tier = resolvedTier;
            }

            return new GridTrustContext
            {
                GridId = gridId,
                Tier = tier,
                Outcome = VerificationOutcome.Verified,
            };
        }
        catch (Exception e)
        {
            // ADR-005: verification never hard-fails. Any unexpected error is Open.
            s_faultLog.Log(m_log, LogLevel.Warning, e, "[TRUSTED HG]: unexpected error during verification; classifying Open.");
            return GridTrustContext.Open;
        }
    }
}
