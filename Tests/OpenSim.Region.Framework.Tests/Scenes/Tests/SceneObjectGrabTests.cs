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
/// Click-and-drag of a physical object. The viewer sends ObjectGrab once, ObjectGrabUpdate while the mouse moves and
/// ObjectDeGrab once. A drag steers the object's root actor with its move-to-target (target, tau, active) and never
/// sends a force or a velocity; the release switches the target off and puts back what a script had set.
/// </summary>
// Each test builds its own scene and stand-in physics actors and touches no process-wide state, so the class runs in parallel.
public class SceneObjectGrabTests : OpenSimTestCase
{
    private static readonly Vector3 Rest = new(100, 100, 25);

    /// <summary>A physics actor that records, in order, everything core asks of it.</summary>
    private sealed class Recorder : NullPhysicsActor
    {
        private readonly List<string> m_calls = new();
        private bool m_pidActive, m_hoverActive, m_physical = true;
        private Vector3 m_velocity, m_position;
        private Quaternion m_orientation = Quaternion.Identity;

        // A part with a physics actor reads its position from the actor, so the stand-in keeps what it is given.
        public override Vector3 Position { get => m_position; set => m_position = value; }
        public override Quaternion Orientation { get => m_orientation; set => m_orientation = value; }

        public List<string> Calls { get { lock (m_calls) return m_calls.ToList(); } }
        public void Clear() { lock (m_calls) m_calls.Clear(); }
        private void Log(string s) { lock (m_calls) m_calls.Add(s); }

        public override bool IsPhysical { get => m_physical; set => m_physical = value; }
        public override float Mass => 10f;
        public override Vector3 Velocity { get => m_velocity; set { m_velocity = value; Log("Velocity=" + V(value)); } }
        public override void AddForce(Vector3 force, bool pushforce) => Log("AddForce=" + V(force));
        public override void AddAngularForce(Vector3 force, bool pushforce) => Log("AddAngularForce=" + V(force));
        public override Vector3 PIDTarget { set => Log("PIDTarget=" + V(value)); }
        public override float PIDTau { set => Log("PIDTau=" + F(value)); }
        public override bool PIDActive { get => m_pidActive; set { m_pidActive = value; Log("PIDActive=" + value); } }
        public override float PIDHoverHeight { set => Log("PIDHoverHeight=" + F(value)); }
        public override PIDHoverType PIDHoverType { set => Log("PIDHoverType=" + value); }
        public override float PIDHoverTau { set => Log("PIDHoverTau=" + F(value)); }
        public override bool PIDHoverActive { get => m_hoverActive; set { m_hoverActive = value; Log("PIDHoverActive=" + value); } }
        public override bool Selected { set => Log("Selected=" + value); }
        public override int VehicleType { get => 0; set => Log("VehicleType=" + value); }
    }

