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
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Terrain.Tests;

/// <summary>
/// The viewer's land tools (ModifyLand) as TerrainModule takes them: a brush at a point, or a flood over a
/// selection, kept to one parcel when the packet names it.
/// </summary>
public class TerrainEditTests : OpenSimTestCase
{
    private const byte Raise = 1;
    private const float Ground = 20f;

    private static readonly UUID s_estateOwner = TestHelpers.ParseTail(0x10);
    private static readonly UUID s_resident = TestHelpers.ParseTail(0x11);

    private sealed class Region
    {
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly TerrainModule Terrain = new TerrainModule();
        public readonly LandManagementModule Land = new LandManagementModule();
        public readonly ILandObject Parcel;

        /// <param name="permissions">With the permissions module: the estate and parcel rules apply. Without it every
        /// check allows, which leaves the terrain module's own handling.</param>
        public Region(UUID parcelOwner, bool permissions)
        {
            if (permissions)
                SceneHelpers.SetupSceneModules(Scene, new IniConfigSource(), Terrain, Land, new DefaultPermissionsModule());
            else
                SceneHelpers.SetupSceneModules(Scene, Terrain, Land);
            Scene.RegionInfo.EstateSettings.EstateOwner = s_estateOwner;

            ILandObject lo = new LandObject(parcelOwner, false, Scene);
            lo.SetLandBitmap(lo.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            Parcel = Land.AddLandObject(lo);

            Terrain.InterfaceFillTerrain(new object[] { Ground });
        }

        public TestClient Client(UUID agent)
        {
            return (TestClient)SceneHelpers.AddScenePresence(Scene, agent).ControllingClient;
        }

        public float Height(int x, int y) => Scene.Heightmap[x, y];
    }

    /// <summary>
    /// One ModifyLand, after the module's 20 ms pause between two.
    /// </summary>
    private static void Modify(TestClient c, float west, float south, float east, float north, int parcel)
    {
        Thread.Sleep(40);
        c.TriggerModifyTerrain(Ground, 1f, 4f, Raise, north, west, south, east, parcel);
    }

    // A ModifyLand kept to a parcel that does not exist returned from the middle of the handler with the module's lock
    // held: nobody could terraform again and no terrain patch was sent until the region restarted.
    [Fact]
    public void ATerraformKeptToAParcelThatDoesNotExistLeavesTheLandToolsWorking()
    {
        TestHelpers.InMethod();
        Region r = new Region(s_resident, permissions: false);
        TestClient c = r.Client(s_resident);

        Modify(c, 0, 0, 16, 16, parcel: 9999);
        Assert.Equal(Ground, r.Height(8, 8));

        // the next packet on another thread, as the region's packet jobs run (a Monitor is re-entered by the thread
        // that holds it)
        Task.Run(() => Modify(c, 40, 40, 40, 40, parcel: -1)).Wait();
        Assert.True(r.Height(40, 40) > Ground, "the brush did not change the ground after the refused ModifyLand");
    }

    // The flood kept to a parcel took four metres at a time from where the selection started: a selection off the
    // 4 m grid changed the next parcel's edge with the first parcel's permission, and one ending near the region's
    // north edge ran past it.
    [Fact]
    public void ATerraformKeptToAParcelChangesThatParcelOnly()
    {
        TestHelpers.InMethod();
        Region r = new Region(s_resident, permissions: false);
        r.Land.Subdivide(0, 0, 16, 16, s_resident);
        ILandObject square = r.Land.GetLandObject(0, 0);
        Assert.NotEqual(r.Parcel.LandData.LocalID, square.LandData.LocalID);
        TestClient c = r.Client(s_resident);

        Modify(c, 0, 2, 15, 19, square.LandData.LocalID);

        Assert.True(r.Height(5, 2) > Ground, "the parcel's own land did not change");
        Assert.True(r.Height(5, 15) > Ground, "the parcel's own land did not change");
        for (int y = 16; y <= 19; y++)
            Assert.Equal(Ground, r.Height(5, y));

        // a selection to the north edge, off the grid
        Modify(c, 0, 1, 15, 255, r.Parcel.LandData.LocalID);
        Assert.True(r.Height(5, 255) > Ground, "the selection to the edge did not change the parcel");
        Assert.True(r.Height(5, 15) < Ground + 1.5f, "the next parcel changed again");
    }

    // A flood whose selection had no part in the region was clamped into it, and changed the edge's row or column.
    [Fact]
    public void ASelectionOutsideTheRegionChangesNothing()
    {
        TestHelpers.InMethod();
        Region r = new Region(s_resident, permissions: false);
        TestClient c = r.Client(s_resident);

        Modify(c, 300, 0, 400, 16, parcel: -1);
        Modify(c, 0, -40, 16, -20, parcel: -1);
        Modify(c, float.NaN, 0, 16, 16, parcel: -1);

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(Ground, r.Height(255, i));
            Assert.Equal(Ground, r.Height(i, 0));
        }

        // one that reaches into the region changes its part
        Modify(c, 250, 0, 300, 4, parcel: -1);
        Assert.True(r.Height(255, 2) > Ground, "the part in the region did not change");
    }

    // The region's Block Terraform was only sent to the viewer, which greys its tools out; a client that sent
    // ModifyLand anyway changed any parcel that let it.
    [Fact]
    public void BlockTerraformLeavesTheEstateManagersAlone()
    {
        TestHelpers.InMethod();
        Region r = new Region(s_resident, permissions: true);
        r.Scene.RegionInfo.RegionSettings.BlockTerraform = true;
        TestClient resident = r.Client(s_resident);
        TestClient owner = r.Client(s_estateOwner);

        Modify(resident, 40, 40, 40, 40, parcel: -1);
        Assert.Equal(Ground, r.Height(40, 40));

        Modify(owner, 60, 60, 60, 60, parcel: -1);
        Assert.True(r.Height(60, 60) > Ground, "the estate owner could not terraform");

        r.Scene.RegionInfo.RegionSettings.BlockTerraform = false;
        Modify(resident, 40, 40, 40, 40, parcel: -1);
        Assert.True(r.Height(40, 40) > Ground, "the parcel's owner could not terraform once the region allowed it");
    }
}
