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
using System.Threading;
using OpenMetaverse;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>Whether a request's signature was cryptographically verified (Design Brief §6).</summary>
public enum VerificationOutcome
{
    /// <summary>No signature, or one that failed crypto / freshness / replay checks. Always Open tier.</summary>
    Unverified = 0,

    /// <summary>Signature verified: fresh, non-replayed, valid over the canonical payload.</summary>
    Verified = 1,
}

/// <summary>
/// The result of classifying an inbound HG request (Design Brief §6). Produced by
/// <see cref="GridSignatureVerifier"/>, which NEVER rejects — absence of a context, or a context
/// carrying <see cref="VerificationOutcome.Unverified"/>, means Open tier. Enforcement is a
/// separate concern (a later slice); this type only carries the classification.
///
/// Tier values follow Design Brief §4: 0 = Blocked, 1 = Open, 2 = Trusted.
/// </summary>
public sealed class GridTrustContext
{
    public const int TierBlocked = 0;
    public const int TierOpen = 1;
    public const int TierTrusted = 2;

    /// <summary>Resolved registry id, or <see cref="UUID.Zero"/> when the grid is unknown.</summary>
    public UUID GridId { get; init; } = UUID.Zero;

    /// <summary>Resolved tier (§4 values). Unknown or unverified callers are Open.</summary>
    public int Tier { get; init; } = TierOpen;

    public VerificationOutcome Outcome { get; init; } = VerificationOutcome.Unverified;

    /// <summary>A fresh Open, Unverified context — the default classification for anyone.</summary>
    public static GridTrustContext Open => new();

    // ---- ambient per-request context (Design Brief §6: "available to services") ----

    private static readonly AsyncLocal<GridTrustContext> s_current = new();

    /// <summary>
    /// The context for the request currently being handled on this async flow, or null.
    /// Populated early in request handling; read by policy checks and by
    /// <c>TrustedGridAuthentication</c>. Null means Open.
    /// </summary>
    public static GridTrustContext Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }

    /// <summary>
    /// Publish <paramref name="context"/> as <see cref="Current"/> for the duration of a request and
    /// restore the previous value when the returned scope is disposed (Slice 3b). Use in a
    /// <c>using</c> around the request body so the context can never outlive it:
    /// <list type="bullet">
    /// <item>Sequential requests on one thread — including a long-lived listener thread that is
    /// not returned to the pool — never see a predecessor's context, because the scope restores
    /// (normally to null) in <c>finally</c> on every exit path, exception included.</item>
    /// <item>Concurrent requests on different threads never see each other's context, because
    /// <see cref="AsyncLocal{T}"/> is stored in the execution context that is private to each
    /// thread / async flow; a value set on one flow is invisible to every other.</item>
    /// </list>
    /// Passing null publishes "no context" (Open) for the scope, which is the correct value when
    /// the feature is disabled.
    /// </summary>
    public static IDisposable Enter(GridTrustContext context)
    {
        return new Scope(context);
    }

    private sealed class Scope : IDisposable
    {
        private readonly GridTrustContext m_previous;
        private bool m_disposed;

        public Scope(GridTrustContext context)
        {
            m_previous = s_current.Value;
            s_current.Value = context;
        }

        public void Dispose()
        {
            if (m_disposed)
                return;
            m_disposed = true;
            s_current.Value = m_previous;
        }
    }
}
