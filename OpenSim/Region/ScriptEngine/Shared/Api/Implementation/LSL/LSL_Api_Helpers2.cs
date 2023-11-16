/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Animation;
using OpenSim.Region.Framework.Scenes.Scripting;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;
using PermissionMask = OpenSim.Framework.PermissionMask;
using PrimType = OpenSim.Region.Framework.Scenes.PrimType;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        public void CreateLink(string target, int parent)
        {
            if (!UUID.TryParse(target, out var targetID) || targetID.IsZero())
                return;

            var hostgroup = m_host.ParentGroup;
            if (hostgroup.AttachmentPoint != 0)
                return; // Fail silently if attached
            if ((hostgroup.RootPart.OwnerMask & (uint)PermissionMask.Modify) == 0)
                return;

            var targetPart = World.GetSceneObjectPart(targetID);
            if (targetPart == null)
                return;

            var targetgrp = targetPart.ParentGroup;

            if (targetgrp == null || targetgrp.OwnerID.NotEqual(hostgroup.OwnerID))
                return;

            if (targetgrp.AttachmentPoint != 0)
                return; // Fail silently if attached
            if ((targetgrp.RootPart.OwnerMask & (uint)PermissionMask.Modify) == 0)
                return;

            SceneObjectGroup parentPrim = null, childPrim = null;

            if (parent != 0)
            {
                parentPrim = hostgroup;
                childPrim = targetgrp;
            }
            else
            {
                parentPrim = targetgrp;
                childPrim = hostgroup;
            }

            // Required for linking
            childPrim.RootPart.ClearUpdateSchedule();
            parentPrim.LinkToGroup(childPrim, true);

            parentPrim.TriggerScriptChangedEvent(Changed.LINK);
            parentPrim.RootPart.CreateSelected = false;
            parentPrim.HasGroupChanged = true;
            parentPrim.ScheduleGroupForFullUpdate();

            IClientAPI client = null;
            var sp = World.GetScenePresence(m_host.OwnerID);
            if (sp != null)
                client = sp.ControllingClient;

            if (client != null)
                parentPrim.SendPropertiesToClient(client);

            ScriptSleep(m_sleepMsOnCreateLink);
        }

        public void BreakLink(int linknum)
        {
            if (linknum < ScriptBaseClass.LINK_THIS)
                return;

            var parentSOG = m_host.ParentGroup;

            if (parentSOG.AttachmentPoint != 0)
                return; // Fail silently if attached

            if ((parentSOG.RootPart.OwnerMask & (uint)PermissionMask.Modify) == 0)
                return;

            SceneObjectPart childPrim = null;

            switch (linknum)
            {
                case ScriptBaseClass.LINK_ROOT:
                case ScriptBaseClass.LINK_SET:
                case ScriptBaseClass.LINK_ALL_OTHERS:
                case ScriptBaseClass.LINK_ALL_CHILDREN:
                    break;
                case ScriptBaseClass.LINK_THIS: // not as spec
                    childPrim = m_host;
                    break;
                default:
                    childPrim = parentSOG.GetLinkNumPart(linknum);
                    break;
            }

            if (linknum == ScriptBaseClass.LINK_ROOT)
            {
                var avs = parentSOG.GetSittingAvatars();
                foreach (var av in avs)
                    av.StandUp();

                var parts = new List<SceneObjectPart>(parentSOG.Parts);
                parts.Remove(parentSOG.RootPart);
                if (parts.Count > 0)
                    foreach (var part in parts)
                        parentSOG.DelinkFromGroup(part.LocalId, true);

                parentSOG.HasGroupChanged = true;
                parentSOG.ScheduleGroupForFullUpdate();
                parentSOG.TriggerScriptChangedEvent(Changed.LINK);

                if (parts.Count > 0)
                {
                    var newRoot = parts[0];
                    parts.Remove(newRoot);

                    foreach (var part in parts)
                    {
                        part.ClearUpdateSchedule();
                        newRoot.ParentGroup.LinkToGroup(part.ParentGroup);
                    }

                    newRoot.ParentGroup.HasGroupChanged = true;
                    newRoot.ParentGroup.ScheduleGroupForFullUpdate();
                }
            }
            else
            {
                if (childPrim == null)
                    return;

                var avs = parentSOG.GetSittingAvatars();
                foreach (var av in avs)
                    av.StandUp();

                parentSOG.DelinkFromGroup(childPrim.LocalId, true);
            }
        }

        public void BreakAllLinks()
        {
            var parentPrim = m_host.ParentGroup;
            if (parentPrim.AttachmentPoint != 0)
                return; // Fail silently if attached

            var parts = new List<SceneObjectPart>(parentPrim.Parts);
            parts.Remove(parentPrim.RootPart);

            foreach (var part in parts)
            {
                parentPrim.DelinkFromGroup(part.LocalId, true);
                parentPrim.TriggerScriptChangedEvent(Changed.LINK);
            }

            parentPrim.HasGroupChanged = true;
            parentPrim.ScheduleGroupForFullUpdate();
        }

        private void DoLLTeleport(ScenePresence sp, string destination, Vector3 targetPos, Vector3 targetLookAt)
        {
            var assetID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, destination);

            // The destination is not an asset ID and also doesn't name a landmark.
            // Use it as a sim name
            if (assetID.IsZero())
            {
                if (string.IsNullOrEmpty(destination))
                    World.RequestTeleportLocation(sp.ControllingClient, m_regionName, targetPos, targetLookAt,
                        (uint)TeleportFlags.ViaLocation);
                else
                    World.RequestTeleportLocation(sp.ControllingClient, destination, targetPos, targetLookAt,
                        (uint)TeleportFlags.ViaLocation);
                return;
            }

            var lma = World.AssetService.Get(assetID.ToString());
            if (lma == null || lma.Data == null || lma.Data.Length == 0)
                return;

            if (lma.Type != (sbyte)AssetType.Landmark)
                return;

            var lm = new AssetLandmark(lma);

            World.RequestTeleportLandmark(sp.ControllingClient, lm, targetLookAt);
        }

        protected int GetNumberOfSides(SceneObjectPart part)
        {
            return part.GetNumberOfSides();
        }

        protected LSL_Vector GetTextureOffset(SceneObjectPart part, int face)
        {
            var tex = part.Shape.Textures;
            var offset = new LSL_Vector();
            if (face == ScriptBaseClass.ALL_SIDES) face = 0;
            if (face >= 0 && face < GetNumberOfSides(part))
            {
                offset.x = tex.GetFace((uint)face).OffsetU;
                offset.y = tex.GetFace((uint)face).OffsetV;
                offset.z = 0.0;
                return offset;
            }

            return offset;
        }

        protected LSL_Float GetTextureRot(SceneObjectPart part, int face)
        {
            var tex = part.Shape.Textures;
            if (face == -1) face = 0;
            if (face >= 0 && face < GetNumberOfSides(part))
                return tex.GetFace((uint)face).Rotation;
            return 0.0;
        }

        private void SetTextureAnim(SceneObjectPart part, int mode, int face, int sizex, int sizey, double start,
            double length, double rate)
        {
            //ALL_SIDES
            if (face == ScriptBaseClass.ALL_SIDES)
                face = 255;

            var pTexAnim = new Primitive.TextureAnimation
            {
                Flags = (Primitive.TextureAnimMode)mode,
                Face = (uint)face,
                Length = (float)length,
                Rate = (float)rate,
                SizeX = (uint)sizex,
                SizeY = (uint)sizey,
                Start = (float)start
            };

            part.AddTextureAnimation(pTexAnim);
            part.SendFullUpdateToAllClients();
            part.ParentGroup.HasGroupChanged = true;
        }

        internal Primitive.ParticleSystem.ParticleDataFlags ConvertUINTtoFlags(uint flags)
        {
            var returnval = Primitive.ParticleSystem.ParticleDataFlags.None;

            return returnval;
        }

        protected Primitive.ParticleSystem getNewParticleSystemWithSLDefaultValues()
        {
            var ps = new Primitive.ParticleSystem
            {
                PartStartColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f),
                PartEndColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f),
                PartStartScaleX = 1.0f,
                PartStartScaleY = 1.0f,
                PartEndScaleX = 1.0f,
                PartEndScaleY = 1.0f,
                BurstSpeedMin = 1.0f,
                BurstSpeedMax = 1.0f,
                BurstRate = 0.1f,
                PartMaxAge = 10.0f,
                BurstPartCount = 1,
                BlendFuncSource = ScriptBaseClass.PSYS_PART_BF_SOURCE_ALPHA,
                BlendFuncDest = ScriptBaseClass.PSYS_PART_BF_ONE_MINUS_SOURCE_ALPHA,
                PartStartGlow = 0.0f,
                PartEndGlow = 0.0f
            };

            return ps;
        }

        public void SetParticleSystem(SceneObjectPart part, LSL_List rules, string originFunc, bool expire = false)
        {
            if (rules.Length == 0)
            {
                part.RemoveParticleSystem();
                part.ParentGroup.HasGroupChanged = true;
            }
            else
            {
                var prules = getNewParticleSystemWithSLDefaultValues();
                var tempv = new LSL_Vector();

                float tempf = 0;
                var tmpi = 0;

                for (var i = 0; i < rules.Length; i += 2)
                {
                    int psystype;
                    try
                    {
                        psystype = rules.GetLSLIntegerItem(i);
                    }
                    catch (InvalidCastException)
                    {
                        Error(originFunc,
                            string.Format(
                                "Error running particle system params index #{0}: particle system parameter type must be integer",
                                i));
                        return;
                    }

                    switch (psystype)
                    {
                        case ScriptBaseClass.PSYS_PART_FLAGS:
                            try
                            {
                                prules.PartDataFlags =
                                    (Primitive.ParticleSystem.ParticleDataFlags)(uint)rules.GetLSLIntegerItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_FLAGS: arg #{0} - parameter 1 must be integer",
                                        i + 1));
                                return;
                            }

                            break;

                        case ScriptBaseClass.PSYS_PART_START_COLOR:
                            try
                            {
                                tempv = rules.GetVector3Item(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_START_COLOR: arg #{0} - parameter 1 must be vector",
                                        i + 1));
                                return;
                            }

                            prules.PartStartColor.R = (float)tempv.x;
                            prules.PartStartColor.G = (float)tempv.y;
                            prules.PartStartColor.B = (float)tempv.z;
                            break;

                        case ScriptBaseClass.PSYS_PART_START_ALPHA:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_START_ALPHA: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.PartStartColor.A = tempf;
                            break;

                        case ScriptBaseClass.PSYS_PART_END_COLOR:
                            try
                            {
                                tempv = rules.GetVector3Item(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_END_COLOR: arg #{0} - parameter 1 must be vector",
                                        i + 1));
                                return;
                            }

                            prules.PartEndColor.R = (float)tempv.x;
                            prules.PartEndColor.G = (float)tempv.y;
                            prules.PartEndColor.B = (float)tempv.z;
                            break;

                        case ScriptBaseClass.PSYS_PART_END_ALPHA:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_END_ALPHA: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.PartEndColor.A = tempf;
                            break;

                        case ScriptBaseClass.PSYS_PART_START_SCALE:
                            try
                            {
                                tempv = rules.GetVector3Item(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_START_SCALE: arg #{0} - parameter 1 must be vector",
                                        i + 1));
                                return;
                            }

                            prules.PartStartScaleX = validParticleScale((float)tempv.x);
                            prules.PartStartScaleY = validParticleScale((float)tempv.y);
                            break;

                        case ScriptBaseClass.PSYS_PART_END_SCALE:
                            try
                            {
                                tempv = rules.GetVector3Item(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_END_SCALE: arg #{0} - parameter 1 must be vector",
                                        i + 1));
                                return;
                            }

                            prules.PartEndScaleX = validParticleScale((float)tempv.x);
                            prules.PartEndScaleY = validParticleScale((float)tempv.y);
                            break;

                        case ScriptBaseClass.PSYS_PART_MAX_AGE:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_MAX_AGE: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.PartMaxAge = tempf;
                            break;

                        case ScriptBaseClass.PSYS_SRC_ACCEL:
                            try
                            {
                                tempv = rules.GetVector3Item(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_ACCEL: arg #{0} - parameter 1 must be vector",
                                        i + 1));
                                return;
                            }

                            prules.PartAcceleration.X = (float)tempv.x;
                            prules.PartAcceleration.Y = (float)tempv.y;
                            prules.PartAcceleration.Z = (float)tempv.z;
                            break;

                        case ScriptBaseClass.PSYS_SRC_PATTERN:
                            try
                            {
                                tmpi = rules.GetLSLIntegerItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_PATTERN: arg #{0} - parameter 1 must be integer",
                                        i + 1));
                                return;
                            }

                            prules.Pattern = (Primitive.ParticleSystem.SourcePattern)tmpi;
                            break;

                        // PSYS_SRC_INNERANGLE and PSYS_SRC_ANGLE_BEGIN use the same variables. The
                        // PSYS_SRC_OUTERANGLE and PSYS_SRC_ANGLE_END also use the same variable. The
                        // client tells the difference between the two by looking at the 0x02 bit in
                        // the PartFlags variable.
                        case ScriptBaseClass.PSYS_SRC_INNERANGLE:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_INNERANGLE: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.InnerAngle = tempf;
                            prules.PartFlags &= 0xFFFFFFFD; // Make sure new angle format is off.
                            break;

                        case ScriptBaseClass.PSYS_SRC_OUTERANGLE:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_OUTERANGLE: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.OuterAngle = tempf;
                            prules.PartFlags &= 0xFFFFFFFD; // Make sure new angle format is off.
                            break;

                        case ScriptBaseClass.PSYS_PART_BLEND_FUNC_SOURCE:
                            try
                            {
                                tmpi = rules.GetLSLIntegerItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_BLEND_FUNC_SOURCE: arg #{0} - parameter 1 must be integer",
                                        i + 1));
                                return;
                            }

                            prules.BlendFuncSource = (byte)tmpi;
                            break;

                        case ScriptBaseClass.PSYS_PART_BLEND_FUNC_DEST:
                            try
                            {
                                tmpi = rules.GetLSLIntegerItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_BLEND_FUNC_DEST: arg #{0} - parameter 1 must be integer",
                                        i + 1));
                                return;
                            }

                            prules.BlendFuncDest = (byte)tmpi;
                            break;

                        case ScriptBaseClass.PSYS_PART_START_GLOW:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_START_GLOW: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.PartStartGlow = tempf;
                            break;

                        case ScriptBaseClass.PSYS_PART_END_GLOW:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_PART_END_GLOW: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.PartEndGlow = tempf;
                            break;

                        case ScriptBaseClass.PSYS_SRC_TEXTURE:
                            try
                            {
                                prules.Texture =
                                    ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, rules.GetStrictStringItem(i + 1));
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_TEXTURE: arg #{0} - parameter 1 must be string or key",
                                        i + 1));
                                return;
                            }

                            break;

                        case ScriptBaseClass.PSYS_SRC_BURST_RATE:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_BURST_RATE: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.BurstRate = tempf;
                            break;

                        case ScriptBaseClass.PSYS_SRC_BURST_PART_COUNT:
                            try
                            {
                                prules.BurstPartCount = (byte)(int)rules.GetLSLIntegerItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_BURST_PART_COUNT: arg #{0} - parameter 1 must be integer",
                                        i + 1));
                                return;
                            }

                            break;

                        case ScriptBaseClass.PSYS_SRC_BURST_RADIUS:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_BURST_RADIUS: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.BurstRadius = tempf;
                            break;

                        case ScriptBaseClass.PSYS_SRC_BURST_SPEED_MIN:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_BURST_SPEED_MIN: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.BurstSpeedMin = tempf;
                            break;

                        case ScriptBaseClass.PSYS_SRC_BURST_SPEED_MAX:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_BURST_SPEED_MAX: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.BurstSpeedMax = tempf;
                            break;

                        case ScriptBaseClass.PSYS_SRC_MAX_AGE:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_MAX_AGE: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.MaxAge = tempf;
                            break;

                        case ScriptBaseClass.PSYS_SRC_TARGET_KEY:
                            if (UUID.TryParse(rules.Data[i + 1].ToString(), out var key))
                                prules.Target = key;
                            else
                                prules.Target = part.UUID;
                            break;

                        case ScriptBaseClass.PSYS_SRC_OMEGA:
                            // AL: This is an assumption, since it is the only thing that would match.
                            try
                            {
                                tempv = rules.GetVector3Item(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_OMEGA: arg #{0} - parameter 1 must be vector",
                                        i + 1));
                                return;
                            }

                            prules.AngularVelocity.X = (float)tempv.x;
                            prules.AngularVelocity.Y = (float)tempv.y;
                            prules.AngularVelocity.Z = (float)tempv.z;
                            break;

                        case ScriptBaseClass.PSYS_SRC_ANGLE_BEGIN:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_ANGLE_BEGIN: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.InnerAngle = tempf;
                            prules.PartFlags |= 0x02; // Set new angle format.
                            break;

                        case ScriptBaseClass.PSYS_SRC_ANGLE_END:
                            try
                            {
                                tempf = (float)rules.GetLSLFloatItem(i + 1);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule PSYS_SRC_ANGLE_END: arg #{0} - parameter 1 must be float",
                                        i + 1));
                                return;
                            }

                            prules.OuterAngle = tempf;
                            prules.PartFlags |= 0x02; // Set new angle format.
                            break;
                    }
                }

                prules.CRC = 1;

                part.AddNewParticleSystem(prules, expire);
                if (!expire || prules.MaxAge != 0 || prules.MaxAge > 300)
                    part.ParentGroup.HasGroupChanged = true;
            }

            part.SendFullUpdateToAllClients();
        }

        private float validParticleScale(float value)
        {
            if (value > 7.96f) return 7.96f;
            return value;
        }

        protected void SitTarget(SceneObjectPart part, LSL_Vector offset, LSL_Rotation rot)
        {
            // LSL quaternions can normalize to 0, normal Quaternions can't.
            if (rot.s == 0 && rot.x == 0 && rot.y == 0 && rot.z == 0)
                rot.s = 1; // ZERO_ROTATION = 0,0,0,1

            part.SitTargetPosition = offset;
            part.SitTargetOrientation = rot;
            part.ParentGroup.HasGroupChanged = true;
        }

        protected ObjectShapePacket.ObjectDataBlock SetPrimitiveBlockShapeParams(SceneObjectPart part, int holeshape,
            LSL_Vector cut, float hollow, LSL_Vector twist, byte profileshape, byte pathcurve)
        {
            float tempFloat; // Use in float expressions below to avoid byte cast precision issues.
            var shapeBlock = new ObjectShapePacket.ObjectDataBlock();
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return shapeBlock;

            if (holeshape != ScriptBaseClass.PRIM_HOLE_DEFAULT &&
                holeshape != ScriptBaseClass.PRIM_HOLE_CIRCLE &&
                holeshape != ScriptBaseClass.PRIM_HOLE_SQUARE &&
                holeshape != ScriptBaseClass.PRIM_HOLE_TRIANGLE)
                holeshape = ScriptBaseClass.PRIM_HOLE_DEFAULT;
            shapeBlock.PathCurve = pathcurve;
            shapeBlock.ProfileCurve = (byte)holeshape; // Set the hole shape.
            shapeBlock.ProfileCurve += profileshape; // Add in the profile shape.
            if (cut.x < 0f)
                cut.x = 0f;
            else if (cut.x > 1f) cut.x = 1f;
            if (cut.y < 0f)
                cut.y = 0f;
            else if (cut.y > 1f) cut.y = 1f;
            if (cut.y - cut.x < 0.02f)
            {
                cut.x = cut.y - 0.02f;
                if (cut.x < 0.0f)
                {
                    cut.x = 0.0f;
                    cut.y = 0.02f;
                }
            }

            shapeBlock.ProfileBegin = (ushort)(50000 * cut.x);
            shapeBlock.ProfileEnd = (ushort)(50000 * (1 - cut.y));
            if (hollow < 0f) hollow = 0f;
            // If the prim is a Cylinder, Prism, Sphere, Torus or Ring (or not a
            // Box or Tube) and the hole shape is a square, hollow is limited to
            // a max of 70%. The viewer performs its own check on this value but
            // we need to do it here also so llGetPrimitiveParams can have access
            // to the correct value.
            if (profileshape != (byte)ProfileCurve.Square &&
                holeshape == ScriptBaseClass.PRIM_HOLE_SQUARE)
            {
                if (hollow > 0.70f) hollow = 0.70f;
            }
            // Otherwise, hollow is limited to 99%.
            else
            {
                if (hollow > 0.99f) hollow = 0.99f;
            }

            shapeBlock.ProfileHollow = (ushort)(50000 * hollow);
            if (twist.x < -1.0f)
                twist.x = -1.0f;
            else if (twist.x > 1.0f) twist.x = 1.0f;
            if (twist.y < -1.0f)
                twist.y = -1.0f;
            else if (twist.y > 1.0f) twist.y = 1.0f;
            tempFloat = 100.0f * (float)twist.x;
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathTwistBegin = (sbyte)tempFloat;

            tempFloat = 100.0f * (float)twist.y;
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathTwist = (sbyte)tempFloat;

            shapeBlock.ObjectLocalID = part.LocalId;

            part.Shape.SculptEntry = false;
            return shapeBlock;
        }

        // Prim type box, cylinder and prism.
        protected void SetPrimitiveShapeParams(SceneObjectPart part, int holeshape, LSL_Vector cut, float hollow,
            LSL_Vector twist, LSL_Vector taper_b, LSL_Vector topshear, byte profileshape, byte pathcurve)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            float tempFloat; // Use in float expressions below to avoid byte cast precision issues.
            ObjectShapePacket.ObjectDataBlock shapeBlock;

            shapeBlock = SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist, profileshape, pathcurve);

            if (taper_b.x < 0f)
                taper_b.x = 0f;
            else if (taper_b.x > 2f) taper_b.x = 2f;
            if (taper_b.y < 0f)
                taper_b.y = 0f;
            else if (taper_b.y > 2f) taper_b.y = 2f;
            tempFloat = 100.0f * (2.0f - (float)taper_b.x);
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathScaleX = (byte)tempFloat;

            tempFloat = 100.0f * (2.0f - (float)taper_b.y);
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathScaleY = (byte)tempFloat;

            if (topshear.x < -0.5f)
                topshear.x = -0.5f;
            else if (topshear.x > 0.5f) topshear.x = 0.5f;
            if (topshear.y < -0.5f)
                topshear.y = -0.5f;
            else if (topshear.y > 0.5f) topshear.y = 0.5f;
            tempFloat = 100.0f * (float)topshear.x;
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathShearX = (byte)tempFloat;

            tempFloat = 100.0f * (float)topshear.y;
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathShearY = (byte)tempFloat;

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        // Prim type sphere.
        protected void SetPrimitiveShapeParams(SceneObjectPart part, int holeshape, LSL_Vector cut, float hollow,
            LSL_Vector twist, LSL_Vector dimple, byte profileshape, byte pathcurve)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            ObjectShapePacket.ObjectDataBlock shapeBlock;

            shapeBlock = SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist, profileshape, pathcurve);

            // profile/path swapped for a sphere
            shapeBlock.PathBegin = shapeBlock.ProfileBegin;
            shapeBlock.PathEnd = shapeBlock.ProfileEnd;

            shapeBlock.PathScaleX = 100;
            shapeBlock.PathScaleY = 100;

            if (dimple.x < 0f)
                dimple.x = 0f;
            else if (dimple.x > 1f) dimple.x = 1f;
            if (dimple.y < 0f)
                dimple.y = 0f;
            else if (dimple.y > 1f) dimple.y = 1f;
            if (dimple.y - dimple.x < 0.02f)
            {
                dimple.x = dimple.y - 0.02f;
                if (dimple.x < 0.0f)
                {
                    dimple.x = 0.0f;
                    dimple.y = 0.02f;
                }
            }

            shapeBlock.ProfileBegin = (ushort)(50000 * dimple.x);
            shapeBlock.ProfileEnd = (ushort)(50000 * (1 - dimple.y));

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        // Prim type torus, tube and ring.
        protected void SetPrimitiveShapeParams(SceneObjectPart part, int holeshape, LSL_Vector cut, float hollow,
            LSL_Vector twist, LSL_Vector holesize, LSL_Vector topshear, LSL_Vector profilecut, LSL_Vector taper_a,
            float revolutions, float radiusoffset, float skew, byte profileshape, byte pathcurve)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            float tempFloat; // Use in float expressions below to avoid byte cast precision issues.
            ObjectShapePacket.ObjectDataBlock shapeBlock;

            shapeBlock = SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist, profileshape, pathcurve);

            // profile/path swapped for a torrus, tube, ring
            shapeBlock.PathBegin = shapeBlock.ProfileBegin;
            shapeBlock.PathEnd = shapeBlock.ProfileEnd;

            if (holesize.x < 0.01f)
                holesize.x = 0.01f;
            else if (holesize.x > 1f) holesize.x = 1f;
            tempFloat = 100.0f * (2.0f - (float)holesize.x) + 0.5f;
            shapeBlock.PathScaleX = (byte)tempFloat;

            if (holesize.y < 0.01f)
                holesize.y = 0.01f;
            else if (holesize.y > 0.5f) holesize.y = 0.5f;
            tempFloat = 100.0f * (2.0f - (float)holesize.y) + 0.5f;
            shapeBlock.PathScaleY = (byte)tempFloat;

            if (topshear.x < -0.5f)
                topshear.x = -0.5f;
            else if (topshear.x > 0.5f) topshear.x = 0.5f;
            tempFloat = (float)(100.0d * topshear.x);
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathShearX = (byte)tempFloat;

            if (topshear.y < -0.5f)
                topshear.y = -0.5f;
            else if (topshear.y > 0.5f) topshear.y = 0.5f;
            tempFloat = (float)(100.0d * topshear.y);
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathShearY = (byte)tempFloat;

            if (profilecut.x < 0f)
                profilecut.x = 0f;
            else if (profilecut.x > 1f) profilecut.x = 1f;
            if (profilecut.y < 0f)
                profilecut.y = 0f;
            else if (profilecut.y > 1f) profilecut.y = 1f;
            if (profilecut.y - profilecut.x < 0.02f)
            {
                profilecut.x = profilecut.y - 0.02f;
                if (profilecut.x < 0.0f)
                {
                    profilecut.x = 0.0f;
                    profilecut.y = 0.02f;
                }
            }

            shapeBlock.ProfileBegin = (ushort)(50000 * profilecut.x);
            shapeBlock.ProfileEnd = (ushort)(50000 * (1 - profilecut.y));
            if (taper_a.x < -1f) taper_a.x = -1f;
            if (taper_a.x > 1f) taper_a.x = 1f;
            tempFloat = 100.0f * (float)taper_a.x;
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathTaperX = (sbyte)tempFloat;

            if (taper_a.y < -1f)
                taper_a.y = -1f;
            else if (taper_a.y > 1f) taper_a.y = 1f;
            tempFloat = 100.0f * (float)taper_a.y;
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathTaperY = (sbyte)tempFloat;

            if (revolutions < 1f) revolutions = 1f;
            if (revolutions > 4f) revolutions = 4f;
            tempFloat = 66.66667f * (revolutions - 1.0f) + 0.5f;
            shapeBlock.PathRevolutions = (byte)tempFloat;
            // limits on radiusoffset depend on revolutions and hole size (how?) seems like the maximum range is 0 to 1
            if (radiusoffset < 0f) radiusoffset = 0f;
            if (radiusoffset > 1f) radiusoffset = 1f;
            tempFloat = 100.0f * radiusoffset + 0.5f;
            shapeBlock.PathRadiusOffset = (sbyte)tempFloat;
            if (skew < -0.95f) skew = -0.95f;
            if (skew > 0.95f) skew = 0.95f;
            tempFloat = 100.0f * skew;
            if (tempFloat >= 0)
                tempFloat += 0.5f;
            else
                tempFloat -= 0.5f;
            shapeBlock.PathSkew = (sbyte)tempFloat;

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        // Prim type sculpt.
        protected void SetPrimitiveShapeParams(SceneObjectPart part, string map, int type, byte pathcurve)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var shapeBlock = new ObjectShapePacket.ObjectDataBlock();

            if (!UUID.TryParse(map, out var sculptId))
                sculptId = ScriptUtils.GetAssetIdFromItemName(m_host, map, (int)AssetType.Texture);

            if (sculptId.IsZero())
                return;

            shapeBlock.PathCurve = pathcurve;
            shapeBlock.ObjectLocalID = part.LocalId;
            shapeBlock.PathScaleX = 100;
            shapeBlock.PathScaleY = 150;

            var flag = type & (ScriptBaseClass.PRIM_SCULPT_FLAG_INVERT | ScriptBaseClass.PRIM_SCULPT_FLAG_MIRROR);

            if (type != (ScriptBaseClass.PRIM_SCULPT_TYPE_CYLINDER | flag) &&
                type != (ScriptBaseClass.PRIM_SCULPT_TYPE_PLANE | flag) &&
                type != (ScriptBaseClass.PRIM_SCULPT_TYPE_SPHERE | flag) &&
                type != (ScriptBaseClass.PRIM_SCULPT_TYPE_TORUS | flag))
                // default
                type = type | ScriptBaseClass.PRIM_SCULPT_TYPE_SPHERE;

            part.Shape.SetSculptProperties((byte)type, sculptId);
            part.Shape.SculptEntry = true;
            part.UpdateShape(shapeBlock);
        }

        private void SetLinkPrimParams(int linknumber, LSL_List rules, string originFunc)
        {
            var parts = new List<object>();
            var prims = GetLinkParts(linknumber);
            var avatars = GetLinkAvatars(linknumber);
            foreach (var p in prims)
                parts.Add(p);
            foreach (var p in avatars)
                parts.Add(p);

            var remaining = new LSL_List();
            uint rulesParsed = 0;

            if (parts.Count > 0)
            {
                foreach (var part in parts)
                    if (part is SceneObjectPart)
                        remaining = SetPrimParams((SceneObjectPart)part, rules, originFunc, ref rulesParsed);
                    else
                        remaining = SetPrimParams((ScenePresence)part, rules, originFunc, ref rulesParsed);

                while (remaining.Length > 2)
                {
                    linknumber = remaining.GetLSLIntegerItem(0);
                    rules = remaining.GetSublist(1, -1);
                    parts.Clear();
                    prims = GetLinkParts(linknumber);
                    avatars = GetLinkAvatars(linknumber);
                    foreach (var p in prims)
                        parts.Add(p);
                    foreach (var p in avatars)
                        parts.Add(p);

                    remaining = new LSL_List();
                    foreach (var part in parts)
                        if (part is SceneObjectPart)
                            remaining = SetPrimParams((SceneObjectPart)part, rules, originFunc, ref rulesParsed);
                        else
                            remaining = SetPrimParams((ScenePresence)part, rules, originFunc, ref rulesParsed);
                }
            }
        }

        private void SetPhysicsMaterial(SceneObjectPart part, int material_bits,
            float material_density, float material_friction,
            float material_restitution, float material_gravity_modifier)
        {
            var physdata = new ExtraPhysicsData
            {
                PhysShapeType = (PhysShapeType)part.PhysicsShapeType,
                Density = part.Density,
                Friction = part.Friction,
                Bounce = part.Restitution,
                GravitationModifier = part.GravityModifier
            };

            if ((material_bits & ScriptBaseClass.DENSITY) != 0)
                physdata.Density = material_density;
            if ((material_bits & ScriptBaseClass.FRICTION) != 0)
                physdata.Friction = material_friction;
            if ((material_bits & ScriptBaseClass.RESTITUTION) != 0)
                physdata.Bounce = material_restitution;
            if ((material_bits & ScriptBaseClass.GRAVITY_MULTIPLIER) != 0)
                physdata.GravitationModifier = material_gravity_modifier;

            part.UpdateExtraPhysics(physdata);
        }

        // vector up using libomv (c&p from sop )
        // vector up rotated by r
        private Vector3 Zrot(Quaternion r)
        {
            double x, y, z, m;

            m = r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W;
            if (Math.Abs(1.0 - m) > 0.000001)
            {
                m = 1.0 / Math.Sqrt(m);
                r.X *= (float)m;
                r.Y *= (float)m;
                r.Z *= (float)m;
                r.W *= (float)m;
            }

            x = 2 * (r.X * r.Z + r.Y * r.W);
            y = 2 * (-r.X * r.W + r.Y * r.Z);
            z = -r.X * r.X - r.Y * r.Y + r.Z * r.Z + r.W * r.W;

            return new Vector3((float)x, (float)y, (float)z);
        }

        protected LSL_List SetPrimParams(SceneObjectPart part, LSL_List rules, string originFunc, ref uint rulesParsed)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return new LSL_List();

            var idx = 0;
            var idxStart = 0;

            var parentgrp = part.ParentGroup;

            var positionChanged = false;
            var materialChanged = false;
            var currentPosition = GetPartLocalPos(part);

            try
            {
                while (idx < rules.Length)
                {
                    ++rulesParsed;
                    int code = rules.GetLSLIntegerItem(idx++);

                    var remain = rules.Length - idx;
                    idxStart = idx;

                    int face;
                    LSL_Vector v;

                    switch (code)
                    {
                        case ScriptBaseClass.PRIM_POSITION:
                        case ScriptBaseClass.PRIM_POS_LOCAL:
                            if (remain < 1)
                                return new LSL_List();

                            try
                            {
                                v = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                if (code == ScriptBaseClass.PRIM_POSITION)
                                    Error(originFunc,
                                        string.Format(
                                            "Error running rule #{0} -> PRIM_POSITION: arg #{1} - parameter 1 must be vector",
                                            rulesParsed, idx - idxStart - 1));
                                else
                                    Error(originFunc,
                                        string.Format(
                                            "Error running rule #{0} -> PRIM_POS_LOCAL: arg #{1} - parameter 1 must be vector",
                                            rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            if (part.IsRoot && !part.ParentGroup.IsAttachment)
                                currentPosition = GetSetPosTarget(part, v, currentPosition, true);
                            else
                                currentPosition = GetSetPosTarget(part, v, currentPosition, false);
                            positionChanged = true;

                            break;
                        case ScriptBaseClass.PRIM_SIZE:
                            if (remain < 1)
                                return new LSL_List();

                            v = rules.GetVector3Item(idx++);
                            SetScale(part, v);

                            break;
                        case ScriptBaseClass.PRIM_ROTATION:
                            if (remain < 1)
                                return new LSL_List();
                            LSL_Rotation q;
                            try
                            {
                                q = rules.GetQuaternionItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ROTATION: arg #{1} - parameter 1 must be rotation",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            // try to let this work as in SL...
                            if (part.ParentID == 0 || (part.ParentGroup != null && part == part.ParentGroup.RootPart))
                            {
                                // special case: If we are root, rotate complete SOG to new rotation
                                SetRot(part, q);
                            }
                            else
                            {
                                // we are a child. The rotation values will be set to the one of root modified by rot, as in SL. Don't ask.
                                var rootPart = part.ParentGroup.RootPart;
                                SetRot(part, rootPart.RotationOffset * (Quaternion)q);
                            }

                            break;

                        case ScriptBaseClass.PRIM_TYPE:
                            if (remain < 3)
                                return new LSL_List();

                            try
                            {
                                code = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TYPE: arg #{1} - parameter 1 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            remain = rules.Length - idx;
                            float hollow;
                            LSL_Vector twist;
                            LSL_Vector taper_b;
                            LSL_Vector topshear;
                            float revolutions;
                            float radiusoffset;
                            float skew;
                            LSL_Vector holesize;
                            LSL_Vector profilecut;

                            switch (code)
                            {
                                case ScriptBaseClass.PRIM_TYPE_BOX:
                                    if (remain < 6)
                                        return new LSL_List();

                                    try
                                    {
                                        face = rules.GetLSLIntegerItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_BOX: arg #{1} - parameter 2 must be integer",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        v = rules.GetVector3Item(idx++); // cut
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_BOX: arg #{1} - parameter 3 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        hollow = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_BOX: arg #{1} - parameter 4 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        twist = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_BOX: arg #{1} - parameter 5 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        taper_b = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_BOX: arg #{1} - parameter 6 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        topshear = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_BOX: arg #{1} - parameter 7 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    SetPrimitiveShapeParams(part, face, v, hollow, twist, taper_b, topshear,
                                        (byte)ProfileShape.Square, (byte)Extrusion.Straight);
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_CYLINDER:
                                    if (remain < 6)
                                        return new LSL_List();

                                    try
                                    {
                                        face = rules.GetLSLIntegerItem(idx++); // holeshape
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_CYLINDER: arg #{1} - parameter 3 must be integer",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        v = rules.GetVector3Item(idx++); // cut
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_CYLINDER: arg #{1} - parameter 4 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        hollow = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_CYLINDER: arg #{1} - parameter 5 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        twist = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_CYLINDER: arg #{1} - parameter 6 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        taper_b = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_CYLINDER: arg #{1} - parameter 7 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        topshear = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_CYLINDER: arg #{1} - parameter 8 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    SetPrimitiveShapeParams(part, face, v, hollow, twist, taper_b, topshear,
                                        (byte)ProfileShape.Circle, (byte)Extrusion.Straight);
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_PRISM:
                                    if (remain < 6)
                                        return new LSL_List();

                                    try
                                    {
                                        face = rules.GetLSLIntegerItem(idx++); // holeshape
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_PRISM: arg #{1} - parameter 3 must be integer",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        v = rules.GetVector3Item(idx++); //cut
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_PRISM: arg #{1} - parameter 4 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        hollow = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_PRISM: arg #{1} - parameter 5 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        twist = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_PRISM: arg #{1} - parameter 6 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        taper_b = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_PRISM: arg #{1} - parameter 7 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        topshear = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_PRISM: arg #{1} - parameter 8 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    SetPrimitiveShapeParams(part, face, v, hollow, twist, taper_b, topshear,
                                        (byte)ProfileShape.EquilateralTriangle, (byte)Extrusion.Straight);
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_SPHERE:
                                    if (remain < 5)
                                        return new LSL_List();

                                    try
                                    {
                                        face = rules.GetLSLIntegerItem(idx++); // holeshape
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_SPHERE: arg #{1} - parameter 3 must be integer",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        v = rules.GetVector3Item(idx++); // cut
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_SPHERE: arg #{1} - parameter 4 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        hollow = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_SPHERE: arg #{1} - parameter 5 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        twist = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_SPHERE: arg #{1} - parameter 6 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        taper_b = rules.GetVector3Item(idx++); // dimple
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_SPHERE: arg #{1} - parameter 7 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    SetPrimitiveShapeParams(part, face, v, hollow, twist, taper_b,
                                        (byte)ProfileShape.HalfCircle, (byte)Extrusion.Curve1);
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_TORUS:
                                    if (remain < 11)
                                        return new LSL_List();

                                    try
                                    {
                                        face = rules.GetLSLIntegerItem(idx++); // holeshape
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 3 must be integer",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        v = rules.GetVector3Item(idx++); //cut
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 4 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        hollow = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 5 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        twist = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 6 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        holesize = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 7 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        topshear = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 8 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        profilecut = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 9 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        taper_b = rules.GetVector3Item(idx++); // taper_a
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 10 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        revolutions = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 11 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        radiusoffset = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 12 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        skew = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TORUS: arg #{1} - parameter 13 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    SetPrimitiveShapeParams(part, face, v, hollow, twist, holesize, topshear,
                                        profilecut, taper_b,
                                        revolutions, radiusoffset, skew, (byte)ProfileShape.Circle,
                                        (byte)Extrusion.Curve1);
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_TUBE:
                                    if (remain < 11)
                                        return new LSL_List();

                                    try
                                    {
                                        face = rules.GetLSLIntegerItem(idx++); // holeshape
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 3 must be integer",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        v = rules.GetVector3Item(idx++); //cut
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 4 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        hollow = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 5 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        twist = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 6 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        holesize = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 7 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        topshear = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 8 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        profilecut = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 9 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        taper_b = rules.GetVector3Item(idx++); // taper_a
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 10 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        revolutions = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 11 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        radiusoffset = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 12 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        skew = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_TUBE: arg #{1} - parameter 13 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    SetPrimitiveShapeParams(part, face, v, hollow, twist, holesize, topshear,
                                        profilecut, taper_b,
                                        revolutions, radiusoffset, skew, (byte)ProfileShape.Square,
                                        (byte)Extrusion.Curve1);
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_RING:
                                    if (remain < 11)
                                        return new LSL_List();

                                    try
                                    {
                                        face = rules.GetLSLIntegerItem(idx++); // holeshape
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 3 must be integer",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        v = rules.GetVector3Item(idx++); //cut
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 4 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        hollow = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 5 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        twist = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 6 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        holesize = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 7 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        topshear = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 8 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        profilecut = rules.GetVector3Item(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 9 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        taper_b = rules.GetVector3Item(idx++); // taper_a
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 10 must be vector",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        revolutions = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 11 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        radiusoffset = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 12 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    try
                                    {
                                        skew = (float)rules.GetLSLFloatItem(idx++);
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_RING: arg #{1} - parameter 13 must be float",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    SetPrimitiveShapeParams(part, face, v, hollow, twist, holesize, topshear,
                                        profilecut, taper_b,
                                        revolutions, radiusoffset, skew, (byte)ProfileShape.EquilateralTriangle,
                                        (byte)Extrusion.Curve1);
                                    break;

                                case ScriptBaseClass.PRIM_TYPE_SCULPT:
                                    if (remain < 2)
                                        return new LSL_List();

                                    var map = rules.Data[idx++].ToString();
                                    try
                                    {
                                        face = rules.GetLSLIntegerItem(idx++); // type
                                    }
                                    catch (InvalidCastException)
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_TYPE, PRIM_TYPE_SCULPT: arg #{1} - parameter 4 must be integer",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }

                                    SetPrimitiveShapeParams(part, map, face, (byte)Extrusion.Curve1);
                                    break;
                            }

                            break;

                        case ScriptBaseClass.PRIM_TEXTURE:
                            if (remain < 5)
                                return new LSL_List();

                            face = rules.GetLSLIntegerItem(idx++);
                            string tex;
                            LSL_Vector repeats;
                            LSL_Vector offsets;
                            double rotation;

                            tex = rules.Data[idx++].ToString();
                            try
                            {
                                repeats = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TEXTURE: arg #{1} - parameter 3 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                offsets = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TEXTURE: arg #{1} - parameter 4 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                rotation = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TEXTURE: arg #{1} - parameter 5 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            SetTextureParams(part, tex, repeats.x, repeats.y, offsets.x, offsets.y, rotation, face);
                            break;

                        case ScriptBaseClass.PRIM_COLOR:
                            if (remain < 3)
                                return new LSL_List();

                            LSL_Vector color;
                            double alpha;

                            try
                            {
                                face = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_COLOR: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                color = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_COLOR: arg #{1} - parameter 3 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                alpha = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_COLOR: arg #{1} - parameter 4 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            part.SetFaceColorAlpha(face, color, alpha);

                            break;

                        case ScriptBaseClass.PRIM_FLEXIBLE:
                            if (remain < 7)
                                return new LSL_List();
                            bool flexi;
                            int softness;
                            float gravity;
                            float friction;
                            float wind;
                            float tension;
                            LSL_Vector force;

                            try
                            {
                                flexi = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FLEXIBLE: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                softness = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FLEXIBLE: arg #{1} - parameter 3 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                gravity = (float)rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FLEXIBLE: arg #{1} - parameter 4 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                friction = (float)rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FLEXIBLE: arg #{1} - parameter 5 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                wind = (float)rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FLEXIBLE: arg #{1} - parameter 6 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                tension = (float)rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FLEXIBLE: arg #{1} - parameter 7 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                force = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FLEXIBLE: arg #{1} - parameter 8 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            SetFlexi(part, flexi, softness, gravity, friction, wind, tension, force);

                            break;

                        case ScriptBaseClass.PRIM_POINT_LIGHT:
                            if (remain < 5)
                                return new LSL_List();
                            bool light;
                            LSL_Vector lightcolor;
                            float intensity;
                            float radius;
                            float falloff;

                            try
                            {
                                light = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_POINT_LIGHT: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                lightcolor = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_POINT_LIGHT: arg #{1} - parameter 3 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                intensity = (float)rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_POINT_LIGHT: arg #{1} - parameter 4 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                radius = (float)rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_POINT_LIGHT: arg #{1} - parameter 5 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                falloff = (float)rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_POINT_LIGHT: arg #{1} - parameter 6 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            SetPointLight(part, light, lightcolor, intensity, radius, falloff);

                            break;

                        case ScriptBaseClass.PRIM_GLOW:
                            if (remain < 2)
                                return new LSL_List();

                            float glow;

                            try
                            {
                                face = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_GLOW: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                glow = (float)rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_GLOW: arg #{1} - parameter 3 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            SetGlow(part, face, glow);

                            break;

                        case ScriptBaseClass.PRIM_BUMP_SHINY:
                            if (remain < 3)
                                return new LSL_List();

                            int shiny;
                            Bumpiness bump;

                            try
                            {
                                face = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_BUMP_SHINY: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                shiny = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_BUMP_SHINY: arg #{1} - parameter 3 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                bump = (Bumpiness)(int)rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_BUMP_SHINY: arg #{1} - parameter 4 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            SetShiny(part, face, shiny, bump);

                            break;

                        case ScriptBaseClass.PRIM_FULLBRIGHT:
                            if (remain < 2)
                                return new LSL_List();
                            bool st;

                            try
                            {
                                face = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FULLBRIGHT: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                st = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_FULLBRIGHT: arg #{1} - parameter 4 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            SetFullBright(part, face, st);
                            break;

                        case ScriptBaseClass.PRIM_MATERIAL:
                            if (remain < 1)
                                return new LSL_List();
                            int mat;

                            try
                            {
                                mat = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_MATERIAL: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            if (mat < 0 || mat > 7)
                                return new LSL_List();

                            part.Material = Convert.ToByte(mat);
                            break;

                        case ScriptBaseClass.PRIM_PHANTOM:
                            if (remain < 1)
                                return new LSL_List();

                            var ph = rules.Data[idx++].ToString();
                            part.ParentGroup.ScriptSetPhantomStatus(ph.Equals("1"));

                            break;

                        case ScriptBaseClass.PRIM_PHYSICS:
                            if (remain < 1)
                                return new LSL_List();
                            var phy = rules.Data[idx++].ToString();
                            part.ScriptSetPhysicsStatus(phy.Equals("1"));
                            break;

                        case ScriptBaseClass.PRIM_PHYSICS_SHAPE_TYPE:
                            if (remain < 1)
                                return new LSL_List();

                            int shape_type;

                            try
                            {
                                shape_type = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_PHYSICS_SHAPE_TYPE: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            var physdata = new ExtraPhysicsData
                            {
                                Density = part.Density,
                                Bounce = part.Restitution,
                                GravitationModifier = part.GravityModifier,
                                PhysShapeType = (PhysShapeType)shape_type
                            };

                            part.UpdateExtraPhysics(physdata);

                            break;

                        case ScriptBaseClass.PRIM_PHYSICS_MATERIAL:
                            if (remain < 5)
                                return new LSL_List();

                            int material_bits = rules.GetLSLIntegerItem(idx++);
                            var material_density = (float)rules.GetLSLFloatItem(idx++);
                            var material_friction = (float)rules.GetLSLFloatItem(idx++);
                            var material_restitution = (float)rules.GetLSLFloatItem(idx++);
                            var material_gravity_modifier = (float)rules.GetLSLFloatItem(idx++);

                            SetPhysicsMaterial(part, material_bits, material_density, material_friction,
                                material_restitution, material_gravity_modifier);

                            break;

                        case ScriptBaseClass.PRIM_TEMP_ON_REZ:
                            if (remain < 1)
                                return new LSL_List();
                            var temp = rules.Data[idx++].ToString();

                            part.ParentGroup.ScriptSetTemporaryStatus(temp.Equals("1"));

                            break;

                        case ScriptBaseClass.PRIM_TEXGEN:
                            if (remain < 2)
                                return new LSL_List();
                            //face,type
                            int style;

                            try
                            {
                                face = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TEXGEN: arg #{1} - parameter 2 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                style = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TEXGEN: arg #{1} - parameter 3 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            SetTexGen(part, face, style);
                            break;
                        case ScriptBaseClass.PRIM_TEXT:
                            if (remain < 3)
                                return new LSL_List();
                            string primText;
                            LSL_Vector primTextColor;
                            LSL_Float primTextAlpha;

                            try
                            {
                                primText = rules.GetStrictStringItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TEXT: arg #{1} - parameter 2 must be string",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                primTextColor = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TEXT: arg #{1} - parameter 3 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                primTextAlpha = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_TEXT: arg #{1} - parameter 4 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            var av3 = Vector3.Clamp(primTextColor, 0.0f, 1.0f);
                            part.SetText(primText, av3, Utils.Clamp((float)primTextAlpha, 0.0f, 1.0f));

                            break;

                        case ScriptBaseClass.PRIM_NAME:
                            if (remain < 1)
                                return new LSL_List();
                            try
                            {
                                var primName = rules.GetStrictStringItem(idx++);
                                part.Name = primName;
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_NAME: arg #{1} - parameter 2 must be string",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            break;
                        case ScriptBaseClass.PRIM_DESC:
                            if (remain < 1)
                                return new LSL_List();
                            try
                            {
                                var primDesc = rules.GetStrictStringItem(idx++);
                                part.Description = primDesc;
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_DESC: arg #{1} - parameter 2 must be string",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            break;
                        case ScriptBaseClass.PRIM_ROT_LOCAL:
                            if (remain < 1)
                                return new LSL_List();
                            LSL_Rotation rot;
                            try
                            {
                                rot = rules.GetQuaternionItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ROT_LOCAL: arg #{1} - parameter 2 must be rotation",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            SetRot(part, rot);
                            break;

                        case ScriptBaseClass.PRIM_OMEGA:
                            if (remain < 3)
                                return new LSL_List();
                            LSL_Vector axis;
                            LSL_Float spinrate;
                            LSL_Float gain;

                            try
                            {
                                axis = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_OMEGA: arg #{1} - parameter 2 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                spinrate = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_OMEGA: arg #{1} - parameter 3 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            try
                            {
                                gain = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_OMEGA: arg #{1} - parameter 4 must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            TargetOmega(part, axis, spinrate, gain);
                            break;

                        case ScriptBaseClass.PRIM_SLICE:
                            if (remain < 1)
                                return new LSL_List();
                            LSL_Vector slice;
                            try
                            {
                                slice = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_SLICE: arg #{1} - parameter 2 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            part.UpdateSlice((float)slice.x, (float)slice.y);
                            break;

                        case ScriptBaseClass.PRIM_SIT_TARGET:
                            if (remain < 3)
                                return new LSL_List();

                            int active;
                            try
                            {
                                active = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_SIT_TARGET: arg #{1} - parameter 1 must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Vector offset;
                            try
                            {
                                offset = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_SIT_TARGET: arg #{1} - parameter 2 must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Rotation sitrot;
                            try
                            {
                                sitrot = rules.GetQuaternionItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_SIT_TARGET: arg #{1} - parameter 3 must be rotation",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            // not SL compatible since we don't have a independent flag to control active target but use the values of offset and rotation
                            if (active == 1)
                            {
                                if (offset.x == 0 && offset.y == 0 && offset.z == 0 && sitrot.s == 1.0)
                                    offset.z = 1e-5f; // hack
                                SitTarget(part, offset, sitrot);
                            }
                            else if (active == 0)
                            {
                                SitTarget(part, Vector3.Zero, Quaternion.Identity);
                            }

                            break;

                        case ScriptBaseClass.PRIM_ALPHA_MODE:
                            if (remain < 3)
                                return new LSL_List();

                            try
                            {
                                face = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ALPHA_MODE: arg #{1} - must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            int materialAlphaMode;
                            try
                            {
                                materialAlphaMode = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ALPHA_MODE: arg #{1} - must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            if (materialAlphaMode < 0 || materialAlphaMode > 3)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ALPHA_MODE: arg #{1} - must be 0 to 3",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            int materialMaskCutoff;
                            try
                            {
                                materialMaskCutoff = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ALPHA_MODE: arg #{1} - must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            if (materialMaskCutoff < 0 || materialMaskCutoff > 255)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ALPHA_MODE: arg #{1} - must be 0 to 255",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            materialChanged |= SetMaterialAlphaMode(part, face, materialAlphaMode, materialMaskCutoff);
                            break;

                        case ScriptBaseClass.PRIM_NORMAL:
                            if (remain < 5)
                                return new LSL_List();

                            try
                            {
                                face = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_NORMAL: arg #{1} - must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            var mapname = rules.Data[idx++].ToString();
                            var mapID = UUID.Zero;
                            if (!string.IsNullOrEmpty(mapname))
                            {
                                mapID = ScriptUtils.GetAssetIdFromItemName(m_host, mapname, (int)AssetType.Texture);
                                if (mapID.IsZero())
                                    if (!UUID.TryParse(mapname, out mapID))
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_NORMAL: arg #{1} - must be a UUID or a texture name on object inventory",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }
                            }

                            LSL_Vector mnrepeat;
                            try
                            {
                                mnrepeat = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_NORMAL: arg #{1} - must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Vector mnoffset;
                            try
                            {
                                mnoffset = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_NORMAL: arg #{1} - must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Float mnrot;
                            try
                            {
                                mnrot = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_NORMAL: arg #{1} - must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            var repeatX = (float)Util.Clamp(mnrepeat.x, -100.0, 100.0);
                            var repeatY = (float)Util.Clamp(mnrepeat.y, -100.0, 100.0);
                            var offsetX = (float)Util.Clamp(mnoffset.x, 0, 1.0);
                            var offsetY = (float)Util.Clamp(mnoffset.y, 0, 1.0);

                            materialChanged |= SetMaterialNormalMap(part, face, mapID, repeatX, repeatY, offsetX,
                                offsetY, (float)mnrot);
                            break;

                        case ScriptBaseClass.PRIM_SPECULAR:
                            if (remain < 8)
                                return new LSL_List();

                            try
                            {
                                face = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_SPECULAR: arg #{1} - must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            var smapname = rules.Data[idx++].ToString();
                            var smapID = UUID.Zero;
                            if (!string.IsNullOrEmpty(smapname))
                            {
                                smapID = ScriptUtils.GetAssetIdFromItemName(m_host, smapname, (int)AssetType.Texture);
                                if (smapID.IsZero())
                                    if (!UUID.TryParse(smapname, out smapID))
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_SPECULAR: arg #{1} - must be a UUID or a texture name on object inventory",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }
                            }

                            LSL_Vector msrepeat;
                            try
                            {
                                msrepeat = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_SPECULAR: arg #{1} - must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Vector msoffset;
                            try
                            {
                                msoffset = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_SPECULAR: arg #{1} - must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Float msrot;
                            try
                            {
                                msrot = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_SPECULAR: arg #{1} - must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Vector mscolor;
                            try
                            {
                                mscolor = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_SPECULAR: arg #{1} - must be vector",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Integer msgloss;
                            try
                            {
                                msgloss = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_SPECULAR: arg #{1} - must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Integer msenv;
                            try
                            {
                                msenv = rules.GetLSLIntegerItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_SPECULAR: arg #{1} - must be integer",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            var srepeatX = (float)Util.Clamp(msrepeat.x, -100.0, 100.0);
                            var srepeatY = (float)Util.Clamp(msrepeat.y, -100.0, 100.0);
                            var soffsetX = (float)Util.Clamp(msoffset.x, -1.0, 1.0);
                            var soffsetY = (float)Util.Clamp(msoffset.y, -1.0, 1.0);
                            var colorR = (byte)(255.0 * Util.Clamp(mscolor.x, 0, 1.0) + 0.5);
                            var colorG = (byte)(255.0 * Util.Clamp(mscolor.y, 0, 1.0) + 0.5);
                            var colorB = (byte)(255.0 * Util.Clamp(mscolor.z, 0, 1.0) + 0.5);
                            var gloss = (byte)Util.Clamp((int)msgloss, 0, 255);
                            var env = (byte)Util.Clamp((int)msenv, 0, 255);

                            materialChanged |= SetMaterialSpecMap(part, face, smapID, srepeatX, srepeatY, soffsetX,
                                soffsetY,
                                (float)msrot, colorR, colorG, colorB, gloss, env);

                            break;

                        case ScriptBaseClass.PRIM_LINK_TARGET:
                            if (remain <
                                3) // setting to 3 on the basis that parsing any usage of PRIM_LINK_TARGET that has nothing following it is pointless.
                                return new LSL_List();

                            return rules.GetSublist(idx, -1);

                        case ScriptBaseClass.PRIM_PROJECTOR:
                            if (remain < 4)
                                return new LSL_List();

                            var stexname = rules.Data[idx++].ToString();
                            var stexID = UUID.Zero;
                            if (!string.IsNullOrEmpty(stexname))
                            {
                                stexID = ScriptUtils.GetAssetIdFromItemName(m_host, stexname, (int)AssetType.Texture);
                                if (stexID.IsZero())
                                    if (!UUID.TryParse(stexname, out stexID))
                                    {
                                        Error(originFunc,
                                            string.Format(
                                                "Error running rule #{0} -> PRIM_PROJECTOR: arg #{1} - must be a UUID or a texture name on object inventory",
                                                rulesParsed, idx - idxStart - 1));
                                        return new LSL_List();
                                    }
                            }

                            LSL_Float fov;
                            try
                            {
                                fov = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_PROJECTOR: arg #{1} - must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Float focus;
                            try
                            {
                                focus = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_PROJECTOR: arg #{1} - must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            LSL_Float amb;
                            try
                            {
                                amb = rules.GetLSLFloatItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format("Error running rule #{0} -> PRIM_PROJECTOR: arg #{1} - must be float",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            if (stexID.IsNotZero())
                            {
                                part.Shape.ProjectionEntry = true;
                                part.Shape.ProjectionTextureUUID = stexID;
                                part.Shape.ProjectionFOV = Util.Clamp((float)fov, 0, 3.0f);
                                part.Shape.ProjectionFocus = Util.Clamp((float)focus, 0, 20.0f);
                                part.Shape.ProjectionAmbiance = Util.Clamp((float)amb, 0, 1.0f);

                                part.ParentGroup.HasGroupChanged = true;
                                part.ScheduleFullUpdate();
                            }
                            else if (part.Shape.ProjectionEntry)
                            {
                                part.Shape.ProjectionEntry = false;

                                part.ParentGroup.HasGroupChanged = true;
                                part.ScheduleFullUpdate();
                            }

                            break;

                        default:
                            Error(originFunc,
                                string.Format("Error running rule #{0}: arg #{1} - unsupported parameter", rulesParsed,
                                    idx - idxStart));
                            return new LSL_List();
                    }
                }
            }
            catch (InvalidCastException e)
            {
                Error(originFunc,
                    string.Format("Error running rule #{0}: arg #{1} - ", rulesParsed, idx - idxStart) + e.Message);
            }
            finally
            {
                if (positionChanged)
                {
                    if (part.ParentGroup.RootPart == part)
                    {
                        var parent = part.ParentGroup;
//                        Util.FireAndForget(delegate(object x) {
                        parent.UpdateGroupPosition(currentPosition);
//                        });
                    }
                    else
                    {
                        part.OffsetPosition = currentPosition;
//                        SceneObjectGroup parent = part.ParentGroup;
//                        parent.HasGroupChanged = true;
//                        parent.ScheduleGroupForTerseUpdate();
                        part.ScheduleTerseUpdate();
                    }
                }

                if (materialChanged)
                    if (part.ParentGroup != null && !part.ParentGroup.IsDeleted)
                    {
                        part.TriggerScriptChangedEvent(Changed.TEXTURE);
                        part.ScheduleFullUpdate();
                        part.ParentGroup.HasGroupChanged = true;
                    }
            }

            return new LSL_List();
        }

        protected bool SetMaterialAlphaMode(SceneObjectPart part, int face, int materialAlphaMode,
            int materialMaskCutoff)
        {
            if (m_materialsModule == null)
                return false;

            var nsides = part.GetNumberOfSides();

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                var changed = false;
                for (var i = 0; i < nsides; i++)
                    changed |= SetFaceMaterialAlphaMode(part, i, materialAlphaMode, materialMaskCutoff);
                return changed;
            }

            if (face >= 0 && face < nsides)
                return SetFaceMaterialAlphaMode(part, face, materialAlphaMode, materialMaskCutoff);

            return false;
        }

        protected bool SetFaceMaterialAlphaMode(SceneObjectPart part, int face, int materialAlphaMode,
            int materialMaskCutoff)
        {
            var tex = part.Shape.Textures;
            var texface = tex.CreateFace((uint)face);
            if (texface == null)
                return false;

            FaceMaterial mat = null;
            var oldid = texface.MaterialID;
            if (oldid.IsZero())
            {
                if (materialAlphaMode == 1)
                    return false;
            }
            else
            {
                mat = m_materialsModule.GetMaterialCopy(oldid);
            }

            if (mat == null)
                mat = new FaceMaterial();

            mat.DiffuseAlphaMode = (byte)materialAlphaMode;
            mat.AlphaMaskCutoff = (byte)materialMaskCutoff;

            var id = m_materialsModule
                .AddNewMaterial(mat); // id is a hash of entire material hash, so this means no change
            if (oldid.Equals(id))
                return false;

            texface.MaterialID = id;
            part.Shape.TextureEntry = tex.GetBytes(9);
            m_materialsModule.RemoveMaterial(oldid);
            return true;
        }

        protected bool SetMaterialNormalMap(SceneObjectPart part, int face, UUID mapID, float repeatX, float repeatY,
            float offsetX, float offsetY, float rot)
        {
            if (m_materialsModule == null)
                return false;

            var nsides = part.GetNumberOfSides();

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                var changed = false;
                for (var i = 0; i < nsides; i++)
                    changed |= SetFaceMaterialNormalMap(part, i, mapID, repeatX, repeatY, offsetX, offsetY, rot);
                return changed;
            }

            if (face >= 0 && face < nsides)
                return SetFaceMaterialNormalMap(part, face, mapID, repeatX, repeatY, offsetX, offsetY, rot);

            return false;
        }

        protected bool SetFaceMaterialNormalMap(SceneObjectPart part, int face, UUID mapID, float repeatX,
            float repeatY,
            float offsetX, float offsetY, float rot)

        {
            var tex = part.Shape.Textures;
            var texface = tex.CreateFace((uint)face);
            if (texface == null)
                return false;

            FaceMaterial mat = null;
            var oldid = texface.MaterialID;

            if (oldid.IsZero())
            {
                if (mapID.IsZero())
                    return false;
            }
            else
            {
                mat = m_materialsModule.GetMaterialCopy(oldid);
            }

            if (mat == null)
                mat = new FaceMaterial();

            mat.NormalMapID = mapID;
            mat.NormalOffsetX = offsetX;
            mat.NormalOffsetY = offsetY;
            mat.NormalRepeatX = repeatX;
            mat.NormalRepeatY = repeatY;
            mat.NormalRotation = rot;

            mapID = m_materialsModule.AddNewMaterial(mat);

            if (oldid.Equals(mapID))
                return false;

            texface.MaterialID = mapID;
            part.Shape.TextureEntry = tex.GetBytes(9);
            m_materialsModule.RemoveMaterial(oldid);
            return true;
        }

        protected bool SetMaterialSpecMap(SceneObjectPart part, int face, UUID mapID, float repeatX, float repeatY,
            float offsetX, float offsetY, float rot,
            byte colorR, byte colorG, byte colorB,
            byte gloss, byte env)
        {
            if (m_materialsModule == null)
                return false;

            var nsides = part.GetNumberOfSides();

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                var changed = false;
                for (var i = 0; i < nsides; i++)
                    changed |= SetFaceMaterialSpecMap(part, i, mapID, repeatX, repeatY, offsetX, offsetY, rot,
                        colorR, colorG, colorB, gloss, env);
                return changed;
            }

            if (face >= 0 && face < nsides)
                return SetFaceMaterialSpecMap(part, face, mapID, repeatX, repeatY, offsetX, offsetY, rot,
                    colorR, colorG, colorB, gloss, env);

            return false;
        }

        protected bool SetFaceMaterialSpecMap(SceneObjectPart part, int face, UUID mapID, float repeatX, float repeatY,
            float offsetX, float offsetY, float rot,
            byte colorR, byte colorG, byte colorB,
            byte gloss, byte env)
        {
            var tex = part.Shape.Textures;
            var texface = tex.CreateFace((uint)face);
            if (texface == null)
                return false;

            FaceMaterial mat = null;
            var oldid = texface.MaterialID;

            if (oldid.IsZero())
            {
                if (mapID.IsZero())
                    return false;
            }
            else
            {
                mat = m_materialsModule.GetMaterialCopy(oldid);
            }

            if (mat == null)
                mat = new FaceMaterial();

            mat.SpecularMapID = mapID;
            mat.SpecularOffsetX = offsetX;
            mat.SpecularOffsetY = offsetY;
            mat.SpecularRepeatX = repeatX;
            mat.SpecularRepeatY = repeatY;
            mat.SpecularRotation = rot;
            mat.SpecularLightColorR = colorR;
            mat.SpecularLightColorG = colorG;
            mat.SpecularLightColorB = colorB;
            mat.SpecularLightExponent = gloss;
            mat.EnvironmentIntensity = env;

            mapID = m_materialsModule.AddNewMaterial(mat);

            if (oldid.Equals(mapID))
                return false;

            texface.MaterialID = mapID;
            part.Shape.TextureEntry = tex.GetBytes(9);
            m_materialsModule.RemoveMaterial(oldid);
            return true;
        }

        protected LSL_List SetAgentParams(ScenePresence sp, LSL_List rules, string originFunc, ref uint rulesParsed)
        {
            var idx = 0;
            var idxStart = 0;

            try
            {
                while (idx < rules.Length)
                {
                    ++rulesParsed;
                    int code = rules.GetLSLIntegerItem(idx++);

                    var remain = rules.Length - idx;
                    idxStart = idx;

                    switch (code)
                    {
                        case ScriptBaseClass.PRIM_POSITION:
                        case ScriptBaseClass.PRIM_POS_LOCAL:
                            if (remain < 1)
                                return new LSL_List();

                            try
                            {
                                sp.OffsetPosition = rules.GetVector3Item(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                if (code == ScriptBaseClass.PRIM_POSITION)
                                    Error(originFunc,
                                        string.Format(
                                            "Error running rule #{0} -> PRIM_POSITION: arg #{1} - parameter 2 must be vector",
                                            rulesParsed, idx - idxStart - 1));
                                else
                                    Error(originFunc,
                                        string.Format(
                                            "Error running rule #{0} -> PRIM_POS_LOCAL: arg #{1} - parameter 2 must be vector",
                                            rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            break;

                        case ScriptBaseClass.PRIM_ROTATION:
                            if (remain < 1)
                                return new LSL_List();

                            Quaternion inRot;

                            try
                            {
                                inRot = rules.GetQuaternionItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ROTATION: arg #{1} - parameter 2 must be rotation",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            var parentPart = sp.ParentPart;

                            if (parentPart != null)
                                sp.Rotation = m_host.GetWorldRotation() * inRot;

                            break;

                        case ScriptBaseClass.PRIM_ROT_LOCAL:
                            if (remain < 1)
                                return new LSL_List();

                            try
                            {
                                sp.Rotation = rules.GetQuaternionItem(idx++);
                            }
                            catch (InvalidCastException)
                            {
                                Error(originFunc,
                                    string.Format(
                                        "Error running rule #{0} -> PRIM_ROT_LOCAL: arg #{1} - parameter 2 must be rotation",
                                        rulesParsed, idx - idxStart - 1));
                                return new LSL_List();
                            }

                            break;

                        case ScriptBaseClass.PRIM_TYPE:
                            Error(originFunc, "PRIM_TYPE disallowed on agent");
                            return new LSL_List();

                        case ScriptBaseClass.PRIM_OMEGA:
                            Error(originFunc, "PRIM_OMEGA disallowed on agent");
                            return new LSL_List();

                        case ScriptBaseClass.PRIM_LINK_TARGET:
                            if (remain <
                                3) // setting to 3 on the basis that parsing any usage of PRIM_LINK_TARGET that has nothing following it is pointless.
                                return new LSL_List();

                            return rules.GetSublist(idx, -1);

                        default:
                            Error(originFunc,
                                string.Format("Error running rule #{0} on agent: arg #{1} - disallowed on agent",
                                    rulesParsed, idx - idxStart));
                            return new LSL_List();
                    }
                }
            }
            catch (InvalidCastException e)
            {
                Error(
                    originFunc,
                    string.Format("Error running rule #{0}: arg #{1} - ", rulesParsed, idx - idxStart) + e.Message);
            }

            return new LSL_List();
        }

        /// <summary>
        ///     Helper to calculate bounding box of an avatar.
        /// </summary>
        private void BoundingBoxOfScenePresence(ScenePresence sp, out Vector3 lower, out Vector3 upper)
        {
            // Adjust from OS model
            // avatar height = visual height - 0.2, bounding box height = visual height
            // to SL model
            // avatar height = visual height, bounding box height = visual height + 0.2
            var height = sp.Appearance.AvatarHeight + m_avatarHeightCorrection;

            // According to avatar bounding box in SL 2015-04-18:
            // standing = <-0.275,-0.35,-0.1-0.5*h> : <0.275,0.35,0.1+0.5*h>
            // groundsitting = <-0.3875,-0.5,-0.05-0.375*h> : <0.3875,0.5,0.5>
            // sitting = <-0.5875,-0.35,-0.35-0.375*h> : <0.1875,0.35,-0.25+0.25*h>

            // When avatar is sitting
            if (sp.ParentPart != null)
            {
                lower = new Vector3(m_lABB1SitX0, m_lABB1SitY0, m_lABB1SitZ0 + m_lABB1SitZ1 * height);
                upper = new Vector3(m_lABB2SitX0, m_lABB2SitY0, m_lABB2SitZ0 + m_lABB2SitZ1 * height);
            }
            // When avatar is groundsitting
            else if (sp.Animator.Animations.ImplicitDefaultAnimation.AnimID.Equals(
                         DefaultAvatarAnimations.AnimsUUIDbyName["SIT_GROUND_CONSTRAINED"]))
            {
                lower = new Vector3(m_lABB1GrsX0, m_lABB1GrsY0, m_lABB1GrsZ0 + m_lABB1GrsZ1 * height);
                upper = new Vector3(m_lABB2GrsX0, m_lABB2GrsY0, m_lABB2GrsZ0 + m_lABB2GrsZ1 * height);
            }
            // When avatar is standing or flying
            else
            {
                lower = new Vector3(m_lABB1StdX0, m_lABB1StdY0, m_lABB1StdZ0 + m_lABB1StdZ1 * height);
                upper = new Vector3(m_lABB2StdX0, m_lABB2StdY0, m_lABB2StdZ0 + m_lABB2StdZ1 * height);
            }
        }

        public LSL_List GetPrimParams(SceneObjectPart part, LSL_List rules, ref LSL_List res)
        {
            var idx = 0;
            int face;
            Primitive.TextureEntry tex;
            var nsides = GetNumberOfSides(part);

            while (idx < rules.Length)
            {
                int code = rules.GetLSLIntegerItem(idx++);
                var remain = rules.Length - idx;

                switch (code)
                {
                    case ScriptBaseClass.PRIM_MATERIAL:
                        res.Add(new LSL_Integer(part.Material));
                        break;

                    case ScriptBaseClass.PRIM_PHYSICS:
                        if ((part.GetEffectiveObjectFlags() & (uint)PrimFlags.Physics) != 0)
                            res.Add(new LSL_Integer(1));
                        else
                            res.Add(new LSL_Integer(0));
                        break;

                    case ScriptBaseClass.PRIM_TEMP_ON_REZ:
                        if ((part.GetEffectiveObjectFlags() & (uint)PrimFlags.TemporaryOnRez) != 0)
                            res.Add(new LSL_Integer(1));
                        else
                            res.Add(new LSL_Integer(0));
                        break;

                    case ScriptBaseClass.PRIM_PHANTOM:
                        if ((part.GetEffectiveObjectFlags() & (uint)PrimFlags.Phantom) != 0)
                            res.Add(new LSL_Integer(1));
                        else
                            res.Add(new LSL_Integer(0));
                        break;

                    case ScriptBaseClass.PRIM_POSITION:
                        var v = new LSL_Vector(part.AbsolutePosition.X,
                            part.AbsolutePosition.Y,
                            part.AbsolutePosition.Z);
                        res.Add(v);
                        break;

                    case ScriptBaseClass.PRIM_SIZE:
                        res.Add(new LSL_Vector(part.Scale));
                        break;

                    case ScriptBaseClass.PRIM_ROTATION:
                        res.Add(GetPartRot(part));
                        break;

                    case ScriptBaseClass.PRIM_PHYSICS_SHAPE_TYPE:
                        res.Add(new LSL_Integer(part.PhysicsShapeType));
                        break;

                    case ScriptBaseClass.PRIM_TYPE:
                        // implementing box
                        var Shape = part.Shape;
                        var primType = (int)part.GetPrimType();
                        res.Add(new LSL_Integer(primType));
                        var topshearx = (sbyte)Shape.PathShearX / 100.0; // Fix negative values for PathShearX
                        var topsheary = (sbyte)Shape.PathShearY / 100.0; // and PathShearY.
                        switch (primType)
                        {
                            case ScriptBaseClass.PRIM_TYPE_BOX:
                            case ScriptBaseClass.PRIM_TYPE_CYLINDER:
                            case ScriptBaseClass.PRIM_TYPE_PRISM:
                                res.Add(new LSL_Integer(Shape.ProfileCurve) & 0xf0); // Isolate hole shape nibble.
                                res.Add(new LSL_Vector(Shape.ProfileBegin / 50000.0, 1 - Shape.ProfileEnd / 50000.0,
                                    0));
                                res.Add(new LSL_Float(Shape.ProfileHollow / 50000.0));
                                res.Add(new LSL_Vector(Shape.PathTwistBegin / 100.0, Shape.PathTwist / 100.0, 0));
                                res.Add(new LSL_Vector(1 - (Shape.PathScaleX / 100.0 - 1),
                                    1 - (Shape.PathScaleY / 100.0 - 1), 0));
                                res.Add(new LSL_Vector(topshearx, topsheary, 0));
                                break;

                            case ScriptBaseClass.PRIM_TYPE_SPHERE:
                                res.Add(new LSL_Integer(Shape.ProfileCurve) & 0xf0); // Isolate hole shape nibble.
                                res.Add(new LSL_Vector(Shape.PathBegin / 50000.0, 1 - Shape.PathEnd / 50000.0, 0));
                                res.Add(new LSL_Float(Shape.ProfileHollow / 50000.0));
                                res.Add(new LSL_Vector(Shape.PathTwistBegin / 100.0, Shape.PathTwist / 100.0, 0));
                                res.Add(new LSL_Vector(Shape.ProfileBegin / 50000.0, 1 - Shape.ProfileEnd / 50000.0,
                                    0));
                                break;

                            case ScriptBaseClass.PRIM_TYPE_SCULPT:
                                res.Add(new LSL_String(Shape.SculptTexture.ToString()));
                                res.Add(new LSL_Integer(Shape.SculptType));
                                break;

                            case ScriptBaseClass.PRIM_TYPE_RING:
                            case ScriptBaseClass.PRIM_TYPE_TUBE:
                            case ScriptBaseClass.PRIM_TYPE_TORUS:
                                // holeshape
                                res.Add(new LSL_Integer(Shape.ProfileCurve) & 0xf0); // Isolate hole shape nibble.

                                // cut
                                res.Add(new LSL_Vector(Shape.PathBegin / 50000.0, 1 - Shape.PathEnd / 50000.0, 0));

                                // hollow
                                res.Add(new LSL_Float(Shape.ProfileHollow / 50000.0));

                                // twist
                                res.Add(new LSL_Vector(Shape.PathTwistBegin / 100.0, Shape.PathTwist / 100.0, 0));

                                // vector holesize
                                res.Add(new LSL_Vector(1 - (Shape.PathScaleX / 100.0 - 1),
                                    1 - (Shape.PathScaleY / 100.0 - 1), 0));

                                // vector topshear
                                res.Add(new LSL_Vector(topshearx, topsheary, 0));

                                // vector profilecut
                                res.Add(new LSL_Vector(Shape.ProfileBegin / 50000.0, 1 - Shape.ProfileEnd / 50000.0,
                                    0));

                                // vector tapera
                                res.Add(new LSL_Vector(Shape.PathTaperX / 100.0, Shape.PathTaperY / 100.0, 0));

                                // float revolutions
                                res.Add(new LSL_Float(Math.Round(Shape.PathRevolutions * 0.015d, 2,
                                    MidpointRounding.AwayFromZero)) + 1.0d);
                                // Slightly inaccurate, because an unsigned byte is being used to represent
                                // the entire range of floating-point values from 1.0 through 4.0 (which is how
                                // SL does it).
                                //
                                // Using these formulas to store and retrieve PathRevolutions, it is not
                                // possible to use all values between 1.00 and 4.00. For instance, you can't
                                // represent 1.10. You can represent 1.09 and 1.11, but not 1.10. So, if you
                                // use llSetPrimitiveParams to set revolutions to 1.10 and then retreive them
                                // with llGetPrimitiveParams, you'll retrieve 1.09. You can also see a similar
                                // behavior in the viewer as you cannot set 1.10. The viewer jumps to 1.11.
                                // In SL, llSetPrimitveParams and llGetPrimitiveParams can set and get a value
                                // such as 1.10. So, SL must store and retreive the actual user input rather
                                // than only storing the encoded value.

                                // float radiusoffset
                                res.Add(new LSL_Float(Shape.PathRadiusOffset / 100.0));

                                // float skew
                                res.Add(new LSL_Float(Shape.PathSkew / 100.0));
                                break;
                        }

                        break;

                    case ScriptBaseClass.PRIM_TEXTURE:
                        if (remain < 1)
                            return new LSL_List();

                        face = rules.GetLSLIntegerItem(idx++);
                        tex = part.Shape.Textures;

                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < nsides; face++)
                            {
                                var texface = tex.GetFace((uint)face);

                                res.Add(new LSL_String(texface.TextureID.ToString()));
                                res.Add(new LSL_Vector(texface.RepeatU,
                                    texface.RepeatV,
                                    0));
                                res.Add(new LSL_Vector(texface.OffsetU,
                                    texface.OffsetV,
                                    0));
                                res.Add(new LSL_Float(texface.Rotation));
                            }
                        }
                        else
                        {
                            if (face >= 0 && face < nsides)
                            {
                                var texface = tex.GetFace((uint)face);

                                res.Add(new LSL_String(texface.TextureID.ToString()));
                                res.Add(new LSL_Vector(texface.RepeatU,
                                    texface.RepeatV,
                                    0));
                                res.Add(new LSL_Vector(texface.OffsetU,
                                    texface.OffsetV,
                                    0));
                                res.Add(new LSL_Float(texface.Rotation));
                            }
                        }

                        break;

                    case ScriptBaseClass.PRIM_COLOR:
                        if (remain < 1)
                            return new LSL_List();

                        face = rules.GetLSLIntegerItem(idx++);
                        tex = part.Shape.Textures;
                        Color4 texcolor;

                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < nsides; face++)
                            {
                                texcolor = tex.GetFace((uint)face).RGBA;
                                res.Add(new LSL_Vector(texcolor.R,
                                    texcolor.G,
                                    texcolor.B));
                                res.Add(new LSL_Float(texcolor.A));
                            }
                        }
                        else
                        {
                            texcolor = tex.GetFace((uint)face).RGBA;
                            res.Add(new LSL_Vector(texcolor.R,
                                texcolor.G,
                                texcolor.B));
                            res.Add(new LSL_Float(texcolor.A));
                        }

                        break;

                    case ScriptBaseClass.PRIM_BUMP_SHINY:
                    {
                        if (remain < 1)
                            return new LSL_List();

                        face = rules.GetLSLIntegerItem(idx++);
                        tex = part.Shape.Textures;
                        int shiny;
                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < nsides; face++)
                            {
                                var shinyness = tex.GetFace((uint)face).Shiny;
                                if (shinyness == Shininess.High)
                                    shiny = ScriptBaseClass.PRIM_SHINY_HIGH;
                                else if (shinyness == Shininess.Medium)
                                    shiny = ScriptBaseClass.PRIM_SHINY_MEDIUM;
                                else if (shinyness == Shininess.Low)
                                    shiny = ScriptBaseClass.PRIM_SHINY_LOW;
                                else
                                    shiny = ScriptBaseClass.PRIM_SHINY_NONE;
                                res.Add(new LSL_Integer(shiny));
                                res.Add(new LSL_Integer((int)tex.GetFace((uint)face).Bump));
                            }
                        }
                        else
                        {
                            var shinyness = tex.GetFace((uint)face).Shiny;
                            if (shinyness == Shininess.High)
                                shiny = ScriptBaseClass.PRIM_SHINY_HIGH;
                            else if (shinyness == Shininess.Medium)
                                shiny = ScriptBaseClass.PRIM_SHINY_MEDIUM;
                            else if (shinyness == Shininess.Low)
                                shiny = ScriptBaseClass.PRIM_SHINY_LOW;
                            else
                                shiny = ScriptBaseClass.PRIM_SHINY_NONE;
                            res.Add(new LSL_Integer(shiny));
                            res.Add(new LSL_Integer((int)tex.GetFace((uint)face).Bump));
                        }

                        break;
                    }
                    case ScriptBaseClass.PRIM_FULLBRIGHT:
                    {
                        if (remain < 1)
                            return new LSL_List();

                        face = rules.GetLSLIntegerItem(idx++);

                        tex = part.Shape.Textures;
                        int fullbright;
                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < nsides; face++)
                            {
                                if (tex.GetFace((uint)face).Fullbright)
                                    fullbright = ScriptBaseClass.TRUE;
                                else
                                    fullbright = ScriptBaseClass.FALSE;
                                res.Add(new LSL_Integer(fullbright));
                            }
                        }
                        else
                        {
                            if (tex.GetFace((uint)face).Fullbright)
                                fullbright = ScriptBaseClass.TRUE;
                            else
                                fullbright = ScriptBaseClass.FALSE;
                            res.Add(new LSL_Integer(fullbright));
                        }

                        break;
                    }
                    case ScriptBaseClass.PRIM_FLEXIBLE:
                        var shape = part.Shape;

                        // at sl this does not return true state, but if data was set
                        if (shape.FlexiEntry)
                            // correct check should had been:
                            //if (shape.PathCurve == (byte)Extrusion.Flexible)
                            res.Add(new LSL_Integer(1)); // active
                        else
                            res.Add(new LSL_Integer(0));
                        res.Add(new LSL_Integer(shape.FlexiSoftness)); // softness
                        res.Add(new LSL_Float(shape.FlexiGravity)); // gravity
                        res.Add(new LSL_Float(shape.FlexiDrag)); // friction
                        res.Add(new LSL_Float(shape.FlexiWind)); // wind
                        res.Add(new LSL_Float(shape.FlexiTension)); // tension
                        res.Add(new LSL_Vector(shape.FlexiForceX, // force
                            shape.FlexiForceY,
                            shape.FlexiForceZ));
                        break;

                    case ScriptBaseClass.PRIM_TEXGEN:
                        // (PRIM_TEXGEN_DEFAULT, PRIM_TEXGEN_PLANAR)
                        if (remain < 1)
                            return new LSL_List();

                        face = rules.GetLSLIntegerItem(idx++);

                        tex = part.Shape.Textures;
                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < nsides; face++)
                                if (tex.GetFace((uint)face).TexMapType == MappingType.Planar)
                                    res.Add(new LSL_Integer(ScriptBaseClass.PRIM_TEXGEN_PLANAR));
                                else
                                    res.Add(new LSL_Integer(ScriptBaseClass.PRIM_TEXGEN_DEFAULT));
                        }
                        else
                        {
                            if (tex.GetFace((uint)face).TexMapType == MappingType.Planar)
                                res.Add(new LSL_Integer(ScriptBaseClass.PRIM_TEXGEN_PLANAR));
                            else
                                res.Add(new LSL_Integer(ScriptBaseClass.PRIM_TEXGEN_DEFAULT));
                        }

                        break;

                    case ScriptBaseClass.PRIM_POINT_LIGHT:
                        shape = part.Shape;

                        if (shape.LightEntry)
                            res.Add(new LSL_Integer(1)); // active
                        else
                            res.Add(new LSL_Integer(0));
                        res.Add(new LSL_Vector(shape.LightColorR, // color
                            shape.LightColorG,
                            shape.LightColorB));
                        res.Add(new LSL_Float(shape.LightIntensity)); // intensity
                        res.Add(new LSL_Float(shape.LightRadius)); // radius
                        res.Add(new LSL_Float(shape.LightFalloff)); // falloff
                        break;

                    case ScriptBaseClass.PRIM_GLOW:
                        if (remain < 1)
                            return new LSL_List();

                        face = rules.GetLSLIntegerItem(idx++);

                        tex = part.Shape.Textures;
                        float primglow;
                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < nsides; face++)
                            {
                                primglow = tex.GetFace((uint)face).Glow;
                                res.Add(new LSL_Float(primglow));
                            }
                        }
                        else
                        {
                            primglow = tex.GetFace((uint)face).Glow;
                            res.Add(new LSL_Float(primglow));
                        }

                        break;

                    case ScriptBaseClass.PRIM_TEXT:
                        var textColor = part.GetTextColor();
                        res.Add(new LSL_String(part.Text));
                        res.Add(new LSL_Vector(textColor.R,
                            textColor.G,
                            textColor.B));
                        res.Add(new LSL_Float(textColor.A));
                        break;

                    case ScriptBaseClass.PRIM_NAME:
                        res.Add(new LSL_String(part.Name));
                        break;

                    case ScriptBaseClass.PRIM_DESC:
                        res.Add(new LSL_String(part.Description));
                        break;
                    case ScriptBaseClass.PRIM_ROT_LOCAL:
                        res.Add(new LSL_Rotation(part.RotationOffset));
                        break;

                    case ScriptBaseClass.PRIM_POS_LOCAL:
                        res.Add(new LSL_Vector(GetPartLocalPos(part)));
                        break;
                    case ScriptBaseClass.PRIM_SLICE:
                        var prim_type = part.GetPrimType();
                        var useProfileBeginEnd = prim_type == PrimType.SPHERE || prim_type == PrimType.TORUS ||
                                                 prim_type == PrimType.TUBE || prim_type == PrimType.RING;
                        res.Add(new LSL_Vector(
                            (useProfileBeginEnd ? part.Shape.ProfileBegin : part.Shape.PathBegin) / 50000.0,
                            1 - (useProfileBeginEnd ? part.Shape.ProfileEnd : part.Shape.PathEnd) / 50000.0,
                            0
                        ));
                        break;

                    case ScriptBaseClass.PRIM_OMEGA:
                        // this may return values diferent from SL since we don't handle set the same way
                        var gain = 1.0f; // we don't use gain and don't store it
                        var axis = part.AngularVelocity;
                        var spin = axis.Length();
                        if (spin < 1.0e-6)
                        {
                            axis = Vector3.Zero;
                            gain = 0.0f;
                            spin = 0.0f;
                        }
                        else
                        {
                            axis = axis * (1.0f / spin);
                        }

                        res.Add(new LSL_Vector(axis.X,
                            axis.Y,
                            axis.Z));
                        res.Add(new LSL_Float(spin));
                        res.Add(new LSL_Float(gain));
                        break;

                    case ScriptBaseClass.PRIM_SIT_TARGET:
                        if (part.IsSitTargetSet)
                        {
                            res.Add(new LSL_Integer(1));
                            res.Add(new LSL_Vector(part.SitTargetPosition));
                            res.Add(new LSL_Rotation(part.SitTargetOrientation));
                        }
                        else
                        {
                            res.Add(new LSL_Integer(0));
                            res.Add(new LSL_Vector(Vector3.Zero));
                            res.Add(new LSL_Rotation(Quaternion.Identity));
                        }

                        break;

                    case ScriptBaseClass.PRIM_NORMAL:
                    case ScriptBaseClass.PRIM_SPECULAR:
                    case ScriptBaseClass.PRIM_ALPHA_MODE:
                        if (remain < 1)
                            return new LSL_List();

                        face = rules.GetLSLIntegerItem(idx++);
                        tex = part.Shape.Textures;
                        if (face == ScriptBaseClass.ALL_SIDES)
                        {
                            for (face = 0; face < nsides; face++)
                            {
                                var texface = tex.GetFace((uint)face);
                                getLSLFaceMaterial(ref res, code, part, texface);
                            }
                        }
                        else
                        {
                            if (face >= 0 && face < nsides)
                            {
                                var texface = tex.GetFace((uint)face);
                                getLSLFaceMaterial(ref res, code, part, texface);
                            }
                        }

                        break;

                    case ScriptBaseClass.PRIM_LINK_TARGET:

                        // TODO: Should be issuing a runtime script warning in this case.
                        if (remain < 2)
                            return new LSL_List();

                        return rules.GetSublist(idx, -1);

                    case ScriptBaseClass.PRIM_PROJECTOR:
                        if (part.Shape.ProjectionEntry)
                        {
                            res.Add(new LSL_String(part.Shape.ProjectionTextureUUID.ToString()));
                            res.Add(new LSL_Float(part.Shape.ProjectionFOV));
                            res.Add(new LSL_Float(part.Shape.ProjectionFocus));
                            res.Add(new LSL_Float(part.Shape.ProjectionAmbiance));
                        }
                        else
                        {
                            res.Add(new LSL_String(ScriptBaseClass.NULL_KEY));
                            res.Add(new LSL_Float(0));
                            res.Add(new LSL_Float(0));
                            res.Add(new LSL_Float(0));
                        }

                        break;
                }
            }

            return new LSL_List();
        }

        private string GetMaterialTextureUUIDbyRights(UUID origID, SceneObjectPart part)
        {
            if (World.Permissions.CanEditObject(m_host.ParentGroup.UUID, m_host.ParentGroup.RootPart.OwnerID))
                return origID.ToString();

            lock (part.TaskInventory)
            {
                foreach (var inv in part.TaskInventory)
                    if (inv.Value.InvType == (int)InventoryType.Texture && inv.Value.AssetID.Equals(origID))
                        return origID.ToString();
            }

            return ScriptBaseClass.NULL_KEY;
        }

        private void getLSLFaceMaterial(ref LSL_List res, int code, SceneObjectPart part,
            Primitive.TextureEntryFace texface)
        {
            var matID = UUID.Zero;
            if (m_materialsModule != null)
                matID = texface.MaterialID;

            if (!matID.IsZero())
            {
                var mat = m_materialsModule.GetMaterial(matID);
                if (mat != null)
                {
                    if (code == ScriptBaseClass.PRIM_NORMAL)
                    {
                        res.Add(new LSL_String(GetMaterialTextureUUIDbyRights(mat.NormalMapID, part)));
                        res.Add(new LSL_Vector(mat.NormalRepeatX, mat.NormalRepeatY, 0));
                        res.Add(new LSL_Vector(mat.NormalOffsetX, mat.NormalOffsetY, 0));
                        res.Add(new LSL_Float(mat.NormalRotation));
                    }
                    else if (code == ScriptBaseClass.PRIM_SPECULAR)
                    {
                        const float colorScale = 1.0f / 255f;
                        res.Add(new LSL_String(GetMaterialTextureUUIDbyRights(mat.SpecularMapID, part)));
                        res.Add(new LSL_Vector(mat.SpecularRepeatX, mat.SpecularRepeatY, 0));
                        res.Add(new LSL_Vector(mat.SpecularOffsetX, mat.SpecularOffsetY, 0));
                        res.Add(new LSL_Float(mat.SpecularRotation));
                        res.Add(new LSL_Vector(mat.SpecularLightColorR * colorScale,
                            mat.SpecularLightColorG * colorScale,
                            mat.SpecularLightColorB * colorScale));
                        res.Add(new LSL_Integer(mat.SpecularLightExponent));
                        res.Add(new LSL_Integer(mat.EnvironmentIntensity));
                    }
                    else if (code == ScriptBaseClass.PRIM_ALPHA_MODE)
                    {
                        res.Add(new LSL_Integer(mat.DiffuseAlphaMode));
                        res.Add(new LSL_Integer(mat.AlphaMaskCutoff));
                    }

                    return;
                }
            }

            // material not found
            if (code == ScriptBaseClass.PRIM_NORMAL || code == ScriptBaseClass.PRIM_SPECULAR)
            {
                res.Add(new LSL_String(ScriptBaseClass.NULL_KEY));
                res.Add(new LSL_Vector(1.0, 1.0, 0));
                res.Add(new LSL_Vector(0, 0, 0));
                res.Add(new LSL_Float(0));

                if (code == ScriptBaseClass.PRIM_SPECULAR)
                {
                    res.Add(new LSL_Vector(1.0, 1.0, 1.0));
                    res.Add(new LSL_Integer(51));
                    res.Add(new LSL_Integer(0));
                }
            }
            else if (code == ScriptBaseClass.PRIM_ALPHA_MODE)
            {
                res.Add(new LSL_Integer(1));
                res.Add(new LSL_Integer(0));
            }
        }

        private LSL_List GetPrimMediaParams(SceneObjectPart part, int face, LSL_List rules)
        {
            // LSL Spec http://wiki.secondlife.com/wiki/LlGetPrimMediaParams says to fail silently if face is invalid
            // TODO: Need to correctly handle case where a face has no media (which gives back an empty list).
            // Assuming silently fail means give back an empty list.  Ideally, need to check this.
            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return new LSL_List();

            var module = m_ScriptEngine.World.RequestModuleInterface<IMoapModule>();
            if (null == module)
                return new LSL_List();

            var me = module.GetMediaEntry(part, face);

            // As per http://wiki.secondlife.com/wiki/LlGetPrimMediaParams
            if (null == me)
                return new LSL_List();

            var res = new LSL_List();

            for (var i = 0; i < rules.Length; i++)
            {
                int code = rules.GetLSLIntegerItem(i);

                switch (code)
                {
                    case ScriptBaseClass.PRIM_MEDIA_ALT_IMAGE_ENABLE:
                        // Not implemented
                        res.Add(new LSL_Integer(0));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_CONTROLS:
                        if (me.Controls == MediaControls.Standard)
                            res.Add(new LSL_Integer(ScriptBaseClass.PRIM_MEDIA_CONTROLS_STANDARD));
                        else
                            res.Add(new LSL_Integer(ScriptBaseClass.PRIM_MEDIA_CONTROLS_MINI));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_CURRENT_URL:
                        res.Add(new LSL_String(me.CurrentURL));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_HOME_URL:
                        res.Add(new LSL_String(me.HomeURL));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_LOOP:
                        res.Add(me.AutoLoop ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_PLAY:
                        res.Add(me.AutoPlay ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_SCALE:
                        res.Add(me.AutoScale ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_ZOOM:
                        res.Add(me.AutoZoom ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_FIRST_CLICK_INTERACT:
                        res.Add(me.InteractOnFirstClick ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WIDTH_PIXELS:
                        res.Add(new LSL_Integer(me.Width));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_HEIGHT_PIXELS:
                        res.Add(new LSL_Integer(me.Height));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WHITELIST_ENABLE:
                        res.Add(me.EnableWhiteList ? ScriptBaseClass.TRUE : ScriptBaseClass.FALSE);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WHITELIST:
                        var urls = (string[])me.WhiteList.Clone();

                        for (var j = 0; j < urls.Length; j++)
                            urls[j] = Uri.EscapeDataString(urls[j]);

                        res.Add(new LSL_String(string.Join(", ", urls)));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_PERMS_INTERACT:
                        res.Add(new LSL_Integer((int)me.InteractPermissions));
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_PERMS_CONTROL:
                        res.Add(new LSL_Integer((int)me.ControlPermissions));
                        break;

                    default: return ScriptBaseClass.LSL_STATUS_MALFORMED_PARAMS;
                }
            }

            return res;
        }

        private LSL_Integer SetPrimMediaParams(SceneObjectPart part, LSL_Integer face, LSL_List rules)
        {
            // LSL Spec http://wiki.secondlife.com/wiki/LlSetPrimMediaParams says to fail silently if face is invalid
            // Assuming silently fail means sending back LSL_STATUS_OK.  Ideally, need to check this.
            // Don't perform the media check directly
            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return ScriptBaseClass.LSL_STATUS_NOT_FOUND;

            var module = m_ScriptEngine.World.RequestModuleInterface<IMoapModule>();
            if (null == module)
                return ScriptBaseClass.LSL_STATUS_NOT_SUPPORTED;

            var me = module.GetMediaEntry(part, face);
            if (null == me)
                me = new MediaEntry();

            var i = 0;

            while (i < rules.Length - 1)
            {
                int code = rules.GetLSLIntegerItem(i++);

                switch (code)
                {
                    case ScriptBaseClass.PRIM_MEDIA_ALT_IMAGE_ENABLE:
                        me.EnableAlterntiveImage = rules.GetLSLIntegerItem(i++) != 0 ? true : false;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_CONTROLS:
                        int v = rules.GetLSLIntegerItem(i++);
                        if (ScriptBaseClass.PRIM_MEDIA_CONTROLS_STANDARD == v)
                            me.Controls = MediaControls.Standard;
                        else
                            me.Controls = MediaControls.Mini;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_CURRENT_URL:
                        me.CurrentURL = rules.GetStringItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_HOME_URL:
                        me.HomeURL = rules.GetStringItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_LOOP:
                        me.AutoLoop = ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_PLAY:
                        me.AutoPlay = ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_SCALE:
                        me.AutoScale = ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_AUTO_ZOOM:
                        me.AutoZoom = ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_FIRST_CLICK_INTERACT:
                        me.InteractOnFirstClick = ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WIDTH_PIXELS:
                        me.Width = rules.GetLSLIntegerItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_HEIGHT_PIXELS:
                        me.Height = rules.GetLSLIntegerItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WHITELIST_ENABLE:
                        me.EnableWhiteList = ScriptBaseClass.TRUE == rules.GetLSLIntegerItem(i++) ? true : false;
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_WHITELIST:
                        var rawWhiteListUrls = rules.GetStringItem(i++).Split(',');
                        var whiteListUrls = new List<string>();
                        Array.ForEach(
                            rawWhiteListUrls, delegate(string rawUrl) { whiteListUrls.Add(rawUrl.Trim()); });
                        me.WhiteList = whiteListUrls.ToArray();
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_PERMS_INTERACT:
                        me.InteractPermissions = (MediaPermission)(byte)(int)rules.GetLSLIntegerItem(i++);
                        break;

                    case ScriptBaseClass.PRIM_MEDIA_PERMS_CONTROL:
                        me.ControlPermissions = (MediaPermission)(byte)(int)rules.GetLSLIntegerItem(i++);
                        break;

                    default: return ScriptBaseClass.LSL_STATUS_MALFORMED_PARAMS;
                }
            }

            module.SetMediaEntry(part, face, me);

            return ScriptBaseClass.LSL_STATUS_OK;
        }

        private LSL_Integer ClearPrimMedia(SceneObjectPart part, LSL_Integer face)
        {
            // LSL Spec http://wiki.secondlife.com/wiki/LlClearPrimMedia says to fail silently if face is invalid
            // Assuming silently fail means sending back LSL_STATUS_OK.  Ideally, need to check this.
            // FIXME: Don't perform the media check directly
            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return ScriptBaseClass.LSL_STATUS_NOT_FOUND;

            var module = m_ScriptEngine.World.RequestModuleInterface<IMoapModule>();
            if (null == module)
                return ScriptBaseClass.LSL_STATUS_NOT_SUPPORTED;

            module.ClearMediaEntry(part, face);

            return ScriptBaseClass.LSL_STATUS_OK;
        }

        private LSL_List ParseString2List(string src, LSL_List separators, LSL_List spacers, bool keepNulls)
        {
            var srclen = src.Length;
            var seplen = separators.Length;
            var separray = separators.Data;
            var spclen = spacers.Length;
            var spcarray = spacers.Data;
            var dellen = 0;
            var delarray = new string[seplen + spclen];

            var outlen = 0;
            var outarray = new string[srclen * 2 + 1];

            int i, j;
            string d;


            /*
             * Convert separator and spacer lists to C# strings.
             * Also filter out null strings so we don't hang.
             */
            for (i = 0; i < seplen; i++)
            {
                d = separray[i].ToString();
                if (d.Length > 0) delarray[dellen++] = d;
            }

            seplen = dellen;

            for (i = 0; i < spclen; i++)
            {
                d = spcarray[i].ToString();
                if (d.Length > 0) delarray[dellen++] = d;
            }

            /*
             * Scan through source string from beginning to end.
             */
            for (i = 0;;)
            {
                /*
                 * Find earliest delimeter in src starting at i (if any).
                 */
                var earliestDel = -1;
                var earliestSrc = srclen;
                string earliestStr = null;
                for (j = 0; j < dellen; j++)
                {
                    d = delarray[j];
                    if (d != null)
                    {
                        var index = src.IndexOf(d, i);
                        if (index < 0)
                        {
                            delarray[j] = null; // delim nowhere in src, don't check it anymore
                        }
                        else if (index < earliestSrc)
                        {
                            earliestSrc = index; // where delimeter starts in source string
                            earliestDel = j; // where delimeter is in delarray[]
                            earliestStr = d; // the delimeter string from delarray[]
                            if (index == i) break; // can't do any better than found at beg of string
                        }
                    }
                }

                /*
                 * Output source string starting at i through start of earliest delimeter.
                 */
                if (keepNulls || earliestSrc > i) outarray[outlen++] = src.Substring(i, earliestSrc - i);

                /*
                 * If no delimeter found at or after i, we're done scanning.
                 */
                if (earliestDel < 0) break;

                /*
                 * If delimeter was a spacer, output the spacer.
                 */
                if (earliestDel >= seplen) outarray[outlen++] = earliestStr;

                /*
                 * Look at rest of src string following delimeter.
                 */
                i = earliestSrc + earliestStr.Length;
            }

            /*
             * Make up an exact-sized output array suitable for an LSL_List object.
             */
            var outlist = new object[outlen];
            for (i = 0; i < outlen; i++) outlist[i] = new LSL_String(outarray[i]);
            return new LSL_List(outlist);
        }

        private int PermissionMaskToLSLPerm(uint value)
        {
            value &= fullperms;
            if (value == fullperms)
                return ScriptBaseClass.PERM_ALL;
            if (value == 0)
                return 0;

            var ret = 0;

            if ((value & (uint)PermissionMask.Copy) != 0)
                ret |= ScriptBaseClass.PERM_COPY;

            if ((value & (uint)PermissionMask.Modify) != 0)
                ret |= ScriptBaseClass.PERM_MODIFY;

            if ((value & (uint)PermissionMask.Move) != 0)
                ret |= ScriptBaseClass.PERM_MOVE;

            if ((value & (uint)PermissionMask.Transfer) != 0)
                ret |= ScriptBaseClass.PERM_TRANSFER;

            return ret;
        }

        private uint LSLPermToPermissionMask(int lslperm, uint oldvalue)
        {
            lslperm &= ScriptBaseClass.PERM_ALL;
            if (lslperm == ScriptBaseClass.PERM_ALL)
                return oldvalue |= fullperms;

            oldvalue &= ~fullperms;
            if (lslperm != 0)
            {
                if ((lslperm & ScriptBaseClass.PERM_COPY) != 0)
                    oldvalue |= (uint)PermissionMask.Copy;

                if ((lslperm & ScriptBaseClass.PERM_MODIFY) != 0)
                    oldvalue |= (uint)PermissionMask.Modify;

                if ((lslperm & ScriptBaseClass.PERM_MOVE) != 0)
                    oldvalue |= (uint)PermissionMask.Move;

                if ((lslperm & ScriptBaseClass.PERM_TRANSFER) != 0)
                    oldvalue |= (uint)PermissionMask.Transfer;
            }

            return oldvalue;
        }

        private int fixedCopyTransfer(int value)
        {
            if ((value & (ScriptBaseClass.PERM_COPY | ScriptBaseClass.PERM_TRANSFER)) == 0)
                value |= ScriptBaseClass.PERM_TRANSFER;
            return value;
        }

        private string truncateBase64(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            var paddingPos = -1;
            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (c >= 'A' && c <= 'Z')
                    continue;
                if (c >= 'a' && c <= 'z')
                    continue;
                if (c >= '0' && c <= '9')
                    continue;
                if (c == '+' || c == '/')
                    continue;
                paddingPos = i;
                break;
            }

            if (paddingPos == 0)
                return string.Empty;

            if (paddingPos > 0)
                input = input.Substring(0, paddingPos);

            var remainder = input.Length % 4;
            switch (remainder)
            {
                case 0:
                    return input;
                case 1:
                    return input.Substring(0, input.Length - 1);
                case 2:
                    return input + "==";
            }

            return input + "=";
        }

        internal UUID GetScriptByName(string name)
        {
            var item = m_host.Inventory.GetInventoryItem(name);

            if (item == null || item.Type != 10)
                return UUID.Zero;

            return item.ItemID;
        }

        /// <summary>
        ///     Reports the script error in the viewer's Script Warning/Error dialog and shouts it on the debug channel.
        /// </summary>
        /// <param name="command">The name of the command that generated the error.</param>
        /// <param name="message">The error message to report to the user.</param>
        internal void Error(string command, string message)
        {
            var text = command + ": " + message;
            if (text.Length > 1023) text = text.Substring(0, 1023);

            World.SimChat(Utils.StringToBytes(text), ChatTypeEnum.DebugChannel, ScriptBaseClass.DEBUG_CHANNEL,
                m_host.ParentGroup.RootPart.AbsolutePosition, m_host.Name, m_host.UUID, false);

            var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            if (wComm != null)
                wComm.DeliverMessage(ChatTypeEnum.Shout, ScriptBaseClass.DEBUG_CHANNEL, m_host.Name, m_host.UUID, text);
            Sleep(1000);
        }

        /// <summary>
        ///     Reports that the command is not implemented as a script error.
        /// </summary>
        /// <param name="command">The name of the command that is not implemented.</param>
        /// <param name="message">Additional information to report to the user. (Optional)</param>
        internal void NotImplemented(string command, string message = "")
        {
            if (throwErrorOnNotImplemented)
            {
                if (message != "") message = " - " + message;

                throw new NotImplementedException("Command not implemented: " + command + message);
            }

            var text = "Command not implemented";
            if (message != "") text = text + " - " + message;

            Error(command, text);
        }

        /// <summary>
        ///     Reports that the command is deprecated as a script error.
        /// </summary>
        /// <param name="command">The name of the command that is deprecated.</param>
        /// <param name="message">Additional information to report to the user. (Optional)</param>
        internal void Deprecated(string command, string message = "")
        {
            var text = "Command deprecated";
            if (message != "") text = text + " - " + message;

            Error(command, text);
        }

        protected void WithNotecard(UUID assetID, AssetRequestCallback cb)
        {
            World.AssetService.Get(assetID.ToString(), this,
                delegate(string i, object sender, AssetBase a)
                {
                    UUID.TryParse(i, out var uuid);
                    cb(uuid, a);
                });
        }

        public void print(string str)
        {
            // yes, this is a real LSL function. See: http://wiki.secondlife.com/wiki/Print
            var ossl = (IOSSL_Api)m_ScriptEngine.GetApi(m_item.ItemID, "OSSL");
            if (ossl != null)
            {
                ossl.CheckThreatLevel(ThreatLevel.High, "print");
                m_log.Info("LSL print():" + str);
            }
        }

        private string Name2Username(string name)
        {
            var parts = name.Split(' ');
            if (parts.Length < 2)
                return name.ToLower();
            if (parts[1].Equals("Resident"))
                return parts[0].ToLower();

            return name.Replace(" ", ".").ToLower();
        }

        private bool InBoundingBox(ScenePresence avatar, Vector3 point)
        {
            var height = avatar.Appearance.AvatarHeight;
            var b1 = avatar.AbsolutePosition + new Vector3(-0.22f, -0.22f, -height / 2);
            var b2 = avatar.AbsolutePosition + new Vector3(0.22f, 0.22f, height / 2);

            if (point.X > b1.X && point.X < b2.X &&
                point.Y > b1.Y && point.Y < b2.Y &&
                point.Z > b1.Z && point.Z < b2.Z)
                return true;
            return false;
        }

        private ContactResult[] AvatarIntersection(Vector3 rayStart, Vector3 rayEnd, bool skipPhys)
        {
            var contacts = new List<ContactResult>();

            var ab = rayEnd - rayStart;
            var ablen = ab.Length();

            World.ForEachScenePresence(delegate(ScenePresence sp)
            {
                if (skipPhys && sp.PhysicsActor != null)
                    return;

                var ac = sp.AbsolutePosition - rayStart;

                double d = Math.Abs(Vector3.Mag(Vector3.Cross(ab, ac)) / ablen);

                if (d > 1.5)
                    return;

                double d2 = Vector3.Dot(Vector3.Negate(ab), ac);

                if (d2 > 0)
                    return;

                var dp = Math.Sqrt(Vector3.Mag(ac) * Vector3.Mag(ac) - d * d);
                var p = rayStart + Vector3.Divide(Vector3.Multiply(ab, (float)dp), Vector3.Mag(ab));

                if (!InBoundingBox(sp, p))
                    return;

                var result = new ContactResult
                {
                    ConsumerID = sp.LocalId,
                    Depth = Vector3.Distance(rayStart, p),
                    Normal = Vector3.Zero,
                    Pos = p
                };

                contacts.Add(result);
            });

            return contacts.ToArray();
        }

        private ContactResult[] ObjectIntersection(Vector3 rayStart, Vector3 rayEnd, bool includePhysical,
            bool includeNonPhysical, bool includePhantom)
        {
            var ray = new Ray(rayStart, Vector3.Normalize(rayEnd - rayStart));
            var contacts = new List<ContactResult>();

            var ab = rayEnd - rayStart;

            World.ForEachSOG(delegate(SceneObjectGroup group)
            {
                if (m_host.ParentGroup == group)
                    return;

                if (group.IsAttachment)
                    return;

                if (group.RootPart.PhysActor == null)
                {
                    if (!includePhantom)
                        return;
                }
                else
                {
                    if (group.RootPart.PhysActor.IsPhysical)
                    {
                        if (!includePhysical)
                            return;
                    }
                    else
                    {
                        if (!includeNonPhysical)
                            return;
                    }
                }

                // Find the radius ouside of which we don't even need to hit test
                var radius = 0.0f;
                group.GetAxisAlignedBoundingBoxRaw(out var minX, out var maxX, out var minY, out var maxY, out var minZ,
                    out var maxZ);

                if (Math.Abs(minX) > radius)
                    radius = Math.Abs(minX);
                if (Math.Abs(minY) > radius)
                    radius = Math.Abs(minY);
                if (Math.Abs(minZ) > radius)
                    radius = Math.Abs(minZ);
                if (Math.Abs(maxX) > radius)
                    radius = Math.Abs(maxX);
                if (Math.Abs(maxY) > radius)
                    radius = Math.Abs(maxY);
                if (Math.Abs(maxZ) > radius)
                    radius = Math.Abs(maxZ);
                radius = radius * 1.413f;
                var ac = group.AbsolutePosition - rayStart;
//                Vector3 bc = group.AbsolutePosition - rayEnd;

                double d = Math.Abs(Vector3.Mag(Vector3.Cross(ab, ac)) / Vector3.Distance(rayStart, rayEnd));

                // Too far off ray, don't bother
                if (d > radius)
                    return;

                // Behind ray, drop
                double d2 = Vector3.Dot(Vector3.Negate(ab), ac);
                if (d2 > 0)
                    return;

                ray = new Ray(rayStart, Vector3.Normalize(rayEnd - rayStart));
                var intersection = group.TestIntersection(ray, true, false);
                // Miss.
                if (!intersection.HitTF)
                    return;

                var b1 = group.AbsolutePosition + new Vector3(minX, minY, minZ);
                var b2 = group.AbsolutePosition + new Vector3(maxX, maxY, maxZ);
                //m_log.DebugFormat("[LLCASTRAY]: min<{0},{1},{2}>, max<{3},{4},{5}> = hitp<{6},{7},{8}>", b1.X,b1.Y,b1.Z,b2.X,b2.Y,b2.Z,intersection.ipoint.X,intersection.ipoint.Y,intersection.ipoint.Z);
                if (!(intersection.ipoint.X >= b1.X && intersection.ipoint.X <= b2.X &&
                      intersection.ipoint.Y >= b1.Y && intersection.ipoint.Y <= b2.Y &&
                      intersection.ipoint.Z >= b1.Z && intersection.ipoint.Z <= b2.Z))
                    return;

                var result = new ContactResult
                {
                    ConsumerID = group.LocalId,
                    //Depth = intersection.distance;
                    Normal = intersection.normal,
                    Pos = intersection.ipoint
                };
                result.Depth = Vector3.Mag(rayStart - result.Pos);

                contacts.Add(result);
            });

            return contacts.ToArray();
        }

        private ContactResult? GroundIntersection(Vector3 rayStart, Vector3 rayEnd)
        {
            var heightfield = World.Heightmap.GetDoubles();
            var contacts = new List<ContactResult>();

            var min = 2048.0;
            var max = 0.0;

            // Find the min and max of the heightfield
            for (var x = 0; x < World.Heightmap.Width; x++)
            for (var y = 0; y < World.Heightmap.Height; y++)
            {
                if (heightfield[x, y] > max)
                    max = heightfield[x, y];
                if (heightfield[x, y] < min)
                    min = heightfield[x, y];
            }


            // A ray extends past rayEnd, but doesn't go back before
            // rayStart. If the start is above the highest point of the ground
            // and the ray goes up, we can't hit the ground. Ever.
            if (rayStart.Z > max && rayEnd.Z >= rayStart.Z)
                return null;

            // Same for going down
            if (rayStart.Z < min && rayEnd.Z <= rayStart.Z)
                return null;

            var trilist = new List<Tri>();

            // Create our triangle list
            for (var x = 1; x < World.Heightmap.Width; x++)
            for (var y = 1; y < World.Heightmap.Height; y++)
            {
                var t1 = new Tri();
                var t2 = new Tri();

                var p1 = new Vector3(x - 1, y - 1, (float)heightfield[x - 1, y - 1]);
                var p2 = new Vector3(x, y - 1, (float)heightfield[x, y - 1]);
                var p3 = new Vector3(x, y, (float)heightfield[x, y]);
                var p4 = new Vector3(x - 1, y, (float)heightfield[x - 1, y]);

                t1.p1 = p1;
                t1.p2 = p2;
                t1.p3 = p3;

                t2.p1 = p3;
                t2.p2 = p4;
                t2.p3 = p1;

                trilist.Add(t1);
                trilist.Add(t2);
            }

            // Ray direction
            var rayDirection = rayEnd - rayStart;

            foreach (var t in trilist)
            {
                // Compute triangle plane normal and edges
                var u = t.p2 - t.p1;
                var v = t.p3 - t.p1;
                var n = Vector3.Cross(u, v);

                if (n.IsZero())
                    continue;

                var w0 = rayStart - t.p1;
                double a = -Vector3.Dot(n, w0);
                double b = Vector3.Dot(n, rayDirection);

                // Not intersecting the plane, or in plane (same thing)
                // Ignoring this MAY cause the ground to not be detected
                // sometimes
                if (Math.Abs(b) < 0.000001)
                    continue;

                var r = a / b;

                // ray points away from plane
                if (r < 0.0)
                    continue;

                var ip = rayStart + Vector3.Multiply(rayDirection, (float)r);

                var uu = Vector3.Dot(u, u);
                var uv = Vector3.Dot(u, v);
                var vv = Vector3.Dot(v, v);
                var w = ip - t.p1;
                var wu = Vector3.Dot(w, u);
                var wv = Vector3.Dot(w, v);
                var d = uv * uv - uu * vv;

                var cs = (uv * wv - vv * wu) / d;
                if (cs < 0 || cs > 1.0)
                    continue;
                var ct = (uv * wu - uu * wv) / d;
                if (ct < 0 || cs + ct > 1.0)
                    continue;

                // Add contact point
                var result = new ContactResult
                {
                    ConsumerID = 0,
                    Depth = Vector3.Distance(rayStart, ip),
                    Normal = n,
                    Pos = ip
                };

                contacts.Add(result);
            }

            if (contacts.Count == 0)
                return null;

            contacts.Sort(delegate(ContactResult a, ContactResult b) { return (int)(a.Depth - b.Depth); });

            return contacts[0];
        }
    }
}