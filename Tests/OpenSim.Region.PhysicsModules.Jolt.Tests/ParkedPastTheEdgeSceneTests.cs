/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical object that leaves a region with no neighbour, through a real region scene running on the Jolt engine: the
/// engine reports it past the edge, core starts the crossing (SceneObjectPart.PhysicsRequestingTerseUpdate,
/// SceneObjectGroup.CrossAsync), finds no region and puts the object back half a metre inside, stopped, and calls the
/// actor's CrossingFailure (CrossAsyncCompleted). The region's transfer module here answers "no region" only when the test
/// lets it, so the object waits past the edge for as long as the test needs.
///
/// <para>While it waits the object keeps its one body, parked, as ubODE keeps its body and disables it
/// (ODEPrim.UpdatePositionAndVelocity and CrossingStart call disableBodySoft and UnSubscribeEvents; Move and
/// UpdatePositionAndVelocity skip a body that is out of bounds): it touches nothing, raises no collision event, takes no
/// force, impulse or velocity, its vehicle is not driven, its move to target, rotation target, hover and buoyancy do not
/// act, and the engine reports nothing for it. Put back, it carries on with the same body, as ubODE's CrossingFailure
/// enables the same body again (changeDisable(false)), with its targets, hover and buoyancy still set and its vehicle's
/// motors off (ODEDynamics.Stop).</para>
///
/// Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class ParkedPastTheEdgeSceneTests
{
    private readonly ITestOutputHelper _out;
    public ParkedPastTheEdgeSceneTests(ITestOutputHelper output) { _out = output; }
    private void Say(string line) => _out.WriteLine(line);

    private const float Half = 0.25f;   // the 0.5 m box's half size
    private static readonly Vector3 Box = new(0.5f, 0.5f, 0.5f);

    /// <summary>
    /// The region's transfer module: asked where an object goes, it waits until the test lets it answer, then says there is
    /// no region (null), as a grid with no neighbour does. Everything else answers nothing.
    /// </summary>
    public class HeldTransfer : DispatchProxy
    {
        internal readonly ManualResetEventSlim Answer = new(false);
        internal int Asked;

        internal static IEntityTransferModule Create(out HeldTransfer held)
        {
            IEntityTransferModule proxy = Create<IEntityTransferModule, HeldTransfer>();
            held = (HeldTransfer)(object)proxy;
            return proxy;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IEntityTransferModule.GetObjectDestination))
            {
                Interlocked.Increment(ref Asked);
                Answer.Wait(TimeSpan.FromSeconds(60));
                args[2] = Vector3.Zero;
                return null;
            }
            Type t = method.ReturnType;
            return t.IsValueType && t != typeof(void) ? Activator.CreateInstance(t) : null;
        }
    }

    private sealed class Region : IDisposable
    {
        public readonly TestScene Scene;
        public readonly JoltScene Jolt;
        public readonly HeldTransfer Transfer;
        public int Heartbeats;
        private int _nextId = 0x1000;

        public Region()
        {
            Jolt = NewScene(Scratch());
            Scene = new SceneHelpers().SetupScene();
            Scene.PhysicsScene = Jolt;
            Scene.RegisterModuleInterface(HeldTransfer.Create(out Transfer));
        }

        public SceneObjectPart Physical(string name, Vector3 at, Vector3 size)
        {
            SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, UUID.Random(), name, _nextId += 0x10);
            SceneObjectPart part = so.RootPart;
            part.Shape = PrimitiveBaseShape.CreateBox();
            part.Scale = size;
            so.AbsolutePosition = at;
            Scene.AddNewSceneObject(so, false);
            so.ScriptSetPhysicsStatus(true);
            Assert.True(part.PhysActor is { IsPhysical: true }, $"{name} is not physical");
            return part;
        }

        public void Step() { Jolt.Simulate(Heartbeat); Heartbeats++; }

        public void Steps(double seconds)
        {
            for (int i = 0; i < (int)Math.Round(seconds / Heartbeat); i++)
                Step();
        }

        public int Live => Jolt.CapacityStats().LiveBodyCount;
        public int Active => Jolt.CapacityStats().ActiveBodyCount;

        public void Dispose()
        {
            Transfer.Answer.Set();
            Jolt.Dispose();
        }
    }

    /// <summary>What the object and the region did on one heartbeat while it waited.</summary>
    private readonly record struct Beat(int N, Vector3 Position, int Terse, int Collisions, int Active, int Live);

    /// <summary>An object with its engine's reports counted.</summary>
    private sealed class Watched
    {
        public readonly SceneObjectPart Part;
        public int Terse, Collisions;
        public readonly List<uint> Colliders = new();

        public Watched(SceneObjectPart part, bool collisionEvents = false)
        {
            Part = part;
            part.PhysActor.OnRequestTerseUpdate += () => Interlocked.Increment(ref Terse);
            part.PhysActor.OnCollisionUpdate += e =>
            {
                Interlocked.Increment(ref Collisions);
                lock (Colliders) Colliders.AddRange(((CollisionEventUpdate)e).m_objCollisionList.Keys);
            };
            if (collisionEvents)
                part.PhysActor.SubscribeEvents(50);   // what core does for a script with a collision event
        }

        public PhysicsActor Actor => Part.PhysActor;
        public JoltPrim Prim => Assert.IsType<JoltPrim>(Part.PhysActor);
    }

    private static bool Inside(Vector3 p) => p.X >= 0f && p.X < Size && p.Y >= 0f && p.Y < Size;

    // Steps until the engine has reported the object past the edge and core is asking where it goes. Generous caps: the
    // crossing runs on its own thread.
    private static void UntilWaitingPastTheEdge(Region r, Watched o, double seconds = 5)
    {
        for (int i = 0; i < (int)(seconds / Heartbeat) && Inside(o.Actor.Position); i++)
            r.Step();
        Assert.False(Inside(o.Actor.Position), $"never left the region: {o.Actor.Position}");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref r.Transfer.Asked) > 0, TimeSpan.FromSeconds(10)),
                    "core never looked for a region beyond");
        r.Step();   // the heartbeat it left on ends what it was touching (collision_end); the wait is counted from here
    }

    private static List<Beat> Wait(Region r, Watched o, double seconds, Action<int> each = null)
    {
        var beats = new List<Beat>();
        for (int i = 0; i < (int)Math.Round(seconds / Heartbeat); i++)
        {
            each?.Invoke(i);
            r.Step();
            beats.Add(new Beat(i, o.Actor.Position, Volatile.Read(ref o.Terse), Volatile.Read(ref o.Collisions), r.Active, r.Live));
        }
        return beats;
    }

    // Lets core answer "no region" and waits until it has put the object back (CrossAsyncCompleted ends the transit).
    private static void PutBack(Region r, Watched o)
    {
        r.Transfer.Answer.Set();
        SceneObjectGroup g = o.Part.ParentGroup;
        Assert.True(SpinWait.SpinUntil(() => !g.inTransit, TimeSpan.FromSeconds(10)), "core never put the object back");
        Assert.True(Inside(o.Actor.Position), $"put back outside the region: {o.Actor.Position}");
    }

    // The parked contract on every heartbeat of the wait: where it was reported to wait (0.1 to 2 m past the east edge, as
    // ubODE leaves it), no report from the engine and no collision event (core subscribes a physical part for its collision
    // sound), no body awake, and the region's body count unchanged: it keeps its body.
    private void AssertParked(List<Beat> beats, int liveBefore, string what)
    {
        Beat first = beats[0];
        foreach (Beat b in beats)
        {
            Say($"PARKED {what} beat {b.N}: pos {b.Position} terse {b.Terse} coll {b.Collisions} active {b.Active} live {b.Live}");
            Assert.InRange(b.Position.X, Size + 0.09f, Size + 2.01f);
            Assert.True(Vector3.Distance(b.Position, first.Position) < 1e-4f, $"{what}: moved while waiting: {first.Position} -> {b.Position}");
            Assert.Equal(first.Terse, b.Terse);
            Assert.Equal(first.Collisions, b.Collisions);
            Assert.Equal(0, b.Active);
            Assert.Equal(liveBefore, b.Live);
        }
    }

    private Watched Pushed(Region r, Vector3 velocity, bool collisionEvents = false, float y = 128f)
    {
        var o = new Watched(r.Physical("leaving", new Vector3(254.6f, y, Ground + Half), Box), collisionEvents);
        r.Steps(1.0);
        o.Actor.Velocity = velocity;
        return o;
    }

    // ---- 1. its one body, parked ---------------------------------------------------------------------------------

    [Fact]
    public void While_it_waits_past_the_edge_it_keeps_its_one_body_asleep_and_the_engine_reports_nothing()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f));
        int live = r.Live;
        UntilWaitingPastTheEdge(r, o);
        AssertParked(Wait(r, o, 2.0), live, "one body");
    }

    // ---- 2. no contacts ------------------------------------------------------------------------------------------

    // A second box set down inside the edge so that it overlaps the waiting one: nothing pushes it and it hears of no
    // collision with the waiting object.
    [Fact]
    public void While_it_waits_past_the_edge_a_box_set_down_through_it_is_not_touched()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f));
        UntilWaitingPastTheEdge(r, o);
        Vector3 waiting = o.Actor.Position;
        var other = new Watched(r.Physical("in the way", new Vector3(255.9f, waiting.Y, Ground + 0.6f), new Vector3(1.2f, 1.2f, 1.2f)), collisionEvents: true);
        Vector3 placed = other.Actor.Position;
        for (int i = 0; i < 110 && r.Active > 0; i++)   // the new box settles and sleeps, at most 10 s
            r.Step();
        int live = r.Live;
        List<Beat> beats = Wait(r, o, 2.0);
        Say($"CONTACT waiting at {waiting}, other placed {placed} now {other.Actor.Position}, colliders {string.Join(",", other.Colliders)}");
        AssertParked(beats, live, "overlapped");
        Assert.True(MathF.Abs(other.Actor.Position.X - placed.X) < 0.02f && MathF.Abs(other.Actor.Position.Y - placed.Y) < 0.02f,
                    $"the other box was pushed: {placed} -> {other.Actor.Position}");
        lock (other.Colliders)
            Assert.DoesNotContain(o.Part.LocalId, other.Colliders);
    }

    // ---- 3. no collision events ----------------------------------------------------------------------------------

    // Sliding on the ground with a collision event subscribed: its events stop while it waits (ubODE unsubscribes it,
    // ODEPrim.CrossingStart).
    [Fact]
    public void While_it_waits_past_the_edge_it_raises_no_collision_event()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f), collisionEvents: true);
        int live = r.Live;
        UntilWaitingPastTheEdge(r, o);
        int before = Volatile.Read(ref o.Collisions);
        List<Beat> beats = Wait(r, o, 2.0);
        Say($"COLLISIONS before the wait {before}, after it {beats[^1].Collisions}");
        Assert.True(before > 0, "it raised no collision event while it slid on the ground: the test proves nothing");
        Assert.All(beats, b => Assert.Equal(before, b.Collisions));
        AssertParked(beats, live, "collision events");
    }

    // ---- 4. no forces, impulses or velocities --------------------------------------------------------------------

    [Fact]
    public void While_it_waits_past_the_edge_forces_impulses_and_velocities_do_not_move_it()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f));
        int live = r.Live;
        UntilWaitingPastTheEdge(r, o);
        List<Beat> beats = Wait(r, o, 2.0, i =>
        {
            if (i == 1) o.Actor.AddForce(new Vector3(0f, 0f, 500f), true);          // an impulse (llApplyImpulse)
            if (i == 2) o.Actor.AddAngularForce(new Vector3(0f, 0f, 50f), true);    // llApplyRotationalImpulse
            if (i == 3) o.Actor.Velocity = new Vector3(0f, 5f, 3f);                 // llSetVelocity
            if (i == 4) o.Actor.RotationalVelocity = new Vector3(0f, 0f, 3f);       // llSetAngularVelocity
            if (i == 5) o.Actor.AddForce(new Vector3(0f, 0f, 400f), false);         // a push over the step
        });
        AssertParked(beats, live, "forces");
    }

    // ---- 5. no vehicle drive ---------------------------------------------------------------------------------------

    // A car driven off the edge with its motor on: while it waits its controller does not drive the body; put back it
    // stays at rest inside the edge, its motors off (ODEDynamics.Stop).
    [Fact]
    public void A_vehicle_driven_off_the_edge_is_not_driven_while_it_waits_and_comes_back_with_its_motors_off()
    {
        using var r = new Region();
        var o = new Watched(r.Physical("car", new Vector3(250f, 128f, Ground + 0.25f), new Vector3(1f, 2f, 0.5f)));
        PhysicsActor car = o.Actor;
        car.VehicleType = (int)Vehicle.TYPE_CAR;
        car.VehicleVectorParam((int)Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
        car.VehicleFloatParam((int)Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
        car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, new Vector3(120f, 120f, 120f));
        r.Steps(1.0);
        int live = r.Live;
        car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        UntilWaitingPastTheEdge(r, o, 8);
        AssertParked(Wait(r, o, 0.6), live, "vehicle");   // under a second: longer, an idle vehicle resets its motors itself
        PutBack(r, o);
        r.Steps(1.0);
        List<Beat> after = Wait(r, o, 2.0);
        Say($"VEHICLE back at {after[0].Position}, end {after[^1].Position}");
        Assert.All(after, b => Assert.True(Inside(b.Position) && b.Position.X > Size - 2f, $"drove away after it came back: {b.Position}"));
        Assert.True(Vector3.Distance(after[0].Position, after[^1].Position) < 0.05f, $"moved after it came back: {after[0].Position} -> {after[^1].Position}");
    }

    // ---- 6. move to target -----------------------------------------------------------------------------------------

    // llMoveToTarget set while it waits does not act on the parked body; put back, it goes to the target, as ubODE keeps
    // the target (m_usePID) across the failed crossing.
    [Fact]
    public void A_move_to_target_set_while_it_waits_does_not_act_until_it_is_back_then_it_does()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f));
        int live = r.Live;
        UntilWaitingPastTheEdge(r, o);
        var target = new Vector3(250f, 128f, Ground + 1f);
        List<Beat> beats = Wait(r, o, 2.0, i =>
        {
            if (i != 1) return;
            o.Actor.PIDTarget = target;
            o.Actor.PIDTau = 0.5f;
            o.Actor.PIDActive = true;
        });
        AssertParked(beats, live, "move to target");
        PutBack(r, o);
        r.Steps(6.0);
        Say($"MOVE-TO back, after 6 s at {o.Actor.Position}");
        Assert.True(Vector3.Distance(o.Actor.Position, target) < 0.5f, $"did not go to its target once back: {o.Actor.Position}");
    }

    // ---- 7. rotation target (llLookAt, llRotLookAt) -------------------------------------------------------------

    [Fact]
    public void A_rotation_target_set_while_it_waits_does_not_act_until_it_is_back_then_it_does()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f));
        int live = r.Live;
        UntilWaitingPastTheEdge(r, o);
        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        Quaternion before = o.Actor.Orientation;
        List<Beat> beats = Wait(r, o, 2.0, i =>
        {
            if (i != 1) return;
            o.Actor.APIDTarget = turned;
            o.Actor.APIDStrength = 0.5f;
            o.Actor.APIDDamping = 0.5f;
            o.Actor.APIDActive = true;
        });
        AssertParked(beats, live, "rotation target");
        Assert.True(Angle(o.Actor.Orientation, before) < 0.5f, $"turned while it waited: {Angle(o.Actor.Orientation, before):0.00} degrees");
        PutBack(r, o);
        r.Steps(6.0);
        Say($"LOOK-AT back, after 6 s {Angle(o.Actor.Orientation, turned):0.00} degrees from the target");
        Assert.True(Angle(o.Actor.Orientation, turned) < 5f, $"did not turn to its target once back: {Angle(o.Actor.Orientation, turned):0.00} degrees off");
    }

    private static float Angle(Quaternion a, Quaternion b)
    {
        float d = MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
        return 2f * MathF.Acos(d) * 180f / MathF.PI;
    }

    // ---- 8. hover --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_hover_set_while_it_waits_does_not_act_until_it_is_back_then_it_does()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f));
        int live = r.Live;
        UntilWaitingPastTheEdge(r, o);
        List<Beat> beats = Wait(r, o, 2.0, i =>
        {
            if (i != 1) return;
            o.Actor.PIDHoverType = PIDHoverType.Ground;   // llSetHoverHeight(2.0, FALSE, 0.5)
            o.Actor.PIDHoverHeight = 2f;
            o.Actor.PIDHoverTau = 0.5f;
            o.Actor.PIDHoverActive = true;
        });
        AssertParked(beats, live, "hover");
        PutBack(r, o);
        r.Steps(5.0);
        Say($"HOVER back, after 5 s at {o.Actor.Position}");
        Assert.True(o.Actor.Position.Z > Ground + 1.5f, $"does not hover once back: z {o.Actor.Position.Z:0.000}");
    }

    // ---- 9. buoyancy -----------------------------------------------------------------------------------------------

    // llSetBuoyancy(1.0) set while it waits: put back 0.2 m up, it floats there instead of falling to the ground.
    [Fact]
    public void Buoyancy_set_while_it_waits_holds_it_up_once_it_is_back()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f));
        int live = r.Live;
        UntilWaitingPastTheEdge(r, o);
        List<Beat> beats = Wait(r, o, 2.0, i => { if (i == 1) o.Actor.Buoyancy = 1f; });
        AssertParked(beats, live, "buoyancy");
        PutBack(r, o);
        float backZ = o.Actor.Position.Z;
        r.Steps(2.0);
        Say($"BUOYANCY back at z {backZ:0.000}, after 2 s z {o.Actor.Position.Z:0.000}");
        Assert.True(backZ > Ground + Half + 0.15f, $"not put back 0.2 m up: z {backZ:0.000}");
        Assert.True(o.Actor.Position.Z > backZ - 0.03f, $"fell from {backZ:0.000} to {o.Actor.Position.Z:0.000} with buoyancy 1");
    }

    // ---- 10. put back, it carries on -------------------------------------------------------------------------------

    // The same body, simulated again: it falls the 0.2 m core's put-back raised it, lands on the ground, its collision
    // events come again and a ray finds it again.
    [Fact]
    public void Put_back_it_carries_on_with_the_same_body_lands_collides_and_is_found_again()
    {
        using var r = new Region();
        Watched o = Pushed(r, new Vector3(6f, 0f, 0f), collisionEvents: true);
        BodyId body = o.Prim.BodyHandle;
        int live = r.Live;
        UntilWaitingPastTheEdge(r, o);
        AssertParked(Wait(r, o, 1.0), live, "before the put-back");
        int collisionsWaiting = Volatile.Read(ref o.Collisions);
        PutBack(r, o);
        r.Steps(3.0);
        Vector3 p = o.Actor.Position;
        bool found = r.Jolt.RaycastWorld(new Vector3(p.X, p.Y, p.Z + 5f), -Vector3.UnitZ, 10f, 10).Any(c => c.ConsumerID == o.Part.LocalId);
        Say($"BACK at {p}, body {o.Prim.BodyHandle.Value} (was {body.Value}), live {r.Live} (was {live}), collisions {o.Collisions} (waiting {collisionsWaiting}), ray found {found}");
        Assert.True(o.Prim.BodyHandle.IsValid && o.Prim.BodyHandle.Value == body.Value, "it came back with another body");
        Assert.Equal(live, r.Live);
        Assert.InRange(p.X, Size - 0.6f, Size - 0.4f);
        Assert.InRange(p.Z, Ground + Half - 0.05f, Ground + Half + 0.05f);
        Assert.True(o.Actor.Velocity.Length() < 0.1f, $"not at rest: {o.Actor.Velocity}");
        Assert.True(Volatile.Read(ref o.Collisions) > collisionsWaiting, "no collision event once it was back on the ground");
        Assert.True(found, "a ray does not find it once it is back");
    }
}
