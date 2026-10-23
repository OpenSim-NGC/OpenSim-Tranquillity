/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The parcel decisions of a "local" voice provision, driven through the region module's capability handler on a
 * real Scene with two parcels and a recording voice service in place of the gateway:
 *   - the parcel comes from where a root agent stands, the viewer's parcel_local_id is only a hint;
 *   - a neighbouring region's child agent keeps naming its parcel, but only a parcel near its known position counts;
 *   - parcel voice-off and parcel bans are honoured on every estate, TaxFree estates included;
 *   - a ban is honoured on a parcel that uses the estate voice channel.
 * The voice service sees only requests that passed these checks, so "the service was not called" is a refusal.
 */

using System.Collections.Generic;
using System.Linq;
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
    public class ProvisionParcelModuleTests
    {
        private static readonly UUID Owner = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Visitor = new UUID("b0b00000-0000-4000-8000-000000000002");

        private static readonly Vector3 InWest = new Vector3(64, 128, 25);
        private static readonly Vector3 InEast = new Vector3(192, 128, 25);

        // Records the requests that reach it and answers with a success map.
        private sealed class RecordingVoiceService : IWebRtcVoiceService
        {
            public readonly List<OSDMap> Requests = new();

            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene)
            {
                Requests.Add(pRequest);
                return new OSDMap { { "jsep", new OSDMap() }, { "viewer_session", "vs" } };
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
            public ILandObject West;
            public ILandObject East;
        }

        private static Rig NewRig(bool taxFree)
        {
            var rig = new Rig();
            rig.Scene = new SceneHelpers().SetupScene();
            var land = new LandManagementModule();
            SceneHelpers.SetupSceneModules(rig.Scene, land);
            rig.Scene.Permissions.OnIsAdministrator += _ => false;
            rig.Scene.RegionInfo.EstateSettings.AllowVoice = true;
            rig.Scene.RegionInfo.EstateSettings.TaxFree = taxFree;
            rig.West = AddParcel(rig.Scene, land, 0, 0, 128, 256, estateChannel: false);
            rig.East = AddParcel(rig.Scene, land, 128, 0, 256, 256, estateChannel: true);
            rig.Scene.RegisterModuleInterface<IWebRtcVoiceService>(rig.Service);

            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            rig.Module = new WebRtcVoiceRegionModule();
            rig.Module.Initialise(config);
            return rig;
        }

        private static ILandObject AddParcel(Scene scene, LandManagementModule land, int x0, int y0, int x1, int y1, bool estateChannel)
        {
            ILandObject lo = new LandObject(Owner, false, scene);
            lo.SetLandBitmap(lo.GetSquareLandBitmap(x0, y0, x1, y1));
            lo = land.AddLandObject(lo);
            lo.LandData.Flags |= (uint)ParcelFlags.AllowVoiceChat;
            if (estateChannel)
                lo.LandData.Flags |= (uint)ParcelFlags.UseEstateVoiceChan;
            return lo;
        }

        private static void Ban(ILandObject parcel, UUID agent)
        {
            parcel.LandData.Flags |= (uint)ParcelFlags.UseBanList;
            parcel.LandData.ParcelAccessList.Add(new LandAccessEntry { AgentID = agent, Flags = AccessList.Ban, Expires = 0 });
        }

        private static (int Status, OSDMap Sent) Provision(Rig rig, UUID agent, int? parcelLocalId)
        {
            var body = new OSDMap
            {
                ["channel_type"] = OSD.FromString("local"),
                ["voice_server_type"] = OSD.FromString("webrtc"),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
            };
            if (parcelLocalId.HasValue)
                body["parcel_local_id"] = OSD.FromInteger(parcelLocalId.Value);

            int before = rig.Service.Requests.Count;
            var resp = new TestOSHttpResponse();
            rig.Module.ProvisionVoiceAccountRequest(new LlsdPost(body), resp, agent, rig.Scene);
            OSDMap sent = rig.Service.Requests.Count > before ? rig.Service.Requests.Last() : null;
            return (resp.StatusCode, sent);
        }

        private static ScenePresence Root(Rig rig, UUID id, Vector3 at)
        {
            ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, id);
            sp.AbsolutePosition = at;
            return sp;
        }

        [Test]
        public void RootAgent_TheParcelWhereItStandsWins_OverTheOneTheViewerNames()
        {
            Rig rig = NewRig(taxFree: false);
            Root(rig, Visitor, InWest);

            // The viewer names the estate-channel parcel while standing on the west parcel.
            var (status, sent) = Provision(rig, Visitor, rig.East.LandData.LocalID);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(sent, Is.Not.Null, "the request went on to the voice service");
            Assert.That(sent["parcel_local_id"].AsInteger(), Is.EqualTo(rig.West.LandData.LocalID), "the room is the west parcel's");
        }

        [Test]
        public void RootAgent_WithNoParcelNamed_GetsTheParcelWhereItStands()
        {
            Rig rig = NewRig(taxFree: false);
            Root(rig, Visitor, InWest);

            var (status, sent) = Provision(rig, Visitor, null);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(sent["parcel_local_id"].AsInteger(), Is.EqualTo(rig.West.LandData.LocalID));
        }

        [Test]
        public void RootAgent_OnAnEstateChannelParcel_GetsNoParcelId()
        {
            Rig rig = NewRig(taxFree: false);
            Root(rig, Visitor, InEast);

            var (status, sent) = Provision(rig, Visitor, rig.West.LandData.LocalID);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(sent.ContainsKey("parcel_local_id"), Is.False, "the estate channel is the region-wide room");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ParcelWithVoiceOff_IsRefused_OnAnyEstate(bool taxFree)
        {
            Rig rig = NewRig(taxFree);
            rig.West.LandData.Flags &= ~(uint)ParcelFlags.AllowVoiceChat;
            Root(rig, Visitor, InWest);

            var (status, sent) = Provision(rig, Visitor, rig.West.LandData.LocalID);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(sent, Is.Null, "the voice service was not called");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BannedAgent_IsRefused_OnAnyEstate(bool taxFree)
        {
            Rig rig = NewRig(taxFree);
            Ban(rig.West, Visitor);
            Root(rig, Visitor, InWest);

            var (status, sent) = Provision(rig, Visitor, null);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(sent, Is.Null, "the voice service was not called");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BannedAgent_IsRefused_OnAParcelUsingTheEstateChannel(bool taxFree)
        {
            Rig rig = NewRig(taxFree);
            Ban(rig.East, Visitor);
            Root(rig, Visitor, InEast);

            var (status, sent) = Provision(rig, Visitor, null);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(sent, Is.Null, "the voice service was not called");
        }

        [Test]
        public void BanOnAnotherParcel_DoesNotStopAgentStandingElsewhere()
        {
            Rig rig = NewRig(taxFree: true);
            Ban(rig.East, Visitor);
            Root(rig, Visitor, InWest);

            var (status, sent) = Provision(rig, Visitor, rig.East.LandData.LocalID);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK), "the parcel checked is the one the agent stands on");
            Assert.That(sent, Is.Not.Null);
        }

        [Test]
        public void ParcelOwner_IsNotRefusedByItsOwnBanList()
        {
            Rig rig = NewRig(taxFree: true);
            Ban(rig.West, Owner);
            Root(rig, Owner, InWest);

            var (status, sent) = Provision(rig, Owner, null);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(sent, Is.Not.Null);
        }

        [Test]
        public void ChildAgent_WithNoParcelNamed_GetsNoParcelChecksAndTheRegionRoom()
        {
            Rig rig = NewRig(taxFree: false);
            Ban(rig.West, Visitor);
            ScenePresence child = SceneHelpers.AddChildScenePresence(rig.Scene, Visitor);
            child.AbsolutePosition = new Vector3(300, 128, 25);   // outside this region

            var (status, sent) = Provision(rig, Visitor, null);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(sent.ContainsKey("parcel_local_id"), Is.False);
        }

        [Test]
        public void ChildAgent_NamingAParcelFarFromItsKnownPosition_IsAnsweredAsIfItNamedNone()
        {
            Rig rig = NewRig(taxFree: false);
            ScenePresence child = SceneHelpers.AddChildScenePresence(rig.Scene, Visitor);
            child.AbsolutePosition = new Vector3(-200, 128, 25);   // well clear of the east parcel

            var (status, sent) = Provision(rig, Visitor, rig.East.LandData.LocalID);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK), "never a refusal for a hint that is dropped");
            Assert.That(sent.ContainsKey("parcel_local_id"), Is.False, "the dropped id is not forwarded");
        }
    }
}
