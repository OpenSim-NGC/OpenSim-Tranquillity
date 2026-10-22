/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A real <see cref="JoltPhysicsBackend"/> for a test, constructed the way <see cref="NativeSmokeTests"/> does it
/// (new + Initialize) with the region defaults, plus the few scene-building helpers the backend tests share.
/// </summary>
internal sealed class JoltTestBackend : IDisposable
{
    public readonly JoltPhysicsBackend B = new();

    public JoltTestBackend() : this(Settings()) { }

    public JoltTestBackend(PhysicsBackendSettings settings) => B.Initialize(settings);

    /// <summary>The region defaults, with CollisionSteps = 6 as AddRegion sets it.</summary>
    public static PhysicsBackendSettings Settings(int maxBodies = 65536, int maxBodyPairs = 65536, int maxContactConstraints = 16384)
    {
        var s = PhysicsBackendSettings.Default;
        s.CollisionSteps = 6;
        s.MaxBodies = maxBodies;
        s.MaxBodyPairs = maxBodyPairs;
        s.MaxContactConstraints = maxContactConstraints;
        return s;
    }

    public BodyId Ground(float halfSize = 64f, float topZ = 0f)
    {
        var shape = B.CreateBoxShape(new Vector3(halfSize, halfSize, 1f));
        var d = BodyDesc.Default;
        d.Shape = shape;
        d.Position = new Vector3(128f, 128f, topZ - 1f);
        return B.CreateBody(d);
    }

    public BodyId Dynamic(ShapeId shape, Vector3 pos, uint userData = 0, bool wantsContacts = false, bool startActive = true)
    {
        var d = BodyDesc.Default;
        d.Shape = shape;
        d.Position = pos;
        d.Layer = PhysicsLayer.Dynamic;
        d.MotionType = BodyMotionType.Dynamic;
        d.StartActive = startActive;
        d.UserData = userData;
        d.WantsContactEvents = wantsContacts;
        return B.CreateBody(d);
    }

    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
    public static bool Finite(in BodyState s) => Finite(s.Position) && Finite(s.Orientation) && Finite(s.LinearVelocity) && Finite(s.AngularVelocity);

    public StepResult Step(int bodyBuf = 1024, int charBuf = 64, int contactBuf = 4096)
        => B.Step(1f / 11f, new BodyState[bodyBuf], new CharacterState[charBuf], new ContactReport[contactBuf]);

    public void Dispose() => B.Dispose();
}

/// <summary>
/// Every backend test that steps a real backend runs in this collection, which xunit runs on its own after the
/// parallel ones. A test stepping while TeleportCrossingHarnessTests' two regions stepped put three
/// PhysicsSystem::Update calls on one shared JobSystemThreadPool at once, and all three spun inside native
/// Update indefinitely (one core busy, no progress). That is a hazard of a shared pool (see
/// ConcurrentUpdateTests) - not something these tests should trip over at random.
/// </summary>
[Xunit.CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JoltNativeSerial
{
    public const string Name = "Jolt native (serial)";
}
