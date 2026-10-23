/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Spatial "local" rooms are created with vis_authority=true only when the sim arms. The create
 * body is also pinned byte-for-byte by VisibilityKnobOffGoldenTests.
 */

using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisAuthorityRoomDeclarationTests
    {
        private static OSDMap Body(AudioBridgeCreateRoomReq req)
        {
            req.ToJson();
            return (OSDMap)req.RawBody["body"];
        }

        [Test]
        public void CreateBody_WithoutDeclaration_HasNoVisAuthorityKey()
        {
            Assert.That(Body(new AudioBridgeCreateRoomReq(7, true, "d")).ContainsKey("vis_authority"), Is.False);
            Assert.That(Body(new AudioBridgeCreateRoomReq(7, true, "d", false)).ContainsKey("vis_authority"), Is.False);
        }

        [Test]
        public void CreateBody_Declared_CarriesVisAuthorityTrue_AfterEveryOtherKey()
        {
            OSDMap body = Body(new AudioBridgeCreateRoomReq(7, true, "d", true));
            Assert.That(body["vis_authority"].AsBoolean(), Is.True);
            // The declaration is appended after every key the body has without arming.
            Assert.That(OSDParser.SerializeJsonString(body, true),
                Does.EndWith("\"description\":\"d\",\"vis_authority\":true}"));
        }

        [Test]
        public void CreateBody_IsNotMarkedSimCreated_UnlessAskedTo()
        {
            Assert.That(Body(new AudioBridgeCreateRoomReq(7)).ContainsKey("sim_created"), Is.False);
            Assert.That(Body(new AudioBridgeCreateRoomReq(7, true, "d", true)).ContainsKey("sim_created"), Is.False);
        }

        [Test]
        public void CreateBody_MarkedSimCreated_IsIndependentOfTheDeclaration()
        {
            // A room can be marked and undeclared (a call), or marked and declared (a spatial parcel room). The two
            // keys answer different questions: who arms the room, and who owns it. A mixer that requires join
            // capabilities keys on the second, so a call's room is gated too.
            var undeclared = new AudioBridgeCreateRoomReq(7, false, "call", false);
            undeclared.SetSimCreated();
            OSDMap body = Body(undeclared);
            Assert.That(body.ContainsKey("vis_authority"), Is.False);
            Assert.That(body["sim_created"].AsBoolean(), Is.True);

            var declared = new AudioBridgeCreateRoomReq(7, true, "local", true);
            declared.SetSimCreated();
            body = Body(declared);
            Assert.That(body["vis_authority"].AsBoolean(), Is.True);
            Assert.That(body["sim_created"].AsBoolean(), Is.True);
        }

        [Test]
        public void OnlySpatialLocalRooms_AreDeclared_AndOnlyWhenArming()
        {
            Assert.That(JanusAudioBridge.ShouldDeclareVisAuthority(true, "local"), Is.True);
            Assert.That(JanusAudioBridge.ShouldDeclareVisAuthority(true, "multiagent"), Is.False, "A2A rooms get no batches");
            Assert.That(JanusAudioBridge.ShouldDeclareVisAuthority(false, "local"), Is.False);
        }
    }
}
