/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A harness for TempAllocator lock discipline during inter-region crossings.
///
/// <para><b>Why it did not exist before.</b> The failure is <c>std::abort()</c> inside joltc when its LIFO
/// TempAllocator is freed out of order (<c>Jolt/Core/TempAllocator.h:83-84</c>). An abort takes the process
/// down: no managed exception, no stack, no failing assertion — only a Windows
/// event id and two console lines, so a fix could only be argued from source. This harness replaces the
/// abort with a catchable exception by asking, at every managed call site that reaches the allocator, whether
/// the calling thread holds that backend's <c>_simLock</c>
/// (<c>JoltPhysicsBackend.AllocatorOwnerCheck</c> / <c>RequireSimLock</c>).</para>
///
/// <para><b>What it reproduces.</b> The class, not the instance: an inter-region crossing. Two backends step
/// on their own threads at ~11 Hz, as three regions in one simulator process do, while a driver repeatedly
/// does what a teleport arrival does — create a character in the arriving region and set its size immediately
/// after (<c>JoltCharacter.cs:274</c>), rez a handful of attachment bodies there, and remove the character
/// from the departing region. Every call is made from a thread that is neither region's step thread, which is
/// exactly where the scene/teleport thread sits.</para>
/// </summary>
public class TeleportCrossingHarnessTests
{
    private readonly ITestOutputHelper _out;
    public TeleportCrossingHarnessTests(ITestOutputHelper output) { _out = output; }

    private const int StepHz = 11;
    private const int AttachmentsPerArrival = 14;   // about what a crossing re-adds for a typical avatar

    private static PhysicsBackendSettings Settings() => new()
    {
        Gravity = new Vector3(0f, 0f, -9.8f),
        MaxBodies = 4096,
        MaxBodyPairs = 4096,
        MaxContactConstraints = 2048,
        ThreadCount = 2,
        PositionIterations = 2,
        VelocityIterations = 10,
        CollisionSteps = 1,
    };

    private static CharacterDesc Avatar(Vector3 at, uint userData = 1u) => new()
    {
        Position = at,
        Orientation = Quaternion.Identity,
        CapsuleHalfHeight = 0.45f,
        CapsuleRadius = 0.30f,
        Mass = 80f,
        Friction = 0.5f,
        MaxSlopeAngle = 1.0f,
        StepHeight = 0.45f,
        PushStrength = 1f,
        // Without this the Persist gate suppresses the standing-on-the-floor contact that fires
        // every step, and OnContactPersisted - the callback most likely to be inside ExtendedUpdate when
        // something else happens - never runs.
        WantsContactEvents = true,
        UserData = userData,
    };

    /// <summary>Ground top is z=21 (box centre 20, half-height 1); the capsule centre rests standHalf above it.</summary>
    private const float FloorTop = 21f;
    private static Vector3 Standing(float x = 128f, float y = 128f) => new(x, y, FloorTop + 0.75f + 0.05f);

    /// <summary>One region: a backend and the heartbeat thread that steps it, as the simulator runs them.</summary>
    private sealed class Region : IDisposable
    {
        public readonly JoltPhysicsBackend Backend = new();
        public readonly ConcurrentQueue<Exception> Faults = new();
        private readonly Thread _thread;
        private volatile bool _run = true;
        public long Steps;

        /// <summary>Contacts drained from Step, counted by kind. A harness whose callbacks never
        /// fire proves nothing - these counts are how the test checks that they did.</summary>
        public long BodyContactsBegin, BodyContactsPersist, CharacterContacts;

        public Region(string name)
        {
            Backend.Initialize(Settings());

            // A floor, so the arriving character LANDS on something and keeps touching it. Without this the
            // characters fall through an empty world for ever and CharacterVirtual::OnContact* never fires -
            // and ExtendedUpdate could run millions of times without exercising a single callback.
            var ground = Backend.CreateBoxShape(new Vector3(64f, 64f, 1f));
            Backend.CreateBody(new BodyDesc
            {
                Shape = ground,
                Position = new Vector3(128f, 128f, 20f),
                Orientation = Quaternion.Identity,
                Layer = PhysicsLayer.Static,
                MotionType = BodyMotionType.Static,
                Density = 1000f,
                WantsContactEvents = true,
            });

            _thread = new Thread(Loop) { IsBackground = true, Name = $"step:{name}" };
            _thread.Start();
        }

