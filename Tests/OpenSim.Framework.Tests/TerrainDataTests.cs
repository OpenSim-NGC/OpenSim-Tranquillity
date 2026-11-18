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


using OpenSim.Framework;
using Xunit;

namespace OpenSim.Framework.Tests;

/// <summary>
/// The height range of a terrain patch, which the patch's encoding for the viewers is built on.
/// </summary>
public class TerrainDataTests
{
    // The first line of a patch was read one cell short: the cell (16 px, 16 py + 15) never counted toward the range.
    // Raised or lowered past the rest of the patch, it was encoded wrong and viewers saw a height the region does not
    // have.
    [Theory]
    [InlineData(16, 31)]
    [InlineData(16, 16)]
    [InlineData(31, 31)]
    [InlineData(20, 25)]
    public void EveryCellOfAPatchCountsTowardItsRange(int x, int y)
    {
        TerrainData td = new TerrainData(256, 256, 256);
        for (int i = 0; i < 256; i++)
            for (int j = 0; j < 256; j++)
                td[i, j] = 20f;

        td[x, y] = 50f;
        td.GetPatchMinMax(1, 1, out float min, out float max);
        Assert.Equal((20f, 50f), (min, max));

        td[x, y] = 5f;
        td.GetPatchMinMax(1, 1, out min, out max);
        Assert.Equal((5f, 20f), (min, max));
    }
}
