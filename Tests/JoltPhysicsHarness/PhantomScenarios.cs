/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Phantom and volume-detect scenarios: what passes through what, and which collision events the parts raise.
//
// Second Life documents (wiki.secondlife.com):
// - llVolumeDetect: "physical object and avatars can pass through the object"; "VolumeDetect objects trigger
//   collision_start and collision_end events when interpenetrating"; "If the volume detecting object is not
//   physical, it can only detect physical objects and avatars."
// - STATUS_PHANTOM: "objects and avatars can pass through it". A physical phantom object collides "with the ground
//   but will not pass through", and its "land collision events are queued" (llVolumeDetect's comparison table).
//
// A part's flags change the way SceneObjectPart.UpdatePrimFlags changes them: volume detect implies phantom; a
// non-physical phantom that is not a volume detector is taken out of physics (RemovePrim) and put back
// (AddPrimShape) when it stops being phantom; any other change sets Phantom and SetVolumeDetect on the actor the part already has.
// A CollisionWatch turns each collision update into the events SceneObjectPart.PhysicsCollision derives from it:
// a collider id that appears is a start, one that goes away is an end, and id 0 is the land.

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>The collision events one actor raised, derived from its collision updates as the scene derives them.</summary>
public sealed class CollisionWatch
{
    public readonly string Name;
    public readonly Dictionary<uint, int> Starts = new();
    public readonly Dictionary<uint, int> Ends = new();
    public int LandStarts;
    public int LandEnds;
    /// <summary>Every collider id this actor ever reported touching (0 = land).</summary>
    public readonly HashSet<uint> Touched = new();
    /// <summary>The updates that named an object (each is a collision event in the scene), and those that named the land
    /// (each a land_collision event).</summary>
    public int Collisions;
    public int LandCollisions;
    /// <summary>For each collider (0 = land), the lowest ContactPoint.RelativeSpeed it was reported with: how fast the two
    /// closed (below zero) at the hardest strike. Collision sounds and an avatar's impact damage read it.</summary>
    public readonly Dictionary<uint, float> Strikes = new();
    /// <summary>Each start and end, and each update, with the time of the heartbeat that sent it (when attached with a
    /// run): "start" / "end" with the collider's id, "land start" / "land end" with 0, "update" with the number of ids.</summary>
    public readonly List<(double T, string Event, uint Id)> Log = new();
    private Run _run;

    private readonly HashSet<uint> _last = new();
    private bool _lastLand;

    public CollisionWatch(string name) => Name = name;

    public int StartsOf(uint id) => Starts.TryGetValue(id, out int n) ? n : 0;
    public int EndsOf(uint id) => Ends.TryGetValue(id, out int n) ? n : 0;
    public int ObjectStarts => Starts.Values.Sum();
    public int ObjectEnds => Ends.Values.Sum();

    /// <summary>Subscribes the actor to collision updates, as SceneObjectPart.UpdatePhysicsSubscribedEvents does for a
    /// part whose script has a collision handler (and ScenePresence for every avatar).</summary>
    public void Attach(PhysicsActor pa)
    {
        pa.OnCollisionUpdate += OnUpdate;
        pa.SubscribeEvents(50);
    }

    /// <summary>As <see cref="Attach(PhysicsActor)"/>, and times each event by the run's heartbeat.</summary>
    public void Attach(PhysicsActor pa, Run run)
    {
        _run = run;
        Attach(pa);
    }

    private double Now => _run != null ? _run.Now + _run.Dt : double.NaN;

    /// <summary>The times of the logged events of one kind (and collider, when given).</summary>
    public List<double> TimesOf(string ev, uint? id = null)
        => Log.Where(l => l.Event == ev && (id == null || l.Id == id)).Select(l => l.T).ToList();

    private void OnUpdate(EventArgs e)
    {
        var update = (CollisionEventUpdate)e;
        foreach (KeyValuePair<uint, ContactPoint> kv in update.m_objCollisionList)
            if (!Strikes.TryGetValue(kv.Key, out float least) || kv.Value.RelativeSpeed < least)
                Strikes[kv.Key] = kv.Value.RelativeSpeed;
        var now = new HashSet<uint>(update.m_objCollisionList.Keys);
        double t = Now;
        Log.Add((t, "update", (uint)now.Count));
        bool land = now.Remove(0);
        if (now.Count > 0) Collisions++;
        if (land) LandCollisions++;
        foreach (uint id in now)
        {
            Touched.Add(id);
            if (!_last.Contains(id))
            {
                Starts[id] = StartsOf(id) + 1;
                Log.Add((t, "start", id));
            }
        }
        foreach (uint id in _last)
            if (!now.Contains(id))
            {
                Ends[id] = EndsOf(id) + 1;
                Log.Add((t, "end", id));
            }
        if (land)
        {
            Touched.Add(0);
            if (!_lastLand) { LandStarts++; Log.Add((t, "land start", 0)); }
        }
        else if (_lastLand)
        {
            LandEnds++;
            Log.Add((t, "land end", 0));
        }
        _last.Clear();
        _last.UnionWith(now);
        _lastLand = land;
    }

