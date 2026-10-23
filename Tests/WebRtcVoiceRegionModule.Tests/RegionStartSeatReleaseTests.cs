/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A region that has just started holds no viewers, so a region keeping its call sessions in the grid service asks the
 * service to release the seats that still name that region (left by the process before it). A region keeping its own
 * sessions asks nobody.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class RegionStartSeatReleaseTests
    {
        private static void SetPrivate(object target, string field, object value)
        {
            FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(target.GetType().Name, field);
            f.SetValue(target, value);
        }

        private static void RegisterScene(WebRtcVoiceRegionModule module, Scene scene)
            => typeof(WebRtcVoiceRegionModule)
                .GetMethod("RegisterSceneForVoiceSessions", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(module, new object[] { scene });

        private static void WaitUntil(Func<bool> done)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (!done() && w.Elapsed < TimeSpan.FromSeconds(20))
                System.Threading.Thread.Yield();
        }

        [Test]
        public void AGridRegionThatStarts_ReleasesTheSeatsLeftByAnEarlierRun_AndNoOthers()
        {
            Scene scene = new SceneHelpers().SetupScene();
            UUID thisRegion = scene.RegionInfo.RegionID;
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions earlierRun = VoiceSessionServiceTests.NewRegion(service);
            UUID stale = earlierRun.StartAdhoc(Alice, UUID.Random(), thisRegion, new List<UUID> { Bob }).Session.SessionId;
            UUID other = earlierRun.StartAdhoc(Carol, UUID.Random(), RegionB).Session.SessionId;
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(service);
            WebRtcVoiceRegionModule module = new WebRtcVoiceRegionModule();
            SetPrivate(module, "m_gridSessions", VoiceSessionServiceTests.NewRegion(flaky));

            RegisterScene(module, scene);
            WaitUntil(() => earlierRun.Get(stale).SeatsHeld == 0);   // the result, not the request: the request is logged before it is carried out

            Assert.That(earlierRun.Get(stale).SeatsHeld, Is.EqualTo(0), "the seat that named this region is released");
            Assert.That(flaky.Delivered.Count(w => w == "departregion"), Is.EqualTo(1), "asked once for the region");
            Assert.That(earlierRun.Get(other).SeatsHeld, Is.EqualTo(1), "another region's seat is not");
        }

        [Test]
        public void ARegionThatKeepsItsOwnSessions_AsksNobody()
        {
            Scene scene = new SceneHelpers().SetupScene();
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(VoiceSessionServiceTests.NewService());
            WebRtcVoiceRegionModule module = new WebRtcVoiceRegionModule();   // m_gridSessions stays unset: local mode

            Assert.DoesNotThrow(() => RegisterScene(module, scene));
            System.Threading.Thread.Sleep(300);   // a window for something that must NOT happen

            Assert.That(flaky.Calls, Is.EqualTo(0));
        }

        [Test]
        public void AGridRegionWhoseServiceIsDown_StillStarts()
        {
            Scene scene = new SceneHelpers().SetupScene();
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(VoiceSessionServiceTests.NewService()) { Up = false };
            WebRtcVoiceRegionModule module = new WebRtcVoiceRegionModule();
            SetPrivate(module, "m_gridSessions", VoiceSessionServiceTests.NewRegion(flaky));

            Assert.DoesNotThrow(() => RegisterScene(module, scene));
            WaitUntil(() => flaky.Calls > 0);

            Assert.That(flaky.Calls, Is.EqualTo(1), "it asked, was not answered, and went on");
        }
    }
}
