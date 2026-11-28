/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenMetaverse;

namespace OpenSim.Framework.Tests;

/// <summary>
/// AssetLandmark reads the landmark text by key. Well-formed landmarks read as before; a short, reordered or
/// incomplete one is read or refused with a FormatException instead of throwing out of a Substring.
/// </summary>
public class AssetLandmarkTests
{
    private static readonly UUID Region = new("6a5d2c1e-0f3b-4a7d-9e8c-1b2a3c4d5e6f");
    private const ulong Handle = 1099511628032000;

    private static AssetLandmark Read(string data)
    {
        AssetBase asset = new(UUID.Random(), "lm", (sbyte)AssetType.Landmark, UUID.Zero.ToString())
        {
            Data = Encoding.UTF8.GetBytes(data),
        };
        return new AssetLandmark(asset);
    }

    private static void AssertRead(AssetLandmark lm, Vector3 position, ulong handle, string gatekeeper)
    {
        Assert.Equal(2, lm.Version);
        Assert.Equal(Region, lm.RegionID);
        Assert.Equal(position, lm.Position);
        Assert.Equal(handle, lm.RegionHandle);
        Assert.Equal(gatekeeper, lm.Gatekeeper);
    }

    [Fact]
    public void ALandmarkWrittenByThisSimulatorIsRead()
    {
        // As InventoryAccessModule.GenerateLandmark writes it.
        string data = string.Format(CultureInfo.InvariantCulture.NumberFormat,
            "Landmark version 2\nregion_id {0}\nlocal_pos {1} {2} {3}\nregion_handle {4}\n",
            Region, 128.5f, 64.25f, 22.75f, Handle);

        AssertRead(Read(data), new Vector3(128.5f, 64.25f, 22.75f), Handle, string.Empty);
    }

    [Fact]
    public void AHypergridLandmarkIsReadWithItsGatekeeper()
    {
        // As HGInventoryAccessModule.GenerateLandmark writes it.
        string data = string.Format(Culture.FormatProvider,
            "Landmark version 2\nregion_id {0}\nlocal_pos {1} {2} {3}\nregion_handle {4}\ngatekeeper {5}\n",
            Region, 10.5f, 20f, 30.125f, Handle, "http://grid.example:8002");

        AssertRead(Read(data), new Vector3(10.5f, 20f, 30.125f), Handle, "http://grid.example:8002");
    }

    [Theory]
    // The viewer's minimum (lllandmark.cpp reads version, region_id and local_pos only): no region_handle.
    [InlineData("Landmark version 2\nregion_id {0}\nlocal_pos 1 2 3\n", 0ul, "")]
    // A region_handle line with no value.
    [InlineData("Landmark version 2\nregion_id {0}\nlocal_pos 1 2 3\nregion_handle\n", 0ul, "")]
    [InlineData("Landmark version 2\nregion_id {0}\nlocal_pos 1 2 3\nregion_handle \n", 0ul, "")]
    // CRLF line endings and extra spaces.
    [InlineData("Landmark version 2\r\nregion_id  {0} \r\nlocal_pos  1   2  3 \r\nregion_handle 1099511628032000\r\n", Handle, "")]
    // Lines in another order.
    [InlineData("Landmark version 2\ngatekeeper http://grid.example:8002\nlocal_pos 1 2 3\nregion_id {0}\nregion_handle 1099511628032000\n", Handle, "http://grid.example:8002")]
    public void AShortOrReorderedLandmarkIsRead(string format, ulong handle, string gatekeeper)
    {
        AssertRead(Read(string.Format(format, Region)), new Vector3(1, 2, 3), handle, gatekeeper);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a landmark")]
    [InlineData("Landmark version 2")]
    [InlineData("Landmark version 2\n")]
    [InlineData("Landmark version 2\nregion_id\nlocal_pos 1 2 3\n")]
    [InlineData("Landmark version 2\nregion_id 1234\nlocal_pos 1 2 3\n")]
    [InlineData("Landmark version 2\nregion_id {0}\n")]
    [InlineData("Landmark version 2\nregion_id {0}\nlocal_pos\n")]
    [InlineData("Landmark version 2\nregion_id {0}\nlocal_pos 1 2\nregion_handle 1099511628032000\n")]
    public void ALandmarkWithoutRegionOrPositionIsRefused(string format)
    {
        Assert.Throws<FormatException>(() => Read(string.Format(format, Region)));
    }

    [Fact]
    public void APositionWrittenInTheInvariantCultureIsReadInAnyCulture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            AssetLandmark lm = Read($"Landmark version 2\nregion_id {Region}\nlocal_pos 10.5 20.25 30\nregion_handle {Handle}\n");
            Assert.Equal(new Vector3(10.5f, 20.25f, 30f), lm.Position);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
