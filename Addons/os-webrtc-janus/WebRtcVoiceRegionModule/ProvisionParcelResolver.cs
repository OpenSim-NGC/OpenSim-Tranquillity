/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Decides which parcel a "local" voice provision is authorized and roomed against.
 *
 * For a ROOT agent the parcel is derived server-side from the avatar's position; the viewer's
 * parcel_local_id is a hint only. It never selects the parcel — it only decides what gets logged (a
 * mismatch, or an absent id). The parcel was taken from what the viewer sent, rather than from where the
 * avatar is. A mismatch is never refused: a viewer mid-crossing can be honestly stale.
 *
 * A CHILD agent (the viewer's neighbour-region provision) has a position that is not inside this
 * region, so position derivation finds no parcel. A child uses the client's id: if
 * sent, it selects the parcel and every check runs against it; no id (the stock viewer sends none)
 * means no parcel checks and the service's estate room (-999). The caller then checks the named parcel
 * against the child's known position (ChildParcelProximity); a parcel that is not near it, or that does
 * not exist, is dropped with RejectHint and the child gets the estate room. Voice is never refused for that.
 *
 * Pure and Scene-free so the decision unit-tests without a Scene.
 */

namespace osWebRtcVoice
{
    public enum ParcelSource
    {
        /// <summary>Root agent: the parcel under the avatar's position.</summary>
        ServerPosition,
        /// <summary>Child agent that sent parcel_local_id: the client's parcel.</summary>
        ClientHint,
        /// <summary>Child agent with no parcel_local_id: no parcel checks, estate room.</summary>
        None,
    }

    public readonly record struct ParcelResolveInput(int? ClientLocalId, bool IsChildAgent);

    /// <param name="Source">Where the parcel comes from.</param>
    /// <param name="LocalId">The parcel to check and room against; null for <see cref="ParcelSource.None"/>,
    /// or for a root agent whose position resolved to no parcel (a refusal).</param>
    /// <param name="ServerLocalId">The position-derived parcel (root only; null otherwise).</param>
    /// <param name="HintRejected">True when a child agent's parcel_local_id was dropped because the parcel is not
    /// near the child's known position (see <see cref="ProvisionParcelResolver.RejectHint"/>).</param>
    public readonly record struct ParcelResolution(ParcelSource Source, int? LocalId, int? ServerLocalId, bool ClientMismatch, bool ClientAbsent, bool HintRejected = false);

    public static class ProvisionParcelResolver
    {
        /// <summary>Whether the handler should look the parcel up by the avatar's position (root agents only).</summary>
        public static bool DerivesFromPosition(ParcelResolveInput input) => !input.IsChildAgent;

        /// <param name="serverLocalId">The parcel under the avatar's position, or null when none was found
        /// or none was looked up (a child agent). Ignored for a child agent.</param>
        public static ParcelResolution Resolve(ParcelResolveInput input, int? serverLocalId)
        {
            int? client = input.ClientLocalId;

            if (input.IsChildAgent)
            {
                return client is null
                    ? new ParcelResolution(ParcelSource.None, LocalId: null, ServerLocalId: null, ClientMismatch: false, ClientAbsent: true)
                    : new ParcelResolution(ParcelSource.ClientHint, LocalId: client, ServerLocalId: null, ClientMismatch: false, ClientAbsent: false);
            }

            return new ParcelResolution(
                Source: ParcelSource.ServerPosition,
                LocalId: serverLocalId,
                ServerLocalId: serverLocalId,
                ClientMismatch: client is not null && serverLocalId is not null && client != serverLocalId,
                ClientAbsent: client is null);
        }

        /// <summary>A child agent's hinted parcel failed the proximity check: treat the request as if it had sent no
        /// id, so it gets the estate room. Any other resolution is returned unchanged.</summary>
        public static ParcelResolution RejectHint(ParcelResolution resolution)
            => resolution.Source != ParcelSource.ClientHint
                ? resolution
                : new ParcelResolution(ParcelSource.None, LocalId: null, ServerLocalId: null, ClientMismatch: false, ClientAbsent: false, HintRejected: true);

        /// <summary>Root-agent form: the server-derived parcel is always known.</summary>
        public static ParcelResolution Resolve(int? clientLocalId, int serverLocalId)
            => Resolve(new ParcelResolveInput(clientLocalId, IsChildAgent: false), serverLocalId);
    }
}