    private static string F(float f) => f.ToString("F2", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"<{F(v.X)},{F(v.Y)},{F(v.Z)}>";

    private sealed class Fixture
    {
        public TestScene Scene;
        public SceneObjectGroup Group;
        public Recorder Actor;
        public ScenePresence Holder, Other;
        public IClientAPI Client => Holder.ControllingClient;
        public SceneObjectPart Root => Group.RootPart;

        public void Update(Vector3 pos, SceneObjectPart part = null, ScenePresence by = null)
            => Scene.ProcessObjectGrabUpdate((part ?? Root).UUID, Vector3.Zero, pos, (by ?? Holder).ControllingClient, null);
        public void Grab(SceneObjectPart part = null, ScenePresence by = null)
            => Scene.ProcessObjectGrab((part ?? Root).LocalId, Vector3.Zero, (by ?? Holder).ControllingClient, null);
        public void Release(SceneObjectPart part = null, ScenePresence by = null)
            => Scene.ProcessObjectDeGrab((part ?? Root).LocalId, (by ?? Holder).ControllingClient, null);
    }

    /// <summary>A scene with a physical object of <paramref name="parts"/> parts resting at <see cref="Rest"/> and two avatars.</summary>
    private static Fixture Make(int parts = 1)
    {
        var f = new Fixture { Scene = new SceneHelpers().SetupScene() };
        f.Holder = SceneHelpers.AddScenePresence(f.Scene, TestHelpers.ParseTail(0x11));
        f.Other = SceneHelpers.AddScenePresence(f.Scene, TestHelpers.ParseTail(0x12));
        f.Group = SceneHelpers.AddSceneObject(f.Scene, parts, TestHelpers.ParseTail(0x1), "grab", 0x100);
        f.Actor = new Recorder();
        f.Root.PhysActor = f.Actor;
        f.Group.AbsolutePosition = Rest;
        f.Actor.Position = Rest;
        f.Actor.Clear();
        return f;
    }

    [Fact]
    public void TheFirstUpdateSetsTauTargetAndActiveAndNothingElse()
    {
        var f = Make();
        // The first point only fixes where the grab is on the object, so the first target is where the root already is.
        f.Update(new Vector3(105, 100, 25));
        Assert.Equal(new[] { "PIDTau=0.25", "PIDTarget=<100.00,100.00,25.00>", "PIDActive=True" }, f.Actor.Calls);
    }

    [Fact]
    public void LaterUpdatesOnlyMoveTheTargetAndAnUpdateNeverSendsAForceOrAVelocity()
    {
        var f = Make();
        f.Update(new Vector3(101, 100, 25));
        f.Actor.Clear();
        for (int i = 2; i <= 12; i++)
            f.Update(new Vector3(100 + i, 100, 25 + i * 0.1f));
        var calls = f.Actor.Calls;
        Assert.Equal(11, calls.Count);
        Assert.All(calls, c => Assert.StartsWith("PIDTarget=", c));
        Assert.Equal("PIDTarget=<111.00,100.00,26.20>", calls[^1]);
    }

    [Fact]
    public void TheTargetKeepsTheGrabbedPointUnderTheCursor()
    {
        // The first cursor point is on the surface, 1 m east and 0.5 m above the root's centre: the root is not pulled onto the cursor.
        var f = Make();
        f.Update(new Vector3(101, 100, 25.5f));
        f.Actor.Clear();
        f.Update(new Vector3(106, 100, 28.5f));
        Assert.Equal(new[] { "PIDTarget=<105.00,100.00,28.00>" }, f.Actor.Calls);
    }

    [Fact]
    public void AGrabOfAChildPartSteersTheRootsActorOnly()
    {
        var f = Make(3);
        var child = f.Group.Parts.First(p => p != f.Root);
        var childActor = new Recorder();
        child.PhysActor = childActor;
        f.Update(new Vector3(105, 100, 25), child);
        Assert.Equal(new[] { "PIDTau=0.25", "PIDTarget=<100.00,100.00,25.00>", "PIDActive=True" }, f.Actor.Calls);
        Assert.Empty(childActor.Calls);
        f.Release(child);
        Assert.Equal("PIDActive=False", f.Actor.Calls[^1]);
        Assert.Empty(childActor.Calls);
    }

    [Fact]
    public void ReleaseSwitchesTheTargetOffAndClearsNothingElse()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Release();
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void ReleaseLeavesTheVelocityTheObjectHas()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Velocity = new Vector3(1, 2, 3);
        f.Actor.Clear();
        f.Release();
        Assert.Equal(new Vector3(1, 2, 3), f.Actor.Velocity);
        Assert.DoesNotContain(f.Actor.Calls, c => c.StartsWith("Velocity=") || c.StartsWith("AddForce="));
    }