        private void Loop()
        {
            var bodies = new BodyState[256];
            var chars = new CharacterState[16];
            var contacts = new ContactReport[256];
            // NOT a real 11 Hz heartbeat: this spins. A sleeping loop steps ~11 times a second and the
            // crossings, which take microseconds, simply never land inside an Update - 1000 crossings can run
            // against ONE step per region and "pass" having overlapped
            // nothing. Spinning keeps each backend inside _system.Update as much of the time as possible,
            // which is the only way a crossing call can contend with one. The step size stays 1/11 s so the
            // simulation itself behaves like a running region.
            while (_run)
            {
                try
                {
                    var r = Backend.Step(1f / StepHz, bodies, chars, contacts);
                    Interlocked.Increment(ref Steps);
                    for (var i = 0; i < r.ContactCount; i++)
                    {
                        ref var c = ref contacts[i];
                        // avatar-vs-avatar: neither side is a rigid body, both carry avatar UserData
                        if (!c.BodyA.IsValid && !c.BodyB.IsValid && c.UserDataA != 0 && c.UserDataB != 0)
                            Interlocked.Increment(ref CharacterContacts);
                        else if (c.Phase == ContactPhase.Begin)
                            Interlocked.Increment(ref BodyContactsBegin);
                        else if (c.Phase == ContactPhase.Persist)
                            Interlocked.Increment(ref BodyContactsPersist);
                    }
                }
                catch (Exception ex) { Faults.Enqueue(ex); }
            }
        }

        public void Dispose()
        {
            _run = false;
            _thread.Join(TimeSpan.FromSeconds(5));
            Backend.Dispose();
        }
    }

    /// <summary>
    /// The crossing, from a thread that is neither step thread. Returns whatever it threw, or null.
    /// </summary>
    private static Exception Cross(Region arriving, Region departing, CharacterId leaving, CharacterId resident, out CharacterId arrived)
    {
        arrived = default;
        try
        {
            // Put the resident back where the arrival lands. Two capsules 0.35 apart with 0.30 radii overlap,
            // so the controller shoves them apart within a step or two and avatar-vs-avatar contact stops; without
            // re-grounding a thousand crossings give only a couple of dozen such callbacks.
            // Re-grounding each crossing keeps the pair genuinely in contact.
            if (resident.Value != 0)
                arriving.Backend.ReGroundCharacter(resident, Standing(128.35f, 128f));

            // 1. the arriving region creates the character, standing ON the floor so its contacts fire...
            arrived = arriving.Backend.CreateCharacter(Avatar(Standing(), 0x515A));

            // 2. ...and the scene thread sets its size immediately after, as JoltCharacter.Size does when the
            //    avatar's appearance is applied on arrival. This is the call that must hold _simLock.
            arriving.Backend.SetCharacterShape(arrived, 0.48f, 0.31f);

            // 3. attachment rez: a handful of bodies into the arriving region's broadphase, then removed
            //    again so a thousand crossings do not exhaust MaxBodies. Both halves mutate the broadphase
            //    that Update is walking, which is the other half of a real arrival's sequence.
            var shape = arriving.Backend.CreateBoxShape(new Vector3(0.1f, 0.1f, 0.1f));
            var rezzed = new List<BodyId>(AttachmentsPerArrival);
            for (var i = 0; i < AttachmentsPerArrival; i++)
            {
                rezzed.Add(arriving.Backend.CreateBody(new BodyDesc
                {
                    Shape = shape,
                    // ON the avatar, not beside it: an attachment overlaps the wearer, and an overlapping
                    // dynamic body is what makes CharacterVirtual::OnContactAdded fire during ExtendedUpdate.
                    Position = Standing() + new Vector3((i % 4) * 0.12f - 0.18f, (i / 4) * 0.12f - 0.18f, 0.1f),
                    Orientation = Quaternion.Identity,
                    Layer = PhysicsLayer.Dynamic,
                    MotionType = BodyMotionType.Dynamic,
                    Density = 1000f,
                    GravityFactor = 1f,
                    StartActive = true,
                    WantsContactEvents = true,
                    UserData = (uint)(0xA77A0000 + i),
                }));
            }
            foreach (var b in rezzed)
                arriving.Backend.RemoveBody(b);

            // 4. and the departing region drops its copy - the avatar becomes a child agent there.
            if (leaving.Value != 0)
                departing.Backend.RemoveCharacter(leaving);

            return null;
        }
        catch (Exception ex) { return ex; }
    }

