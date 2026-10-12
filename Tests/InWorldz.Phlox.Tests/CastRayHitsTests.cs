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
using System.Collections.Generic;
using System.Linq;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What llCastRay hands the physics engine and what a script gets back. The Second Life wiki
/// (https://wiki.secondlife.com/wiki/LlCastRay) gives RC_MAX_HITS a maximum of 256, and says that phantom and
/// volume-detect objects are detected only when RC_DETECT_PHANTOM is set. A type the cast rejects is filtered by the
/// engine, so a rejected hit takes no place among the hits asked for.
///
/// <para>The physics scene here is a stand-in that filters and counts as an engine does; it touches no process-wide
/// state, so these run in parallel.</para>
/// </summary>
public class CastRayHitsTests
{
    private enum Kind { Land, Agent, Host }

    /// <summary>An engine that takes a list of hits along the ray, applies the type filter and count it is given, and records both.</summary>
    private sealed class FilteringScene : OpenSim.Region.PhysicsModules.BasicPhysics.BasicScene
    {
        public readonly List<(Kind kind, float depth, uint id)> Hits = new();
        public int AskedCount = -1;
        public RayFilterFlags? AskedFilter;
        public int FourArgumentCalls;
        public int FiveArgumentCalls;
        public bool Filters = true;

        public override bool SupportsRaycastWorldFiltered() => Filters;

        public override List<ContactResult> RaycastWorld(Vector3 position, Vector3 direction, float length, int Count)
        {
            FourArgumentCalls++;
            return Hits.OrderBy(h => h.depth).Take(Count).Select(ToContact).ToList();
        }

        public override object RaycastWorld(Vector3 position, Vector3 direction, float length, int Count, RayFilterFlags filter)
        {
            FiveArgumentCalls++;
            AskedCount = Count;
            AskedFilter = filter;
            return Hits
                .Where(h => h.kind != Kind.Land || (filter & RayFilterFlags.land) != 0)
                .Where(h => h.kind != Kind.Agent || (filter & RayFilterFlags.agent) != 0)
                .Where(h => h.kind != Kind.Host || (filter & RayFilterFlags.nonphysical) != 0)
                .OrderBy(h => h.depth).Take(Count).Select(ToContact).ToList();
        }

        private static ContactResult ToContact((Kind kind, float depth, uint id) h)
            => new ContactResult { ConsumerID = h.id, Depth = h.depth, Pos = new Vector3(10, 10, 50 - h.depth), Normal = Vector3.UnitZ };
    }

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H = new();
        public readonly FilteringScene Physics = new();
        public readonly ScenePresence Agent;
        private readonly PhysicsScene m_previous;

        public Rig()
        {
            m_previous = H.Scene.PhysicsScene;
            H.Scene.PhysicsScene = Physics;
            Agent = SceneHelpers.AddScenePresence(H.Scene, UUID.Random());
        }

        public LSLSystemAPI Script() => new LSLSystemAPI(H.Engine, H.Prim, H.Prim.LocalId, UUID.Random());

