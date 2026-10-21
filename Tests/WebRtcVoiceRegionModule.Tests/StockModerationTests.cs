/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Parcel voice moderation on Janus's stock AudioBridge, which has no per-listener visibility, so a mute is the
 * plugin's own mute of one participant.
 *
 * The request answers 200 only when the voice server did what was asked. A voice server that cannot mute a
 * participant (no such interface, or a plugin version without the request) gets a 501, and nothing is recorded, so the
 * record never says something the mixer does not do. A mute-everyone is recorded per parcel, so it holds when the room is
 * empty and applies to whoever joins after.
 *
 * M1-M9 run the region module's capability handler against a voice service that records what it was asked to mute.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
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
    public class StockModerationTests
    {
        private static readonly UUID Owner = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Bob = new UUID("b0b00000-0000-4000-8000-000000000002");
        private static readonly UUID Carol = new UUID("ca401000-0000-4000-8000-000000000003");
        private static readonly UUID Dave = new UUID("da7e0000-0000-4000-8000-000000000004");

        private static readonly Vector3 InWest = new Vector3(64, 128, 25);
        private static readonly Vector3 InEast = new Vector3(192, 128, 25);

        private sealed class FakeMuteService : IWebRtcVoiceService, ISpatialParticipantMute
        {
            public readonly List<(UUID Agent, bool Muted)> Calls = new();
            public ParticipantMuteResult Answer = ParticipantMuteResult.Applied;

            public ParticipantMuteResult SetSpatialMute(UUID pSceneID, UUID pAgentID, bool pMuted)
            {
                lock (Calls) Calls.Add((pAgentID, pMuted));
                return Answer;
            }

            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;
        }

        // A service that cannot mute a participant: it does not implement ISpatialParticipantMute.
        private sealed class PlainService : IWebRtcVoiceService
        {
            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;
        }

        private Scene _scene;
        private WebRtcVoiceRegionModule _module;

        private void Build(IWebRtcVoiceService service, bool stock = true)
        {
            _scene = new SceneHelpers().SetupScene();
            var land = new LandManagementModule();
            SceneHelpers.SetupSceneModules(_scene, land);
            _scene.Permissions.OnIsAdministrator += _ => false;
            AddParcel(land, 0, 0, 128, 256);
            AddParcel(land, 128, 0, 256, 256);
            if (service is not null)
                _scene.RegisterModuleInterface<IWebRtcVoiceService>(service);
            _module = new WebRtcVoiceRegionModule();
            Set("m_moderationEnabled", true);
            Set("m_stockPlugin", stock);
            Avatar(Owner, InWest);
        }

        private void AddParcel(LandManagementModule land, int x0, int y0, int x1, int y1)
        {
            ILandObject lo = new LandObject(Owner, false, _scene);
            lo.SetLandBitmap(lo.GetSquareLandBitmap(x0, y0, x1, y1));
            lo = land.AddLandObject(lo);
            lo.LandData.Flags |= (uint)ParcelFlags.AllowVoiceChat;
        }

        private void Avatar(UUID id, Vector3 position)
        {
            ScenePresence sp = SceneHelpers.AddScenePresence(_scene, id);
            sp.AbsolutePosition = position;
        }

        private void Set(string field, object value)
            => typeof(WebRtcVoiceRegionModule).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_module, value);

        private int Moderate(UUID moderator, string operand, UUID? target = null)
        {
            var body = new OSDMap { ["operand"] = OSD.FromString(operand) };
            if (target.HasValue)
                body["agent_id"] = OSD.FromUUID(target.Value);
            var resp = new TestOSHttpResponse();
            _module.SpatialVoiceModerationRequest(new LlsdPost(body), resp, moderator, _scene);
            return resp.StatusCode;
        }

        private void JoinedSpatialRoom(UUID agent)
        {
            MethodInfo m = typeof(WebRtcVoiceRegionModule).GetMethod("ApplyStockMuteOnJoin", BindingFlags.Instance | BindingFlags.NonPublic);
            m.Invoke(_module, new object[] { agent, _scene });
        }

        [Test]
        public void M1_AMuteIsAppliedToTheParticipant_AndAnswers200()
        {
            var svc = new FakeMuteService();
            Build(svc);
            Avatar(Bob, InWest);

            int status = Moderate(Owner, "mute", Bob);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(svc.Calls, Is.EqualTo(new[] { (Bob, true) }));
        }

        [Test]
        public void M2_AnUnmuteLiftsIt()
        {
            var svc = new FakeMuteService();
            Build(svc);
            Avatar(Bob, InWest);
            Moderate(Owner, "mute", Bob);

            int status = Moderate(Owner, "unmute", Bob);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(svc.Calls.Last(), Is.EqualTo((Bob, false)));
            JoinedSpatialRoom(Bob);
            Assert.That(svc.Calls.Count, Is.EqualTo(2), "and a later join is not muted");
        }

        [Test]
        public void M3_APluginThatCannotMute_GetsA501_AndNothingIsRecorded()
        {
            var svc = new FakeMuteService { Answer = ParticipantMuteResult.Unsupported };
            Build(svc);
            Avatar(Bob, InWest);

            int status = Moderate(Owner, "mute", Bob);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotImplemented));
            svc.Answer = ParticipantMuteResult.Applied;
            svc.Calls.Clear();
            JoinedSpatialRoom(Bob);
            Assert.That(svc.Calls, Is.Empty, "the refused mute was not recorded, so a join is not muted");
        }

        [Test]
        public void M4_AVoiceServiceWithNoParticipantMute_GetsA501()
        {
            Build(new PlainService());
            Avatar(Bob, InWest);

            Assert.That(Moderate(Owner, "mute", Bob), Is.EqualTo((int)HttpStatusCode.NotImplemented));
        }

        [Test]
        public void M5_AGatewayThatCannotBeAsked_GetsA503()
        {
            var svc = new FakeMuteService { Answer = ParticipantMuteResult.Failed };
            Build(svc);
            Avatar(Bob, InWest);

            Assert.That(Moderate(Owner, "mute", Bob), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void M6_MuteAll_MutesWhoeverIsOnTheParcel_ButNotTheModerator_NorOtherParcels()
        {
            var svc = new FakeMuteService();
            Build(svc);
            Avatar(Bob, InWest);
            Avatar(Carol, InWest);
            Avatar(Dave, InEast);

            int status = Moderate(Owner, "mute_all");

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            var muted = svc.Calls.Where(c => c.Muted).Select(c => c.Agent).ToList();
            Assert.That(muted, Is.EquivalentTo(new[] { Bob, Carol }));
            Assert.That(svc.Calls.Select(c => c.Agent), Does.Not.Contain(Owner).And.Not.Contain(Dave));
        }

        [Test]
        public void M7_MuteAll_HoldsWithNobodyInTheRoom_AndAppliesToWhoeverJoinsLater()
        {
            var svc = new FakeMuteService();
            Build(svc);

            Assert.That(Moderate(Owner, "mute_all"), Is.EqualTo((int)HttpStatusCode.OK), "nobody else is here");
            Avatar(Bob, InWest);
            JoinedSpatialRoom(Bob);

            Assert.That(svc.Calls, Is.EqualTo(new[] { (Bob, true) }));

            Moderate(Owner, "unmute_all");
            Assert.That(svc.Calls.Last(), Is.EqualTo((Bob, false)), "unmute_all lifts it for the one who is there");
            svc.Calls.Clear();
            JoinedSpatialRoom(Bob);
            Assert.That(svc.Calls, Is.Empty, "and a later join is not muted");
        }

        [Test]
        public void M8_ANonModerator_IsRefused_AndNothingIsMuted()
        {
            var svc = new FakeMuteService();
            Build(svc);
            Avatar(Bob, InWest);
            Avatar(Carol, InWest);

            int status = Moderate(Bob, "mute", Carol);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(svc.Calls, Is.Empty);
        }

        [Test]
        public void M9_NotOnTheStockPlugin_WithNoFeeder_TheAnswerIsStillA501()
        {
            var svc = new FakeMuteService();
            Build(svc, stock: false);
            Avatar(Bob, InWest);

            Assert.That(Moderate(Owner, "mute", Bob), Is.EqualTo((int)HttpStatusCode.NotImplemented));
            Assert.That(svc.Calls, Is.Empty, "the participant mute is for the stock plugin only");
        }

        [Test]
        public void M10_AnAgentNotInVoiceYet_IsRecorded_AndMutedWhenItJoins()
        {
            var svc = new FakeMuteService { Answer = ParticipantMuteResult.NotInRoom };
            Build(svc);
            Avatar(Bob, InWest);

            Assert.That(Moderate(Owner, "mute", Bob), Is.EqualTo((int)HttpStatusCode.OK), "nothing to mute yet is not a failure");
            svc.Calls.Clear();
            svc.Answer = ParticipantMuteResult.Applied;
            JoinedSpatialRoom(Bob);

            Assert.That(svc.Calls, Is.EqualTo(new[] { (Bob, true) }));
        }
    }
}
