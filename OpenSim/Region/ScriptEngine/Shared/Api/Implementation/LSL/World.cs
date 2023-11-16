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
using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
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

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        public void llStopLookAt()
        {
            m_host.StopLookAt();
        }


        public LSL_Integer llGetRegionAgentCount()
        {
            var count = 0;
            World.ForEachRootScenePresence(delegate { count++; });

            return new LSL_Integer(count);
        }

        public LSL_Vector llGetRegionCorner()
        {
            return new LSL_Vector(World.RegionInfo.WorldLocX, World.RegionInfo.WorldLocY, 0);
        }

        public LSL_Key llGetEnv(LSL_Key name)
        {
            string sname = name;
            sname = sname.ToLower();
            switch (sname)
            {
                case "agent_limit":
                    return World.RegionInfo.RegionSettings.AgentLimit.ToString();

                case "dynamic_pathfinding":
                    return "0";

                case "estate_id":
                    return World.RegionInfo.EstateSettings.EstateID.ToString();

                case "estate_name":
                    return World.RegionInfo.EstateSettings.EstateName;

                case "frame_number":
                    return World.Frame.ToString();

                case "region_cpu_ratio":
                    return "1";

                case "region_idle":
                    return "0";

                case "region_product_name":
                    if (World.RegionInfo.RegionType != string.Empty)
                        return World.RegionInfo.RegionType;
                    return "";

                case "region_product_sku":
                    return "OpenSim";

                case "region_start_time":
                    return World.UnixStartTime.ToString();

                case "region_up_time":
                    var time = Util.UnixTimeSinceEpoch() - World.UnixStartTime;
                    return time.ToString();

                case "sim_channel":
                    return "OpenSim";

                case "sim_version":
                    return World.GetSimulatorVersion();

                case "simulator_hostname":
                    var UrlModule = World.RequestModuleInterface<IUrlModule>();
                    return UrlModule.ExternalHostNameForLSL;

                case "region_max_prims":
                    return World.RegionInfo.ObjectCapacity.ToString();

                case "region_object_bonus":
                    return World.RegionInfo.RegionSettings.ObjectBonus.ToString();

                case "whisper_range":
                    return m_whisperdistance.ToString();

                case "chat_range":
                    return m_saydistance.ToString();

                case "shout_range":
                    return m_shoutdistance.ToString();

                default:
                    return "";
            }
        }

        public LSL_Integer llOverMyLand(string id)
        {
            if (UUID.TryParse(id, out var key) && key.IsNotZero())
                try
                {
                    var presence = World.GetScenePresence(key);
                    if (presence != null) // object is an avatar
                    {
                        if (m_host.OwnerID.Equals(World.LandChannel.GetLandObject(presence.AbsolutePosition).LandData
                                .OwnerID))
                            return 1;
                    }
                    else // object is not an avatar
                    {
                        var obj = World.GetSceneObjectPart(key);

                        if (obj != null &&
                            m_host.OwnerID.Equals(
                                World.LandChannel.GetLandObject(obj.AbsolutePosition).LandData.OwnerID))
                            return 1;
                    }
                }
                catch
                {
                }

            return 0;
        }

        public LSL_Key llGetLandOwnerAt(LSL_Vector pos)
        {
            var land = World.LandChannel.GetLandObject((float)pos.x, (float)pos.y);
            if (land == null)
                return ScriptBaseClass.NULL_KEY;
            return land.LandData.OwnerID.ToString();
        }

        /// <summary>
        ///     According to http://lslwiki.net/lslwiki/wakka.php?wakka=llGetAgentSize
        ///     only the height of avatars vary and that says:
        ///     Width (x) and depth (y) are constant. (0.45m and 0.6m respectively).
        /// </summary>
        public LSL_Vector llGetAgentSize(LSL_Key id)
        {
            if (!UUID.TryParse(id, out var avID) || avID.IsZero())
                return ScriptBaseClass.ZERO_VECTOR;

            var avatar = World.GetScenePresence(avID);
            if (avatar == null || avatar.IsChildAgent) // Fail if not in the same region
                return ScriptBaseClass.ZERO_VECTOR;

            return new LSL_Vector(avatar.Appearance.AvatarSize);
        }

        public LSL_Integer llSameGroup(string id)
        {
            if (!UUID.TryParse(id, out var uuid) || uuid.IsZero())
                return 0;

            // Check if it's a group key
            if (uuid.Equals(m_host.ParentGroup.RootPart.GroupID))
                return 1;

            // Handle object case
            var part = World.GetSceneObjectPart(uuid);
            if (part != null)
            {
                if (part.ParentGroup.IsAttachment)
                {
                    uuid = part.ParentGroup.AttachedAvatar;
                }
                else
                {
                    // This will handle both deed and non-deed and also the no
                    // group case
                    if (part.ParentGroup.RootPart.GroupID.Equals(m_host.ParentGroup.RootPart.GroupID))
                        return 1;
                    return 0;
                }
            }

            // Handle the case where id names an avatar
            var presence = World.GetScenePresence(uuid);
            if (presence != null)
            {
                if (presence.IsChildAgent)
                    return 0;

                var client = presence.ControllingClient;
                if (m_host.ParentGroup.RootPart.GroupID.Equals(client.ActiveGroupId))
                    return 1;

                return 0;
            }

            return 0;
        }

        public void llUnSit(string id)
        {
            if (!UUID.TryParse(id, out var key) || key.IsZero())
                return;

            var av = World.GetScenePresence(key);
            if (av == null)
                return;

            var sittingAvatars = m_host.ParentGroup.GetSittingAvatars();

            if (sittingAvatars.Contains(av))
            {
                av.StandUp();
            }
            else
            {
                // If the object owner also owns the parcel
                // or
                // if the land is group owned and the object is group owned by the same group
                // or
                // if the object is owned by a person with estate access.
                var parcel = World.LandChannel.GetLandObject(av.AbsolutePosition);
                if (parcel != null)
                    if (m_host.OwnerID.Equals(parcel.LandData.OwnerID) ||
                        (m_host.OwnerID.Equals(m_host.GroupID) && m_host.GroupID.Equals(parcel.LandData.GroupID)
                                                               && parcel.LandData.IsGroupOwned) ||
                        World.Permissions.IsGod(m_host.OwnerID))
                        av.StandUp();
            }
        }


        public void llGroundRepel(double height, int water, double tau)
        {
            if (m_host.PhysActor != null)
            {
                var ground = (float)llGround(new LSL_Vector(0, 0, 0));
                var waterLevel = (float)llWater(new LSL_Vector(0, 0, 0));
                var hoverType = PIDHoverType.Ground;
                if (water != 0)
                {
                    hoverType = PIDHoverType.GroundAndWater;
                    if (ground < waterLevel)
                        height += waterLevel;
                    else
                        height += ground;
                }
                else
                {
                    height += ground;
                }

                m_host.SetHoverHeight((float)height, hoverType, (float)tau);
            }
        }

        public void llVolumeDetect(int detect)
        {
            if (!m_host.ParentGroup.IsDeleted)
                m_host.ParentGroup.ScriptSetVolumeDetect(detect != 0);
        }

        public void llSetObjectPermMask(int mask, int value)
        {
            if (!m_AllowGodFunctions || !World.Permissions.IsAdministrator(m_host.OwnerID))
                return;

            // not even admins have right to violate basic rules
            if (mask != ScriptBaseClass.MASK_BASE)
            {
                mask &= PermissionMaskToLSLPerm(m_host.BaseMask);
                if (mask != ScriptBaseClass.MASK_OWNER)
                    mask &= PermissionMaskToLSLPerm(m_host.OwnerMask);
            }

            switch (mask)
            {
                case ScriptBaseClass.MASK_BASE:
                    value = fixedCopyTransfer(value);
                    m_host.BaseMask = LSLPermToPermissionMask(value, m_host.BaseMask);
                    break;

                case ScriptBaseClass.MASK_OWNER:
                    value = fixedCopyTransfer(value);
                    m_host.OwnerMask = LSLPermToPermissionMask(value, m_host.OwnerMask);
                    break;

                case ScriptBaseClass.MASK_GROUP:
                    m_host.GroupMask = LSLPermToPermissionMask(value, m_host.GroupMask);
                    break;

                case ScriptBaseClass.MASK_EVERYONE:
                    m_host.EveryoneMask = LSLPermToPermissionMask(value, m_host.EveryoneMask);
                    break;

                case ScriptBaseClass.MASK_NEXT:
                    value = fixedCopyTransfer(value);
                    m_host.NextOwnerMask = LSLPermToPermissionMask(value, m_host.NextOwnerMask);
                    break;
                default:
                    return;
            }

            m_host.ParentGroup.AggregatePerms();
        }

        public void llSetInventoryPermMask(string itemName, int mask, int value)
        {
            if (!m_AllowGodFunctions || !World.Permissions.IsAdministrator(m_host.OwnerID))
                return;

            var item = m_host.Inventory.GetInventoryItem(itemName);

            if (item != null)
            {
                if (mask != ScriptBaseClass.MASK_BASE)
                {
                    mask &= PermissionMaskToLSLPerm(item.BasePermissions);
                    if (mask != ScriptBaseClass.MASK_OWNER)
                        mask &= PermissionMaskToLSLPerm(item.CurrentPermissions);
                }

                /*
                if(item.Type == (int)(AssetType.Settings))
                    value |= ScriptBaseClass.PERM_COPY;
                */

                switch (mask)
                {
                    case ScriptBaseClass.MASK_BASE:
                        value = fixedCopyTransfer(value);
                        item.BasePermissions = LSLPermToPermissionMask(value, item.BasePermissions);
                        break;
                    case ScriptBaseClass.MASK_OWNER:
                        value = fixedCopyTransfer(value);
                        item.CurrentPermissions = LSLPermToPermissionMask(value, item.CurrentPermissions);
                        break;
                    case ScriptBaseClass.MASK_GROUP:
                        item.GroupPermissions = LSLPermToPermissionMask(value, item.GroupPermissions);
                        break;
                    case ScriptBaseClass.MASK_EVERYONE:
                        item.EveryonePermissions = LSLPermToPermissionMask(value, item.EveryonePermissions);
                        break;
                    case ScriptBaseClass.MASK_NEXT:
                        value = fixedCopyTransfer(value);
                        item.NextPermissions = LSLPermToPermissionMask(value, item.NextPermissions);
                        break;
                    default:
                        return;
                }

                m_host.ParentGroup.InvalidateDeepEffectivePerms();
                m_host.ParentGroup.AggregatePerms();
            }
        }

        public void llRemoveFromLandPassList(string avatar)
        {
            if (!UUID.TryParse(avatar, out var key) || key.IsZero())
                return;

            var land = World.LandChannel.GetLandObject(m_host.AbsolutePosition);
            if (World.Permissions.CanEditParcelProperties(m_host.OwnerID, land, GroupPowers.LandManagePasses, false))
            {
                var idx = land.LandData.ParcelAccessList.FindIndex(
                    delegate(LandAccessEntry e)
                    {
                        if (e.Flags == AccessList.Access && e.AgentID.Equals(key))
                            return true;
                        return false;
                    });

                if (idx != -1)
                {
                    land.LandData.ParcelAccessList.RemoveAt(idx);
                    World.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
                }
            }

            ScriptSleep(m_sleepMsOnRemoveFromLandPassList);
        }

        public void llRemoveFromLandBanList(string avatar)
        {
            if (!UUID.TryParse(avatar, out var key) || key.IsZero())
                return;

            var land = World.LandChannel.GetLandObject(m_host.AbsolutePosition);
            if (World.Permissions.CanEditParcelProperties(m_host.OwnerID, land, GroupPowers.LandManageBanned, false))
            {
                var idx = land.LandData.ParcelAccessList.FindIndex(
                    delegate(LandAccessEntry e)
                    {
                        if (e.Flags == AccessList.Ban && e.AgentID.Equals(key))
                            return true;
                        return false;
                    });

                if (idx != -1)
                {
                    land.LandData.ParcelAccessList.RemoveAt(idx);
                    World.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
                }
            }

            ScriptSleep(m_sleepMsOnRemoveFromLandBanList);
        }

        public void llResetLandBanList()
        {
            var land = World.LandChannel.GetLandObject(m_host.AbsolutePosition).LandData;
            if (land.ParcelAccessList.Count > 0 && land.OwnerID.Equals(m_host.OwnerID))
            {
                var todelete = new List<LandAccessEntry>();
                foreach (var entry in land.ParcelAccessList)
                    if (entry.Flags == AccessList.Ban)
                        todelete.Add(entry);
                foreach (var entry in todelete)
                    land.ParcelAccessList.Remove(entry);
            }

            ScriptSleep(m_sleepMsOnResetLandBanList);
        }

        public void llResetLandPassList()
        {
            var land = World.LandChannel.GetLandObject(m_host.AbsolutePosition).LandData;
            if (land.ParcelAccessList.Count > 0 && land.OwnerID.Equals(m_host.OwnerID))
            {
                var todelete = new List<LandAccessEntry>();
                foreach (var entry in land.ParcelAccessList)
                    if (entry.Flags == AccessList.Access)
                        todelete.Add(entry);
                foreach (var entry in todelete)
                    land.ParcelAccessList.Remove(entry);
            }

            ScriptSleep(m_sleepMsOnResetLandPassList);
        }

        public void llRezAtRoot(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param)
        {
            doObjectRez(inventory, pos, vel, rot, param, true);
        }


        public void llRezObject(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param)
        {
            doObjectRez(inventory, pos, vel, rot, param, false);
        }


        public void llLookAt(LSL_Vector target, double strength, double damping)
        {
            // Get the normalized vector to the target
            var from = llGetPos();

            // normalized direction to target
            var dir = llVecNorm(target - from);

            // use vertical to help compute left axis
//            LSL_Vector up = new LSL_Vector(0.0, 0.0, 1.0);
            // find normalized left axis parallel to horizon
//            LSL_Vector left = llVecNorm(LSL_Vector.Cross(up, dir));

            var left = new LSL_Vector(-dir.y, dir.x, 0.0f);
            left = llVecNorm(left);
            // make up orthogonal to left and dir
            var up = LSL_Vector.Cross(dir, left);

            // compute rotation based on orthogonal axes
            // and rotate so Z points to target with X below horizont
            var rot = new LSL_Rotation(0.0, 0.707107, 0.0, 0.707107) * llAxes2Rot(dir, left, up);

            var sog = m_host.ParentGroup;
            if (sog == null || sog.IsDeleted)
                return;

            if (!sog.UsesPhysics || sog.IsAttachment)
            {
                // Do nothing if either value is 0 (this has been checked in SL)
                if (strength <= 0.0 || damping <= 0.0)
                    return;

                llSetLocalRot(rot);
            }
            else
            {
                if (strength == 0)
                {
                    llSetLocalRot(rot);
                    return;
                }

                sog.StartLookAt(rot, (float)strength, (float)damping);
            }
        }


        public void llCollisionFilter(LSL_String name, LSL_Key id, LSL_Integer accept)
        {
            UUID.TryParse(id, out var objectID);
            if (objectID.IsZero())
                m_host.SetCollisionFilter(accept != 0, name.m_string.ToLower(CultureInfo.InvariantCulture),
                    string.Empty);
            else
                m_host.SetCollisionFilter(accept != 0, name.m_string.ToLower(CultureInfo.InvariantCulture),
                    objectID.ToString());
        }

        public LSL_String llGetDate()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd");
        }

        public LSL_Integer llEdgeOfWorld(LSL_Vector pos, LSL_Vector dir)
        {
            if (dir.x == 0 && dir.y == 0)
                return 1; // SL wiki

            float rsx = World.RegionInfo.RegionSizeX;
            float rsy = World.RegionInfo.RegionSizeY;

            // can understand what sl does if position is not in region, so do something :)
            var px = (float)Util.Clamp(pos.x, 0.5, rsx - 0.5);
            var py = (float)Util.Clamp(pos.y, 0.5, rsy - 0.5);

            float ex, ey;

            if (dir.x == 0)
            {
                ex = px;
                ey = dir.y > 0 ? rsy + 1.0f : -1.0f;
            }
            else if (dir.y == 0)
            {
                ex = dir.x > 0 ? rsx + 1.0f : -1.0f;
                ey = py;
            }
            else
            {
                var dx = (float)dir.x;
                var dy = (float)dir.y;

                var t1 = dx * dx + dy * dy;
                t1 = (float)Math.Sqrt(t1);
                dx /= t1;
                dy /= t1;

                if (dx > 0)
                    t1 = (rsx + 1f - px) / dx;
                else
                    t1 = -(px + 1f) / dx;

                float t2;
                if (dy > 0)
                    t2 = (rsy + 1f - py) / dy;
                else
                    t2 = -(py + 1f) / dy;

                if (t1 > t2)
                    t1 = t2;

                ex = px + t1 * dx;
                ey = py + t1 * dy;
            }

            ex += World.RegionInfo.WorldLocX;
            ey += World.RegionInfo.WorldLocY;

            if (World.GridService.GetRegionByPosition(RegionScopeID, (int)ex, (int)ey) != null)
                return 0;
            return 1;
        }

        public LSL_Integer llGetAgentInfo(LSL_Key id)
        {
            if (!UUID.TryParse(id, out var key) || key.IsZero()) return 0;

            var agent = World.GetScenePresence(key);
            if (agent == null) return 0;

            if (agent.IsChildAgent || agent.IsDeleted)
                return 0; // Fail if they are not in the same region

            var flags = 0;
            try
            {
                // note: in OpenSim, sitting seems to cancel AGENT_ALWAYS_RUN, unlike SL
                if (agent.SetAlwaysRun) flags |= ScriptBaseClass.AGENT_ALWAYS_RUN;

                if (agent.HasAttachments())
                {
                    flags |= ScriptBaseClass.AGENT_ATTACHMENTS;
                    if (agent.HasScriptedAttachments())
                        flags |= ScriptBaseClass.AGENT_SCRIPTED;
                }

                if ((agent.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0)
                {
                    flags |= ScriptBaseClass.AGENT_FLYING;
                    flags |= ScriptBaseClass
                        .AGENT_IN_AIR; // flying always implies in-air, even if colliding with e.g. a wall
                }

                if ((agent.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AWAY) != 0)
                    flags |= ScriptBaseClass.AGENT_AWAY;

                if (agent.Animator.HasAnimation(busyAnimation)) flags |= ScriptBaseClass.AGENT_BUSY;

                // seems to get unset, even if in mouselook, when avatar is sitting on a prim???
                if ((agent.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0)
                    flags |= ScriptBaseClass.AGENT_MOUSELOOK;

                if ((agent.State & (byte)AgentState.Typing) != 0) flags |= ScriptBaseClass.AGENT_TYPING;

                var agentMovementAnimation = agent.Animator.CurrentMovementAnimation;

                if (agentMovementAnimation == "CROUCH")
                    flags |= ScriptBaseClass.AGENT_CROUCHING;
                else if (agentMovementAnimation == "WALK" || agentMovementAnimation == "CROUCHWALK")
                    flags |= ScriptBaseClass.AGENT_WALKING;

                // not colliding implies in air. Note: flying also implies in-air, even if colliding (see above)

                // note: AGENT_IN_AIR and AGENT_WALKING seem to be mutually exclusive states in SL.

                // note: this may need some tweaking when walking downhill. you "fall down" for a brief instant
                // and don't collide when walking downhill, which instantly registers as in-air, briefly. should
                // there be some minimum non-collision threshold time before claiming the avatar is in-air?
                if ((flags & ScriptBaseClass.AGENT_WALKING) == 0 && !agent.IsColliding)
                    flags |= ScriptBaseClass.AGENT_IN_AIR;

                if (agent.ParentPart != null)
                {
                    flags |= ScriptBaseClass.AGENT_ON_OBJECT;
                    flags |= ScriptBaseClass.AGENT_SITTING;
                }

                if (agent.Animator.Animations.ImplicitDefaultAnimation.AnimID.Equals(
                        DefaultAvatarAnimations.AnimsUUIDbyName["SIT_GROUND_CONSTRAINED"]))
                    flags |= ScriptBaseClass.AGENT_SITTING;

                if (agent.Appearance.VisualParams[(int)AvatarAppearance.VPElement.SHAPE_MALE] > 0)
                    flags |= ScriptBaseClass.AGENT_MALE;
            }
            catch
            {
                return 0;
            }

            return flags;
        }

        public LSL_String llGetAgentLanguage(LSL_Key id)
        {
            // This should only return a value if the avatar is in the same region, but eh. idc.
            if (World.AgentPreferencesService == null)
            {
                Error("llGetAgentLanguage", "No AgentPreferencesService present");
            }
            else
            {
                if (UUID.TryParse(id, out var key) && key.IsNotZero())
                    return new LSL_String(World.AgentPreferencesService.GetLang(key));
            }

            return new LSL_String("en-us");
        }

        /// <summary>
        ///     http://wiki.secondlife.com/wiki/LlGetAgentList
        ///     The list of options is currently not used in SL
        ///     scope is one of:-
        ///     AGENT_LIST_REGION - all in the region
        ///     AGENT_LIST_PARCEL - all in the same parcel as the scripted object
        ///     AGENT_LIST_PARCEL_OWNER - all in any parcel owned by the owner of the
        ///     current parcel.
        ///     AGENT_LIST_EXCLUDENPC ignore NPCs (bit mask)
        /// </summary>
        public LSL_List llGetAgentList(LSL_Integer scope, LSL_List options)
        {
            // do our bit masks part
            var noNPC = (scope & ScriptBaseClass.AGENT_LIST_EXCLUDENPC) != 0;

            // remove bit masks part
            scope &= ~ ScriptBaseClass.AGENT_LIST_EXCLUDENPC;

            // the constants are 1, 2 and 4 so bits are being set, but you
            // get an error "INVALID_SCOPE" if it is anything but 1, 2 and 4
            bool regionWide = scope == ScriptBaseClass.AGENT_LIST_REGION;
            bool parcelOwned = scope == ScriptBaseClass.AGENT_LIST_PARCEL_OWNER;
            bool parcel = scope == ScriptBaseClass.AGENT_LIST_PARCEL;

            var result = new LSL_List();

            if (!regionWide && !parcelOwned && !parcel)
            {
                result.Add("INVALID_SCOPE");
                return result;
            }

            ILandObject land;
            var id = UUID.Zero;

            if (parcel || parcelOwned)
            {
                land = World.LandChannel.GetLandObject(m_host.ParentGroup.RootPart.GetWorldPosition());
                if (land == null)
                {
                    id = UUID.Zero;
                }
                else
                {
                    if (parcelOwned)
                        id = land.LandData.OwnerID;
                    else
                        id = land.LandData.GlobalID;
                }
            }

            World.ForEachRootScenePresence(
                delegate(ScenePresence ssp)
                {
                    if (noNPC && ssp.IsNPC)
                        return;

                    // Gods are not listed in SL
                    if (!ssp.IsDeleted && !ssp.IsViewerUIGod && !ssp.IsChildAgent)
                    {
                        if (!regionWide)
                        {
                            land = World.LandChannel.GetLandObject(ssp.AbsolutePosition);
                            if (land != null)
                                if ((parcelOwned && land.LandData.OwnerID.Equals(id)) ||
                                    (parcel && land.LandData.GlobalID.Equals(id)))
                                    result.Add(new LSL_Key(ssp.UUID.ToString()));
                        }
                        else
                        {
                            result.Add(new LSL_Key(ssp.UUID.ToString()));
                        }
                    }

                    // Maximum of 100 results
                    if (result.Length > 99) return;
                }
            );
            return result;
        }

        public void llEjectFromLand(LSL_Key pest)
        {
            if (UUID.TryParse(pest, out var agentID) && agentID.IsNotZero())
            {
                var presence = World.GetScenePresence(agentID);
                if (presence != null)
                {
                    // agent must be over the owners land
                    var land = World.LandChannel.GetLandObject(presence.AbsolutePosition);
                    if (land == null)
                        return;

                    if (m_host.OwnerID.Equals(land.LandData.OwnerID))
                    {
                        var p = World.GetNearestAllowedPosition(presence, land);
                        presence.TeleportOnEject(p);
                        presence.ControllingClient.SendAlertMessage("You have been ejected from this land");
                    }
                }
            }

            ScriptSleep(m_sleepMsOnEjectFromLand);
        }

        public LSL_Vector llGroundSlope(LSL_Vector offset)
        {
            //Get the slope normal.  This gives us the equation of the plane tangent to the slope.
            LSL_Vector vsn = llGroundNormal(offset);

            //Plug the x,y coordinates of the slope normal into the equation of the plane to get
            //the height of that point on the plane.  The resulting vector gives the slope.
            Vector3 vsl = vsn;
            vsl.Z = (float)(((vsn.x * vsn.x) + (vsn.y * vsn.y)) / (-1 * vsn.z));
            vsl.Normalize();
            //Normalization might be overkill here

            vsn.x = vsl.X;
            vsn.y = vsl.Y;
            vsn.z = vsl.Z;

            return vsn;
        }

        public LSL_Vector llGroundNormal(LSL_Vector offset)
        {
            var pos = m_host.GetWorldPosition() + (Vector3)offset;
            // Clamp to valid position
            if (pos.X < 0)
                pos.X = 0;
            else if (pos.X >= World.Heightmap.Width)
                pos.X = World.Heightmap.Width - 1;
            if (pos.Y < 0)
                pos.Y = 0;
            else if (pos.Y >= World.Heightmap.Height)
                pos.Y = World.Heightmap.Height - 1;

            //Find two points in addition to the position to define a plane
            var p0 = new Vector3(pos.X, pos.Y,
                World.Heightmap[(int)pos.X, (int)pos.Y]);
            var p1 = new Vector3();
            var p2 = new Vector3();
            if (pos.X + 1.0f >= World.Heightmap.Width)
                p1 = new Vector3(pos.X + 1.0f, pos.Y,
                    World.Heightmap[(int)pos.X, (int)pos.Y]);
            else
                p1 = new Vector3(pos.X + 1.0f, pos.Y,
                    World.Heightmap[(int)(pos.X + 1.0f), (int)pos.Y]);
            if (pos.Y + 1.0f >= World.Heightmap.Height)
                p2 = new Vector3(pos.X, pos.Y + 1.0f,
                    World.Heightmap[(int)pos.X, (int)pos.Y]);
            else
                p2 = new Vector3(pos.X, pos.Y + 1.0f,
                    World.Heightmap[(int)pos.X, (int)(pos.Y + 1.0f)]);

            //Find normalized vectors from p0 to p1 and p0 to p2
            var v0 = new Vector3(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z);
            var v1 = new Vector3(p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z);
            v0.Normalize();
            v1.Normalize();

            //Find the cross product of the vectors (the slope normal).
            var vsn = new Vector3
            {
                X = v0.Y * v1.Z - v0.Z * v1.Y,
                Y = v0.Z * v1.X - v0.X * v1.Z,
                Z = v0.X * v1.Y - v0.Y * v1.X
            };
            vsn.Normalize();
            //I believe the crossproduct of two normalized vectors is a normalized vector so
            //this normalization may be overkill

            return new LSL_Vector(vsn);
        }

        public LSL_Vector llGroundContour(LSL_Vector offset)
        {
            var x = llGroundSlope(offset);
            return new LSL_Vector(-x.y, x.x, 0.0);
        }


        public virtual LSL_Integer llGetFreeMemory()
        {
            // Make scripts designed for Mono happy
            return 65536;
        }


        public LSL_String llGetRegionName()
        {
            return m_regionName;
        }

        public LSL_Float llGetRegionTimeDilation()
        {
            return (double)World.TimeDilation;
        }

        /// <summary>
        ///     Returns the value reported in the client Statistics window
        /// </summary>
        public LSL_Float llGetRegionFPS()
        {
            return World.StatsReporter.LastReportedSimFPS;
        }


        public void llAddToLandPassList(LSL_Key avatar, LSL_Float hours)
        {
            if (!UUID.TryParse(avatar, out var key) || key.IsZero())
                return;

            var land = World.LandChannel.GetLandObject(m_host.AbsolutePosition);
            if (World.Permissions.CanEditParcelProperties(m_host.OwnerID, land, GroupPowers.LandManagePasses, false))
            {
                LandAccessEntry entry;

                var expires = hours != 0 ? Util.UnixTimeSinceEpoch() + (int)(3600.0 * hours) : 0;
                var idx = land.LandData.ParcelAccessList.FindIndex(
                    delegate(LandAccessEntry e)
                    {
                        if (e.Flags == AccessList.Access && e.AgentID.Equals(key))
                            return true;
                        return false;
                    });

                if (idx != -1)
                {
                    entry = land.LandData.ParcelAccessList[idx];
                    if (entry.Expires == 0)
                        return;
                    if (expires != 0 && expires < entry.Expires)
                        return;

                    entry.Expires = expires;
                    World.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
                    return;
                }

                entry = new LandAccessEntry
                {
                    AgentID = key,
                    Flags = AccessList.Access,
                    Expires = expires
                };

                land.LandData.ParcelAccessList.Add(entry);
                World.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
            }

            ScriptSleep(m_sleepMsOnAddToLandPassList);
        }

        public LSL_Integer llScriptDanger(LSL_Vector pos)
        {
            return World.LSLScriptDanger(m_host, pos) ? 1 : 0;
        }

        public LSL_List llGetAnimationList(LSL_Key id)
        {
            if (!UUID.TryParse(id, out var avID) || avID.IsZero())
                return new LSL_List();

            var av = World.GetScenePresence(avID);
            if (av == null || av.IsChildAgent) // only if in the region
                return new LSL_List();

            var anims = av.Animator.GetAnimationArray();
            var l = new LSL_List();
            foreach (var foo in anims)
                l.Add(new LSL_Key(foo.ToString()));
            return l;
        }

        public LSL_Integer llGetObjectPermMask(int mask)
        {
            switch (mask)
            {
                case ScriptBaseClass.MASK_BASE:
                    return PermissionMaskToLSLPerm(m_host.BaseMask);

                case ScriptBaseClass.MASK_OWNER:
                    return PermissionMaskToLSLPerm(m_host.OwnerMask);

                case ScriptBaseClass.MASK_GROUP:
                    return PermissionMaskToLSLPerm(m_host.GroupMask);

                case ScriptBaseClass.MASK_EVERYONE:
                    return PermissionMaskToLSLPerm(m_host.EveryoneMask);

                case ScriptBaseClass.MASK_NEXT:
                    return PermissionMaskToLSLPerm(m_host.NextOwnerMask);
            }

            return -1;
        }

        public LSL_Integer llGetInventoryPermMask(string itemName, int mask)
        {
            var item = m_host.Inventory.GetInventoryItem(itemName);

            if (item == null)
                return -1;

            switch (mask)
            {
                case ScriptBaseClass.MASK_BASE:
                    return PermissionMaskToLSLPerm(item.BasePermissions);
                case ScriptBaseClass.MASK_OWNER:
                    return PermissionMaskToLSLPerm(item.CurrentPermissions);
                case ScriptBaseClass.MASK_GROUP:
                    return PermissionMaskToLSLPerm(item.GroupPermissions);
                case ScriptBaseClass.MASK_EVERYONE:
                    return PermissionMaskToLSLPerm(item.EveryonePermissions);
                case ScriptBaseClass.MASK_NEXT:
                    return PermissionMaskToLSLPerm(item.NextPermissions);
            }

            return -1;
        }

        public LSL_Key llGetInventoryCreator(string itemName)
        {
            var item = m_host.Inventory.GetInventoryItem(itemName);

            if (item == null)
            {
                Error("llGetInventoryCreator", "Can't find item '" + itemName + "'");
                return string.Empty;
            }

            return item.CreatorID.ToString();
        }

        public LSL_String llGetInventoryAcquireTime(string itemName)
        {
            var item = m_host.Inventory.GetInventoryItem(itemName);

            if (item == null)
            {
                Error("llGetInventoryAcquireTime", "Can't find item '" + itemName + "'");
                return string.Empty;
            }

            var date = Util.ToDateTime(item.CreationDate);
            return date.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }


        public LSL_Integer llGetInventoryType(string name)
        {
            var item = m_host.Inventory.GetInventoryItem(name);

            if (item == null)
                return -1;

            return item.Type;
        }

        public void llMapDestination(string simname, LSL_Vector pos, LSL_Vector lookAt)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, 0);
            if (detectedParams == null)
            {
                if (m_host.ParentGroup.IsAttachment)
                    detectedParams = new DetectParams
                    {
                        Key = m_host.OwnerID
                    };
                else
                    return;
            }

            var avatar = World.GetScenePresence(detectedParams.Key);
            if (avatar != null)
                avatar.ControllingClient.SendScriptTeleportRequest(m_host.Name,
                    simname, pos, lookAt);
            ScriptSleep(m_sleepMsOnMapDestination);
        }

        public void llAddToLandBanList(LSL_Key avatar, LSL_Float hours)
        {
            if (!UUID.TryParse(avatar, out var key) || key.IsZero())
                return;

            var land = World.LandChannel.GetLandObject(m_host.AbsolutePosition);
            if (World.Permissions.CanEditParcelProperties(m_host.OwnerID, land, GroupPowers.LandManageBanned, false))
            {
                LandAccessEntry entry;
                var expires = hours != 0 ? Util.UnixTimeSinceEpoch() + (int)(3600.0 * hours) : 0;

                var idx = land.LandData.ParcelAccessList.FindIndex(
                    delegate(LandAccessEntry e)
                    {
                        if (e.Flags == AccessList.Ban && e.AgentID.Equals(key))
                            return true;
                        return false;
                    });

                if (idx != -1)
                {
                    entry = land.LandData.ParcelAccessList[idx];
                    if (entry.Expires == 0)
                        return;
                    if (expires != 0 && expires < entry.Expires)
                        return;

                    entry.Expires = expires;
                    World.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
                    return;
                }

                entry = new LandAccessEntry
                {
                    AgentID = key,
                    Flags = AccessList.Ban,
                    Expires = expires
                };

                land.LandData.ParcelAccessList.Add(entry);

                World.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
            }

            ScriptSleep(m_sleepMsOnAddToLandBanList);
        }

        public LSL_Float llListStatistics(int operation, LSL_List src)
        {
            switch (operation)
            {
                case ScriptBaseClass.LIST_STAT_RANGE:
                    return src.Range();
                case ScriptBaseClass.LIST_STAT_MIN:
                    return src.Min();
                case ScriptBaseClass.LIST_STAT_MAX:
                    return src.Max();
                case ScriptBaseClass.LIST_STAT_MEAN:
                    return src.Mean();
                case ScriptBaseClass.LIST_STAT_MEDIAN:
                    return LSL_List.ToDoubleList(src).Median();
                case ScriptBaseClass.LIST_STAT_NUM_COUNT:
                    return src.NumericLength();
                case ScriptBaseClass.LIST_STAT_STD_DEV:
                    return src.StdDev();
                case ScriptBaseClass.LIST_STAT_SUM:
                    return src.Sum();
                case ScriptBaseClass.LIST_STAT_SUM_SQUARES:
                    return src.SumSqrs();
                case ScriptBaseClass.LIST_STAT_GEOMETRIC_MEAN:
                    return src.GeometricMean();
                case ScriptBaseClass.LIST_STAT_HARMONIC_MEAN:
                    return src.HarmonicMean();
                default:
                    return 0.0;
            }
        }

        public LSL_Integer llGetUnixTime()
        {
            return Util.UnixTimeSinceEpoch();
        }

        public LSL_Integer llGetParcelFlags(LSL_Vector pos)
        {
            return (int)World.LandChannel.GetLandObject((float)pos.x, (float)pos.y).LandData.Flags;
        }

        public LSL_Integer llGetRegionFlags()
        {
            var estate = World.RequestModuleInterface<IEstateModule>();
            if (estate == null)
                return 67108864;
            return (int)estate.GetRegionFlags();
        }

        public LSL_Integer llGetParcelPrimCount(LSL_Vector pos, int category, int sim_wide)
        {
            var lo = World.LandChannel.GetLandObject((float)pos.x, (float)pos.y);

            if (lo == null)
                return 0;

            var pc = lo.PrimCounts;

            if (sim_wide != ScriptBaseClass.FALSE)
            {
                if (category == ScriptBaseClass.PARCEL_COUNT_TOTAL)
                    return pc.Simulator;
                // counts not implemented yet
                return 0;
            }

            if (category == ScriptBaseClass.PARCEL_COUNT_TOTAL)
                return pc.Total;
            if (category == ScriptBaseClass.PARCEL_COUNT_OWNER)
                return pc.Owner;
            if (category == ScriptBaseClass.PARCEL_COUNT_GROUP)
                return pc.Group;
            if (category == ScriptBaseClass.PARCEL_COUNT_OTHER)
                return pc.Others;
            if (category == ScriptBaseClass.PARCEL_COUNT_SELECTED)
                return pc.Selected;
            if (category == ScriptBaseClass.PARCEL_COUNT_TEMP)
                return 0; // counts not implemented yet

            return 0;
        }

        public LSL_List llGetParcelPrimOwners(LSL_Vector pos)
        {
            var land = (LandObject)World.LandChannel.GetLandObject((float)pos.x, (float)pos.y);
            var ret = new LSL_List();
            if (land != null)
                foreach (var detectedParams in land.GetLandObjectOwners())
                {
                    ret.Add(new LSL_String(detectedParams.Key.ToString()));
                    ret.Add(new LSL_Integer(detectedParams.Value));
                }

            ScriptSleep(m_sleepMsOnGetParcelPrimOwners);
            return ret;
        }

        public LSL_Integer llGetObjectPrimCount(LSL_Key object_id)
        {
            if (!UUID.TryParse(object_id, out var id) || id.IsZero())
                return 0;

            var part = World.GetSceneObjectPart(id);
            if (part == null)
                return 0;

            return part.ParentGroup.PrimCount;
        }

        public LSL_Integer llGetParcelMaxPrims(LSL_Vector pos, int sim_wide)
        {
            var lo = World.LandChannel.GetLandObject((float)pos.x, (float)pos.y);

            if (lo == null)
                return 0;

            if (sim_wide != 0)
                return lo.GetSimulatorMaxPrimCount();
            return lo.GetParcelMaxPrimCount();
        }

        public LSL_List llGetParcelDetails(LSL_Vector pos, LSL_List param)
        {
            var parcel = World.LandChannel.GetLandObject(pos);
            if (parcel == null) return new LSL_List(0);

            var land = parcel.LandData;
            if (land == null) return new LSL_List(0);

            var ret = new LSL_List();
            foreach (var o in param.Data)
                switch (o.ToString())
                {
                    case "0":
                        ret.Add(new LSL_String(land.Name));
                        break;
                    case "1":
                        ret.Add(new LSL_String(land.Description));
                        break;
                    case "2":
                        ret.Add(new LSL_Key(land.OwnerID.ToString()));
                        break;
                    case "3":
                        ret.Add(new LSL_Key(land.GroupID.ToString()));
                        break;
                    case "4":
                        ret.Add(new LSL_Integer(land.Area));
                        break;
                    case "5":
                        ret.Add(new LSL_Key(land.GlobalID.ToString()));
                        break;
                    case "6":
                        ret.Add(new LSL_Integer(land.SeeAVs ? 1 : 0));
                        break;
                    case "7":
                        ret.Add(new LSL_Integer(parcel.GetParcelMaxPrimCount()));
                        break;
                    case "8":
                        ret.Add(new LSL_Integer(parcel.PrimCounts.Total));
                        break;
                    case "9":
                        ret.Add(new LSL_Vector(land.UserLocation));
                        break;
                    case "10":
                        ret.Add(new LSL_Vector(land.UserLookAt));
                        break;
                    case "11":
                        ret.Add(new LSL_Integer(land.LandingType));
                        break;
                    case "12":
                        ret.Add(new LSL_Integer(land.Flags));
                        break;
                    case "13":
                        ret.Add(new LSL_Integer(World.LSLScriptDanger(m_host, pos) ? 1 : 0));
                        break;
                    case "64":
                        ret.Add(new LSL_Integer(land.Dwell));
                        break;
                    case "65":
                        ret.Add(new LSL_Integer(land.ClaimDate));
                        break;
                    default:
                        ret.Add(new LSL_Integer(0));
                        break;
                }

            return ret;
        }

        public LSL_List llGetObjectDetails(LSL_Key id, LSL_List args)
        {
            var ret = new LSL_List();
            if (!UUID.TryParse(id, out var key) || key.IsZero())
                return ret;

            var count = 0;
            var av = World.GetScenePresence(key);
            if (av != null)
            {
                List<SceneObjectGroup> Attachments = null;
                int? nAnimated = null;
                foreach (var o in args.Data)
                    switch (int.Parse(o.ToString()))
                    {
                        case ScriptBaseClass.OBJECT_NAME:
                            ret.Add(new LSL_String(av.Firstname + " " + av.Lastname));
                            break;
                        case ScriptBaseClass.OBJECT_DESC:
                            ret.Add(new LSL_String(""));
                            break;
                        case ScriptBaseClass.OBJECT_POS:
                            Vector3 avpos;

                            if (av.ParentID != 0 && av.ParentPart != null &&
                                av.ParentPart.ParentGroup != null && av.ParentPart.ParentGroup.RootPart != null)
                            {
                                avpos = av.OffsetPosition;

                                if (!av.LegacySitOffsets)
                                {
                                    var sitOffset = Zrot(av.Rotation) * (av.Appearance.AvatarHeight * 0.02638f * 2.0f);
                                    avpos -= sitOffset;
                                }

                                var sitRoot = av.ParentPart.ParentGroup.RootPart;
                                avpos = sitRoot.GetWorldPosition() + avpos * sitRoot.GetWorldRotation();
                            }
                            else
                            {
                                avpos = av.AbsolutePosition;
                            }

                            ret.Add(new LSL_Vector(avpos.X, avpos.Y, avpos.Z));
                            break;
                        case ScriptBaseClass.OBJECT_ROT:
                            var avrot = av.GetWorldRotation();
                            ret.Add(new LSL_Rotation(avrot));
                            break;
                        case ScriptBaseClass.OBJECT_VELOCITY:
                            var avvel = av.GetWorldVelocity();
                            ret.Add(new LSL_Vector(avvel.X, avvel.Y, avvel.Z));
                            break;
                        case ScriptBaseClass.OBJECT_OWNER:
                            ret.Add(new LSL_Key((string)id));
                            break;
                        case ScriptBaseClass.OBJECT_GROUP:
                            ret.Add(new LSL_String(ScriptBaseClass.NULL_KEY));
                            break;
                        case ScriptBaseClass.OBJECT_CREATOR:
                            ret.Add(new LSL_Key(ScriptBaseClass.NULL_KEY));
                            break;
                        // For the following 8 see the Object version below
                        case ScriptBaseClass.OBJECT_RUNNING_SCRIPT_COUNT:
                            ret.Add(new LSL_Integer(av.RunningScriptCount()));
                            break;
                        case ScriptBaseClass.OBJECT_TOTAL_SCRIPT_COUNT:
                            ret.Add(new LSL_Integer(av.ScriptCount()));
                            break;
                        case ScriptBaseClass.OBJECT_SCRIPT_MEMORY:
                            ret.Add(new LSL_Integer(av.RunningScriptCount() * 16384));
                            break;
                        case ScriptBaseClass.OBJECT_SCRIPT_TIME:
                            ret.Add(new LSL_Float(av.ScriptExecutionTime() / 1000.0f));
                            break;
                        case ScriptBaseClass.OBJECT_PRIM_EQUIVALENCE:
                            ret.Add(new LSL_Integer(1));
                            break;
                        case ScriptBaseClass.OBJECT_SERVER_COST:
                            ret.Add(new LSL_Float(0));
                            break;
                        case ScriptBaseClass.OBJECT_STREAMING_COST:
                            ret.Add(new LSL_Float(0));
                            break;
                        case ScriptBaseClass.OBJECT_PHYSICS_COST:
                            ret.Add(new LSL_Float(0));
                            break;
                        case ScriptBaseClass.OBJECT_CHARACTER_TIME: // Pathfinding
                            ret.Add(new LSL_Float(0));
                            break;
                        case ScriptBaseClass.OBJECT_ROOT:
                            var p = av.ParentPart;
                            if (p != null)
                                ret.Add(new LSL_String(p.ParentGroup.RootPart.UUID.ToString()));
                            else
                                ret.Add(new LSL_Key((string)id));
                            break;
                        case ScriptBaseClass.OBJECT_ATTACHED_POINT:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_PATHFINDING_TYPE: // Pathfinding
                            ret.Add(new LSL_Integer(ScriptBaseClass.OPT_AVATAR));
                            break;
                        case ScriptBaseClass.OBJECT_PHYSICS:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_PHANTOM:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_TEMP_ON_REZ:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_RENDER_WEIGHT:
                            ret.Add(new LSL_Integer(-1));
                            break;
                        case ScriptBaseClass.OBJECT_HOVER_HEIGHT:
                            ret.Add(new LSL_Float(0));
                            break;
                        case ScriptBaseClass.OBJECT_BODY_SHAPE_TYPE:
                            LSL_Float shapeType;
                            if (av.Appearance.VisualParams[(int)AvatarAppearance.VPElement.SHAPE_MALE] != 0)
                                shapeType = new LSL_Float(1);
                            else
                                shapeType = new LSL_Float(0);
                            ret.Add(shapeType);
                            break;
                        case ScriptBaseClass.OBJECT_LAST_OWNER_ID:
                            ret.Add(new LSL_Key(ScriptBaseClass.NULL_KEY));
                            break;
                        case ScriptBaseClass.OBJECT_CLICK_ACTION:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_OMEGA:
                            ret.Add(new LSL_Vector(Vector3.Zero));
                            break;
                        case ScriptBaseClass.OBJECT_PRIM_COUNT:
                            if (Attachments == null)
                                Attachments = av.GetAttachments();
                            count = 0;
                            try
                            {
                                foreach (var Attachment in Attachments)
                                    count += Attachment.PrimCount;
                            }
                            catch
                            {
                            }

                            ;
                            ret.Add(new LSL_Integer(count));
                            break;
                        case ScriptBaseClass.OBJECT_TOTAL_INVENTORY_COUNT:
                            if (Attachments == null)
                                Attachments = av.GetAttachments();
                            count = 0;
                            try
                            {
                                foreach (var Attachment in Attachments)
                                {
                                    var parts = Attachment.Parts;
                                    for (var i = 0; i < parts.Length; i++)
                                        count += parts[i].Inventory.Count;
                                }
                            }
                            catch
                            {
                            }

                            ;
                            ret.Add(new LSL_Integer(count));
                            break;
                        case ScriptBaseClass.OBJECT_REZZER_KEY:
                            ret.Add(new LSL_Key((string)id));
                            break;
                        case ScriptBaseClass.OBJECT_GROUP_TAG:
                            ret.Add(new LSL_String(av.Grouptitle));
                            break;
                        case ScriptBaseClass.OBJECT_TEMP_ATTACHED:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_ATTACHED_SLOTS_AVAILABLE:
                            ret.Add(new LSL_Integer(Constants.MaxAgentAttachments - av.GetAttachmentsCount()));
                            break;
                        case ScriptBaseClass.OBJECT_CREATION_TIME:
                            ret.Add(new LSL_String(""));
                            break;
                        case ScriptBaseClass.OBJECT_SELECT_COUNT:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_SIT_COUNT:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_ANIMATED_COUNT:
                            count = 0;
                            if (nAnimated.HasValue)
                            {
                                count = nAnimated.Value;
                            }
                            else
                            {
                                if (Attachments == null)
                                    Attachments = av.GetAttachments();
                                try
                                {
                                    for (var i = 0; i < Attachments.Count; ++i)
                                        if (Attachments[i].RootPart.Shape.MeshFlagEntry)
                                            ++count;
                                }
                                catch
                                {
                                }

                                ;
                                nAnimated = count;
                            }

                            ret.Add(new LSL_Integer(count));
                            break;

                        case ScriptBaseClass.OBJECT_ANIMATED_SLOTS_AVAILABLE:
                            count = 0;
                            if (nAnimated.HasValue)
                            {
                                count = nAnimated.Value;
                            }
                            else
                            {
                                if (Attachments == null)
                                    Attachments = av.GetAttachments();
                                count = 0;
                                try
                                {
                                    for (var i = 0; i < Attachments.Count; ++i)
                                        if (Attachments[i].RootPart.Shape.MeshFlagEntry)
                                            ++count;
                                }
                                catch
                                {
                                }

                                ;
                                nAnimated = count;
                            }

                            count = 2 - count; // for now hardcoded max (simulator features, viewers settings, etc)
                            if (count < 0)
                                count = 0;
                            ret.Add(new LSL_Integer(count));
                            break;

                        case ScriptBaseClass.OBJECT_ACCOUNT_LEVEL:
                            ret.Add(new LSL_Integer(1));
                            break;
                        case ScriptBaseClass.OBJECT_MATERIAL:
                            ret.Add(new LSL_Integer((int)Material.Flesh));
                            break;
                        case ScriptBaseClass.OBJECT_MASS:
                            ret.Add(new LSL_Float(av.GetMass()));
                            break;
                        case ScriptBaseClass.OBJECT_TEXT:
                            ret.Add(new LSL_String(""));
                            break;
                        case ScriptBaseClass.OBJECT_REZ_TIME:
                            ret.Add(new LSL_String(""));
                            break;
                        case ScriptBaseClass.OBJECT_LINK_NUMBER:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_SCALE:
                            ret.Add(new LSL_Vector(av.Appearance.AvatarBoxSize));
                            break;
                        case ScriptBaseClass.OBJECT_TEXT_COLOR:
                            ret.Add(new LSL_Vector(0f, 0f, 0f));
                            break;
                        case ScriptBaseClass.OBJECT_TEXT_ALPHA:
                            ret.Add(new LSL_Float(1.0f));
                            break;
                        default:
                            // Invalid or unhandled constant.
                            ret.Add(new LSL_Integer(ScriptBaseClass.OBJECT_UNKNOWN_DETAIL));
                            break;
                    }

                return ret;
            }

            var obj = World.GetSceneObjectPart(key);
            if (obj != null)
                foreach (var o in args.Data)
                    switch (int.Parse(o.ToString()))
                    {
                        case ScriptBaseClass.OBJECT_NAME:
                            ret.Add(new LSL_String(obj.Name));
                            break;
                        case ScriptBaseClass.OBJECT_DESC:
                            ret.Add(new LSL_String(obj.Description));
                            break;
                        case ScriptBaseClass.OBJECT_POS:
                            ret.Add(new LSL_Vector(obj.AbsolutePosition));
                            break;
                        case ScriptBaseClass.OBJECT_ROT:
                            Quaternion rot;

                            if (obj.ParentGroup.IsAttachment)
                            {
                                var sp = World.GetScenePresence(obj.ParentGroup.AttachedAvatar);
                                rot = sp != null ? sp.GetWorldRotation() : Quaternion.Identity;
                            }
                            else
                            {
                                if (obj.ParentGroup.RootPart.LocalId == obj.LocalId)
                                    rot = obj.ParentGroup.GroupRotation;
                                else
                                    rot = obj.GetWorldRotation();
                            }

                            var objrot = new LSL_Rotation(rot);
                            ret.Add(objrot);

                            break;
                        case ScriptBaseClass.OBJECT_VELOCITY:
                            Vector3 vel;

                            if (obj.ParentGroup.IsAttachment)
                            {
                                var sp = World.GetScenePresence(obj.ParentGroup.AttachedAvatar);
                                vel = sp != null ? sp.GetWorldVelocity() : Vector3.Zero;
                            }
                            else
                            {
                                vel = obj.Velocity;
                            }

                            ret.Add(new LSL_Vector(vel));
                            break;
                        case ScriptBaseClass.OBJECT_OWNER:
                            ret.Add(new LSL_String(obj.OwnerID.ToString()));
                            break;
                        case ScriptBaseClass.OBJECT_GROUP:
                            ret.Add(new LSL_String(obj.GroupID.ToString()));
                            break;
                        case ScriptBaseClass.OBJECT_CREATOR:
                            ret.Add(new LSL_String(obj.CreatorID.ToString()));
                            break;
                        case ScriptBaseClass.OBJECT_RUNNING_SCRIPT_COUNT:
                            ret.Add(new LSL_Integer(obj.ParentGroup.RunningScriptCount()));
                            break;
                        case ScriptBaseClass.OBJECT_TOTAL_SCRIPT_COUNT:
                            ret.Add(new LSL_Integer(obj.ParentGroup.ScriptCount()));
                            break;
                        case ScriptBaseClass.OBJECT_SCRIPT_MEMORY:
                            // The value returned in SL for mono scripts is 65536 * number of active scripts
                            // and 16384 * number of active scripts for LSO. since llGetFreememory
                            // is coded to give the LSO value use it here
                            ret.Add(new LSL_Integer(obj.ParentGroup.RunningScriptCount() * 16384));
                            break;
                        case ScriptBaseClass.OBJECT_SCRIPT_TIME:
                            // Average cpu time in seconds per simulator frame expended on all scripts in the object
                            ret.Add(new LSL_Float(obj.ParentGroup.ScriptExecutionTime() / 1000.0f));
                            break;
                        case ScriptBaseClass.OBJECT_PRIM_EQUIVALENCE:
                            // according to the SL wiki A prim or linkset will have prim
                            // equivalent of the number of prims in a linkset if it does not
                            // contain a mesh anywhere in the link set or is not a normal prim
                            // The value returned in SL for normal prims is prim count
                            ret.Add(new LSL_Integer(obj.ParentGroup.PrimCount));
                            break;

                        // costs below may need to be diferent for root parts, need to check
                        case ScriptBaseClass.OBJECT_SERVER_COST:
                            // The linden calculation is here
                            // http://wiki.secondlife.com/wiki/Mesh/Mesh_Server_Weight
                            // The value returned in SL for normal prims looks like the prim count
                            ret.Add(new LSL_Float(0));
                            break;
                        case ScriptBaseClass.OBJECT_STREAMING_COST:
                            // The value returned in SL for normal prims is prim count * 0.06
                            ret.Add(new LSL_Float(obj.StreamingCost));
                            break;
                        case ScriptBaseClass.OBJECT_PHYSICS_COST:
                            // The value returned in SL for normal prims is prim count
                            ret.Add(new LSL_Float(obj.PhysicsCost));
                            break;
                        case ScriptBaseClass.OBJECT_CHARACTER_TIME: // Pathfinding
                            ret.Add(new LSL_Float(0));
                            break;
                        case ScriptBaseClass.OBJECT_ROOT:
                            ret.Add(new LSL_String(obj.ParentGroup.RootPart.UUID.ToString()));
                            break;
                        case ScriptBaseClass.OBJECT_ATTACHED_POINT:
                            ret.Add(new LSL_Integer(obj.ParentGroup.AttachmentPoint));
                            break;
                        case ScriptBaseClass.OBJECT_PATHFINDING_TYPE:
                            var pcode = obj.Shape.PCode;
                            if (obj.ParentGroup.AttachmentPoint != 0
                                || pcode == (byte)PCode.Grass
                                || pcode == (byte)PCode.Tree
                                || pcode == (byte)PCode.NewTree)
                                ret.Add(new LSL_Integer(ScriptBaseClass.OPT_OTHER));
                            else
                                ret.Add(new LSL_Integer(ScriptBaseClass.OPT_LEGACY_LINKSET));
                            break;
                        case ScriptBaseClass.OBJECT_PHYSICS:
                            if (obj.ParentGroup.AttachmentPoint != 0)
                                ret.Add(new LSL_Integer(0)); // Always false if attached
                            else
                                ret.Add(new LSL_Integer(obj.ParentGroup.UsesPhysics ? 1 : 0));
                            break;
                        case ScriptBaseClass.OBJECT_PHANTOM:
                            if (obj.ParentGroup.AttachmentPoint != 0)
                                ret.Add(new LSL_Integer(0)); // Always false if attached
                            else
                                ret.Add(new LSL_Integer(obj.ParentGroup.IsPhantom ? 1 : 0));
                            break;
                        case ScriptBaseClass.OBJECT_TEMP_ON_REZ:
                            ret.Add(new LSL_Integer(obj.ParentGroup.IsTemporary ? 1 : 0));
                            break;
                        case ScriptBaseClass.OBJECT_RENDER_WEIGHT:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_HOVER_HEIGHT:
                            ret.Add(new LSL_Float(0));
                            break;
                        case ScriptBaseClass.OBJECT_BODY_SHAPE_TYPE:
                            ret.Add(new LSL_Float(-1));
                            break;
                        case ScriptBaseClass.OBJECT_LAST_OWNER_ID:
                            ret.Add(new LSL_Key(obj.ParentGroup.LastOwnerID.ToString()));
                            break;
                        case ScriptBaseClass.OBJECT_CLICK_ACTION:
                            ret.Add(new LSL_Integer(obj.ClickAction));
                            break;
                        case ScriptBaseClass.OBJECT_OMEGA:
                            ret.Add(new LSL_Vector(obj.AngularVelocity));
                            break;
                        case ScriptBaseClass.OBJECT_PRIM_COUNT:
                            ret.Add(new LSL_Integer(obj.ParentGroup.PrimCount));
                            break;
                        case ScriptBaseClass.OBJECT_TOTAL_INVENTORY_COUNT:
                            var parts = obj.ParentGroup.Parts;
                            count = 0;
                            for (var i = 0; i < parts.Length; i++)
                                count += parts[i].Inventory.Count;
                            ret.Add(new LSL_Integer(count));
                            break;
                        case ScriptBaseClass.OBJECT_REZZER_KEY:
                            ret.Add(new LSL_Key(obj.ParentGroup.RezzerID.ToString()));
                            break;
                        case ScriptBaseClass.OBJECT_GROUP_TAG:
                            ret.Add(new LSL_String(string.Empty));
                            break;
                        case ScriptBaseClass.OBJECT_TEMP_ATTACHED:
                            if (obj.ParentGroup.AttachmentPoint != 0 && obj.ParentGroup.FromItemID.IsZero())
                                ret.Add(new LSL_Integer(1));
                            else
                                ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_ATTACHED_SLOTS_AVAILABLE:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_CREATION_TIME:
                            var date = Util.ToDateTime(obj.ParentGroup.RootPart.CreationDate);
                            ret.Add(new LSL_String(date.ToString("yyyy-MM-ddTHH:mm:ssZ",
                                CultureInfo.InvariantCulture)));
                            break;
                        case ScriptBaseClass.OBJECT_SELECT_COUNT:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_SIT_COUNT:
                            ret.Add(new LSL_Integer(obj.ParentGroup.GetSittingAvatarsCount()));
                            break;
                        case ScriptBaseClass.OBJECT_ANIMATED_COUNT:
                            if (obj.ParentGroup.RootPart.Shape.MeshFlagEntry)
                                ret.Add(new LSL_Integer(1));
                            else
                                ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_ANIMATED_SLOTS_AVAILABLE:
                            ret.Add(new LSL_Integer(0));
                            break;
                        case ScriptBaseClass.OBJECT_ACCOUNT_LEVEL:
                            ret.Add(new LSL_Integer(1));
                            break;
                        case ScriptBaseClass.OBJECT_MATERIAL:
                            ret.Add(new LSL_Integer(obj.Material));
                            break;
                        case ScriptBaseClass.OBJECT_MASS:
                            float mass;
                            if (obj.ParentGroup.IsAttachment)
                            {
                                var attachedAvatar = World.GetScenePresence(obj.ParentGroup.AttachedAvatar);
                                mass = attachedAvatar is null ? 0 : attachedAvatar.GetMass();
                            }
                            else
                            {
                                mass = obj.ParentGroup.GetMass();
                            }

                            mass *= 100f;
                            ret.Add(new LSL_Float(mass));
                            break;
                        case ScriptBaseClass.OBJECT_TEXT:
                            ret.Add(new LSL_String(obj.Text));
                            break;
                        case ScriptBaseClass.OBJECT_REZ_TIME:
                            ret.Add(new LSL_String(obj.Rezzed.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ",
                                CultureInfo.InvariantCulture)));
                            break;
                        case ScriptBaseClass.OBJECT_LINK_NUMBER:
                            ret.Add(new LSL_Integer(obj.LinkNum));
                            break;
                        case ScriptBaseClass.OBJECT_SCALE:
                            ret.Add(new LSL_Vector(obj.Scale));
                            break;
                        case ScriptBaseClass.OBJECT_TEXT_COLOR:
                            var textColor = obj.GetTextColor();
                            ret.Add(new LSL_Vector(textColor.R, textColor.G, textColor.B));
                            break;
                        case ScriptBaseClass.OBJECT_TEXT_ALPHA:
                            ret.Add(new LSL_Float(obj.GetTextColor().A));
                            break;
                        default:
                            // Invalid or unhandled constant.
                            ret.Add(new LSL_Integer(ScriptBaseClass.OBJECT_UNKNOWN_DETAIL));
                            break;
                    }

            return ret;
        }

        public LSL_Key llGetNumberOfNotecardLines(string name)
        {
            if (!UUID.TryParse(name, out var assetID))
            {
                var item = m_host.Inventory.GetInventoryItem(name);

                if (item != null && item.Type == 7)
                    assetID = item.AssetID;
            }

            if (assetID.IsZero())
            {
                // => complain loudly, as specified by the LSL docs
                Error("llGetNumberOfNotecardLines", "Can't find notecard '" + name + "'");
                return ScriptBaseClass.NULL_KEY;
            }

            if (NotecardCache.IsCached(assetID))
            {
                var ftid = m_AsyncCommands.DataserverPlugin.RequestWithImediatePost(m_host.LocalId, m_item.ItemID,
                    NotecardCache.GetLines(assetID).ToString());
                ScriptSleep(m_sleepMsOnGetNumberOfNotecardLines);
                return ftid;
            }

            Action<string> act = eventID =>
            {
                if (NotecardCache.IsCached(assetID))
                {
                    m_AsyncCommands.DataserverPlugin.DataserverReply(eventID,
                        NotecardCache.GetLines(assetID).ToString());
                    return;
                }

                var a = World.AssetService.Get(assetID.ToString());
                if (a == null || a.Type != 7)
                {
                    m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, string.Empty);
                    return;
                }

                NotecardCache.Cache(assetID, a.Data);
                m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, NotecardCache.GetLines(assetID).ToString());
            };

            var tid = m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId, m_item.ItemID, act);
            ScriptSleep(m_sleepMsOnGetNumberOfNotecardLines);
            return tid.ToString();
        }

        public LSL_Key llGetNotecardLine(string name, int line)
        {
            if (!UUID.TryParse(name, out var assetID))
            {
                var item = m_host.Inventory.GetInventoryItem(name);

                if (item != null && item.Type == 7)
                    assetID = item.AssetID;
            }

            if (assetID.IsZero())
            {
                // => complain loudly, as specified by the LSL docs
                Error("llGetNotecardLine", "Can't find notecard '" + name + "'");
                return ScriptBaseClass.NULL_KEY;
            }

            if (NotecardCache.IsCached(assetID))
            {
                var eid = m_AsyncCommands.DataserverPlugin.RequestWithImediatePost(m_host.LocalId, m_item.ItemID,
                    NotecardCache.GetLine(assetID, line, m_notecardLineReadCharsMax));

                ScriptSleep(m_sleepMsOnGetNotecardLine);
                return eid;
            }

            Action<string> act = eventID =>
            {
                if (NotecardCache.IsCached(assetID))
                {
                    m_AsyncCommands.DataserverPlugin.DataserverReply(eventID,
                        NotecardCache.GetLine(assetID, line, m_notecardLineReadCharsMax));
                    return;
                }

                var a = World.AssetService.Get(assetID.ToString());
                if (a == null || a.Type != 7)
                {
                    m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, string.Empty);
                    return;
                }

                NotecardCache.Cache(assetID, a.Data);
                m_AsyncCommands.DataserverPlugin.DataserverReply(
                    eventID, NotecardCache.GetLine(assetID, line, m_notecardLineReadCharsMax));
            };

            var tid = m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId, m_item.ItemID, act);
            ScriptSleep(m_sleepMsOnGetNotecardLine);
            return tid.ToString();
        }

        public LSL_String llGetUsername(LSL_Key id)
        {
            return Name2Username(llKey2Name(id));
        }

        public LSL_Key llRequestUsername(LSL_Key id)
        {
            if (!UUID.TryParse(id, out var key) || key.IsZero())
                return string.Empty;

            var lpresence = World.GetScenePresence(key);
            if (lpresence != null)
            {
                var lname = lpresence.Name;
                var ftid = m_AsyncCommands.DataserverPlugin.RequestWithImediatePost(m_host.LocalId,
                    m_item.ItemID, Name2Username(lname));
                return ftid;
            }

            Action<string> act = eventID =>
            {
                var name = string.Empty;
                var presence = World.GetScenePresence(key);
                if (presence != null)
                {
                    name = presence.Name;
                }
                else if (World.TryGetSceneObjectPart(key, out var sop) && sop != null)
                {
                    name = sop.Name;
                }
                else
                {
                    var account = m_userAccountService.GetUserAccount(RegionScopeID, key);
                    if (account != null) name = account.FirstName + " " + account.LastName;
                }

                m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, Name2Username(name));
            };

            var rq = m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId, m_item.ItemID, act);
            ScriptSleep(m_sleepMsOnRequestAgentData);
            return rq.ToString();
        }

        public LSL_String llGetDisplayName(LSL_Key id)
        {
            if (UUID.TryParse(id, out var key) && key.IsNotZero())
            {
                var presence = World.GetScenePresence(key);
                if (presence != null) return presence.Name;
            }

            return string.Empty;
        }

        public LSL_Key llRequestDisplayName(LSL_Key id)
        {
            if (!UUID.TryParse(id, out var key) || key.IsZero())
                return string.Empty;

            var lpresence = World.GetScenePresence(key);
            if (lpresence != null)
            {
                var lname = lpresence.Name;
                var ftid = m_AsyncCommands.DataserverPlugin.RequestWithImediatePost(m_host.LocalId,
                    m_item.ItemID, lname);
                return ftid;
            }

            Action<string> act = eventID =>
            {
                var name = string.Empty;
                var presence = World.GetScenePresence(key);
                if (presence != null)
                {
                    name = presence.Name;
                }
                else if (World.TryGetSceneObjectPart(key, out var sop) && sop != null)
                {
                    name = sop.Name;
                }
                else
                {
                    var account = m_userAccountService.GetUserAccount(RegionScopeID, key);
                    if (account != null) name = account.FirstName + " " + account.LastName;
                }

                m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, name);
            };

            var rq = m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId, m_item.ItemID, act);
            return rq.ToString();
        }


        public LSL_Integer llManageEstateAccess(int action, string avatar)
        {
            if (!UUID.TryParse(avatar, out var id) || id.IsZero())
                return 0;

            var estate = World.RegionInfo.EstateSettings;
            if (!estate.IsEstateOwner(m_host.OwnerID) || !estate.IsEstateManagerOrOwner(m_host.OwnerID))
                return 0;

            var account = m_userAccountService.GetUserAccount(RegionScopeID, id);
            var isAccount = account != null ? true : false;
            var isGroup = false;
            if (!isAccount)
            {
                var groups = World.RequestModuleInterface<IGroupsModule>();
                if (groups != null)
                {
                    var group = groups.GetGroupRecord(id);
                    isGroup = group != null ? true : false;
                    if (!isGroup)
                        return 0;
                }
                else
                {
                    return 0;
                }
            }

            switch (action)
            {
                case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_AGENT_ADD:
                    if (!isAccount) return 0;
                    if (estate.HasAccess(id)) return 1;
                    if (estate.IsBanned(id, World.GetUserFlags(id)))
                        estate.RemoveBan(id);
                    estate.AddEstateUser(id);
                    break;
                case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_AGENT_REMOVE:
                    if (!isAccount || !estate.HasAccess(id)) return 0;
                    estate.RemoveEstateUser(id);
                    break;
                case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_GROUP_ADD:
                    if (!isGroup) return 0;
                    if (estate.GroupAccess(id)) return 1;
                    estate.AddEstateGroup(id);
                    break;
                case ScriptBaseClass.ESTATE_ACCESS_ALLOWED_GROUP_REMOVE:
                    if (!isGroup || !estate.GroupAccess(id)) return 0;
                    estate.RemoveEstateGroup(id);
                    break;
                case ScriptBaseClass.ESTATE_ACCESS_BANNED_AGENT_ADD:
                    if (!isAccount) return 0;
                    if (estate.IsBanned(id, World.GetUserFlags(id))) return 1;
                    var ban = new EstateBan
                    {
                        EstateID = estate.EstateID,
                        BannedUserID = id
                    };
                    estate.AddBan(ban);
                    break;
                case ScriptBaseClass.ESTATE_ACCESS_BANNED_AGENT_REMOVE:
                    if (!isAccount || !estate.IsBanned(id, World.GetUserFlags(id))) return 0;
                    estate.RemoveBan(id);
                    break;
                default: return 0;
            }

            return 1;
        }


        public void llSetAnimationOverride(LSL_String animState, LSL_String anim)
        {
            if (m_item.PermsGranter.IsZero())
            {
                llShout(ScriptBaseClass.DEBUG_CHANNEL, "No permission to override animations");
                return;
            }

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_OVERRIDE_ANIMATIONS) == 0)
            {
                llShout(ScriptBaseClass.DEBUG_CHANNEL, "No permission to override animations");
                return;
            }

            var presence = World.GetScenePresence(m_item.PermsGranter);
            if (presence == null)
                return;

            var state = string.Empty;

            foreach (var kvp in MovementAnimationsForLSL)
                if (kvp.Value.ToLower() == ((string)animState).ToLower())
                {
                    state = kvp.Key;
                    break;
                }

            if (state.Length == 0)
            {
                llShout(ScriptBaseClass.DEBUG_CHANNEL, "Invalid animation state " + animState);
                return;
            }


            UUID animID;

            animID = ScriptUtils.GetAssetIdFromItemName(m_host, anim, (int)AssetType.Animation);

            if (animID.IsZero())
            {
                var animupper = ((string)anim).ToUpperInvariant();
                DefaultAvatarAnimations.AnimsUUIDbyName.TryGetValue(animupper, out animID);
            }

            if (animID.IsZero())
            {
                llShout(ScriptBaseClass.DEBUG_CHANNEL, "Animation not found");
                return;
            }

            presence.SetAnimationOverride(state, animID);
        }

        public void llResetAnimationOverride(LSL_String animState)
        {
            var presence = World.GetScenePresence(m_item.PermsGranter);
            if (presence == null)
                return;

            if (m_item.PermsGranter.IsZero())
            {
                llShout(ScriptBaseClass.DEBUG_CHANNEL, "No permission to override animations");
                return;
            }

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_OVERRIDE_ANIMATIONS) == 0)
            {
                llShout(ScriptBaseClass.DEBUG_CHANNEL, "No permission to override animations");
                return;
            }

            if (animState == "ALL")
            {
                presence.SetAnimationOverride("ALL", UUID.Zero);
                return;
            }

            var state = string.Empty;

            foreach (var kvp in MovementAnimationsForLSL)
                if (kvp.Value.ToLower() == ((string)animState).ToLower())
                {
                    state = kvp.Key;
                    break;
                }

            if (state.Length == 0) return;

            presence.SetAnimationOverride(state, UUID.Zero);
        }

        public LSL_String llGetAnimationOverride(LSL_String animState)
        {
            var presence = World.GetScenePresence(m_item.PermsGranter);
            if (presence == null)
                return string.Empty;

            if (m_item.PermsGranter.IsZero())
            {
                llShout(ScriptBaseClass.DEBUG_CHANNEL, "No permission to override animations");
                return string.Empty;
            }

            if ((m_item.PermsMask & (ScriptBaseClass.PERMISSION_OVERRIDE_ANIMATIONS |
                                     ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION)) == 0)
            {
                llShout(ScriptBaseClass.DEBUG_CHANNEL, "No permission to override animations");
                return string.Empty;
            }

            var state = string.Empty;

            foreach (var kvp in MovementAnimationsForLSL)
                if (kvp.Value.ToLower() == ((string)animState).ToLower())
                {
                    state = kvp.Key;
                    break;
                }

            if (state.Length == 0) return string.Empty;

            if (!presence.TryGetAnimationOverride(state, out var animID) || animID.IsZero())
                return animState;

            foreach (var kvp in DefaultAvatarAnimations.AnimsUUIDbyName)
                if (kvp.Value.Equals(animID))
                    return kvp.Key.ToLower();

            foreach (var item in m_host.Inventory.GetInventoryItems())
                if (item.AssetID.Equals(animID))
                    return item.Name;

            return string.Empty;
        }

        public LSL_Integer llGetDayLength()
        {
            if (m_envModule == null)
                return 14400;

            return m_envModule.GetDayLength(m_host.GetWorldPosition());
        }

        public LSL_Integer llGetRegionDayLength()
        {
            if (m_envModule == null)
                return 14400;

            return m_envModule.GetRegionDayLength();
        }

        public LSL_Integer llGetDayOffset()
        {
            if (m_envModule == null)
                return 57600;

            return m_envModule.GetDayOffset(m_host.GetWorldPosition());
        }

        public LSL_Integer llGetRegionDayOffset()
        {
            if (m_envModule == null)
                return 57600;

            return m_envModule.GetRegionDayOffset();
        }

        public LSL_Vector llGetSunDirection()
        {
            if (m_envModule == null)
                return Vector3.Zero;

            return m_envModule.GetSunDir(m_host.GetWorldPosition());
        }

        public LSL_Vector llGetRegionSunDirection()
        {
            if (m_envModule == null)
                return Vector3.Zero;

            var z = m_host.GetWorldPosition().Z;
            return m_envModule.GetRegionSunDir(z);
        }

        public LSL_Vector llGetMoonDirection()
        {
            if (m_envModule == null)
                return Vector3.Zero;

            return m_envModule.GetMoonDir(m_host.GetWorldPosition());
        }

        public LSL_Vector llGetRegionMoonDirection()
        {
            if (m_envModule == null)
                return Vector3.Zero;

            var z = m_host.GetWorldPosition().Z;
            return m_envModule.GetRegionMoonDir(z);
        }

        public LSL_Rotation llGetSunRotation()
        {
            if (m_envModule == null)
                return Quaternion.Identity;

            return m_envModule.GetSunRot(m_host.GetWorldPosition());
        }

        public LSL_Rotation llGetRegionSunRotation()
        {
            if (m_envModule == null)
                return Quaternion.Identity;

            var z = m_host.GetWorldPosition().Z;
            return m_envModule.GetRegionSunRot(z);
        }

        public LSL_Rotation llGetMoonRotation()
        {
            if (m_envModule == null)
                return Quaternion.Identity;

            return m_envModule.GetMoonRot(m_host.GetWorldPosition());
        }

        public LSL_Rotation llGetRegionMoonRotation()
        {
            if (m_envModule == null)
                return Quaternion.Identity;

            var z = m_host.GetWorldPosition().Z;
            return m_envModule.GetRegionMoonRot(z);
        }
    }
}