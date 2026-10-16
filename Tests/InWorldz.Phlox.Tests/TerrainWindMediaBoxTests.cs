/*
 * Copyright (c) 2026 Legion Builds
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
///   vector, not normalised, with a negative z on sloped ground; the normal is &lt;slope.x, slope.y, 1&gt; (SL,
///   https://wiki.secondlife.com/wiki/LlGroundNormal: the result is not a unit-length vector); the contour is
///   &lt;-slope.y, slope.x, 0&gt;. iwGroundSurfaceNormal is Halcyon's unit normal averaged over the cell's two triangles.
/// - llWind returns no z (Halcyon and YEngine).
/// - iwSetWind hands the cell at the object's position plus the offset to the region's wind module, for estate
///   managers and gods only (Halcyon); anyone else is ignored.
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

    [Fact]
    public void BothEnginesAskForTheWindAtTheHeightOfThePosition()
    {
        // A wind model with layers (a current under the water, the wind aloft) needs the height from llWind too.
        using var h = new SchedulerHarness(withYEngine: true);
        var wind = RecordWind(h);
        var origin = h.Prim.GetWorldPosition();
        var item = OpenSim.Tests.Common.TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "yengine-api", "default { }");
        var yengine = new OpenSim.Region.ScriptEngine.Shared.Api.LSL_Api();
        yengine.Initialize(h.YEngine, h.Prim, item);

        Api(h).llWind(new Vector3(3, 4, -7));
        yengine.llWind(new OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3(3, 4, -7));
        Assert.Equal(2, wind.Asked.Count);
        Assert.Equal(wind.Asked[0], wind.Asked[1]);
        Assert.Equal(((int)(origin.X + 3), (int)(origin.Y + 4), (int)(origin.Z - 7)), wind.Asked[0]);
    }

    // ---- iwSetWind ----

    private static string[] Errors(SchedulerHarness h)
        => h.SaidOn.Where(s => s.Channel == DEBUG_CHANNEL).Select(s => s.Message).ToArray();

    private class RecordingWind : DispatchProxy
    {
        public readonly System.Collections.Generic.List<(int Type, Vector3 Loc, Vector3 Speed)> Sets = new();
        public readonly System.Collections.Generic.List<(int X, int Y, int Z)> Asked = new();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "WindSpeed")
                lock (Asked) Asked.Add(((int)args[0], (int)args[1], (int)args[2]));
            if (targetMethod.Name == "WindSet")
                lock (Sets) Sets.Add(((int)args[0], (Vector3)args[1], (Vector3)args[2]));
            var rt = targetMethod.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private static RecordingWind RecordWind(SchedulerHarness h)
    {
        var proxy = DispatchProxy.Create<IWindModule, RecordingWind>();
        h.Scene.RegisterModuleInterface<IWindModule>(proxy);
        return (RecordingWind)(object)proxy;
    }

    [Fact]
    public void SetWindPassesTheCellPositionToTheWindModuleForAnEstateManager()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => false;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => user == owner;
        var wind = RecordWind(h);
        var origin = h.Prim.GetWorldPosition();
        Api(h).iwSetWind(1, new Vector3(16, -8, 2), new Vector3(3, 4, 0));
        var set = Assert.Single(wind.Sets);
        Assert.Equal(1, set.Type);
        Near(origin + new Vector3(16, -8, 2), set.Loc);
        Near(new Vector3(3, 4, 0), set.Speed);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void SetWindWorksForAGod()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => id == owner;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => false;
        var wind = RecordWind(h);
        Api(h).iwSetWind(0, Vector3.Zero, Vector3.Zero);
        Assert.Single(wind.Sets);
    }

    [Fact]
    public void SetWindDefaultsFormIsPassedThroughAsType0()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => false;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => user == owner;
        var wind = RecordWind(h);
        Api(h).iwSetWind(0, Vector3.Zero, new Vector3(9, 9, 9));
        Assert.Equal(0, Assert.Single(wind.Sets).Type);
    }

    [Fact]
    public void SetWindIsSilentAndChangesNothingForAnyoneElse()
    {
        using var h = new SchedulerHarness();
        h.Scene.Permissions.OnIsAdministrator += id => false;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => false;
        var wind = RecordWind(h);
        Api(h).iwSetWind(1, Vector3.Zero, new Vector3(1, 0, 0));
        Assert.Empty(wind.Sets);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void SetWindWithoutAWindModuleDoesNothing()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => user == owner;
        Api(h).iwSetWind(1, Vector3.Zero, new Vector3(1, 0, 0));
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
