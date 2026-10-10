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

using System;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Ground vectors, wind, iwSetWind, parcel media for one agent, and the world bounding box:
/// - llGroundSlope, llGroundNormal and llGroundContour are Halcyon's: the slope is the heightmap triangle's downhill
///   vector, not normalised, with a negative z on sloped ground; the normal is &lt;slope.x, slope.y, 1&gt; (SL:
///   "This function does not return a unit vector"); the contour is &lt;-slope.y, slope.x, 0&gt;.
///   iwGroundSurfaceNormal is Halcyon's unit normal averaged over the cell's two triangles.
/// - llWind returns no z (Halcyon and YEngine).
/// - iwSetWind is not implemented: estate managers and gods, who could call it on InWorldz, are told so on
///   DEBUG_CHANNEL; anyone else gets nothing, as before.
/// - PARCEL_MEDIA_COMMAND_AGENT applies the command to that agent only and leaves the parcel's media settings
///   (SL wiki llParcelMediaCommandList).
/// - iwGetWorldBoundingBox gives the box in region coordinates (Halcyon).
/// </summary>
// Calls the API directly on the harness prim; no clock and no process-wide state, so the class runs in parallel.
public class TerrainWindMediaBoxTests
{
    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const int PARCEL_MEDIA_COMMAND_URL = 5, PARCEL_MEDIA_COMMAND_AGENT = 7;

    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static void Ground(Scene scene, Func<int, int, float> height)
    {
        var map = scene.Heightmap;
        for (int x = 0; x < map.Width; x++)
            for (int y = 0; y < map.Height; y++)
                map[x, y] = height(x, y);
    }

    private static SchedulerHarness At(Func<int, int, float> height)
    {
        var h = new SchedulerHarness();
        Ground(h.Scene, height);
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(128.3f, 128.6f, 200f);
        return h;
    }

    // ---- ground vectors ----

    [Fact]
    public void FlatGroundHasNoSlopeAnUpNormalAndNoContour()
    {
        using var h = At((x, y) => 21f);
        var api = Api(h);
        Near(Vector3.Zero, api.llGroundSlope(Vector3.Zero));
        Near(Vector3.UnitZ, api.llGroundNormal(Vector3.Zero));
        Near(Vector3.Zero, api.llGroundContour(Vector3.Zero));
        Near(Vector3.UnitZ, api.iwGroundSurfaceNormal(Vector3.Zero));
    }

    [Fact]
    public void AOneInOneSlopeGivesHalcyonsVectors()
    {
        // Ground rising one metre per metre east: downhill is west and down.
        using var h = At((x, y) => x);
        var api = Api(h);
        Near(new Vector3(-1, 0, -1), api.llGroundSlope(Vector3.Zero));
        Near(new Vector3(-1, 0, 1), api.llGroundNormal(Vector3.Zero));
        Near(new Vector3(0, -1, 0), api.llGroundContour(Vector3.Zero));
        Vector3 normal = new Vector3(-1, 0, 1);
        Near(Vector3.Normalize(in normal), api.iwGroundSurfaceNormal(Vector3.Zero));
    }

    [Fact]
    public void AnOffsetOffTheRegionReadsTheEdgeCell()
    {
        using var h = At((x, y) => x);
        Near(new Vector3(-1, 0, -1), Api(h).llGroundSlope(new Vector3(-500, 0, 0)));
    }

    // ---- wind ----

    private class FixedWind : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "WindSpeed") return new Vector3(1, 2, 3);
            var rt = targetMethod.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    [Fact]
    public void WindHasNoVerticalPart()
    {
        using var h = new SchedulerHarness();
        h.Scene.RegisterModuleInterface<IWindModule>(DispatchProxy.Create<IWindModule, FixedWind>());
        var api = Api(h);
        Near(new Vector3(1, 2, 0), api.llWind(Vector3.Zero));
        Near(new Vector3(1, 2, 3), api.iwWind(Vector3.Zero));
    }

    // ---- iwSetWind ----

    private static string[] Errors(SchedulerHarness h)
        => h.SaidOn.Where(s => s.Channel == DEBUG_CHANNEL).Select(s => s.Message).ToArray();

    [Fact]
    public void SetWindTellsAnEstateManagerItIsNotImplemented()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => false;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => user == owner;
        Api(h).iwSetWind(0, Vector3.Zero, new Vector3(1, 0, 0));
        Assert.Contains(Errors(h), e => e.EndsWith("Command not implemented: iwSetWind"));
    }

    [Fact]
    public void SetWindIsSilentForAnyoneElse()
    {
        using var h = new SchedulerHarness();
        h.Scene.Permissions.OnIsAdministrator += id => false;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => false;
        Api(h).iwSetWind(0, Vector3.Zero, new Vector3(1, 0, 0));
        Assert.Empty(Errors(h));
    }

    // ---- parcel media for one agent ----

    private static ILandObject OwnedParcel(SchedulerHarness h)
    {
        var lmm = new LandManagementModule();
        SceneHelpers.SetupSceneModules(h.Scene, h.Config, lmm);
        var land = new LandObject(h.Prim.OwnerID, false, h.Scene);
        land.SetLandBitmap(land.GetSquareLandBitmap(0, 0, (int)OpenSim.Framework.Constants.RegionSize, (int)OpenSim.Framework.Constants.RegionSize));
        var parcel = lmm.AddLandObject(land);
        parcel.LandData.OwnerID = h.Prim.OwnerID;
        parcel.LandData.MediaURL = "http://example.org/before";
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(128, 128, 30);
        return parcel;
    }

    [Fact]
    public void AnAgentCommandLeavesTheParcelsMediaSettings()
    {
        using var h = new SchedulerHarness();
        var parcel = OwnedParcel(h);
        var client = h.AddClient();
        Api(h).llParcelMediaCommandList(new LSLList(new object[]
            { PARCEL_MEDIA_COMMAND_AGENT, client.AgentId.ToString(), PARCEL_MEDIA_COMMAND_URL, "http://example.org/after" }));
        Assert.Equal("http://example.org/before", parcel.LandData.MediaURL);
    }

    [Fact]
    public void ACommandWithNoAgentChangesTheParcelsMedia()
    {
        using var h = new SchedulerHarness();
        var parcel = OwnedParcel(h);
        Api(h).llParcelMediaCommandList(new LSLList(new object[] { PARCEL_MEDIA_COMMAND_URL, "http://example.org/after" }));
        Assert.Equal("http://example.org/after", parcel.LandData.MediaURL);
    }

    // ---- world bounding box ----

    [Fact]
    public void WorldBoundingBoxIsInRegionCoordinates()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(100, 50, 30);
        var api = Api(h);
        string key = h.Prim.UUID.ToString();
        var rel = api.llGetBoundingBox(key);
        var world = api.iwGetWorldBoundingBox(key);
        Near((Vector3)rel.Data[0] + new Vector3(100, 50, 30), (Vector3)world.Data[0]);
        Near((Vector3)rel.Data[1] + new Vector3(100, 50, 30), (Vector3)world.Data[1]);
    }
}
