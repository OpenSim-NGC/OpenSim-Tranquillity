/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;

// Several test projects compile this file, and each sets only the fields its tests use; the rest keep their defaults.
#pragma warning disable CS0649

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A scalar emulation of InWorldz Halcyon's linear motor (VehicleDynamics.cs:2576-2725 and MoveLinear, 3047-3078; read, not
/// run), written out here a second time in doubles as the reference the controller is compared with. A vehicle whose frame
/// is the world's, with friction and deflection off, and a linear motor only: each step computes the velocity the motor
/// wants from the velocity at the start of the step and the motor's target, blends it in by the decay, and adds the
/// difference as a change of velocity, after the up-motor gravity rule; the body's own gravity follows. Halcyon runs its
/// motor once in each of its physics steps of 15 ms, so here the motor is stepped on a clock of carried time: for every 15 ms
/// that goes by in the steps the caller takes (of any length), one motor step of 15 ms, reading the velocity the one before
/// left; friction and gravity are integrated over each step the caller takes.
/// </summary>
internal sealed class HalcyonLinearReference
{
    private readonly double[] _dir, _ts, _td;
    private readonly double _maxSpeed;
    public readonly double[] V = new double[3];
    public readonly double[] Target = new double[3];
    public double Index;

    /// <summary>The linear friction timescales; 1000 s and over is off. Friction takes the share 1 - e^(-h / T) of the velocity
    /// at the start of the step away, as a change of its own beside the motor's.</summary>
    public double[] Friction = { 1000, 1000, 1000 };

    /// <summary>Whether the target replaces a lower velocity on an axis the motor never had a direction on (Halcyon's code as written;
    /// the controller's default leaves such an axis its velocity, see VehicleSettings.MotorTargetOnUndrivenAxes).</summary>
    public bool TargetOnUndrivenAxes;
    private readonly bool[] _driven = new bool[3];   // the axes the motor has had a direction on: the rule goes on acting on them

    public HalcyonLinearReference(double[] direction, double[] timescale, double[] decay, double maxSpeed = 200.0)
    {
        _dir = (double[])direction.Clone();
        _ts = timescale;
        _td = decay;
        _maxSpeed = maxSpeed;
        Index = DecayStart();
    }

    // MoveLinear: "SL cheat, ramp and decay should proceed simultaneously, but short decays often will prevent motor
    // ramp-up altogether. The cheat is to delay the decay by a tiny amount."
    private double DecayStart()
    {
        double mag = Math.Sqrt(_ts[0] * _ts[0] + _ts[1] * _ts[1] + _ts[2] * _ts[2]);
        double f = mag < 0.9 ? 1.0 - mag : 0.0;
        return 0.0156 * (1.0 - f) - 0.06 * f;
    }

    /// <summary>MoveLinear for a new direction: the target is scaled by the decay it has had, and the clock starts over.</summary>
    public void Move(double[] direction)
    {
        if (Index > 1.0)
            for (int i = 0; i < 3; i++)
                Target[i] *= Decay(Index, _td[i]);
        Index = DecayStart();
        for (int i = 0; i < 3; i++)
            _dir[i] = direction[i];
    }

    public static double Decay(double index, double timescale)
    {
        if (index <= 0) return 1.0;
        double f = Math.Exp(-index / timescale);
        return f < 0.002 ? 0.0 : f;
    }

    private static double Growth(double s, double e, double steps)
    {
        double elog;
        if (s * e > 0)
            elog = Math.Log(Math.Abs(e) / Math.Abs(s));
        else if (e == 0 && s == 0)
            return 0;
        else if (e == 0)
            elog = -Math.Log(1.0 + Math.Abs(s));
        else if (s == 0)
            elog = Math.Log(1.0 + Math.Abs(e));
        else if (Math.Abs(s) > 0.3)
            elog = -Math.Log(1.0 + Math.Abs(s - e));
        else
            return -2.010203;
        return elog / steps;
    }

    public const double HalcyonStep = 0.015;
    private double _carry;

