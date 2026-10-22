/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;

// Several test projects compile this file, and each sets only the fields its tests use; the rest keep their defaults.
#pragma warning disable CS0649

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>The prim shapes and the mesher of <c>PrimHullProbeTests</c>, which the harness tests also build on.</summary>
public partial class PrimHullProbeTests
{
    // ------------------------------------------------------------------ the shapes

    internal sealed class Spec
    {
        public string Name;
        public ProfileShape Profile = ProfileShape.Square;
        public HollowShape Hole = HollowShape.Same;
        public float Hollow;
        public float CutBegin, CutEnd = 1f;          // profile cut, 0..1 of the profile
        public bool Circular;
        public float PathBegin, PathEnd = 1f;        // path cut, 0..1 of the path
        public int Twist, TwistBegin;                // PathTwist units (1.8 degrees linear, 3.6 degrees circular)
        public int Revolutions;                      // PathRevolutions units (+1.5 % of a turn each)
        public byte ScaleX = 100, ScaleY = 100;

        public PrimitiveBaseShape Build()
        {
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            pbs.PathCurve = (byte)(Circular ? Extrusion.Curve1 : Extrusion.Straight);
            pbs.ProfileShape = Profile;
            pbs.HollowShape = Hole;
            pbs.ProfileBegin = (ushort)MathF.Round(CutBegin * 50000f);
            pbs.ProfileEnd = (ushort)MathF.Round((1f - CutEnd) * 50000f);
            pbs.ProfileHollow = (ushort)MathF.Round(Hollow * 50000f);
            pbs.PathBegin = (ushort)MathF.Round(PathBegin * 50000f);
            pbs.PathEnd = (ushort)MathF.Round((1f - PathEnd) * 50000f);
            pbs.PathTwist = (sbyte)Twist;
            pbs.PathTwistBegin = (sbyte)TwistBegin;
            pbs.PathRevolutions = (byte)Revolutions;
            pbs.PathScaleX = ScaleX;
            pbs.PathScaleY = ScaleY;
            pbs.SculptEntry = false;
            return pbs;
        }

        public override string ToString() => Name;
    }

    private static Spec Torus(string name, ProfileShape profile, float hollow = 0f) =>
        new Spec { Name = name, Profile = profile, Hollow = hollow, Circular = true, ScaleX = 100, ScaleY = 175 };

    // The set the measurement is asked for. UI names: for a box, cylinder or prism "path cut" is the profile cut
    // (ProfileBegin / ProfileEnd), the pie slice; for a torus, tube or ring it is the path cut (PathBegin / PathEnd).
    internal static List<Spec> Shapes() => new List<Spec>
    {
        new Spec { Name = "hollow box 0.5", Profile = ProfileShape.Square, Hollow = 0.5f },
        new Spec { Name = "hollow box 0.95 (thin wall)", Profile = ProfileShape.Square, Hollow = 0.95f },
        new Spec { Name = "hollow cylinder 0.5", Profile = ProfileShape.Circle, Hollow = 0.5f },
        new Spec { Name = "path-cut cylinder (pie 0.25 to 1.0, 270 deg)", Profile = ProfileShape.Circle, CutBegin = 0.25f },
        new Spec { Name = "path-cut cylinder (pie 0.25 to 0.75, 180 deg)", Profile = ProfileShape.Circle, CutBegin = 0.25f, CutEnd = 0.75f },
        new Spec { Name = "profile-cut box (0 to 0.75, 270 deg)", Profile = ProfileShape.Square, CutEnd = 0.75f },
        new Spec { Name = "profile-cut hollow box (0 to 0.75, hollow 0.5)", Profile = ProfileShape.Square, CutEnd = 0.75f, Hollow = 0.5f },
        new Spec { Name = "cylinder, path begin/end 0.25..0.75 (straight path)", Profile = ProfileShape.Circle, PathBegin = 0.25f, PathEnd = 0.75f },
        new Spec { Name = "twisted hollow box (180 deg)", Profile = ProfileShape.Square, Hollow = 0.5f, Twist = 100 },
        Torus("torus", ProfileShape.Circle),
        Torus("torus, hollow 0.5", ProfileShape.Circle, 0.5f),
        Torus("twisted torus (180 deg)", ProfileShape.Circle) .With(s => s.Twist = 50),
        Torus("twisted torus (360 deg)", ProfileShape.Circle) .With(s => s.Twist = 100),
        Torus("tube", ProfileShape.Square),
        Torus("tube, hollow 0.5", ProfileShape.Square, 0.5f),
        Torus("ring", ProfileShape.EquilateralTriangle),
        Torus("torus, path cut 0..0.5", ProfileShape.Circle).With(s => s.PathEnd = 0.5f),
        new Spec { Name = "hollow sphere 0.5", Profile = ProfileShape.HalfCircle, Hollow = 0.5f, Circular = true, ScaleX = 100, ScaleY = 100 },
        new Spec { Name = "hollow sphere 0.9", Profile = ProfileShape.HalfCircle, Hollow = 0.9f, Circular = true, ScaleX = 100, ScaleY = 100 },
        // A bowl: a hollow sphere with its top dimpled away (profile cut 0 to 0.5 of the half circle: the lower quarter turn).
        new Spec { Name = "bowl (hollow sphere 0.8, dimple 0..0.5)", Profile = ProfileShape.HalfCircle, Hollow = 0.8f, CutEnd = 0.5f, Circular = true, ScaleX = 100, ScaleY = 100 },
    };


    internal static Meshmerizer NewMesher(string scratch)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("meshing", "Meshmerizer");
        startup.Set("DecodedSculptMapPath", scratch);
        var mesher = new Meshmerizer();
        mesher.Initialise(config);
        return mesher;
    }
}

internal static class SpecExtensions
{
    internal static PrimHullProbeTests.Spec With(this PrimHullProbeTests.Spec s, Action<PrimHullProbeTests.Spec> change) { change(s); return s; }
}
