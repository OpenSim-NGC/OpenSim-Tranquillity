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

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Ctrl-Shift drag (rotate-drag) of a physical object. The viewer sends ObjectSpinStart once, ObjectSpinUpdate with the
/// rotation it wants, absolute, while the mouse moves, and ObjectSpinStop once. The turn steers the root actor with the
/// look-at controller (the one llRotLookAt uses) and never sends an angular force.
/// </summary>
// Each test builds its own scene and stand-in physics actors and touches no process-wide state, so the class runs in parallel.
public class SceneObjectSpinTests : OpenSimTestCase
{
    private static readonly Vector3 Rest = new(100, 100, 25);

    /// <summary>A physics actor that records, in order, everything core asks of it.</summary>
    private sealed class Recorder : NullPhysicsActor
    {
        private readonly List<string> m_calls = new();
        private bool m_pidActive, m_physical = true;
        private Vector3 m_velocity, m_position, m_rotational;
        private Quaternion m_orientation = Quaternion.Identity;

        public override Vector3 Position { get => m_position; set => m_position = value; }
        public override Quaternion Orientation { get => m_orientation; set => m_orientation = value; }

        public List<string> Calls { get { lock (m_calls) return m_calls.ToList(); } }
        public void Clear() { lock (m_calls) m_calls.Clear(); }
        private void Log(string s) { lock (m_calls) m_calls.Add(s); }

        public override bool IsPhysical { get => m_physical; set => m_physical = value; }
        public override float Mass => 10f;
        public override Vector3 Velocity { get => m_velocity; set { m_velocity = value; Log("Velocity=" + V(value)); } }
        public override Vector3 RotationalVelocity { get => m_rotational; set { m_rotational = value; Log("RotationalVelocity=" + V(value)); } }
        public override void AddForce(Vector3 force, bool pushforce) => Log("AddForce=" + V(force));
        public override void AddAngularForce(Vector3 force, bool pushforce) => Log("AddAngularForce=" + V(force));
        public override Vector3 PIDTarget { set => Log("PIDTarget=" + V(value)); }
        public override float PIDTau { set => Log("PIDTau=" + F(value)); }
        public override bool PIDActive { get => m_pidActive; set { m_pidActive = value; Log("PIDActive=" + value); } }
        public override bool Selected { set => Log("Selected=" + value); }
        public override int VehicleType { get => 0; set => Log("VehicleType=" + value); }
    }

