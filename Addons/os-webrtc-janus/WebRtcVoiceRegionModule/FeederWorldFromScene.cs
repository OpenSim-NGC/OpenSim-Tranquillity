/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Binds the Scene-free IFeederWorld (Addons/os-webrtc-janus/Visibility) to a live
 * Scene / LandChannel / EstateSettings. This is the ONLY Scene-coupled part of the feeder;
 * it is exercised by the real-scene integration test and the in-world DEBUG smoke check, not
 * the pure engine unit tests.
 *
 * The parcel ban/restrict delegates read ACTUAL LandObject state on each call (this is where
 * VoiceStateFeeder.Tick's derivation hardening earns its keep — a live ParcelAccessList can
 * mutate mid-scan).
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public sealed class FeederWorldFromScene : IFeederWorld, IPositionWorld
    {
        // Unknown/absent parcel: benign (visible, voice-on, no bans) so an unresolved parcel
        // never spuriously hides or mutes. SeeAVs=true is important - default(ParcelView) would
        // be SeeAVs=false and hide everyone.
        private static readonly ParcelView UnknownParcel =
            new ParcelView(UUID.Zero, seeAVs: true, allowVoiceChat: true, null, null);

        private readonly Scene m_scene;
        // Voice moderation state (in-memory, per-region). Optional: the real-scene tests
        // construct this adapter without it, in which case no source is ever moderated.
        private readonly VoiceModerationStore m_moderation;

        public FeederWorldFromScene(Scene scene, VoiceModerationStore moderation = null)
        {
            m_scene = scene;
            m_moderation = moderation;
        }

        public IReadOnlyList<AgentView> SnapshotAgents()
        {
            var agents = new List<AgentView>();
            UUID regionId = m_scene.RegionInfo.RegionID;
            // ForEachScenePresence copies each present avatar's fields into our list here (the
            // "copied snapshot"). If the enumeration itself throws, Tick() catches it and keeps the
            // last matrix.
            //
            // The IsAgentInRegion gate admits a presence only if it holds a voice session in THIS
            // region. Two constraints drive this shape:
            //   1. We must NEVER iterate VoiceViewerSession.ViewerSessions to build the matrix: that
            //      registry leaks unbounded over process uptime (ghost sessions that never get
            //      removed accumulate), so any scan of it is an O(leak) cost that grows without
            //      bound and would drag every tick down as the process ages.
            //   2. So the filter is anchored to presences, not to sessions: we walk the live scene
            //      population (bounded by avatars actually here) and ask the index an O(1) question
            //      per presence. A ghost/stale index entry for an agent that has left the region
            //      matches no presence and contributes nothing, so the matrix stays bounded by
            //      population no matter how much the registry has leaked.
            // RegionId-scoped is deliberate: a child agent of an adjacent region shows up in this
            // scene but is voiced in its home region, not here, so it is correctly excluded rather
            // than emitted as a spurious column the mixer would have to drop.
            m_scene.ForEachScenePresence(sp =>
            {
                if (!VoiceViewerSession.IsAgentInRegion(regionId, sp.UUID))
                    return;
                agents.Add(new AgentView(
                    sp.UUID, sp.IsChildAgent, sp.AbsolutePosition, sp.currentParcelUUID, sp.IsViewerUIGod));
            });
            return agents;
        }

        /// <summary>Where every presence that holds a voice session in this region is, in global centimetres, with the
        /// sim's own speed for it. Same population as <see cref="SnapshotAgents"/>. A child agent's position is the one
        /// its root region last sent across (already in this region's frame), and its velocity is not kept, so none is
        /// given for it.</summary>
        public IReadOnlyList<PositionView> SnapshotPositions()
        {
            var views = new List<PositionView>();
            UUID regionId = m_scene.RegionInfo.RegionID;
            uint worldLocX = m_scene.RegionInfo.WorldLocX;
            uint worldLocY = m_scene.RegionInfo.WorldLocY;
            m_scene.ForEachScenePresence(sp =>
            {
                if (!VoiceViewerSession.IsAgentInRegion(regionId, sp.UUID))
                    return;
                Vector3? seatedOn = null;
                if (!sp.IsChildAgent && sp.IsSatOnObject)
                {
                    SceneObjectPart root = sp.ParentPart?.ParentGroup?.RootPart;
                    if (root != null)
                        seatedOn = root.Velocity;
                }
                views.Add(ToPositionView(sp.UUID, sp.IsChildAgent, worldLocX, worldLocY, sp.AbsolutePosition,
                    sp.IsChildAgent ? Vector3.Zero : sp.Velocity, seatedOn));
            });
            return views;
        }

        /// <summary>One presence as the position feed sees it: its position in the global centimetres a viewer reports its
        /// own position in, and its speed from the avatar's velocity or that of what it sits on.</summary>
        public static PositionView ToPositionView(UUID id, bool isChild, uint worldLocX, uint worldLocY,
            Vector3 position, Vector3 velocity, Vector3? seatedOnVelocity)
        {
            (int x, int y, int z) = GlobalCentimetres(worldLocX, worldLocY, position);
            return new PositionView(id, isChild, x, y, z, PositionTolerance.SpeedCmS(velocity, seatedOnVelocity));
        }

        /// <summary>A position in EXACTLY the frame a viewer sends in SLData: global centimetres as integers,
        /// (region global origin + region-local position) x 100. The viewer builds its "sp" as
        /// (int)(getPositionGlobal() * 100), and the mixer only ever uses differences, so a position in any other
        /// frame (region-local metres above all) is kilometres from every avatar. The origin is
        /// RegionInfo.WorldLocX/Y in metres, which holds for a var region too: its origin is its south-west corner.
        /// Truncation toward zero, as the viewer's (int) cast. Z has no origin.</summary>
        public static (int X, int Y, int Z) GlobalCentimetres(uint worldLocX, uint worldLocY, Vector3 position)
            => ((int)((worldLocX + (double)position.X) * 100.0),
                (int)((worldLocY + (double)position.Y) * 100.0),
                (int)((double)position.Z * 100.0));

        /// <summary>The parcel_local_id an avatar on this parcel is provisioned with: the parcel's own id, or
        /// REGION_ROOM_ID when the parcel runs the estate channel.</summary>
        internal static int ParcelLocalIdFor(LandData land)
            => land is null || (land.Flags & (uint)ParcelFlags.UseEstateVoiceChan) != 0
                ? JanusAudioBridge.REGION_ROOM_ID
                : land.LocalID;

        public ParcelView GetParcelAt(Vector3 position)
            => ToParcelView(m_scene.LandChannel?.GetLandObject(position.X, position.Y));

        public ParcelView GetParcelByGlobalId(UUID parcelGlobalId)
            => ToParcelView(m_scene.LandChannel?.GetLandObject(parcelGlobalId));

        public EstateView Estate
        {
            get
            {
                EstateSettings es = m_scene.RegionInfo.EstateSettings;
                return new EstateView(es.AllowVoice, es.TaxFree, a => es.IsBanned(a));
            }
        }

        private ParcelView ToParcelView(ILandObject parcel)
        {
            if (parcel == null)
                return UnknownParcel;
            LandData ld = parcel.LandData;
            if (ld == null)
                return UnknownParcel;

            bool allowVoiceChat = (ld.Flags & (uint)ParcelFlags.AllowVoiceChat) != 0;
            EstateSettings estate = m_scene.RegionInfo.EstateSettings;

            // Ban delegate — Decision 2b (ban-only void fix). When the estate is NOT TaxFree we
            // defer to the sim predicate (parity, zero drift). Under TaxFree,
            // LandObject.IsBannedFromLand returns false (defect §E: the legacy provision path at
            // WebRtcVoiceRegionModule.cs:301 never even ran on the estate channel), so we
            // re-evaluate ban-list membership minus the TaxFree short-circuit. Reads live LandData.
            //
            // The TaxFree branch is chosen HERE, once per parcel, rather than inside the closure, so
            // the two exemption delegates are built ONCE instead of on every invocation. They capture
            // only m_scene and estate — both stable for the tick — so hoisting them is semantically
            // identical. The old closure allocated both on EVERY call; since the matrix build invokes
            // the ban delegate across all pairs, that made allocation O(N^2) on the TaxFree path (the
            // earlier scaling assessment named the O(N) outer-closure churn and missed this). The
            // live-LandData contract is unchanged: the ban-list scan still runs at invocation. A
            // (parcel, avatar) result memo is deliberately NOT taken — it pays only when agents
            // cluster onto few parcels, and it would break the read-live-LandData-per-call contract.
            Func<UUID, bool> banned;
            if (estate.TaxFree)
            {
                Func<UUID, bool> isAdministrator = a => m_scene.Permissions.IsAdministrator(a);
                Func<UUID, bool> isEstateManagerOrOwner = a => estate.IsEstateManagerOrOwner(a);
                banned = avatar => LandBan.IsBanned(
                    ld, isAdministrator, isEstateManagerOrOwner, avatar, Util.UnixTimeSinceEpoch());
            }
            else
            {
                banned = parcel.IsBannedFromLand;
            }

            // Restrict delegate — DELIBERATE divergence from the ban fix: use the real predicate
            // unconditionally, so access-restriction keeps the sim's TaxFree self-nullify. The §E
            // void fix is ban-only by decision; restrict is not re-implemented here.
            // (Diverges from the legacy :301 path only for bans, not restrict.)
            Func<UUID, bool> restricted = avatar => parcel.IsRestrictedFromLand(avatar);

            // Voice moderation delegate. A source is excluded when the store says this
            // avatar is muted on THIS parcel (mute-everyone or the individual set) AND the avatar is
            // NOT itself a moderator — owner / estate manager / group moderator are exempt, via the
            // SAME composition (VoiceModerationAuth) the CAP handler authorises with. Source-side;
            // reads live store state per call. Null store (real-scene tests) => never moderated.
            Func<UUID, bool> voiceModerated = avatar =>
                m_moderation != null
                && m_moderation.IsModerated(ld.GlobalID, avatar)
                && !VoiceModerationAuth.MayModerate(m_scene, ld, avatar);

            return new ParcelView(ld.GlobalID, ld.SeeAVs, allowVoiceChat, banned, restricted, voiceModerated);
        }
    }
}
