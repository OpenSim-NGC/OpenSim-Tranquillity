/*
 * Copyright (c) Contributors, http://opensimulator.org/
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
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// A reflection probe's ambiance survives the ExtraParams bytes a prim is saved and
    /// reloaded with. SL's range is 0..100 (PRIM_REFLECTION_PROBE), the same range
    /// LSL_Api clamps to when a script sets it.
    /// </summary>
    public class PrimitiveBaseShapeReflectionProbeTests
    {
        private static Primitive.ReflectionProbe RoundTrip(float ambiance)
        {
            PrimitiveBaseShape shape = PrimitiveBaseShape.CreateBox();
            shape.ReflectionProbe = new Primitive.ReflectionProbe
            {
                Ambiance = ambiance,
                ClipDistance = 12f,
                Flags = 1
            };

            PrimitiveBaseShape loaded = PrimitiveBaseShape.CreateBox();
            loaded.ExtraParams = shape.ExtraParams;
            return loaded.ReflectionProbe;
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.5f)]
        [InlineData(1f)]
        [InlineData(50f)]
        [InlineData(100f)]
        public void AnAmbianceInSLsRangeRoundTripsThroughExtraParams(float ambiance)
        {
            Primitive.ReflectionProbe probe = RoundTrip(ambiance);

            Assert.NotNull(probe);
            Assert.Equal(ambiance, probe.Ambiance);
            Assert.Equal(12f, probe.ClipDistance);
            Assert.Equal((byte)1, probe.Flags);
        }

        [Theory]
        [InlineData(-5f, 0f)]
        [InlineData(150f, 100f)]
        public void AnAmbianceOutsideSLsRangeClampsToItsEnds(float ambiance, float expected)
        {
            Primitive.ReflectionProbe probe = RoundTrip(ambiance);

            Assert.NotNull(probe);
            Assert.Equal(expected, probe.Ambiance);
        }
    }
}
