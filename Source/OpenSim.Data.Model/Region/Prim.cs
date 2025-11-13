/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Region;

public partial class Prim
{
    public int? CreationDate { get; set; }
    public string Name { get; set; }
    public string Text { get; set; }
    public string Description { get; set; }
    public string SitName { get; set; }
    public string TouchName { get; set; }
    public int? ObjectFlags { get; set; }
    public int? OwnerMask { get; set; }
    public int? NextOwnerMask { get; set; }
    public int? GroupMask { get; set; }
    public int? EveryoneMask { get; set; }
    public int? BaseMask { get; set; }
    public float PositionX { get; set; } = 0.0f;
    public float PositionY { get; set; } = 0.0f;
    public float PositionZ { get; set; } = 0.0f;
    public float GroupPositionX { get; set; } = 0.0f;
    public float GroupPositionY { get; set; } = 0.0f;
    public float GroupPositionZ { get; set; } = 0.0f;
    public float VelocityX { get; set; } = 0.0f;
    public float VelocityY { get; set; } = 0.0f;
    public float VelocityZ { get; set; } = 0.0f;
    public float AngularVelocityX { get; set; } = 0.0f;
    public float AngularVelocityY { get; set; } = 0.0f;
    public float AngularVelocityZ { get; set; } = 0.0f;
    public float AccelerationX { get; set; } = 0.0f;
    public float AccelerationY { get; set; } = 0.0f;
    public float AccelerationZ { get; set; } = 0.0f;
    public float RotationX { get; set; } = 0.0f;
    public float RotationY { get; set; } = 0.0f;
    public float RotationZ { get; set; } = 0.0f;
    public float RotationW { get; set; } = 1.0f;
    public float SitTargetOffsetX { get; set; } = 0.0f;
    public float SitTargetOffsetY { get; set; } = 0.0f;
    public float SitTargetOffsetZ { get; set; } = 0.0f;
    public float SitTargetOrientW { get; set; } = 0.0f;
    public float SitTargetOrientX { get; set; } = 0.0f;
    public float SitTargetOrientY { get; set; } = 0.0f;
    public float SitTargetOrientZ { get; set; } = 0.0f;
    public string Uuid { get; set; } = String.Empty;
    public string RegionUuid { get; set; }
    public string CreatorId { get; set; } = String.Empty;
    public string OwnerId { get; set; }
    public string GroupId { get; set; }
    public string LastOwnerId { get; set; }
    public string SceneGroupId { get; set; }
    public int PayPrice { get; set; }
    public int PayButton1 { get; set; }
    public int PayButton2 { get; set; }
    public int PayButton3 { get; set; }
    public int PayButton4 { get; set; }
    public string LoopedSound { get; set; } = "00000000-0000-0000-0000-000000000000";
    public float LoopedSoundGain { get; set; } = 0.0f;
    public byte[] TextureAnimation { get; set; }
    public float OmegaX { get; set; } = 0.0f;
    public float OmegaY { get; set; } = 0.0f;
    public float OmegaZ { get; set; } = 0.0f;
    public float CameraEyeOffsetX { get; set; } = 0.0f;
    public float CameraEyeOffsetY { get; set; } = 0.0f;
    public float CameraEyeOffsetZ { get; set; } = 0.0f;
    public float CameraAtOffsetX { get; set; } = 0.0f;
    public float CameraAtOffsetY { get; set; } = 0.0f;
    public float CameraAtOffsetZ { get; set; } = 0.0f;
    public sbyte ForceMouselook { get; set; }
    public int ScriptAccessPin { get; set; }
    public sbyte AllowedDrop { get; set; }
    public sbyte DieAtEdge { get; set; }
    public int SalePrice { get; set; } = 10;
    public sbyte SaleType { get; set; }
    public int ColorR { get; set; }
    public int ColorG { get; set; }
    public int ColorB { get; set; }
    public int ColorA { get; set; }
    public byte[] ParticleSystem { get; set; }
    public sbyte ClickAction { get; set; }
    public sbyte Material { get; set; } = 3;
    public string CollisionSound { get; set; } = "00000000-0000-0000-0000-000000000000";
    public double CollisionSoundVolume { get; set; }
    public int LinkNumber { get; set; }
    public sbyte PassTouches { get; set; }
    public string MediaUrl { get; set; }
    public string DynAttrs { get; set; }
    public sbyte PhysicsShapeType { get; set; }
    public float Density { get; set; } = 1000.0f;
    public float GravityModifier { get; set; } = 1.0f;
    public float Friction { get; set; } = 0.6f;
    public float Restitution { get; set; } = 0.5f;
    public byte[] KeyframeMotion { get; set; }
    public float AttachedPosX { get; set; } = 0.0f;
    public float AttachedPosY { get; set; } = 0.0f;
    public float AttachedPosZ { get; set; } = 0.0f;
    public sbyte PassCollisions { get; set; }
    public string Vehicle { get; set; }
    public sbyte RotationAxisLocks { get; set; }
    public string RezzerId { get; set; }
    public string PhysInertia { get; set; }
    public byte[] Sopanims { get; set; }
    public float Standtargetx { get; set; } = 0.0f;
    public float Standtargety { get; set; } = 0.0f;
    public float Standtargetz { get; set; } = 0.0f;
    public float Sitactrange { get; set; } = 0.0f;
    public int Pseudocrc { get; set; } = 0;
    public string LinksetData { get; set; } = String.Empty;
    public sbyte AllowUnsit { get; set; } = 1;
    public sbyte ScriptedSitOnly { get; set; } = 0;
    public string StartStr { get; set; } = String.Empty;
}
