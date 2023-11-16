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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Animation;
using OpenSim.Region.Framework.Scenes.Scripting;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Services.Interfaces;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        public LSL_Float llGetAlpha(int face)
        {
            return GetAlpha(m_host, face);
        }

        public void llSetAlpha(double alpha, int face)
        {
            SetAlpha(m_host, alpha, face);
        }

        public void llSetLinkAlpha(int linknumber, double alpha, int face)
        {
            var parts = GetLinkParts(linknumber);
            if (parts.Count > 0)
                foreach (var part in parts)
                    SetAlpha(part, alpha, face);
        }

        public LSL_Vector llGetColor(int face)
        {
            return GetColor(m_host, face);
        }


        public void llSetStatus(int status, int value)
        {
            if (m_host == null || m_host.ParentGroup == null || m_host.ParentGroup.IsDeleted)
                return;

            var statusrotationaxis = 0;

            if ((status & ScriptBaseClass.STATUS_PHYSICS) == ScriptBaseClass.STATUS_PHYSICS)
            {
                if (value != 0)
                {
                    var group = m_host.ParentGroup;
                    var allow = true;

                    var maxprims = World.m_linksetPhysCapacity;
                    var checkShape = maxprims > 0 && group.PrimCount > maxprims;

                    foreach (var part in group.Parts)
                    {
                        if (part.PhysicsShapeType == (byte)PhysicsShapeType.None)
                            continue;

                        if (part.Scale.X > World.m_maxPhys || part.Scale.Y > World.m_maxPhys ||
                            part.Scale.Z > World.m_maxPhys)
                        {
                            allow = false;
                            break;
                        }

                        if (checkShape)
                            if (--maxprims < 0)
                            {
                                allow = false;
                                break;
                            }
                    }

                    if (!allow)
                        return;

                    if (m_host.ParentGroup.RootPart.PhysActor != null &&
                        m_host.ParentGroup.RootPart.PhysActor.IsPhysical)
                        return;

                    m_host.ScriptSetPhysicsStatus(true);
                }
                else
                {
                    m_host.ScriptSetPhysicsStatus(false);
                }
            }

            if ((status & ScriptBaseClass.STATUS_PHANTOM) == ScriptBaseClass.STATUS_PHANTOM)
                m_host.ParentGroup.ScriptSetPhantomStatus(value != 0);

            if ((status & ScriptBaseClass.STATUS_CAST_SHADOWS) == ScriptBaseClass.STATUS_CAST_SHADOWS)
                m_host.AddFlag(PrimFlags.CastShadows);

            if ((status & ScriptBaseClass.STATUS_ROTATE_X) == ScriptBaseClass.STATUS_ROTATE_X)
                statusrotationaxis |= ScriptBaseClass.STATUS_ROTATE_X;

            if ((status & ScriptBaseClass.STATUS_ROTATE_Y) == ScriptBaseClass.STATUS_ROTATE_Y)
                statusrotationaxis |= ScriptBaseClass.STATUS_ROTATE_Y;

            if ((status & ScriptBaseClass.STATUS_ROTATE_Z) == ScriptBaseClass.STATUS_ROTATE_Z)
                statusrotationaxis |= ScriptBaseClass.STATUS_ROTATE_Z;

            if ((status & ScriptBaseClass.STATUS_BLOCK_GRAB) == ScriptBaseClass.STATUS_BLOCK_GRAB)
                m_host.BlockGrab = value != 0;

            if ((status & ScriptBaseClass.STATUS_BLOCK_GRAB_OBJECT) == ScriptBaseClass.STATUS_BLOCK_GRAB_OBJECT)
                m_host.ParentGroup.BlockGrabOverride = value != 0;

            if ((status & ScriptBaseClass.STATUS_DIE_AT_EDGE) == ScriptBaseClass.STATUS_DIE_AT_EDGE)
            {
                if (value != 0)
                    m_host.SetDieAtEdge(true);
                else
                    m_host.SetDieAtEdge(false);
            }

            if ((status & ScriptBaseClass.STATUS_RETURN_AT_EDGE) == ScriptBaseClass.STATUS_RETURN_AT_EDGE)
            {
                if (value != 0)
                    m_host.SetReturnAtEdge(true);
                else
                    m_host.SetReturnAtEdge(false);
            }

            if ((status & ScriptBaseClass.STATUS_SANDBOX) == ScriptBaseClass.STATUS_SANDBOX)
            {
                if (value != 0)
                    m_host.SetStatusSandbox(true);
                else
                    m_host.SetStatusSandbox(false);
            }

            if (statusrotationaxis != 0) m_host.SetAxisRotation(statusrotationaxis, value);
        }

        [DebuggerNonUserCode]
        public virtual void llDie()
        {
            if (!m_host.ParentGroup.IsAttachment) throw new SelfDeleteException();
        }


        public void llSetScale(LSL_Vector scale)
        {
            SetScale(m_host, scale);
        }

        public LSL_Vector llGetScale()
        {
            return new LSL_Vector(m_host.Scale.X, m_host.Scale.Y, m_host.Scale.Z);
        }

        public void llSetClickAction(int action)
        {
            m_host.ClickAction = (byte)action;
            m_host.ParentGroup.HasGroupChanged = true;
            m_host.ScheduleFullUpdate();
        }

        public void llSetColor(LSL_Vector color, int face)
        {
            SetColor(m_host, color, face);
        }


        public void llSetTexture(string texture, int face)
        {
            SetTexture(m_host, texture, face);
            ScriptSleep(m_sleepMsOnSetTexture);
        }

        public void llSetLinkTexture(int linknumber, string texture, int face)
        {
            var parts = GetLinkParts(linknumber);
            if (parts.Count > 0)
                foreach (var part in parts)
                    SetTexture(part, texture, face);
            ScriptSleep(m_sleepMsOnSetLinkTexture);
        }


        public void llScaleTexture(double u, double v, int face)
        {
            ScaleTexture(m_host, u, v, face);
            ScriptSleep(m_sleepMsOnScaleTexture);
        }

        public void llOffsetTexture(double u, double v, int face)
        {
            OffsetTexture(m_host, u, v, face);
            ScriptSleep(m_sleepMsOnOffsetTexture);
        }

        public void llRotateTexture(double rotation, int face)
        {
            RotateTexture(m_host, rotation, face);
            ScriptSleep(m_sleepMsOnRotateTexture);
        }

        public void llTargetRemove(int number)
        {
            m_host.ParentGroup.UnregisterTargetWaypoint(number);
        }

        public void llRotTargetRemove(int number)
        {
            m_host.ParentGroup.UnRegisterRotTargetWaypoint(number);
        }

        public void llStopMoveToTarget()
        {
            m_host.ParentGroup.StopMoveToTarget();
        }

        public void llSetBuoyancy(double buoyancy)
        {
            if (!m_host.ParentGroup.IsDeleted) m_host.ParentGroup.RootPart.SetBuoyancy((float)buoyancy);
        }


        /// <summary>
        ///     Attempt to clamp the object on the Z axis at the given height over tau seconds.
        /// </summary>
        /// <param name="height">Height to hover.  Height of zero disables hover.</param>
        /// <param name="water">
        ///     False if height is calculated just from ground, otherwise uses ground or water depending on
        ///     whichever is higher
        /// </param>
        /// <param name="tau">Number of seconds over which to reach target</param>
        public void llSetHoverHeight(double height, int water, double tau)
        {
            var hoverType = PIDHoverType.Ground;
            if (water != 0) hoverType = PIDHoverType.GroundAndWater;
            m_host.SetHoverHeight((float)height, hoverType, (float)tau);
        }

        public void llStopHover()
        {
            m_host.SetHoverHeight(0f, PIDHoverType.Ground, 0f);
        }

        public void llMinEventDelay(double delay)
        {
            try
            {
                m_ScriptEngine.SetMinEventDelay(m_item.ItemID, delay);
            }
            catch (NotImplementedException)
            {
                // Currently not implemented in DotNetEngine only XEngine
                NotImplemented("llMinEventDelay", "In DotNetEngine");
            }
        }

        public void llStartObjectAnimation(string anim)
        {
            // Do NOT try to parse UUID, animations cannot be triggered by ID
            var animID = ScriptUtils.GetAssetIdFromItemName(m_host, anim, (int)AssetType.Animation);
            if (animID.IsZero())
                animID = DefaultAvatarAnimations.GetDefaultAnimation(anim);
            if (!animID.IsZero())
                m_host.AddAnimation(animID, anim);
        }

        public void llStopObjectAnimation(string anim)
        {
            m_host.RemoveAnimation(anim);
        }

        public void llBreakLink(int linknum)
        {
            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_CHANGE_LINKS) == 0
                && !m_automaticLinkPermission)
            {
                Error("llBreakLink", "PERMISSION_CHANGE_LINKS permission not set");
                return;
            }

            BreakLink(linknum);
        }

        public void llBreakAllLinks()
        {
            var item = m_item;

            if ((item.PermsMask & ScriptBaseClass.PERMISSION_CHANGE_LINKS) == 0
                && !m_automaticLinkPermission)
            {
                Error("llBreakAllLinks", "Script trying to link but PERMISSION_CHANGE_LINKS permission not set!");
                return;
            }

            BreakAllLinks();
        }


        [DebuggerNonUserCode]
        public void llRemoveInventory(string name)
        {
            var item = m_host.Inventory.GetInventoryItem(name);

            if (item == null)
                return;

            if (item.ItemID == m_item.ItemID)
                throw new ScriptDeleteException();
            m_host.Inventory.RemoveInventoryItem(item.ItemID);
        }

        public void llPassTouches(int pass)
        {
            if (pass != 0)
                m_host.PassTouches = true;
            else
                m_host.PassTouches = false;
        }

        public void llSetDamage(double damage)
        {
            m_host.ParentGroup.Damage = (float)damage;
        }


        public void llPassCollisions(int pass)
        {
            if (pass == 1)
                m_host.PassCollisions = true;
            else
                m_host.PassCollisions = false;
        }

        public void llSetObjectName(string name)
        {
            m_host.Name = name ?? string.Empty;
        }

        public void llSetTextureAnim(int mode, int face, int sizex, int sizey, double start, double length, double rate)
        {
            SetTextureAnim(m_host, mode, face, sizex, sizey, start, length, rate);
        }

        public void llSetLinkTextureAnim(int linknumber, int mode, int face, int sizex, int sizey, double start,
            double length, double rate)
        {
            var parts = GetLinkParts(linknumber);

            foreach (var part in parts) SetTextureAnim(part, mode, face, sizex, sizey, start, length, rate);
        }

        public void llSetRemoteScriptAccessPin(int pin)
        {
            m_host.ScriptAccessPin = pin;
        }

        public void llRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param)
        {
            if (!UUID.TryParse(target, out var destId) || destId.IsZero())
            {
                Error("llRemoteLoadScriptPin", "invalid key '" + target + "'");
                return;
            }

            // target must be a different prim than the one containing the script
            if (m_host.UUID.Equals(destId)) return;

            // copy the first script found with this inventory name
            var item = m_host.Inventory.GetInventoryItem(name);

            // make sure the object is a script
            if (item == null || item.Type != 10)
            {
                Error("llRemoteLoadScriptPin", "Can't find script '" + name + "'");
                return;
            }

            if ((item.BasePermissions & (uint)PermissionMask.Copy) == 0)
            {
                Error("llRemoteLoadScriptPin", "No copy rights");
                return;
            }

            // the rest of the permission checks are done in RezScript, so check the pin there as well
            World.RezScriptFromPrim(item.ItemID, m_host, destId, pin, running, start_param);

            // this will cause the delay even if the script pin or permissions were wrong - seems ok
            ScriptSleep(m_sleepMsOnRemoteLoadScriptPin);
        }

        public void llSetObjectDesc(string desc)
        {
            m_host.Description = desc ?? string.Empty;
        }

        public LSL_Integer llScaleByFactor(double scaling_factor)
        {
            var group = m_host.ParentGroup;

            if (scaling_factor < 1e-6)
                return ScriptBaseClass.FALSE;
            if (scaling_factor > 1e6)
                return ScriptBaseClass.FALSE;

            if (group == null || group.IsDeleted || group.inTransit)
                return ScriptBaseClass.FALSE;

            if (group.RootPart.PhysActor != null && group.RootPart.PhysActor.IsPhysical)
                return ScriptBaseClass.FALSE;

            if (group.RootPart.KeyframeMotion != null)
                return ScriptBaseClass.FALSE;

            if (group.GroupResize(scaling_factor))
                return ScriptBaseClass.TRUE;
            return ScriptBaseClass.FALSE;
        }

        public LSL_Float llGetMaxScaleFactor()
        {
            var group = m_host.ParentGroup;

            if (group == null || group.IsDeleted || group.inTransit)
                return 1.0f;

            return group.GetMaxGroupResizeScale();
        }

        public LSL_Float llGetMinScaleFactor()
        {
            var group = m_host.ParentGroup;

            if (group == null || group.IsDeleted || group.inTransit)
                return 1.0f;

            return group.GetMinGroupResizeScale();
        }


        public LSL_Integer llGetStatus(int status)
        {
            // m_log.Debug(m_host.ToString() + " status is " + m_host.GetEffectiveObjectFlags().ToString());
            switch (status)
            {
                case ScriptBaseClass.STATUS_PHYSICS:
                    return IsPhysical() ? 1 : 0;

                case ScriptBaseClass.STATUS_PHANTOM:
                    if ((m_host.GetEffectiveObjectFlags() & (uint)PrimFlags.Phantom) ==
                        (uint)PrimFlags.Phantom) return 1;
                    return 0;

                case ScriptBaseClass.STATUS_CAST_SHADOWS:
                    if ((m_host.GetEffectiveObjectFlags() & (uint)PrimFlags.CastShadows) ==
                        (uint)PrimFlags.CastShadows) return 1;
                    return 0;

                case ScriptBaseClass.STATUS_BLOCK_GRAB:
                    return m_host.BlockGrab ? 1 : 0;

                case ScriptBaseClass.STATUS_BLOCK_GRAB_OBJECT:
                    return m_host.ParentGroup.BlockGrabOverride ? 1 : 0;

                case ScriptBaseClass.STATUS_DIE_AT_EDGE:
                    if (m_host.GetDieAtEdge())
                        return 1;
                    return 0;

                case ScriptBaseClass.STATUS_RETURN_AT_EDGE:
                    if (m_host.GetReturnAtEdge())
                        return 1;
                    return 0;

                case ScriptBaseClass.STATUS_ROTATE_X:
                    // if (m_host.GetAxisRotation(2) != 0)
                    if (m_host.GetAxisRotation((int)SceneObjectGroup.axisSelect.STATUS_ROTATE_X) != 0)
                        return 1;
                    return 0;

                case ScriptBaseClass.STATUS_ROTATE_Y:
                    if (m_host.GetAxisRotation((int)SceneObjectGroup.axisSelect.STATUS_ROTATE_Y) != 0)
                        return 1;
                    return 0;

                case ScriptBaseClass.STATUS_ROTATE_Z:
                    if (m_host.GetAxisRotation((int)SceneObjectGroup.axisSelect.STATUS_ROTATE_Z) != 0)
                        return 1;
                    return 0;

                case ScriptBaseClass.STATUS_SANDBOX:
                    if (m_host.GetStatusSandbox())
                        return 1;
                    return 0;
            }

            return 0;
        }

        public LSL_String llGetTexture(int face)
        {
            return GetTexture(m_host, face);
        }

        public void llSetPos(LSL_Vector pos)
        {
            SetPos(m_host, pos, true);

            ScriptSleep(m_sleepMsOnSetPos);
        }

        /// <summary>
        ///     Tries to move the entire object so that the root prim is within 0.1m of position.
        ///     http://wiki.secondlife.com/wiki/LlSetRegionPos
        ///     Documentation indicates that the use of x/y coordinates up to 10 meters outside the bounds of a region will work
        ///     but do not specify what happens if there is no adjacent region for the object to move into.
        ///     Uses the RegionSize constant here rather than hard-coding 266.0 to alert any developer modifying OpenSim to support
        ///     variable-sized regions that this method will need tweaking.
        /// </summary>
        /// <param name="pos"></param>
        /// <returns>1 if successful, 0 otherwise.</returns>
        public LSL_Integer llSetRegionPos(LSL_Vector pos)
        {
            // BEGIN WORKAROUND
            // IF YOU GET REGION CROSSINGS WORKING WITH THIS FUNCTION, REPLACE THE WORKAROUND.
            //
            // This workaround is to prevent silent failure of this function.
            // According to the specification on the SL Wiki, providing a position outside of the
            if (pos.x < 0 || pos.x > World.RegionInfo.RegionSizeX || pos.y < 0 || pos.y > World.RegionInfo.RegionSizeY)
                return 0;
            // END WORK AROUND
            if ( // this is not part of the workaround if-block because it's not related to the workaround.
                IsPhysical() ||
                m_host.ParentGroup.IsAttachment || // return FALSE if attachment
                pos.x < -10.0 || // return FALSE if more than 10 meters into a west-adjacent region.
                pos.x > World.RegionInfo.RegionSizeX +
                10 || // return FALSE if more than 10 meters into a east-adjacent region.
                pos.y < -10.0 || // return FALSE if more than 10 meters into a south-adjacent region.
                pos.y > World.RegionInfo.RegionSizeY +
                10 || // return FALSE if more than 10 meters into a north-adjacent region.
                pos.z > Constants.RegionHeight // return FALSE if altitude than 4096m
               )
                return 0;

            // if we reach this point, then the object is not physical, it's not an attachment, and the destination is within the valid range.
            // this could possibly be done in the above else-if block, but we're doing the check here to keep the code easier to read.

            var objectPos = m_host.ParentGroup.RootPart.AbsolutePosition;
            var here = World.GetLandData(objectPos);
            var there = World.GetLandData(pos);

            // we're only checking prim limits if it's moving to a different parcel under the assumption that if the object got onto the parcel without exceeding the prim limits.

            var sameParcel = here.GlobalID.Equals(there.GlobalID);

            if (!sameParcel && !World.Permissions.CanRezObject(
                    m_host.ParentGroup.PrimCount, m_host.ParentGroup.OwnerID, pos))
                return 0;

            SetPos(m_host.ParentGroup.RootPart, pos, false);

            return VecDistSquare(pos, llGetRootPosition()) <= 0.01 ? 1 : 0;
        }

        public LSL_Vector llGetPos()
        {
            return m_host.GetWorldPosition();
        }

        public LSL_Vector llGetLocalPos()
        {
            return GetPartLocalPos(m_host);
        }

        public void llSetRot(LSL_Rotation rot)
        {
            // try to let this work as in SL...
            if (m_host.ParentID == 0 || (m_host.ParentGroup != null && m_host == m_host.ParentGroup.RootPart))
            {
                // special case: If we are root, rotate complete SOG to new rotation
                SetRot(m_host, rot);
            }
            else
            {
                // we are a child. The rotation values will be set to the one of root modified by rot, as in SL. Don't ask.
                var rootPart = m_host.ParentGroup.RootPart;
                if (rootPart != null) // better safe than sorry
                    SetRot(m_host, rootPart.RotationOffset * (Quaternion)rot);
            }

            ScriptSleep(m_sleepMsOnSetRot);
        }

        public void llSetLocalRot(LSL_Rotation rot)
        {
            SetRot(m_host, rot);
            ScriptSleep(m_sleepMsOnSetLocalRot);
        }

        /// <summary>
        ///     See http://lslwiki.net/lslwiki/wakka.php?wakka=ChildRotation
        /// </summary>
        public LSL_Rotation llGetRot()
        {
            // unlinked or root prim then use llRootRotation
            // see llRootRotaion for references.
            if (m_host.LinkNum == 0 || m_host.LinkNum == 1) return llGetRootRotation();

            var q = m_host.GetWorldRotation();

            if (m_host.ParentGroup != null && m_host.ParentGroup.AttachmentPoint != 0)
            {
                var avatar = World.GetScenePresence(m_host.ParentGroup.AttachedAvatar);
                if (avatar != null)
                {
                    if ((avatar.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0)
                        q = avatar.CameraRotation * q; // Mouselook
                    else
                        q = avatar.Rotation * q; // Currently infrequently updated so may be inaccurate
                }
            }

            return new LSL_Rotation(q.X, q.Y, q.Z, q.W);
        }

        public LSL_Rotation llGetLocalRot()
        {
            return GetPartLocalRot(m_host);
        }

        public void llSetForce(LSL_Vector force, int local)
        {
            if (!m_host.ParentGroup.IsDeleted)
            {
                if (local != 0)
                    force *= llGetRot();

                m_host.ParentGroup.RootPart.SetForce(force);
            }
        }

        public LSL_Vector llGetForce()
        {
            var force = new LSL_Vector(0.0, 0.0, 0.0);


            if (!m_host.ParentGroup.IsDeleted) force = m_host.ParentGroup.RootPart.GetForce();

            return force;
        }

        public void llSetVelocity(LSL_Vector vel, int local)
        {
            m_host.SetVelocity(new Vector3((float)vel.x, (float)vel.y, (float)vel.z), local != 0);
        }

        public void llSetAngularVelocity(LSL_Vector avel, int local)
        {
            m_host.SetAngularVelocity(new Vector3((float)avel.x, (float)avel.y, (float)avel.z), local != 0);
        }

        public LSL_Integer llTarget(LSL_Vector position, double range)
        {
            return m_host.ParentGroup.RegisterTargetWaypoint(m_item.ItemID, position, (float)range);
        }

        public LSL_Integer llRotTarget(LSL_Rotation rot, double error)
        {
            return m_host.ParentGroup.RegisterRotTargetWaypoint(m_item.ItemID, rot, (float)error);
        }

        public void llMoveToTarget(LSL_Vector target, double tau)
        {
            m_host.ParentGroup.MoveToTarget(target, (float)tau);
        }

        public LSL_Key llGetOwner()
        {
            return m_host.OwnerID.ToString();
        }

        public LSL_Key llGetKey()
        {
            return m_host.UUID.ToString();
        }

        public LSL_Key llGenerateKey()
        {
            return UUID.Random().ToString();
        }

        public void llRotLookAt(LSL_Rotation target, double strength, double damping)
        {
            // Per discussion with Melanie, for non-physical objects llLookAt appears to simply
            // set the rotation of the object, copy that behavior
            var sog = m_host.ParentGroup;
            if (sog == null || sog.IsDeleted)
                return;

            if (strength == 0 || !sog.UsesPhysics || sog.IsAttachment)
                llSetLocalRot(target);
            else
                sog.RotLookAt(target, (float)strength, (float)damping);
        }

        public LSL_List llGetObjectAnimationNames()
        {
            var ret = new LSL_List();

            if (m_host.AnimationsNames == null || m_host.AnimationsNames.Count == 0)
                return ret;

            foreach (var name in m_host.AnimationsNames.Values)
                ret.Add(new LSL_String(name));
            return ret;
        }


        public void llTargetOmega(LSL_Vector axis, double spinrate, double gain)
        {
            TargetOmega(m_host, axis, spinrate, gain);
        }

        public LSL_Integer llGetStartParameter()
        {
            return m_ScriptEngine.GetStartParameter(m_item.ItemID);
        }

        public LSL_Integer llGetLinkNumber()
        {
            if (m_host.ParentGroup.PrimCount > 1)
                return m_host.LinkNum;
            return 0;
        }

        public void llSetLinkColor(int linknumber, LSL_Vector color, int face)
        {
            var parts = GetLinkParts(linknumber);
            if (parts.Count > 0)
                foreach (var part in parts)
                    part.SetFaceColorAlpha(face, color, null);
        }

        public void llCreateLink(LSL_Key target, LSL_Integer parent)
        {
            if (!m_automaticLinkPermission)
            {
                if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_CHANGE_LINKS) == 0)
                {
                    Error("llCreateLink", "PERMISSION_CHANGE_LINKS required");
                    return;
                }

                if (m_item.PermsGranter.NotEqual(m_host.ParentGroup.OwnerID))
                {
                    Error("llCreateLink", "PERMISSION_CHANGE_LINKS not set by script owner");
                    return;
                }
            }

            CreateLink(target, parent);
        }

        public LSL_Key llGetLinkKey(int linknum)
        {
            if (linknum < 0)
            {
                if (linknum == ScriptBaseClass.LINK_THIS)
                    return m_host.UUID.ToString();
                return ScriptBaseClass.NULL_KEY;
            }

            var sog = m_host.ParentGroup;
            if (linknum < 2)
                return sog.RootPart.UUID.ToString();

            var part = sog.GetLinkNumPart(linknum);
            if (part != null) return part.UUID.ToString();

            if (linknum > sog.PrimCount)
            {
                linknum -= sog.PrimCount + 1;

                var avatars = GetLinkAvatars(ScriptBaseClass.LINK_SET, sog);
                if (avatars.Count > linknum) return avatars[linknum].UUID.ToString();
            }

            return ScriptBaseClass.NULL_KEY;
        }

        public LSL_Key llGetObjectLinkKey(LSL_Key objectid, int linknum)
        {
            if (!UUID.TryParse(objectid, out var oID))
                return ScriptBaseClass.NULL_KEY;

            if (!World.TryGetSceneObjectPart(oID, out var sop))
                return ScriptBaseClass.NULL_KEY;

            if (linknum < 0)
            {
                if (linknum == ScriptBaseClass.LINK_THIS)
                    return sop.UUID.ToString();
                return ScriptBaseClass.NULL_KEY;
            }

            var sog = sop.ParentGroup;

            if (linknum < 2)
                return sog.RootPart.UUID.ToString();

            var part = sog.GetLinkNumPart(linknum);
            if (part != null) return part.UUID.ToString();

            if (linknum > sog.PrimCount)
            {
                linknum -= sog.PrimCount + 1;

                var avatars = GetLinkAvatars(ScriptBaseClass.LINK_SET, sog);
                if (avatars.Count > linknum) return avatars[linknum].UUID.ToString();
            }

            return ScriptBaseClass.NULL_KEY;
        }

        /// <summary>
        ///     Returns the name of the child prim or seated avatar matching the
        ///     specified link number.
        /// </summary>
        /// <param name="linknum">
        ///     The number of a link in the linkset or a link-related constant.
        /// </param>
        /// <returns>
        ///     The name determined to match the specified link number.
        /// </returns>
        /// <remarks>
        ///     The rules governing the returned name are not simple. The only
        ///     time a blank name is returned is if the target prim has a blank
        ///     name. If no prim with the given link number can be found then
        ///     usually NULL_KEY is returned but there are exceptions.
        ///     In a single unlinked prim, A call with 0 returns the name, all
        ///     other values for link number return NULL_KEY
        ///     In link sets it is more complicated.
        ///     If the script is in the root prim:-
        ///     A zero link number returns NULL_KEY.
        ///     Positive link numbers return the name of the prim, or NULL_KEY
        ///     if a prim does not exist at that position.
        ///     Negative link numbers return the name of the first child prim.
        ///     If the script is in a child prim:-
        ///     Link numbers 0 or 1 return the name of the root prim.
        ///     Positive link numbers return the name of the prim or NULL_KEY
        ///     if a prim does not exist at that position.
        ///     Negative numbers return the name of the root prim.
        ///     References
        ///     http://lslwiki.net/lslwiki/wakka.php?wakka=llGetLinkName
        ///     Mentions NULL_KEY being returned
        ///     http://wiki.secondlife.com/wiki/LlGetLinkName
        ///     Mentions using the LINK_* constants, some of which are negative
        /// </remarks>
        public LSL_String llGetLinkName(int linknum)
        {
            var entity = GetLinkEntity(m_host, linknum);

            if (entity != null)
                return entity.Name;
            return ScriptBaseClass.NULL_KEY;
        }

        public LSL_Integer llGetInventoryNumber(int type)
        {
            var count = 0;

            m_host.TaskInventory.LockItemsForRead(true);
            foreach (var inv in m_host.TaskInventory)
                if (inv.Value.Type == type || type == -1)
                    count = count + 1;

            m_host.TaskInventory.LockItemsForRead(false);
            return count;
        }

        public LSL_String llGetInventoryName(int type, int number)
        {
            var keys = new ArrayList();

            m_host.TaskInventory.LockItemsForRead(true);
            foreach (var inv in m_host.TaskInventory)
                if (inv.Value.Type == type || type == -1)
                    keys.Add(inv.Value.Name);
            m_host.TaskInventory.LockItemsForRead(false);

            if (keys.Count == 0) return string.Empty;
            keys.Sort();
            if (keys.Count > number) return (string)keys[number];
            return string.Empty;
        }

        public void llSetText(string text, LSL_Vector color, double alpha)
        {
            var av3 = Vector3.Clamp(color, 0.0f, 1.0f);
            m_host.SetText(text, av3, Utils.Clamp((float)alpha, 0.0f, 1.0f));
        }

        public LSL_Key llRequestAgentData(string id, int data)
        {
            if (UUID.TryParse(id, out var uuid) && uuid.IsNotZero())
            {
                //pre process fast local avatars
                switch (data)
                {
                    case ScriptBaseClass.DATA_ONLINE:
                        World.TryGetScenePresence(uuid, out ScenePresence sp);
                        if (sp != null)
                        {
                            var ftid = m_AsyncCommands.DataserverPlugin.RequestWithImediatePost(m_host.LocalId,
                                m_item.ItemID, "1");
                            ScriptSleep(m_sleepMsOnRequestAgentData);
                            return ftid;
                        }

                        break;
                    case ScriptBaseClass.DATA_NAME: // DATA_NAME (First Last)
                    case ScriptBaseClass.DATA_BORN: // DATA_BORN (YYYY-MM-DD)
                    case ScriptBaseClass.DATA_RATING: // DATA_RATING (0,0,0,0,0,0)
                    case 7: // DATA_USERLEVEL (integer).  This is not available in LL and so has no constant.
                    case ScriptBaseClass.DATA_PAYINFO: // DATA_PAYINFO (0|1|2|3)
                        break;
                    default:
                        return string.Empty; // Raise no event
                }

                Action<string> act = eventID =>
                {
                    UserAccount account = null;
                    string reply;

                    if (data == ScriptBaseClass.DATA_ONLINE)
                    {
                        World.TryGetScenePresence(uuid, out ScenePresence sp);
                        if (sp != null)
                        {
                            reply = "1";
                        }
                        else
                        {
                            account = m_userAccountService.GetUserAccount(RegionScopeID, uuid);
                            if (account == null)
                            {
                                reply = "0";
                            }
                            else
                            {
                                PresenceInfo pinfo = null;
                                if (!m_PresenceInfoCache.TryGetValue(uuid, out pinfo))
                                {
                                    var pinfos = World.PresenceService.GetAgents(new[] { uuid.ToString() });
                                    if (pinfos != null && pinfos.Length > 0)
                                        foreach (var p in pinfos)
                                            if (!p.RegionID.IsZero())
                                                pinfo = p;
                                    m_PresenceInfoCache.AddOrUpdate(uuid, pinfo, m_llRequestAgentDataCacheTimeout);
                                }

                                reply = pinfo == null ? "0" : "1";
                            }
                        }
                    }
                    else
                    {
                        if (account == null)
                            account = m_userAccountService.GetUserAccount(RegionScopeID, uuid);

                        if (account == null)
                            reply = "0";
                        else
                            switch (data)
                            {
                                case ScriptBaseClass.DATA_NAME: // DATA_NAME (First Last)
                                    reply = account.FirstName + " " + account.LastName;
                                    break;
                                case ScriptBaseClass.DATA_BORN: // DATA_BORN (YYYY-MM-DD)
                                    var born = new DateTime(1970, 1, 1, 0, 0, 0, 0);
                                    born = born.AddSeconds(account.Created);
                                    reply = born.ToString("yyyy-MM-dd");
                                    break;
                                case ScriptBaseClass.DATA_RATING: // DATA_RATING (0,0,0,0,0,0)
                                    reply = "0,0,0,0,0,0";
                                    break;
                                case 7
                                    : // DATA_USERLEVEL (integer).  This is not available in LL and so has no constant.
                                    reply = account.UserLevel.ToString();
                                    break;
                                case ScriptBaseClass.DATA_PAYINFO: // DATA_PAYINFO (0|1|2|3)
                                    reply = "0";
                                    break;
                                default:
                                    reply = "0"; // Raise no event
                                    break;
                            }
                    }

                    m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, reply);
                };

                var tid = m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId,
                    m_item.ItemID, act);

                ScriptSleep(m_sleepMsOnRequestAgentData);
                return tid.ToString();
            }

            Error("llRequestAgentData", "Invalid UUID passed to llRequestAgentData.");
            return string.Empty;
        }

        //bad if lm is HG
        public LSL_Key llRequestInventoryData(LSL_String name)
        {
            Action<string> act = eventID =>
            {
                var reply = string.Empty;
                foreach (var item in m_host.Inventory.GetInventoryItems())
                    if (item.Type == 3 && item.Name == name)
                    {
                        var a = World.AssetService.Get(item.AssetID.ToString());
                        if (a != null)
                        {
                            var lm = new AssetLandmark(a);
                            if (lm != null)
                            {
                                var rx = (lm.RegionHandle >> 32) - (double)World.RegionInfo.WorldLocX + lm.Position.X;
                                var ry = (lm.RegionHandle & 0xffffffff) - (double)World.RegionInfo.WorldLocY +
                                         lm.Position.Y;
                                var region = new LSL_Vector(rx, ry, lm.Position.Z);
                                reply = region.ToString();
                            }
                        }

                        break;
                    }

                m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, reply);
            };

            var tid = m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId,
                m_item.ItemID, act);

            ScriptSleep(m_sleepMsOnRequestInventoryData);
            return tid.ToString();
        }

        public LSL_String llGetScriptName()
        {
            return m_item.Name ?? string.Empty;
        }

        public LSL_Integer llGetLinkNumberOfSides(int link)
        {
            SceneObjectPart linkedPart;

            if (link == ScriptBaseClass.LINK_ROOT)
                linkedPart = m_host.ParentGroup.RootPart;
            else if (link == ScriptBaseClass.LINK_THIS)
                linkedPart = m_host;
            else
                linkedPart = m_host.ParentGroup.GetLinkNumPart(link);

            return GetNumberOfSides(linkedPart);
        }

        public LSL_Integer llGetNumberOfSides()
        {
            return m_host.GetNumberOfSides();
        }

        public LSL_Key llGetInventoryKey(string name)
        {
            var item = m_host.Inventory.GetInventoryItem(name);

            if (item == null)
                return ScriptBaseClass.NULL_KEY;

            if ((item.CurrentPermissions
                 & (uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Modify))
                == (uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Modify))
                return item.AssetID.ToString();

            return ScriptBaseClass.NULL_KEY;
        }

        public void llAllowInventoryDrop(LSL_Integer add)
        {
            if (add != 0)
                m_host.ParentGroup.RootPart.AllowedDrop = true;
            else
                m_host.ParentGroup.RootPart.AllowedDrop = false;

            // Update the object flags
            m_host.ParentGroup.RootPart.aggregateScriptEvents();
        }

        public LSL_Vector llGetTextureOffset(int face)
        {
            return GetTextureOffset(m_host, face);
        }

        public LSL_Vector llGetTextureScale(int side)
        {
            var tex = m_host.Shape.Textures;
            LSL_Vector scale;
            if (side == -1) side = 0;
            scale.x = tex.GetFace((uint)side).RepeatU;
            scale.y = tex.GetFace((uint)side).RepeatV;
            scale.z = 0.0;
            return scale;
        }

        public LSL_Float llGetTextureRot(int face)
        {
            return GetTextureRot(m_host, face);
        }


        public LSL_Key llGetOwnerKey(string id)
        {
            if (UUID.TryParse(id, out var key))
            {
                if (key.IsZero())
                    return id;

                var obj = World.GetSceneObjectPart(key);
                return obj == null ? id : obj.OwnerID.ToString();
            }

            return ScriptBaseClass.NULL_KEY;
        }

        public LSL_String llGetObjectName()
        {
            return m_host.Name ?? string.Empty;
        }


        public void llSitTarget(LSL_Vector offset, LSL_Rotation rot)
        {
            SitTarget(m_host, offset, rot);
        }

        public void llLinkSitTarget(LSL_Integer link, LSL_Vector offset, LSL_Rotation rot)
        {
            if (link == ScriptBaseClass.LINK_ROOT)
            {
                SitTarget(m_host.ParentGroup.RootPart, offset, rot);
            }
            else if (link == ScriptBaseClass.LINK_THIS)
            {
                SitTarget(m_host, offset, rot);
            }
            else
            {
                var part = m_host.ParentGroup.GetLinkNumPart(link);
                if (null != part) SitTarget(part, offset, rot);
            }
        }

        public LSL_Key llAvatarOnSitTarget()
        {
            return m_host.SitTargetAvatar.ToString();
        }

        // http://wiki.secondlife.com/wiki/LlAvatarOnLinkSitTarget
        public LSL_Key llAvatarOnLinkSitTarget(LSL_Integer linknum)
        {
            if (linknum == ScriptBaseClass.LINK_SET ||
                linknum == ScriptBaseClass.LINK_ALL_CHILDREN ||
                linknum == ScriptBaseClass.LINK_ALL_OTHERS ||
                linknum == 0)
                return ScriptBaseClass.NULL_KEY;

            var parts = GetLinkParts(linknum);
            if (parts.Count == 0)
                return ScriptBaseClass.NULL_KEY;
            return parts[0].SitTargetAvatar.ToString();
        }

        public void llSetPrimitiveParams(LSL_List rules)
        {
            SetLinkPrimParams(ScriptBaseClass.LINK_THIS, rules, "llSetPrimitiveParams");

            ScriptSleep(m_sleepMsOnSetPrimitiveParams);
        }

        public void llSetLinkPrimitiveParams(int linknumber, LSL_List rules)
        {
            SetLinkPrimParams(linknumber, rules, "llSetLinkPrimitiveParams");

            ScriptSleep(m_sleepMsOnSetLinkPrimitiveParams);
        }

        public void llSetLinkPrimitiveParamsFast(int linknumber, LSL_List rules)
        {
            SetLinkPrimParams(linknumber, rules, "llSetLinkPrimitiveParamsFast");
        }

        public void llSetKeyframedMotion(LSL_List frames, LSL_List options)
        {
            var group = m_host.ParentGroup;

            if (group.RootPart.PhysActor != null && group.RootPart.PhysActor.IsPhysical)
                return;
            if (group.IsAttachment)
                return;

            if (frames.Data.Length > 0) // We are getting a new motion
            {
                if (group.RootPart.KeyframeMotion != null)
                    group.RootPart.KeyframeMotion.Delete();
                group.RootPart.KeyframeMotion = null;

                var idx = 0;

                var mode = KeyframeMotion.PlayMode.Forward;
                var data = KeyframeMotion.DataFormat.Translation | KeyframeMotion.DataFormat.Rotation;

                while (idx < options.Data.Length)
                {
                    int option = options.GetLSLIntegerItem(idx++);
                    var remain = options.Data.Length - idx;

                    switch (option)
                    {
                        case ScriptBaseClass.KFM_MODE:
                            if (remain < 1)
                                break;
                            int modeval = options.GetLSLIntegerItem(idx++);
                            switch (modeval)
                            {
                                case ScriptBaseClass.KFM_FORWARD:
                                    mode = KeyframeMotion.PlayMode.Forward;
                                    break;
                                case ScriptBaseClass.KFM_REVERSE:
                                    mode = KeyframeMotion.PlayMode.Reverse;
                                    break;
                                case ScriptBaseClass.KFM_LOOP:
                                    mode = KeyframeMotion.PlayMode.Loop;
                                    break;
                                case ScriptBaseClass.KFM_PING_PONG:
                                    mode = KeyframeMotion.PlayMode.PingPong;
                                    break;
                            }

                            break;
                        case ScriptBaseClass.KFM_DATA:
                            if (remain < 1)
                                break;
                            int dataval = options.GetLSLIntegerItem(idx++);
                            data = (KeyframeMotion.DataFormat)dataval;
                            break;
                    }
                }

                group.RootPart.KeyframeMotion = new KeyframeMotion(group, mode, data);

                idx = 0;

                var elemLength = 2;
                if (data == (KeyframeMotion.DataFormat.Translation | KeyframeMotion.DataFormat.Rotation))
                    elemLength = 3;

                var keyframes = new List<KeyframeMotion.Keyframe>();
                var hasTranslation = (data & KeyframeMotion.DataFormat.Translation) != 0;
                var hasRotation = (data & KeyframeMotion.DataFormat.Rotation) != 0;
                while (idx < frames.Data.Length)
                {
                    var remain = frames.Data.Length - idx;

                    if (remain < elemLength)
                        break;

                    var frame = new KeyframeMotion.Keyframe
                    {
                        Position = null,
                        Rotation = null
                    };

                    if (hasTranslation)
                    {
                        var tempv = frames.GetVector3Item(idx++);
                        frame.Position = new Vector3((float)tempv.x, (float)tempv.y, (float)tempv.z);
                    }

                    if (hasRotation)
                    {
                        var tempq = frames.GetQuaternionItem(idx++);
                        var q = new Quaternion((float)tempq.x, (float)tempq.y, (float)tempq.z, (float)tempq.s);
                        frame.Rotation = q;
                    }

                    var tempf = (float)frames.GetLSLFloatItem(idx++);
                    frame.TimeMS = (int)(tempf * 1000.0f);

                    keyframes.Add(frame);
                }

                group.RootPart.KeyframeMotion.SetKeyframes(keyframes.ToArray());
                group.RootPart.KeyframeMotion.Start();
            }
            else
            {
                if (group.RootPart.KeyframeMotion == null)
                    return;

                if (options.Data.Length == 0)
                {
                    group.RootPart.KeyframeMotion.Stop();
                    return;
                }

                var idx = 0;

                while (idx < options.Data.Length)
                {
                    int option = options.GetLSLIntegerItem(idx++);

                    switch (option)
                    {
                        case ScriptBaseClass.KFM_COMMAND:
                            int cmd = options.GetLSLIntegerItem(idx++);
                            switch (cmd)
                            {
                                case ScriptBaseClass.KFM_CMD_PLAY:
                                    group.RootPart.KeyframeMotion.Start();
                                    break;
                                case ScriptBaseClass.KFM_CMD_STOP:
                                    group.RootPart.KeyframeMotion.Stop();
                                    break;
                                case ScriptBaseClass.KFM_CMD_PAUSE:
                                    group.RootPart.KeyframeMotion.Pause();
                                    break;
                            }

                            break;
                    }
                }
            }
        }

        public LSL_List llGetPhysicsMaterial()
        {
            var result = new LSL_List();

            result.Add(new LSL_Float(m_host.GravityModifier));
            result.Add(new LSL_Float(m_host.Restitution));
            result.Add(new LSL_Float(m_host.Friction));
            result.Add(new LSL_Float(m_host.Density));

            return result;
        }

        public void llSetPhysicsMaterial(int material_bits,
            LSL_Float material_gravity_modifier, LSL_Float material_restitution,
            LSL_Float material_friction, LSL_Float material_density)
        {
            SetPhysicsMaterial(m_host, material_bits, (float)material_density, (float)material_friction,
                (float)material_restitution, (float)material_gravity_modifier);
        }

        public LSL_Vector llGetRootPosition()
        {
            return new LSL_Vector(m_host.ParentGroup.AbsolutePosition);
        }

        /// <summary>
        ///     http://lslwiki.net/lslwiki/wakka.php?wakka=llGetRot
        ///     http://lslwiki.net/lslwiki/wakka.php?wakka=ChildRotation
        ///     Also tested in sl in regards to the behaviour in attachments/mouselook
        ///     In the root prim:-
        ///     Returns the object rotation if not attached
        ///     Returns the avatars rotation if attached
        ///     Returns the camera rotation if attached and the avatar is in mouselook
        /// </summary>
        public LSL_Rotation llGetRootRotation()
        {
            Quaternion q;
            if (m_host.ParentGroup.AttachmentPoint != 0)
            {
                var avatar = World.GetScenePresence(m_host.ParentGroup.AttachedAvatar);
                if (avatar != null)
                    if ((avatar.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0)
                        q = avatar.CameraRotation; // Mouselook
                    else
                        q = avatar.GetWorldRotation(); // Currently infrequently updated so may be inaccurate
                else
                    q = m_host.ParentGroup.GroupRotation; // Likely never get here but just in case
            }
            else
            {
                q = m_host.ParentGroup.GroupRotation; // just the group rotation
            }

            return new LSL_Rotation(q);
        }

        public LSL_String llGetObjectDesc()
        {
            return m_host.Description ?? string.Empty;
        }

        public LSL_Key llGetCreator()
        {
            return m_host.CreatorID.ToString();
        }

        public LSL_Integer llGetNumberOfPrims()
        {
            return m_host.ParentGroup.PrimCount + m_host.ParentGroup.GetSittingAvatarsCount();
        }

        /// <summary>
        ///     Full implementation of llGetBoundingBox according to SL 2015-04-15.
        ///     http://wiki.secondlife.com/wiki/LlGetBoundingBox
        ///     http://lslwiki.net/lslwiki/wakka.php?wakka=llGetBoundingBox
        ///     Returns local bounding box of avatar without attachments
        ///     if target is non-seated avatar or prim/mesh in avatar attachment.
        ///     Returns local bounding box of object
        ///     if target is seated avatar or prim/mesh in object.
        ///     Uses less accurate box models for speed.
        /// </summary>
        public LSL_List llGetBoundingBox(string obj)
        {
            var result = new LSL_List();

            // If the ID is not valid, return null result
            if (!UUID.TryParse(obj, out var objID) || objID.IsZero())
            {
                result.Add(new LSL_Vector());
                result.Add(new LSL_Vector());
                return result;
            }

            // Check if this is an attached prim. If so, replace
            // the UUID with the avatar UUID and report it's bounding box
            var part = World.GetSceneObjectPart(objID);
            if (part != null && part.ParentGroup.IsAttachment)
                objID = part.ParentGroup.AttachedAvatar;

            // Find out if this is an avatar ID. If so, return it's box
            var presence = World.GetScenePresence(objID);
            if (presence != null)
            {
                LSL_Vector lower;
                LSL_Vector upper;

                var box = presence.Appearance.AvatarBoxSize * 0.5f;

                if (presence.Animator.Animations.ImplicitDefaultAnimation.AnimID.Equals(
                        DefaultAvatarAnimations.AnimsUUIDbyName["SIT_GROUND_CONSTRAINED"]))
                {
                    // This is for ground sitting avatars TODO!
                    lower = new LSL_Vector(-box.X - 0.1125, -box.Y, box.Z * -1.0f);
                    upper = new LSL_Vector(box.X + 0.1125, box.Y, box.Z * -1.0f);
                }
                else
                {
                    // This is for standing/flying avatars
                    lower = new LSL_Vector(-box.X, -box.Y, -box.Z);
                    upper = new LSL_Vector(box.X, box.Y, box.Z);
                }

                if (lower.x > upper.x)
                    lower.x = upper.x;
                if (lower.y > upper.y)
                    lower.y = upper.y;
                if (lower.z > upper.z)
                    lower.z = upper.z;

                result.Add(lower);
                result.Add(upper);
                return result;
            }

            part = World.GetSceneObjectPart(objID);

            // Currently only works for single prims without a sitting avatar
            if (part == null)
            {
                result.Add(new LSL_Vector());
                result.Add(new LSL_Vector());
                return result;
            }

            var sog = part.ParentGroup;
            if (sog.IsDeleted)
            {
                result.Add(new LSL_Vector());
                result.Add(new LSL_Vector());
                return result;
            }

            sog.GetBoundingBox(out var minX, out var maxX, out var minY, out var maxY, out var minZ, out var maxZ);

            result.Add(new LSL_Vector(minX, minY, minZ));
            result.Add(new LSL_Vector(maxX, maxY, maxZ));
            return result;
        }


        public LSL_Vector llGetGeometricCenter()
        {
            return new LSL_Vector(m_host.GetGeometricCenter());
        }

        public LSL_List llGetPrimitiveParams(LSL_List rules)
        {
            var result = new LSL_List();

            var remaining = GetPrimParams(m_host, rules, ref result);

            while (!(remaining is null) && remaining.Length > 1)
            {
                int linknumber = remaining.GetLSLIntegerItem(0);
                rules = remaining.GetSublist(1, -1);
                var parts = GetLinkParts(linknumber);
                if (parts.Count == 0)
                    break;
                foreach (var part in parts)
                    remaining = GetPrimParams(part, rules, ref result);
            }

            return result;
        }

        public LSL_List llGetLinkPrimitiveParams(int linknumber, LSL_List rules)
        {
            // according to SL wiki this must indicate a single link number or link_root or link_this.
            // keep other options as before

            List<SceneObjectPart> parts;
            List<ScenePresence> avatars;

            var res = new LSL_List();
            var remaining = new LSL_List();

            while (rules.Length > 0)
            {
                parts = GetLinkParts(linknumber);
                avatars = GetLinkAvatars(linknumber);

                remaining = new LSL_List();
                foreach (var part in parts) remaining = GetPrimParams(part, rules, ref res);
                foreach (var avatar in avatars) remaining = GetPrimParams(avatar, rules, ref res);

                if (remaining.Length > 0)
                {
                    linknumber = remaining.GetLSLIntegerItem(0);
                    rules = remaining.GetSublist(1, -1);
                }
                else
                {
                    break;
                }
            }

            return res;
        }

        public LSL_List llGetPrimMediaParams(int face, LSL_List rules)
        {
            ScriptSleep(m_sleepMsOnGetPrimMediaParams);
            return GetPrimMediaParams(m_host, face, rules);
        }

        public LSL_List llGetLinkMedia(LSL_Integer link, LSL_Integer face, LSL_List rules)
        {
            ScriptSleep(m_sleepMsOnGetLinkMedia);
            if (link == ScriptBaseClass.LINK_ROOT) return GetPrimMediaParams(m_host.ParentGroup.RootPart, face, rules);

            if (link == ScriptBaseClass.LINK_THIS) return GetPrimMediaParams(m_host, face, rules);

            var part = m_host.ParentGroup.GetLinkNumPart(link);
            if (null != part)
                return GetPrimMediaParams(part, face, rules);

            return new LSL_List();
        }

        public LSL_Integer llSetPrimMediaParams(LSL_Integer face, LSL_List rules)
        {
            ScriptSleep(m_sleepMsOnSetPrimMediaParams);
            return SetPrimMediaParams(m_host, face, rules);
        }

        public LSL_Integer llSetLinkMedia(LSL_Integer link, LSL_Integer face, LSL_List rules)
        {
            ScriptSleep(m_sleepMsOnSetLinkMedia);
            if (link == ScriptBaseClass.LINK_ROOT) return SetPrimMediaParams(m_host.ParentGroup.RootPart, face, rules);

            if (link == ScriptBaseClass.LINK_THIS) return SetPrimMediaParams(m_host, face, rules);

            var part = m_host.ParentGroup.GetLinkNumPart(link);
            if (null != part)
                return SetPrimMediaParams(part, face, rules);

            return ScriptBaseClass.LSL_STATUS_NOT_FOUND;
        }

        public LSL_Integer llClearPrimMedia(LSL_Integer face)
        {
            ScriptSleep(m_sleepMsOnClearPrimMedia);
            return ClearPrimMedia(m_host, face);
        }

        public LSL_Integer llClearLinkMedia(LSL_Integer link, LSL_Integer face)
        {
            ScriptSleep(m_sleepMsOnClearLinkMedia);
            if (link == ScriptBaseClass.LINK_ROOT) return ClearPrimMedia(m_host.ParentGroup.RootPart, face);

            if (link == ScriptBaseClass.LINK_THIS) return ClearPrimMedia(m_host, face);

            var part = m_host.ParentGroup.GetLinkNumPart(link);
            if (null != part)
                return ClearPrimMedia(part, face);

            return ScriptBaseClass.LSL_STATUS_NOT_FOUND;
        }

        public void llSetPayPrice(int price, LSL_List quick_pay_buttons)
        {
            if (m_host.LocalId != m_host.ParentGroup.RootPart.LocalId)
                return;

            if (quick_pay_buttons.Data.Length < 4)
            {
                int x;
                for (x = quick_pay_buttons.Data.Length; x <= 4; x++) quick_pay_buttons.Add(ScriptBaseClass.PAY_HIDE);
            }

            var nPrice = new int[5];
            nPrice[0] = price;
            nPrice[1] = quick_pay_buttons.GetLSLIntegerItem(0);
            nPrice[2] = quick_pay_buttons.GetLSLIntegerItem(1);
            nPrice[3] = quick_pay_buttons.GetLSLIntegerItem(2);
            nPrice[4] = quick_pay_buttons.GetLSLIntegerItem(3);
            m_host.ParentGroup.RootPart.PayPrice = nPrice;
            m_host.ParentGroup.HasGroupChanged = true;
        }


        public LSL_Integer llGetLinkNumberOfSides(LSL_Integer link)
        {
            var parts = GetLinkParts(link);
            if (parts.Count < 1)
                return 0;

            return GetNumberOfSides(parts[0]);
        }
    }
}