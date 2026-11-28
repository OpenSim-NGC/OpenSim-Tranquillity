/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Integration tests for FeederWorldFromScene + VoiceVisibilityService against a real Scene
 * (SceneHelpers). These exercise the adapter's Scene-COUPLED surface:
 *   - LandChannel -> ParcelView mapping (SeeAVs / AllowVoiceChat flags),
 *   - the ban delegate reading real LandObject state (the §E void fix, Decision 2b),
 *   - the sim-event -> dirty-flag wiring.
 *
 * They deliberately do NOT create ScenePresences: this tree's ScenePresence finalizer throws an
 * NRE during GC and crashes the test host, a pre-existing harness fragility. The full
 * presence -> matrix path (symmetric exclusion across avatars) is covered deterministically by the
 * engine BanScenario test (WebRtcJanusService.Tests) and by the in-world DEBUG smoke check.
 *
 * Note: an empty test Scene defaults to god-mode permissions (Scene.Permissions.IsAdministrator
 * returns true when no handler is registered), which would EXEMPT every ban. The ban test
 * registers a deny-all IsAdministrator handler so the ban is actually evaluated.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class FeederWorldFromSceneTests
    {
        private static readonly UUID Owner = new UUID("00000000-0000-0000-0000-0000000000f1");
        private static readonly UUID Banned = new UUID("00000000-0000-0000-0000-00000000000a");
        private static readonly UUID Other = new UUID("00000000-0000-0000-0000-00000000000b");

        private static Scene NewSceneWithLand(out LandManagementModule lmm)
        {
            Scene scene = new SceneHelpers().SetupScene();
            lmm = new LandManagementModule();
            SceneHelpers.SetupSceneModules(scene, lmm);
            scene.Permissions.OnIsAdministrator += _ => false;   // non-admin so bans are not exempted
            return scene;
        }

        private static ILandObject AddWholeRegionParcel(Scene scene, LandManagementModule lmm)
        {
            ILandObject lo = new LandObject(Owner, false, scene);
            lo.SetLandBitmap(lo.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            return lmm.AddLandObject(lo);
        }

        // The ban delegate on the adapter's ParcelView reports a real parcel ban (and only for the
        // banned avatar) - reading live LandObject state through the Scene. Covers the §E fix path
        // regardless of the estate TaxFree flag (parity branch or the TaxFree-bypass branch).
        [Fact(Timeout = 60000)]
        public void RealScene_ParcelBanDelegate_DetectsBanFromLandObjectState()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = NewSceneWithLand(out LandManagementModule lmm);
            ILandObject parcel = AddWholeRegionParcel(scene, lmm);
            parcel.LandData.Flags |= (uint)ParcelFlags.UseBanList;
            parcel.LandData.ParcelAccessList.Add(
                new LandAccessEntry { AgentID = Banned, Flags = AccessList.Ban, Expires = 0 });

            var world = new FeederWorldFromScene(scene);
            ParcelView byPos = world.GetParcelAt(new Vector3(128, 128, 25));
            ParcelView byId = world.GetParcelByGlobalId(parcel.LandData.GlobalID);

            {
                var assertedValue = byPos.GlobalId;
                TestAssert.WithMessage(() => Assert.Equal(parcel.LandData.GlobalID, assertedValue), "GetParcelAt resolves the parcel");
            }
            {
                var assertedValue = byId.GlobalId;
                TestAssert.WithMessage(() => Assert.Equal(parcel.LandData.GlobalID, assertedValue), "GetParcelByGlobalId resolves the parcel");
            }
            Assert.True(byId.IsBannedFromLand(Banned), "ban delegate detects the banned avatar");
            Assert.False(byId.IsBannedFromLand(Other), "non-banned avatar is not flagged");
        }

        // ParcelView reflects the SeeAVs and AllowVoiceChat flags read off real LandData.
        [Fact(Timeout = 60000)]
        public void RealScene_ParcelView_MapsSeeAvsAndVoiceFlag()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = NewSceneWithLand(out LandManagementModule lmm);
            ILandObject parcel = AddWholeRegionParcel(scene, lmm);
            parcel.LandData.SeeAVs = false;
            parcel.LandData.Flags &= ~(uint)ParcelFlags.AllowVoiceChat;   // voice off on this parcel

            ParcelView pv = new FeederWorldFromScene(scene).GetParcelByGlobalId(parcel.LandData.GlobalID);

            Assert.False(pv.SeeAVs, "SeeAVs mapped from LandData");
            Assert.False(pv.AllowVoiceChat, "AllowVoiceChat mapped from LandData.Flags");
        }

        // Unknown/absent parcel resolves to the benign default (visible, voice-on, no bans).
        [Fact(Timeout = 60000)]
        public void RealScene_UnknownParcel_IsBenign()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = NewSceneWithLand(out _);   // no parcel added
            ParcelView pv = new FeederWorldFromScene(scene).GetParcelByGlobalId(UUID.Random());

            Assert.True(pv.SeeAVs, "unknown parcel must not hide");
            Assert.True(pv.AllowVoiceChat, "unknown parcel must not mute");
        }

        // Event -> dirty wiring: a land change flips the feeder's pending-invalidation flag
        // (WireEvents only, so no running loop consumes it). No presences created.
        [Fact(Timeout = 60000)]
        public void EventWiring_LandChange_SetsPendingInvalidation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            var svc = new VoiceVisibilityService(scene, 250);
            svc.WireEvents();

            Assert.False(svc.HasPendingInvalidation);
            scene.EventManager.TriggerLandObjectRemoved(UUID.Random());
            Assert.True(svc.HasPendingInvalidation, "OnLandObjectRemoved must mark the feeder dirty");
        }
    }
}
