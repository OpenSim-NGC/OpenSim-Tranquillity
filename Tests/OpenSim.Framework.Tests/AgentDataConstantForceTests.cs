/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework.Tests
{
    /// <summary>
    /// The constant force an attachment's llSetForce puts on an avatar travels with the avatar's agent data to the
    /// next region. The field is optional: it is written only when there is a force, and agent data without it
    /// (from a simulator that does not send it) means no force.
    /// </summary>
    // Pure packing and unpacking; no process-wide state.
    public class AgentDataConstantForceTests
    {
        private static AgentData NewData() => new AgentData
        {
            AgentID = UUID.Random(),
            RegionID = UUID.Random(),
            SessionID = UUID.Random(),
        };

        private static AgentData RoundTrip(OSDMap packed)
        {
            var back = new AgentData();
            back.Unpack(packed, null, new EntityTransferContext());
            return back;
        }

        [Fact]
        public void AForcePacksAndUnpacksWithItsFrame()
        {
            var data = NewData();
            data.ConstantForce = new Vector3(12.5f, -3f, 40f);
            data.ConstantForceIsLocal = true;

            var back = RoundTrip(data.Pack(new EntityTransferContext()));

            Assert.Equal(new Vector3(12.5f, -3f, 40f), back.ConstantForce);
            Assert.True(back.ConstantForceIsLocal);
        }

        [Fact]
        public void ARegionAxesForceKeepsItsFrame()
        {
            var data = NewData();
            data.ConstantForce = new Vector3(0, 0, -9f);
            data.ConstantForceIsLocal = false;

            var back = RoundTrip(data.Pack(new EntityTransferContext()));

            Assert.Equal(new Vector3(0, 0, -9f), back.ConstantForce);
            Assert.False(back.ConstantForceIsLocal);
        }

        [Fact]
        public void NoForceWritesNoField()
        {
            OSDMap packed = NewData().Pack(new EntityTransferContext());

            Assert.False(packed.ContainsKey("constant_force"));
            Assert.False(packed.ContainsKey("constant_force_local"));
        }

        [Fact]
        public void AgentDataWithoutTheFieldHasNoForce()
        {
            var data = NewData();
            data.ConstantForce = new Vector3(1, 2, 3);
            data.ConstantForceIsLocal = true;
            OSDMap packed = data.Pack(new EntityTransferContext());
            packed.Remove("constant_force");
            packed.Remove("constant_force_local");

            var back = RoundTrip(packed);

            Assert.Equal(Vector3.Zero, back.ConstantForce);
            Assert.False(back.ConstantForceIsLocal);
        }

        [Fact]
        public void AFieldThatIsNotAVectorIsIgnored()
        {
            OSDMap packed = NewData().Pack(new EntityTransferContext());
            packed["constant_force"] = OSD.FromString("not a vector");

            var back = RoundTrip(packed);

            Assert.Equal(Vector3.Zero, back.ConstantForce);
        }
    }
}
