/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Vehicles;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>The model builder of <c>VehicleInWorldzMotorTests</c>, which the parity tests also use.</summary>
public partial class VehicleInWorldzMotorTests
{
    /// <summary>The model given the parameters the controller holds (the preset the port ships for the type, and a test script's
    /// changes), so that the two differ only in the law they apply to them.</summary>
    internal static HalcyonMotorModel ModelOf(VehicleController car) => new HalcyonMotorModel
    {
        Type = VehicleType.Car,
        LinearFrictionTimescale = car.GetVecParam(VehVectorParam.LinearFrictionTimescale),
        AngularFrictionTimescale = car.GetVecParam(VehVectorParam.AngularFrictionTimescale),
        LinearMotorTimescale = car.GetVecParam(VehVectorParam.LinearMotorTimescale),
        AngularMotorTimescale = car.GetVecParam(VehVectorParam.AngularMotorTimescale),
        LinearMotorDecayTimescale = car.GetVecParam(VehVectorParam.LinearMotorDecayTimescale),
        AngularMotorDecayTimescale = car.GetVecParam(VehVectorParam.AngularMotorDecayTimescale),
        VerticalAttractionTimescale = car.GetFloatParam(VehFloatParam.VerticalAttractionTimescale),
        AngularDeflectionTimescale = car.GetFloatParam(VehFloatParam.AngularDeflectionTimescale),
        AngularDeflectionEfficiency = car.GetFloatParam(VehFloatParam.AngularDeflectionEfficiency),
        Buoyancy = car.GetFloatParam(VehFloatParam.Buoyancy),
        LimitMotorUp = true,
    };
}
