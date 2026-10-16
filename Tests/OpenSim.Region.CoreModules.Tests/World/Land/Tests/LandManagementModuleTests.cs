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
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.Land.Tests;

public class LandManagementModuleTests : OpenSimTestCase
{
    [Fact]
    public void TestAddLandObject()
    {
        TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

        UUID userId = TestHelpers.ParseTail(0x1);

        LandManagementModule lmm = new LandManagementModule();
        Scene scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, lmm);

        ILandObject lo = new LandObject(userId, false, scene);
        lo.LandData.Name = "lo1";
        lo.SetLandBitmap(
            lo.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
        lo = lmm.AddLandObject(lo);

        // TODO: Should add asserts to check that land object was added properly.

        // At the moment, this test just makes sure that we can't add a land object that overlaps the areas that
        // the first still holds.
        ILandObject lo2 = new LandObject(userId, false, scene);
        lo2.SetLandBitmap(
            lo2.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
        lo2.LandData.Name = "lo2";
        lo2 = lmm.AddLandObject(lo2);

        {
            ILandObject loAtCoord = lmm.GetLandObject(0, 0);
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        {
            ILandObject loAtCoord = lmm.GetLandObject((int)Constants.RegionSize - 1, ((int)Constants.RegionSize - 1));
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }
    }

    private sealed class ParcelFixture
    {
        public readonly UUID OwnerId = TestHelpers.ParseTail(0x1);
        public readonly LandManagementModule Module = new LandManagementModule();
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly TestClient Client;
        public readonly ILandObject Parcel;
        public int Stores;

        public ParcelFixture()
        {
            SceneHelpers.SetupSceneModules(Scene, Module);
            // No PermissionsModule is loaded, so the parcel permission checks allow the owner's edits.
            Client = new TestClient(SceneHelpers.GenerateAgentData(OwnerId), Scene);

            ILandObject lo = new LandObject(OwnerId, false, Scene);
            lo.SetLandBitmap(
                lo.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            Parcel = Module.AddLandObject(lo);

            // The scene stores a parcel whenever this event fires.
            Scene.EventManager.OnLandObjectAdded += _ => Stores++;
        }

        public void SendAccessList(AccessList list, UUID transaction, params UUID[] agents)
        {
            List<LandAccessEntry> entries = new();
            foreach (UUID agent in agents)
                entries.Add(new LandAccessEntry { AgentID = agent, Flags = list, Expires = 0 });
            Module.ClientOnParcelAccessListUpdateRequest(
                OwnerId, (uint)list, transaction, Parcel.LandData.LocalID, entries, Client);
        }

        public LandData Current => Module.GetLandObject(0, 0).LandData;
    }

    // The viewer's Access and Ban tabs send the list as one request, and the region stores the parcel as a result.
    // Without that, the list is held in memory only and is gone after a restart.
    [Theory]
    [InlineData(AccessList.Ban, ParcelFlags.UseBanList)]
    [InlineData(AccessList.Access, ParcelFlags.UseAccessList)]
    public void TestAccessListUpdateIsStored(AccessList list, ParcelFlags flag)
    {
        TestHelpers.InMethod();
        ParcelFixture f = new ParcelFixture();
        UUID target = TestHelpers.ParseTail(0x2);

        f.SendAccessList(list, UUID.Random(), target);

        Assert.Contains(f.Current.ParcelAccessList, e => e.AgentID.Equals(target) && e.Flags == list);
        Assert.NotEqual(0u, f.Current.Flags & (uint)flag);
        Assert.True(f.Stores > 0, "the parcel was not stored after the list update");
    }

    [Fact]
    public void TestClearingTheBanListIsStored()
    {
        TestHelpers.InMethod();
        ParcelFixture f = new ParcelFixture();
        f.SendAccessList(AccessList.Ban, UUID.Random(), TestHelpers.ParseTail(0x2));
        f.Stores = 0;

        // The viewer clears a list by sending one entry with a zero agent id.
        f.SendAccessList(AccessList.Ban, UUID.Random(), UUID.Zero);

        Assert.DoesNotContain(f.Current.ParcelAccessList, e => e.Flags == AccessList.Ban);
        Assert.Equal(0u, f.Current.Flags & (uint)ParcelFlags.UseBanList);
        Assert.True(f.Stores > 0, "the cleared list was not stored");
    }

    private static LandUpdateArgs PropertiesSave(LandData data, uint parcelFlags)
    {
        return new LandUpdateArgs
        {
            ParcelFlags = parcelFlags,
            Name = data.Name,
            Desc = data.Description,
            MediaURL = data.MediaURL,
            MusicURL = data.MusicURL,
            MediaType = data.MediaType,
        };
    }

    // Every tab of the viewer's parcel floater but Access sends the whole flag word at the value it cached, and the
    // viewer has no control for the ban list's on/off flag, so a save can arrive with the flag cleared while the
    // parcel still holds bans. Taking the word as it comes would leave bans that are stored but no longer enforced.
    [Fact]
    public void TestPropertiesSaveOmittingUseBanListPreservesBanFlag()
    {
        TestHelpers.InMethod();
        ParcelFixture f = new ParcelFixture();
        f.SendAccessList(AccessList.Ban, UUID.Random(), TestHelpers.ParseTail(0x2));
        Assert.NotEqual(0u, f.Current.Flags & (uint)ParcelFlags.UseBanList);

        f.Parcel.UpdateLandProperties(
            PropertiesSave(f.Current, f.Current.Flags & ~(uint)ParcelFlags.UseBanList), f.Client, out _, out _);

        Assert.NotEqual(0u, f.Current.Flags & (uint)ParcelFlags.UseBanList);
    }

    // The ban flag is restored only for a parcel that holds a ban, and no other list flag is managed this way: a
    // parcel with an access list may be saved with the flag off (public access on, list kept).
    [Fact]
    public void TestPropertiesSaveDoesNotInventListFlags()
    {
        TestHelpers.InMethod();
        ParcelFixture f = new ParcelFixture();
        f.SendAccessList(AccessList.Access, UUID.Random(), TestHelpers.ParseTail(0x2));

        f.Parcel.UpdateLandProperties(
            PropertiesSave(f.Current, f.Current.Flags & ~(uint)ParcelFlags.UseAccessList), f.Client, out _, out _);

        Assert.Equal(0u, f.Current.Flags & (uint)ParcelFlags.UseAccessList);
        Assert.Equal(0u, f.Current.Flags & (uint)ParcelFlags.UseBanList);
    }

    /// <summary>
    /// Test parcels on region when no land data exists to be loaded.
    /// </summary>
    [Fact]
    public void TestLoadWithNoParcels()
    {
        TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

        SceneHelpers sh = new SceneHelpers();
        LandManagementModule lmm = new LandManagementModule();
        Scene scene = sh.SetupScene();
        SceneHelpers.SetupSceneModules(scene, lmm);

        scene.loadAllLandObjectsFromStorage(scene.RegionInfo.RegionID);

        ILandObject loAtCoord1 = lmm.GetLandObject(0, 0);
        Assert.NotEqual(0, loAtCoord1.LandData.LocalID);
        Assert.NotEqual(UUID.Zero, loAtCoord1.LandData.GlobalID);

        ILandObject loAtCoord2 = lmm.GetLandObject((int)Constants.RegionSize - 1, ((int)Constants.RegionSize - 1));
        // TODO: Assert.Equal(,); - incomplete assertion
        // TODO: Assert.Equal(,); - incomplete assertion
    }

    /// <summary>
    /// Test parcels on region when a single parcel already exists but it does not cover the whole region.
    /// </summary>
    [Fact]
    public void TestLoadWithSinglePartialCoveringParcel()
    {
        TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

        UUID userId = TestHelpers.ParseTail(0x1);

        SceneHelpers sh = new SceneHelpers();
        LandManagementModule lmm = new LandManagementModule();
        Scene scene = sh.SetupScene();
        SceneHelpers.SetupSceneModules(scene, lmm);

        ILandObject originalLo1 = new LandObject(userId, false, scene);
        originalLo1.LandData.Name = "lo1";
        originalLo1.SetLandBitmap(
            originalLo1.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize / 2));

        sh.SimDataService.StoreLandObject(originalLo1);

        scene.loadAllLandObjectsFromStorage(scene.RegionInfo.RegionID);

        ILandObject loAtCoord1 = lmm.GetLandObject(0, 0);
        // TODO: Assert.Equal(,); - incomplete assertion
        // TODO: Assert.Equal(,); - incomplete assertion

        ILandObject loAtCoord2 = lmm.GetLandObject((int)Constants.RegionSize - 1, ((int)Constants.RegionSize - 1));
        // TODO: Assert.Equal(,); - incomplete assertion
        // TODO: Assert.Equal(,); - incomplete assertion
    }

    /// <summary>
    /// Test parcels on region when a single parcel already exists but it does not cover the whole region.
    /// </summary>
    [Fact]
    public void TestLoadWithMultiplePartialCoveringParcels()
    {
        TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

        UUID userId = TestHelpers.ParseTail(0x1);

        SceneHelpers sh = new SceneHelpers();
        LandManagementModule lmm = new LandManagementModule();
        Scene scene = sh.SetupScene();
        SceneHelpers.SetupSceneModules(scene, lmm);

        ILandObject originalLo1 = new LandObject(userId, false, scene);
        originalLo1.LandData.Name = "lo1";
        originalLo1.SetLandBitmap(
            originalLo1.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize / 2));

        sh.SimDataService.StoreLandObject(originalLo1);

        ILandObject originalLo2 = new LandObject(userId, false, scene);
        originalLo2.LandData.Name = "lo2";
        originalLo2.SetLandBitmap(
            originalLo2.GetSquareLandBitmap(
            0, (int)Constants.RegionSize / 2, (int)Constants.RegionSize, ((int)Constants.RegionSize / 4) * 3));

        sh.SimDataService.StoreLandObject(originalLo2);

        scene.loadAllLandObjectsFromStorage(scene.RegionInfo.RegionID);

        ILandObject loAtCoord1 = lmm.GetLandObject(0, 0);
        // TODO: Assert.Equal(,); - incomplete assertion
        // TODO: Assert.Equal(,); - incomplete assertion

        ILandObject loAtCoord2
            = lmm.GetLandObject((int)Constants.RegionSize - 1, (((int)Constants.RegionSize / 4) * 3) - 1);
        // TODO: Assert.Equal(,); - incomplete assertion
        // TODO: Assert.Equal(,); - incomplete assertion

        ILandObject loAtCoord3 = lmm.GetLandObject((int)Constants.RegionSize - 1, ((int)Constants.RegionSize - 1));
        Assert.NotEqual(loAtCoord1.LandData.LocalID, loAtCoord3.LandData.LocalID);
        Assert.NotEqual(loAtCoord2.LandData.LocalID, loAtCoord3.LandData.LocalID);
        Assert.NotEqual(loAtCoord1.LandData.GlobalID, loAtCoord3.LandData.GlobalID);
        Assert.NotEqual(loAtCoord2.LandData.GlobalID, loAtCoord3.LandData.GlobalID);
    }

    /// <summary>
    /// Test parcels on region when whole region is parcelled (which should normally always be the case).
    /// </summary>
    [Fact]
    public void TestLoad()
    {
        TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

        UUID userId = TestHelpers.ParseTail(0x1);

        SceneHelpers sh = new SceneHelpers();
        LandManagementModule lmm = new LandManagementModule();
        Scene scene = sh.SetupScene();
        SceneHelpers.SetupSceneModules(scene, lmm);

        ILandObject originalLo1 = new LandObject(userId, false, scene);
        originalLo1.LandData.Name = "lo1";
        originalLo1.SetLandBitmap(
            originalLo1.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize / 2));

        sh.SimDataService.StoreLandObject(originalLo1);

        ILandObject originalLo2 = new LandObject(userId, false, scene);
        originalLo2.LandData.Name = "lo2";
        originalLo2.SetLandBitmap(
            originalLo2.GetSquareLandBitmap(0, (int)Constants.RegionSize / 2, (int)Constants.RegionSize, (int)Constants.RegionSize));

        sh.SimDataService.StoreLandObject(originalLo2);

        scene.loadAllLandObjectsFromStorage(scene.RegionInfo.RegionID);

        {
            ILandObject loAtCoord = lmm.GetLandObject(0, 0);
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }

        {
            ILandObject loAtCoord = lmm.GetLandObject((int)Constants.RegionSize - 1, ((int)Constants.RegionSize - 1));
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }
    }

    [Fact]
    public void TestSubdivide()
    {
        TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

        UUID userId = TestHelpers.ParseTail(0x1);

        LandManagementModule lmm = new LandManagementModule();
        Scene scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, lmm);

        ILandObject lo = new LandObject(userId, false, scene);
        lo.LandData.Name = "lo1";
        lo.SetLandBitmap(
            lo.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
        lo = lmm.AddLandObject(lo);

        lmm.Subdivide(0, 0, Constants.LandUnit, Constants.LandUnit, userId);

        {
            ILandObject loAtCoord = lmm.GetLandObject(0, 0);
            Assert.NotEqual(lo.LandData.LocalID, loAtCoord.LandData.LocalID);
            Assert.NotEqual(lo.LandData.GlobalID, loAtCoord.LandData.GlobalID);
        }

        {
            ILandObject loAtCoord = lmm.GetLandObject(Constants.LandUnit, Constants.LandUnit);
            // TODO: Assert.Equal(,); - incomplete assertion
            // TODO: Assert.Equal(,); - incomplete assertion
        }
    }
}