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
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llSetRegionPos as the SL wiki documents it (https://wiki.secondlife.com/wiki/LlSetRegionPos):
/// - "Returns FALSE and does not move the object if position is more than 10m off region or above 4096m";
/// - FALSE for a dynamic (physical) object, an avatar attachment, or a parcel or region refusal;
/// - below ground the object goes to ground level: TRUE within 0.1 m, FALSE (but still moved) further down.
/// Up to 10 m past the edge the object crosses into the region there, which core's crossing does; with no region
/// there the call is FALSE and the object stays.
/// </summary>
// Calls the API directly. The crossing tests wait on core's asynchronous crossing by polling with a cap; they do not
// switch Util.FireAndForgetMethod (process-wide). They register two regions in the test grid, which is shared by the
// process, at coordinates no other test uses, so the class runs in parallel.
public class SetRegionPosTests
{
    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.01f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    /// <summary>Flat ground at <paramref name="height"/> over the whole region.</summary>
    private static void FlatGround(Scene scene, float height)
    {
        var map = scene.Heightmap;
        for (int x = 0; x < map.Width; x++)
            for (int y = 0; y < map.Height; y++)
                map[x, y] = height;
    }

    private static SchedulerHarness Harness(float ground = 20f)
    {
        var h = new SchedulerHarness();
        FlatGround(h.Scene, ground);
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(128, 128, 30);
        return h;
    }

    [Fact]
    public void AMoveInsideTheRegionIsTrue()
    {
        using var h = Harness();
        Assert.Equal(1, Api(h).llSetRegionPos(new Vector3(40, 200, 50)));
        Near(new Vector3(40, 200, 50), h.Prim.ParentGroup.AbsolutePosition);
    }

    [Theory]
    [InlineData(-10.5f, 128f, 30f)]
    [InlineData(266.5f, 128f, 30f)]
    [InlineData(128f, -11f, 30f)]
    [InlineData(128f, 267f, 30f)]
    [InlineData(128f, 128f, 4097f)]
    public void MoreThanTenMetresOffTheRegionOrAbove4096IsFalseAndDoesNotMove(float x, float y, float z)
    {
        using var h = Harness();
        Assert.Equal(0, Api(h).llSetRegionPos(new Vector3(x, y, z)));
        Near(new Vector3(128, 128, 30), h.Prim.ParentGroup.AbsolutePosition);
    }

    [Fact]
    public void PastTheEdgeWithNoRegionThereIsFalseAndDoesNotMove()
    {
        using var h = Harness();
        Assert.Equal(0, Api(h).llSetRegionPos(new Vector3(-5, 128, 30)));
        Near(new Vector3(128, 128, 30), h.Prim.ParentGroup.AbsolutePosition);
        Assert.False(h.Prim.ParentGroup.inTransit);
    }

    [Fact]
    public void APhysicalObjectIsFalse()
    {
        using var h = Harness();
        h.Prim.AddFlag(PrimFlags.Physics);
        Assert.Equal(0, Api(h).llSetRegionPos(new Vector3(40, 40, 40)));
        Near(new Vector3(128, 128, 30), h.Prim.ParentGroup.AbsolutePosition);
    }

    [Fact]
    public void AnAttachmentIsFalse()
    {
        using var h = Harness();
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sp.AddAttachment(sog);
        Vector3 before = sp.AbsolutePosition;
        Assert.Equal(0, Api(h).llSetRegionPos(new Vector3(40, 40, 40)));
        Near(before, sp.AbsolutePosition);
    }

    [Fact]
    public void ARegionRefusalIsFalse()
    {
        using var h = Harness();
        h.Scene.Permissions.OnObjectEntry += (sog, entering, pos) => false;
        Assert.Equal(0, Api(h).llSetRegionPos(new Vector3(40, 40, 40)));
        Near(new Vector3(128, 128, 30), h.Prim.ParentGroup.AbsolutePosition);
    }

    [Fact]
    public void JustBelowGroundGoesToTheGroundAndIsTrue()
    {
        using var h = Harness(ground: 20f);
        Assert.Equal(1, Api(h).llSetRegionPos(new Vector3(40, 40, 19.95f)));
        Near(new Vector3(40, 40, 20), h.Prim.ParentGroup.AbsolutePosition);
    }

    [Fact]
    public void WellBelowGroundGoesToTheGroundButIsFalse()
    {
        using var h = Harness(ground: 20f);
        Assert.Equal(0, Api(h).llSetRegionPos(new Vector3(40, 40, 5f)));
        Near(new Vector3(40, 40, 20), h.Prim.ParentGroup.AbsolutePosition);
    }

    // ---- crossing (Q6: does core cross an object moved up to 10 m past the edge?) ----

    /// <summary>
    /// Region A at (7300, 7300) and region B south of it at (7300, 7299), on one simulator. The test grid service
    /// keeps every region registered in the process, so the pair sits far from the harness region at (1000, 1000):
    /// placed beside it, region B became the harness region's neighbour for tests running alongside.
    /// </summary>
    private static (TestScene A, TestScene B) TwoRegions()
    {
        var etmA = new EntityTransferModule();
        var etmB = new EntityTransferModule();
        var lscm = new LocalSimulationConnectorModule();
        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        var sh = new SceneHelpers();
        TestScene a = sh.SetupScene("Example Region A", UUID.Random(), 7300, 7300);
        TestScene b = sh.SetupScene("Example Region B", UUID.Random(), 7300, 7299);
        SceneHelpers.SetupSceneModules(new Scene[] { a, b }, config, lscm);
        SceneHelpers.SetupSceneModules(a, config, etmA);
        SceneHelpers.SetupSceneModules(b, config, etmB);
        FlatGround(a, 20f);
        FlatGround(b, 20f);
        return (a, b);
    }

    /// <summary>An API whose engine knows only its scene: llSetRegionPos reads nothing else from the engine.</summary>
    private static LSLSystemAPI ApiOn(Scene scene, SceneObjectPart part)
    {
        var engine = new PhloxEngine();
        typeof(PhloxEngine).GetField("m_Scene", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, scene);
        return new LSLSystemAPI(engine, part, part.LocalId, UUID.Random());
    }

    private static bool WaitFor(Func<bool> done, double seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            System.Threading.Thread.Sleep(50);
        }
        return done();
    }

    [Fact]
    public void CoreCrossesAnObjectMovedFiveMetresPastTheEdge()
    {
        var (a, b) = TwoRegions();
        var sog = SceneHelpers.AddSceneObject(a, "Example Object", UUID.Random());
        sog.AbsolutePosition = new Vector3(128, 3, 30);
        UUID id = sog.UUID;

        sog.UpdateGroupPosition(new Vector3(128, -5, 30));

        Assert.True(WaitFor(() => b.GetSceneObjectGroup(id) != null), "the object did not reach the region beyond the edge");
        Assert.True(WaitFor(() => a.GetSceneObjectGroup(id) == null), "the object stayed in the first region");
    }

    [Fact]
    public void SetRegionPosPastTheEdgeCrossesIntoTheRegionThere()
    {
        var (a, b) = TwoRegions();
        var sog = SceneHelpers.AddSceneObject(a, "Example Object", UUID.Random());
        sog.AbsolutePosition = new Vector3(128, 3, 30);
        UUID id = sog.UUID;

        Assert.Equal(1, ApiOn(a, sog.RootPart).llSetRegionPos(new Vector3(128, -5, 30)));

        Assert.True(WaitFor(() => b.GetSceneObjectGroup(id) != null), "the object did not reach the region beyond the edge");
        var there = b.GetSceneObjectGroup(id);
        Near(new Vector3(128, 251, 30), there.AbsolutePosition, 0.5f);
    }

    [Fact]
    public void EdgeOfWorldIsFalseTowardsANeighbour()
    {
        var (a, _) = TwoRegions();
        var sog = SceneHelpers.AddSceneObject(a, "Example Object", UUID.Random());
        var api = ApiOn(a, sog.RootPart);
        Assert.Equal(0, api.llEdgeOfWorld(new Vector3(128, 128, 30), new Vector3(0, -1, 0)));
        Assert.Equal(1, api.llEdgeOfWorld(new Vector3(128, 128, 30), new Vector3(0, 1, 0)));
    }
}