    /// <summary>One step of h seconds. gravityZ is the body's own (negative) gravity, buoyancy the vehicle's.</summary>
    public void Step(double h, double gravityZ = 0.0, double buoyancy = 0.0)
    {
        double[] friction = new double[3];
        for (int i = 0; i < 3; i++)
            friction[i] = Friction[i] >= 1000 ? 0.0 : V[i] * (Math.Exp(-h / Friction[i]) - 1.0);

        // The motor steps that fall due: each covers the 15 ms that has just gone by, so its clock reads what it read at the
        // start of that interval, and each works from the velocity the one before left.
        double carried = _carry;
        _carry += h;
        int due = (int)Math.Floor(_carry / HalcyonStep + 1e-6);
        if (due > 0)
            _carry = Math.Max(0.0, _carry - due * HalcyonStep);
        double start = Index;
        double[] v = (double[])V.Clone();
        double[] motor = new double[3];
        for (int k = 0; k < due; k++)
        {
            double[] delta = Fire(start - carried + k * HalcyonStep, v, gravityZ, buoyancy);
            for (int i = 0; i < 3; i++)
            {
                v[i] += delta[i];
                motor[i] += delta[i];
            }
        }
        Index = start + h;
        for (int i = 0; i < 3; i++) V[i] += motor[i] + friction[i];
        V[2] += gravityZ * h;
    }

    // One of Halcyon's motor steps (15 ms) from the velocity v, with the decay clock reading `index`.
    private double[] Fire(double index, double[] v, double gravityZ, double buoyancy)
    {
        double[] g = new double[3];
        for (int i = 0; i < 3; i++)
            g[i] = Decay(index, _td[i]);
        double[] motor = new double[3];
        if (g[0] == 0 && g[1] == 0 && g[2] == 0)
        {
            for (int i = 0; i < 3; i++) Target[i] = v[i];
            return motor;
        }
        double[] nv = new double[3];
        double[] ramp = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double sign = _dir[i] < 0 ? -1.0 : 1.0;
            double adj = v[i];
            if (_dir[i] != 0) _driven[i] = true;
            if (sign * (Target[i] - adj) >= 0 && (_dir[i] != 0 || TargetOnUndrivenAxes || _driven[i])) adj = Target[i];
            ramp[i] = Growth(adj, _dir[i], Math.Max(_ts[i] / HalcyonStep, 1.0));
            nv[i] = adj + adj * ramp[i];
            if (Math.Abs(ramp[i] - -2.010203) < 1e-9) ramp[i] = -ramp[i];
            if (ramp[i] > 0 && _dir[i] != 0 && Math.Abs(nv[i]) < 0.005) nv[i] = sign * 0.005 * 8;
            if (_dir[i] * nv[i] < 0 || ramp[i] < 0) Target[i] = nv[i];
            else Target[i] = Math.Clamp(nv[i], -Math.Abs(_dir[i]), Math.Abs(_dir[i]));
            nv[i] = Math.Clamp(nv[i], -_maxSpeed, _maxSpeed);
            if (Target[i] == 0 && _dir[i] == 0) nv[i] = v[i];
            if (ramp[i] >= 0 && sign * (v[i] - nv[i]) > 0) g[i] = 0;
        }
        double[] w = new double[3];
        for (int i = 0; i < 3; i++)
            w[i] = nv[i] * g[i] + v[i] * (1.0 - g[i]);
        if (Math.Abs(w[0]) >= 0.005 || Math.Abs(w[1]) >= 0.005 || Math.Abs(w[2]) >= 0.005)
        {
            double[] delta = { w[0] - v[0], w[1] - v[1], w[2] - v[2] };
            // "Gravity check. Any up Z-forces less than the effective gravity are nulled."
            if (delta[2] > 0)
            {
                delta[2] += gravityZ * (1.0 - buoyancy);
                if (delta[2] < 0) delta[2] = 0;
            }
            for (int i = 0; i < 3; i++) motor[i] = delta[i];
        }
        return motor;
    }
}