    /// <summary>
    /// The harness proper. Runs the crossing repeatedly with both regions stepping, and fails naming the API
    /// and the region if any allocator-touching call is made without that backend's <c>_simLock</c>.
    /// </summary>
#if DEBUG
    [Theory]
#else
    [Theory(Skip = "Needs JoltPhysicsBackend.AllocatorOwnerCheck, which is on by default only in Debug builds")]
#endif
    [InlineData(1000)]
    public void A_thousand_crossings_never_touch_a_TempAllocator_unlocked(int crossings)
    {
        Assert.True(JoltPhysicsBackend.AllocatorOwnerCheck,
            "the harness is meaningless without the owner check; tests build DEBUG, where it is on by default");

        using var regionA = new Region("Region A");
        using var regionB = new Region("Region B");

        // A resident avatar standing where the arrival lands, in each region. Two CharacterVirtuals inside one
        // backend's CharacterVsCharacterCollisionSimple, overlapping, is the only way OnCharacterContactAdded /
        // Persisted fire - and those are the callbacks that hand a SECOND CharacterVirtual to managed code from
        // inside ExtendedUpdate.
        var residents = new[]
        {
            regionA.Backend.CreateCharacter(Avatar(Standing(128.35f, 128f), 0x9E51)),
            regionB.Backend.CreateCharacter(Avatar(Standing(128.35f, 128f), 0x9E52)),
        };

        var faults = new List<string>();
        CharacterId inRegionA = default, inRegionB = default;

        for (var i = 0; i < crossings && faults.Count == 0; i++)
        {
            // alternate direction, as a resident hopping back and forth does
            var toRegionB = (i & 1) == 0;
            var arriving = toRegionB ? regionB : regionA;
            var departing = toRegionB ? regionA : regionB;
            var leaving = toRegionB ? inRegionA : inRegionB;
            long stepsA = Interlocked.Read(ref regionA.Steps), stepsB = Interlocked.Read(ref regionB.Steps);

            var ex = Cross(arriving, departing, leaving, toRegionB ? residents[1] : residents[0], out var arrived);
            if (ex is not null) faults.Add($"crossing {i}: {ex.GetType().Name}: {ex.Message}");

            if (toRegionB) { inRegionB = arrived; inRegionA = default; }
            else { inRegionA = arrived; inRegionB = default; }

            // Both regions step while the crossings run. On a slow machine the crossings can outrun the step threads
            // and leave fewer steps than crossings, overlapping less than the check below demands: before the next
            // crossing, wait until each region has stepped once more since this one began.
            WaitForSteps(regionA, stepsA);
            WaitForSteps(regionB, stepsB);
        }

        foreach (var r in new[] { regionA, regionB })
            while (r.Faults.TryDequeue(out var ex))
                faults.Add($"step thread: {ex.GetType().Name}: {ex.Message}");

        _out.WriteLine($"crossings={crossings} steps: Region A={regionA.Steps} Region B={regionB.Steps}");
        foreach (var (n, r) in new[] { ("Region A", regionA), ("Region B", regionB) })
            _out.WriteLine($"  {n}: body-begin={r.BodyContactsBegin} body-persist={r.BodyContactsPersist} character-character={r.CharacterContacts}");
        Assert.True(faults.Count == 0, string.Join("\n", faults.Take(5)));

        // The result is worthless unless both regions really were stepping throughout. A harness that
        // sleeps between steps can manage ONE step per region across a thousand crossings; it
        // passes, and it has overlapped nothing. Demand at least one step per crossing on each side.
        Assert.True(regionA.Steps >= crossings, $"Region A stepped {regionA.Steps} times for {crossings} crossings - not contended");
        Assert.True(regionB.Steps >= crossings, $"Region B stepped {regionB.Steps} times for {crossings} crossings - not contended");

        // And the callbacks must actually have run. ExtendedUpdate re-entering managed code is the
        // whole hypothesis; a harness where OnContact* never fires tests nothing.
        var begin = regionA.BodyContactsBegin + regionB.BodyContactsBegin;
        var persist = regionA.BodyContactsPersist + regionB.BodyContactsPersist;
        var charChar = regionA.CharacterContacts + regionB.CharacterContacts;
        Assert.True(begin > 0, "CharacterVirtual::OnContactAdded never fired - the avatars never touched anything");
        Assert.True(persist > 0, "CharacterVirtual::OnContactPersisted never fired - nothing stayed in contact");
        Assert.True(charChar > 0, "OnCharacterContactAdded/Persisted never fired - no avatar-vs-avatar contact");
    }

    // Until the region has stepped past `before`. The cap is for a step thread that has stopped, generous so that a
    // stalled machine cannot reach it; reaching it fails the test at once rather than at the step-count check below.
    private static void WaitForSteps(Region region, long before)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Interlocked.Read(ref region.Steps) <= before && sw.Elapsed < TimeSpan.FromSeconds(60))
            Thread.Yield();
        Assert.True(Interlocked.Read(ref region.Steps) > before, $"a region did not step within 60 s (waiting for more than {before} steps)");
    }

    /// <summary>
    /// The harness's own control: with the check on, a deliberate unlocked call into an allocator-touching API
    /// must be caught. Without this, a harness that reproduces nothing proves nothing.
    /// </summary>
#if DEBUG
    [Fact]
#else
    [Fact(Skip = "Needs JoltPhysicsBackend.AllocatorOwnerCheck, which is on by default only in Debug builds")]
#endif
    public void The_owner_check_catches_an_unlocked_allocator_call()
    {
        using var region = new Region("Control");
        var id = region.Backend.CreateCharacter(Avatar(new Vector3(128f, 128f, 25f)));

        var ex = Record.Exception(() => region.Backend.SetCharacterShapeUnlockedForTest(id, 0.5f, 0.3f));

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("does not hold _simLock", ex.Message);
        Assert.Contains("SetShape", ex.Message);
    }
}
