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
 * EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE
 * CONTRIBUTORS BE LIABLE FOR ANY DAMAGES ARISING IN ANY WAY OUT OF THE USE OF
 * THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Specialized;
using System.Net;
using OpenMetaverse;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.TrustedHypergrid;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace OpenSim.TrustedHypergrid.Tests;

public class SignatureVerificationTests
{
    private const string Method = "get_region";

    private static Hashtable SampleParams() => new() { { "region_id", "abc" }, { "x", "1000" }, { "y", "2000" } };

    private static (GridKeypair kp, GridSignatureSigner signer) NewSigner()
    {
        GridKeypair kp = GridKeypair.Generate();
        return (kp, new GridSignatureSigner(kp));
    }

    /// <summary>Registry stub: resolves one fingerprint to a fixed grid id + tier.</summary>
    private sealed class FakeLookup : IGridTrustLookup
    {
        private readonly string m_fingerprint;
        private readonly UUID m_gridId;
        private readonly int m_tier;

        public FakeLookup(string fingerprint, UUID gridId, int tier)
        {
            m_fingerprint = fingerprint;
            m_gridId = gridId;
            m_tier = tier;
        }

        public bool TryResolveByFingerprint(string keyFingerprint, out UUID gridId, out int tier)
        {
            if (keyFingerprint == m_fingerprint)
            {
                gridId = m_gridId;
                tier = m_tier;
                return true;
            }
            gridId = UUID.Zero;
            tier = GridTrustContext.TierOpen;
            return false;
        }
    }

    // 1 — sign -> verify round-trip yields a Trusted-eligible context.
    [Fact]
    public void SignVerifyRoundTrip_YieldsTrustedEligibleContext()
    {
        (GridKeypair kp, GridSignatureSigner signer) = NewSigner();
        UUID gridId = UUID.Random();
        var verifier = new GridSignatureVerifier(new FakeLookup(kp.Fingerprint, gridId, GridTrustContext.TierTrusted));

        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;
        SignatureMaterial material = signer.Sign(Method, p, now);

        GridTrustContext ctx = verifier.Verify(material, Method, p, now);

        Assert.Equal(VerificationOutcome.Verified, ctx.Outcome);
        Assert.Equal(GridTrustContext.TierTrusted, ctx.Tier);
        Assert.Equal(gridId, ctx.GridId);
    }

