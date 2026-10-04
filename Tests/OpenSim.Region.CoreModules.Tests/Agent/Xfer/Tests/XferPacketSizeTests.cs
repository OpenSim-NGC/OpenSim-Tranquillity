/*
 * Copyright (c) Legion Builds
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
using OpenSim.Region.CoreModules.Agent.Xfer;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Agent.Xfer.Tests;

/// <summary>
/// The region sends an Xfer download in 1000-byte pieces, as LL's sender does (llxfer.cpp, LL_XFER_CHUNK_SIZE = 1000;
/// LLXfer::sendPacket reads packet N from N * chunk). LL's receiver appends each piece at the running length
/// (llxfer.cpp, LLXfer::receiveData); a client that places piece N at 1000 * N needs the region to use LL's size.
/// </summary>
// No test reaches a network service. Test grouping: each test builds its own scene, client and transfer and touches
// no process-wide state (the class does not derive from OpenSimTestCase, which clears MainServer), so it runs in
// parallel.
public class XferPacketSizeTests
{
    private const uint LastPacketFlag = 0x80000000;

    /// <summary>A client that records every SendXferPacket the region gives it.</summary>
    private sealed class RecordingClient : TestClient
    {
        public readonly List<(uint Packet, byte[] Data, int Offset, int Length)> Sent = new();

        public RecordingClient(TestScene scene)
            : base(new AgentCircuitData { AgentID = UUID.Random(), SessionID = UUID.Random() }, scene)
        {
        }

        public override void SendXferPacket(ulong xferID, uint packet,
            byte[] XferData, int XferDataOffset, int XferDatapktLen, bool isTaskInventory)
        {
            Sent.Add((packet, XferData, XferDataOffset, XferDatapktLen));
        }
    }

    private static byte[] Payload(int length)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    /// <summary>Run a whole transfer, acknowledging each piece as the viewer does, and return the pieces sent.</summary>
    private static List<(uint Packet, byte[] Data, int Offset, int Length)> Transfer(byte[] data)
    {
        RecordingClient client = new RecordingClient(new SceneHelpers().SetupScene());
        XferModule.XferDownLoad download = new XferModule.XferDownLoad("test.tmp", data, 42, client, 0);

        download.StartSend();
        for (int i = 0; i < 100 && (client.Sent[^1].Packet & LastPacketFlag) == 0; i++)
            download.AckPacket(client.Sent[^1].Packet);

        Assert.True((client.Sent[^1].Packet & LastPacketFlag) != 0, "the transfer did not reach its last piece");
        return client.Sent;
    }

    private static byte[] Bytes((uint Packet, byte[] Data, int Offset, int Length) piece)
        => piece.Data.AsSpan(piece.Offset, piece.Length).ToArray();

    [Fact]
    public void ATransferLongerThanOnePieceArrivesIntact()
    {
        byte[] data = Payload(2500);

        var pieces = Transfer(data);

        // Appended at the running length, as LL's receiver does.
        Assert.Equal(data, pieces.SelectMany(Bytes).ToArray());

        // Placed at 1000 * packet number, as a client that assumes LL's piece size does.
        byte[] placed = new byte[data.Length];
        foreach (var piece in pieces)
            Bytes(piece).CopyTo(placed, 1000 * (int)(piece.Packet & ~LastPacketFlag));
        Assert.Equal(data, placed);
    }

    [Fact]
    public void ThePiecesAreLLsThousandBytes()
    {
        var pieces = Transfer(Payload(2500));

        Assert.Equal(new[] { 1000, 1000, 500 }, pieces.Select(p => p.Length));
        Assert.Equal(new[] { 0, 1000, 2000 }, pieces.Select(p => p.Offset));
        Assert.Equal(1000, XferModule.XferDownLoad.PacketPayload);
    }

    [Fact]
    public void OnlyTheLastPieceIsMarkedAsLast()
    {
        var pieces = Transfer(Payload(2500));

        Assert.Equal(new uint[] { 0, 1, 2 | LastPacketFlag }, pieces.Select(p => p.Packet));
    }

    [Fact]
    public void ATransferOfWholePiecesEndsWithoutAnEmptyPiece()
    {
        byte[] data = Payload(2000);

        var pieces = Transfer(data);

        Assert.Equal(new uint[] { 0, 1 | LastPacketFlag }, pieces.Select(p => p.Packet));
        Assert.Equal(new[] { 1000, 1000 }, pieces.Select(p => p.Length));
        Assert.Equal(data, pieces.SelectMany(Bytes).ToArray());
    }

    [Fact]
    public void ATransferShorterThanOnePieceIsOneLastPiece()
    {
        byte[] data = Payload(700);

        var pieces = Transfer(data);

        var piece = Assert.Single(pieces);
        Assert.Equal(LastPacketFlag, piece.Packet);
        Assert.Equal(data, Bytes(piece));
    }
}
