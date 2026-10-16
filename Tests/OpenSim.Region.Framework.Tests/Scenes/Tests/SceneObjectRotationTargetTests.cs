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
using System.Threading;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A physics scene that says it has a rotation target (PhysicsScene.SupportsRotationTarget) makes core hand the root actor
/// the target of llRotLookAt, llLookAt and the rotate-drag, with its strength and damping, and switch it on, instead of
/// steering the body with core's own look-at controller. A scene that says no gets exactly what it got before.
/// </summary>
// Each test builds its own scene, physics scene and stand-in actors and touches no process-wide state, so the class runs in parallel.
public class SceneObjectRotationTargetTests : OpenSimTestCase
{
    private static readonly Vector3 Rest = new(100, 100, 25);

    /// <summary>A physics scene with a fixed answer to "does the engine have a rotation target?", counting how often it is asked.</summary>
    private sealed class AnswerScene : BasicScene
    {
        private readonly bool m_answer;
        private int m_asked;
        public AnswerScene(bool answer) { m_answer = answer; }
        public int Asked => Volatile.Read(ref m_asked);
        public override bool SupportsRotationTarget() { Interlocked.Increment(ref m_asked); return m_answer; }
    }

    /// <summary>A physics actor that records, in order, everything core asks of it.</summary>
    private sealed class Recorder : NullPhysicsActor
    {
        private readonly List<string> m_calls = new();
        private bool m_physical = true;
        private Vector3 m_rotational, m_position;
        private Quaternion m_orientation = Quaternion.Identity;

        public List<string> Calls { get { lock (m_calls) return m_calls.ToList(); } }
        public void Clear() { lock (m_calls) m_calls.Clear(); }
        private void Log(string s) { lock (m_calls) m_calls.Add(s); }

        public override Vector3 Position { get => m_position; set => m_position = value; }
        public override Quaternion Orientation { get => m_orientation; set => m_orientation = value; }
        public override bool IsPhysical { get => m_physical; set => m_physical = value; }
        public override float Mass => 10f;
        public override Vector3 RotationalVelocity { get => m_rotational; set { m_rotational = value; Log("RotationalVelocity=" + V(value)); } }
        public override void AddAngularForce(Vector3 force, bool pushforce) => Log("AddAngularForce=" + V(force));
        public override Quaternion APIDTarget { set => Log("Target=" + Q(value)); }
        public override float APIDStrength { set => Log("Strength=" + F(value)); }
        public override float APIDDamping { set => Log("Damping=" + F(value)); }
        public override bool APIDActive { set => Log("Active=" + value); }
        public override bool Selected { set => Log("Selected=" + value); }
        public override int VehicleType { get => 0; set => Log("VehicleType=" + value); }
    }

