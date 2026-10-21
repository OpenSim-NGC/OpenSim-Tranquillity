/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>The connector's authorised scope. Only Estate exists (the estate voice
/// room); per-parcel scope is not supported.</summary>
public enum VoiceConnectorScope
{
    Estate = 0,
}

/// <summary>
/// One voice-connector policy
/// record, loaded from a [VoiceConnector.&lt;name&gt;] ini section. Identity fields are immutable
/// (the record IS the operator's grant: writing the ini is the authorisation); the
/// mutable slots at the bottom are runtime state, set at registration, and are
/// guarded by the owning registry's lock, the A2ASessionRegistry shape (A2ASessionRegistry.cs).
/// </summary>
public sealed class VoiceConnectorRecord
{
    /// <summary>The section-name suffix ([VoiceConnector.Recorder] → "Recorder").</summary>
    public string Name { get; }
    public bool Enabled { get; }
    public string NpcFirstName { get; }
    public string NpcLastName { get; }
    /// <summary>Region-local position the NPC stands at — the parcel identity the visibility
    /// matrix reasons about.</summary>
    public Vector3 Position { get; }
    public VoiceConnectorScope Scope { get; }
    /// <summary>false = recording only; pushes a moderation mute for the NPC identity at
    /// registration (the mix silences it for every listener, no mixer change).</summary>
    public bool MayInject { get; }
    /// <summary>The authorising principal, as the operator recorded it.</summary>
    public string AuthorisedBy { get; }
    /// <summary>URL the sim POSTs the NPC's chat text to for TTS injection. Null when
    /// the key is absent (optional).</summary>
    public string InjectSourceUrl { get; }
    /// <summary>Optional region-name filter: a region server hosts several regions and
    /// each gets its own non-shared module instance, so without this an enabled record would
    /// spawn its NPC in EVERY region. Null = every region (single-region instances).</summary>
    public string Region { get; }
    /// <summary>The bearer secret for this record's join-capability endpoint,
    /// POST /voice/connector/&lt;name&gt;/join-cap. Null = no endpoint for this record (the default). A value shorter
    /// than <see cref="VoiceConnectorJoinCapEndpoint.MinSecretLength"/> is refused at load and stored as null.
    /// Never logged.</summary>
    public string CapabilitySecret { get; }

    public string NpcFullName => $"{NpcFirstName} {NpcLastName}";

    // ---- Mutable runtime state, set from registration onward. Written ONLY under the owning registry's
    // lock (VoiceConnectorRegistry), never by this class's consumers directly — the
    // A2ASessionRegistry discipline (immutable identity, lock-guarded mutable state).
    /// <summary>The NPC's agent id once created; UUID.Zero until then. Public setter
    /// for the test fixtures; production writes come only from VoiceConnectorRegistrar.</summary>
    public UUID NpcId { get; set; }
    /// <summary>The registered voice session id once provisioned; null until then.
    /// Public setter for the test fixtures, as NpcId.</summary>
    public string ViewerSessionId { get; set; }
    /// <summary>The room registration recorded for the NPC (the pRecordRoom value); null while
    /// inactive. The join-capability endpoint binds the capability to it.</summary>
    public int? Room { get; set; }

    // Public so tests can construct records directly; production records
    // still come only from VoiceConnectorRegistry.LoadFrom, which owns every refusal rule.
    public VoiceConnectorRecord(string pName, bool pEnabled, string pFirst, string pLast,
        Vector3 pPosition, VoiceConnectorScope pScope, bool pMayInject, string pAuthorisedBy,
        string pInjectSourceUrl, string pRegion = null, string pCapabilitySecret = null)
    {
        Region = pRegion;
        CapabilitySecret = string.IsNullOrEmpty(pCapabilitySecret) ? null : pCapabilitySecret;
        Name = pName;
        Enabled = pEnabled;
        NpcFirstName = pFirst;
        NpcLastName = pLast;
        Position = pPosition;
        Scope = pScope;
        MayInject = pMayInject;
        AuthorisedBy = pAuthorisedBy;
        InjectSourceUrl = pInjectSourceUrl;
        NpcId = UUID.Zero;
        ViewerSessionId = null;
    }
}
