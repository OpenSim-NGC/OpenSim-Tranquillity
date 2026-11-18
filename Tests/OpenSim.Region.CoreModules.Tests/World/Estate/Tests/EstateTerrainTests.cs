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


using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Estate;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Estate.Tests;

/// <summary>
/// The estate tools' terrain: the limits and water height they set, and a terrain file they upload.
/// </summary>
public class EstateTerrainTests : OpenSimTestCase
{
    private const uint Last = 0x80000000;

    private sealed class Upload
    {
        public readonly List<uint> Confirmed = new();
        public readonly List<ulong> Aborted = new();
        public byte[] Done;
        public string Failed;
        public readonly TestClient Client;
        public readonly EstateTerrainXferHandler Handler;

        public Upload(long maxSize)
        {
            Scene scene = new SceneHelpers().SetupScene();
            Client = new TestClient(SceneHelpers.GenerateAgentData(TestHelpers.ParseTail(0x1)), scene);
            Client.OnSentConfirmXfer += (_, packet) => Confirmed.Add(packet);
            Client.OnSentAbortXfer += Aborted.Add;
            Handler = new EstateTerrainXferHandler(Client, "terrain.raw", maxSize);
            Handler.TerrainUploadDone += (_, data, _) => Done = data;
            Handler.TerrainUploadFailed += (reason, _) => Failed = reason;
            Handler.RequestStartXfer(Client);
        }

        public void Packet(uint number, byte[] data) => Handler.XferReceive(Client, Handler.XferID, number, data);
    }

    private static byte[] First(int fileSize, params byte[] data)
    {
        byte[] p = new byte[4 + data.Length];
        BitConverter.GetBytes(fileSize).CopyTo(p, 0);
        data.CopyTo(p, 4);
        return p;
    }

    [Fact]
    public void AFileComesInItsPacketsOrder()
    {
        TestHelpers.InMethod();
        Upload u = new Upload(1000);
        u.Packet(0, First(6, 1, 2));
        u.Packet(1, new byte[] { 3, 4 });
        u.Packet(2 | Last, new byte[] { 5, 6 });

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, u.Done);
        Assert.Equal(new uint[] { 0, 1, 2 | Last }, u.Confirmed);
        Assert.Null(u.Failed);
    }

    // A packet the viewer sent again, because the region's confirm of it was lost, was added a second time and the
    // file came out corrupt.
    [Fact]
    public void APacketSentAgainIsConfirmedAgainButTakenOnce()
    {
        TestHelpers.InMethod();
        Upload u = new Upload(1000);
        u.Packet(0, First(4, 1, 2));
        u.Packet(0, First(4, 1, 2));
        u.Packet(2 | Last, new byte[] { 9 });   // ahead of the one expected: not taken, not confirmed
        u.Packet(1 | Last, new byte[] { 3, 4 });
        u.Packet(1 | Last, new byte[] { 3, 4 });

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, u.Done);
        Assert.Equal(new uint[] { 0, 0, 1 | Last, 1 | Last }, u.Confirmed);
    }

    // There was no limit: a file of any size was held in memory, copied whole at every packet.
    [Fact]
    public void AFileTooLargeForTheRegionIsRefused()
    {
        TestHelpers.InMethod();
        Upload declared = new Upload(10);
        declared.Packet(0, First(11, 1, 2));
        Assert.Contains("too large", declared.Failed);
        Assert.Single(declared.Aborted);
        Assert.Null(declared.Done);

        Upload sent = new Upload(4);
        sent.Packet(0, First(4, 1, 2));
        sent.Packet(1 | Last, new byte[] { 3, 4, 5 });
        Assert.Contains("too large", sent.Failed);
        Assert.Null(sent.Done);
    }

    // A first packet too short for the file's size threw (a negative array size).
    [Fact]
    public void AFirstPacketTooShortIsRefused()
    {
        TestHelpers.InMethod();
        Upload u = new Upload(1000);
        u.Packet(0 | Last, new byte[] { 1, 2 });
        Assert.NotNull(u.Failed);
        Assert.Null(u.Done);
    }

    // The region kept one upload at a time and forgot it only when it ended or was aborted: an estate manager who
    // left mid-upload kept every other upload out, "Another Terrain Upload is in progress", until the region
    // restarted.
    [Fact]
    public void AnUploadWhoseViewerLeftMakesRoomForTheNext()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        EstateManagementModule estate = new EstateManagementModule();
        SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), estate, new DefaultPermissionsModule());
        UUID owner = TestHelpers.ParseTail(0x10), manager = TestHelpers.ParseTail(0x11);
        scene.RegionInfo.EstateSettings.EstateOwner = owner;
        scene.RegionInfo.EstateSettings.AddEstateManager(manager);
        TestClient first = (TestClient)SceneHelpers.AddScenePresence(scene, owner).ControllingClient;
        TestClient second = (TestClient)SceneHelpers.AddScenePresence(scene, manager).ControllingClient;
        List<string> asked = new(), alerts = new();
        first.OnSentXferRequest += (_, name) => asked.Add("first " + name);
        second.OnSentXferRequest += (_, name) => asked.Add("second " + name);
        second.OnReceivedAlertMessage += alerts.Add;

        first.TriggerUploadTerrain("a.raw");
        second.TriggerUploadTerrain("b.raw");
        Assert.Equal(new List<string> { "first a.raw" }, asked);
        Assert.Contains(alerts, a => a.Contains("in progress"));

        first.IsActive = false;
        second.TriggerUploadTerrain("b.raw");
        Assert.Equal(new List<string> { "first a.raw", "second b.raw" }, asked);
    }

    // The limits and the water height were stored as sent: a limit that is not a number turned the limits off, as
    // every comparison with it is false.
    [Fact]
    public void TheTerrainLimitsAreNumbersInTheirRanges()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        EstateManagementModule estate = new EstateManagementModule();
        SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), estate);
        RegionSettings rs = scene.RegionInfo.RegionSettings;

        estate.SetRegionTerrainSettings(20f, 4f, -4f, false, false, 0, false, false, 0);
        Assert.Equal((20.0, 4.0, -4.0), (rs.WaterHeight, rs.TerrainRaiseLimit, rs.TerrainLowerLimit));

        estate.SetRegionTerrainSettings(25f, float.NaN, -4f, false, false, 0, false, false, 0);
        estate.SetRegionTerrainSettings(float.PositiveInfinity, 4f, -4f, false, false, 0, false, false, 0);
        Assert.Equal((20.0, 4.0, -4.0), (rs.WaterHeight, rs.TerrainRaiseLimit, rs.TerrainLowerLimit));

        estate.SetRegionTerrainSettings(500f, -3f, 7f, false, false, 0, false, false, 0);
        Assert.Equal((100.0, 0.0, 0.0), (rs.WaterHeight, rs.TerrainRaiseLimit, rs.TerrainLowerLimit));
        estate.SetRegionTerrainSettings(-5f, 400f, -400f, false, false, 0, false, false, 0);
        Assert.Equal((0.0, 100.0, -100.0), (rs.WaterHeight, rs.TerrainRaiseLimit, rs.TerrainLowerLimit));
    }
}
