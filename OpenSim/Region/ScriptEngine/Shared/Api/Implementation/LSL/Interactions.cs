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
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Scripting;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Services.Connectors.Hypergrid;
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
        public void llListenControl(int number, int active)
        {
            var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            if (wComm != null)
                wComm.ListenControl(m_item.ItemID, number, active);
        }

        public void llListenRemove(int number)
        {
            var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            if (wComm != null)
                wComm.ListenRemove(m_item.ItemID, number);
        }


        public void llSensor(string name, string id, int type, double range, double arc)
        {
            UUID.TryParse(id, out var keyID);
            m_AsyncCommands.SensorRepeatPlugin.SenseOnce(m_host.LocalId, m_item.ItemID, name, keyID, type, range, arc,
                m_host);
        }

        public void llSensorRepeat(string name, string id, int type, double range, double arc, double rate)
        {
            UUID.TryParse(id, out var keyID);
            m_AsyncCommands.SensorRepeatPlugin.SetSenseRepeatEvent(m_host.LocalId, m_item.ItemID, name, keyID, type,
                range, arc, rate, m_host);
        }

        public void llSensorRemove()
        {
            m_AsyncCommands.SensorRepeatPlugin.UnSetSenseRepeaterEvents(m_host.LocalId, m_item.ItemID);
        }


        public void llTakeControls(int controls, int accept, int pass_on)
        {
            if (!m_item.PermsGranter.IsZero())
            {
                var presence = World.GetScenePresence(m_item.PermsGranter);

                if (presence != null)
                    if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_TAKE_CONTROLS) != 0)
                        presence.RegisterControlEventsToScript(controls, accept, pass_on, m_host.LocalId,
                            m_item.ItemID);
            }
        }

        public void llReleaseControls()
        {
            if (!m_item.PermsGranter.IsZero())
            {
                var presence = World.GetScenePresence(m_item.PermsGranter);

                if (presence != null)
                    if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_TAKE_CONTROLS) != 0)
                    {
                        // Unregister controls from Presence
                        presence.UnRegisterControlEventsToScript(m_host.LocalId, m_item.ItemID);
                        // Remove Take Control permission.
                        m_item.PermsMask &= ~ScriptBaseClass.PERMISSION_TAKE_CONTROLS;
                    }
            }
        }

        public void llDetachFromAvatar()
        {
            if (m_host.ParentGroup.AttachmentPoint == 0)
                return;

            if (m_item.PermsGranter != m_host.OwnerID)
                return;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_ATTACH) != 0)
                DetachFromAvatar();
        }

        public void llStartAnimation(string anim)
        {
            if (m_item.PermsGranter.IsZero())
                return;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION) != 0)
            {
                var presence = World.GetScenePresence(m_item.PermsGranter);

                if (presence != null)
                {
                    // Do NOT try to parse UUID, animations cannot be triggered by ID
                    var animID = ScriptUtils.GetAssetIdFromItemName(m_host, anim, (int)AssetType.Animation);
                    if (animID.IsZero())
                        presence.Animator.AddAnimation(anim, m_host.UUID);
                    else
                        presence.Animator.AddAnimation(animID, m_host.UUID);
                }
            }
        }

        public void llStopAnimation(string anim)
        {
            if (m_item.PermsGranter.IsZero())
                return;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION) != 0)
            {
                var presence = World.GetScenePresence(m_item.PermsGranter);

                if (presence != null)
                {
                    var animID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, anim);
                    if (animID.IsNotZero())
                        presence.Animator.RemoveAnimation(animID, true);
                    else if (presence.TryGetAnimationOverride(anim.ToUpper(), out var sitanimID))
                        presence.Animator.RemoveAnimation(sitanimID, true);
                    else
                        presence.Animator.RemoveAnimation(anim);
                }
            }
        }

        public void llRequestPermissions(string agent, int perm)
        {
            if (!UUID.TryParse(agent, out var agentID) || agentID.IsZero())
                return;

            if (agentID == UUID.Zero || perm == 0) // Releasing permissions
            {
                llReleaseControls();

                m_item.PermsGranter = UUID.Zero;
                m_item.PermsMask = 0;

                m_ScriptEngine.PostScriptEvent(m_item.ItemID, new EventParams(
                    "run_time_permissions", new object[]
                    {
                        new LSL_Integer(0)
                    },
                    new DetectParams[0]));

                return;
            }

            if (m_item.PermsGranter != agentID || (perm & ScriptBaseClass.PERMISSION_TAKE_CONTROLS) == 0)
                llReleaseControls();


            var implicitPerms = 0;

            if (m_host.ParentGroup.IsAttachment && (UUID)agent == m_host.ParentGroup.AttachedAvatar)
            {
                // When attached, certain permissions are implicit if requested from owner
                implicitPerms = ScriptBaseClass.PERMISSION_TAKE_CONTROLS |
                                ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION |
                                ScriptBaseClass.PERMISSION_CONTROL_CAMERA |
                                ScriptBaseClass.PERMISSION_TRACK_CAMERA |
                                ScriptBaseClass.PERMISSION_ATTACH |
                                ScriptBaseClass.PERMISSION_OVERRIDE_ANIMATIONS;
            }
            else
            {
                if (m_host.ParentGroup.HasSittingAvatar(agentID))
                {
                    // When agent is sitting, certain permissions are implicit if requested from sitting agent
                    implicitPerms = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION |
                                    ScriptBaseClass.PERMISSION_CONTROL_CAMERA |
                                    ScriptBaseClass.PERMISSION_TRACK_CAMERA |
                                    ScriptBaseClass.PERMISSION_TAKE_CONTROLS;
                }
                else
                {
                    if (World.GetExtraSetting("auto_grant_attach_perms") == "true")
                        implicitPerms = ScriptBaseClass.PERMISSION_ATTACH;
                }

                if (World.GetExtraSetting("auto_grant_all_perms") == "true") implicitPerms = perm;
            }

            if ((perm & ~implicitPerms) == 0) // Requested only implicit perms
            {
                m_host.TaskInventory.LockItemsForWrite(true);
                m_host.TaskInventory[m_item.ItemID].PermsGranter = agentID;
                m_host.TaskInventory[m_item.ItemID].PermsMask = perm;
                m_host.TaskInventory.LockItemsForWrite(false);

                m_ScriptEngine.PostScriptEvent(m_item.ItemID, new EventParams(
                    "run_time_permissions", new object[]
                    {
                        new LSL_Integer(perm)
                    },
                    new DetectParams[0]));

                return;
            }

            var presence = World.GetScenePresence(agentID);

            if (presence != null)
            {
                // If permissions are being requested from an NPC and were not implicitly granted above then
                // auto grant all requested permissions if the script is owned by the NPC or the NPCs owner
                var npcModule = World.RequestModuleInterface<INPCModule>();
                if (npcModule != null && npcModule.IsNPC(agentID, World))
                {
                    if (npcModule.CheckPermissions(agentID, m_host.OwnerID))
                    {
                        lock (m_host.TaskInventory)
                        {
                            m_host.TaskInventory[m_item.ItemID].PermsGranter = agentID;
                            m_host.TaskInventory[m_item.ItemID].PermsMask = perm;
                        }

                        m_ScriptEngine.PostScriptEvent(
                            m_item.ItemID,
                            new EventParams(
                                "run_time_permissions", new object[] { new LSL_Integer(perm) }, new DetectParams[0]));
                    }

                    // it is an NPC, exit even if the permissions werent granted above, they are not going to answer
                    // the question!
                    return;
                }

                var ownerName = resolveName(m_host.ParentGroup.RootPart.OwnerID);
                if (ownerName == string.Empty)
                    ownerName = "(hippos)";

                if (!m_waitingForScriptAnswer)
                {
                    m_host.TaskInventory.LockItemsForWrite(true);
                    m_host.TaskInventory[m_item.ItemID].PermsGranter = agentID;
                    m_host.TaskInventory[m_item.ItemID].PermsMask = 0;
                    m_host.TaskInventory.LockItemsForWrite(false);

                    presence.ControllingClient.OnScriptAnswer += handleScriptAnswer;
                    m_waitingForScriptAnswer = true;
                }

                presence.ControllingClient.SendScriptQuestion(
                    m_host.UUID, m_host.ParentGroup.RootPart.Name, ownerName, m_item.ItemID, perm);

                return;
            }

            // Requested agent is not in range, refuse perms
            m_ScriptEngine.PostScriptEvent(
                m_item.ItemID,
                new EventParams("run_time_permissions", new object[] { new LSL_Integer(0) }, new DetectParams[0]));
        }


        public void llTeleportAgentHome(string agent)
        {
            if (UUID.TryParse(agent, out var agentId) && agentId.IsNotZero())
            {
                var presence = World.GetScenePresence(agentId);
                if (presence == null || presence.IsDeleted || presence.IsChildAgent || presence.IsNPC ||
                    presence.IsInTransit)
                    return;

                // agent must not be a god
                if (presence.GodController.UserLevel >= 200)
                    return;

                // agent must be over the owners land
                if (m_host.OwnerID.Equals(World.LandChannel.GetLandObject(presence.AbsolutePosition).LandData.OwnerID))
                    World.TeleportClientHome(agentId, presence.ControllingClient);
            }

            ScriptSleep(m_sleepMsOnSetDamage);
        }

        public void llTeleportAgent(string agent, string destination, LSL_Vector targetPos,
            LSL_Vector targetLookAt)
        {
            // If attached using llAttachToAvatarTemp, cowardly refuse
            if (m_host.ParentGroup.AttachmentPoint != 0 && m_host.ParentGroup.FromItemID.IsZero())
                return;

            if (UUID.TryParse(agent, out var agentId) && agentId.IsNotZero())
            {
                var presence = World.GetScenePresence(agentId);
                if (presence == null || presence.IsDeleted || presence.IsChildAgent || presence.IsNPC ||
                    presence.IsSatOnObject || presence.IsInTransit)
                    return;

                if (m_item.PermsGranter.Equals(agentId))
                    if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_TELEPORT) != 0)
                    {
                        DoLLTeleport(presence, destination, targetPos, targetLookAt);
                        return;
                    }

                // special opensim legacy extra permissions, possible to remove
                // agent must be wearing the object
                if (m_host.ParentGroup.AttachmentPoint != 0 && m_host.OwnerID.Equals(presence.UUID))
                {
                    DoLLTeleport(presence, destination, targetPos, targetLookAt);
                    return;
                }

                // agent must not be a god
                if (presence.IsViewerUIGod)
                    return;

                // agent must be over the owners land
                var agentLand = World.LandChannel.GetLandObject(presence.AbsolutePosition);
                var objectLand = World.LandChannel.GetLandObject(m_host.AbsolutePosition);
                if (m_host.OwnerID.Equals(objectLand.LandData.OwnerID) &&
                    m_host.OwnerID.Equals(agentLand.LandData.OwnerID))
                    DoLLTeleport(presence, destination, targetPos, targetLookAt);
            }
        }

        public void llTeleportAgentGlobalCoords(string agent, LSL_Vector global_coords,
            LSL_Vector targetPos,
            LSL_Vector targetLookAt)
        {
            // If attached using llAttachToAvatarTemp, cowardly refuse
            if (m_host.ParentGroup.AttachmentPoint != 0 && m_host.ParentGroup.FromItemID.IsZero())
                return;

            if (UUID.TryParse(agent, out var agentId) && agentId.IsNotZero())
            {
                // This function is owner only!
                if (m_host.OwnerID.NotEqual(agentId))
                    return;

                var presence = World.GetScenePresence(agentId);
                if (presence == null || presence.IsDeleted || presence.IsChildAgent || presence.IsNPC ||
                    presence.IsSatOnObject || presence.IsInTransit)
                    return;

                if (m_item.PermsGranter.Equals(agentId))
                    if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_TELEPORT) != 0)
                    {
                        var regionHandle = Util.RegionWorldLocToHandle((uint)global_coords.x, (uint)global_coords.y);
                        World.RequestTeleportLocation(presence.ControllingClient, regionHandle, targetPos, targetLookAt,
                            (uint)TeleportFlags.ViaLocation);
                    }
            }
        }

        public void llTextBox(string agent, string message, int chatChannel)
        {
            var dm = World.RequestModuleInterface<IDialogModule>();

            if (dm == null)
                return;

            if (!UUID.TryParse(agent, out var av) || av.IsZero())
            {
                Error("llTextBox", "First parameter must be a valid agent key");
                return;
            }

            if (message.Length == 0)
            {
                Error("llTextBox", "Empty message");
            }
            else if (Encoding.UTF8.GetByteCount(message) > 512)
            {
                Error("llTextBox", "Message longer than 512 bytes");
            }
            else if (m_host.GetOwnerName(out var fname, out var lname))
            {
                dm.SendTextBoxToUser(av, message, chatChannel, m_host.Name, m_host.UUID, fname, lname, m_host.OwnerID);
                ScriptSleep(m_sleepMsOnTextBox);
            }
        }

        public void llModifyLand(int action, int brush)
        {
            var tm = m_ScriptEngine.World.RequestModuleInterface<ITerrainModule>();
            if (tm != null) tm.ModifyTerrain(m_host.OwnerID, m_host.AbsolutePosition, (byte)brush, (byte)action);
        }

        public void llCollisionSound(LSL_Key impact_sound, LSL_Float impact_volume)
        {
            if (string.IsNullOrEmpty(impact_sound.m_string))
            {
                m_host.CollisionSoundVolume = (float)impact_volume;
                m_host.CollisionSound = m_host.invalidCollisionSoundUUID;
                m_host.CollisionSoundType = -1; // disable all sounds
                m_host.aggregateScriptEvents();
                return;
            }

            // TODO: Parameter check logic required.
            var soundId = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, impact_sound, AssetType.Sound);
            if (soundId.IsZero())
            {
                m_host.CollisionSoundType = -1;
            }
            else
            {
                m_host.CollisionSound = soundId;
                m_host.CollisionSoundVolume = (float)impact_volume;
                m_host.CollisionSoundType = 1;
            }

            m_host.aggregateScriptEvents();
        }


        public LSL_Key llKey2Name(LSL_Key id)
        {
            if (UUID.TryParse(id, out var key) && key.IsNotZero())
            {
                var presence = World.GetScenePresence(key);
                if (presence != null) return presence.Name;
                var sop = World.GetSceneObjectPart(key);
                if (sop != null) return sop.Name;
            }

            return string.Empty;
        }

        public LSL_Key llName2Key(LSL_Key name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return ScriptBaseClass.NULL_KEY;

            var nc = Util.ParseAvatarName(name, out var firstName, out var lastName, out var server);
            if (nc < 2)
                return ScriptBaseClass.NULL_KEY;

            string sname;
            if (nc == 2)
                sname = firstName + " " + lastName;
            else
                sname = firstName + "." + lastName + " @" + server;

            foreach (var sp in World.GetScenePresences())
            {
                if (sp.IsDeleted || sp.IsChildAgent)
                    continue;
                if (string.Compare(sname, sp.Name, true) == 0)
                    return sp.UUID.ToString();
            }

            return ScriptBaseClass.NULL_KEY;
        }

        public LSL_Key llRequestUserKey(LSL_Key username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return ScriptBaseClass.NULL_KEY;

            var nc = Util.ParseAvatarName(username, out var firstName, out var lastName, out var server);
            if (nc < 2)
                return ScriptBaseClass.NULL_KEY;

            string sname;
            if (nc == 2)
                sname = firstName + " " + lastName;
            else
                sname = firstName + "." + lastName + " @" + server;

            foreach (var sp in World.GetScenePresences())
            {
                if (sp.IsDeleted || sp.IsChildAgent)
                    continue;
                if (string.Compare(sname, sp.Name, true) == 0)
                {
                    var ftid = m_AsyncCommands.DataserverPlugin.RequestWithImediatePost(m_host.LocalId,
                        m_item.ItemID, sp.UUID.ToString());
                    return ftid;
                }
            }

            Action<string> act = eventID =>
            {
                var reply = ScriptBaseClass.NULL_KEY;
                var userID = UUID.Zero;
                var userManager = World.RequestModuleInterface<IUserManagement>();
                if (nc == 2)
                {
                    if (userManager != null)
                    {
                        userID = userManager.GetUserIdByName(firstName, lastName);
                        if (!userID.IsZero())
                            reply = userID.ToString();
                    }
                }
                else
                {
                    var url = "http://" + server;
                    if (Uri.TryCreate(url, UriKind.Absolute, out var dummy))
                    {
                        var notfound = true;
                        if (userManager != null)
                        {
                            var hgfirst = firstName + "." + lastName;
                            var hglast = "@" + server;
                            userID = userManager.GetUserIdByName(hgfirst, hglast);
                            if (!userID.IsZero())
                            {
                                notfound = false;
                                reply = userID.ToString();
                            }
                        }

                        if (notfound)
                            try
                            {
                                var userConnection = new UserAgentServiceConnector(url);
                                if (userConnection != null)
                                {
                                    userID = userConnection.GetUUID(firstName, lastName);
                                    if (!userID.IsZero())
                                    {
                                        if (userManager != null)
                                            userManager.AddUser(userID, firstName, lastName, url);
                                        reply = userID.ToString();
                                    }
                                }
                            }
                            catch
                            {
                                reply = ScriptBaseClass.NULL_KEY;
                            }
                    }
                }

                m_AsyncCommands.DataserverPlugin.DataserverReply(eventID, reply);
            };

            var tid = m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId, m_item.ItemID, act);
            ScriptSleep(m_sleepMsOnRequestAgentData);
            return tid.ToString();
        }

        public LSL_Integer llGetAttached()
        {
            return m_host.ParentGroup.AttachmentPoint;
        }

        public LSL_List llGetAttachedList(LSL_Key id)
        {
            if (!UUID.TryParse(id, out var avID) || avID.IsZero())
                return new LSL_List("NOT_FOUND");

            var av = World.GetScenePresence(avID);
            if (av == null || av.IsDeleted)
                return new LSL_List("NOT_FOUND");

            if (av.IsChildAgent || av.IsInTransit)
                return new LSL_List("NOT_ON_REGION");

            var AttachmentsList = new LSL_List();
            List<SceneObjectGroup> Attachments;

            Attachments = av.GetAttachments();

            foreach (var Attachment in Attachments)
            {
                if (Attachment.HasPrivateAttachmentPoint)
                    continue;
                AttachmentsList.Add(new LSL_Key(Attachment.UUID.ToString()));
            }

            return AttachmentsList;
        }


        public void llSetTouchText(string text)
        {
            if (text.Length <= 9)
                m_host.TouchName = text;
            else
                m_host.TouchName = text.Substring(0, 9);
        }

        public void llSetSitText(string text)
        {
            if (text.Length <= 9)
                m_host.SitName = text;
            else
                m_host.SitName = text.Substring(0, 9);
        }

        public void llForceMouselook(int mouselook)
        {
            m_host.SetForceMouselook(mouselook != 0);
        }

        public void llClearCameraParams()
        {
            // the object we are in
            var objectID = m_host.ParentUUID;
            if (objectID.IsZero())
                return;

            // we need the permission first, to know which avatar we want to clear the camera for
            var agentID = m_item.PermsGranter;

            if (agentID.IsZero())
                return;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_CONTROL_CAMERA) == 0)
                return;

            var presence = World.GetScenePresence(agentID);

            // we are not interested in child-agents
            if (presence.IsChildAgent)
                return;

            presence.ControllingClient.SendClearFollowCamProperties(objectID);
        }


        public LSL_Integer llListen(int channelID, string name, string ID, string msg)
        {
            var wComm = m_ScriptEngine.World.RequestModuleInterface<IWorldComm>();
            if (wComm == null)
                return -1;

            UUID.TryParse(ID, out var keyID);
            return wComm.Listen(m_item.ItemID, m_host.UUID, channelID, name, keyID, msg);
        }

        public LSL_String llDetectedName(int number)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (detectedParams == null)
                return string.Empty;
            return detectedParams.Name;
        }

        public LSL_Key llDetectedKey(int number)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (detectedParams == null)
                return string.Empty;
            return detectedParams.Key.ToString();
        }

        public LSL_Key llDetectedOwner(int number)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (detectedParams == null)
                return string.Empty;
            return detectedParams.Owner.ToString();
        }

        public LSL_Integer llDetectedType(int number)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (detectedParams == null)
                return 0;
            return new LSL_Integer(detectedParams.Type);
        }

        public LSL_Vector llDetectedPos(int number)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.Position;
        }

        public LSL_Vector llDetectedVel(int number)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.Velocity;
        }

        public LSL_Vector llDetectedGrab(int number)
        {
            var parms = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (parms == null)
                return new LSL_Vector(0, 0, 0);

            return parms.OffsetPos;
        }

        public LSL_Rotation llDetectedRot(int number)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (detectedParams == null)
                return new LSL_Rotation();
            return detectedParams.Rotation;
        }

        public LSL_Integer llDetectedGroup(int number)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (detectedParams == null)
                return new LSL_Integer(0);
            if (m_host.GroupID.Equals(detectedParams.Group))
                return new LSL_Integer(1);
            return new LSL_Integer(0);
        }

        public LSL_Integer llDetectedLinkNumber(int number)
        {
            var parms = m_ScriptEngine.GetDetectParams(m_item.ItemID, number);
            if (parms == null)
                return new LSL_Integer(0);

            return new LSL_Integer(parms.LinkNum);
        }

        /// <summary>
        ///     See http://wiki.secondlife.com/wiki/LlDetectedTouchBinormal for details
        /// </summary>
        public LSL_Vector llDetectedTouchBinormal(int index)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, index);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.TouchBinormal;
        }

        /// <summary>
        ///     See http://wiki.secondlife.com/wiki/LlDetectedTouchFace for details
        /// </summary>
        public LSL_Integer llDetectedTouchFace(int index)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, index);
            if (detectedParams == null)
                return new LSL_Integer(-1);
            return new LSL_Integer(detectedParams.TouchFace);
        }

        /// <summary>
        ///     See http://wiki.secondlife.com/wiki/LlDetectedTouchNormal for details
        /// </summary>
        public LSL_Vector llDetectedTouchNormal(int index)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, index);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.TouchNormal;
        }

        /// <summary>
        ///     See http://wiki.secondlife.com/wiki/LlDetectedTouchPos for details
        /// </summary>
        public LSL_Vector llDetectedTouchPos(int index)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, index);
            if (detectedParams == null)
                return new LSL_Vector();
            return detectedParams.TouchPos;
        }

        /// <summary>
        ///     See http://wiki.secondlife.com/wiki/LlDetectedTouchST for details
        /// </summary>
        public LSL_Vector llDetectedTouchST(int index)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, index);
            if (detectedParams == null)
                return new LSL_Vector(-1.0, -1.0, 0.0);
            return detectedParams.TouchST;
        }

        /// <summary>
        ///     See http://wiki.secondlife.com/wiki/LlDetectedTouchUV for details
        /// </summary>
        public LSL_Vector llDetectedTouchUV(int index)
        {
            var detectedParams = m_ScriptEngine.GetDetectParams(m_item.ItemID, index);
            if (detectedParams == null)
                return new LSL_Vector(-1.0, -1.0, 0.0);
            return detectedParams.TouchUV;
        }

        public void llAttachToAvatar(LSL_Integer attachmentPoint)
        {
            if (m_item.PermsGranter != m_host.OwnerID)
                return;

            var grp = m_host.ParentGroup;
            if (grp == null || grp.IsDeleted || grp.IsAttachment)
                return;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_ATTACH) != 0)
                AttachToAvatar(attachmentPoint);
        }

        public void llAttachToAvatarTemp(LSL_Integer attachmentPoint)
        {
            var attachmentsModule = World.RequestModuleInterface<IAttachmentsModule>();
            if (attachmentsModule == null)
                return;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_ATTACH) == 0)
                return;

            var grp = m_host.ParentGroup;
            if (grp == null || grp.IsDeleted || grp.IsAttachment)
                return;

            if (!World.TryGetScenePresence(m_item.PermsGranter, out ScenePresence target))
                return;

            if (target.UUID != grp.OwnerID)
            {
                var effectivePerms = grp.EffectiveOwnerPerms;

                if ((effectivePerms & (uint)PermissionMask.Transfer) == 0)
                    return;

                var permsgranter = m_item.PermsGranter;
                var permsmask = m_item.PermsMask;

                grp.SetOwner(target.UUID, target.ControllingClient.ActiveGroupId);

                if (World.Permissions.PropagatePermissions())
                {
                    foreach (var child in grp.Parts)
                    {
                        child.Inventory.ChangeInventoryOwner(target.UUID);
                        child.TriggerScriptChangedEvent(Changed.OWNER);
                        child.ApplyNextOwnerPermissions();
                    }

                    grp.InvalidateEffectivePerms();
                }

                m_item.PermsMask = permsmask;
                m_item.PermsGranter = permsgranter;

                grp.RootPart.ObjectSaleType = 0;
                grp.RootPart.SalePrice = 10;

                grp.HasGroupChanged = true;
                grp.RootPart.SendPropertiesToClient(target.ControllingClient);
                grp.RootPart.ScheduleFullUpdate();
            }

            attachmentsModule.AttachObject(target, grp, (uint)attachmentPoint, false, false, true);
        }


        public LSL_Key llGetPermissionsKey()
        {
            return m_item.PermsGranter.ToString();
        }

        public LSL_Integer llGetPermissions()
        {
            var perms = m_item.PermsMask;

            if (m_automaticLinkPermission)
                perms |= ScriptBaseClass.PERMISSION_CHANGE_LINKS;

            return perms;
        }

        public void llGiveInventory(LSL_Key destination, LSL_String inventory)
        {
            if (!UUID.TryParse(destination, out var destId) || destId.IsZero())
            {
                Error("llGiveInventory", "Can't parse destination key '" + destination + "'");
                return;
            }

            var item = m_host.Inventory.GetInventoryItem(inventory);
            if (item == null)
            {
                Error("llGiveInventory", "Can't find inventory object '" + inventory + "'");
                return;
            }

            var objId = item.ItemID;

            // check if destination is an object
            if (World.GetSceneObjectPart(destId) != null)
            {
                // destination is an object
                World.MoveTaskInventoryItem(destId, m_host, objId);
            }
            else
            {
                var presence = World.GetScenePresence(destId);

                if (presence == null)
                {
                    var account = m_userAccountService.GetUserAccount(RegionScopeID, destId);

                    if (account == null)
                    {
                        var info = World.GridUserService.GetGridUserInfo(destId.ToString());
                        if (info == null || info.Online == false)
                        {
                            Error("llGiveInventory", "Can't find destination '" + destId + "'");
                            return;
                        }
                    }
                }

                // destination is an avatar
                var agentItem =
                    World.MoveTaskInventoryItem(destId, UUID.Zero, m_host, objId, out var message);

                if (agentItem == null)
                {
                    llSay(0, message);
                    return;
                }

                var bucket = new byte[1];
                bucket[0] = (byte)item.Type;

                var msg = new GridInstantMessage(World,
                    m_host.OwnerID, m_host.Name, destId,
                    (byte)InstantMessageDialog.TaskInventoryOffered,
                    m_host.OwnerID.Equals(m_host.GroupID), "'" + item.Name + "'. (" + m_host.Name + " is located at " +
                                                           m_regionName + " " + m_host.AbsolutePosition +
                                                           ")",
                    agentItem.ID, true, m_host.AbsolutePosition,
                    bucket, true);

                if (World.TryGetScenePresence(destId, out ScenePresence sp))
                    sp.ControllingClient.SendInstantMessage(msg);
                else
                    m_TransferModule?.SendInstantMessage(msg, delegate { });

                //This delay should only occur when giving inventory to avatars.
                ScriptSleep(m_sleepMsOnGiveInventory);
            }
        }

        public LSL_String llGetAnimation(LSL_Key id)
        {
            // This should only return a value if the avatar is in the same region
            if (!UUID.TryParse(id, out var avatar) || avatar.IsZero())
                return "";

            var presence = World.GetScenePresence(avatar);
            if (presence == null || presence.IsChildAgent || presence.Animator == null)
                return string.Empty;

            //if (presence.SitGround)
            //    return "Sitting on Ground";
            //if (presence.ParentID != 0 || presence.ParentUUID != UUID.Zero)
            //    return "Sitting";
            var movementAnimation = presence.Animator.CurrentMovementAnimation;
            if (MovementAnimationsForLSL.TryGetValue(movementAnimation, out var lslMovementAnimation))
                return lslMovementAnimation;

            return string.Empty;
        }


        public void llPushObject(string target, LSL_Vector impulse, LSL_Vector ang_impulse, int local)
        {
            var pushrestricted = World.RegionInfo.RegionSettings.RestrictPushing;
            var pushAllowed = false;

            var pusheeIsAvatar = false;

            if (!UUID.TryParse(target, out var targetID) || targetID.IsZero())
                return;

            ScenePresence pusheeav = null;
            var PusheePos = Vector3.Zero;
            SceneObjectPart pusheeob = null;

            var avatar = World.GetScenePresence(targetID);
            if (avatar != null)
            {
                pusheeIsAvatar = true;

                // Pushee doesn't have a physics actor
                if (avatar.PhysicsActor == null)
                    return;

                // Pushee is in GodMode this pushing object isn't owned by them
                if (avatar.IsViewerUIGod && m_host.OwnerID != targetID)
                    return;

                pusheeav = avatar;

                // Find pushee position
                // Pushee Linked?
                var sitPart = pusheeav.ParentPart;
                if (sitPart != null)
                    PusheePos = sitPart.AbsolutePosition;
                else
                    PusheePos = pusheeav.AbsolutePosition;
            }

            if (!pusheeIsAvatar)
            {
                // not an avatar so push is not affected by parcel flags
                pusheeob = World.GetSceneObjectPart((UUID)target);

                // We can't find object
                if (pusheeob == null)
                    return;

                // Object not pushable.  Not an attachment and has no physics component
                if (!pusheeob.ParentGroup.IsAttachment && pusheeob.PhysActor == null)
                    return;

                PusheePos = pusheeob.AbsolutePosition;
                pushAllowed = true;
            }
            else
            {
                if (pushrestricted)
                {
                    var targetlandObj = World.LandChannel.GetLandObject(PusheePos);

                    // We didn't find the parcel but region is push restricted so assume it is NOT ok
                    if (targetlandObj == null)
                        return;

                    // Need provisions for Group Owned here
                    if (m_host.OwnerID.Equals(targetlandObj.LandData.OwnerID) ||
                        targetlandObj.LandData.IsGroupOwned || m_host.OwnerID.Equals(targetID))
                        pushAllowed = true;
                }
                else
                {
                    var targetlandObj = World.LandChannel.GetLandObject(PusheePos);
                    if (targetlandObj == null)
                    {
                        // We didn't find the parcel but region isn't push restricted so assume it's ok
                        pushAllowed = true;
                    }
                    else
                    {
                        // Parcel push restriction
                        if ((targetlandObj.LandData.Flags & (uint)ParcelFlags.RestrictPushObject) ==
                            (uint)ParcelFlags.RestrictPushObject)
                        {
                            // Need provisions for Group Owned here
                            if (m_host.OwnerID.Equals(targetlandObj.LandData.OwnerID) ||
                                targetlandObj.LandData.IsGroupOwned ||
                                m_host.OwnerID.Equals(targetID))
                                pushAllowed = true;

                            //ParcelFlags.RestrictPushObject
                            //pushAllowed = true;
                        }
                        else
                        {
                            // Parcel isn't push restricted
                            pushAllowed = true;
                        }
                    }
                }
            }

            if (pushAllowed)
            {
                var distance = (PusheePos - m_host.AbsolutePosition).Length();
                var distance_term = distance * distance * distance; // Script Energy
                // use total object mass and not part
                var pusher_mass = m_host.ParentGroup.GetMass();

                var PUSH_ATTENUATION_DISTANCE = 17f;
                var PUSH_ATTENUATION_SCALE = 5f;
                var distance_attenuation = 1f;
                if (distance > PUSH_ATTENUATION_DISTANCE)
                {
                    var normalized_units = 1f + (distance - PUSH_ATTENUATION_DISTANCE) / PUSH_ATTENUATION_SCALE;
                    distance_attenuation = 1f / normalized_units;
                }

                Vector3 applied_linear_impulse = impulse;
                {
                    var impulse_length = applied_linear_impulse.Length();

                    var desired_energy = impulse_length * pusher_mass;
                    if (desired_energy > 0f)
                        desired_energy += distance_term;

                    var scaling_factor = 1f;
                    scaling_factor *= distance_attenuation;
                    applied_linear_impulse *= scaling_factor;
                }

                if (pusheeIsAvatar)
                {
                    if (pusheeav != null)
                    {
                        var pa = pusheeav.PhysicsActor;

                        if (pa != null)
                        {
                            if (local != 0)
                                //                                applied_linear_impulse *= m_host.GetWorldRotation();
                                applied_linear_impulse *= pusheeav.GetWorldRotation();

                            pa.AddForce(applied_linear_impulse, true);
                        }
                    }
                }
                else
                {
                    if (pusheeob != null)
                        if (pusheeob.PhysActor != null)
                            pusheeob.ApplyImpulse(applied_linear_impulse, local != 0);
                }
            }
        }

        public void llGiveInventoryList(LSL_Key destination, LSL_String category, LSL_List inventory)
        {
            if (inventory.Length == 0)
                return;

            if (!UUID.TryParse(destination, out var destID) || destID.IsZero())
                return;

            var isNotOwner = true;
            if (!World.TryGetSceneObjectPart(destID, out var destSop))
            {
                if (!World.TryGetScenePresence(destID, out ScenePresence sp))
                {
                    // we could check if it is a grid user and allow the transfer as in older code
                    // but that increases security risk
                    Error("llGiveInventoryList", "Unable to give list, destination not found");
                    ScriptSleep(100);
                    return;
                }

                isNotOwner = sp.UUID.NotEqual(m_host.OwnerID);
            }

            var itemList = new List<UUID>(inventory.Length);
            foreach (var item in inventory.Data)
            {
                var rawItemString = item.ToString();
                TaskInventoryItem taskItem = null;

                if (UUID.TryParse(rawItemString, out var itemID))
                    taskItem = m_host.Inventory.GetInventoryItem(itemID);
                else
                    taskItem = m_host.Inventory.GetInventoryItem(rawItemString);

                if (taskItem == null)
                    continue;

                if ((taskItem.CurrentPermissions & (uint)PermissionMask.Copy) == 0)
                    continue;

                if (destSop != null)
                {
                    if (!World.Permissions.CanDoObjectInvToObjectInv(taskItem, m_host, destSop))
                        continue;
                }
                else
                {
                    if (isNotOwner)
                        if ((taskItem.CurrentPermissions & (uint)PermissionMask.Transfer) == 0)
                            continue;
                }

                itemList.Add(taskItem.ItemID);
            }

            if (itemList.Count == 0)
            {
                Error("llGiveInventoryList", "Unable to give list, no items found");
                ScriptSleep(100);
                return;
            }

            var folderID = m_ScriptEngine.World.MoveTaskInventoryItems(destID, category, m_host, itemList, false);

            if (folderID.IsZero())
            {
                Error("llGiveInventoryList", "Unable to give list");
                ScriptSleep(100);
                return;
            }

            if (destSop != null)
            {
                ScriptSleep(100);
                return;
            }

            if (m_TransferModule != null)
            {
                byte[] bucket = { (byte)AssetType.Folder };

                var pos = m_host.AbsolutePosition;

                var msg = new GridInstantMessage(World,
                    m_host.OwnerID, m_host.Name, destID,
                    (byte)InstantMessageDialog.TaskInventoryOffered,
                    m_host.OwnerID.Equals(m_host.GroupID),
                    string.Format("'{0}'", category),
                    //string.Format("'{0}'  ( http://slurl.com/secondlife/{1}/{2}/{3}/{4} )", category, World.Name, (int)pos.X, (int)pos.Y, (int)pos.Z),
                    folderID, false, pos,
                    bucket, false);

                m_TransferModule.SendInstantMessage(msg, delegate { });
            }

            ScriptSleep(3000);
        }


        public void llSetCameraEyeOffset(LSL_Vector offset)
        {
            m_host.SetCameraEyeOffset(offset);

            if (m_host.ParentGroup.RootPart.GetCameraEyeOffset().IsZero())
                m_host.ParentGroup.RootPart.SetCameraEyeOffset(offset);
        }

        public void llSetCameraAtOffset(LSL_Vector offset)
        {
            m_host.SetCameraAtOffset(offset);

            if (m_host.ParentGroup.RootPart.GetCameraAtOffset().IsZero())
                m_host.ParentGroup.RootPart.SetCameraAtOffset(offset);
        }

        public void llSetLinkCamera(LSL_Integer link, LSL_Vector eye, LSL_Vector at)
        {
            if (link == ScriptBaseClass.LINK_SET ||
                link == ScriptBaseClass.LINK_ALL_CHILDREN ||
                link == ScriptBaseClass.LINK_ALL_OTHERS) return;

            SceneObjectPart part = null;

            switch (link)
            {
                case ScriptBaseClass.LINK_ROOT:
                    part = m_host.ParentGroup.RootPart;
                    break;
                case ScriptBaseClass.LINK_THIS:
                    part = m_host;
                    break;
                default:
                    part = m_host.ParentGroup.GetLinkNumPart(link);
                    break;
            }

            if (null != part)
            {
                part.SetCameraEyeOffset(eye);
                part.SetCameraAtOffset(at);
            }
        }

        public void llDialog(LSL_Key avatar, LSL_String message, LSL_List buttons, int chat_channel)
        {
            var dm = World.RequestModuleInterface<IDialogModule>();

            if (dm == null)
                return;

            if (!UUID.TryParse(avatar, out var av) || av.IsZero())
            {
                Error("llDialog", "First parameter must be a valid key");
                return;
            }

            if (!m_host.GetOwnerName(out var fname, out var lname))
                return;

            var length = buttons.Length;
            if (length < 1)
            {
                buttons.Add(new LSL_String("Ok"));
                length = 1;
            }
            else if (length > 12)
            {
                Error("llDialog", "No more than 12 buttons can be shown");
                return;
            }

            if (message.Length == 0)
                Error("llDialog", "Empty message");
            else if (Encoding.UTF8.GetByteCount(message) > 512) Error("llDialog", "Message longer than 512 bytes");

            var buts = new string[length];
            for (var i = 0; i < length; i++)
            {
                buts[i] = buttons.Data[i].ToString();
                if (buts[i].Length == 0)
                {
                    Error("llDialog", "Button label cannot be blank");
                    return;
                }

/*
                if (buttons.Data[i].ToString().Length > 24)
                {
                    Error("llDialog", "Button label cannot be longer than 24 characters");
                    return;
                }
*/
                buts[i] = buttons.Data[i].ToString();
            }

            dm.SendDialogToUser(
                av, m_host.Name, m_host.UUID, m_host.OwnerID, fname, lname,
                message, new UUID("00000000-0000-2222-3333-100000001000"), chat_channel, buts);

            ScriptSleep(m_sleepMsOnDialog);
        }

        public LSL_Vector llGetCameraPos()
        {
            if (m_item.PermsGranter.IsZero())
                return Vector3.Zero;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_TRACK_CAMERA) == 0)
            {
                Error("llGetCameraPos", "No permissions to track the camera");
                return Vector3.Zero;
            }

