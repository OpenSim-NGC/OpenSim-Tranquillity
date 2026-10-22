/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical object that leaves the region: off an edge with no neighbour, off an edge into a neighbour, far below the
/// ground and very high up, at the 11 Hz heartbeat with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat.
///
/// <para>What core does, which <see cref="CoreObject"/> stands in for. The root prim's terse update request
/// (SceneObjectPart.PhysicsRequestingTerseUpdate) reads the actor's position; one outside the region's x and y sets the
/// group's position, which starts a crossing on another thread (SceneObjectGroup.AbsolutePosition, CrossAsync). That calls
/// the actor's CrossingStart, then looks for a neighbour. With one, the object is handed over (CrossPrimGroupIntoNewRegion,
/// which serialises the group with the actor's velocity) and deleted here, which removes the actor. With none, the group is
/// put back half a metre inside the edge, stopped, and the actor's CrossingFailure is called (CrossAsyncCompleted). An
/// actor's OutOfBounds event makes the object non-physical (SceneObjectPart.PhysicsOutOfBounds: DoPhysicsPropertyUpdate
/// with isNew set, so only IsPhysical is cleared). A seated avatar has no actor (ScenePresence removes it on sitting).</para>
///
/// <para>What the module does, ubODE's (ODEPrim.UpdatePositionAndVelocity, CrossingStart, CrossingFailure): past the edge
/// the object waits just outside (0.1 to 2 m), stopped, with the velocity it left at reported to core and handed to the
/// neighbour; a failed crossing brings it back 0.2 m higher, at rest, its vehicle motors off. Below -100 m or above
/// 100 000 m it is stopped there and OutOfBounds is raised. While it waits, and after, its body is not simulated.</para>
///
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class LeavingTheRegionTests
{
    private readonly ITestOutputHelper _out;
    public LeavingTheRegionTests(ITestOutputHelper output) { _out = output; }

    private const float Heartbeat = 1f / 11f;
    private const int RegionSize = 256;
    private const float Ground = 25f;
    private static readonly Vector3 Unit = new(1f, 1f, 1f);

    // ---- the region and core's part --------------------------------------------------------------------------------

    /// <summary>A region on the flat course (ground 25 m, water 20 m), stepped heartbeat by heartbeat.</summary>
    private sealed class Region : IDisposable
    {
        internal readonly JoltScene Scene;
        internal readonly List<CoreObject> Objects = new();
        internal int Heartbeats;
        internal double Now => Heartbeats * (double)Heartbeat;

        internal Region(double physicsHz)
        {
            var config = new IniConfigSource();
            IConfig startup = config.AddConfig("Startup");
            startup.Set("physics", "Jolt");
            startup.Set("meshing", "Meshmerizer");
            IConfig jolt = config.AddConfig("Jolt");
            jolt.Set("PhysicsStepRate", physicsHz.ToString(System.Globalization.CultureInfo.InvariantCulture));
            OpenSim.Region.PhysicsModules.Jolt.Harness.MaterialPins.Apply(jolt);
            Scene = new JoltScene();
            Scene.Initialise(config);
            Scene.VehicleClock = () => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks((long)Math.Round(Now * TimeSpan.TicksPerSecond));
            Scene.InitialiseWithoutScene("Leaving", RegionSize, RegionSize, Course.Heightmap(0f), Course.Water, Heartbeat);
        }

        internal PhysicsActor AddBox(Vector3 size, Vector3 position, uint localId, PhysicsActor linkTo = null)
        {
            PhysicsActor pa = Scene.AddPrimShape("leaving box", PrimitiveBaseShape.CreateBox(), position, size, Quaternion.Identity, true, localId);
            pa.Density = 1000f;
            if (linkTo != null)
                pa.link(linkTo);
            return pa;
        }

        internal void Step()
        {
            foreach (CoreObject o in Objects.ToArray())
                o.BeforeHeartbeat();
            Scene.Simulate(Heartbeat);
            Heartbeats++;
        }

        internal void StepFor(double seconds)
        {
            int n = (int)Math.Round(seconds / Heartbeat);
            for (int i = 0; i < n; i++)
                Step();
        }

        internal int ActiveBodies => Scene.CapacityStats().ActiveBodyCount;
        internal int LiveBodies => Scene.CapacityStats().LiveBodyCount;

        public void Dispose() => Scene.Dispose();
    }

    /// <summary>One object as core handles it: its root's terse updates, crossing, failed crossing, hand-over and
    /// out-of-bounds, as described on the class.</summary>
    private sealed class CoreObject
    {
        private readonly Region _r;
        internal readonly PhysicsActor Root;
        private readonly List<(PhysicsActor Part, Vector3 Offset)> _children = new();
        private readonly bool _neighbour;
        private readonly double _transit;

        internal int TerseUpdates, CollisionEvents, OutOfBoundsEvents;
        internal Vector3 OutOfBoundsAt;
        internal double LeftAt = double.NaN, BackAt = double.NaN, HandedAt = double.NaN, OutOfBoundsT = double.NaN;
        internal Vector3 LeftPosition, LeftVelocity, BackPosition, HandedPosition, HandedVelocity;
        internal int Crossings;
        internal bool Removed;
        private bool _inTransit, _startPending;
        private double _crossStartedAt;

        internal CoreObject(Region r, PhysicsActor root, bool neighbour, double transitSeconds)
        {
            _r = r;
            Root = root;
            _neighbour = neighbour;
            _transit = transitSeconds;
            root.OnRequestTerseUpdate += OnTerse;
            root.OnOutOfBounds += OnOutOfBounds;
            root.OnCollisionUpdate += _ => CollisionEvents++;
            root.SubscribeEvents(100);
            r.Objects.Add(this);
        }

        internal void AddChild(PhysicsActor child)
            => _children.Add((child, (child.Position - Root.Position) * Quaternion.Inverse(Root.Orientation)));

        internal bool InTransit => _inTransit;

        private static bool InRegion(Vector3 p) => p.X >= 0f && p.X < RegionSize && p.Y >= 0f && p.Y < RegionSize;

        private void OnTerse()
        {
            TerseUpdates++;
            Vector3 p = Root.Position;
            if (_inTransit || Removed || InRegion(p))
                return;
            _inTransit = true;
            _startPending = true;
            Crossings++;
            if (double.IsNaN(LeftAt))
            {
                LeftAt = _r.Now;
                LeftPosition = p;
                LeftVelocity = Root.Velocity;
            }
        }

        private void OnOutOfBounds(Vector3 pos)
        {
            OutOfBoundsEvents++;
            OutOfBoundsAt = pos;
            OutOfBoundsT = _r.Now;
            Root.IsPhysical = false;
        }

        internal void BeforeHeartbeat()
        {
            if (_startPending)
            {
                _startPending = false;
                _crossStartedAt = _r.Now;
                Root.CrossingStart();
                return;
            }
            if (!_inTransit || _r.Now < _crossStartedAt + _transit - 1e-9)
                return;
            _inTransit = false;
            if (_neighbour)
            {
                HandedAt = _r.Now;
                HandedPosition = Root.Position;
                HandedVelocity = Root.Velocity;
                foreach ((PhysicsActor part, _) in _children)
                    _r.Scene.RemovePrim(part);
                _r.Scene.RemovePrim(Root);
                Removed = true;
                return;
            }
            Vector3 back = Root.Position;
            back.X = Math.Clamp(back.X, 0.5f, RegionSize - 0.5f);
            back.Y = Math.Clamp(back.Y, 0.5f, RegionSize - 0.5f);
            Root.Position = back;
            foreach ((PhysicsActor part, Vector3 offset) in _children)
            {
                part.Position = back + offset * Root.Orientation;
                part.Orientation = Root.Orientation;
            }
            Root.Velocity = Vector3.Zero;            // SceneObjectPart.Stop
            Root.RotationalVelocity = Vector3.Zero;
            Root.CrossingFailure();
            if (double.IsNaN(BackAt))
            {
                BackAt = _r.Now;
                BackPosition = back;
            }
        }
    }

    /// <summary>What the object and the region did on each heartbeat after it.</summary>
    private readonly record struct Point(double T, Vector3 Position, Vector3 Velocity, int Terse, int Collisions, int Active, int Live, bool InTransit);

    private static Point Sample(Region r, CoreObject o)
        => new(r.Now, o.Root.Position, o.Root.Velocity, o.TerseUpdates, o.CollisionEvents, r.ActiveBodies, r.LiveBodies, o.InTransit);

    private void Print(string label, List<Point> trace)
    {
        _out.WriteLine(label);
        foreach (Point p in trace)
            _out.WriteLine($"  t={p.T:0.000} pos=({p.Position.X:0.000},{p.Position.Y:0.000},{p.Position.Z:0.000}) vel=({p.Velocity.X:0.000},{p.Velocity.Y:0.000},{p.Velocity.Z:0.000}) terse={p.Terse} coll={p.Collisions} active={p.Active} live={p.Live} transit={p.InTransit}");
    }

    private static List<Point> Run(Region r, CoreObject o, double seconds, Action<double> act = null)
    {
        var trace = new List<Point>();
        int n = (int)Math.Round(seconds / Heartbeat);
        for (int i = 0; i < n; i++)
        {
            act?.Invoke(r.Now);
            r.Step();
            trace.Add(Sample(r, o));
        }
        return trace;
    }

    // The heartbeats from core's CrossingStart to the end of the transit: the object waits outside.
    private static List<Point> Waiting(List<Point> trace, CoreObject o)
        => trace.Where(p => p.T > o.LeftAt + Heartbeat * 1.5 && p.InTransit).ToList();

    // While it waits just outside the edge: where ubODE leaves it, at the height and with the velocity it left with, not
    // stepped, not updated, touching nothing; the region has one body fewer.
    private static void AssertWaitsOutside(List<Point> trace, CoreObject o, int liveBefore, int bodiesPerObject = 1)
    {
        Assert.False(double.IsNaN(o.LeftAt), "core was never told the object left the region");
        List<Point> waiting = Waiting(trace, o);
        Assert.True(waiting.Count >= 3, $"too few samples while it waited ({waiting.Count})");
        Point first = waiting[0];
        foreach (Point p in waiting)
        {
            Assert.True(p.Position.X > RegionSize + 0.09f && p.Position.X < RegionSize + 2.01f,
                        $"t={p.T:0.000}: not waiting 0.1-2 m outside the edge: {p.Position}");
            Assert.True(MathF.Abs(p.Position.Z - o.LeftPosition.Z) < 0.01f,
                        $"t={p.T:0.000}: moved down or up while waiting: z {p.Position.Z:0.000}, left at {o.LeftPosition.Z:0.000}");
            Assert.True(Vector3.Distance(p.Velocity, o.LeftVelocity) < 0.01f,
                        $"t={p.T:0.000}: reported velocity {p.Velocity} is not the one it left with {o.LeftVelocity}");
            Assert.Equal(first.Terse, p.Terse);
            Assert.Equal(first.Collisions, p.Collisions);
            Assert.Equal(0, p.Active);
            Assert.Equal(liveBefore - bodiesPerObject, p.Live);
        }
    }

    // After the failed crossing: back half a metre inside, 0.2 m higher, at rest, simulated again, and it settles on the
    // ground inside the region.
    private static void AssertBackAndLands(List<Point> trace, CoreObject o, float restingZ)
    {
        Assert.False(double.IsNaN(o.BackAt), "the crossing never failed");
        Point back = trace.First(p => p.T > o.BackAt + 1e-9);
        Assert.InRange(back.Position.X, RegionSize - 0.6f, RegionSize - 0.4f);
        Point last = trace[^1];
        Assert.True(last.Position.X < RegionSize && last.Position.X > RegionSize - 2f, $"not back inside the region at the end: {last.Position}");
        Assert.InRange(last.Position.Z, restingZ - 0.05f, restingZ + 0.05f);
        Assert.True(last.Velocity.Length() < 0.1f, $"not at rest at the end: {last.Velocity}");
        Assert.Equal(1, o.Crossings);
    }

    // ---- off an edge with no neighbour -------------------------------------------------------------------------------

    // A box sliding east off the edge of a region with no neighbour: it waits just outside, not falling, while core looks
    // for a neighbour (1 s here), then comes back half a metre inside and lands on the ground there.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_pushed_off_an_edge_with_no_neighbour_waits_outside_then_comes_back_and_lands(double physicsHz)
    {
        using var r = new Region(physicsHz);
        PhysicsActor box = r.AddBox(Unit, new Vector3(252f, 128f, Ground + 0.52f), 1000);
        var o = new CoreObject(r, box, neighbour: false, transitSeconds: 1.0);
        r.StepFor(1.0);
        int live = r.LiveBodies;
        box.Velocity = new Vector3(8f, 0f, 0f);
        List<Point> trace = Run(r, o, 6.0);
        Print($"pushed off, no neighbour, {physicsHz} Hz", trace);

        AssertWaitsOutside(trace, o, live);
        Assert.True(o.LeftVelocity.X > 1f, $"left the region at {o.LeftVelocity}");
        AssertBackAndLands(trace, o, Ground + 0.5f);
        Assert.InRange(o.BackPosition.Z, o.LeftPosition.Z - 0.01f, o.LeftPosition.Z + 0.01f);
        Assert.Equal(live, trace[^1].Live);
    }

    // A box dropped from 15 m over the edge, moving east: it crosses in the air, waits outside at the height it crossed,
    // comes back 0.2 m above that, half a metre inside, at rest, and falls to the ground inside the region.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_dropped_over_an_edge_with_no_neighbour_waits_at_the_height_it_crossed_then_falls_inside(double physicsHz)
    {
        using var r = new Region(physicsHz);
        PhysicsActor box = r.AddBox(Unit, new Vector3(255.0f, 128f, Ground + 15f), 1000);
        var o = new CoreObject(r, box, neighbour: false, transitSeconds: 1.0);
        int live = r.LiveBodies;
        List<Point> trace = Run(r, o, 6.0, now => { if (r.Heartbeats == 1) box.Velocity = new Vector3(3f, 0f, 0f); });
        Print($"dropped over the edge, no neighbour, {physicsHz} Hz", trace);

        Assert.True(o.LeftPosition.Z > Ground + 10f, $"crossed at z {o.LeftPosition.Z:0.000}, not in the air");
        AssertWaitsOutside(trace, o, live);
        // Back 0.2 m above where it crossed (ODEPrim.CrossingFailure), at rest: the first heartbeat after it starts from 0.
        Point back = trace.First(p => p.T > o.BackAt + 1e-9);
        Assert.InRange(back.Position.Z, o.LeftPosition.Z + 0.2f - 0.15f, o.LeftPosition.Z + 0.2f + 0.01f);
        Assert.True(MathF.Abs(back.Velocity.X) < 0.01f && MathF.Abs(back.Velocity.Y) < 0.01f, $"came back moving sideways: {back.Velocity}");
        AssertBackAndLands(trace, o, Ground + 0.5f);
    }

    // ---- off an edge into a neighbour ------------------------------------------------------------------------------

    // A box sliding off the edge into a neighbour: while core hands it over (1 s here) it waits outside, and the neighbour
    // gets the velocity it left with, not one gravity has added to while it waited. Removed, it leaves no body.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_crossing_into_a_neighbour_is_handed_over_with_the_velocity_it_left_with(double physicsHz)
    {
        using var r = new Region(physicsHz);
        PhysicsActor box = r.AddBox(Unit, new Vector3(252f, 128f, Ground + 0.52f), 1000);
        var o = new CoreObject(r, box, neighbour: true, transitSeconds: 1.0);
        r.StepFor(1.0);
        int live = r.LiveBodies;
        box.Velocity = new Vector3(8f, 0f, 0f);
        List<Point> trace = Run(r, o, 3.0);
        Print($"into a neighbour, {physicsHz} Hz", trace);

        AssertWaitsOutside(trace, o, live);
        Assert.True(o.Removed, "never handed over");
        Assert.True(Vector3.Distance(o.HandedVelocity, o.LeftVelocity) < 0.01f,
                    $"handed over at {o.HandedVelocity}, left at {o.LeftVelocity}");
        Assert.True(MathF.Abs(o.HandedPosition.Z - o.LeftPosition.Z) < 0.01f,
                    $"handed over at z {o.HandedPosition.Z:0.000}, left at {o.LeftPosition.Z:0.000}");
        Assert.Equal(live - 1, trace[^1].Live);
        Assert.Equal(0, trace[^1].Active);
    }

    // ---- far below the ground and very high up ---------------------------------------------------------------------

    // A box falling under the terrain reaches -100 m: it is stopped there and OutOfBounds is raised, once; core makes it
    // non-physical and it stays there, at rest, costing nothing.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_falling_below_minus_100_m_is_stopped_there_and_reported_once(double physicsHz)
    {
        using var r = new Region(physicsHz);
        // Made on the ground and then moved under it: a body made below the terrain is lifted onto it.
        PhysicsActor box = r.AddBox(Unit, new Vector3(128f, 128f, Ground + 0.52f), 1000);
        var o = new CoreObject(r, box, neighbour: false, transitSeconds: 1.0);
        r.StepFor(0.5);
        box.Position = new Vector3(128f, 128f, -60f);
        box.Velocity = new Vector3(0f, 0f, -20f);
        List<Point> trace = Run(r, o, 6.0);
        Print($"below -100 m, {physicsHz} Hz", trace);

        Assert.Equal(1, o.OutOfBoundsEvents);
        Assert.InRange(o.OutOfBoundsAt.Z, -100.01f, -99.99f);
        Assert.False(box.IsPhysical);
        List<Point> after = trace.Where(p => p.T > o.OutOfBoundsT + 1e-9).ToList();
        Assert.True(after.Count > 30, $"too few samples after ({after.Count})");
        foreach (Point p in after)
        {
            Assert.InRange(p.Position.Z, -100.01f, -99.99f);
            Assert.True(p.Velocity.Length() < 1e-4f, $"t={p.T:0.000}: still moving {p.Velocity}");
            Assert.Equal(0, p.Active);
            Assert.Equal(after[0].Terse, p.Terse);
        }
    }

    // A box rising past 100 000 m: stopped there, OutOfBounds raised once, made non-physical by core.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_rising_past_100000_m_is_stopped_there_and_reported_once(double physicsHz)
    {
        using var r = new Region(physicsHz);
        PhysicsActor box = r.AddBox(Unit, new Vector3(128f, 128f, 99_950f), 1000);
        var o = new CoreObject(r, box, neighbour: false, transitSeconds: 1.0);
        List<Point> trace = Run(r, o, 4.0, now => { if (r.Heartbeats == 1) box.Velocity = new Vector3(0f, 0f, 200f); });
        Print($"above 100000 m, {physicsHz} Hz", trace);

        Assert.Equal(1, o.OutOfBoundsEvents);
        Assert.InRange(o.OutOfBoundsAt.Z, 99_999f, 100_000.01f);
        Assert.False(box.IsPhysical);
        foreach (Point p in trace.Where(p => p.T > o.OutOfBoundsT + 1e-9))
        {
            Assert.InRange(p.Position.Z, 99_999f, 100_000.01f);
            Assert.True(p.Velocity.Length() < 1e-4f, $"t={p.T:0.000}: still moving {p.Velocity}");
            Assert.Equal(0, p.Active);
        }
    }

    // ---- a linkset, a vehicle with a seated avatar, a sleeping object pushed out ----------------------------------

    // A three-prim linkset pushed off the edge with no neighbour behaves as the single box: it waits outside as one, its
    // parts where they were on it, and comes back and lands inside.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_linkset_pushed_off_an_edge_with_no_neighbour_waits_outside_then_comes_back_and_lands(double physicsHz)
    {
        using var r = new Region(physicsHz);
        PhysicsActor root = r.AddBox(Unit, new Vector3(251f, 128f, Ground + 0.52f), 1000);
        PhysicsActor a = r.AddBox(Unit, new Vector3(251f, 129f, Ground + 0.52f), 1001, root);
        PhysicsActor b = r.AddBox(Unit, new Vector3(251f, 127f, Ground + 0.52f), 1002, root);
        var o = new CoreObject(r, root, neighbour: false, transitSeconds: 1.0);
        o.AddChild(a);
        o.AddChild(b);
        r.StepFor(1.0);
        int live = r.LiveBodies;
        root.Velocity = new Vector3(9f, 0f, 0f);
        var childGaps = new List<(double T, float A, float B)>();
        List<Point> trace = Run(r, o, 6.0, now => childGaps.Add((now, Vector3.Distance(a.Position, root.Position), Vector3.Distance(b.Position, root.Position))));
        Print($"linkset, no neighbour, {physicsHz} Hz", trace);

        AssertWaitsOutside(trace, o, live);
        AssertBackAndLands(trace, o, Ground + 0.5f);
        foreach ((double t, float da, float db) in childGaps)
        {
            Assert.InRange(da, 0.99f, 1.01f);
            Assert.InRange(db, 0.99f, 1.01f);
        }
    }

    // A car driven off the edge with its motor on and a seated avatar (whose actor core has removed): it waits outside
    // as the box does, and comes back with its motors off, as ubODE's failed crossing (ODEDynamics.Stop), so it stays at
    // rest inside the edge instead of driving out again.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_vehicle_with_a_seated_avatar_driven_off_an_edge_comes_back_with_its_motors_off(double physicsHz)
    {
        using var r = new Region(physicsHz);
        // 1 m long, so put back half a metre inside it rests wholly on the region's terrain.
        PhysicsActor car = r.AddBox(new Vector3(1f, 2f, 0.5f), new Vector3(246f, 128f, Ground + 0.27f), 1000);
        car.VehicleType = (int)Vehicle.TYPE_CAR;
        car.VehicleVectorParam((int)Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
        car.VehicleFloatParam((int)Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
        car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, new Vector3(120f, 120f, 120f));
        // The driver sits: ScenePresence takes the avatar out of physics, so the region has no character for it.
        PhysicsActor driver = r.Scene.AddAvatar(2000, "Test User", new Vector3(246f, 128f, Ground + 1.5f), new Vector3(0.45f, 0.6f, 1.9f), 0f, false);
        r.Scene.RemoveAvatar(driver);
        // Under a second: a vehicle not stepped for longer lets its motors go by itself (VehicleController.CheckResetMotors).
        var o = new CoreObject(r, car, neighbour: false, transitSeconds: 0.5);
        r.StepFor(1.0);
        int live = r.LiveBodies;
        car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        List<Point> trace = Run(r, o, 8.0);
        Print($"vehicle, no neighbour, {physicsHz} Hz", trace);

        AssertWaitsOutside(trace, o, live);
        AssertBackAndLands(trace, o, Ground + 0.25f);
        Assert.True(trace.Where(p => p.T > o.BackAt + 1.0).All(p => p.Velocity.Length() < 0.1f),
                    "the car moved again after it came back");
    }

    // A box asleep near the edge, pushed off by a second box sliding into it: it leaves as the box pushed off does, and
    // the second box does not collide with it while it waits. The second box stops at the edge, where core puts the first
    // one back, so the first comes to rest on the ground there or on top of the second.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_sleeping_box_pushed_off_an_edge_by_another_waits_outside_then_comes_back_and_lands(double physicsHz)
    {
        using var r = new Region(physicsHz);
        PhysicsActor sleeper = r.AddBox(Unit, new Vector3(254.8f, 128f, Ground + 0.52f), 1000);
        PhysicsActor pusher = r.AddBox(Unit, new Vector3(249f, 128f, Ground + 0.52f), 1001);
        var o = new CoreObject(r, sleeper, neighbour: false, transitSeconds: 1.0);
        _ = new CoreObject(r, pusher, neighbour: false, transitSeconds: 1.0);
        r.StepFor(1.0);
        for (int i = 0; i < 110 && r.ActiveBodies > 0; i++)   // then until both are asleep, at most 10 s more
            r.Step();
        Assert.Equal(0, r.ActiveBodies);
        int live = r.LiveBodies;
        pusher.Velocity = new Vector3(12f, 0f, 0f);
        List<Point> trace = Run(r, o, 7.0);
        Print($"sleeping box pushed off, no neighbour, {physicsHz} Hz", trace);

        Assert.False(double.IsNaN(o.LeftAt), "the sleeping box was never pushed out");
        List<Point> waiting = Waiting(trace, o);
        Assert.True(waiting.Count >= 3, $"too few samples while it waited ({waiting.Count})");
        foreach (Point p in waiting)
        {
            Assert.True(p.Position.X > RegionSize + 0.09f && p.Position.X < RegionSize + 2.01f, $"t={p.T:0.000}: not waiting outside: {p.Position}");
            Assert.True(MathF.Abs(p.Position.Z - o.LeftPosition.Z) < 0.01f, $"t={p.T:0.000}: moved while waiting: {p.Position}");
            Assert.Equal(waiting[0].Terse, p.Terse);
            Assert.Equal(waiting[0].Collisions, p.Collisions);
            Assert.True(p.Live <= live - 1, $"t={p.T:0.000}: its body is still in the region ({p.Live} of {live})");
        }
        Assert.False(double.IsNaN(o.BackAt), "the crossing never failed");
        Point back = trace.First(p => p.T > o.BackAt + 1e-9);
        Assert.InRange(back.Position.X, RegionSize - 0.6f, RegionSize - 0.4f);
        Point last = trace[^1];
        Assert.True(last.Position.X < RegionSize && last.Position.X > RegionSize - 2f, $"not back inside the region at the end: {last.Position}");
        bool onGround = MathF.Abs(last.Position.Z - (Ground + 0.5f)) < 0.05f;
        bool onPusher = MathF.Abs(last.Position.Z - (pusher.Position.Z + 1f)) < 0.05f;
        Assert.True(onGround || onPusher, $"not resting on the ground or the other box at the end: {last.Position} (other box at {pusher.Position})");
        Assert.True(last.Velocity.Length() < 0.1f, $"not at rest at the end: {last.Velocity}");
        Assert.Equal(1, o.Crossings);
    }
}