    [Fact]
    public void AClickWithoutAnUpdateAsksTheActorNothing()
    {
        var f = Make();
        f.Group.MoveToTarget(new Vector3(110, 100, 25), 2f);
        f.Actor.Clear();
        f.Grab();
        f.Release();
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AScriptsMoveToTargetIsSwitchedOffForTheDragAndPutBackOnRelease()
    {
        var f = Make();
        f.Group.MoveToTarget(new Vector3(110, 100, 25), 2f);
        f.Actor.Clear();
        f.Update(new Vector3(105, 100, 25));
        // off first, so an engine that reads tau only when it activates takes the drag's
        Assert.Equal(new[] { "PIDActive=False", "PIDTau=0.25", "PIDTarget=<100.00,100.00,25.00>", "PIDActive=True" }, f.Actor.Calls);
        f.Actor.Clear();
        f.Release();
        Assert.Equal(new[] { "PIDTarget=<110.00,100.00,25.00>", "PIDTau=2.00", "PIDActive=True" }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsMoveToTargetDuringTheDragIsRecordedAndAppliedOnRelease()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Group.MoveToTarget(new Vector3(120, 100, 25), 3f);
        Assert.Empty(f.Actor.Calls);
        f.Release();
        Assert.Equal(new[] { "PIDTarget=<120.00,100.00,25.00>", "PIDTau=3.00", "PIDActive=True" }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsStopMoveToTargetDuringTheDragIsNotUndoneOnRelease()
    {
        var f = Make();
        f.Group.MoveToTarget(new Vector3(110, 100, 25), 2f);
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Group.StopMoveToTarget();
        Assert.Empty(f.Actor.Calls);
        f.Release();
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsStopBeforeTheDragLeavesNothingToRestore()
    {
        var f = Make();
        f.Group.MoveToTarget(new Vector3(110, 100, 25), 2f);
        f.Group.StopMoveToTarget();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Release();
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsHoverIsSwitchedOffForTheDragAndPutBackOnRelease()
    {
        var f = Make();
        f.Group.SetHoverHeight(2f, PIDHoverType.Ground, 1f);
        f.Actor.Clear();
        f.Update(new Vector3(105, 100, 25));
        Assert.Equal(new[] { "PIDHoverActive=False", "PIDTau=0.25", "PIDTarget=<100.00,100.00,25.00>", "PIDActive=True" }, f.Actor.Calls);
        f.Actor.Clear();
        f.Release();
        Assert.Equal(new[]
        {
            "PIDActive=False", "PIDHoverHeight=2.00", "PIDHoverType=Ground", "PIDHoverTau=1.00", "PIDHoverActive=True"
        }, f.Actor.Calls);
    }

    [Fact]
    public void AHoverTheScriptStopsDuringTheDragStaysOffOnRelease()
    {
        var f = Make();
        f.Group.SetHoverHeight(2f, PIDHoverType.Ground, 1f);
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Group.SetHoverHeight(0f, PIDHoverType.Ground, 0f);
        Assert.Empty(f.Actor.Calls);
        f.Release();
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void AScriptsTargetAndHoverOutsideAnyDragAreAppliedAsBefore()
    {
        var f = Make();
        f.Group.MoveToTarget(new Vector3(110, 100, 25), 2f);
        f.Group.SetHoverHeight(2f, PIDHoverType.Ground, 1f);
        Assert.Equal(new[]
        {
            "PIDTarget=<110.00,100.00,25.00>", "PIDTau=2.00", "PIDActive=True",
            "PIDHoverHeight=2.00", "PIDHoverType=Ground", "PIDHoverTau=1.00", "PIDHoverActive=True"
        }, f.Actor.Calls);
    }

    [Fact]
    public void ABlockedPartIsNotDraggedAndTheActorIsAskedNothing()
    {
        var f = Make(3);
        var child = f.Group.Parts.First(p => p != f.Root);
        child.BlockGrab = true;
        f.Update(new Vector3(105, 100, 25), child);
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ABlockedRootIsNotDraggedWhicheverPartIsGrabbed()
    {
        var f = Make(3);
        var child = f.Group.Parts.First(p => p != f.Root);
        f.Root.BlockGrab = true;
        f.Update(new Vector3(105, 100, 25), child);
        f.Update(new Vector3(105, 100, 25));
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ABlockedObjectIsNotDragged()
    {
        var f = Make(3);
        var child = f.Group.Parts.First(p => p != f.Root);
        f.Group.BlockGrabOverride = true;
        f.Update(new Vector3(105, 100, 25), child);
        f.Update(new Vector3(105, 100, 25));
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ABlockSetDuringTheDragEndsItAndRestoresTheScriptsTarget()
    {
        var f = Make();
        f.Group.MoveToTarget(new Vector3(110, 100, 25), 2f);
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Group.BlockGrabOverride = true;
        f.Update(new Vector3(106, 100, 25));
        Assert.Equal(new[] { "PIDTarget=<110.00,100.00,25.00>", "PIDTau=2.00", "PIDActive=True" }, f.Actor.Calls);
    }

    [Fact]
    public void ASelectedObjectIsNotDragged()
    {
        var f = Make();
        f.Group.IsSelected = true;
        f.Actor.Clear();
        f.Update(new Vector3(105, 100, 25));
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AnObjectSelectedDuringTheDragIsLetGo()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Group.IsSelected = true;
        f.Actor.Clear();
        f.Update(new Vector3(106, 100, 25));
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void AnAttachmentIsNotDragged()
    {
        var f = Make();
        f.Group.IsAttachment = true;
        f.Update(new Vector3(105, 100, 25));
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AVehicleIsNotDragged()
    {
        var f = Make();
        f.Root.VehicleType = (int)Vehicle.TYPE_CAR;
        f.Actor.Clear();
        f.Update(new Vector3(105, 100, 25));
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AnObjectWithAnAvatarSittingOnItIsNotDragged()
    {
        var f = Make();
        SitOn(f.Group, f.Other);
        f.Update(new Vector3(105, 100, 25));
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void AnObjectThatStopsBeingPhysicalDuringTheDragIsLetGo()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.IsPhysical = false;
        f.Actor.Clear();
        f.Update(new Vector3(106, 100, 25));
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void ADragCarriesOnWhenTheRootsActorIsReplaced()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        var replacement = new Recorder { Position = Rest };
        f.Root.PhysActor = replacement;
        f.Actor.Clear();
        f.Update(new Vector3(106, 100, 25));
        Assert.Empty(f.Actor.Calls);
        Assert.Equal(new[] { "PIDTau=0.25", "PIDTarget=<101.00,100.00,25.00>", "PIDActive=True" }, replacement.Calls);
        replacement.Clear();
        f.Release();
        Assert.Equal(new[] { "PIDActive=False" }, replacement.Calls);
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void ATargetThatIsNotAFiniteNumberIsDropped()
    {
        var f = Make();
        f.Update(new Vector3(float.NaN, 100, 25));
        f.Update(new Vector3(float.PositiveInfinity, 100, 25));
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void OnlyTheHoldersReleaseEndsTheDrag()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Release(by: f.Other);
        Assert.Empty(f.Actor.Calls);
        f.Release();
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void AnotherAgentsUpdateTakesTheDragOverWithoutRestartingIt()
    {
        var f = Make();
        f.Update(new Vector3(101, 100, 25));
        f.Update(new Vector3(103, 100, 25));
        f.Actor.Clear();
        f.Update(new Vector3(110, 100, 25), by: f.Other);
        // the new holder's own first point is the anchor: the object does not jump at the takeover
        Assert.Equal(new[] { "PIDTarget=<100.00,100.00,25.00>" }, f.Actor.Calls);
        f.Actor.Clear();
        f.Release();
        Assert.Empty(f.Actor.Calls);
        f.Release(by: f.Other);
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void TheHoldersNewGrabEndsADragWhoseReleaseWasLost()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Grab();
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void AnotherAgentsGrabLeavesTheDragAlone()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Grab(by: f.Other);
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void TheDragEndsWhenTheHoldersPresenceLeavesTheRegion()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Scene.EventManager.TriggerOnRemovePresence(f.Other.UUID);
        Assert.Empty(f.Actor.Calls);
        f.Scene.EventManager.TriggerOnRemovePresence(f.Holder.UUID);
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
        f.Actor.Clear();
        f.Scene.EventManager.TriggerOnRemovePresence(f.Holder.UUID);
        Assert.Empty(f.Actor.Calls);
    }

    [Fact]
    public void TheDragEndsWhenTheHoldersPresenceBecomesAChildAgent()
    {
        var f = Make();
        f.Update(new Vector3(105, 100, 25));
        f.Actor.Clear();
        f.Scene.EventManager.TriggerOnMakeChildAgent(f.Holder);
        Assert.Equal(new[] { "PIDActive=False" }, f.Actor.Calls);
    }

    [Fact]
    public void ANonPhysicalObjectIsStillPutAtTheCursorAndTheActorIsAskedNothing()
    {
        var f = Make();
        f.Actor.IsPhysical = false;
        f.Update(new Vector3(105, 100, 25));
        Assert.Empty(f.Actor.Calls);
        Assert.Equal(new Vector3(105, 100, 25), f.Group.AbsolutePosition);
    }

    [Fact]
    public void ABlockedPartOfANonPhysicalObjectIsStillNotMoved()
    {
        var f = Make();
        f.Actor.IsPhysical = false;
        f.Root.BlockGrab = true;
        f.Update(new Vector3(105, 100, 25));
        Assert.Equal(Rest, f.Group.AbsolutePosition);
    }

    private static void SitOn(SceneObjectGroup group, ScenePresence sitter)
    {
        var list = (List<ScenePresence>)typeof(SceneObjectGroup)
            .GetField("m_sittingAvatars", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(group)!;
        lock (list) list.Add(sitter);
    }
}
