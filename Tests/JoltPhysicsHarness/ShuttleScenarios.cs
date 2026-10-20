/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Shuttle scenarios: a standing avatar on a 9.9 x 9.9 x 0.3 m deck that goes 44 m along +x at 3 m/s, slows over its last 7 m
// to 0.5 m/s, stops, waits 8 s and comes back the same way: a platform ride across a region border.
//   - physical: the deck is a physical sled vehicle (VEHICLE_TYPE_SLED, linear motor timescale 3 s, hover 0.25 m) whose motor
//     direction is set every 0.25 s from its place, as a platform script drives one;
//   - keyframed: the deck is a non-physical prim that moves along the same speed profile by a position set every 50 ms with
//     PhysicsActor.MovedByKeyframedMotion on, as core plays llSetKeyframedMotion.
// With `crossing` the deck starts 24 m from the region's east edge and the run does what core does when the deck's centre
// reaches the edge, in one scene (the far side is the same flat ground): the rider is taken out of the engine, the deck is
// taken out and made again 256 m west of where it was (the destination region's coordinates), held still while the rider
// is made over it (a carried arrival: it may stand outside the region, and asks for no momentum), and then the deck's hold
// ends, it goes on by its velocity times the time it waited, the rider is moved by the same distance, and the script's
// region-changed handler sets the vehicle up again and sets its motor.
// The deck is the run's actor and the avatar its second actor, so a sample's Position is the deck and Other the avatar.
// Figures reads where the avatar is against the deck and how it moves once the deck has stopped.

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class ShuttleScenarios
{
    public const float DeckSide = 9.9f, DeckThickness = 0.3f, Distance = 44f, Cruise = 3f, Ramp = 7f, MinSpeed = 0.5f, Hover = 0.25f;
    public const double SettleSeconds = 3.0, WaitSeconds = 8.0, MotorInterval = 0.25, KeyframeInterval = 0.05, TrailSeconds = 6.0;
    public const float LinearAcceleration = 1f, RegionSize = 256f, CrossingStartX = 232f, InRegionStartX = 60f, StartY = 128f;
    public const int ArrivalBeats = 2, HoldBeats = 11;
    public const float CrossingMargin = 0.5f, CrossShift = 255.3f;   // the deck crosses when its origin is CrossingMargin from the edge and is made again CrossShift west of where it was, 0.2 m inside the next region: the harness ends a run at the first sample outside the region
    private const uint DeckId = 1000u, RiderId = 1001u, FirstPartId = 2100u;

    private sealed class State
    {
        public int Phase;               // 0 settling, 1 out, 2 waiting at the far end, 3 back, 4 parked
        public double PhaseStart, NextTick;
        public float PathX;             // keyframed: the deck's place along the path (m from its start)
        public float PathSpeed;         // keyframed: the deck's speed along the path
        public float Shift;             // metres to add to a local x to get the x along the whole path (256 once it has crossed)
        public int Crossed;             // 0 not yet, 1 deck here and rider on the way, 2 rider here and hold ended
        public int Beat, CrossBeat;
        public Vector3 RiderAt;
    }

    /// <summary>What a run found: where the avatar is against the deck, and how it moves against the deck while the deck is stopped.</summary>
    public sealed record Figures(bool OnAtEnd, double LostContactT, double MaxSlideBack, double MaxAhead, double EndDistance,
                                 double SpeedWhileStopped, double EndRelZ, double DeckStopT)
    {
        public string Verdict => OnAtEnd ? "stays on" : double.IsNaN(LostContactT) ? "off at the end" : $"off from {LostContactT:0.0} s";
    }

    public static string Name(bool physical, float riderAhead, bool linkset = false, float drop = 0f, bool crossing = false, bool rider = true)
        => $"shuttle-{(physical ? "physical" : "keyframed")}-{riderAhead:0.#}{(linkset ? "-linkset" : "")}{(drop > 0f ? $"-drop{drop:0.#}" : "")}{(crossing ? "-crossing" : "")}{(rider ? "" : "-empty")}";

    /// <summary>The deck's speed (m/s) on the way out or back, from how far it still has to go: the script's profile.</summary>
    public static float ProfileSpeed(float left)
    {
        float speed = left < Ramp ? Cruise * left / Ramp : Cruise;
        return MathF.Max(speed, MinSpeed);
    }

    // A platform script's setup(): a sled that cannot tilt or turn, with the platform's friction, motor and hover. The calls go
    // through the part's vehicle record, as a script's do (Run.MakeVehicle and the setters).
    private static void ApplyVehicle(Run r, PhysicsActor deck)
    {
        r.Actor = deck;
        if (r.VehiclePart != null)
        {
            // the record of the deck that crossed: it goes with the run's other cleanups, as the run's own record does
            SceneObjectPart old = r.VehiclePart;
            r.Cleanups.Add(() => { old.PhysActor = null; old.Dispose(); });
            r.VehiclePart = null;
        }
        r.MakeVehicle(Vehicle.TYPE_SLED);
        r.SetFlags((int)(VehicleFlag.LIMIT_MOTOR_UP | VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_ROLL_ONLY), true);
        r.SetVector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1.5f, 0.25f, 1f));
        r.SetVector(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(0.3f, 0.3f, 0.1f));
        r.SetFloat(Vehicle.LINEAR_MOTOR_TIMESCALE, 3f);
        r.SetFloat(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 60f);
        r.SetFloat(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.3f);
        r.SetFloat(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 60f);
        r.SetFloat(Vehicle.HOVER_HEIGHT, Hover);
        r.SetFloat(Vehicle.HOVER_EFFICIENCY, 1f);
        r.SetFloat(Vehicle.HOVER_TIMESCALE, 0.3f);
        r.SetFloat(Vehicle.BUOYANCY, 0f);
        r.SetFloat(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
        r.SetFloat(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 0.3f);
        deck.LockAngularMotion(0x0E);   // llSetStatus(STATUS_ROTATE_X/Y/Z, FALSE)
    }

    private static PhysicsActor MakeDeck(Run r, Vector3 centre, bool physical, bool linkset)
    {
        var size = new Vector3(DeckSide, DeckSide, DeckThickness);
        PhysicsActor deck = r.AddPart(PrimitiveBaseShape.CreateBox(), size, centre, Quaternion.Identity, physical, DeckId);
        if (physical)
            ApplyVehicle(r, deck);
        if (linkset && physical)
        {
            // a post at each corner and a thin arrow on the deck, linked to its root
            uint id = FirstPartId;
            foreach ((float px, float py) in new[] { (4.7f, 4.7f), (4.7f, -4.7f), (-4.7f, 4.7f), (-4.7f, -4.7f) })
                r.AddPart(PrimitiveBaseShape.CreateCylinder(), new Vector3(0.2f, 0.2f, 1f), centre + new Vector3(px, py, 0.65f), Quaternion.Identity, true, id++, deck);
            r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1.6f, 1.4f, 0.04f), centre + new Vector3(2.5f, 0f, 0.17f), Quaternion.Identity, true, id++, deck);
        }
        return deck;
    }

    public static Scenario Ride(bool physical, float riderAhead, bool linkset = false, float drop = 0f, bool crossing = false, bool rider = true)
    {
        State st = null;
        float startX = crossing ? CrossingStartX : InRegionStartX;
        return new Scenario
        {
            Name = Name(physical, riderAhead, linkset, drop, crossing, rider),
            Description = $"A standing avatar {riderAhead:0.#} m ahead of the middle of a {DeckSide:0.#} m {(physical ? "physical sled" : "keyframed")} deck that goes {Distance:0} m out and back"
                          + (crossing ? ", across a region border." : "."),
            DefaultDuration = _ => (float)(SettleSeconds + 2 * (Distance / Cruise + 4) + WaitSeconds + TrailSeconds + 8),
            Setup = r =>
            {
                st = new State();
                float ground = r.GroundAt(startX, StartY);
                var deckAt = new Vector3(startX, StartY, ground + (physical ? Hover : DeckThickness * 0.5f));
                PhysicsActor deck = MakeDeck(r, deckAt, physical, linkset);
                float top = deckAt.Z + DeckThickness * 0.5f;
                if (rider)
                {
                    PhysicsActor av = r.AddAvatarAt(new Vector3(deckAt.X + riderAhead, deckAt.Y, top + r.AvatarStandHalf + 0.05f + drop), false);
                    r.Other = av;
                    r.OtherSize = r.AvatarBox;
                }
                r.Actor = deck;
                r.ActorSize = new Vector3(DeckSide, DeckSide, DeckThickness);
                st.NextTick = SettleSeconds;
            },
            Input = r =>
            {
                st.Beat++;
                double until = r.Now + r.Dt;
                if (st.Phase == 0 && until > SettleSeconds + 1e-9)
                {
                    st.Phase = 1;
                    st.PhaseStart = SettleSeconds;
                    if (!physical)
                        r.Actor.MovedByKeyframedMotion = true;
                }
                if (st.Phase == 0)
                    return;
                if (crossing)
                {
                    // core lets a rider on a moving object stand outside the region until the object crosses (ScenePresence.CheckForBorderCrossing)
                    if (st.Crossed == 0 && r.Other != null)
                        r.Other.AllowOutsideRegion = true;
                    Cross(r, st, physical, linkset);
                }
                if (crossing && st.Phase == 3 && r.Now - st.PhaseStart > 4.0)
                    r.StopAt = r.Now;   // the way back crosses the border again, which this run does not play
                if (crossing && st.Crossed == 1)
                    return;   // the deck is held: nothing drives it until its hold ends
                float x = r.Actor.Position.X + st.Shift - startX;
                if (physical)
                {
                    while (st.NextTick < until - 1e-9 && st.Phase < 4)
                    {
                        st.NextTick += MotorInterval;
                        Drive(r, st, x);
                    }
                }
                else
                {
                    while (st.NextTick < until - 1e-9 && st.Phase < 4)
                    {
                        st.NextTick += KeyframeInterval;
                        Glide(r, st, startX);
                    }
                }
            },
        };
    }

    // What core does when the deck's centre reaches the region's edge, and then in the region ahead until the deck is let go.
    private static void Cross(Run r, State st, bool physical, bool linkset)
    {
        PhysicsScene scene = r.PhysicsScene;
        if (st.Crossed == 0 && r.Actor.Position.X >= RegionSize - CrossingMargin)
        {
            Vector3 deckAt = r.Actor.Position, deckVel = r.Actor.Velocity;
            st.RiderAt = r.Other.Position - new Vector3(CrossShift, 0f, 0f);
            scene.RemoveAvatar(r.Other);          // the rider is frozen and leaves the engine
            r.Actor.CrossingStart();              // the deck's body leaves the engine
            scene.RemovePrim(r.Actor);
            r.Other = null;
            st.Shift += CrossShift;
            st.Crossed = 1;
            st.CrossBeat = st.Beat;
            PhysicsActor deck = MakeDeck(r, deckAt - new Vector3(CrossShift, 0f, 0f), physical, linkset);
            r.Actor = deck;
            if (physical)
            {
                deck.Velocity = deckVel;
                deck.HoldStill();                 // held still while its riders arrive
            }
            return;
        }
        if (st.Crossed != 1)
            return;
        if (st.Beat == st.CrossBeat + ArrivalBeats)
        {
            // ScenePresence.AddToPhysicalScene for an avatar an object carried in, then MakeRootAgent's momentum for it: none
            PhysicsActor pa = scene.AddAvatar(RiderId, "Test User", st.RiderAt, r.AvatarBox, 0f, false, true);
            pa.Orientation = Quaternion.Identity;
            pa.SubscribeEvents(100);
            pa.SetAlwaysRun = false;
            pa.SetMomentum(Vector3.Zero);
            r.Other = pa;
        }
        else if (st.Beat == st.CrossBeat + ArrivalBeats + HoldBeats)
        {
            // The hold ends: the deck goes on by its velocity times the wait, and the rider by the same distance (Scene.ReleasePhysicsHold)
            float waited = (float)((ArrivalBeats + HoldBeats) * r.Dt);
            if (physical)
            {
                Vector3 before = r.Actor.Position;
                r.Actor.ReleaseHold(waited);
                Vector3 moved = r.Actor.Position - before;
                if (moved != Vector3.Zero)
                    r.Other.Position += moved;
            }
            else
                r.Actor.MovedByKeyframedMotion = true;
            st.Crossed = 2;
            st.NextTick = r.Now;   // the script's changed(CHANGED_REGION) sets the motor at once
        }
    }

    // The script's timer: the motor along the path, ramped near each end; a zero motor and a wait at the far end.
    private static void Drive(Run r, State st, float x)
    {
        double now = st.NextTick;
        Vector3 motor = Vector3.Zero;
        switch (st.Phase)
        {
            case 1:
                if (x >= Distance - 0.3f) { st.Phase = 2; st.PhaseStart = now; }
                else motor = new Vector3(ProfileSpeed(Distance - x), 0f, 0f);
                break;
            case 2:
                if (now - st.PhaseStart >= WaitSeconds) { st.Phase = 3; st.PhaseStart = now; motor = new Vector3(-ProfileSpeed(x), 0f, 0f); }
                break;
            case 3:
                if (x <= 0.4f) { st.Phase = 4; st.PhaseStart = now; }
                else motor = new Vector3(-ProfileSpeed(x), 0f, 0f);
                break;
        }
        r.SetVector(Vehicle.LINEAR_MOTOR_DIRECTION, motor);
    }

    // The deck on the same profile by position sets: it speeds up at 1 m/s^2 to the profile's speed, follows it, stops.
    private static void Glide(Run r, State st, float startX)
    {
        double now = st.NextTick, dt = KeyframeInterval;
        float target = 0f;
        switch (st.Phase)
        {
            case 1:
                if (st.PathX >= Distance - 0.05f) { st.Phase = 2; st.PhaseStart = now; st.PathSpeed = 0f; }
                else target = ProfileSpeed(Distance - st.PathX);
                break;
            case 2:
                if (now - st.PhaseStart >= WaitSeconds) { st.Phase = 3; st.PhaseStart = now; }
                break;
            case 3:
                if (st.PathX <= 0.05f) { st.Phase = 4; st.PathSpeed = 0f; }
                else target = -ProfileSpeed(st.PathX);
                break;
        }
        float step = (float)(LinearAcceleration * dt);
        st.PathSpeed += MathF.Max(-step, MathF.Min(step, target - st.PathSpeed));
        st.PathX = MathF.Max(0f, MathF.Min(Distance, st.PathX + st.PathSpeed * (float)dt));
        float ground = r.GroundAt(startX, StartY);
        r.Actor.Position = new Vector3(startX + st.PathX - st.Shift, StartY, ground + DeckThickness * 0.5f);
        if (st.Phase == 4)
            r.Actor.MovedByKeyframedMotion = false;
    }

    /// <summary>Reads a run: from the end of the settle, where the avatar is against the deck. Samples taken while the rider is
    /// out of the engine in a crossing (no second actor) are skipped. Speeds are read from positions over half a second (a
    /// non-physical prim reports no velocity): the deck is stopped when it moves under 0.1 m/s after it has moved at over
    /// 2 m/s, and the avatar's speed against the deck is the largest over the time the deck is stopped, from half a second
    /// after it stopped.</summary>
    public static Figures Measure(RunResult result)
    {
        List<Sample> all = result.Samples;
        int at = all.FindLastIndex(x => x.T <= SettleSeconds + 1e-6);
        Vector3 off0 = all[at].Other - all[at].Position;
        double lost = double.NaN, maxBack = 0, maxAhead = 0, stopT = double.NaN, whileStopped = 0;
        bool moved = false;
        var s = new List<Sample>();
        for (int i = at + 1; i < all.Count; i++)
        {
            if (all[i].Other == Vector3.Zero)
                continue;
            s.Add(all[i]);
            Sample c = all[i];
            Vector3 rel = c.Other - c.Position;
            maxBack = Math.Max(maxBack, off0.X - rel.X);
            maxAhead = Math.Max(maxAhead, rel.X - off0.X);
            bool off = Math.Abs(rel.X) > DeckSide / 2 + 0.1 || Math.Abs(rel.Y) > DeckSide / 2 + 0.1 || rel.Z < off0.Z - 0.4;
            if (off && double.IsNaN(lost))
                lost = c.T;

            int j = s.Count - 1;
            while (j > 0 && c.T - s[j].T < 0.5 - 1e-9)
                j--;
            double span = c.T - s[j].T;
            if (span < 0.4 || Math.Abs(s[j].Position.X - c.Position.X) > 20)   // too short a stretch, or a jump: the crossing
                continue;
            Vector3 dv = (c.Position - s[j].Position) / (float)span, rv = (c.Other - s[j].Other) / (float)span;
            float deckSpeed = new Vector3(dv.X, dv.Y, 0f).Length();
            if (deckSpeed > 2f)
                moved = true;
            if (moved && deckSpeed < 0.1f)
            {
                if (double.IsNaN(stopT))
                    stopT = c.T;
                if (c.T - stopT >= 0.5)
                    whileStopped = Math.Max(whileStopped, new Vector3(rv.X - dv.X, rv.Y - dv.Y, 0f).Length());
            }
        }
        Sample end = all[^1];
        Vector3 relEnd = end.Other - end.Position;
        bool on = end.Other != Vector3.Zero && Math.Abs(relEnd.X) < DeckSide / 2 && Math.Abs(relEnd.Y) < DeckSide / 2 && Math.Abs(relEnd.Z - off0.Z) < 0.4;
        double dist = Math.Sqrt(relEnd.X * relEnd.X + relEnd.Y * relEnd.Y);
        return new Figures(on, lost, maxBack, maxAhead, dist, whileStopped, relEnd.Z - off0.Z, stopT);
    }

    /// <summary>How far the deck strays from its line (the run's y, the deck being driven along x only), from the end of the settle:
    /// the largest distance from StartY, the largest sideways speed the engine reports, and the largest turn of its heading from
    /// +x in degrees.</summary>
    public static (double MaxOffLine, double MaxSideSpeed, double MaxHeadingDeg, double FirstOffLineT) Drift(RunResult result)
    {
        double off = 0, side = 0, head = 0, firstT = double.NaN;
        foreach (Sample s in result.Samples)
        {
            if (s.T <= SettleSeconds)
                continue;
            double d = Math.Abs(s.Position.Y - StartY);
            off = Math.Max(off, d);
            if (d > 0.5 && double.IsNaN(firstT))
                firstT = s.T;
            side = Math.Max(side, Math.Abs(s.Velocity.Y));
            Vector3 fwd = Vector3.UnitX * s.Rotation;
            head = Math.Max(head, Math.Abs(Math.Atan2(fwd.Y, fwd.X)) * 180.0 / Math.PI);
        }
        return (off, side, head, firstT);
    }
}
