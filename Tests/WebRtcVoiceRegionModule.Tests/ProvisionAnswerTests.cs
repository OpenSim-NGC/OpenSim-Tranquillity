/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * How the region module answers a "local" voice provision, driven through its capability handler on a real Scene
 * with a recording voice service in place of the gateway:
 *   - a capacity rejection from the mixer (error_code 495) is answered HTTP 409, every other failure map HTTP 200;
 *   - a refused provision is answered again from a cache for RefusalCacheSeconds without repeating the checks, the
 *     cache is cleared by a logout, and 0 turns it off.
 */

using System.Collections.Generic;
using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionAnswerTests
    {
        private static readonly UUID Owner = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Visitor = new UUID("b0b00000-0000-4000-8000-000000000002");

        // Records the requests that reach it and answers with the map a test sets.
        private sealed class RecordingVoiceService : IWebRtcVoiceService
        {
            public readonly List<OSDMap> Requests = new();
            public OSDMap Answer = new OSDMap { { "jsep", new OSDMap() }, { "viewer_session", "vs" } };

            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene)
            {
                Requests.Add(pRequest);
                return Answer;
            }
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession s, OSDMap r, UUID u, UUID sc) => new OSDMap();
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession s, OSDMap r, UUID u, UUID sc) => new OSDMap();
            public IVoiceViewerSession CreateViewerSession(OSDMap r, UUID u, UUID sc) => null;
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;
        }

        private sealed class Rig
        {
            public Scene Scene;
            public WebRtcVoiceRegionModule Module;
            public RecordingVoiceService Service = new();
        }

        private static Rig NewRig(string refusalCacheSeconds = null)
        {
            var rig = new Rig();
            rig.Scene = new SceneHelpers().SetupScene();
            var land = new LandManagementModule();
            SceneHelpers.SetupSceneModules(rig.Scene, land);   // provides the scene's land channel
            ILandObject whole = new LandObject(Owner, false, rig.Scene);   // one parcel over the whole region, voice on
            whole.SetLandBitmap(whole.GetSquareLandBitmap(0, 0, 256, 256));
            whole = land.AddLandObject(whole);
            whole.LandData.Flags |= (uint)ParcelFlags.AllowVoiceChat;
            rig.Scene.RegionInfo.EstateSettings.AllowVoice = true;
            rig.Scene.RegisterModuleInterface<IWebRtcVoiceService>(rig.Service);
            SceneHelpers.AddScenePresence(rig.Scene, Visitor);

            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            if (refusalCacheSeconds is not null)
                voice.Set("RefusalCacheSeconds", refusalCacheSeconds);
            rig.Module = new WebRtcVoiceRegionModule();
            rig.Module.Initialise(config);
            return rig;
        }

        private static OSDMap LocalBody() => new OSDMap
        {
            ["channel_type"] = OSD.FromString("local"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
            ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
        };

        private static OSDMap LogoutBody() => new OSDMap
        {
            ["logout"] = OSD.FromBoolean(true),
            ["viewer_session"] = OSD.FromString("vs"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
        };

        private static int Send(Rig rig, OSDMap body)
        {
            var resp = new TestOSHttpResponse();
            rig.Module.ProvisionVoiceAccountRequest(new LlsdPost(body), resp, Visitor, rig.Scene);
            return resp.StatusCode;
        }

        [Test]
        public void ACapacityRejection_IsAnswered409()
        {
            Rig rig = NewRig();
            rig.Service.Answer = new OSDMap { { "response", "failed" }, { "error", "room is full" }, { "error_code", WebRtcJanusService.JANUS_ROOM_FULL_ERROR_CODE } };

            Assert.That(Send(rig, LocalBody()), Is.EqualTo((int)HttpStatusCode.Conflict));
        }

        [Test]
        public void AnyOtherFailure_KeepsItsOkStatus()
        {
            Rig rig = NewRig();
            rig.Service.Answer = new OSDMap { { "response", "failed" }, { "error", "JoinRoom failed" } };

            Assert.That(Send(rig, LocalBody()), Is.EqualTo((int)HttpStatusCode.OK));
        }

        [Test]
        public void ARefusal_IsAnsweredAgainFromTheCache_WithoutRepeatingTheChecks()
        {
            Rig rig = NewRig();
            rig.Scene.RegionInfo.EstateSettings.AllowVoice = false;
            Assert.That(Send(rig, LocalBody()), Is.EqualTo((int)HttpStatusCode.NotImplemented), "refused: voice is off for the estate");

            rig.Scene.RegionInfo.EstateSettings.AllowVoice = true;   // the checks would now pass

            Assert.That(Send(rig, LocalBody()), Is.EqualTo((int)HttpStatusCode.NotImplemented), "the retry gets the cached refusal");
            Assert.That(rig.Service.Requests, Is.Empty, "the voice service was not called");
        }

        [Test]
        public void ALogout_ClearsTheCachedRefusal()
        {
            Rig rig = NewRig();
            rig.Scene.RegionInfo.EstateSettings.AllowVoice = false;
            Send(rig, LocalBody());
            rig.Scene.RegionInfo.EstateSettings.AllowVoice = true;

            Send(rig, LogoutBody());

            Assert.That(Send(rig, LocalBody()), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(rig.Service.Requests, Has.Count.GreaterThanOrEqualTo(1), "the provision reached the voice service");
        }

        [Test]
        public void AZeroWindow_TurnsTheCacheOff()
        {
            Rig rig = NewRig(refusalCacheSeconds: "0");
            rig.Scene.RegionInfo.EstateSettings.AllowVoice = false;
            Assert.That(Send(rig, LocalBody()), Is.EqualTo((int)HttpStatusCode.NotImplemented));

            rig.Scene.RegionInfo.EstateSettings.AllowVoice = true;

            Assert.That(Send(rig, LocalBody()), Is.EqualTo((int)HttpStatusCode.OK), "the checks ran again");
        }
    }
}
