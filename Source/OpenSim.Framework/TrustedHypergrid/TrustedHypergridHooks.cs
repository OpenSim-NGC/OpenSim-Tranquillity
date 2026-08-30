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
using System.Reflection;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// Process-wide ambient entry points that HG call sites use to sign outbound requests and classify
/// inbound ones (Design Brief §5, §6). Everything here is a safe no-op until the runtime is
/// initialised and <see cref="TrustedHypergridRuntime.Enabled"/> — a call site can call these
/// unconditionally and, when the feature is off or unconfigured, nothing changes.
/// </summary>
public static class TrustedHypergridHooks
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly object s_lock = new();
    private static bool s_initialized;

    /// <summary>The active runtime, or null before initialisation. Settable for tests.</summary>
    public static TrustedHypergridRuntime Runtime { get; set; }

    /// <summary>
    /// The trust registry the runtime's verifier resolves against, or null when none was supplied
    /// (verified callers then classify as Open). Also consulted for first-contact recording when it
    /// implements <see cref="IGridTrustRecorder"/>. Settable for tests.
    /// </summary>
    public static IGridTrustLookup Lookup { get; set; }

    /// <summary>
    /// Initialise the process runtime once from config. Idempotent: the first caller (the inbound
    /// gatekeeper connector, which owns the config) wins; later calls are no-ops.
    /// <paramref name="lookup"/> is the trust registry (Slice 3); null keeps Slice 2b behaviour,
    /// where a verified caller resolves to no grid and classifies Open.
    /// </summary>
    public static void EnsureInitialized(IConfigSource config, IGridTrustLookup lookup = null)
    {
        if (s_initialized)
            return;

        lock (s_lock)
        {
            if (s_initialized)
                return;

            try
            {
                Lookup = lookup;
                Runtime = TrustedHypergridRuntime.FromConfig(config, lookup);
            }
            catch (Exception e)
            {
                // Never let identity setup break service startup; degrade to disabled.
                m_log.LogWarning(e, "[TRUSTED HG]: failed to initialise runtime; feature disabled for this process.");
                Runtime = TrustedHypergridRuntime.Disabled();
            }

            s_initialized = true;
        }
    }

    /// <summary>
    /// Attach the four <c>tg_*</c> signature keys to an outbound XML-RPC param Hashtable. No-op when
    /// the feature is disabled/unconfigured, leaving the Hashtable byte-identical to unsigned.
    /// </summary>
    public static void SignOutbound(Hashtable parameters, string method)
    {
        TrustedHypergridRuntime rt = Runtime;
        if (rt == null || !rt.Enabled || rt.Signer == null || parameters == null)
            return;

        rt.Signer.SignInto(parameters, method, DateTime.UtcNow);
    }

    /// <summary>
    /// Classify an inbound XML-RPC request from its param Hashtable and log the resulting tier at
    /// DEBUG. Returns null when the feature is disabled. NEVER rejects and NEVER throws (ADR-005):
    /// no material, malformed, unknown, expired or replayed all yield an Open context and the caller
    /// proceeds normally. In this slice the context is only logged; nothing is enforced.
    /// </summary>
    public static GridTrustContext ClassifyInbound(Hashtable parameters, string method)
        => ClassifyInbound(parameters, method, null);

    /// <summary>
    /// As <see cref="ClassifyInbound(Hashtable, string)"/>, additionally recording first contact
    /// (Design Brief §3) when the caller's home URI is known: a signature-verified caller is
    /// recorded against <paramref name="claimedHomeUri"/> as Open/pending, a repeat contact
    /// refreshes last-seen, and a changed key is flagged — never promoted, never refused. The
    /// gatekeeper XML-RPC transport carries no caller URI (LEDGER D-5), so callers on that path
    /// pass null and nothing is recorded.
    /// </summary>
    public static GridTrustContext ClassifyInbound(Hashtable parameters, string method, string claimedHomeUri)
    {
        TrustedHypergridRuntime rt = Runtime;
        if (rt == null || !rt.Enabled || rt.Verifier == null)
            return null;

        DateTime now = DateTime.UtcNow;
        SignatureMaterial material = SignatureMaterial.FromHashtable(parameters);
        GridTrustContext ctx = rt.Verifier.Verify(material, method, parameters, now);

        if (ctx.Outcome == VerificationOutcome.Verified
            && !string.IsNullOrWhiteSpace(claimedHomeUri)
            && Lookup is IGridTrustRecorder recorder)
        {
            ctx = RecordFirstContact(recorder, ctx, material, claimedHomeUri, now);
        }

        m_log.LogDebug("[TRUSTED HG]: inbound {0} classified tier={1} outcome={2} grid={3}",
            method, ctx.Tier, ctx.Outcome, ctx.GridId);

        return ctx;
    }

    /// <summary>
    /// TOFU record and re-resolve. Any failure leaves the context exactly as verified (ADR-005).
    /// </summary>
    private static GridTrustContext RecordFirstContact(IGridTrustRecorder recorder, GridTrustContext ctx,
        SignatureMaterial material, string claimedHomeUri, DateTime now)
    {
        try
        {
            byte[] publicKey = Convert.FromBase64String(material.Key);
            string fingerprint = HGSignatureEnvelope.Sha256Hex(publicKey);
            recorder.RecordPresentedKey(claimedHomeUri, publicKey, fingerprint, now);

            if (ctx.GridId == UUID.Zero && Lookup.TryResolveByFingerprint(fingerprint, out UUID gridId, out int tier))
            {
                return new GridTrustContext
                {
                    GridId = gridId,
                    Tier = tier,
                    Outcome = ctx.Outcome,
                };
            }
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "[TRUSTED HG]: first-contact recording failed for {0}; classification unchanged", claimedHomeUri);
        }
        return ctx;
    }
}
