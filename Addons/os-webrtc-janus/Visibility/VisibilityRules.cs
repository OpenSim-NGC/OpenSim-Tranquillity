/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The exclusion predicate: may listener L hear source S? All inputs are sim-authoritative
 * (never viewer-supplied). This is the SINGLE place the visibility rules live, so the same
 * output drives both future audio exclusion and roster/join omission (semantics doc 1).
 */

using OpenMetaverse;

namespace osWebRtcVoice
{
    public static class VisibilityRules
    {
        /// True if listener must NOT hear source. Order: voice-enable, estate ban, voice
        /// moderation (source-side), parcel ban/restrict (symmetric), SeeAVs hide (symmetric).
        public static bool IsExcluded(
            in AgentView listener, in ParcelView listenerParcel,
            in AgentView source, in ParcelView sourceParcel,
            in EstateView estate)
        {
            // (1) Voice enable. If the source's voice is off, it is audible to no one.
            //     The estate's TaxFree flag does not switch a voice-off parcel back on.
            if (!SourceVoiceAudible(sourceParcel, estate))
                return true;

            // (2) Estate ban - symmetric: an estate-banned party neither hears nor is heard.
            if (estate.IsEstateBanned(source.Id) || estate.IsEstateBanned(listener.Id))
                return true;

            // (2b) MOVED OUT of the exclusion result. Voice moderation is now a SEPARATE channel
            //      (IsModerationMuted below): a moderated source STAYS in the roster, greyed, rather
            //      than being removed like a ban. This method (IsExcluded) therefore returns ONLY
            //      ban/visibility exclusions - the class the mixer turns into a leave/removal. See
            //      IsModerationMuted for the mute class and VisibilityMatrix.Build for the routing
            //      (ban wins over mute: a source excluded here is NOT offered to the mute set).

            // (3) Parcel ban/restrict - SYMMETRIC (#13 fix): excluded if the source is barred
            //     from the listener's parcel OR the listener is barred from the source's parcel.
            //     Membership-based (position-independent) and, in production, TaxFree-independent
            //     (Decision 2b). Exemptions stay inside the delegate.
            if (sourceParcel.ExcludesByBan(listener.Id) || listenerParcel.ExcludesByBan(source.Id))
                return true;

            // (4) SeeAVs parcel hide - SYMMETRIC for voice (Decision 1b).
            if (SeeAvsHidesSymmetric(listener, listenerParcel, source, sourceParcel))
                return true;

            return false;
        }

        /// True if this SOURCE is moderation-muted on its CURRENT parcel (rule 2b, source-side).
        /// SEPARATE from IsExcluded by decision (LL-parity, Option A): a moderation mute silences the
        /// source and greys its roster row but does NOT remove it, so it travels in its own mute
        /// channel, not the exclusion set. Reads only the source and the source's parcel; exemptions
        /// (owner / estate manager / group moderator) live inside the delegate, like the ban delegate.
        /// PARCEL-STICKY, not avatar-sticky (mute-everyone is parcel state) — matches SL.
        ///
        /// Precedence is the CALLER's job (VisibilityMatrix.Build): a source already excluded by ban/
        /// visibility must NOT also be offered to the mute set — removal is stricter than silence.
        public static bool IsModerationMuted(in AgentView source, in ParcelView sourceParcel)
            => sourceParcel.ExcludesByModeration(source.Id);

        /// voice ON for a source's parcel: estate master AND parcel AllowVoiceChat.
        internal static bool SourceVoiceAudible(in ParcelView sourceParcel, in EstateView estate)
            => estate.AllowVoice && sourceParcel.AllowVoiceChat;

        // ---- SeeAVs symmetric-hiding predicate --------------------------------------------
        //
        // AMBIGUITY / PENDING SL VERIFICATION (Decision 1b; see parcel-voice-semantics.md
        // addendum F):
        //   The *visual* SeeAVs model is ONE-WAY at the source - an occupant of a SeeAVs=false
        //   parcel is hidden from avatars on OTHER parcels, but those outsiders stay VISIBLE to
        //   the occupant, because ParcelHideThisAvatar derives from the SENDER's parcel
        //   (baseline 1.2). For VOICE we deliberately apply it SYMMETRICALLY so a listener never
        //   hears someone they cannot see. Whether stock SL voice hides symmetrically is not yet
        //   confirmed in-world. If SL proves one-way, delete the second disjunct
        //   (listenerHiddenFromSource) and this becomes the source-only rule.
        //
        //   God: IsViewerUIGod exempts the hide for the RECEIVER. Under symmetry a god standing
        //   ON a hidden parcel is still excluded from outsiders via the second disjunct. Left
        //   literal pending review (no god scenario is in the test set).
        internal static bool SeeAvsHidesSymmetric(
            in AgentView listener, in ParcelView listenerParcel,
            in AgentView source, in ParcelView sourceParcel)
        {
            if (listenerParcel.GlobalId == sourceParcel.GlobalId)
                return false;   // same parcel: occupants always see each other

            bool sourceHiddenFromListener = !sourceParcel.SeeAVs && !listener.IsGod;
            bool listenerHiddenFromSource = !listenerParcel.SeeAVs && !source.IsGod;
            return sourceHiddenFromListener || listenerHiddenFromSource;
        }
    }
}
