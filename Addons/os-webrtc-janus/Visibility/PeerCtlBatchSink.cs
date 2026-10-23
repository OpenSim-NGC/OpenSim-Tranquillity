/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Transport seam for the peer_ctl_batch feed (mixer-feed-protocol.md). Backend-neutral: the
 * orchestrator passes a typed op + per-listener exclusion sets; the implementation serializes,
 * STAMPS THE ROOM, wraps the Janus admin envelope, and sends. The orchestrator stays room-agnostic
 * and Janus-agnostic — this interface is the only thing it knows about the transport.
 *
 * Lives in Visibility so both the orchestrator (WebRtcVoiceRegionModule) and the prod
 * implementation (Janus-side, WebRtcVoiceServiceModule) can reference it without either referencing
 * the other, and without touching WebRtcVoice.dll.
 */

using System.Collections.Generic;
using System.Threading.Tasks;
using OpenMetaverse;

namespace osWebRtcVoice
{
    /// <summary>How a peer_ctl_batch send resolved. Mirrors the transport's Ok/TransportError/
    /// ProtocolError, mapped by the sink. Note: Ok means the mixer accepted+dispatched the batch,
    /// NOT that it applied — an unknown room returns success (see JanusAdminClient).</summary>
    public enum PeerCtlSendResult
    {
        Ok,
        TransportError,
        ProtocolError,
    }

    /// <summary>Inner-reply observability for the most recent SendAsync, SUMMED across the rooms the
    /// send addressed. Parsed from the mixer's peer_ctl_batch reply
    /// (<c>{janus:success, response:{slvoice, entries, mute_entries, skipped, deferred_listeners}}</c>,
    /// mixer janus_slvoice.c:1552-1557 lineage plus the additive deferred_listeners from 27977c8). Every
    /// field is "no info" (zero) when the mixer omitted it -- an old / pre-mute-channel mixer, whose
    /// reply carries none of these -- so the default is behaviourally identical to before this change.
    /// PLUMBING only: VisibilityBatchSender does NOT act on it (yet); it is surfaced so a future decision
    /// (e.g. a targeted resync on a drop) can read it. See mixer-feed-protocol.md.</summary>
    public readonly struct PeerCtlSendStats
    {
        /// <summary>Rooms in the send whose reply carried a parseable inner slvoice object.</summary>
        public int RepliesParsed { get; init; }
        /// <summary>Sum of "deferred_listeners": entries the mixer deferred for not-yet-joined
        /// listeners (join-window self-heal, replayed on join). Reported at INFO, never a fault.</summary>
        public int DeferredListeners { get; init; }
        /// <summary>Sum of "skipped": parse-time malformed items the mixer dropped. &gt;0 is real loss.</summary>
        public int Skipped { get; init; }
        /// <summary>Sum of "entries": exclusion listener entries the mixer parsed.</summary>
        public int Entries { get; init; }
        /// <summary>Sum of "mute_entries": moderation-mute listener entries the mixer parsed.</summary>
        public int MuteEntries { get; init; }
        /// <summary>Rooms whose reply was present but not applied (inner status != "applied") or
        /// malformed. &gt;0 indicates protocol drift; reported at WARN. Does NOT change the send's
        /// PeerCtlSendResult -- the transport succeeded, this is a mixer-side application signal.</summary>
        public int Anomalies { get; init; }
    }

    /// <summary>The orchestrator→transport seam. One op (add/remove/replace) with per-listener
    /// excluded-source sets. The implementation stamps the room and sends.</summary>
    public interface IPeerCtlBatchSink
    {
        /// <param name="mute">ADDITIVE moderation-mute channel (Option A). Null/empty => no mute
        /// changes in this op => the serialized body has no "mute" key => byte-for-byte the old wire.
        /// Optional so existing callers and test doubles compile unchanged.</param>
        Task<PeerCtlSendResult> SendAsync(VisOp op,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute = null);

        /// <summary>Inner-reply stats from the most recent <see cref="SendAsync"/> (for observability),
        /// or default (all-zero) for a sink whose transport reports nothing. Read-only PLUMBING -- a
        /// future decision may consult it; nothing acts on it today. Never affects SendAsync's result.</summary>
        PeerCtlSendStats LastSendStats { get; }
    }
}
