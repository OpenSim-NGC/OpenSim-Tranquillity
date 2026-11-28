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
    public class VisAuthorityRoomDeclarationTests
    {
        private static OSDMap Body(AudioBridgeCreateRoomReq req)
        {
            req.ToJson();
            return (OSDMap)req.RawBody["body"];
        }

        [Fact(Timeout = 60000)]
        public void CreateBody_WithoutDeclaration_HasNoVisAuthorityKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(Body(new AudioBridgeCreateRoomReq(7, true, "d")).ContainsKey("vis_authority"));
            Assert.False(Body(new AudioBridgeCreateRoomReq(7, true, "d", false)).ContainsKey("vis_authority"));
        }

        [Fact(Timeout = 60000)]
        public void CreateBody_Declared_CarriesVisAuthorityTrue_AfterEveryOtherKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap body = Body(new AudioBridgeCreateRoomReq(7, true, "d", true));
            Assert.True(body["vis_authority"].AsBoolean());
            // The declaration is appended after every key the body has without arming.
            Assert.EndsWith("\"description\":\"d\",\"vis_authority\":true}", OSDParser.SerializeJsonString(body, true));
        }

        [Fact(Timeout = 60000)]
        public void CreateBody_IsNotMarkedSimCreated_UnlessAskedTo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(Body(new AudioBridgeCreateRoomReq(7)).ContainsKey("sim_created"));
            Assert.False(Body(new AudioBridgeCreateRoomReq(7, true, "d", true)).ContainsKey("sim_created"));
        }

        [Fact(Timeout = 60000)]
        public void CreateBody_MarkedSimCreated_IsIndependentOfTheDeclaration()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A room can be marked and undeclared (a call), or marked and declared (a spatial parcel room). The two
            // keys answer different questions: who arms the room, and who owns it. A mixer that requires join
            // capabilities keys on the second, so a call's room is gated too.
            var undeclared = new AudioBridgeCreateRoomReq(7, false, "call", false);
            undeclared.SetSimCreated();
            OSDMap body = Body(undeclared);
            Assert.False(body.ContainsKey("vis_authority"));
            Assert.True(body["sim_created"].AsBoolean());

            var declared = new AudioBridgeCreateRoomReq(7, true, "local", true);
            declared.SetSimCreated();
            body = Body(declared);
            Assert.True(body["vis_authority"].AsBoolean());
            Assert.True(body["sim_created"].AsBoolean());
        }

        [Fact(Timeout = 60000)]
        public void OnlySpatialLocalRooms_AreDeclared_AndOnlyWhenArming()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(JanusAudioBridge.ShouldDeclareVisAuthority(true, "local"));
            Assert.False(JanusAudioBridge.ShouldDeclareVisAuthority(true, "multiagent"), "A2A rooms get no batches");
            Assert.False(JanusAudioBridge.ShouldDeclareVisAuthority(false, "local"));
        }
    }
}
