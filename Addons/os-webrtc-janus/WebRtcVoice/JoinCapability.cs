/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The sim-issued join capability.
 *
 * The sim mints one capability per join, bound to the agent, its viewer session, the room, the arming state the
 * sim believed current (epoch and generation), an expiry and a one-shot nonce. The mixer verifies it
 * (the slvoice mixer's join-capability validator). The VIEWER NEVER SEES IT: the viewer talks to a region capability URL and
 * the sim's own Janus session performs the join, so the capability travels sim -> mixer only.
 *
 *   join_cap = "v1." + b64url(payload) + "." + b64url(HMAC-SHA256(key, "v1." + b64url(payload)))
 *   payload  = "<agent>|<session>|<room>|<epoch>|<generation>|<iat>|<exp>|<nonce>"
 *
 * It is defence in depth against a leaked JS_API_SECRET, and it is NOT arming: a capability admits a join and
 * grants no audibility.
 *
 * NEVER log a capability, its payload, its nonce or the secret, at any level. Tests assert this.
 */

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace osWebRtcVoice;

public static class JoinCapability
{
    /// <summary>The wire version this sim mints and the mixer accepts.</summary>
    public const string Version = "v1";

    /// <summary>Short-lived: seconds from issue to expiry.</summary>
    public const int LifetimeSeconds = 60;

    /// <summary>Nonce size in bytes, rendered as lowercase hex (32 chars), like the A2A session token.</summary>
    public const int NonceBytes = 16;

    /// <summary>The epoch field when the sim holds no authority for the room (arming off).</summary>
    public const string NoEpoch = "0000000000000000";

    /// <summary>A fresh one-shot nonce.</summary>
    public static string NewNonce()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceBytes)).ToLowerInvariant();

    /// <summary>The canonical payload the HMAC covers. Field order is the wire order and must not change without a
    /// new version prefix; the mixer splits on '|', so no field may contain one.</summary>
    public static string Canonical(string agent, string session, int room, string epochHex, uint generation,
        long issuedAtUnix, long expiresAtUnix, string nonce)
        => string.Join("|",
            agent,
            session,
            room.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrEmpty(epochHex) ? NoEpoch : epochHex,
            generation.ToString(CultureInfo.InvariantCulture),
            issuedAtUnix.ToString(CultureInfo.InvariantCulture),
            expiresAtUnix.ToString(CultureInfo.InvariantCulture),
            nonce);

    /// <summary>Mint one capability. Returns null when it cannot be minted (no secret, or a field that would break
    /// the payload), so a caller with the knob on and no secret simply sends no capability rather than a broken
    /// one. <paramref name="nonce"/> is for tests; production passes null and gets a fresh one.</summary>
    public static string Mint(string secret, string agent, string session, int room, string epochHex, uint generation,
        long nowUnix, string nonce = null, int lifetimeSeconds = LifetimeSeconds)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(agent) || string.IsNullOrEmpty(session) || room <= 0)
            return null;
        nonce ??= NewNonce();
        string epoch = string.IsNullOrEmpty(epochHex) ? NoEpoch : epochHex;
        if (agent.Contains('|') || session.Contains('|') || nonce.Contains('|') || epoch.Contains('|'))
            return null;
        string payload = Canonical(agent, session, room, epoch, generation, nowUnix, nowUnix + lifetimeSeconds, nonce);
        string signing = Version + "." + B64Url(Encoding.UTF8.GetBytes(payload));
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return signing + "." + B64Url(mac.ComputeHash(Encoding.UTF8.GetBytes(signing)));
    }

    /// <summary>Unix seconds now, the clock both sides compare against (tolerates ±120 s of skew).</summary>
    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