    // 2 — no signature material yields an Open context; no exception, no rejection.
    [Fact]
    public void NoSignatureMaterial_YieldsOpen_NoThrow()
    {
        var verifier = new GridSignatureVerifier(lookup: null);

        // A carrier that never had the tg_* keys (a stock grid) extracts to "not HasAll".
        SignatureMaterial absent = SignatureMaterial.FromHashtable(new Hashtable());

        GridTrustContext ctx = null;
        Exception ex = Record.Exception(() => ctx = verifier.Verify(absent, Method, SampleParams(), DateTime.UtcNow));

        Assert.Null(ex);
        Assert.NotNull(ctx);
        Assert.Equal(VerificationOutcome.Unverified, ctx.Outcome);
        Assert.Equal(GridTrustContext.TierOpen, ctx.Tier);

        // And an outright null material behaves the same.
        Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(null, Method, null, DateTime.UtcNow).Outcome);
    }

    // 3 — tampered signature yields Open, does not throw.
    [Fact]
    public void TamperedSignature_YieldsOpen_NoThrow()
    {
        (_, GridSignatureSigner signer) = NewSigner();
        var verifier = new GridSignatureVerifier(lookup: null);

        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;
        SignatureMaterial material = signer.Sign(Method, p, now);

        byte[] sig = Convert.FromBase64String(material.Signature);
        sig[0] ^= 0xFF;                                   // flip a bit; still valid base64
        material.Signature = Convert.ToBase64String(sig);

        GridTrustContext ctx = null;
        Exception ex = Record.Exception(() => ctx = verifier.Verify(material, Method, p, now));

        Assert.Null(ex);
        Assert.Equal(VerificationOutcome.Unverified, ctx.Outcome);
        Assert.Equal(GridTrustContext.TierOpen, ctx.Tier);
    }

    // 3b — tampered parameters (payload no longer matches the signature) also yield Open.
    [Fact]
    public void TamperedParameters_YieldOpen()
    {
        (_, GridSignatureSigner signer) = NewSigner();
        var verifier = new GridSignatureVerifier(lookup: null);

        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;
        SignatureMaterial material = signer.Sign(Method, p, now);

        p["x"] = "9999";   // change a signed parameter after signing

        Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(material, Method, p, now).Outcome);
    }

    // 4 — timestamp at +301s yields Open.
    [Fact]
    public void TimestampBeyondTolerance_YieldsOpen()
    {
        (_, GridSignatureSigner signer) = NewSigner();
        var verifier = new GridSignatureVerifier(lookup: null);

        Hashtable p = SampleParams();
        // Whole-second instant: the envelope timestamp is ISO-8601 seconds, so a fractional
        // "now" would push the ±300s boundary off by the truncated sub-second remainder.
        DateTime now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        SignatureMaterial material = signer.Sign(Method, p, now);

        GridTrustContext ctx = verifier.Verify(material, Method, p, now.AddSeconds(301));

        Assert.Equal(VerificationOutcome.Unverified, ctx.Outcome);
        Assert.Equal(GridTrustContext.TierOpen, ctx.Tier);

        // Sanity: at exactly +300s it is still within tolerance and verifies.
        SignatureMaterial fresh = signer.Sign(Method, p, now);
        Assert.Equal(VerificationOutcome.Verified, verifier.Verify(fresh, Method, p, now.AddSeconds(300)).Outcome);
    }

    // 5 — replayed nonce within the 600s window yields Open.
    [Fact]
    public void ReplayedNonce_YieldsOpen()
    {
        (_, GridSignatureSigner signer) = NewSigner();
        var verifier = new GridSignatureVerifier(lookup: null);

        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;
        SignatureMaterial material = signer.Sign(Method, p, now);

        Assert.Equal(VerificationOutcome.Verified, verifier.Verify(material, Method, p, now).Outcome);

        // Same material replayed a minute later — still inside the 600s window.
        GridTrustContext replay = verifier.Verify(material, Method, p, now.AddSeconds(60));
        Assert.Equal(VerificationOutcome.Unverified, replay.Outcome);
        Assert.Equal(GridTrustContext.TierOpen, replay.Tier);
    }

    // 6 — TrustedGridAuthentication returns TRUE for every one of the above cases; only Blocked is refused.
    [Fact]
    public void TrustedGridAuthentication_ReturnsTrue_ForEveryNonBlockedCase()
    {
        (GridKeypair kp, GridSignatureSigner signer) = NewSigner();
        UUID gridId = UUID.Random();
        var verifier = new GridSignatureVerifier(new FakeLookup(kp.Fingerprint, gridId, GridTrustContext.TierTrusted));
        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;

        SignatureMaterial signed = signer.Sign(Method, p, now);
        SignatureMaterial tampered = signer.Sign(Method, p, now);
        byte[] sig = Convert.FromBase64String(tampered.Signature); sig[0] ^= 0xFF;
        tampered.Signature = Convert.ToBase64String(sig);

        var contexts = new[]
        {
            verifier.Verify(signed, Method, p, now),                              // verified/Trusted
            GridSignatureVerifierOpen(),                                          // no material
            new GridSignatureVerifier(null).Verify(tampered, Method, p, now),     // tampered
            new GridSignatureVerifier(null).Verify(signer.Sign(Method, p, now), Method, p, now.AddSeconds(301)), // expired
        };

        var auth = new TrustedGridAuthentication();
        try
        {
            foreach (GridTrustContext ctx in contexts)
            {
                GridTrustContext.Current = ctx;
                bool ok = auth.Authenticate(new NameValueCollection(), (_, _) => { }, out HttpStatusCode sc);
                Assert.True(ok);
                Assert.Equal(HttpStatusCode.OK, sc);
            }

            // Absent context (the common case for a stock grid) → true.
            GridTrustContext.Current = null;
            Assert.True(auth.Authenticate(new NameValueCollection(), (_, _) => { }, out _));

            // The one rejection path: an explicit Blocked tier → false.
            GridTrustContext.Current = new GridTrustContext { Tier = GridTrustContext.TierBlocked, Outcome = VerificationOutcome.Verified };
            bool blocked = auth.Authenticate(new NameValueCollection(), (_, _) => { }, out HttpStatusCode blockedCode);
            Assert.False(blocked);
            Assert.Equal(HttpStatusCode.Forbidden, blockedCode);
        }
        finally
        {
            GridTrustContext.Current = null;
        }
    }

    private static GridTrustContext GridSignatureVerifierOpen()
        => new GridSignatureVerifier(null).Verify(SignatureMaterial.FromHashtable(new Hashtable()), Method, null, DateTime.UtcNow);

    // 7 — the canonical payload is byte-identical between the signer and verifier paths.
    [Fact]
    public void CanonicalPayload_IsByteIdentical_BetweenSignerAndVerifier()
    {
        (GridKeypair kp, GridSignatureSigner signer) = NewSigner();
        Hashtable p = new() { { "z", "26" }, { "a", "1" }, { "m", "13" } };   // unordered on purpose
        DateTime now = DateTime.UtcNow;

        SignatureMaterial material = signer.Sign(Method, p, now);

        // Reconstruct exactly what the verifier canonicalises, from the wire material.
        byte[] publicKey = Convert.FromBase64String(material.Key);
        string fingerprint = HGSignatureEnvelope.Sha256Hex(publicKey);
        string digest = HGSignatureEnvelope.ParametersDigest(p);
        byte[] verifierPayload = HGSignatureEnvelope.BuildCanonicalPayload(
            Method, fingerprint, material.Timestamp, material.Nonce, digest);

        // The signer signed some bytes; prove they are exactly the verifier's bytes by checking the
        // signature validates against the reconstructed payload (Ed25519 is deterministic per key).
        var v = new Ed25519Signer();
        v.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        v.BlockUpdate(verifierPayload, 0, verifierPayload.Length);
        Assert.True(v.VerifySignature(Convert.FromBase64String(material.Signature)),
            "signer and verifier disagree on the canonical payload bytes");

        // And the builder itself is deterministic for a fixed input.
        byte[] again = HGSignatureEnvelope.BuildCanonicalPayload(
            Method, fingerprint, material.Timestamp, material.Nonce, digest);
        Assert.Equal(verifierPayload, again);

        // Parameter digest is order-independent (canonical form sorts keys).
        Hashtable reordered = new() { { "m", "13" }, { "a", "1" }, { "z", "26" } };
        Assert.Equal(digest, HGSignatureEnvelope.ParametersDigest(reordered));
    }
}
