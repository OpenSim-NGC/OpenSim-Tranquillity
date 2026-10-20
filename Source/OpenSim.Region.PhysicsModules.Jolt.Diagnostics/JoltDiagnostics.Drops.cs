/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using static OpenSim.Region.PhysicsModules.Jolt.JoltScene;

namespace OpenSim.Region.PhysicsModules.Jolt.Diagnostics
{
    // The drop tracker of `jolt droptest`, `jolt dropmesh` and `jolt dropstatus`, and the places in the module's step
    // that feed it (IJoltDiagnostics). This code was part of JoltScene.cs before the test commands moved out of the
    // module.
    internal sealed partial class JoltDiagnostics
    {
        // The tracked physical drops. _lastBoxRestZ/_lastMeshRestZ persist across drops so a re-run can report
        // determinism (same rest height).
        private float _lastBoxRestZ = float.NaN, _lastMeshRestZ = float.NaN;
        private sealed class DropTrack
        {
            public uint LocalId;
            public string Kind;                 // "box" or "mesh"
            public float StartZ;
            public long StartStep;
            public float MinZ = float.MaxValue;
            public float LastZ, LastSpeed;
            public int JustDeactivatedCount;
            public float RestZ = float.NaN;
            public long RestStep = -1;
            public float ExpectedMass;          // box: volume*density; mesh: hull(=mesh)volume*density
            public float ExpectedRestZ;         // terrain Z + half-height
        }
        private readonly List<DropTrack> _drops = new List<DropTrack>();
        private long _logStepsUntil = -1;   // window: log per-frame dt/ActiveBodyCount/liveZ after a drop

        // Test scaffolding gated by [Startup] JoltAutoDropTest, and only when [Jolt] TestCommands is true:
        // auto-drop a few physical boxes so fall + settle-on-real-terrain is verifiable from the log
        // ([dropframe] liveZ, or `jolt dropstatus`) with no viewer, and every region carries bodies for a
        // concurrent-step load test. Uses the same DropOne code path as `jolt droptest`. NEVER runs unless the
        // flags are set, so it cannot affect real regions.
        public void AutoDrop(IConfigSource source)
        {
            if (AutoDropTestEnabled(source))
            {
                ClearTestPrims();
                DropOne("box", new Vector3(2f, 2f, 2f), 118f, 128f);
                DropOne("box", new Vector3(2f, 2f, 2f), 124f, 128f);
                DropOne("box", new Vector3(2f, 2f, 2f), 130f, 128f);
                m_log.LogInformation($"{LogHeader} JoltAutoDropTest: dropped 3 physical boxes in '{RegionName}' (watch [dropframe] / `jolt dropstatus`).");
            }
        }

        // Update a tracked drop from a drained BodyState (called in the Simulate drain).
        public void BodyUpdated(in BodyState bs)
        {
            if (_drops.Count == 0)
                return;
            foreach (DropTrack t in _drops)
            {
                if (t.LocalId != bs.UserData) continue;
                float z = bs.Position.Z;
                t.LastZ = z;
                if (z < t.MinZ) t.MinZ = z;
                t.LastSpeed = bs.LinearVelocity.Length();
                if ((bs.Flags & BodyStateFlags.JustDeactivated) != 0)
                {
                    t.JustDeactivatedCount++;    // must be EXACTLY 1 at rest (the settle update)
                    t.RestZ = z;
                    t.RestStep = _stepCount;
                }
                break;
            }
        }

        // Windowed per-frame diagnostic (set by a drop): is Step advancing with a REAL dt, is the
        // just-dropped body in our active set, and is its Z actually changing? It tells apart why a
        // drop might hang - dt=0 => idle-step stall; active=1 but
        // liveZ frozen => body active-but-not-integrated (deeper); active=0 => activation lost.
        public void StepReported(IPhysicsBackend backend, long stepCount, float timeStep, in StepResult r)
        {
            if (stepCount <= _logStepsUntil && _drops.Count > 0)
            {
                DropTrack td = _drops[_drops.Count - 1];
                float lz = float.NaN, vz = float.NaN; bool ja = false;
                lock (_prims)
                    if (_prims.TryGetValue(td.LocalId, out JoltPrim jd) && backend.TryGetBodyState(jd.BodyHandle, out BodyState sd))
                    { lz = sd.Position.Z; vz = sd.LinearVelocity.Z; ja = (sd.Flags & BodyStateFlags.Active) != 0; }
                m_log.LogDebug($"{LogHeader} [dropframe] step={stepCount} dt={timeStep:0.0000} active={r.ActiveBodyCount} updates={r.BodyUpdateCount} box(id={td.LocalId}) liveZ={lz:0.000} vZ={vz:0.000} joltActive={ja}");
            }
        }

        // The tracked drops' lines of the `jolt heights` report.
        public void DescribeDrops(IPhysicsBackend backend, Action<string> output)
        {
            foreach (DropTrack t in _drops)
            {
                lock (_prims)
                    if (_prims.TryGetValue(t.LocalId, out JoltPrim jp) && backend.TryGetBodyState(jp.BodyHandle, out BodyState st))
                        output($"  (c) drop {t.Kind} id={t.LocalId} : liveZ={st.Position.Z:0.000} joltActive={(((st.Flags & BodyStateFlags.Active) != 0) ? "Y" : "N")} startZ={t.StartZ:0.00}");
            }
        }
    }
}
