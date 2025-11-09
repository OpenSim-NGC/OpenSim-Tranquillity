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

using System;
using Xunit;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Terrain.PaintBrushes;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.Terrain.Tests
{
    public class TerrainTest : OpenSimTestCase
    {
        [Fact]
        public void BrushTest()
        {
            int midRegion = (int)Constants.RegionSize / 2;

            // Create a mask that covers only the left half of the region
            bool[,] allowMask = new bool[(int)Constants.RegionSize, 256];
            int x;
            int y;
            for (x = 0; x < midRegion; x++)
            {
                for (y = 0; y < (int)Constants.RegionSize; y++)
                {
                    allowMask[x,y] = true;
                }
            }

            //
            // Test RaiseSphere
            //
            TerrainChannel map = new TerrainChannel((int)Constants.RegionSize, (int)Constants.RegionSize);
            ITerrainPaintableEffect effect = new RaiseSphere();

            effect.PaintEffect(map, allowMask, midRegion, midRegion, -1.0f, 5, 6.0f,
                0, midRegion - 1,0, (int)Constants.RegionSize -1);
            Assert.True(map[127, midRegion] > 0.0);
            Assert.True(map[124, midRegion] > 0.0);
            Assert.True(map[120, midRegion] == 0.0);
            Assert.True(map[128, midRegion] == 0.0);
//            Assert.True(map[0, midRegion] == 0.0);
            //
            // Test LowerSphere
            //
            map = new TerrainChannel((int)Constants.RegionSize, (int)Constants.RegionSize);
            for (x=0; x<map.Width; x++)
            {
                for (y=0; y<map.Height; y++)
                {
                    map[x,y] = 1.0f;
                }
            }
            effect = new LowerSphere();

            effect.PaintEffect(map, allowMask, midRegion, midRegion, -1.0f, 5, 6.0f,
                0, (int)Constants.RegionSize -1,0, (int)Constants.RegionSize -1);
            Assert.True(map[127, midRegion] >= 0.0);
            Assert.True(map[127, midRegion] == 0.0);
            Assert.True(map[125, midRegion] < 1.0);
            Assert.True(map[120, midRegion] == 1.0);
            Assert.True(map[128, midRegion] == 1.0);
//            Assert.True(map[0, midRegion] == 1.0);
        }

        [Fact]
        public void TerrainChannelTest()
        {
            TerrainChannel x = new TerrainChannel((int)Constants.RegionSize, (int)Constants.RegionSize);
            Assert.True(x[0, 0] == 0.0);

            x[0, 0] = 1.0f;
            Assert.True(x[0, 0] == 1.0);

            x[0, 0] = 0;
            x[0, 0] += 5.0f;
            x[0, 0] -= 1.0f;
            Assert.True(x[0, 0] == 4.0f);

            x[0, 0] = 1.0f;
            float[] floatsExport = x.GetFloatsSerialised();
            Assert.Equal(1.0f, floatsExport[0]);

            x[0, 0] = 1.0f;
            Assert.True(x.Tainted(0, 0));

            TerrainChannel y = x.Copy();
            Assert.False(ReferenceEquals(x, y));
            Assert.True(!ReferenceEquals(x.GetDoubles(), y.GetDoubles()));
        }
    }
}
