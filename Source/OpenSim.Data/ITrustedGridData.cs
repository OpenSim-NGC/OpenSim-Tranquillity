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
using System.Data;
using OpenMetaverse;
using OpenSim.Framework.TrustedHypergrid;

namespace OpenSim.Data;

/// <summary>
/// Trust tier for a remote grid (Design Brief §3/§4, <c>tier</c> column).
/// </summary>
public enum TrustTier
{
    Blocked = 0,
    Open = 1,
    Trusted = 2,
}

/// <summary>
/// Establishment state for a trust-registry row (Design Brief §4, <c>state</c> column).
/// </summary>
public enum TrustState
{
    Pending = 0,
    Approved = 1,
    KeyChangedPendingReapproval = 2,
}

/// <summary>
/// One row of the <c>hg_trusted_grids</c> trust registry (Design Brief §4).
/// Columns are exactly as specified by the brief; do not add, rename, or retype.
/// </summary>
public class TrustedGridData
{
    public UUID Id;
    public string HomeUri;
    /// <summary>Ed25519 raw public key (32 bytes). Null for grids that have never signed.</summary>
    public byte[] PublicKey;
    /// <summary>SHA-256 hex of the public key; the operator-facing identifier.</summary>
    public string KeyFingerprint;
    public int Tier;
    public int State;
    public DateTime FirstSeen;
    public DateTime LastSeen;
    public string ApprovedBy;
    public DateTime? ApprovedAt;
    public string Notes;

    /// <summary>Column list in canonical order, shared by every backend's SQL.</summary>
    public static readonly string[] Columns =
    {
        "id", "home_uri", "public_key", "key_fingerprint", "tier", "state",
        "first_seen", "last_seen", "approved_by", "approved_at", "notes",
    };

    /// <summary>Materialise a row from a reader positioned on it. Backend-agnostic.</summary>
    public static TrustedGridData FromReader(IDataReader r)
    {
        return new TrustedGridData
        {
            Id = new UUID(r["id"].ToString()),
            HomeUri = r["home_uri"].ToString(),
            PublicKey = r["public_key"] is DBNull ? null : (byte[])r["public_key"],
            KeyFingerprint = r["key_fingerprint"] is DBNull ? string.Empty : r["key_fingerprint"].ToString(),
            Tier = Convert.ToInt32(r["tier"]),
            State = Convert.ToInt32(r["state"]),
            FirstSeen = Convert.ToDateTime(r["first_seen"]),
            LastSeen = Convert.ToDateTime(r["last_seen"]),
            ApprovedBy = r["approved_by"] is DBNull ? string.Empty : r["approved_by"].ToString(),
            ApprovedAt = r["approved_at"] is DBNull ? (DateTime?)null : Convert.ToDateTime(r["approved_at"]),
            Notes = r["notes"] is DBNull ? string.Empty : r["notes"].ToString(),
        };
    }
}

/// <summary>
/// Persistence for the Trusted Hypergrid trust registry (Design Brief §4).
/// Slice 1: data only — nothing enforces or verifies against this yet.
///
/// URI normalisation is the single shared <see cref="HGUriNormalizer"/> applied on both
/// the write and the lookup side of every URI-keyed operation, per the Design Brief §4
/// mandate that ad-hoc string comparison (the existing defect) not be reintroduced.
/// </summary>
public interface ITrustedGridData
{
    /// <summary>Lookup by primary key.</summary>
    TrustedGridData Get(UUID id);

    /// <summary>Lookup by home URI. The argument is normalised before comparison.</summary>
    TrustedGridData GetByHomeUri(string homeUri);

    /// <summary>Lookup by the SHA-256 key fingerprint (the operator-facing identifier).</summary>
    TrustedGridData GetByFingerprint(string keyFingerprint);

    /// <summary>Lookup a grid via one of its alias URIs (<c>hg_grid_aliases</c>).</summary>
    TrustedGridData GetByAlias(string aliasUri);

    /// <summary>
    /// Insert or update a full row keyed by <see cref="TrustedGridData.Id"/>.
    /// The home URI is normalised on the way in.
    /// </summary>
    bool Store(TrustedGridData data);

    /// <summary>Record an alias URI (normalised) for an existing grid (<c>hg_grid_aliases</c>).</summary>
    bool StoreAlias(UUID gridId, string aliasUri);

    /// <summary>
    /// Record a public key presented by a grid at contact, implementing the Design Brief §3
    /// establishment flow:
    ///   * unknown URI  -> insert (tier Open, state Pending), key adopted;
    ///   * same key     -> touch <c>last_seen</c>;
    ///   * a URI that had no key yet -> adopt the key, state unchanged;
    ///   * a *different* key on an established relationship -> state
    ///     <see cref="TrustState.KeyChangedPendingReapproval"/>; the stored key is preserved,
    ///     never silently overwritten (Design Brief §3: a key change is a hard failure
    ///     requiring re-approval).
    /// Returns the stored row.
    /// </summary>
    TrustedGridData RecordPresentedKey(string homeUri, byte[] publicKey, string keyFingerprint, DateTime whenUtc)
    {
        string norm = HGUriNormalizer.Normalize(homeUri);
        TrustedGridData existing = GetByHomeUri(norm);

        if (existing == null)
        {
            var rec = new TrustedGridData
            {
                Id = UUID.Random(),
                HomeUri = norm,
                PublicKey = publicKey,
                KeyFingerprint = keyFingerprint ?? string.Empty,
                Tier = (int)TrustTier.Open,
                State = (int)TrustState.Pending,
                FirstSeen = whenUtc,
                LastSeen = whenUtc,
                ApprovedBy = string.Empty,
                ApprovedAt = null,
                Notes = string.Empty,
            };
            Store(rec);
            return GetByHomeUri(norm);
        }

        existing.LastSeen = whenUtc;

        if (existing.PublicKey == null || existing.PublicKey.Length == 0)
        {
            // First key ever seen for this URI: adopt it, leave state as-is.
            existing.PublicKey = publicKey;
            existing.KeyFingerprint = keyFingerprint ?? string.Empty;
        }
        else if (!KeysEqual(existing.PublicKey, publicKey))
        {
            // Different key on an established relationship: flag for re-approval,
            // preserve the stored key and fingerprint (do not overwrite).
            existing.State = (int)TrustState.KeyChangedPendingReapproval;
        }
        // else: same key — nothing to change beyond last_seen.

        Store(existing);
        return GetByHomeUri(norm);
    }

    private static bool KeysEqual(byte[] a, byte[] b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null)
            return false;
        return ((ReadOnlySpan<byte>)a).SequenceEqual(b);
    }
}
