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

using System.Reflection;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Vehicle settings are kept on the root prim (SceneObjectPart.SetVehicle*), as Halcyon kept them, so they persist
/// with the object and reach physics when it is rebuilt; before, they went to the physics actor only and were lost
/// (or never set when the prim had no actor). llSetVehicleFloatParam on a vector parameter sets all three components
/// (SOPVehicle), and the rotation parameter is stored normalised.
/// llGetObjectMass gives 0.01 for a child agent (SL: "This function returns a mass of 0.01 for child agents.").
/// llSetKeyframedMotion([], []) and a new motion stop the motion they replace (SL: the empty call stops it).
/// </summary>
// Calls the API directly on the harness prim; no clock and no process-wide state, so the class runs in parallel.
public class VehicleMassKeyframeTests
{
    private const int VEHICLE_TYPE_CAR = 3, VEHICLE_LINEAR_FRICTION_TIMESCALE = 16, VEHICLE_HOVER_HEIGHT = 24,
        VEHICLE_REFERENCE_FRAME = 44, VEHICLE_FLAG_HOVER_UP_ONLY = 32, VEHICLE_FLAG_CAMERA_DECOUPLED = 512;

    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());

    private static VehicleData Data(SchedulerHarness h)
    {
        Assert.NotNull(h.Prim.VehicleParams);
        return h.Prim.VehicleParams.vd;
    }

    [Fact]
    public void VehicleTypeIsKeptOnThePrimWithoutAPhysicsActor()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = null;
        Api(h).llSetVehicleType(VEHICLE_TYPE_CAR);
        Assert.Equal(VEHICLE_TYPE_CAR, h.Prim.VehicleType);
    }

    [Fact]
    public void VehicleParametersAreKeptOnThePrim()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = null;
        var api = Api(h);
        api.llSetVehicleType(VEHICLE_TYPE_CAR);
        api.llSetVehicleFloatParam(VEHICLE_HOVER_HEIGHT, 3.5f);
        api.llSetVehicleVectorParam(VEHICLE_LINEAR_FRICTION_TIMESCALE, new Vector3(4, 5, 6));
        api.llSetVehicleRotationParam(VEHICLE_REFERENCE_FRAME, new Quaternion(0, 0, 2, 2));
        var vd = Data(h);
        Assert.Equal(3.5f, vd.m_VhoverHeight, 3);
        Assert.Equal(new Vector3(4, 5, 6), vd.m_linearFrictionTimescale);
        Assert.Equal(1f, vd.m_referenceFrame.Length(), 3);
    }

    [Fact]
    public void AFloatOnAVectorParameterSetsAllThreeComponents()
    {
        using var h = new SchedulerHarness();
        var api = Api(h);
        api.llSetVehicleType(VEHICLE_TYPE_CAR);
        api.llSetVehicleFloatParam(VEHICLE_LINEAR_FRICTION_TIMESCALE, 7f);
        Assert.Equal(new Vector3(7, 7, 7), Data(h).m_linearFrictionTimescale);
    }

    [Fact]
    public void VehicleFlagsAreSetAndRemovedOnThePrim()
    {
        using var h = new SchedulerHarness();
        var api = Api(h);
        api.llSetVehicleType(VEHICLE_TYPE_CAR);
        api.llSetVehicleFlags(VEHICLE_FLAG_HOVER_UP_ONLY | VEHICLE_FLAG_CAMERA_DECOUPLED);
        Assert.True(((int)Data(h).m_flags & VEHICLE_FLAG_HOVER_UP_ONLY) != 0);
        Assert.True(h.Prim.VehicleParams.CameraDecoupled);
        api.llRemoveVehicleFlags(VEHICLE_FLAG_HOVER_UP_ONLY);
        Assert.True(((int)Data(h).m_flags & VEHICLE_FLAG_HOVER_UP_ONLY) == 0);
    }

    [Fact]
    public void VehicleSettingsAlsoReachAPhysicsActor()
    {
        using var h = new SchedulerHarness();
        var actor = new TypeRecorder();
        h.Prim.PhysActor = actor;
        Api(h).llSetVehicleType(VEHICLE_TYPE_CAR);
        Assert.Equal(VEHICLE_TYPE_CAR, actor.Type);
    }

    private sealed class TypeRecorder : NullPhysicsActor
    {
        public int Type;
        public override int VehicleType { get => Type; set => Type = value; }
    }

    // ---- llGetObjectMass ----

    [Fact]
    public void AChildAgentWeighsOneHundredthOfAKilogram()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        sp.IsChildAgent = true;
        Assert.Equal(0.01f, Api(h).llGetObjectMass(sp.UUID.ToString()), 4);
    }

    // ---- llSetKeyframedMotion ----

    private static bool Running(KeyframeMotion m)
        => (bool)typeof(KeyframeMotion).GetField("m_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!;

    private static LSLList Frames() => new LSLList(new object[] { new Vector3(1, 0, 0), 2.0f });

    /// <summary>[KFM_DATA, KFM_TRANSLATION]: the frames carry a position and a time only.</summary>
    private static LSLList Translation() => new LSLList(new object[] { 2, 2 });

    [Fact]
    public void AnEmptyCallStopsTheMotion()
    {
        using var h = new SchedulerHarness();
        var api = Api(h);
        api.llSetKeyframedMotion(Frames(), Translation());
        var motion = h.Prim.KeyframeMotion;
        Assert.NotNull(motion);
        Assert.True(Running(motion));
        api.llSetKeyframedMotion(new LSLList(new object[0]), new LSLList(new object[0]));
        Assert.Null(h.Prim.KeyframeMotion);
        Assert.False(Running(motion));
    }

    [Fact]
    public void ANewMotionStopsTheOneItReplaces()
    {
        using var h = new SchedulerHarness();
        var api = Api(h);
        api.llSetKeyframedMotion(Frames(), Translation());
        var first = h.Prim.KeyframeMotion;
        api.llSetKeyframedMotion(Frames(), Translation());
        Assert.NotSame(first, h.Prim.KeyframeMotion);
        Assert.False(Running(first));
        Assert.True(Running(h.Prim.KeyframeMotion));
    }
}
