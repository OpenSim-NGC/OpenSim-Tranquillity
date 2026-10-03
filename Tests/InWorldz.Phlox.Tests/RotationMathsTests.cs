/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llRot2Angle, llRot2Axis and llAngleBetween. SL (llRot2Angle): "a positive angle &lt;= PI radians, that is, it is the
/// unsigned minimum angle". YEngine (LSL_Api.cs llRot2Angle / llRot2Axis / llAngleBetween) normalises the input, has no
/// small-angle cut-off, and measures the angle between two rotations by their normalised dot product.
/// </summary>
// No process-wide state: the class runs in parallel.
public class RotationMathsTests
{
    private static LSLSystemAPI Api() => new LSLSystemAPI(null, null, 0, UUID.Random());

    private static Quaternion AxisAngle(Vector3 axis, float angle)
        => Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);

    [Fact]
    public void ASmallRotationKeepsItsAngle()
    {
        // OpenMetaverse's GetAxisAngle answered 0 below about 0.02 rad.
        Assert.Equal(0.01f, Api().llRot2Angle(AxisAngle(new Vector3(0, 0, 1), 0.01f)), 4);
    }

    [Fact]
    public void ASmallRotationKeepsItsAxis()
    {
        Vector3 axis = Api().llRot2Axis(AxisAngle(new Vector3(0, 1, 0), 0.01f));
        Assert.Equal(0f, axis.X, 3);
        Assert.Equal(1f, axis.Y, 3);
        Assert.Equal(0f, axis.Z, 3);
    }

    [Fact]
    public void AnUnnormalisedRotationIsNormalisedFirst()
    {
        // <0,0,2,2> is a 90 degree turn about Z, scaled by 2*sqrt(2).
        var q = new Quaternion(0, 0, 2, 2);
        var api = Api();
        Assert.Equal((float)(Math.PI / 2), api.llRot2Angle(q), 4);
        Vector3 axis = api.llRot2Axis(q);
        Assert.Equal(0f, axis.X, 4);
        Assert.Equal(0f, axis.Y, 4);
        Assert.Equal(1f, axis.Z, 4);
    }

    [Fact]
    public void ATurnOfThreeHalvesPiReadsAsHalfPi()
    {
        // SL: "A rotation of 3/2 PI radians (270 degrees) will return an angle of PI / 2 radians, not -PI / 2."
        var q = AxisAngle(new Vector3(0, 0, 1), (float)(1.5 * Math.PI));
        var api = Api();
        Assert.Equal((float)(Math.PI / 2), api.llRot2Angle(q), 4);
        // The axis goes with the folded angle: a 90 degree turn about -Z.
        Assert.Equal(-1f, api.llRot2Axis(q).Z, 4);
    }

    [Fact]
    public void NoRotationHasNoAxis()
    {
        Assert.Equal(Vector3.Zero, Api().llRot2Axis(Quaternion.Identity));
        Assert.Equal(0f, Api().llRot2Angle(Quaternion.Identity));
    }

    [Fact]
    public void AngleBetweenIgnoresScale()
    {
        // <0,0,0,0.5> and itself: the same rotation, angle 0. The raw dot product (0.25) gave 2.64 rad.
        var half = new Quaternion(0, 0, 0, 0.5f);
        Assert.Equal(0f, Api().llAngleBetween(half, half), 5);
        var a = Quaternion.Identity;
        var b = AxisAngle(new Vector3(1, 0, 0), 1f);
        Assert.Equal(1f, Api().llAngleBetween(new Quaternion(a.X * 3, a.Y * 3, a.Z * 3, a.W * 3), b), 4);
    }

    [Fact]
    public void AngleBetweenAZeroQuaternionIsZero()
    {
        Assert.Equal(0f, Api().llAngleBetween(new Quaternion(0, 0, 0, 0), Quaternion.Identity));
    }
}