//            ScenePresence presence = World.GetScenePresence(m_host.OwnerID);
            var presence = World.GetScenePresence(m_item.PermsGranter);
            if (presence != null)
            {
                var pos = new LSL_Vector(presence.CameraPosition);
                return pos;
            }

            return Vector3.Zero;
        }

        public LSL_Rotation llGetCameraRot()
        {
            if (m_item.PermsGranter.IsZero())
                return Quaternion.Identity;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_TRACK_CAMERA) == 0)
            {
                Error("llGetCameraRot", "No permissions to track the camera");
                return Quaternion.Identity;
            }

//            ScenePresence presence = World.GetScenePresence(m_host.OwnerID);
            var presence = World.GetScenePresence(m_item.PermsGranter);
            if (presence != null) return new LSL_Rotation(presence.CameraRotation);

            return Quaternion.Identity;
        }


        public void llSetCameraParams(LSL_List rules)
        {
            // the object we are in
            var objectID = m_host.ParentUUID;
            if (objectID.IsZero())
                return;

            // we need the permission first, to know which avatar we want to set the camera for
            var agentID = m_item.PermsGranter;

            if (agentID.IsZero())
                return;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_CONTROL_CAMERA) == 0)
                return;

            var presence = World.GetScenePresence(agentID);

            // we are not interested in child-agents
            if (presence.IsChildAgent) return;

            var parameters = new SortedDictionary<int, float>();
            var data = rules.Data;
            for (var i = 0; i < data.Length; ++i)
            {
                int type;
                try
                {
                    type = Convert.ToInt32(data[i++].ToString());
                }
                catch
                {
                    Error("llSetCameraParams", string.Format("Invalid camera param type {0}", data[i - 1]));
                    return;
                }

                if (i >= data.Length) break; // odd number of entries => ignore the last

                // some special cases: Vector parameters are split into 3 float parameters (with type+1, type+2, type+3)
                switch (type)
                {
                    case ScriptBaseClass.CAMERA_FOCUS:
                    case ScriptBaseClass.CAMERA_FOCUS_OFFSET:
                    case ScriptBaseClass.CAMERA_POSITION:
                        var v = (LSL_Vector)data[i];
                        try
                        {
                            parameters.Add(type + 1, (float)v.x);
                        }
                        catch
                        {
                            switch (type)
                            {
                                case ScriptBaseClass.CAMERA_FOCUS:
                                    Error("llSetCameraParams", "CAMERA_FOCUS: Parameter x is invalid");
                                    return;
                                case ScriptBaseClass.CAMERA_FOCUS_OFFSET:
                                    Error("llSetCameraParams", "CAMERA_FOCUS_OFFSET: Parameter x is invalid");
                                    return;
                                case ScriptBaseClass.CAMERA_POSITION:
                                    Error("llSetCameraParams", "CAMERA_POSITION: Parameter x is invalid");
                                    return;
                            }
                        }

                        try
                        {
                            parameters.Add(type + 2, (float)v.y);
                        }
                        catch
                        {
                            switch (type)
                            {
                                case ScriptBaseClass.CAMERA_FOCUS:
                                    Error("llSetCameraParams", "CAMERA_FOCUS: Parameter y is invalid");
                                    return;
                                case ScriptBaseClass.CAMERA_FOCUS_OFFSET:
                                    Error("llSetCameraParams", "CAMERA_FOCUS_OFFSET: Parameter y is invalid");
                                    return;
                                case ScriptBaseClass.CAMERA_POSITION:
                                    Error("llSetCameraParams", "CAMERA_POSITION: Parameter y is invalid");
                                    return;
                            }
                        }

                        try
                        {
                            parameters.Add(type + 3, (float)v.z);
                        }
                        catch
                        {
                            switch (type)
                            {
                                case ScriptBaseClass.CAMERA_FOCUS:
                                    Error("llSetCameraParams", "CAMERA_FOCUS: Parameter z is invalid");
                                    return;
                                case ScriptBaseClass.CAMERA_FOCUS_OFFSET:
                                    Error("llSetCameraParams", "CAMERA_FOCUS_OFFSET: Parameter z is invalid");
                                    return;
                                case ScriptBaseClass.CAMERA_POSITION:
                                    Error("llSetCameraParams", "CAMERA_POSITION: Parameter z is invalid");
                                    return;
                            }
                        }

                        break;
                    default:
                        // TODO: clean that up as soon as the implicit casts are in
                        if (data[i] is LSL_Float)
                            parameters.Add(type, (float)((LSL_Float)data[i]).value);
                        else if (data[i] is LSL_Integer)
                            parameters.Add(type, ((LSL_Integer)data[i]).value);
                        else
                            try
                            {
                                parameters.Add(type, Convert.ToSingle(data[i]));
                            }
                            catch
                            {
                                Error("llSetCameraParams", string.Format("{0}: Parameter is invalid", type));
                            }

                        break;
                }
            }

            if (parameters.Count > 0) presence.ControllingClient.SendSetFollowCamProperties(objectID, parameters);
        }
    }
}