/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Region;

public partial class Regionwindlight
{
    public string RegionId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public float WaterColorR { get; set; } = 4.000000f;
    public float WaterColorG { get; set; } = 38.000000f;
    public float WaterColorB { get; set; } = 64.000000f;
    public float WaterFogDensityExponent { get; set; } = 4.0000000f;
    public float UnderwaterFogModifier { get; set; } = 0.2500000f;
    public float ReflectionWaveletScale1 { get; set; } = 2.0000000f;
    public float ReflectionWaveletScale2 { get; set; } = 2.0000000f;
    public float ReflectionWaveletScale3 { get; set; } = 2.0000000f;
    public float FresnelScale { get; set; } = 0.40000001f;
    public float FresnelOffset { get; set; } = 0.50000000f;
    public float RefractScaleAbove { get; set; } = 0.03000000f;
    public float RefractScaleBelow { get; set; } = 0.20000000f;
    public float BlurMultiplier { get; set; } = 0.04000000f;
    public float BigWaveDirectionX { get; set; } = 1.04999995f;
    public float BigWaveDirectionY { get; set; } = -0.41999999f;
    public float LittleWaveDirectionX { get; set; } = 1.11000001f;
    public float LittleWaveDirectionY { get; set; } = -1.15999997f;
    public string NormalMapTexture { get; set; } = "822ded49-9a6c-f61c-cb89-6df54f42cdf4";
    public float HorizonR { get; set; } = 0.25000000f;
    public float HorizonG { get; set; } = 0.25000000f;
    public float HorizonB { get; set; } = 0.31999999f;
    public float HorizonI { get; set; } = 0.31999999f;
    public float HazeHorizon { get; set; } = 0.19000000f;
    public float BlueDensityR { get; set; } = 0.12000000f;
    public float BlueDensityG { get; set; } = 0.22000000f;
    public float BlueDensityB { get; set; } = 0.38000000f;
    public float BlueDensityI { get; set; } = 0.38000000f;
    public float HazeDensity { get; set; } = 0.69999999f;
    public float DensityMultiplier { get; set; } = 0.18000001f;
    public float DistanceMultiplier { get; set; } = 0.800000f;
    public uint MaxAltitude { get; set; } = 1605;
    public float SunMoonColorR { get; set; } = 0.23999999f;
    public float SunMoonColorG { get; set; } = 0.25999999f;
    public float SunMoonColorB { get; set; } = 0.30000001f;
    public float SunMoonColorI { get; set; } = 0.30000001f;
    public float SunMoonPosition { get; set; } = 0.31700000f;
    public float AmbientR { get; set; } = 0.349999f;
    public float AmbientG { get; set; } = 0.349999f;
    public float AmbientB { get; set; } = 0.349999f;
    public float AmbientI { get; set; } = 0.349999f;
    public float EastAngle { get; set; }
    public float SunGlowFocus { get; set; } = 0.10000000f;
    public float SunGlowSize { get; set; } = 1.75000000f;
    public float SceneGamma { get; set; } = 1.0000000f;
    public float StarBrightness { get; set; }
    public float CloudColorR { get; set; } = 0.41000000f;
    public float CloudColorG { get; set; } = 0.41000000f;
    public float CloudColorB { get; set; } = 0.41000000f;
    public float CloudColorI { get; set; } = 0.41000000f;
    public float CloudX { get; set; } = 1.00000000f;
    public float CloudY { get; set; } = 0.52999997f;
    public float CloudDensity { get; set; } = 1.00000000f;
    public float CloudCoverage { get; set; } = 0.27000001f;
    public float CloudScale { get; set; } = 0.41999999f;
    public float CloudDetailX { get; set; } = 1.00000000f;
    public float CloudDetailY { get; set; } = 0.52999997f;
    public float CloudDetailDensity { get; set; } = 0.12000000f;
    public float CloudScrollX { get; set; } = 0.2000000f;
    public byte CloudScrollXLock { get; set; }
    public float CloudScrollY { get; set; } = 0.0100000f;
    public byte CloudScrollYLock { get; set; }
    public byte DrawClassicClouds { get; set; } = 1;
}