    public override string ToString()
        => $"{Name}: starts {Fmt(Starts)} ends {Fmt(Ends)} land {LandStarts}/{LandEnds}";

    private static string Fmt(Dictionary<uint, int> d)
        => d.Count == 0 ? "none" : string.Join(",", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}"));
}

/// <summary>A prim of a scenario object: its flags, and the actor the scene gives it (null while out of physics).</summary>
public sealed class HarnessPart
{
    public string Name;
    public uint LocalId;
    public Vector3 Size;
    public Vector3 Position;
    public bool Physical;
    public bool Phantom;
    public bool VolumeDetect;
    public PhysicsActor Actor;
    public CollisionWatch Watch;
    internal Run Run;
    /// <summary>The engine body and position just before and just after the last flag change.</summary>
    public BodyId BodyBeforeChange = BodyId.Invalid, BodyAfterChange = BodyId.Invalid;
    public Vector3 PositionBeforeChange, PositionAfterChange;
    /// <summary>Where the part was when the run ended.</summary>
    public Vector3 EndPosition;
    /// <summary>The first heartbeat time at which the engine had the part's body asleep (NaN: never), and whether it was
    /// asleep when the run ended (both set by scenarios that watch for sleep).</summary>
    public double SleptAt = double.NaN;
    public bool AsleepAtEnd;
    /// <summary>The scenario watches this part's sleep (SleptAt and AsleepAtEnd are set).</summary>
    public bool SleepWatched;
    /// <summary>Where the part was and how fast it went before each heartbeat, when a scenario keeps a trace of it.</summary>
    public List<(double T, Vector3 Position, Vector3 Velocity)> Trace;
    internal bool WasAwake;

    internal BodyId Body => Actor is JoltPrim jp ? jp.BodyHandle : BodyId.Invalid;
}

public static class PhantomScenarios
{
    private const float WalkSpeed = 4.096f;             // ScenePresence.AgentControlNormalVel at speed modifier 1
    public const float WallX = 173.5f;                  // the walk scenarios' object: 1 m thick, across the path
    public const float WalkY = 60f;
    public static readonly Vector3 WallSize = new(1f, 4f, 3f);
    public const float ChangeAt = 0.3f;                 // flags change before the avatar reaches the wall (about 0.6 s)
    public const uint FirstPartId = 1100;

    /// <summary>Adds a box prim through the 9-argument AddPrimShape, as SceneObjectPart.AddToPhysics does, unless the
    /// scene would keep it out of physics; a volume detector then gets SetVolumeDetect(1).</summary>
    public static HarnessPart AddPart(Run r, string name, Vector3 size, Vector3 position, bool physical, bool phantom, bool volumeDetect, bool watch)
    {
        var p = new HarnessPart
        {
            Name = name, LocalId = FirstPartId + (uint)r.Parts.Count, Size = size, Position = position,
            Physical = physical, Phantom = phantom || volumeDetect, VolumeDetect = volumeDetect,
        };
        if (watch)
        {
            p.Watch = new CollisionWatch(name);
            r.Watches.Add(p.Watch);
        }
        p.Run = r;
        r.Parts.Add(p);
        if (InPhysics(p))
            AddToPhysics(r, p);
        return p;
    }

    // SceneObjectPart.ApplyPhysics / UpdatePrimFlags: a non-physical phantom that is not a volume detector has no actor.
    private static bool InPhysics(HarnessPart p) => !p.Phantom || p.Physical || p.VolumeDetect;

    private static void AddToPhysics(Run r, HarnessPart p)
    {
        p.Actor = r.Scene.AddPrimShape(p.Name, PrimitiveBaseShape.CreateBox(), p.Position, p.Size, Quaternion.Identity,
                                       p.Physical, p.Phantom, (byte)PhysShapeType.prim, p.LocalId);
        r.AsSimulatorAdds(p.Actor);
        if (p.Physical)
            p.Actor.Density = 1000f;
        if (p.VolumeDetect)
            p.Actor.SetVolumeDetect(1);
        p.Watch?.Attach(p.Actor, p.Run);
    }

