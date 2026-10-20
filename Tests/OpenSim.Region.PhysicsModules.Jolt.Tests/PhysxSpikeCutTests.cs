/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Omv = OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>[Jolt] VehicleSpikeMitigation: which behaviour turns the vehicle step's spike cut on, and that a setting forces it.</summary>
public class VehicleSpikeMitigationConfigTests
{
    private static VehicleSettings Settings(string behaviour, string cut, List<string> warnings = null)
    {
        var src = new IniConfigSource();
        IConfig jolt = src.AddConfig("Jolt");
        if (behaviour != null) jolt.Set("VehicleBehaviour", behaviour);
        if (cut != null) jolt.Set("VehicleSpikeMitigation", cut);
        return JoltConfig.FromConfig(src, warnings).ToVehicleSettings();
    }

    [Theory]
    [InlineData("InWorldz", null, true)]
    [InlineData("SecondLife", null, false)]
    [InlineData(null, null, true)]          // the region default is InWorldz
    [InlineData("InWorldz", "false", false)]
    [InlineData("SecondLife", "true", true)]
    [InlineData("InWorldz", "true", true)]
    [InlineData("SecondLife", "false", false)]
    public void The_cut_follows_the_behaviour_unless_the_setting_forces_it(string behaviour, string cut, bool expected)
    {
        var warnings = new List<string>();
        Assert.Equal(expected, Settings(behaviour, cut, warnings).SpikeMitigationOn);
        Assert.Empty(warnings);
    }

    [Fact]
    public void An_invalid_value_warns_and_leaves_the_choice_to_the_behaviour()
    {
        var warnings = new List<string>();
        VehicleSettings s = Settings("SecondLife", "maybe", warnings);
        Assert.False(s.SpikeMitigationOn);
        Assert.Single(warnings);
        Assert.Contains("VehicleSpikeMitigation", warnings[0]);
        Assert.True(Settings("InWorldz", "maybe").SpikeMitigationOn);
    }
}

public partial class PhysxSpikeTests
{
    // The cut compares the engine's change of the vehicle's velocity over the last 15.6 ms with 50 m/s^2 up and 100 rad/s^2
    // about the vehicle's y axis, and what the vehicle's own step wrote into the velocity is taken out of that change. So it
    // can act only on what the engine does: contacts (and gravity, which is downward). An airplane released 40 m up with its
    // motor held falls to the ground and lands; the cut must act at the landing, never on the way down.
    [Theory]
    [MemberData(nameof(Cells))]
    public void The_spike_cut_acts_on_an_airplane_only_when_it_touches_the_ground(double rate, int n)
    {
        using var p = new SpikeProbe(rate, n, jolt => jolt.Set("VehicleBehaviour", "InWorldz"));
        JoltPrim box = p.Rig.AddBox(new Omv.Vector3(3f, 3f, 0.5f), new Omv.Vector3(128f, 128f, StepRateRig.Ground + 40f), vehicle: true);
        box.VehicleType = (int)Vehicle.TYPE_AIRPLANE;
        p.Rig.Vector(Vehicle.LINEAR_MOTOR_DIRECTION, new Omv.Vector3(15f, 0f, 0f));
        int last = 0, inFlight = 0, atGround = 0;
        float lastZ = box.Position.Z;
        p.RunTo(8.0, _ =>
        {
            int cuts = box.VehicleControl.SpikeAngularCuts + box.VehicleControl.SpikeLinearCuts;
            float z = box.Position.Z;
            if (cuts > last)
            {
                // A cut is "in flight" only if the vehicle was well clear of the ground before and after the heartbeat it acted in.
                if (Math.Min(lastZ, z) > StepRateRig.Ground + 3f) inFlight += cuts - last; else atGround += cuts - last;
            }
            last = cuts;
            lastZ = z;
        });
        Say($"[physx] spike cut airplane | {rate} Hz x {n} | cuts in flight {inFlight}, at the ground {atGround + (box.VehicleControl.SpikeAngularCuts + box.VehicleControl.SpikeLinearCuts - last)} | ends at z {box.Position.Z:0.00}");
        Assert.True(inFlight == 0, $"{rate} Hz x {n}: the cut acted {inFlight} times while the airplane was more than 3 m above the ground");
    }

    // [Jolt] VehicleSpikeMitigation under Second Life behaviour: the vehicle across the seams and the vehicle over rough
    // ground with the cut forced on and forced off (the behaviour's own choice there is off, and is asserted by the
    // rough-ground vehicle test).
    // The figures are recorded; what is asserted is that the setting does what it says.
    [Theory]
    [MemberData(nameof(Cells))]
    public void SecondLife_behaviour_with_the_spike_cut_forced_on_and_off(double rate, int n)
    {
        var failures = new List<string>();
        var size = new Omv.Vector3(2f, 1f, 0.5f);
        foreach (bool cut in new[] { false, true })
        {
            foreach (Seam seam in Seams)
            {
                int cuts = 0;
                var runs = Nudges.Select(k => { var m = DriveOverSeam(rate, n, false, seam, k, 0.0, 5.0, out _, vehicle: true, behaviour: "SecondLife", cutSetting: cut); cuts += LastCuts; return m; }).ToList();
                Say($"[physx] SL T1 vehicle {seam.Name} | cut {(cut ? "on" : "off")} | {rate} Hz x {n} | dvz15 {runs.Max(m => m.MaxDvz15):0.0} dwy15 {runs.Max(m => m.MaxDwy15):0.0} tilt {runs.Max(m => m.MaxTiltDegrees):0.0} deg, cuts {cuts}");
                if (!cut && cuts != 0) failures.Add($"{seam.Name}: the cut acted {cuts} times with VehicleSpikeMitigation = false");
            }
            foreach (double speed in new[] { 5.0, 15.0 })
            {
                int cuts = 0;
                var runs = Nudges.Select(k => { var m = DriveOverGround(rate, n, true, false, size, speed, k, null, true, true, "SecondLife", cut); cuts += LastCuts; return m; }).ToList();
                Say($"[physx] SL T2 vehicle {speed} m/s | cut {(cut ? "on" : "off")} | {rate} Hz x {n} | up15 {runs.Max(m => m.MaxUp15):0.0} dwy15 {runs.Max(m => m.MaxDwy15):0.0} tilt {runs.Max(m => m.MaxTiltDegrees):0} deg, cuts {cuts}");
                if (!cut && cuts != 0) failures.Add($"rough ground at {speed} m/s: the cut acted {cuts} times with VehicleSpikeMitigation = false");
                if (cut && speed == 15.0 && cuts == 0) failures.Add("rough ground at 15 m/s: the cut never acted with VehicleSpikeMitigation = true");
            }
        }
        Assert.True(failures.Count == 0, $"{rate} Hz x {n}: " + string.Join("; ", failures));
    }
}