    private static string F(float f) => f.ToString("F2", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"<{F(v.X)},{F(v.Y)},{F(v.Z)}>";
    private static string Q(Quaternion q) => $"<{q.X.ToString("F3", CultureInfo.InvariantCulture)},{q.Y.ToString("F3", CultureInfo.InvariantCulture)},{q.Z.ToString("F3", CultureInfo.InvariantCulture)},{q.W.ToString("F3", CultureInfo.InvariantCulture)}>";

    private static Quaternion Turn(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    private static string Target(Quaternion q) => "Target=" + Q(q);
    private static string Strength(float s) => "Strength=" + F(s);
    private static string Damping(float d) => "Damping=" + F(d);
    private const string On = "Active=True";
    private const string Off = "Active=False";

    // what a rotate-drag asks of an engine with a rotation target: a short time to the viewer's rotation
    private const float DragSeconds = 0.1f;

    private sealed class Fixture
    {
        public TestScene Scene;
        public AnswerScene Physics;
        public SceneObjectGroup Group;
        public Recorder Actor;
        public ScenePresence Holder, Other;
        public SceneObjectPart Root => Group.RootPart;

        public void Start() => Scene.ProcessSpinStart(Root.UUID, Holder.ControllingClient);
        public void Spin(Quaternion rotation, ScenePresence by = null)
            => Scene.ProcessSpinObject(Root.UUID, rotation, (by ?? Holder).ControllingClient);
        public void Stop() => Scene.ProcessSpinObjectStop(Root.UUID, Holder.ControllingClient);

        /// <summary>One pass of the look-at controller, which the scene runs once a frame.</summary>
        public void Step() => Root.UpdateLookAt();

        /// <summary>A look-at the way a script sets it, then the frame that carries it out.</summary>
        public void LookAt(Quaternion target, float strength = 0.5f, float damping = 0.25f)
        {
            Group.RotLookAt(target, strength, damping);
            Step();
        }
    }

    /// <summary>A scene whose physics answers <paramref name="engineHasTarget"/>, with a physical object resting at <see cref="Rest"/> and two avatars.</summary>
    private static Fixture Make(bool engineHasTarget, int parts = 1)
    {
        var f = new Fixture { Scene = new SceneHelpers().SetupScene() };
        f.Physics = new AnswerScene(engineHasTarget);
        f.Scene.PhysicsScene = f.Physics;
        f.Holder = SceneHelpers.AddScenePresence(f.Scene, TestHelpers.ParseTail(0x11));
        f.Other = SceneHelpers.AddScenePresence(f.Scene, TestHelpers.ParseTail(0x12));
        f.Group = SceneHelpers.AddSceneObject(f.Scene, parts, TestHelpers.ParseTail(0x1), "target", 0x100);
        f.Actor = new Recorder();
        f.Root.PhysActor = f.Actor;
        f.Root.AddFlag(PrimFlags.Physics);
        f.Group.AbsolutePosition = Rest;
        f.Actor.Position = Rest;
        f.Actor.Clear();
        return f;
    }

    // ---- an engine with a rotation target -------------------------------------------------------------------------

    [Fact]
    public void ALookAtGivesTheActorTheTargetStrengthAndDampingAndThenSwitchesItOn()
    {
        var f = Make(true);
        f.LookAt(Turn(90), 0.5f, 0.25f);
        Assert.Equal(new[] { Target(Turn(90)), Strength(0.5f), Damping(0.25f), On }, f.Actor.Calls);
    }

    [Fact]
    public void CoreSteersNothingItselfWhileTheActorHoldsTheTarget()
    {
        var f = Make(true);
        f.LookAt(Turn(90));
        f.Actor.Clear();
        for (int i = 0; i < 5; i++)
            f.Step();
        Assert.Empty(f.Actor.Calls);
        Assert.Equal(Vector3.Zero, f.Actor.RotationalVelocity);
    }

    [Fact]
    public void ALookAtFromLlLookAtIsGivenTheSameWay()
    {
        var f = Make(true);
        f.Group.StartLookAt(Turn(45), 1f, 0.5f);
        f.Step();
        Assert.Equal(new[] { Target(Turn(45)), Strength(1f), Damping(0.5f), On }, f.Actor.Calls);
    }

    [Fact]
    public void ALookAtFromAChildPartGoesToTheRootsActorOnly()
    {
        var f = Make(true, 3);
        var child = f.Group.Parts.First(p => p != f.Root);
        var childActor = new Recorder();
        child.PhysActor = childActor;
        child.RotLookAt(Turn(90), 0.5f, 0.25f);
        f.Step();
        Assert.Equal(new[] { Target(Turn(90)), Strength(0.5f), Damping(0.25f), On }, f.Actor.Calls);
        Assert.Empty(childActor.Calls);
    }

    [Fact]
    public void ANewTargetWhileOnChangesOnlyTheTarget()
    {
        var f = Make(true);
        f.LookAt(Turn(90));
        f.Actor.Clear();
        f.LookAt(Turn(120));
        Assert.Equal(new[] { Target(Turn(120)) }, f.Actor.Calls);
    }

    [Fact]
    public void ANewStrengthOrDampingWhileOnChangesOnlyThatAndDoesNotSwitchTheTargetOffAndOn()
    {
        var f = Make(true);
        f.LookAt(Turn(90), 0.5f, 0.25f);
        f.Actor.Clear();
        f.LookAt(Turn(90), 0.75f, 0.25f);
        Assert.Equal(new[] { Strength(0.75f) }, f.Actor.Calls);
        f.Actor.Clear();
        f.LookAt(Turn(90), 0.75f, 0.5f);
        Assert.Equal(new[] { Damping(0.5f) }, f.Actor.Calls);
    }

    [Fact]
    public void StoppingTheLookAtSwitchesTheTargetOffAndWritesNoAngularVelocity()
    {
        var f = Make(true);
        f.LookAt(Turn(90));
        f.Actor.Clear();
        f.Root.StopLookAt();
        Assert.Equal(new[] { Off }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void StoppingTheLookAtFromTheGroupSwitchesTheTargetOffOnce()
    {
        var f = Make(true);
        f.LookAt(Turn(90));
        f.Actor.Clear();
        f.Group.StopLookAt();
        Assert.Equal(new[] { Off }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ALookAtGivenAgainAfterAStopIsGivenInFull()
    {
        var f = Make(true);
        f.LookAt(Turn(90), 0.5f, 0.25f);
        f.Group.StopLookAt();
        f.Actor.Clear();
        f.LookAt(Turn(90), 0.5f, 0.25f);
        Assert.Equal(new[] { Target(Turn(90)), Strength(0.5f), Damping(0.25f), On }, f.Actor.Calls);
    }

    [Fact]
    public void AStrengthUnderTheControllersMinimumIsNotGivenAndLeavesNoTargetOn()
    {
        var f = Make(true);
        f.Group.RotLookAt(Turn(90), 0.01f, 0.25f);
        f.Step();
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("Target=") || c.StartsWith("Strength=") || c.StartsWith("Damping=") || c == On);
    }

    [Theory]
    [InlineData(float.NaN, 0.5f, 0.25f)]
    [InlineData(float.PositiveInfinity, 0.5f, 0.25f)]
    [InlineData(0f, float.NaN, 0.25f)]
    [InlineData(0f, float.PositiveInfinity, 0.25f)]
    [InlineData(0f, 0.5f, float.NaN)]
    [InlineData(0f, 0.5f, float.NegativeInfinity)]
    public void ATargetStrengthOrDampingThatIsNotFiniteIsNotGiven(float targetX, float strength, float damping)
    {
        var f = Make(true);
        f.Group.RotLookAt(new Quaternion(targetX, 0, 0, 1), strength, damping);
        f.Step();
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("Target=") || c.StartsWith("Strength=") || c.StartsWith("Damping=") || c == On);
        Assert.DoesNotContain(f.Actor.Calls, c => c.Contains("NaN") || c.Contains("Infinity"));
    }

    [Fact]
    public void ARequestThatIsNotFiniteEndsATargetThatWasOn()
    {
        var f = Make(true);
        f.LookAt(Turn(90));
        f.Actor.Clear();
        f.Group.RotLookAt(Turn(90), float.NaN, 0.25f);
        f.Step();
        Assert.Equal(new[] { Off }, f.Actor.Calls);
    }

    [Fact]
    public void TheSceneIsAskedOncePerFrameAndNeverForAnObjectWithNoLookAt()
    {
        var f = Make(true);
        f.Step();
        Assert.Equal(0, f.Physics.Asked);
        f.Group.RotLookAt(Turn(90), 0.5f, 0.25f);
        int before = f.Physics.Asked;
        f.Step();
        Assert.Equal(before + 1, f.Physics.Asked);
    }

    // ---- the rotate-drag on an engine with a rotation target -----------------------------------------------------

    [Fact]
    public void TheDragGivesTheViewersRotationWithASmallStrengthAndDampingAndNeverSteersItself()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.Equal(new[] { Target(Turn(90)), Strength(DragSeconds), Damping(DragSeconds), On }, f.Actor.Calls);
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("RotationalVelocity=") || c.StartsWith("AddAngularForce="));
    }

    [Fact]
    public void EveryUpdateOfTheDragMovesTheTargetAndNothingElse()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(10));
        f.Step();
        f.Actor.Clear();
        foreach (float degrees in new[] { 10.5f, 11f, 12f, 30f })
        {
            f.Spin(Turn(degrees));
            f.Step();
        }
        Assert.Equal(new[] { 10.5f, 11f, 12f, 30f }.Select(d => Target(Turn(d))).ToArray(), f.Actor.Calls.ToArray());
    }

