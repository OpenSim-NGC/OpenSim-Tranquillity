/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Editing a physical object suspends its physics and deselecting it resumes them, through the harness at the 11 Hz
/// heartbeat, with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat. The viewer's edit tool selects with
/// ObjectSelect and lets go with ObjectDeselect (Scene.SelectPrim and DeselectPrim); SceneObjectPart.IsSelected then sets
/// SceneObjectGroup.IsSelected, which hands Selected to the root's actor and then to every part's actor. A box moving under
/// a set force (llSetForce) stops while it is selected and the force moves it again once it is let go; a falling linkset
/// stops while it is selected and falls again, from rest, once it is let go (ubODE's ODEPrim.DoSelectedStatus and Move).
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SelectionSuspendTests
{
    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private const float X = 128f, Y = 128f;
    private const uint RootId = 1701;
    private const float Gravity = 9.80665f;

    private readonly record struct Point(double T, Vector3 Position, Vector3 Velocity);

    private static List<Point> Run(double physicsHz, float seconds, Action<Run> setup, Action<Run> act)
    {
        var trace = new List<Point>();
        var sc = new Scenario
        {
            Name = "selection-suspend",
            DefaultDuration = _ => seconds,
            Setup = setup,
            Input = r =>
            {
                trace.Add(new Point(r.PhysicsTime, r.Actor.Position, r.Actor.Velocity));
                act(r);
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
        return trace;
    }

    // SceneObjectGroup.IsSelected: the root's actor, then every part's (the root's again among them).
    private static void Select(bool selected, IReadOnlyList<PhysicsActor> parts)
    {
        parts[0].Selected = selected;
        foreach (PhysicsActor p in parts)
            p.Selected = selected;
    }

    // A floating 1 m box (mass 10) pushed along x by a set force of 20 N, so it gains 2 m/s every second. Selected after
    // 1.3 s it stops and stays where it is for 2 s; let go, the force moves it again at 2 m/s^2.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_under_a_set_force_stops_while_selected_and_the_force_moves_it_again_when_let_go(double physicsHz)
    {
        const double ForceAt = 0.2, SelectAt = 1.5, DeselectAt = 3.5;
        var parts = new List<PhysicsActor>();
        bool forced = false, selected = false, deselected = false;
        double selectedT = double.NaN, deselectedT = double.NaN;
        Vector3 heldAt = Vector3.Zero;
        float speedBefore = 0f;
        List<Point> trace = Run(physicsHz, 6f,
            r =>
            {
                PhysicsActor box = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(64f, Y, r.GroundAt(64f, Y) + 20f), Quaternion.Identity, true, RootId);
                box.Buoyancy = 1f;
                r.Actor = box;
                parts.Add(box);
            },
            r =>
            {
                PhysicsActor box = parts[0];
                if (!forced && r.Now >= ForceAt) { forced = true; box.Force = new Vector3(20f, 0f, 0f); }
                if (!selected && r.Now >= SelectAt)
                {
                    selected = true;
                    speedBefore = box.Velocity.X;
                    Select(true, parts);
                    selectedT = r.PhysicsTime;
                    heldAt = box.Position;
                }
                if (selected && !deselected && r.Now >= DeselectAt)
                {
                    deselected = true;
                    Select(false, parts);
                    deselectedT = r.PhysicsTime;
                }
            });

        Assert.True(speedBefore > 2f, $"the force had not moved the box before it was selected ({speedBefore:0.###} m/s)");
        List<Point> held = trace.FindAll(p => p.T > selectedT + 1e-9 && p.T <= deselectedT + 1e-9);
        Assert.True(held.Count >= 20, $"too few samples while selected ({held.Count})");
        foreach (Point p in held)
        {
            Assert.True(Vector3.Distance(p.Position, heldAt) < 0.001f, $"moved while selected at t={p.T:0.00}: {p.Position} (held at {heldAt})");
            Assert.True(p.Velocity.Length() < 1e-4f, $"reported moving while selected at t={p.T:0.00}: {p.Velocity}");
        }

        // Let go: from rest, the force gives it 2 m/s^2 again (the body's 5 % a second damping takes a little off).
        Point a = trace.Find(p => p.T >= deselectedT + 0.5 - 1e-9);
        Point b = trace.Find(p => p.T >= deselectedT + 1.5 - 1e-9);
        float accel = (b.Velocity.X - a.Velocity.X) / (float)(b.T - a.T);
        Assert.InRange(accel, 1.8f, 2.05f);
        Assert.True(b.Position.X > heldAt.X + 1f, $"the box did not move on after it was let go ({b.Position})");
    }

    // A five-prim linkset falling from 40 m is selected after 1 s: it stops where it is and stays there for 2 s. Let go,
    // it falls again from rest, g t^2 / 2 in the first second.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_falling_linkset_stops_while_selected_and_falls_again_when_let_go(double physicsHz)
    {
        const double SelectAt = 1.0, DeselectAt = 3.0;
        var parts = new List<PhysicsActor>();
        bool selected = false, deselected = false;
        double selectedT = double.NaN, deselectedT = double.NaN;
        Vector3 heldAt = Vector3.Zero;
        float speedBefore = 0f;
        List<Point> trace = Run(physicsHz, 4.5f,
            r =>
            {
                float z = r.GroundAt(X, Y) + 40f;
                PhysicsActor root = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X, Y, z), Quaternion.Identity, true, RootId);
                parts.Add(root);
                float[] offsets = { -4f, -2f, 2f, 4f };
                for (int i = 0; i < offsets.Length; i++)
                    parts.Add(r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X + offsets[i], Y, z), Quaternion.Identity, true,
                                        RootId + 1 + (uint)i, root));
                r.Actor = root;
            },
            r =>
            {
                if (!selected && r.Now >= SelectAt)
                {
                    selected = true;
                    speedBefore = -parts[0].Velocity.Z;
                    Select(true, parts);
                    selectedT = r.PhysicsTime;
                    heldAt = parts[0].Position;
                }
                if (selected && !deselected && r.Now >= DeselectAt)
                {
                    deselected = true;
                    Select(false, parts);
                    deselectedT = r.PhysicsTime;
                }
            });

        Assert.True(speedBefore > 7f, $"the linkset was not falling when it was selected ({speedBefore:0.###} m/s)");
        List<Point> held = trace.FindAll(p => p.T > selectedT + 1e-9 && p.T <= deselectedT + 1e-9);
        Assert.True(held.Count >= 20, $"too few samples while selected ({held.Count})");
        foreach (Point p in held)
        {
            Assert.True(Vector3.Distance(p.Position, heldAt) < 0.001f, $"moved while selected at t={p.T:0.00}: {p.Position} (held at {heldAt})");
            Assert.True(p.Velocity.Length() < 1e-4f, $"reported moving while selected at t={p.T:0.00}: {p.Velocity}");
        }

        Point at = trace.Find(p => p.T >= deselectedT + 1.0 - 1e-9);
        float fell = heldAt.Z - at.Position.Z;
        float expected = 0.5f * Gravity * (float)(at.T - deselectedT) * (float)(at.T - deselectedT);
        Assert.True(MathF.Abs(fell - expected) <= 0.1f * expected, $"fell {fell:0.###} m in {at.T - deselectedT:0.###} s after it was let go, not {expected:0.###}");
        Assert.True(at.Velocity.Z < -8f, $"not falling a second after it was let go ({at.Velocity})");
    }
}
