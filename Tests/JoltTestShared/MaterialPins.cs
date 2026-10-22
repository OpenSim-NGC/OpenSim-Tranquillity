/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

// Shared by every test project that runs the Jolt native and by the harness program, which links this file.

/// <summary>
/// [Jolt] material keys a test assembly may pin to the values the module had before vehicles kept their parts' materials
/// and the combine rules, terrain and damping defaults changed: a vehicle with no contact friction, no damping, the
/// geometric-mean and product combine rules and terrain 0.6 / 0. The tests of a controller's laws (a friction timescale's
/// decay, a motor's ramp, a rest rule) need a vehicle that only its controller acts on, and a test class asks for the pins
/// with <see cref="Enabled"/> (for the test being run). Every other run, and the harness program and the baselines, use the
/// module's shipped defaults. The pins leave <c>BasicForcesBehaviour</c> at the shipped default (InWorldz); a test of the
/// Second Life move, hover or look-at laws asks for that one key with <see cref="BasicForces"/>.
/// </summary>
public static class MaterialPins
{
    private static readonly AsyncLocal<bool> EnabledHere = new();
    private static readonly AsyncLocal<string> BasicForcesHere = new();

    /// <summary>Whether the runs of the test now executing are pinned. Per test, so classes that do not ask run in parallel unpinned.</summary>
    public static bool Enabled
    {
        get => EnabledHere.Value;
        set => EnabledHere.Value = value;
    }

    /// <summary>The <c>[Jolt] BasicForcesBehaviour</c> the pinned runs of the test now executing take; null leaves the shipped default.</summary>
    public static string BasicForces
    {
        get => BasicForcesHere.Value;
        set => BasicForcesHere.Value = value;
    }

    private static readonly (string Key, string Value)[] Pins =
    {
        ("VehicleContactFriction", "0"), ("PrimLinearDamping", "0"), ("PrimAngularDamping", "0"),
        ("FrictionCombine", "GeometricMean"), ("RestitutionCombine", "Multiply"),
        ("TerrainFriction", "0.6"), ("TerrainRestitution", "0"),
    };

    /// <summary>A vehicle that bounces off what it hits only through its material: its restitution 0, as a vehicle's was.</summary>
    public static void PinVehicle(OpenSim.Region.PhysicsModules.SharedBase.PhysicsActor vehicle, bool pinned)
    {
        if (pinned)
            vehicle.Restitution = 0f;
    }

    public static void Apply(Nini.Config.IConfig jolt)
    {
        if (!Enabled)
            return;
        foreach ((string key, string value) in Pins)
            jolt.Set(key, value);
        if (BasicForces != null)
            jolt.Set("BasicForcesBehaviour", BasicForces);
    }
}