    [Fact]
    public void AStillMouseSendsNothingMore()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        for (int i = 0; i < 5; i++)
            f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void TheDragTargetIsTheShortWayRoundFromTheObjectsRotation()
    {
        var f = Make(true);
        f.Start();
        Quaternion q = Turn(90);
        f.Spin(new Quaternion(-q.X, -q.Y, -q.Z, -q.W));
        f.Step();
        Assert.Equal(Target(q), f.Actor.Calls[0]);
    }

    [Fact]
    public void ARotationThatIsNotFiniteIsNotGivenAsTheDragTarget()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(60));
        f.Step();
        f.Actor.Clear();
        f.Spin(new Quaternion(float.NaN, 0, 0, 1));
        f.Spin(new Quaternion(0, 0, float.PositiveInfinity, 1));
        f.Spin(new Quaternion(0, 0, 0, 0));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ReleaseSwitchesTheTargetOffAndWritesNoAngularVelocity()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(new[] { Off }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ReleaseFromAnotherAgentLeavesTheTargetOn()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Scene.ProcessSpinObjectStop(f.Root.UUID, f.Other.ControllingClient);
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void TheHoldersPresenceLeavingSwitchesTheTargetOff()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Scene.EventManager.TriggerOnRemovePresence(f.Holder.UUID);
        Assert.Equal(new[] { Off }, f.Actor.Calls);
    }

    [Fact]
    public void ADragWhoseObjectIsNoLongerPhysicalEndsAndGivesNothingMore()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.IsPhysical = false;
        f.Actor.Clear();
        f.Step();
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("Target=") || c == On);
        f.Actor.IsPhysical = true;
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AVehicleIsNotTurnedByTheDragAndIsGivenNothing()
    {
        var f = Make(true);
        f.Root.VehicleType = (int)Vehicle.TYPE_CAR;
        f.Actor.Clear();
        f.Start();
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ASelectedObjectIsNotTurnedByTheDragAndIsGivenNothing()
    {
        var f = Make(true);
        f.Group.IsSelected = true;
        f.Actor.Clear();
        f.Start();
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("Target=") || c == On);
    }

    [Fact]
    public void ABlockedObjectIsNotGivenATargetByTheDrag()
    {
        var f = Make(true);
        f.Root.BlockGrab = true;
        f.Start();
        f.Spin(Turn(60));
        f.Spin(Turn(90));
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ANonPhysicalObjectIsStillTurnedDirectlyAndTheActorIsGivenNothing()
    {
        var f = Make(true);
        f.Actor.IsPhysical = false;
        f.Actor.Clear();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("Target=") || c == On);
        Assert.True(Quaternion.Dot(Turn(90), f.Root.RotationOffset) > 0.999f);
    }

    // ---- a script's look-at and the drag, on an engine with a rotation target ---------------------------------------

    [Fact]
    public void ADragOverAScriptsLookAtTakesTheTargetWithoutSwitchingItOffAndOn()
    {
        var f = Make(true);
        f.LookAt(Turn(-60), 2f, 0.5f);
        f.Actor.Clear();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.Equal(new[] { Target(Turn(90)), Strength(DragSeconds), Damping(DragSeconds) }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsLookAtDuringTheDragIsOnlyRememberedAndIsGivenAgainOnRelease()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Group.RotLookAt(Turn(-60), 2f, 0.5f);
        f.Step();
        Assert.Empty(f.Actor.Calls);

        f.Stop();
        Assert.Equal(new[] { Off }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Equal(new[] { Target(Turn(-60)), Strength(2f), Damping(0.5f), On }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsLookAtHeldBeforeTheDragComesBackWhole()
    {
        var f = Make(true);
        f.LookAt(Turn(-60), 2f, 0.5f);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Stop();
        f.Actor.Clear();
        f.Step();
        Assert.Equal(new[] { Target(Turn(-60)), Strength(2f), Damping(0.5f), On }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsStopLookAtDuringTheDragLeavesTheDragTargetOnAndTheLookAtStaysOffAfterRelease()
    {
        var f = Make(true);
        f.LookAt(Turn(-60), 2f, 0.5f);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Root.StopLookAt();
        f.Group.StopLookAt();
        f.Step();
        Assert.Empty(f.Actor.Calls);
        f.Stop();
        Assert.Equal(new[] { Off }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AScriptsLookAtTheControllerWouldRefuseIsStillOnlyRememberedDuringTheDrag()
    {
        var f = Make(true);
        f.Group.RotLookAt(Turn(-60), 0.01f, 0.5f);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        Assert.Equal(new[] { Target(Turn(90)), Strength(DragSeconds), Damping(DragSeconds), On }, f.Actor.Calls);
        f.Actor.Clear();
        f.Step();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AScriptsTargetOmegaIsRememberedDuringTheDragAndAppliedAgainOnRelease()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.Clear();
        f.Root.UpdateAngularVelocity(new Vector3(0, 0, 1));
        Assert.Empty(f.Actor.Calls);
        f.Stop();
        Assert.Equal(new[] { Off, "RotationalVelocity=" + V(new Vector3(0, 0, 1)) }, f.Actor.Calls);
    }

    [Theory]
    [InlineData(0.00001f)]
    [InlineData(1f)]
    [InlineData(40f)]
    public void AScriptsTargetOmegaSetBeforeTheDragIsAppliedAgainOnReleaseWhateverThePartsCopyHolds(float rate)
    {
        // The part's own copy of the spin equals the omega the script set, and nothing has read it since, while the
        // engine's target (switched off at the release, no zero written) stopped the real spin. The omega must still reach the actor.
        var f = Make(true);
        var omega = new Vector3(0, 0, rate);
        f.Root.UpdateAngularVelocity(omega);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Actor.RotationalVelocity = Vector3.Zero;
        f.Actor.Clear();
        f.Stop();
        Assert.Equal(new[] { Off, "RotationalVelocity=" + V(omega) }, f.Actor.Calls);
        Assert.Equal(omega, f.Actor.RotationalVelocity);
    }

    [Fact]
    public void AScriptsTargetOmegaIsAppliedAgainAfterEveryDragOfSeveral()
    {
        var f = Make(true);
        var omega = new Vector3(0, 0, 1);
        f.Root.UpdateAngularVelocity(omega);
        for (int i = 0; i < 3; i++)
        {
            f.Start();
            f.Spin(Turn(30 + 30 * i));
            f.Step();
            f.Actor.RotationalVelocity = Vector3.Zero;
            f.Actor.Clear();
            f.Stop();
            Assert.Equal(new[] { Off, "RotationalVelocity=" + V(omega) }, f.Actor.Calls);
        }
    }

    [Fact]
    public void ALookAtOnAVehicleIsGivenAsItIsSteeredToday()
    {
        var f = Make(true);
        f.Root.VehicleType = (int)Vehicle.TYPE_CAR;
        f.Actor.Clear();
        f.LookAt(Turn(90), 0.5f, 0.25f);
        Assert.Equal(new[] { Target(Turn(90)), Strength(0.5f), Damping(0.25f), On }, f.Actor.Calls);
    }

    // ---- an actor that is replaced --------------------------------------------------------------------------------

    [Fact]
    public void AReplacementActorIsGivenTheLookAtInFull()
    {
        var f = Make(true);
        f.LookAt(Turn(90), 0.5f, 0.25f);
        f.Actor.Clear();
        var replacement = new Recorder { Position = Rest };
        f.Root.PhysActor = replacement;
        f.Step();
        Assert.Equal(new[] { Target(Turn(90)), Strength(0.5f), Damping(0.25f), On }, replacement.Calls);
        Assert.Empty(f.Actor.Calls);
        replacement.Clear();
        f.Step();
        Assert.Empty(replacement.Calls);
    }

    [Fact]
    public void AReplacementActorIsGivenTheDragTargetInFull()
    {
        var f = Make(true);
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        var replacement = new Recorder { Position = Rest };
        f.Root.PhysActor = replacement;
        f.Step();
        Assert.Equal(new[] { Target(Turn(90)), Strength(DragSeconds), Damping(DragSeconds), On }, replacement.Calls);
        replacement.Clear();
        f.Stop();
        Assert.Equal(new[] { Off }, replacement.Calls);
    }

    [Fact]
    public void AReplacementThatIsNotPhysicalIsGivenNothing()
    {
        var f = Make(true);
        f.LookAt(Turn(90));
        var replacement = new Recorder { Position = Rest, IsPhysical = false };
        f.Root.PhysActor = replacement;
        f.Step();
        Assert.DoesNotContain(replacement.Calls, c => c.StartsWith("Target=") || c == On);
    }

    // ---- an engine without a rotation target -------------------------------------------------------------------

    /// <summary>The same sequence of script calls and viewer events, whatever the physics scene.</summary>
    private static void Run(Fixture f)
    {
        f.LookAt(Turn(90), 0.5f, 0.25f);
        f.Step();
        f.LookAt(Turn(120), 1f, 0.25f);
        f.Group.StartLookAt(Turn(30), 1f, 0.5f);
        f.Step();
        f.Start();
        f.Spin(Turn(40));
        f.Step();
        f.Spin(Turn(50));
        f.Step();
        f.Group.RotLookAt(Turn(-60), 2f, 0.5f);
        f.Step();
        f.Root.UpdateAngularVelocity(new Vector3(0, 0, 1));
        f.Stop();
        f.Step();
        f.Group.StopLookAt();
        f.Step();
        f.Group.RotLookAt(Turn(10), 0.01f, 0.5f);
        f.Step();
    }

    [Fact]
    public void ASceneThatSaysNoGetsNoTargetStrengthDampingOrSwitchOn()
    {
        var f = Make(false);
        Run(f);
        Assert.NotEmpty(f.Actor.Calls);
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("Target=") || c.StartsWith("Strength=") || c.StartsWith("Damping=") || c == On);
    }

    [Fact]
    public void ASceneThatSaysNoGetsTheCallsItGotBeforeTheAnswerExisted()
    {
        // the physics scene of a test region is the default one, which does not override the question
        var before = new Fixture { Scene = new SceneHelpers().SetupScene() };
        before.Holder = SceneHelpers.AddScenePresence(before.Scene, TestHelpers.ParseTail(0x11));
        before.Other = SceneHelpers.AddScenePresence(before.Scene, TestHelpers.ParseTail(0x12));
        before.Group = SceneHelpers.AddSceneObject(before.Scene, 1, TestHelpers.ParseTail(0x1), "target", 0x100);
        before.Actor = new Recorder();
        before.Root.PhysActor = before.Actor;
        before.Root.AddFlag(PrimFlags.Physics);
        before.Group.AbsolutePosition = Rest;
        before.Actor.Position = Rest;
        before.Actor.Clear();
        Run(before);

        var after = Make(false);
        Run(after);

        Assert.Equal(before.Actor.Calls, after.Actor.Calls);
        Assert.Contains(after.Actor.Calls, c => c.StartsWith("RotationalVelocity=") && c != "RotationalVelocity=" + V(Vector3.Zero));
    }

    [Fact]
    public void ASceneWithNoPhysicsAtAllIsNotAnError()
    {
        var f = Make(true);
        f.Scene.PhysicsScene = null;
        f.Group.RotLookAt(Turn(90), 0.5f, 0.25f);
        f.Step();
        f.Start();
        f.Spin(Turn(90));
        f.Step();
        f.Stop();
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("Target=") || c == On);
    }

    [Fact]
    public void ASceneThatSaysNoStillRunsCoresOwnController()
    {
        var f = Make(false);
        f.LookAt(Turn(90), 1f, 0.5f);
        Assert.Single(f.Actor.Calls);
        Assert.StartsWith("RotationalVelocity=", f.Actor.Calls[0]);
        Assert.True(f.Actor.RotationalVelocity.Length() > 1f);
    }
}
