/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The voice connector NPC's agent id, derived instead of random.
 *
 * INPCModule.CreateNPC hands out a random agent id, so using it directly would give the connector a
 * new UUID on every region server restart: the operator would have to re-edit DISPLAY in the mixer-side
 * peer config, and a peer still joined under the OLD id would not be the identity the sim
 * moderation-muted at registration, so a stale injector would play unmuted and undisclosed.
 *
 * The id is therefore RFC 4122 version 5 (SHA-1, name-based) under a fixed connector-identity namespace, with the exact,
 * case-sensitive name "{gridId}/{regionName}/{recordName}":
 *   gridId     - the normalised GatekeeperURI (JanusAudioBridge.ReadGridId); empty when the grid has
 *                none. This tree has no grid UUID, so the grid is identified the same way the multiagent
 *                room numbers identify it.
 *   regionName - the scene the record starts in (a record without Region gets a distinct id per region).
 *   recordName - the [VoiceConnector.<name>] suffix.
 * Same inputs, same id, on every restart; renaming any of the three gives a new one.
 *
 * Pure, so it unit-tests without a Scene.
 */

using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;

namespace osWebRtcVoice;

public static class ConnectorIdentity
{
    /// <summary>The name the connector-identity namespace is itself derived from, under the RFC 4122 URL namespace.</summary>
    public const string ConnectorNamespaceName = "urn:opensim:webrtc-voice:connector";

    /// <summary>The connector-identity namespace: uuid5(NAMESPACE_URL, "urn:opensim:webrtc-voice:connector").
    /// Fixed forever - changing it would give every deployed connector a new id.</summary>
    public static readonly UUID ConnectorNamespace = new UUID("ca7b653b-b665-5e77-9260-6674637f6134");

    /// <summary>The exact v5 name for a connector: "{gridId}/{regionName}/{recordName}" (null reads as empty).</summary>
    public static string IdentityName(string pGridId, string pRegionName, string pRecordName)
        => (pGridId ?? string.Empty) + "/" + (pRegionName ?? string.Empty) + "/" + (pRecordName ?? string.Empty);

    /// <summary>The connector NPC's agent id for this grid, region and record.</summary>
    public static UUID DeriveAgentId(string pGridId, string pRegionName, string pRecordName)
        => NameBasedV5(ConnectorNamespace, IdentityName(pGridId, pRegionName, pRecordName));

    /// <summary>RFC 4122 section 4.3, version 5: SHA-1 over the namespace id (network byte order, which is
    /// the order of its canonical string) followed by the UTF-8 name; the first 16 bytes with the version
    /// nibble set to 5 and the variant bits to 10.</summary>
    public static UUID NameBasedV5(UUID pNamespace, string pName)
    {
        byte[] ns = Convert.FromHexString(pNamespace.ToString().Replace("-", string.Empty));
        byte[] name = Encoding.UTF8.GetBytes(pName ?? string.Empty);
        byte[] input = new byte[ns.Length + name.Length];
        Buffer.BlockCopy(ns, 0, input, 0, ns.Length);
        Buffer.BlockCopy(name, 0, input, ns.Length, name.Length);

        byte[] hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);   // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // RFC 4122 variant (10xx)

        string hex = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        return new UUID(hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-"
            + hex.Substring(16, 4) + "-" + hex.Substring(20, 12));
    }
}
