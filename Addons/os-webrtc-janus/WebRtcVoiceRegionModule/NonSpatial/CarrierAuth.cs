/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Authentication for the cross-process voice carriers: the group and conference ring (LGVRING1), the
 * conference text line (LGVTEXT1) and the avatar-to-avatar carriers (LGVA2A1).
 *
 * Both travel as grid instant messages. The grid IM endpoint sits on the region's public HTTP port and
 * does not authenticate its callers, so on its own a carrier proves nothing about who sent it. Each
 * carrier therefore carries an HMAC-SHA256 made with a secret shared by every region process of the grid
 * ([WebRtcVoice] CarrierSecret), and a receiver acts on a carrier only when the MAC checks out.
 *
 * The MAC covers: the carrier kind, the target, the session id, the session type, the room key, the
 * caller (for a text line, the sender), the credential the ring carries, the issued-at time, a random
 * nonce and, for a text line, the message itself. Each field is length-prefixed, so no value can run into
 * its neighbour whatever characters it holds.
 *
 * A receiver refuses a carrier that has no MAC or a wrong one (compared in constant time), one issued more
 * than the window ago or too far in the future (a bounded clock skew is allowed), and one whose nonce it
 * has already seen. The nonce store is bounded and refuses when it is full, the same fail-closed rule the
 * mixer's join-capability verifier applies. Only a carrier with a valid MAC reaches the nonce store, so an
 * unauthenticated sender cannot fill it.
 *
 * With no secret (or one shorter than MinSecretLength) the signer is off: nothing is sent and every
 * incoming carrier is refused. A grid whose regions all run in one process never uses a carrier.
 *
 * Refusals are counted by reason. Logging them is the caller's job and must name counts and reasons
 * only, never the contents of a carrier.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>What a carrier's MAC covers. Strings may be null; they are treated as empty.</summary>
    public readonly struct CarrierFields
    {
        public string Kind { get; init; }
        public UUID Target { get; init; }
        public UUID SessionId { get; init; }
        public string SessionType { get; init; }
        public string RoomKey { get; init; }
        public UUID Caller { get; init; }
        public string Credential { get; init; }
        public string Text { get; init; }
    }

    public sealed class CarrierAuth
    {
        /// <summary>A secret shorter than this is refused at startup.</summary>
        public const int MinSecretLength = 32;

        /// <summary>How old a carrier may be, in seconds.</summary>
        public const int WindowSeconds = 60;

        /// <summary>How far a sender's clock may differ from ours, in seconds, in either direction.</summary>
        public const int SkewSeconds = 30;

        /// <summary>The most nonces remembered at once. A full store refuses new carriers.</summary>
        public const int MaxNonces = 10000;

        /// <summary>The key the auth block travels under inside the carrier's JSON payload.</summary>
        public const string AuthKey = "carrier_auth";

        public const string KindRing = "ring";
        public const string KindText = "text";

        /// <summary>
        /// The kinds of the avatar-to-avatar carriers: a ring, the callee's join, a leave and a decline. Each
        /// is its own kind, so the MAC of one can never be replayed as another.
        /// </summary>
        public const string KindA2APrefix = "a2a-";

        public const string NoSecret = "no-secret";
        public const string MissingMac = "missing-mac";
        public const string MalformedMac = "malformed-mac";
        public const string BadMac = "bad-mac";
        public const string Stale = "stale";
        public const string Replay = "replay";
        public const string NonceStoreFull = "nonce-store-full";

        private const string CanonicalVersion = "LGVCARRIER1";
        private const int MaxNonceLength = 64;

        private readonly byte[] _key;
        private readonly Func<long> _unixNow;
        private readonly object _nonceLock = new object();
        private readonly Dictionary<string, long> _seen = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _refusals = new ConcurrentDictionary<string, long>();

        /// <param name="secret">The grid-shared secret. Null, empty or short leaves the signer off.</param>
        /// <param name="unixNow">Unix seconds; tests supply their own.</param>
        public CarrierAuth(string secret, Func<long> unixNow = null)
        {
            _unixNow = unixNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (CheckSecret(secret) == null)
                _key = Encoding.UTF8.GetBytes(secret);
        }

        /// <summary>A signer that is off: sends nothing and refuses everything.</summary>
        public static CarrierAuth Off() => new CarrierAuth(null);

        /// <summary>True when a usable secret is configured.</summary>
        public bool Enabled => _key != null;

        /// <summary>Null when the secret is usable; otherwise why not ("not-set" or "too-short").</summary>
        public static string CheckSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "not-set";
            return secret.Length < MinSecretLength ? "too-short" : null;
        }

        /// <summary>
        /// The auth block for one outgoing carrier, or null when the signer is off. The caller adds it to
        /// the carrier's payload under <see cref="AuthKey"/>. Every call draws a fresh nonce.
        /// </summary>
        public OSDMap Seal(in CarrierFields fields)
        {
            if (_key == null) return null;
            long iat = _unixNow();
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            return new OSDMap
            {
                ["iat"] = OSD.FromString(iat.ToString(CultureInfo.InvariantCulture)),
                ["n"] = OSD.FromString(nonce),
                ["mac"] = OSD.FromString(Mac(fields, iat, nonce)),
            };
        }

        /// <summary>
        /// Check one incoming carrier. Returns null when it is genuine, fresh and not seen before (its nonce
        /// is then recorded); otherwise the reason it is refused. The order is deliberate: the MAC is checked
        /// before anything else is learned from the carrier, and a nonce is stored only for a valid MAC.
        /// </summary>
        public string Verify(in CarrierFields fields, OSDMap auth)
        {
            if (_key == null) return NoSecret;
            if (auth == null) return MissingMac;
            if (!auth.TryGetString("iat", out string iatText) || !auth.TryGetString("n", out string nonce)
                || !auth.TryGetString("mac", out string macText))
                return MissingMac;
            if (!long.TryParse(iatText, NumberStyles.None, CultureInfo.InvariantCulture, out long iat)
                || string.IsNullOrEmpty(nonce) || nonce.Length > MaxNonceLength)
                return MalformedMac;

            byte[] given;
            try { given = Convert.FromHexString(macText); }
            catch (FormatException) { return MalformedMac; }

            byte[] expected = MacBytes(fields, iat, nonce);
            if (!CryptographicOperations.FixedTimeEquals(given, expected))
                return BadMac;

            long now = _unixNow();
            long age = now - iat;
            if (age > WindowSeconds + SkewSeconds || age < -SkewSeconds)
                return Stale;

            lock (_nonceLock)
            {
                if (_seen.Count >= MaxNonces)
                    Prune(now);
                if (_seen.ContainsKey(nonce))
                    return Replay;
                if (_seen.Count >= MaxNonces)
                    return NonceStoreFull;
                // Keep it until no carrier carrying it could still pass the freshness test.
                _seen[nonce] = iat + WindowSeconds + 2L * SkewSeconds;
            }
            return null;
        }

        /// <summary>Count one refusal and return how many times this reason has now been seen.</summary>
        public long RecordRefusal(string reason) => _refusals.AddOrUpdate(reason ?? "unknown", 1, (_, n) => n + 1);

        /// <summary>How many carriers have been refused for this reason.</summary>
        public long RefusalCount(string reason) => _refusals.TryGetValue(reason ?? "unknown", out long n) ? n : 0;

        /// <summary>Whether a refusal that brought a reason's count to <paramref name="count"/> is worth a log line.</summary>
        public static bool ShouldLog(long count) => count == 1 || count % 100 == 0;

        /// <summary>The Debug line for a carrier that passed authentication: ids and type only, never contents.</summary>
        public static string AcceptedLine(string kind, UUID session, NonSpatialSessionType type, UUID originRegion)
            => "carrier accepted: kind=" + kind + " session=" + session + " type=" + type + " from=" + originRegion;

        /// <summary>How many nonces are held now (for tests).</summary>
        public int NonceCount
        {
            get { lock (_nonceLock) return _seen.Count; }
        }

        private void Prune(long now)
        {
            List<string> gone = null;
            foreach (KeyValuePair<string, long> kv in _seen)
                if (kv.Value <= now)
                    (gone ??= new List<string>()).Add(kv.Key);
            if (gone == null) return;
            foreach (string n in gone) _seen.Remove(n);
        }

        private string Mac(in CarrierFields f, long iat, string nonce)
            => Convert.ToHexString(MacBytes(f, iat, nonce)).ToLowerInvariant();

        private byte[] MacBytes(in CarrierFields f, long iat, string nonce)
        {
            using HMACSHA256 mac = new HMACSHA256(_key);
            return mac.ComputeHash(Encoding.UTF8.GetBytes(Canonical(f, iat, nonce)));
        }

        private static string Canonical(in CarrierFields f, long iat, string nonce)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(CanonicalVersion);
            Field(sb, f.Kind);
            Field(sb, f.Target.ToString());
            Field(sb, f.SessionId.ToString());
            Field(sb, f.SessionType);
            Field(sb, f.RoomKey);
            Field(sb, f.Caller.ToString());
            Field(sb, f.Credential);
            Field(sb, iat.ToString(CultureInfo.InvariantCulture));
            Field(sb, nonce);
            Field(sb, f.Text);
            return sb.ToString();
        }

        // Length-prefixed, so "ab"+"c" and "a"+"bc" can never produce the same bytes.
        private static void Field(StringBuilder sb, string value)
        {
            value ??= string.Empty;
            sb.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }
    }
}