    /// <summary>Changes a part's phantom and volume-detect flags as SceneObjectPart.UpdatePrimFlags does.</summary>
    public static void SetFlags(Run r, HarnessPart p, bool phantom, bool volumeDetect)
    {
        bool wasVolumeDetect = p.VolumeDetect;
        p.VolumeDetect = volumeDetect;
        if (volumeDetect) phantom = true;              // "volume detector implies phantom"
        else if (wasVolumeDetect) phantom = false;
        p.Phantom = phantom;

        p.BodyBeforeChange = p.Body;
        p.PositionBeforeChange = p.Actor?.Position ?? p.Position;
        if (!InPhysics(p))
        {
            if (p.Actor != null)
            {
                r.Scene.RemovePrim(p.Actor);
                p.Actor = null;
            }
        }
        else if (p.Actor == null)
            AddToPhysics(r, p);
        else
        {
            p.Actor.Phantom = phantom;                 // DoPhysicsPropertyUpdate
            p.Actor.SetVolumeDetect(volumeDetect ? 1 : 0);
        }
        p.BodyAfterChange = p.Body;
        p.PositionAfterChange = p.Actor?.Position ?? p.Position;
    }

    /// <summary>The flags of a whole object, root first, as SceneObjectGroup.UpdateFlags sets them.</summary>
    public static void SetFlags(Run r, IEnumerable<HarnessPart> parts, bool phantom, bool volumeDetect)
    {
        foreach (HarnessPart p in parts)
            SetFlags(r, p, phantom, volumeDetect);
    }

    // The walk scenarios' object: one wall, or three walls one after another along the path (a non-physical
    // linkset; the scene welds only a physical linkset, so each part keeps its own body).
    private static List<HarnessPart> AddWall(Run r, int parts, bool phantom, bool volumeDetect)
    {
        float ground = r.GroundAt(WallX, WalkY);
        var list = new List<HarnessPart>();
        for (int i = 0; i < parts; i++)
            list.Add(AddPart(r, $"wall{i + 1}", WallSize, new Vector3(WallX + i * WallSize.X, WalkY, ground + WallSize.Z * 0.5f),
                             false, phantom, volumeDetect, true));
        return list;
    }

    private static void WalkEast(Run r)
    {
        r.AddAvatar(170f, WalkY);
        var watch = new CollisionWatch("avatar");
        r.Watches.Add(watch);
        watch.Attach(r.Actor);
        r.Actor.TargetVelocity = new Vector3(WalkSpeed, 0f, 0f);
    }