    private static string F(float f) => f.ToString("F2", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"<{F(v.X)},{F(v.Y)},{F(v.Z)}>";

    private static Quaternion Turn(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    private sealed class Fixture
    {
        public TestScene Scene;
        public SceneObjectGroup Group;
        public Recorder Actor;
        public ScenePresence Holder, Other;
        public IClientAPI Client => Holder.ControllingClient;
        public SceneObjectPart Root => Group.RootPart;

        public void Start(SceneObjectPart part = null, ScenePresence by = null)
            => Scene.ProcessSpinStart((part ?? Root).UUID, (by ?? Holder).ControllingClient);
        public void Spin(Quaternion rotation, SceneObjectPart part = null, ScenePresence by = null)
            => Scene.ProcessSpinObject((part ?? Root).UUID, rotation, (by ?? Holder).ControllingClient);
        public void Stop(SceneObjectPart part = null, ScenePresence by = null)
            => Scene.ProcessSpinObjectStop((part ?? Root).UUID, (by ?? Holder).ControllingClient);
        public void Drag(Vector3 pos, ScenePresence by = null)
            => Scene.ProcessObjectGrabUpdate(Root.UUID, Vector3.Zero, pos, (by ?? Holder).ControllingClient, null);
        public void Grab(ScenePresence by = null)
            => Scene.ProcessObjectGrab(Root.LocalId, Vector3.Zero, (by ?? Holder).ControllingClient, null);
        public void DeGrab(ScenePresence by = null)
            => Scene.ProcessObjectDeGrab(Root.LocalId, (by ?? Holder).ControllingClient, null);

        /// <summary>One pass of the look-at controller, which the scene runs once a frame.</summary>
        public void Step() => Root.UpdateLookAt();
    }

    /// <summary>A scene with a physical object of <paramref name="parts"/> parts resting at <see cref="Rest"/> and two avatars.</summary>
    private static Fixture Make(int parts = 1)
    {
        var f = new Fixture { Scene = new SceneHelpers().SetupScene() };
        f.Holder = SceneHelpers.AddScenePresence(f.Scene, TestHelpers.ParseTail(0x11));
        f.Other = SceneHelpers.AddScenePresence(f.Scene, TestHelpers.ParseTail(0x12));
        f.Group = SceneHelpers.AddSceneObject(f.Scene, parts, TestHelpers.ParseTail(0x1), "spin", 0x100);
        f.Actor = new Recorder();
        f.Root.PhysActor = f.Actor;
        f.Root.AddFlag(PrimFlags.Physics);
        f.Group.AbsolutePosition = Rest;
        f.Actor.Position = Rest;
        f.Actor.Clear();
        return f;
    }

    private static void SitOn(SceneObjectGroup group, ScenePresence sitter)
    {
        var list = (List<ScenePresence>)typeof(SceneObjectGroup)
            .GetField("m_sittingAvatars", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(group)!;
        lock (list) list.Add(sitter);
    }

    /// <summary>The angular velocity core's look-at controller gives an object at rest in the identity rotation for a target.</summary>
    private static Vector3 LookAtVelocity(Quaternion target, float strength = 1f)
    {
        var f = Make();
        f.Group.RotLookAt(target, strength, 0.1f);
        f.Step();
        return f.Actor.RotationalVelocity;
    }

    private static string Omega(Vector3 v) => "RotationalVelocity=" + V(v);

    [Fact]
    public void TheTurnSteersTheRootWithTheLookAtControllerTowardTheViewersRotation()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Vector3 expected = LookAtVelocity(Turn(90));
        Assert.True(expected.Length() > 1f);
        Assert.Equal(new[] { Omega(expected) }, f.Actor.Calls);
    }

    [Fact]
    public void ATurnNeverSendsAnAngularForceOrAForceOrAVelocity()
    {
        var f = Make();
        f.Start();
        for (int i = 1; i <= 40; i++)
        {
            f.Spin(Turn(i * 2f));
            f.Step();
        }
        Assert.NotEmpty(f.Actor.Calls);
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("AddAngularForce=") || c.StartsWith("AddForce=") || c.StartsWith("Velocity="));
        Assert.All(f.Actor.Calls, c => Assert.StartsWith("RotationalVelocity=", c));
    }

    [Fact]
    public void EveryUpdateMovesTheTargetAndASmallOneIsNotDropped()
    {
        // Each update turns the viewer's rotation by half a degree, far under what the old per-update delta could carry.
        var f = Make();
        f.Start();
        var seen = new List<Vector3>();
        foreach (float degrees in new[] { 2f, 2.5f, 3f, 3.5f, 4f })
        {
            f.Spin(Turn(degrees));
            f.Step();
            seen.Add(f.Actor.RotationalVelocity);
            Assert.Equal(LookAtVelocity(Turn(degrees)).Z, f.Actor.RotationalVelocity.Z, 4);
        }
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public void TheTargetStaysWhileTheMouseIsStill()
    {
        // A still mouse sends nothing: the controller keeps steering to the last rotation, every frame.
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        float first = f.Actor.RotationalVelocity.Length();
        f.Actor.Orientation = Turn(45);
        f.Step();
        float second = f.Actor.RotationalVelocity.Length();
        Assert.True(second > 0f && second < first);
    }

    [Fact]
    public void ARotationAndItsNegationGiveTheSameTurn()
    {
        var f = Make();
        f.Start();
        Quaternion q = Turn(90);
        f.Spin(new Quaternion(-q.X, -q.Y, -q.Z, -q.W));
        f.Step();
        Assert.Equal(LookAtVelocity(q).Z, f.Actor.RotationalVelocity.Z, 4);
    }

    [Fact]
    public void ARotationThatIsNotAFiniteNumberIsDropped()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(60));
        f.Spin(new Quaternion(float.NaN, 0, 0, 1));
        f.Spin(new Quaternion(0, 0, float.PositiveInfinity, 1));
        f.Spin(new Quaternion(0, 0, 0, 0));
        f.Step();
        // the target is still the last good one
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(60))) }, f.Actor.Calls);
    }

    [Fact]
    public void AGrabOfAChildPartTurnsTheWholeLinksetThroughTheRootsActorOnly()
    {
        var f = Make(3);
        var child = f.Group.Parts.First(p => p != f.Root);
        var childActor = new Recorder();
        child.PhysActor = childActor;
        f.Start(child);
        f.Spin(Turn(90), child);
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(90))) }, f.Actor.Calls);
        Assert.Empty(childActor.Calls);
    }

    [Fact]
    public void ReleaseByTheHolderEndsTheControllerAndStopsTheSpin()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ReleaseFromAnotherAgentIsIgnored()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Stop(by: f.Other);
        Assert.Empty(f.Actor.Calls);
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(90))) }, f.Actor.Calls);
    }

    [Fact]
    public void AnotherAgentsUpdateTakesTheTurnOver()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(30));
        f.Spin(Turn(90), by: f.Other);
        f.Step();
        Assert.Equal(LookAtVelocity(Turn(90)).Z, f.Actor.RotationalVelocity.Z, 4);
        f.Actor.Clear();
        f.Stop();
        Assert.Empty(f.Actor.Calls);
        f.Stop(by: f.Other);
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
    }

    [Fact]
    public void TheTurnEndsWhenTheHoldersPresenceLeavesTheRegion()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Scene.EventManager.TriggerOnRemovePresence(f.Other.UUID);
        Assert.Empty(f.Actor.Calls);
        f.Scene.EventManager.TriggerOnRemovePresence(f.Holder.UUID);
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
        f.Actor.Clear();
        f.Scene.EventManager.TriggerOnRemovePresence(f.Holder.UUID);
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void TheTurnEndsWhenTheHoldersPresenceBecomesAChildAgent()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Actor.Clear();
        f.Scene.EventManager.TriggerOnMakeChildAgent(f.Holder);
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
    }

    [Fact]
    public void TheHoldersNewStartEndsATurnWhoseReleaseWasLost()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Actor.Clear();
        f.Start();
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void TheHoldersNewGrabEndsATurnWhoseReleaseWasLost()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Actor.Clear();
        f.Grab(by: f.Other);
        Assert.Empty(f.Actor.Calls);
        f.Grab();
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
    }

    [Fact]
    public void TheHoldersDeGrabEndsATurnWhoseReleaseWasLost()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Actor.Clear();
        f.DeGrab(by: f.Other);
        Assert.Empty(f.Actor.Calls);
        f.DeGrab();
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
    }

    [Fact]
    public void ABlockedPartIsNotTurnedAndTheActorIsAskedNothing()
    {
        var f = Make(3);
        var child = f.Group.Parts.First(p => p != f.Root);
        child.BlockGrab = true;
        f.Start(child);
        f.Spin(Turn(60), child);
        f.Spin(Turn(90), child);
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ABlockedRootIsNotTurnedWhicheverPartIsGrabbed()
    {
        var f = Make(3);
        var child = f.Group.Parts.First(p => p != f.Root);
        f.Root.BlockGrab = true;
        f.Start(child);
        f.Spin(Turn(60), child);
        f.Spin(Turn(90), child);
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ABlockedObjectIsNotTurned()
    {
        var f = Make(3);
        f.Group.BlockGrabOverride = true;
        f.Start();
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ASelectedObjectIsNotTurned()
    {
        var f = Make();
        f.Group.IsSelected = true;
        f.Actor.Clear();
        f.Start();
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AnAttachmentIsNotTurned()
    {
        var f = Make();
        f.Group.IsAttachment = true;
        f.Start();
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AVehicleIsNotTurned()
    {
        var f = Make();
        f.Root.VehicleType = (int)Vehicle.TYPE_CAR;
        f.Actor.Clear();
        f.Start();
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AnObjectWithAnAvatarSittingOnItIsNotTurned()
    {
        var f = Make();
        SitOn(f.Group, f.Other);
        f.Start();
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AnObjectSelectedDuringTheTurnIsLetGo()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Group.IsSelected = true;
        f.Actor.Clear();
        f.Spin(Turn(95));
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
    }

    [Fact]
    public void ABlockSetDuringTheTurnEndsIt()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Group.BlockGrabOverride = true;
        f.Actor.Clear();
        f.Spin(Turn(95));
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AnObjectThatStopsBeingPhysicalDuringTheTurnIsLetGo()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Actor.IsPhysical = false;
        f.Actor.Clear();
        f.Spin(Turn(95));
        f.Actor.IsPhysical = true;
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ATurnCarriesOnWhenTheRootsActorIsReplaced()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        var replacement = new Recorder { Position = Rest };
        f.Root.PhysActor = replacement;
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(90))) }, replacement.Calls);
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AScriptsLookAtDuringTheTurnIsOnlyRememberedAndStartsAgainOnRelease()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Group.RotLookAt(Turn(-60), 2f, 0.5f);
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(90))) }, f.Actor.Calls);
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(-60), 2f)) }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsLookAtHeldBeforeTheTurnYieldsToItAndComesBackOnRelease()
    {
        var f = Make();
        f.Group.RotLookAt(Turn(-60), 2f, 0.5f);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(90))) }, f.Actor.Calls);
        f.Actor.Clear();
        f.Stop();
        f.Actor.Clear();
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(-60), 2f)) }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsStopLookAtDuringTheTurnIsOnlyRememberedAndTheLookAtStaysOffAfterRelease()
    {
        var f = Make();
        f.Group.RotLookAt(Turn(-60), 2f, 0.5f);
        f.Start();
        f.Spin(Turn(90));
        f.Actor.Clear();
        f.Root.StopLookAt();
        f.Group.StopLookAt();
        Assert.Empty(f.Actor.Calls);
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(90))) }, f.Actor.Calls);
        f.Actor.Clear();
        f.Stop();
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AScriptsLookAtWithAStrengthTheControllerRefusesIsStillOnlyRememberedDuringTheTurn()
    {
        // strength under 0.04 stops a look-at when the controller next runs: the turn must not be stopped by it
        var f = Make();
        f.Group.RotLookAt(Turn(-60), 0.01f, 0.5f);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(90))) }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsTargetOmegaDuringTheTurnIsOnlyRememberedAndAppliedAgainOnRelease()
    {
        var f = Make();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Root.UpdateAngularVelocity(new Vector3(0, 0, 1));
        Assert.Empty(f.Actor.Calls);
        f.Stop();
        Assert.Equal(new[] { Omega(Vector3.Zero), Omega(new Vector3(0, 0, 1)) }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsTargetOmegaSetBeforeTheTurnIsAppliedAgainOnRelease()
    {
        var f = Make();
        f.Root.UpdateAngularVelocity(new Vector3(0, 0, 1));
        Assert.Equal(new[] { Omega(new Vector3(0, 0, 1)) }, f.Actor.Calls);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(new[] { Omega(Vector3.Zero), Omega(new Vector3(0, 0, 1)) }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsTargetOmegaOfZeroDuringTheTurnLeavesNoSpinOnRelease()
    {
        var f = Make();
        f.Root.UpdateAngularVelocity(new Vector3(0, 0, 1));
        f.Start();
        f.Spin(Turn(90));
        f.Root.UpdateAngularVelocity(Vector3.Zero);
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
    }

    [Theory]
    [InlineData(0.00001f)]
    [InlineData(0.001f)]
    [InlineData(1f)]
    [InlineData(40f)]
    public void AScriptsTargetOmegaOfAnySizeIsAppliedAgainOnRelease(float rate)
    {
        var f = Make();
        var omega = new Vector3(0, 0, rate);
        f.Root.UpdateAngularVelocity(omega);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(Omega(omega), f.Actor.Calls[^1]);
        Assert.Equal(omega, f.Root.AngularVelocity);
    }

    [Fact]
    public void AScriptsTargetOmegaIsAppliedAgainAfterEveryTurnOfSeveral()
    {
        var f = Make();
        var omega = new Vector3(0, 0, 1);
        f.Root.UpdateAngularVelocity(omega);
        for (int i = 0; i < 3; i++)
        {
            f.Start();
            f.Spin(Turn(30 + 30 * i));
            f.Step();
            f.Actor.Clear();
            f.Stop();
            Assert.Equal(new[] { Omega(Vector3.Zero), Omega(omega) }, f.Actor.Calls);
        }
    }

    [Fact]
    public void AScriptsTargetOmegaIsAppliedAgainWhenTheActorsSpinWasChangedBehindThePartsBack()
    {
        // The part keeps its own copy of the spin and refreshes it only when it is read. The turn's end must not
        // compare the omega with that copy: whatever the engine changed meanwhile, the script's omega is written.
        var f = Make();
        var omega = new Vector3(0, 0, 1);
        f.Root.UpdateAngularVelocity(omega);
        f.Start();
        f.Spin(Turn(90));
        f.Actor.RotationalVelocity = new Vector3(0, 0, 3);
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(Omega(omega), f.Actor.Calls[^1]);
        Assert.Equal(omega, f.Actor.RotationalVelocity);
    }

    [Fact]
    public void AScriptsTargetOmegaOutsideAnyTurnIsAppliedAsBefore()
    {
        var f = Make();
        f.Root.UpdateAngularVelocity(new Vector3(0, 0, 2));
        Assert.Equal(new[] { Omega(new Vector3(0, 0, 2)) }, f.Actor.Calls);
    }

    [Fact]
    public void AMouseDragAndATurnAtOnceKeepTheObjectHeldWhereItIsWhileItTurns()
    {
        // A viewer does this when Ctrl-Shift is pressed in the middle of a drag: the updates change over from
        // ObjectGrabUpdate to ObjectSpinUpdate, and the move-to-target keeps holding the last cursor point.
        var f = Make();
        f.Grab();
        f.Drag(new Vector3(101, 100, 25));
        f.Drag(new Vector3(105, 100, 27));
        f.Actor.Clear();

        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.Equal(new[] { Omega(LookAtVelocity(Turn(90))) }, f.Actor.Calls);
        Assert.True(f.Actor.PIDActive);

        // the turn ends and the object is still held at the cursor
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(new[] { Omega(Vector3.Zero) }, f.Actor.Calls);
        Assert.True(f.Actor.PIDActive);

        // and the drag carries on from the same anchor
        f.Actor.Clear();
        f.Drag(new Vector3(106, 100, 27));
        Assert.Equal(new[] { "PIDTarget=<105.00,100.00,27.00>" }, f.Actor.Calls);
    }

    [Fact]
    public void ATurnDoesNotChangeWhatTheMouseDragSetsOnTheActor()
    {
        var f = Make();
        f.Drag(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("PID"));
    }

    [Fact]
    public void ANonPhysicalObjectIsStillTurnedToTheViewersRotationAndTheActorIsAskedNothing()
    {
        var f = Make();
        f.Actor.IsPhysical = false;
        f.Actor.Clear();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("RotationalVelocity=") || c.StartsWith("AddAngularForce="));
        Assert.True(Quaternion.Dot(Turn(90), f.Root.RotationOffset) > 0.999f);
    }

    [Fact]
    public void ABlockedNonPhysicalObjectIsStillNotTurned()
    {
        var f = Make();
        f.Actor.IsPhysical = false;
        f.Root.BlockGrab = true;
        f.Start();
        f.Spin(Turn(90));
        Assert.True(Quaternion.Dot(Quaternion.Identity, f.Root.RotationOffset) > 0.999f);
    }
}
