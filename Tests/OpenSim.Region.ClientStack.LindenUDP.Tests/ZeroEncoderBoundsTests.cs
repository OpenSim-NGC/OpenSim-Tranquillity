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

using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// LLUDPZeroEncoder writes without bounds checks. A variable length write that could run past the end of its buffer
/// is refused before a byte is written, instead of overwriting whatever follows the buffer in memory.
/// </summary>
public class ZeroEncoderBoundsTests
{
    [Fact]
    public void BytesThatFitAreWrittenZeroCoded()
    {
        byte[] buffer = new byte[64];
        LLUDPZeroEncoder zc = new(buffer) { Position = 0 };
        zc.AddBytes(new byte[] { 1, 0, 0, 0, 2 }, 5);
        int length = zc.Finish();

        Assert.Equal(new byte[] { 1, 0, 3, 2 }, buffer[..length]);
    }

    [Fact]
    public void BytesThatCouldRunPastTheEndAreRefusedBeforeAnyIsWritten()
    {
        byte[] buffer = new byte[64];
        LLUDPZeroEncoder zc = new(buffer) { Position = 40 };
        byte[] src = Enumerable.Repeat((byte)7, 40).ToArray();

        Assert.Throws<InvalidOperationException>(() => zc.AddBytes(src, src.Length));
        Assert.Equal(40, zc.Position);
        Assert.All(buffer, b => Assert.Equal(0, b));
    }
}