    // A walk through (or into) the wall; the flags change at ChangeAt when `change` is given.
    private static Scenario Walk(string name, string description, int parts, bool phantom, bool volumeDetect,
                                 (bool phantom, bool volumeDetect)? change = null)
        => new()
        {
            Name = name,
            Description = description,
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                AddWall(r, parts, phantom, volumeDetect);
                WalkEast(r);
            },
            Input = r =>
            {
                if (change is { } c && !r.Released && r.Now >= ChangeAt - 1e-9)
                {
                    SetFlags(r, r.Parts, c.phantom, c.volumeDetect);
                    r.Released = true;
                }
            },
        };

    // A physical 0.5 m box let fall from 6 m through a 3 x 3 x 0.5 m slab 3 m up, onto the ground.
    private static Scenario Drop(string name, string description, bool slabPhantom, bool slabVolumeDetect, bool boxPhantom)
        => new()
        {
            Name = name,
            Description = description,
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                float ground = r.GroundAt(128f, 128f);
                AddPart(r, "slab", new Vector3(3f, 3f, 0.5f), new Vector3(128f, 128f, ground + 3f), false, slabPhantom, slabVolumeDetect, true);
                HarnessPart box = AddPart(r, "box", new Vector3(0.5f, 0.5f, 0.5f), new Vector3(128f, 128f, ground + 6f), true, boxPhantom, false, true);
                r.Actor = box.Actor;
                r.ActorSize = box.Size;
                r.ReleaseAt = 0;
            },
        };

    // A physical box (or a three-box physical linkset) resting on a fixed platform 3 m up turns phantom at 1 s and
    // falls to the ground; it turns solid again at 3 s, and at 3.5 s a 0.5 m box is let fall onto it from 2 m.
    public const float PhantomOnAt = 1f, PhantomOffAt = 3f, DropOnItAt = 3.5f;

    private static Scenario PhysicalToggle(string name, string description, int parts)
        => new()
        {
            Name = name,
            Description = description,
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                float ground = r.GroundAt(128f, 128f);
                float top = ground + 3f;
                AddPart(r, "platform", new Vector3(5f, 3f, 0.5f), new Vector3(128f, 128f, top - 0.25f), false, false, false, false);
                var set = new List<HarnessPart>();
                for (int i = 0; i < parts; i++)
                {
                    float x = 128f + (i == 0 ? 0f : (i % 2 == 1 ? 1.05f : -1.05f));
                    set.Add(AddPart(r, i == 0 ? "root" : $"child{i}", new Vector3(1f, 1f, 1f), new Vector3(x, 128f, top + 0.51f),
                                    true, false, false, true));
                }
                for (int i = 1; i < set.Count; i++)
                    set[i].Actor.link(set[0].Actor);   // SceneObjectGroup links each child of a physical linkset to the root
                r.Actor = set[0].Actor;
                r.ActorSize = set[0].Size;
            },
            Input = r =>
            {
                List<HarnessPart> set = r.Parts.Where(p => p.Name != "platform").ToList();
                if (r.Stage == 0 && r.Now >= PhantomOnAt - 1e-9)
                {
                    SetFlags(r, set, true, false);
                    r.Stage = 1;
                }
                else if (r.Stage == 1 && r.Now >= PhantomOffAt - 1e-9)
                {
                    SetFlags(r, set, false, false);
                    r.Stage = 2;
                }
                else if (r.Stage == 2 && r.Now >= DropOnItAt - 1e-9)
                {
                    float ground = r.GroundAt(128f, 128f);
                    // Under the platform (its bottom is 2.5 m up), 1 m above the fallen object's top.
                    r.AddOtherBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(128f, 128f, ground + 2f), Quaternion.Identity, true);
                    r.Stage = 3;
                }
            },
        };

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        Walk("vd-walk", "An avatar walks east at 4.096 m/s through a fixed 1 x 4 x 3 m volume-detect box (x 173.5).", 1, false, true),
        Walk("phantom-walk", "An avatar walks east at 4.096 m/s through a fixed 1 x 4 x 3 m phantom box (x 173.5).", 1, true, false),
        Walk("vd-on-walk", "The walk into a solid fixed box that is made volume detect at 0.3 s, before the avatar reaches it.", 1, false, false, (true, true)),
        Walk("vd-off-walk", "The walk into a fixed volume-detect box that is made solid at 0.3 s, before the avatar reaches it.", 1, true, true, (false, false)),
        Walk("phantom-on-walk", "The walk into a solid fixed box that is made phantom at 0.3 s.", 1, false, false, (true, false)),
        Walk("phantom-off-walk", "The walk into a fixed phantom box that is made solid at 0.3 s.", 1, true, false, (false, false)),
        Walk("vd-on-walk-linkset", "vd-on-walk with three 1 m boxes in a row along the path, one fixed linkset.", 3, false, false, (true, true)),
        Walk("vd-off-walk-linkset", "vd-off-walk with three 1 m boxes in a row along the path, one fixed linkset.", 3, true, true, (false, false)),
        Walk("phantom-on-walk-linkset", "phantom-on-walk with three 1 m boxes in a row along the path, one fixed linkset.", 3, false, false, (true, false)),
        Walk("phantom-off-walk-linkset", "phantom-off-walk with three 1 m boxes in a row along the path, one fixed linkset.", 3, true, false, (false, false)),
        Drop("vd-drop", "A physical 0.5 m box falls from 6 m through a fixed volume-detect slab 3 m up and lands on the ground.", false, true, false),
        Drop("phantom-drop", "A physical 0.5 m box falls from 6 m through a fixed phantom slab 3 m up and lands on the ground.", true, false, false),
        Drop("phantom-physical-drop", "A physical phantom 0.5 m box falls from 6 m through a fixed solid slab 3 m up and rests on the ground.", false, false, true),
        new()
        {
            Name = "phantom-physical-walk",
            Description = "A physical phantom 1 m box rests on the ground at x 173.5; an avatar walks east through it.",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                float ground = r.GroundAt(WallX, WalkY);
                AddPart(r, "box", new Vector3(1f, 1f, 1f), new Vector3(WallX, WalkY, ground + 0.5f), true, true, false, true);
                WalkEast(r);
            },
        },
        PhysicalToggle("phantom-physical-toggle", "A physical 1 m box on a platform 3 m up: phantom at 1 s (falls to the ground), solid at 3 s, a 0.5 m box dropped on it from 2 m at 3.5 s.", 1),
        PhysicalToggle("phantom-physical-toggle-linkset", "phantom-physical-toggle with a physical linkset of three 1 m boxes in a row.", 3),
    };
}