        public void Dispose()
        {
            H.Scene.PhysicsScene = m_previous;
            H.Dispose();
        }
    }

    private static LSLList Options(params int[] pairs) => new LSLList(pairs.Select(p => (object)p).ToList());

    private static LSLList Cast(LSLSystemAPI api, LSLList options)
        => api.llCastRay(new Vector3(10, 10, 50), new Vector3(10, 10, 0), options);

    private static int Status(LSLList result) => result.GetLSLIntegerItem(result.Length - 1);

    [Fact]
    public void ARejectedTypeTakesNoPlaceAmongTheHitsAsked()
    {
        using var r = new Rig();
        r.Physics.Hits.Add((Kind.Agent, 1f, r.Agent.LocalId));
        r.Physics.Hits.Add((Kind.Land, 2f, 0));

        // The agent is in front and rejected: the one hit asked for is the ground behind it.
        LSLList result = Cast(r.Script(), Options(SlConst.RC_MAX_HITS, 1, SlConst.RC_REJECT_TYPES, SlConst.RC_REJECT_AGENTS));

        Assert.Equal(1, Status(result));
        Assert.Equal(UUID.Zero.ToString(), result.GetLSLStringItem(0));
    }

    [Fact]
    public void TheCastIsAskedOfTheEngineByTypeNotFilteredAfterwards()
    {
        using var r = new Rig();
        r.Physics.Hits.Add((Kind.Land, 2f, 0));

        Cast(r.Script(), Options(SlConst.RC_REJECT_TYPES, SlConst.RC_REJECT_LAND | SlConst.RC_REJECT_PHYSICAL));

        Assert.Equal(0, r.Physics.FourArgumentCalls);
        RayFilterFlags f = r.Physics.AskedFilter.Value;
        Assert.Equal(RayFilterFlags.agent | RayFilterFlags.nonphysical | RayFilterFlags.BackFaceCull, f);
    }

    [Fact]
    public void ACastForMoreThanTheOldCapGetsThemUpTo256()
    {
        using var r = new Rig();
        for (int i = 1; i <= 300; i++) r.Physics.Hits.Add((Kind.Land, i, 0));

        LSLList result = Cast(r.Script(), Options(SlConst.RC_MAX_HITS, 300));

        Assert.Equal(256, Status(result));
        Assert.Equal(256 * 2 + 1, result.Length); // an id and a position per hit, then the status
    }

    [Fact]
    public void ACastForSixteenOrFewerIsAskedForAtLeastThatMany()
    {
        using var r = new Rig();
        for (int i = 1; i <= 40; i++) r.Physics.Hits.Add((Kind.Land, i, 0));

        LSLList result = Cast(r.Script(), Options(SlConst.RC_MAX_HITS, 20));

        Assert.Equal(20, Status(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhantomAndVolumeDetectAreAskedForOnlyWhenTheCastSaysSo(bool detectPhantom)
    {
        using var r = new Rig();
        r.Physics.Hits.Add((Kind.Land, 2f, 0));

        LSLList options = detectPhantom
            ? Options(SlConst.RC_DETECT_PHANTOM, 1, SlConst.RC_REJECT_TYPES, SlConst.RC_REJECT_PHYSICAL | SlConst.RC_REJECT_NONPHYSICAL)
            : Options(SlConst.RC_REJECT_TYPES, SlConst.RC_REJECT_PHYSICAL | SlConst.RC_REJECT_NONPHYSICAL);
        Cast(r.Script(), options);

        RayFilterFlags f = r.Physics.AskedFilter.Value;
        Assert.Equal(detectPhantom, (f & RayFilterFlags.LSLPhantom) == RayFilterFlags.LSLPhantom);
        Assert.Equal(detectPhantom, (f & RayFilterFlags.phantom) != 0 && (f & RayFilterFlags.volumedtc) != 0);
        // The two reject flags drop the physical and non-physical layers whether or not phantoms are asked for.
        Assert.Equal(0, (int)(f & (RayFilterFlags.physical | RayFilterFlags.nonphysical)));
    }

    [Fact]
    public void ThePhantomOptionAcceptsAnyNonZeroValue()
    {
        using var r = new Rig();
        Cast(r.Script(), Options(SlConst.RC_DETECT_PHANTOM, 7));
        Assert.True((r.Physics.AskedFilter.Value & RayFilterFlags.LSLPhantom) == RayFilterFlags.LSLPhantom);
    }

    [Fact]
    public void ARayThatHitsOnlyTheScriptsOwnPrimOnTheWayStillGetsTheHitsAsked()
    {
        using var r = new Rig();
        r.Physics.Hits.Add((Kind.Host, 1f, r.H.Prim.LocalId));
        r.Physics.Hits.Add((Kind.Land, 2f, 0));

        // The script's own linkset is left out of the answer; it must not use up the one place.
        LSLList result = Cast(r.Script(), Options(SlConst.RC_MAX_HITS, 1));

        Assert.Equal(1, Status(result));
        Assert.Equal(UUID.Zero.ToString(), result.GetLSLStringItem(0));
    }

    /// <summary>A ray with no length has no direction. YEngine answers [0] when the ray is shorter than 0.001 m, before it asks the engine.</summary>
    [Theory]
    [InlineData(true, 0f)]
    [InlineData(true, 0.0005f)]
    [InlineData(false, 0f)]
    [InlineData(false, 0.0005f)]
    public void ARayWithNoLengthAnswersZeroAndTheEngineIsNeverAsked(bool engineFilters, float length)
    {
        using var r = new Rig();
        r.Physics.Filters = engineFilters;
        r.Physics.Hits.Add((Kind.Land, 0f, 0));

        Vector3 start = new Vector3(10, 10, 50);
        LSLList result = r.Script().llCastRay(start, new Vector3(10, 10, 50 - length), Options());

        Assert.Equal(1, result.Length);
        Assert.Equal(0, result.GetLSLIntegerItem(0));
        Assert.Equal(0, r.Physics.FourArgumentCalls);
        Assert.Equal(0, r.Physics.FiveArgumentCalls);
    }
}
