/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
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

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// Records first contact and key presentation for a signature-verified remote grid (Design Brief
/// §3: first contact records the presented public key against the URI, tier Open, state pending;
/// a key change on an established relationship is flagged, never silently overwritten).
/// </summary>
/// <remarks>
/// Implemented alongside <see cref="IGridTrustLookup"/> by the registry. Kept as a separate,
/// optional interface so a lookup-only implementation (tests, read-only deployments) needs no
/// write path. Recording NEVER promotes a grid: a recorded grid is Open/pending until an operator
/// approves it. Implementations must not throw into the request path.
/// </remarks>
public interface IGridTrustRecorder
{
    /// <summary>
    /// Record that <paramref name="homeUri"/> presented <paramref name="publicKey"/> (with
    /// <paramref name="keyFingerprint"/>) at <paramref name="whenUtc"/>. Unknown URI → new row,
    /// Open/pending. Known URI with the same key → last_seen updated. Known URI with a different
    /// key → state flagged for re-approval, original key preserved.
    /// </summary>
    void RecordPresentedKey(string homeUri, byte[] publicKey, string keyFingerprint, DateTime whenUtc);
}
