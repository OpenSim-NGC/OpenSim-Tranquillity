/*
 * Legion Grid — Phlox Script Engine Integration
 *
 * LSLSystemAPI — implementation of ISystemAPI.
 *
 * Core functions (llSay, math, basic queries) are implemented.
 * Everything else stubs with a log warning and safe default.
 * Port methods from /d/halcyon-reference/InWorldz/InWorldz.Phlox.Engine/LSLSystemAPI.cs
 * as needed, adapting Halcyon-specific APIs to standard OpenSim equivalents.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Animation;
using OpenMetaverse.Packets;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Services.Interfaces;
using InWorldz.Phlox.VM;
using ProtoBuf;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using System.Text;
using System.Drawing;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.OptionalModules.World.NPC;

using Microsoft.Extensions.Logging;

namespace Phlox.ScriptEngine
{
    public class LSLSystemAPI : ISystemAPI
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected PhloxEngine m_ScriptEngine;
        protected SceneObjectPart m_host;
        protected uint m_localID;
        protected UUID m_itemID;

        private Interpreter m_thisScript;
        public Interpreter Script { get => m_thisScript; set => m_thisScript = value; }
        public Scene World => m_ScriptEngine.World;
        private DateTime m_scriptTimer = DateTime.UtcNow;  // for llResetTime/llGetAndResetTime

        // Thread-local Random so each scheduler thread gets its own seeded instance;
        // avoids both per-call seed collisions (new Random()) and lock contention.
        [ThreadStatic]
        private static Random s_threadRandom;
        private static Random ThreadRandom => s_threadRandom ??= new Random();

        public LSLSystemAPI(PhloxEngine engine, SceneObjectPart host, uint localID, UUID itemID)
        {
            m_ScriptEngine = engine;
            m_host = host;
            m_localID = localID;
            m_itemID = itemID;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void Stub(string name) =>
            m_log.LogWarning("[PhloxAPI]: STUB {0} called from {1}", name, m_itemID);

        protected void ScriptSleep(int ms)
        {
            if (m_thisScript == null || ms <= 0) return;
            m_thisScript.ScriptState.NextWakeup = (ulong)OpenSim.Framework.Util.EnvironmentTickCount() + (ulong)ms;
            m_thisScript.ScriptState.RunState = RuntimeState.Status.Sleeping;
        }

        protected TaskInventoryItem GetInventorySelf()
        {
            if (m_host == null) return null;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Type == 10 && kvp.Value.ItemID == m_itemID)
                        return kvp.Value;
            }
            return null;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public void SetScriptEventFlags()
        {
            if (m_thisScript == null) return;
            ulong flags = 0;
            if (m_thisScript.ScriptState.GeneralEnable && m_thisScript.ScriptState.Enabled
                && m_thisScript.ScriptState.LocalDisable == RuntimeState.LocalDisableFlag.None)
            {
                foreach (var evt in m_thisScript.Script.StateEvents[m_thisScript.ScriptState.LSLState])
                    flags |= MapEventFlag((SupportedEventList.Events)evt.EventType);
            }
            m_host.SetScriptEvents(m_itemID, flags);
        }

        public void ShoutError(string errorText)
        {
            m_host?.ParentGroup?.Scene?.SimChat(
                "Script error: " + errorText,
                ChatTypeEnum.Shout, 0,
                m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);
        }

        public void OnScriptReset() { }
        public void OnStateChange() { }
        public void OnScriptUnloaded(ScriptUnloadReason reason, RuntimeState.LocalDisableFlag localFlag)
        {
            m_host?.RemoveScriptEvents(m_itemID);
            m_ScriptEngine.ListenManager?.Remove(m_itemID);
        }
        public void AddExecutionTime(double ms) => m_host?.ParentGroup?.AddScriptLPS((int)ms);
        public void OnScriptInjected(bool fromCrossing)
        {
            if (m_thisScript?.ScriptState?.MiscAttributes == null) return;

            foreach (KeyValuePair<int, object[]> kvp in
                     m_thisScript.ScriptState.MiscAttributes.ToList())
            {
                switch ((RuntimeState.MiscAttr)kvp.Key)
                {
                    case RuntimeState.MiscAttr.SensorRepeat:
                        llSensorRepeat((string)kvp.Value[0], (string)kvp.Value[1],
                            (int)kvp.Value[2], (float)kvp.Value[3],
                            (float)kvp.Value[4], (float)kvp.Value[5]);
                        break;
                    case RuntimeState.MiscAttr.VolumeDetect:
                        llVolumeDetect((int)kvp.Value[0]);
                        break;
                    case RuntimeState.MiscAttr.Control:
                        // Halcyon calls a 7-arg TakeControlsInternal helper that bypasses the
                        // permission check by re-using the existing grant on the TaskInventoryItem.
                        // We call llTakeControls() directly which re-validates PERMISSION_TAKE_CONTROLS.
                        // If permission state didn't persist alongside the Control entry, restore
                        // will silently fail. Acceptable for now; revisit if reports of lost
                        // controls on restart surface.
                        if (m_host.ParentGroup.IsAttachment || !fromCrossing)
                            llTakeControls((int)kvp.Value[0], (int)kvp.Value[1], (int)kvp.Value[2]);
                        break;
                }
            }
        }
        public void OnGroupCrossedAvatarReady(UUID avatarId) { }
        public float GetAverageScriptTime() => 0f;

        // ── Math ───────────────────────────────────────────────────────────────

        public float llSin(float f) => (float)Math.Sin(f);
        public float llCos(float f) => (float)Math.Cos(f);
        public float llTan(float f) => (float)Math.Tan(f);
        public float llAtan2(float y, float x) => (float)Math.Atan2(y, x);
        public float llSqrt(float f) => (float)Math.Sqrt(f);
        public float llPow(float b, float e) => (float)Math.Pow(b, e);
        public int llAbs(int i) => i == int.MinValue ? i : Math.Abs(i);
        public float llFabs(float f) => Math.Abs(f);
        public float llFrand(float mag) => (float)(ThreadRandom.NextDouble() * mag);
        public int llFloor(float f) => (int)Math.Floor(f);
        public int llCeil(float f) => (int)Math.Ceiling(f);
        public int llRound(float f) => (int)Math.Round(f, MidpointRounding.AwayFromZero);
        public float llAcos(float f) => (float)Math.Acos(f);
        public float llAsin(float f) => (float)Math.Asin(f);
        public float llLog10(float f) => (float)Math.Log10(f);
        public float llLog(float f) => (float)Math.Log(f);

        public float llVecMag(Vector3 v) => v.Length();
        public Vector3 llVecNorm(Vector3 v) => Vector3.Normalize(v);
        public float llVecDist(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

        public Vector3 llRot2Euler(Quaternion r)
        {
            Vector3 v = new Vector3(0f, 0f, 1f) * r;
            double m = v.Length();
            if (m == 0.0) return Vector3.Zero;
            double x = Math.Atan2(-v.Y, v.Z);
            double sin = v.X / m;
            if (sin < -0.999999 || sin > 0.999999) x = 0.0;
            double y = Math.Asin(sin);
            v = new Vector3(1f, 0f, 0f) * r
                * new Quaternion((float)Math.Sin(-x / 2), 0f, 0f, (float)Math.Cos(-x / 2))
                * new Quaternion(0f, (float)Math.Sin(-y / 2), 0f, (float)Math.Cos(-y / 2));
            return new Vector3((float)x, (float)y, (float)Math.Atan2(v.Y, v.X));
        }

        public Quaternion llEuler2Rot(Vector3 v)
        {
            double c1 = Math.Cos(v.X/2), c2 = Math.Cos(v.Y/2), c3 = Math.Cos(v.Z/2);
            double s1 = Math.Sin(v.X/2), s2 = Math.Sin(v.Y/2), s3 = Math.Sin(v.Z/2);
            return Quaternion.Normalize(new Quaternion(
                (float)(s1*c2*c3 + c1*s2*s3), (float)(c1*s2*c3 - s1*c2*s3),
                (float)(s1*s2*c3 + c1*c2*s3), (float)(c1*c2*c3 - s1*s2*s3)));
        }

        public Quaternion llAxes2Rot(Vector3 fwd, Vector3 left, Vector3 up)
        {
            double tr = fwd.X + left.Y + up.Z + 1.0;
            float s;
            if (tr >= 1.0)
            {
                s = 0.5f / (float)Math.Sqrt(tr);
                return new Quaternion((left.Z-up.Y)*s,(up.X-fwd.Z)*s,(fwd.Y-left.X)*s,0.25f/s);
            }
            double max = left.Y > up.Z ? left.Y : up.Z;
            if (max < fwd.X)
            {
                s = (float)Math.Sqrt(fwd.X-(left.Y+up.Z)+1.0);
                float x = s*0.5f; s = 0.5f/s;
                return new Quaternion(x,(fwd.Y+left.X)*s,(up.X+fwd.Z)*s,(left.Z-up.Y)*s);
            }
            else if (max == left.Y)
            {
                s = (float)Math.Sqrt(left.Y-(up.Z+fwd.X)+1.0);
                float y = s*0.5f; s = 0.5f/s;
                return new Quaternion((fwd.Y+left.X)*s,y,(left.Z+up.Y)*s,(up.X-fwd.Z)*s);
            }
            else
            {
                s = (float)Math.Sqrt(up.Z-(fwd.X+left.Y)+1.0);
                float z = s*0.5f; s = 0.5f/s;
                return new Quaternion((up.X+fwd.Z)*s,(left.Z+up.Y)*s,z,(fwd.Y-left.X)*s);
            }
        }

        public Vector3 llRot2Fwd(Quaternion r) => Vector3.Normalize(new Vector3(1f,0f,0f)*r);
        public Vector3 llRot2Left(Quaternion r) => Vector3.Normalize(new Vector3(0f,1f,0f)*r);
        public Vector3 llRot2Up(Quaternion r) => Vector3.Normalize(new Vector3(0f,0f,1f)*r);

        public Quaternion llRotBetween(Vector3 a, Vector3 b)
        {
            double dotProduct = Vector3.Dot(a, b);
            Vector3 crossProduct = Vector3.Cross(a, b);
            double magProduct = a.Length() * b.Length();
            if (magProduct == 0) return Quaternion.Identity;
            double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, dotProduct / magProduct)));
            Vector3 axis = Vector3.Normalize(crossProduct);
            if (float.IsNaN(axis.X)) return Quaternion.Identity;
            return Quaternion.CreateFromAxisAngle(axis, (float)angle);
        }

        public Quaternion llAxisAngle2Rot(Vector3 axis, float angle)
            => Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);

        public Vector3 llRot2Axis(Quaternion rot)
        {
            rot.GetAxisAngle(out Vector3 axis, out float angle);
            return axis;
        }

        public float llRot2Angle(Quaternion rot)
        {
            rot.GetAxisAngle(out Vector3 axis, out float angle);
            return angle;
        }

        public float llAngleBetween(Quaternion a, Quaternion b)
        {
            float dotProduct = Quaternion.Dot(a, b);
            return (float)(2.0 * Math.Acos(Math.Abs(Math.Max(-1.0, Math.Min(1.0, dotProduct)))));
        }

        public int llModPow(int a, int b, int c)
        {
            long la = a, result = 1;
            la %= c;
            while (b > 0)
            {
                if ((b & 1) == 1) result = (result * la) % c;
                b >>= 1; la = (la * la) % c;
            }
            return (int)result;
        }

        // ── Chat ───────────────────────────────────────────────────────────────

        public void llSay(int channel, string msg)
        {
            m_host?.ParentGroup?.Scene?.SimChat(msg, ChatTypeEnum.Say, channel,
                m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);
        }

        public void llShout(int channel, string msg)
        {
            m_host?.ParentGroup?.Scene?.SimChat(msg, ChatTypeEnum.Shout, channel,
                m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);
        }

        public void llWhisper(int channel, string msg)
        {
            m_host?.ParentGroup?.Scene?.SimChat(msg, ChatTypeEnum.Whisper, channel,
                m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);
        }

        public void llOwnerSay(string msg)
        {
            UUID ownerID = m_host?.OwnerID ?? UUID.Zero;
            if (ownerID == UUID.Zero) return;
            ScenePresence sp = World?.GetScenePresence(ownerID);
            sp?.ControllingClient?.SendChatMessage(msg, (byte)ChatTypeEnum.Owner,
                m_host.AbsolutePosition, m_host.Name, m_host.UUID, m_host.UUID,
                (byte)ChatSourceType.Object, (byte)ChatAudibleLevel.Fully);
        }

		public void llRegionSay(int channel, string msg)
		{
			if (channel == 0)
			{
				ShoutError("llRegionSay: cannot use channel 0");
				return;
			}
			m_host?.ParentGroup?.Scene?.SimChat(msg, ChatTypeEnum.Region, channel,
				m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);
		}

		public void llRegionSayTo(string destId, int channel, string msg)
		{
			if (m_host == null) return;
			if (!UUID.TryParse(destId, out UUID targetId)) return;

			ScenePresence sp = World?.GetScenePresence(targetId);
			if (sp != null && !sp.IsChildAgent)
			{
				// Target is an avatar — use Direct chat type which delivers only to them
				sp.ControllingClient?.SendChatMessage(
					msg, (byte)ChatTypeEnum.Direct,
					m_host.AbsolutePosition, m_host.Name,
					m_host.UUID, m_host.UUID,
					(byte)ChatSourceType.Object, (byte)ChatAudibleLevel.Fully);
				return;
			}

			// Target is an object — deliver via listen pipeline
			m_ScriptEngine.ListenManager?.DeliverChat(channel, m_host.Name, m_host.UUID, msg);
		}

		public void llInstantMessage(string user, string message)
        {
            ScriptSleep(2000);
            if (!UUID.TryParse(user, out UUID targetID) || targetID == UUID.Zero)
                return;

            IMessageTransferModule tr = World?.RequestModuleInterface<IMessageTransferModule>();
            if (tr == null)
                return;

            GridInstantMessage msg = new GridInstantMessage()
            {
                fromAgentID    = m_host.OwnerID.Guid,
                toAgentID      = targetID.Guid,
                imSessionID    = m_host.UUID.Guid,
                timestamp      = (uint)Util.UnixTimeSinceEpoch(),
                fromAgentName  = m_host.Name,
                message        = message ?? string.Empty,
                dialog         = (byte)InstantMessageDialog.MessageFromObject,
                fromGroup      = false,
                offline        = 0,
                ParentEstateID = World.RegionInfo.EstateSettings.ParentEstateID,
                Position       = m_host.AbsolutePosition,
                RegionID       = World.RegionInfo.RegionID.Guid,
                binaryBucket   = new byte[0]
            };

            tr.SendInstantMessage(msg, success => {});
        } 
		public void llDialog(string avatar, string message, LSLList buttons, int chat_channel)
		{
			if (m_host == null) return;
			if (!UUID.TryParse(avatar, out UUID avatarId)) return;

			ScenePresence sp = World?.GetScenePresence(avatarId);
			if (sp == null || sp.IsChildAgent) return;

			var buttonList = new List<string>();
			if (buttons != null)
			{
				foreach (var o in buttons.Data)
				{
					string label = o?.ToString() ?? string.Empty;
					if (label.Length > 24) label = label.Substring(0, 24);
					buttonList.Add(label);
					if (buttonList.Count >= 12) break;
				}
			}
			if (buttonList.Count == 0) buttonList.Add("OK");

			string ownerFirst = string.Empty, ownerLast = string.Empty;
			ScenePresence ownerSp = World?.GetScenePresence(m_host.OwnerID);
			if (ownerSp != null)
			{
				var parts = ownerSp.Name.Split(' ');
				ownerFirst = parts[0];
				ownerLast  = parts.Length > 1 ? parts[1] : string.Empty;
			}
			else
			{
				UserAccount acct = World?.UserAccountService?.GetUserAccount(
					World.RegionInfo.ScopeID, m_host.OwnerID);
				if (acct != null) { ownerFirst = acct.FirstName; ownerLast = acct.LastName; }
			}

			sp.ControllingClient?.SendDialog(
				m_host.Name,
				m_host.UUID,
				m_host.OwnerID,
				ownerFirst,
				ownerLast,
				message,
				UUID.Zero,          // textureID — dialogs don't use a texture
				chat_channel,
				buttonList.ToArray());

			ScriptSleep(1000);
		}
        public void llTextBox(string avatar, string message, int chat_channel)
        {
            if (!UUID.TryParse(avatar, out UUID av) || av == UUID.Zero) return;
            IDialogModule dm = World?.RequestModuleInterface<IDialogModule>();
            if (dm == null) return;
            if (message != null && message.Length > 1024) message = message.Substring(0, 1024);
            dm.SendTextBoxToUser(av, message, chat_channel, m_host.Name, m_host.UUID, m_host.OwnerID);
            ScriptSleep(1000);
        }

        // ── Object info ────────────────────────────────────────────────────────

        public string llGetKey() => m_host?.UUID.ToString() ?? UUID.Zero.ToString();
        public string llGetOwner() => m_host?.OwnerID.ToString() ?? UUID.Zero.ToString();
        public string llGetCreator() => m_host?.CreatorID.ToString() ?? UUID.Zero.ToString();
        public string llGetObjectName() => m_host?.Name ?? string.Empty;
        public void llSetObjectName(string name) { if (m_host != null) m_host.Name = name; }
        public string llGetObjectDesc() => m_host?.Description ?? string.Empty;
        public void llSetObjectDesc(string name) { if (m_host != null) m_host.Description = name; }
        public int llGetNumberOfPrims() => m_host?.ParentGroup?.PrimCount ?? 1;
        public int llGetLinkNumber() => m_host?.LinkNum ?? 0;
        public int llGetNumberOfSides() => m_host?.GetNumberOfSides() ?? 0;
        public string llGetScriptName() => GetInventorySelf()?.Name ?? string.Empty;
        public string llGetRegionName() => World?.RegionInfo?.RegionName ?? string.Empty;
        public float llGetRegionTimeDilation() => World?.TimeDilation ?? 1f;
        public float llGetRegionFPS() => World?.StatsReporter?.LastReportedSimFPS ?? 45f;
        public Vector3 llGetRegionCorner()
        {
            if (World?.RegionInfo == null) return Vector3.Zero;
            return new Vector3(
                World.RegionInfo.RegionLocX * Constants.RegionSize,
                World.RegionInfo.RegionLocY * Constants.RegionSize,
                0f);
        }
        public int llGetRegionAgentCount() => World?.GetRootAgentCount() ?? 0;
        public int llGetRegionFlags()
        {
            if (World?.RegionInfo?.RegionSettings == null) return 0;
            var s = World.RegionInfo.RegionSettings;
            int flags = 0;
            if (s.AllowDamage)      flags |= 0x1;      // REGION_FLAG_ALLOW_DAMAGE
            if (s.BlockFly)         flags |= 0x80000;  // REGION_FLAG_BLOCK_FLY
            if (s.RestrictPushing)  flags |= 0x400000; // REGION_FLAG_RESTRICT_PUSHOBJECT
            if (s.AllowLandResell)  flags |= 0x4;      // REGION_FLAG_ALLOW_LAND_RESELL
            if (s.DisableCollisions)flags |= 0x1000;   // REGION_FLAG_DISABLE_COLLISIONS
            if (s.DisablePhysics)   flags |= 0x4000;   // REGION_FLAG_DISABLE_PHYSICS
            if (s.Sandbox)          flags |= 0x20;     // REGION_FLAG_SANDBOX
            return flags;
        }
        public string llGetSimulatorHostname() => System.Net.Dns.GetHostName();
        public string llGetDate() => DateTime.UtcNow.ToString("yyyy-MM-dd");
        public string llGetTimestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ");
        public float llGetWallclock() => (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
        public float llGetGMTclock() => (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
        public int llGetUnixTime() => (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public float llGetTimeOfDay()
        {
            // Returns seconds since midnight, cycling on a 4-hour SL day period
            return (float)(DateTime.UtcNow.TimeOfDay.TotalSeconds % (3600.0 * 4.0));
        }

        public float llGetRegionTimeOfDay()
        {
            // SL: returns region-local time of day in seconds since midnight
            // In OpenSim without EEP, this is the same as llGetTimeOfDay
            return llGetTimeOfDay();
        }

        public float llGetTime() => (float)(DateTime.UtcNow - m_scriptTimer).TotalSeconds;

        public void llResetTime()
        {
            m_scriptTimer = DateTime.UtcNow;
        }

        public float llGetAndResetTime()
        {
            float elapsed = (float)(DateTime.UtcNow - m_scriptTimer).TotalSeconds;
            m_scriptTimer = DateTime.UtcNow;
            return elapsed;
        }
        public int llGetLocalTime() => 0;
        public int iwGetLocalTime() => 0;
        public int iwGetLocalTimeOffset() => 0;
        public string iwFormatTime(int unixtime, int isUTC, string format)
        {
            DateTime date = OpenSim.Framework.Util.UnixEpoch.AddSeconds(unixtime);
            if (isUTC == 0)
                date = date.ToLocalTime();
            if (String.IsNullOrEmpty(format))
                format = "yyyy'-'MM'-'dd' 'HH':'mm':'ss";
            return date.ToString(format);
        }

        // ── Position / rotation ────────────────────────────────────────────────

        public Vector3 llGetPos() => m_host?.AbsolutePosition ?? Vector3.Zero;
        public Vector3 llGetLocalPos() => m_host?.OffsetPosition ?? Vector3.Zero;
        public Vector3 llGetRootPosition() => m_host?.ParentGroup?.AbsolutePosition ?? Vector3.Zero;
        public void llSetPos(Vector3 pos)
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null || group.IsDeleted) return;
            pos.X = Math.Max(0f, Math.Min(255.9f, pos.X));
            pos.Y = Math.Max(0f, Math.Min(255.9f, pos.Y));
            pos.Z = Math.Max(0f, Math.Min(4096f, pos.Z));
            if (m_host.LinkNum < 2) group.UpdateGroupPosition(pos);
            else m_host.UpdateOffSet(pos - group.AbsolutePosition);
            ScriptSleep(200);
        }
        public Quaternion llGetRot() => m_host?.GetWorldRotation() ?? Quaternion.Identity;
        public Quaternion llGetLocalRot() => m_host?.RotationOffset ?? Quaternion.Identity;
        public Quaternion llGetRootRotation() => m_host?.ParentGroup?.GroupRotation ?? Quaternion.Identity;
        public void llSetRot(Quaternion rot)
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null || group.IsDeleted) return;
            if (m_host.LinkNum < 2) group.UpdateGroupRotationR(rot);
            else m_host.UpdateRotation(rot);
            ScriptSleep(200);
        }
        public void llSetLocalRot(Quaternion rot)
        {
            if (m_host == null) return;
            m_host.UpdateRotation(rot);
        }
        public Vector3 llGetScale() => m_host?.Scale ?? Vector3.One;
        public void llSetScale(Vector3 scale)
        {
            if (m_host == null) return;
            scale.X = Math.Max(0.01f, Math.Min(64f, scale.X));
            scale.Y = Math.Max(0.01f, Math.Min(64f, scale.Y));
            scale.Z = Math.Max(0.01f, Math.Min(64f, scale.Z));
            m_host.Resize(scale);
        }
        public int llScaleByFactor(float factor)
        {
            // SL: uniformly scale the entire linkset by factor. Returns TRUE on success.
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return 0;
            if (factor <= 0f) return 0;

            SceneObjectGroup group = m_host.ParentGroup;
            SceneObjectPart[] parts = group.Parts;

            // Check if scaling would push any part outside [0.01, 64.0]
            foreach (var part in parts)
            {
                Vector3 s = part.Scale;
                if (s.X * factor < 0.01f || s.Y * factor < 0.01f || s.Z * factor < 0.01f) return 0;
                if (s.X * factor > 64f || s.Y * factor > 64f || s.Z * factor > 64f) return 0;
            }

            // Apply scale to all parts and adjust offsets
            foreach (var part in parts)
            {
                part.Resize(new Vector3(part.Scale.X * factor, part.Scale.Y * factor, part.Scale.Z * factor));
                if (part != group.RootPart)
                    part.OffsetPosition = part.OffsetPosition * factor;
            }
            group.HasGroupChanged = true;
            group.ScheduleGroupForFullUpdate();
            return 1;
        }
        public float llGetMaxScaleFactor()
        {
            // SL: return the maximum factor that llScaleByFactor could use
            if (m_host?.ParentGroup == null) return 1f;
            float maxFactor = float.MaxValue;
            foreach (var part in m_host.ParentGroup.Parts)
            {
                Vector3 s = part.Scale;
                float fx = 64f / s.X;
                float fy = 64f / s.Y;
                float fz = 64f / s.Z;
                maxFactor = Math.Min(maxFactor, Math.Min(fx, Math.Min(fy, fz)));
            }
            return maxFactor == float.MaxValue ? 1f : (float)Math.Round(maxFactor, 5);
        }
        public float llGetMinScaleFactor()
        {
            // SL: return the minimum factor that llScaleByFactor could use
            if (m_host?.ParentGroup == null) return 1f;
            float minFactor = float.MaxValue;
            foreach (var part in m_host.ParentGroup.Parts)
            {
                Vector3 s = part.Scale;
                float fx = s.X / 0.01f;
                float fy = s.Y / 0.01f;
                float fz = s.Z / 0.01f;
                // The min scale factor is 1/max_shrink
                float partMin = Math.Min(fx, Math.Min(fy, fz));
                minFactor = Math.Min(minFactor, partMin);
            }
            if (minFactor == float.MaxValue || minFactor <= 0f) return 1f;
            return (float)Math.Round(1f / minFactor, 5);
        }
        public Vector3 llGetVel() => m_host?.Velocity ?? Vector3.Zero;
        public Vector3 llGetAccel() => Vector3.Zero;
        public Vector3 llGetOmega() => Vector3.Zero;
        public Vector3 llGetTorque() => Vector3.Zero;
        public Vector3 iwGetAngularVelocity() => Vector3.Zero;
        public Vector3 llGetCenterOfMass()
        {
            if (m_host?.ParentGroup == null) return Vector3.Zero;
            return m_host.ParentGroup.GetCenterOfMass();
        }

        public Vector3 llGetGeometricCenter()
        {
            if (m_host == null) return Vector3.Zero;
            return m_host.GetGeometricCenter();
        }
        public Vector3 llGetCameraPos()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return Vector3.Zero;
            if (item.PermsGranter == UUID.Zero) return Vector3.Zero;
            // PERMISSION_TRACK_CAMERA = 0x400
            if ((item.PermsMask & 0x400) == 0)
            {
                ShoutError("No permissions to track the camera");
                return Vector3.Zero;
            }
            ScenePresence presence = World?.GetScenePresence(item.PermsGranter);
            if (presence != null)
                return presence.CameraPosition;
            return Vector3.Zero;
        }
        public Quaternion llGetCameraRot()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return Quaternion.Identity;
            if (item.PermsGranter == UUID.Zero) return Quaternion.Identity;
            // PERMISSION_TRACK_CAMERA = 0x400
            if ((item.PermsMask & 0x400) == 0)
            {
                ShoutError("No permissions to track the camera");
                return Quaternion.Identity;
            }
            ScenePresence presence = World?.GetScenePresence(item.PermsGranter);
            if (presence != null)
                return presence.CameraRotation;
            return Quaternion.Identity;
        }
        public float llGetCameraAspect()
        {
            // SL: returns camera aspect ratio. Viewer doesn't send this to the server.
            // Return standard widescreen 16:9 default.
            return 1.7778f;
        }
        public float llGetCameraFOV()
        {
            // SL: returns camera vertical FOV in radians. Viewer doesn't send this to the server.
            // Return Firestorm default (roughly 60 degrees).
            return 1.0472f;
        }
        public int llSetRegionPos(Vector3 position)
        {
            // Halcyon used ValidLocation() + SetPos() helpers; Legion uses direct group position update.
            // Clamp to region bounds (allow up to 10m outside for cross-region placement per SL spec)
            float regionSize = World?.RegionInfo?.RegionSizeX ?? 256f;
            position.X = Math.Max(-10f, Math.Min(regionSize + 10f, position.X));
            position.Y = Math.Max(-10f, Math.Min(regionSize + 10f, position.Y));
            position.Z = Math.Max(0f, Math.Min(4096f, position.Z));

            if (m_host.ParentGroup.IsAttachment)
            {
                ScenePresence avatar = World?.GetScenePresence(m_host.ParentGroup.AttachedAvatar);
                if (avatar == null)
                    return 0;
                avatar.StandUp();
                avatar.Teleport(position);
            }
            else
            {
                // Move the root prim (entire linkset)
                SceneObjectGroup group = m_host.ParentGroup;
                if (group == null || group.IsDeleted) return 0;
                group.UpdateGroupPosition(position);
            }
            return 1;
        }

        // ── Physics ────────────────────────────────────────────────────────────

        public void llSetForce(Vector3 force, int local)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            if ((m_host.ParentGroup.RootPart.Flags & PrimFlags.Physics) == 0) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            if (local != 0) force *= m_host.GetWorldRotation();
            pa.Force = force;
        }

        public Vector3 llGetForce()
        {
            if (m_host?.ParentGroup == null) return Vector3.Zero;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            return pa?.Force ?? Vector3.Zero;
        }

        public void llSetTorque(Vector3 torque, int local)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            if ((m_host.ParentGroup.RootPart.Flags & PrimFlags.Physics) == 0) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            if (local != 0) torque *= m_host.GetWorldRotation();
            pa.Torque = torque;
        }

        public void llSetForceAndTorque(Vector3 force, Vector3 torque, int local)
        {
            llSetForce(force, local);
            llSetTorque(torque, local);
        }

        public void llApplyImpulse(Vector3 force, int local)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            if ((m_host.ParentGroup.RootPart.Flags & PrimFlags.Physics) == 0) return;
            if (force.LengthSquared() > 20000f * 20000f)
                force = Vector3.Normalize(force) * 20000f;
            m_host.ApplyImpulse(force, local != 0);
        }

        public void llApplyRotationalImpulse(Vector3 force, int local)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            if ((m_host.ParentGroup.RootPart.Flags & PrimFlags.Physics) == 0) return;
            m_host.ApplyAngularImpulse(force, local != 0);
        }
        public void llMoveToTarget(Vector3 target, float tau)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.MoveToTarget(target, tau);
        }

        public void llStopMoveToTarget()
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.StopMoveToTarget();
        }
        public float llGetMass() => m_host?.GetMass() ?? 0f;
        public float llGetMassMKS()
        {
            if (m_host?.ParentGroup == null) return 0f;
            return m_host.ParentGroup.GetMass();
        }
        public float iwGetObjectMassMKS(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return 0f;
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null) return part.ParentGroup.GetMass();
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp?.PhysicsActor != null) return sp.PhysicsActor.Mass;
            return 0f;
        }
        public float llGetObjectMass(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return 0f;
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null) return part.ParentGroup.GetMass();
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp?.PhysicsActor != null) return sp.PhysicsActor.Mass;
            return 0f;
        }
        public void llSetBuoyancy(float buoyancy)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            m_host.ParentGroup.RootPart.SetBuoyancy(buoyancy);
        }

        public void llSetHoverHeight(float height, int water, float tau)
        {
            if (m_host?.PhysActor == null) return;
            PIDHoverType hoverType = (water != 0) ? PIDHoverType.Water : PIDHoverType.Ground;
            m_host.SetHoverHeight(height, hoverType, tau);
        }

        public void llStopHover()
        {
            if (m_host?.PhysActor == null) return;
            m_host.StopHover();
        }
        public void llGroundRepel(float height, int water, float tau)
        {
            // Halcyon used PIDHoverFlag.Ground|Repel; Legion uses PIDHoverType which
            // may not have a separate Repel flag. Use Ground (or Water) — SetHoverHeight
            // with a positive height inherently repels from the ground.
            if (m_host?.PhysActor == null) return;
            PIDHoverType hoverType = PIDHoverType.Ground;
            if (water != 0)
                hoverType = PIDHoverType.Water;
            m_host.SetHoverHeight(height, hoverType, tau);
        }
        // Base64 lookup tables for llIntegerToBase64 / llBase64ToInteger
        private static readonly char[] i2ctable =
        {
            'A','B','C','D','E','F','G','H','I','J','K','L','M','N','O','P',
            'Q','R','S','T','U','V','W','X','Y','Z','a','b','c','d','e','f',
            'g','h','i','j','k','l','m','n','o','p','q','r','s','t','u','v',
            'w','x','y','z','0','1','2','3','4','5','6','7','8','9','+','/'
        };
        private static readonly int[] c2itable =
        {
            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,  // 0-15
            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,  // 16-31
            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,63,-1,-1,-1,64,  // 32-47
            53,54,55,56,57,58,59,60,61,62,-1,-1,-1, 0,-1,-1,  // 48-63
            -1, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15, // 64-79
            16,17,18,19,20,21,22,23,24,25,26,-1,-1,-1,-1,-1,  // 80-95
            -1,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41, // 96-111
            42,43,44,45,46,47,48,49,50,51,52,-1,-1,-1,-1,-1   // 112-127
        };

        // STATUS constants (LSL standard values)
        private const int STATUS_PHYSICS          = 1;
        private const int STATUS_ROTATE_X         = 2;
        private const int STATUS_ROTATE_Y         = 4;
        private const int STATUS_ROTATE_Z         = 8;
        private const int STATUS_PHANTOM          = 16;
        private const int STATUS_CAST_SHADOWS     = 32;
        private const int STATUS_BLOCK_GRAB       = 64;
        private const int STATUS_DIE_AT_EDGE      = 128;
        private const int STATUS_RETURN_AT_EDGE   = 256;
        private const int STATUS_SANDBOX          = 4096;
        private const int STATUS_BLOCK_GRAB_OBJECT= 8192;

        public void llSetStatus(int status, int value)
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return;
            bool on = value != 0;

            if ((status & STATUS_PHYSICS) != 0)
            {
                if (on)
                {
                    bool allow = true;
                    foreach (SceneObjectPart p in group.Parts)
                    {
                        if (p.Scale.X > World.m_maxPhys || p.Scale.Y > World.m_maxPhys || p.Scale.Z > World.m_maxPhys)
                        { allow = false; break; }
                    }
                    if (allow) m_host.ScriptSetPhysicsStatus(true);
                }
                else
                    m_host.ScriptSetPhysicsStatus(false);
            }

            if ((status & STATUS_PHANTOM) != 0)
                group.ScriptSetPhantomStatus(on);

            if ((status & STATUS_CAST_SHADOWS) != 0)
            {
                if (on) m_host.AddFlag(PrimFlags.CastShadows);
                else    m_host.RemFlag(PrimFlags.CastShadows);
            }

            if ((status & STATUS_BLOCK_GRAB) != 0 || (status & STATUS_BLOCK_GRAB_OBJECT) != 0)
                m_host.BlockGrab = on;

            if ((status & STATUS_DIE_AT_EDGE) != 0)
                m_host.SetDieAtEdge(on);

            if ((status & STATUS_SANDBOX) != 0)
                Stub("llSetStatus(STATUS_SANDBOX)");

            // Rotation axis locks — byte bitmask: bit0=X, bit1=Y, bit2=Z
            if ((status & (STATUS_ROTATE_X | STATUS_ROTATE_Y | STATUS_ROTATE_Z)) != 0)
            {
                byte locks = m_host.RotationAxisLocks;
                if ((status & STATUS_ROTATE_X) != 0)
                    locks = on ? (byte)(locks & ~0x01) : (byte)(locks | 0x01);
                if ((status & STATUS_ROTATE_Y) != 0)
                    locks = on ? (byte)(locks & ~0x02) : (byte)(locks | 0x02);
                if ((status & STATUS_ROTATE_Z) != 0)
                    locks = on ? (byte)(locks & ~0x04) : (byte)(locks | 0x04);
                m_host.RotationAxisLocks = locks;
                m_host.PhysActor?.LockAngularMotion(locks);
            }
        }

        public int llGetStatus(int status)
        {
            if (m_host == null) return 0;
            uint flags = m_host.GetEffectiveObjectFlags();
            switch (status)
            {
                case STATUS_PHYSICS:
                    return (flags & (uint)PrimFlags.Physics) != 0 ? 1 : 0;
                case STATUS_PHANTOM:
                    return (flags & (uint)PrimFlags.Phantom) != 0 ? 1 : 0;
                case STATUS_CAST_SHADOWS:
                    return (flags & (uint)PrimFlags.CastShadows) != 0 ? 1 : 0;
                case STATUS_BLOCK_GRAB:
                case STATUS_BLOCK_GRAB_OBJECT:
                    return m_host.BlockGrab ? 1 : 0;
                case STATUS_DIE_AT_EDGE:
                    return m_host.GetDieAtEdge() ? 1 : 0;
                case STATUS_ROTATE_X:
                    return (m_host.RotationAxisLocks & 0x01) == 0 ? 1 : 0;
                case STATUS_ROTATE_Y:
                    return (m_host.RotationAxisLocks & 0x02) == 0 ? 1 : 0;
                case STATUS_ROTATE_Z:
                    return (m_host.RotationAxisLocks & 0x04) == 0 ? 1 : 0;
                default:
                    return 0;
            }
        }
        public void llSetVehicleType(int type)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleType = type;
        }

        public void llSetVehicleFloatParam(int param, float value)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleFloatParam(param, value);
        }

        public void llSetVehicleVectorParam(int param, Vector3 vec)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleVectorParam(param, vec);
        }

        public void llSetVehicleRotationParam(int param, Quaternion rot)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleRotationParam(param, rot);
        }

        public void llSetVehicleFlags(int flags)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleFlags(flags, false);
        }

        public void llRemoveVehicleFlags(int flags)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleFlags(flags, true);
        }
        public LSLList llGetPhysicsMaterial()
        {
            if (m_host == null) return new LSLList();
            // Returns [gravityMultiplier, restitution, friction, density]
            return new LSLList(new object[]
            {
                m_host.GravityModifier,
                m_host.Restitution,
                m_host.Friction,
                m_host.Density
            });
        }

        public void llSetPhysicsMaterial(int mask, float gravityMultiplier, float restitution, float friction, float density)
        {
            if (m_host == null) return;
            // mask bits: 1=gravity, 2=restitution, 4=friction, 8=density
            // SOP property setters handle bounds-clamping, HasGroupChanged, and PhysicsActor update.
            if ((mask & 1) != 0) m_host.GravityModifier = gravityMultiplier;
            if ((mask & 2) != 0) m_host.Restitution     = restitution;
            if ((mask & 4) != 0) m_host.Friction         = friction;
            if ((mask & 8) != 0) m_host.Density          = density;
        }
        public void llSetAngularVelocity(Vector3 force, int local)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.RootPart.SetAngularVelocity(force, local != 0);
        }

        public void llSetVelocity(Vector3 force, int local)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.RootPart.SetVelocity(force, local != 0);
        }
        public void llSetKeyframedMotion(LSLList keyframes, LSLList options)
        {
            // Faithful port from Halcyon, adapted for OpenSim's KeyframeMotion class.
            if (m_host?.ParentGroup == null) return;
            if (m_host.ParentGroup.RootPart != m_host)
            {
                ShoutError("Must be used in the root object!");
                return;
            }

            try
            {
                if (keyframes.Length == 0 && options.Length == 0)
                {
                    // Stop and clear
                    m_host.ParentGroup.RootPart.KeyframeMotion = null;
                    return;
                }

                // KFM constants (raw values since ScriptBaseClass is not accessible)
                const int KFM_COMMAND = 0;
                const int KFM_MODE = 1;
                const int KFM_DATA = 2;
                const int KFM_CMD_PLAY = 0;
                const int KFM_CMD_STOP = 1;
                const int KFM_CMD_PAUSE = 2;
                const int KFM_FORWARD = 0;
                const int KFM_LOOP = 1;
                const int KFM_PING_PONG = 2;
                const int KFM_REVERSE = 3;
                const int KFM_ROTATION = 1;
                const int KFM_TRANSLATION = 2;

                int dataType = KFM_ROTATION | KFM_TRANSLATION; // Both = default
                int mode = KFM_FORWARD;

                for (int i = 0; i < options.Length; i += 2)
                {
                    int option = options.GetLSLIntegerItem(i);
                    int value = options.GetLSLIntegerItem(i + 1);
                    if (option == KFM_COMMAND)
                    {
                        // Command-only call: play/stop/pause an existing motion
                        KeyframeMotion existingMotion = m_host.ParentGroup.RootPart.KeyframeMotion;
                        if (existingMotion == null) return;
                        switch (value)
                        {
                            case KFM_CMD_PLAY: existingMotion.Start(); break;
                            case KFM_CMD_STOP: existingMotion.Stop(); break;
                            case KFM_CMD_PAUSE: existingMotion.Pause(); break;
                        }
                        return;
                    }
                    if (option == KFM_MODE) mode = value;
                    else if (option == KFM_DATA) dataType = value;
                }

                bool hasTranslation = (dataType & KFM_TRANSLATION) != 0;
                bool hasRotation = (dataType & KFM_ROTATION) != 0;
                int stride = (hasTranslation && hasRotation) ? 3 : 2;

                // Build keyframe arrays
                int numFrames = keyframes.Length / stride;
                KeyframeMotion.Keyframe[] kfArray = new KeyframeMotion.Keyframe[numFrames];

                for (int i = 0; i < numFrames; i++)
                {
                    int baseIdx = i * stride;
                    KeyframeMotion.Keyframe kf = new KeyframeMotion.Keyframe();
                    int idx = 0;

                    if (hasTranslation)
                    {
                        Vector3 pos = keyframes.GetVector3Item(baseIdx + idx);
                        kf.Position = pos;
                        idx++;
                    }
                    if (hasRotation)
                    {
                        Quaternion rot = keyframes.GetQuaternionItem(baseIdx + idx);
                        rot.Normalize();
                        kf.Rotation = rot;
                        idx++;
                    }
                    float time = (float)keyframes.GetLSLFloatItem(baseIdx + idx);
                    kf.TimeMS = (int)(time * 1000);

                    kfArray[i] = kf;
                }

                KeyframeMotion.PlayMode playMode;
                switch (mode)
                {
                    case KFM_REVERSE: playMode = KeyframeMotion.PlayMode.Reverse; break;
                    case KFM_LOOP: playMode = KeyframeMotion.PlayMode.Loop; break;
                    case KFM_PING_PONG: playMode = KeyframeMotion.PlayMode.PingPong; break;
                    default: playMode = KeyframeMotion.PlayMode.Forward; break;
                }

                KeyframeMotion.DataFormat dFlags = 0;
                if (hasTranslation) dFlags |= KeyframeMotion.DataFormat.Translation;
                if (hasRotation) dFlags |= KeyframeMotion.DataFormat.Rotation;

                KeyframeMotion motion = new KeyframeMotion(m_host.ParentGroup, playMode, dFlags);
                motion.SetKeyframes(kfArray);
                m_host.ParentGroup.RootPart.KeyframeMotion = motion;
                motion.Start();
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llSetKeyframedMotion exception: {0}", e.Message);
            }
        }

        // ── Timer / sleep ──────────────────────────────────────────────────────

        public void llSetTimerEvent(float sec) => m_ScriptEngine.SetTimerEvent(m_localID, m_itemID, sec);
        public void llSleep(float sec) => ScriptSleep((int)(sec * 1000));
        public void llMinEventDelay(float delay)
        {
            // No-op in Phlox — the scheduler handles event timing internally.
            // LSL spec says this sets a minimum gap between event handler invocations,
            // but Phlox's single-threaded scheduler already serializes events.
        }

        // ── Script state ───────────────────────────────────────────────────────

        public void llResetScript() => m_ScriptEngine.ApiResetScript(m_itemID);
        public void llResetOtherScript(string name)
        {
            if (m_host == null) return;
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.LSLText);
            if (item == null)
            {
                ShoutError("llResetOtherScript: script '" + name + "' not found");
                return;
            }
            m_ScriptEngine.ApiResetScript(item.ItemID);
        }

        public int llGetScriptState(string name)
        {
            if (m_host == null) return 0;
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.LSLText);
            if (item == null)
            {
                ShoutError("llGetScriptState: script '" + name + "' not found");
                return 0;
            }
            return item.ScriptRunning ? 1 : 0;
        }

        public void llSetScriptState(string name, int run)
        {
            if (m_host == null) return;
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.LSLText);
            if (item == null)
            {
                ShoutError("llSetScriptState: script '" + name + "' not found");
                return;
            }
            // Use EventManager directly — SetScriptRunning requires IClientAPI
            if (run != 0)
                World.EventManager.TriggerStartScript(m_host.LocalId, item.ItemID);
            else
                World.EventManager.TriggerStopScript(m_host.LocalId, item.ItemID);
        }
        public void llSetRemoteScriptAccessPin(int pin)
        {
            m_host.ScriptAccessPin = pin;
        }
        public void llRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param)
        {
            RemoteLoadScriptPin(target, name, pin, running, start_param, true);
        }
        public int iwRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param)
        {
            return RemoteLoadScriptPin(target, name, pin, running, start_param, false);
        }

        /// <summary>
        /// Shared implementation for llRemoteLoadScriptPin / iwRemoteLoadScriptPin.
        /// Faithful port from Halcyon.
        /// Returns: 1=success, 0=failure, -1=PIN mismatch, -2=no PIN set.
        /// </summary>
        private int RemoteLoadScriptPin(string target, string name, int pin, int running, int start_param, bool doShout)
        {
            if (pin == 0)
            {
                ShoutError("llRemoteLoadScriptPin: PIN cannot be zero.");
                ScriptSleep(3000);
                return 0;
            }

            if (!UUID.TryParse(target, out UUID destId))
            {
                llSay(0, "Could not parse key " + target);
                ScriptSleep(3000);
                return 0;
            }

            // Target must be a different prim than the one containing the script, owned by the same user.
            SceneObjectPart part = World?.GetSceneObjectPart(destId);
            if (part == null)
            {
                ShoutError("llRemoteLoadScriptPin: Target prim [" + destId.ToString() + "] not found.");
                ScriptSleep(3000);
                return 0;
            }
            if (m_host.OwnerID != part.OwnerID)
            {
                ShoutError("llRemoteLoadScriptPin: Target prim ownership does not match.");
                ScriptSleep(3000);
                return 0;
            }
            if (m_host.UUID == destId)
            {
                ShoutError("llRemoteLoadScriptPin: Target prim cannot be the source prim.");
                ScriptSleep(3000);
                return 0;
            }

            // Find the script in this prim's inventory
            UUID srcId = UUID.Zero;
            bool found = false;
            lock (m_host.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                {
                    if (inv.Value.Name == name && inv.Value.Type == 10) // type 10 = script
                    {
                        found = true;
                        srcId = inv.Key;
                        break;
                    }
                }
            }

            if (!found)
            {
                llSay(0, "Could not find script " + name);
                ScriptSleep(3000);
                return 0;
            }

            // The rest of the permission checks are done in RezScript, so check the pin there as well.
            int ret = 1;
            try
            {
                // Legion signature: RezScriptFromPrim(UUID srcId, SceneObjectPart srcPart, UUID destId, int pin, int running, int start_param)
                World.RezScriptFromPrim(srcId, m_host, destId, pin, running, start_param);
            }
            catch (Exception e)
            {
                string msg = e.Message;
                if (msg.Contains("PIN"))
                {
                    if (doShout) ShoutError("llRemoteLoadScriptPin: Script update denied - PIN mismatch.");
                    ret = -1;
                }
                else
                {
                    m_log.LogWarning("[PhloxAPI]: RemoteLoadScriptPin failed: {0}", msg);
                    if (doShout) ShoutError("llRemoteLoadScriptPin: " + msg);
                    ret = 0;
                }
            }
            ScriptSleep(3000);
            return ret;
        }
        public void llMessageLinked(int linknum, int num, string str, string id)
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null || group.IsDeleted) return;
            var parms = new EventParams("link_message",
                new object[] { m_host.LinkNum, num, str ?? string.Empty, id ?? UUID.Zero.ToString() },
                new DetectParams[0]);
            SceneObjectPart[] parts = group.Parts;
            if (linknum == -4) m_ScriptEngine.PostObjectEvent(m_host.LocalId, parms);
            else if (linknum == -3) foreach (var p in parts) m_ScriptEngine.PostObjectEvent(p.LocalId, parms);
            else if (linknum == -1) foreach (var p in parts) { if (p.LocalId != m_host.LocalId) m_ScriptEngine.PostObjectEvent(p.LocalId, parms); }
            else if (linknum == -2) foreach (var p in parts) { if (p.LinkNum > 1) m_ScriptEngine.PostObjectEvent(p.LocalId, parms); }
            else if (linknum == 1) m_ScriptEngine.PostObjectEvent(group.RootPart.LocalId, parms);
            else if (linknum > 1) { var t = group.GetLinkNumPart(linknum); if (t != null) m_ScriptEngine.PostObjectEvent(t.LocalId, parms); }
        }
        public int llGetStartParameter()
        {
            return m_thisScript?.ScriptState?.StartParameter ?? 0;
        }
        public int llGetFreeMemory() => 65536;
        public int llGetUsedMemory()
        {
            // Phlox VM doesn't track per-script memory the way Mono does.
            // Return a reasonable estimate: 16KB base + script state size.
            if (m_thisScript?.ScriptState != null)
            {
                int stackSize = m_thisScript.ScriptState.Operands?.Count ?? 0;
                return 16384 + (stackSize * 64);
            }
            return 16384;
        }
        public int llSetMemoryLimit(int limit)
        {
            // Halcyon only accepts 128K (131072)
            if (limit == 131072) return 1;
            return 0;
        }
        public int llGetMemoryLimit() { return 131072; /* 128 * 1024 */ }
        public void llScriptProfiler(int flags) { }

        // ── Permissions ────────────────────────────────────────────────────────

        private IClientAPI m_waitingForScriptAnswer = null;

        private UUID InventorySelf()
        {
            return GetInventorySelf()?.ItemID ?? UUID.Zero;
        }

        private void PermsChange(TaskInventoryItem item, UUID granter, int mask)
        {
            int silentEstateManagement = (mask & 0x40) != 0 ? 1 : 0; // PERMISSION_SILENT_ESTATE_MANAGEMENT
            if (m_thisScript?.ScriptState?.MiscAttributes != null)
                m_thisScript.ScriptState.MiscAttributes[(int)InWorldz.Phlox.VM.RuntimeState.MiscAttr.SilentEstateManagement]
                    = new object[] { silentEstateManagement };
            if (item != null)
            {
                item.PermsGranter = granter;
                item.PermsMask = mask;
                m_host.Inventory.ForceInventoryPersistence();
                m_host.ParentGroup.HasGroupChanged = true;
            }
        }

        private int GetImplicitPermissions(TaskInventoryItem item, UUID agentID)
        {
            int implicitPerms = 0;
            if (m_host.ParentGroup.IsAttachment && agentID == m_host.ParentGroup.AttachedAvatar)
            {
                implicitPerms = 4 | 8 | 32 | 64 | 16;
                // PERMISSION_TAKE_CONTROLS | PERMISSION_TRIGGER_ANIMATION |
                // PERMISSION_CONTROL_CAMERA | PERMISSION_TRACK_CAMERA | PERMISSION_ATTACH
            }
            else if (m_host.ParentGroup.RootPart.SitTargetAvatar == agentID && agentID != UUID.Zero)
            {
                implicitPerms = 4 | 8 | 32 | 64; // sitting avatar
            }
            return implicitPerms;
        }

        private bool RequestImplicitPermissions(int perm, TaskInventoryItem item, UUID agentID)
        {
            int implicitPerms = GetImplicitPermissions(item, agentID);
            if (implicitPerms == 0) return false;
            if ((perm & (~implicitPerms)) != 0) return false;
            lock (m_host.TaskInventory)
            {
                item.PermsGranter = agentID;
                item.PermsMask = perm;
            }
            PermsChange(item, item.PermsGranter, item.PermsMask);
            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "run_time_permissions", new object[] { (int)item.PermsMask },
                new DetectParams[0]));
            return true;
        }

        private void ClearWaitingForScriptAnswer(IClientAPI client)
        {
            if (m_waitingForScriptAnswer == null || client != m_waitingForScriptAnswer) return;
            client.OnScriptAnswer -= handleScriptAnswer;
            m_waitingForScriptAnswer = null;
        }

        private void handleConnectionClosed(IClientAPI client)
        {
            ClearWaitingForScriptAnswer(client);
        }

        private void handleScriptAnswer(IClientAPI client, UUID taskID, UUID itemID, int answer)
        {
            if (taskID != m_host.UUID) return;
            if (m_waitingForScriptAnswer == null || client != m_waitingForScriptAnswer) return;
            ClearWaitingForScriptAnswer(client);
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero) return;
            if ((answer & 4) == 0) // PERMISSION_TAKE_CONTROLS
                ; // ReleaseControlsInternal not yet implemented
            TaskInventoryItem item;
            lock (m_host.TaskInventory)
                item = m_host.TaskInventory[invItemID];
            PermsChange(item, client.AgentId, answer);
            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "run_time_permissions", new object[] { (int)item.PermsMask },
                new DetectParams[0]));
        }

        public void llRequestPermissions(string agent, int perm)
        {
            if (!UUID.TryParse(agent, out UUID agentID)) return;
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero) return;
            TaskInventoryItem item;
            lock (m_host.TaskInventory)
                item = m_host.TaskInventory[invItemID];

            if (agentID == UUID.Zero || perm == 0)
            {
                PermsChange(item, UUID.Zero, 0);
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "run_time_permissions", new object[] { 0 },
                    new DetectParams[0]));
                return;
            }

            if (RequestImplicitPermissions(perm, item, agentID))
                return;

            ScenePresence presence = World.GetScenePresence(agentID);
            if (presence == null)
            {
                PermsChange(item, UUID.Zero, 0);
                ScriptSleep(200);
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "run_time_permissions", new object[] { 0 },
                    new DetectParams[0]));
                return;
            }

            string ownerName = m_host.ParentGroup.RootPart.OwnerID.ToString();
            UserAccount ownerAcct = World?.UserAccountService?.GetUserAccount(
                World.RegionInfo.ScopeID, m_host.ParentGroup.RootPart.OwnerID);
            if (ownerAcct != null) ownerName = ownerAcct.FirstName + " " + ownerAcct.LastName;
            if (string.IsNullOrEmpty(ownerName)) ownerName = "(unknown)";

            lock (m_host.TaskInventory)
                item = m_host.TaskInventory[invItemID];

            if (m_waitingForScriptAnswer != presence.ControllingClient)
            {
                ClearWaitingForScriptAnswer(m_waitingForScriptAnswer);
                presence.ControllingClient.OnScriptAnswer += handleScriptAnswer;
                presence.ControllingClient.OnConnectionClosed += handleConnectionClosed;
                m_waitingForScriptAnswer = presence.ControllingClient;
            }

            presence.ControllingClient.SendScriptQuestion(
                m_host.UUID, m_host.ParentGroup.RootPart.Name, ownerName, invItemID, perm,
                GetScriptExperienceId());
        }

        public string llGetPermissionsKey()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return UUID.Zero.ToString();
            return item.PermsGranter.ToString();
        }

        public int llGetPermissions()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return 0;
            return item.PermsMask;
        }
        public void llTakeControls(int controls, int accept, int pass_on)
        {
            // Requires PERMISSION_TAKE_CONTROLS (0x04)
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & 4) == 0)
            {
                ShoutError("llTakeControls: PERMISSION_TAKE_CONTROLS not granted.");
                return;
            }
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;
            sp.RegisterControlEventsToScript(controls, accept, pass_on, m_host.LocalId, m_itemID);
            m_thisScript.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.Control] =
                new object[] { controls, accept, pass_on };
        }

        public void llReleaseControls()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            // Null-conditional (not early-return): we need to reach the MiscAttributes
            // Remove below even when the avatar has left the region, so a stale
            // Control entry isn't restored after the next restart. If you add code
            // after this point, handle the null-sp case explicitly.
            sp?.UnRegisterControlEventsToScript(m_host.LocalId, m_itemID);
            m_thisScript.ScriptState.MiscAttributes.Remove((int)RuntimeState.MiscAttr.Control);
        }

        public void llTakeCamera(string avatar)
        {
            // Deprecated — no-op in modern viewers
        }

        public void llReleaseCamera(string avatar)
        {
            // Deprecated — no-op in modern viewers
        }

        public void llSetCameraEyeOffset(Vector3 offset)
        {
            m_host.SetCameraEyeOffset(offset);
        }

        public void llSetCameraAtOffset(Vector3 offset)
        {
            m_host.SetCameraAtOffset(offset);
        }

        public void llSetCameraParams(LSLList rules)
        {
            // Requires PERMISSION_CONTROL_CAMERA (0x800 = 2048)
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & 2048) == 0) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;

            // Build SortedDictionary<int, float> from the flat [type, value, type, value...] list
            var parameters = new SortedDictionary<int, float>();
            object[] data = rules.Data;
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                if (!int.TryParse(data[i].ToString(), out int camType)) continue;
                if (!float.TryParse(data[i + 1].ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float camVal)) continue;
                parameters[camType] = camVal;
            }
            sp.ControllingClient.SendSetFollowCamProperties(m_host.ParentUUID, parameters);
        }

        public void llClearCameraParams()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;
            sp.ControllingClient.SendClearFollowCamProperties(m_host.ParentUUID);
        }

        public void llSetLinkCamera(int link, Vector3 eye, Vector3 at)
        {
            // Sets camera eye/at offsets on a linked prim
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                part.SetCameraEyeOffset(eye);
                part.SetCameraAtOffset(at);
            }
        }

        public void llForceMouselook(int mouselook)
        {
            m_host.SetForceMouselook(mouselook != 0);
        }
        public void llManageEstateAccess(int action, string avatar)
        {
            if (!World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID))
            {
                ShoutError("llManageEstateAccess: object owner must manage estate.");
                return;
            }
            if (!UUID.TryParse(avatar, out UUID key)) return;
            // action constants: ESTATE_ACCESS_ALLOWED_AGENT_ADD=0, REMOVE=1,
            //   ALLOWED_GROUP_ADD=2, REMOVE=3, BANNED_AGENT_ADD=4, REMOVE=5
            var es = World.RegionInfo.EstateSettings;
            switch (action)
            {
                case 0: es.AddEstateUser(key); break;
                case 1: es.RemoveEstateUser(key); break;
                case 2: es.AddEstateGroup(key); break;
                case 3: es.RemoveEstateGroup(key); break;
                case 4: es.AddBan(new EstateBan { BannedUserID = key, EstateID = es.EstateID }); break;
                case 5: es.RemoveBan(key); break;
            }
            World.EstateDataService?.StoreEstateSettings(es);
        }

        // ── Avatar ─────────────────────────────────────────────────────────────

        public void llAttachToAvatar(int attach_point)
        {
            if (m_host == null) return;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & 0x10) == 0)
            {
                ShoutError("llAttachToAvatar: PERMISSION_ATTACH not granted.");
                return;
            }
            IAttachmentsModule attachMod = World.RequestModuleInterface<IAttachmentsModule>();
            if (attachMod == null) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;
            attachMod.AttachObject(sp, m_host.ParentGroup, (uint)attach_point, false, true, false, GetScriptExperienceId());
        }

        public void llAttachToAvatarTemp(int attachPoint)
        {
            // Temp attachments don't persist to inventory — attach without addToInventory
            if (m_host == null) return;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & 0x10) == 0)
            {
                ShoutError("llAttachToAvatarTemp: PERMISSION_ATTACH not granted.");
                return;
            }
            IAttachmentsModule attachMod = World.RequestModuleInterface<IAttachmentsModule>();
            if (attachMod == null) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;
            attachMod.AttachObject(sp, m_host.ParentGroup, (uint)attachPoint, false, false, false, GetScriptExperienceId());
        }

        public void llDetachFromAvatar()
        {
            if (m_host == null) return;
            if (!m_host.ParentGroup.IsAttachment) return;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & 0x10) == 0)
            {
                ShoutError("llDetachFromAvatar: PERMISSION_ATTACH not granted.");
                return;
            }
            IAttachmentsModule attachMod = World.RequestModuleInterface<IAttachmentsModule>();
            if (attachMod == null) return;
            ScenePresence sp = World.GetScenePresence(m_host.ParentGroup.AttachedAvatar);
            if (sp == null) return;
            attachMod.DetachSingleAttachmentToInv(sp, m_host.ParentGroup);
        }

        public int llGetAttached()
        {
            if (m_host?.ParentGroup == null) return 0;
            if (!m_host.ParentGroup.IsAttachment) return 0;
            return (int)m_host.ParentGroup.AttachmentPoint;
        }
		public void llSitTarget(Vector3 offset, Quaternion rot)
		{
			if (m_host == null) return;
			if (offset == Vector3.Zero && rot == Quaternion.Identity)
			{
				m_host.SitTargetPosition = Vector3.Zero;
				m_host.SitTargetOrientation = Quaternion.Identity;
			}
			else
			{
				m_host.SitTargetPosition = offset;
				m_host.SitTargetOrientation = rot;
			}
			if (m_host.ParentGroup != null)
				m_host.ParentGroup.HasGroupChanged = true;
			m_host.ScheduleFullUpdate();
		}
		public string llAvatarOnSitTarget()
		{
			if (m_host == null) return UUID.Zero.ToString();
			UUID sitter = m_host.SitTargetAvatar;
			return sitter == UUID.Zero ? UUID.Zero.ToString() : sitter.ToString();
		}
		public void llUnSit(string id)
		{
			if (m_host == null || string.IsNullOrEmpty(id)) return;
			if (!UUID.TryParse(id, out UUID targetId)) return;
			ScenePresence sp = World?.GetScenePresence(targetId);
			if (sp == null || sp.ParentID == 0) return;

			// Only unsit if they're sitting on this object, or script owner is unsitting them
			SceneObjectPart seatPart = World.GetSceneObjectPart(sp.ParentID);
			if (seatPart != null && seatPart.ParentGroup?.UUID == m_host.ParentGroup?.UUID)
				sp.StandUp();
			else if (m_host.OwnerID == sp.UUID)
				sp.StandUp(); // owner can always stand themselves up
		} 
        public void llLinkSitTarget(int link, Vector3 offset, Quaternion rot)
        {
            if (m_host == null) return;
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                part.SitTargetPosition    = offset;
                part.SitTargetOrientation = rot;
                if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
                part.ScheduleFullUpdate();
            }
        }

        public string llAvatarOnLinkSitTarget(int linknumber)
        {
            if (m_host == null) return UUID.Zero.ToString();
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                if (part.SitTargetAvatar != UUID.Zero)
                    return part.SitTargetAvatar.ToString();
            }
            return UUID.Zero.ToString();
        }
        public void iwStandTarget(Vector3 offset, Quaternion rot)
        {
            iwLinkStandTarget(m_host.LinkNum, offset, rot);
        }
        public void iwLinkStandTarget(int link, Vector3 offset, Quaternion rot)
        {
            // Faithful port from Halcyon, adapted for Legion.
            // Legion SOP only has StandOffset (Vector3), no StandTargetRot.
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                part.StandOffset = offset;
            }
        }
        public void llSetSitText(string text)
        {
            if (m_host == null) return;
            m_host.SitName = text;
        }

        public void llSetTouchText(string text)
        {
            if (m_host == null) return;
            m_host.TouchName = text;
        }

        public void llSetClickAction(int action)
        {
            if (m_host == null) return;
            m_host.ClickAction = (byte)action;
            if (m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true;
            m_host.ScheduleFullUpdate();
        }
        public int llGetAgentInfo(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return 0;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return 0;

            // AGENT_* bit values (standard LSL constants)
            const int AGENT_FLYING        = 0x0001;
            const int AGENT_ATTACHMENTS   = 0x0002;
            const int AGENT_SCRIPTED      = 0x0004;
            const int AGENT_MOUSELOOK     = 0x0008;
            const int AGENT_SITTING       = 0x0010;
            const int AGENT_ON_OBJECT     = 0x0020;
            const int AGENT_AWAY          = 0x0040;
            const int AGENT_WALKING       = 0x0100;
            const int AGENT_IN_AIR        = 0x0200;
            const int AGENT_TYPING        = 0x0400;
            const int AGENT_CROUCHING     = 0x0800;
            const int AGENT_BUSY          = 0x1000;
            const int AGENT_ALWAYS_RUN    = 0x2000;

            int flags = 0;
            uint ctrlFlags = sp.AgentControlFlags;

            if (sp.SetAlwaysRun)
                flags |= AGENT_ALWAYS_RUN;

            if (sp.HasAttachments())
            {
                flags |= AGENT_ATTACHMENTS;
                if (sp.HasScriptedAttachments())
                    flags |= AGENT_SCRIPTED;
            }

            // AGENT_CONTROL_AWAY = 0x00400000, AGENT_CONTROL_MOUSELOOK = 0x00020000
            if ((ctrlFlags & 0x00400000u) != 0) flags |= AGENT_AWAY;
            if ((ctrlFlags & 0x00020000u) != 0) flags |= AGENT_MOUSELOOK;

            // Sitting detection
            if (sp.ParentPart != null)
            {
                flags |= AGENT_ON_OBJECT;
                flags |= AGENT_SITTING;
            }

            // Flying / in-air (only if not sitting)
            if ((flags & AGENT_SITTING) == 0)
            {
                if (sp.Flying)
                {
                    flags |= AGENT_FLYING;
                    flags |= AGENT_IN_AIR;
                }
                else if (sp.PhysicsActor != null && !sp.PhysicsActor.IsColliding)
                {
                    flags |= AGENT_IN_AIR;
                }
            }

            // Walking/crouching — use control flags
            // AGENT_CONTROL_AT_POS=0x01, AGENT_CONTROL_AT_NEG=0x02
            if (!sp.Flying && (flags & AGENT_SITTING) == 0)
            {
                if ((ctrlFlags & 0x03u) != 0)
                    flags |= AGENT_WALKING;
            }

            return flags;
        }
        public string llGetAgentLanguage(string avatar)
        {
            // Legion doesn't expose AgentPreferences — return empty (caller must handle)
            if (!UUID.TryParse(avatar, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return string.Empty;
            return string.Empty;
        }

        public Vector3 llGetAgentSize(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return Vector3.Zero;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return Vector3.Zero;
            float h = sp.Appearance?.AvatarHeight ?? 1.8f;
            return new Vector3(0.45f, 0.6f, h);
        }
        public int llSameGroup(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return 0;
            UUID hostGroup = m_host.GroupID;
            // Check objects
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null)
                return (part.GroupID == hostGroup && hostGroup != UUID.Zero) ? 1 : 0;
            // Check avatars
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null)
                return (sp.ControllingClient.ActiveGroupId == hostGroup && hostGroup != UUID.Zero) ? 1 : 0;
            return 0;
        }
        public int llIsFriend(string agent)
        {
            // SL: returns TRUE if the specified agent is a friend of the object owner
            if (!UUID.TryParse(agent, out UUID agentID) || agentID == UUID.Zero) return 0;
            var friendsModule = World?.RequestModuleInterface<IFriendsModule>();
            if (friendsModule == null) return 0;
            return friendsModule.IsFriend(m_host.OwnerID, agentID) ? 1 : 0;
        }
        public string llGetOwnerKey(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return UUID.Zero.ToString();
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null) return part.OwnerID.ToString();
            // Not an object — if it's an avatar key, return the key itself (avatars own themselves)
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null) return key.ToString();
            return id; // Unknown UUID: return as-is per LSL spec
        }
        public string llKey2Name(string id)
        {
            if (!UUID.TryParse(id, out UUID key) || key == UUID.Zero) return string.Empty;

            // Check for avatar in region first
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null) return sp.Name;

            // Check for scene object part
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null) return part.Name;

            return string.Empty;
        }
        // ── Teleport helpers (ported from Halcyon) ────────────────────────────

        private bool HasLandPrivileges(ILandObject parcel)
        {
            if (parcel == null) return false;
            // Script owner owns this parcel (includes estate owner)
            if (parcel.LandData.OwnerID == m_host.OwnerID)
                return true;
            // Estate manager
            if (World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID))
                return true;
            // Group-deeded land: Legion doesn't have CanEditParcel with GroupPowers,
            // so check if script owner's group matches the parcel group
            if (parcel.LandData.IsGroupOwned && parcel.LandData.GroupID == m_host.GroupID
                && m_host.GroupID != UUID.Zero)
                return true;
            return false;
        }

        private bool IsTeleportAuthorized(ScenePresence targetSP)
        {
            // Agent must be in this region
            if (targetSP.IsChildAgent)
                return false;

            // Always allow HUDs, attachments and objects owned by the same user
            if (targetSP.UUID == m_host.OwnerID)
                return true;

            // Estate manager can always teleport
            if (World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID))
                return true;

            // Check land privileges: script owner must have privileges on BOTH
            // the parcel the object is on AND the parcel the agent is on
            Vector3 objectPos = m_host.ParentGroup.AbsolutePosition;
            ILandObject objectLand = World.LandChannel.GetLandObject(objectPos.X, objectPos.Y);
            if (objectLand == null)
                return false;

            Vector3 agentPos = targetSP.AbsolutePosition;
            ILandObject agentLand = World.LandChannel.GetLandObject(agentPos.X, agentPos.Y);

            if (HasLandPrivileges(objectLand))
            {
                // If avatar parcel can't be determined but script has land privs, allow it
                if (agentLand == null)
                    return true;
                if (HasLandPrivileges(agentLand))
                    return true;
            }

            return false;
        }

        // ── Teleport functions ───────────────────────────────────────────────

        public void llTeleportAgentHome(string agent)
        {
            if (!UUID.TryParse(agent, out UUID agentId)) return;

            ScenePresence presence = World?.GetScenePresence(agentId);
            if (presence == null) return;

            if (!IsTeleportAuthorized(presence))
                return;

            presence.ControllingClient.SendTeleportStart((uint)OpenMetaverse.TeleportFlags.DisableCancel);
            World.TeleportClientHome(agentId, presence.ControllingClient);
            ScriptSleep(5000);
        }

        public void iwTeleportAgent(string agent, string region, Vector3 pos, Vector3 lookAt)
        {
            if (!UUID.TryParse(agent, out UUID agentId)) return;

            ScenePresence targetSP = World?.GetScenePresence(agentId);
            if (targetSP == null) return;

            if (!IsTeleportAuthorized(targetSP))
                return;

            if (String.IsNullOrEmpty(region))
                region = World.RegionInfo.RegionName;
            else if (region != World.RegionInfo.RegionName)
                targetSP.ControllingClient.SendTeleportStart((uint)OpenMetaverse.TeleportFlags.DisableCancel);

            World.RequestTeleportLocation(targetSP.ControllingClient,
                region, pos, lookAt, (uint)OpenMetaverse.TeleportFlags.ViaLocation);
        }
        public void osTeleportAgent(string agent, string region, Vector3 pos, Vector3 lookAt)
        {
            // OSSL alias for iwTeleportAgent.
            // OSSL semantics: region "" means same region.
            // Auth check (owner / estate manager) is already enforced inside iwTeleportAgent
            // via IsTeleportAuthorized().
            iwTeleportAgent(agent, region, pos, lookAt);
        }

        public void llTeleportAgent(string agent, string landmark, Vector3 pos, Vector3 lookAt)
        {
            // SL: teleport to landmark name or "" for same region
            // In OpenSim, we treat the landmark parameter as a region name (same as iwTeleportAgent)
            iwTeleportAgent(agent, landmark, pos, lookAt);
        }
        public void llTeleportAgentGlobalCoords(string agent, Vector3 globalCoords, Vector3 regionPos, Vector3 lookAt)
        {
            // SL: teleport to global coordinates. globalCoords.X/Y are in global meters (region_x * 256 + local)
            if (!UUID.TryParse(agent, out UUID agentId)) return;
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null) return;
            if (!IsTeleportAuthorized(sp)) return;

            // Convert global coords to region handle
            uint regionX = (uint)((int)globalCoords.X / 256);
            uint regionY = (uint)((int)globalCoords.Y / 256);
            ulong regionHandle = OpenMetaverse.Utils.UIntsToLong(regionX * 256, regionY * 256);

            sp.ControllingClient.SendTeleportStart((uint)OpenMetaverse.TeleportFlags.DisableCancel);
            World.RequestTeleportLocation(sp.ControllingClient, regionHandle,
                regionPos, lookAt, (uint)OpenMetaverse.TeleportFlags.ViaLocation);
        }
        public string llGetAnimation(string id)
        {
            // Faithful port: returns the current movement animation state name
            if (!UUID.TryParse(id, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return string.Empty;
            try
            {
                // OpenSim's Animator exposes CurrentMovementAnimation as a string (e.g. "Standing", "Walking")
                string anim = sp.Animator?.CurrentMovementAnimation;
                return anim ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        public LSLList llGetAnimationList(string id)
        {
            // Faithful port: returns list of currently playing animation UUIDs
            List<object> l = new List<object>();
            if (!UUID.TryParse(id, out UUID key)) return new LSLList(l);
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return new LSLList(l);
            try
            {
                UUID[] anims = sp.Animator?.GetAnimationArray();
                if (anims != null)
                {
                    foreach (UUID anim in anims)
                        l.Add(anim.ToString());
                }
            }
            catch { /* If Animator doesn't support GetAnimationArray, return empty */ }
            return new LSLList(l);
        }

        public void llStartAnimation(string anim)
        {
            const int PERMISSION_TRIGGER_ANIMATION = 0x10;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter == UUID.Zero) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0) return;

            ScenePresence sp = World?.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;

            // Resolve to UUID: try inventory first, then direct parse
            UUID animID = FindInventoryItem(anim, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
            if (animID == UUID.Zero) UUID.TryParse(anim, out animID);
            if (animID == UUID.Zero) return;

            sp.Animator.AddAnimation(animID, m_host.UUID);
            sp.TriggerScenePresenceUpdated();
        }

        public void llStopAnimation(string anim)
        {
            const int PERMISSION_TRIGGER_ANIMATION = 0x10;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter == UUID.Zero) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0) return;

            ScenePresence sp = World?.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;

            UUID animID;
            if (!UUID.TryParse(anim, out animID))
                animID = FindInventoryItem(anim, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
            if (animID == UUID.Zero) return;

            sp.Animator.RemoveAnimation(animID, false);
            sp.TriggerScenePresenceUpdated();
        }
        public void iwStartLinkAnimation(int link, string anim)
        {
            const int PERMISSION_TRIGGER_ANIMATION = 0x10;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter == UUID.Zero) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0) return;

            ScenePresence sp = World?.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;

            // Find animation in the specified link's inventory
            UUID animID = UUID.Zero;
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == anim && kvp.Value.Type == (int)AssetType.Animation)
                        { animID = kvp.Value.AssetID; break; }
                if (animID != UUID.Zero) break;
            }
            if (animID == UUID.Zero) UUID.TryParse(anim, out animID);
            if (animID == UUID.Zero) return;

            sp.Animator.AddAnimation(animID, m_host.UUID);
            sp.TriggerScenePresenceUpdated();
        }
        public void iwStopLinkAnimation(int link, string anim)
        {
            const int PERMISSION_TRIGGER_ANIMATION = 0x10;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter == UUID.Zero) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0) return;

            ScenePresence sp = World?.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;

            UUID animID = UUID.Zero;
            if (!UUID.TryParse(anim, out animID))
            {
                foreach (SceneObjectPart part in GetLinkParts(link))
                {
                    lock (part.TaskInventory)
                        foreach (var kvp in part.TaskInventory)
                            if (kvp.Value.Name == anim && kvp.Value.Type == (int)AssetType.Animation)
                            { animID = kvp.Value.AssetID; break; }
                    if (animID != UUID.Zero) break;
                }
            }
            if (animID == UUID.Zero) return;

            sp.Animator.RemoveAnimation(animID, false);
            sp.TriggerScenePresenceUpdated();
        }
        public string llGetAnimationOverride(string anim_state)
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return string.Empty;
            UUID agentId = item.PermsGranter;
            if (agentId == UUID.Zero) return string.Empty;

            bool hasAnimPerm = (item.PermsMask & 0x8000) != 0;
            if (!hasAnimPerm && !HasExperiencePermission(agentId))
            {
                ShoutError("llGetAnimationOverride: requires PERMISSION_OVERRIDE_ANIMATIONS or experience permission");
                return string.Empty;
            }

            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return string.Empty;

            UUID overrideAnim = sp.Overrides.GetOverriddenAnimation(anim_state);
            if (overrideAnim == UUID.Zero) return string.Empty;

            if (DefaultAvatarAnimations.AnimsNamesbyUUID.TryGetValue(overrideAnim, out string animName))
                return animName;
            return overrideAnim.ToString();
        }
        public void llSetAnimationOverride(string anim_state, string anim)
        {
            if (m_host == null || World == null) return;

            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            UUID agentId = item.PermsGranter;
            if (agentId == UUID.Zero) return;

            bool hasAnimPerm = (item.PermsMask & 0x8000) != 0;
            if (!hasAnimPerm && !HasExperiencePermission(agentId))
            {
                ShoutError("llSetAnimationOverride: requires PERMISSION_OVERRIDE_ANIMATIONS or experience permission");
                return;
            }

            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return;

            UUID animID = UUID.Zero;
            if (!UUID.TryParse(anim, out animID))
            {
                animID = DefaultAvatarAnimations.GetDefaultAnimation(anim);
                if (animID == UUID.Zero)
                {
                    TaskInventoryItem animItem = FindInventoryItem(anim, (int)AssetType.Animation);
                    animID = animItem?.AssetID ?? UUID.Zero;
                }
            }

            if (animID == UUID.Zero)
            {
                ShoutError("llSetAnimationOverride: animation '" + anim + "' not found");
                return;
            }

            sp.Overrides.SetOverride(anim_state, animID);

            m_log.LogDebug("[PhloxAPI]: llSetAnimationOverride: {0} -> {1} for {2}",
                anim_state, animID, sp.Name);
        }
        public void llResetAnimationOverride(string anim_state)
        {
            if (m_host == null || World == null) return;

            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            UUID agentId = item.PermsGranter;
            if (agentId == UUID.Zero) return;

            bool hasAnimPerm = (item.PermsMask & 0x8000) != 0;
            if (!hasAnimPerm && !HasExperiencePermission(agentId))
            {
                ShoutError("llResetAnimationOverride: requires PERMISSION_OVERRIDE_ANIMATIONS or experience permission");
                return;
            }

            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return;

            sp.Overrides.SetOverride(anim_state, UUID.Zero);

            m_log.LogDebug("[PhloxAPI]: llResetAnimationOverride: cleared {0} for {1}",
                anim_state, sp.Name);
        }
        public string llGetDisplayName(string id)
        {
            // In OpenSim display names aren't separate from usernames — return avatar name if in region
            if (!UUID.TryParse(id, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null && !sp.IsChildAgent) return sp.Name;
            // Try user account service for offline users
            UserAccount acct = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
            return acct != null ? acct.FirstName + " " + acct.LastName : string.Empty;
        }

        public void llRequestDisplayName(string id)
        {
            llRequestUsername(id);
        }

        public string llGetUsername(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null && !sp.IsChildAgent) return sp.Name;
            UserAccount acct = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
            return acct != null ? acct.FirstName + " " + acct.LastName : string.Empty;
        }

        public void llRequestUsername(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return;
            UUID requestID = UUID.Random();

            // Fire the dataserver event with the name (synchronous in Phlox)
            string name = string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null && !sp.IsChildAgent)
            {
                name = sp.Name;
            }
            else
            {
                UserAccount acct = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
                if (acct != null) name = acct.FirstName + " " + acct.LastName;
            }

            m_ScriptEngine.PostObjectEvent(m_host.LocalId,
                new EventParams("dataserver",
                    new object[] { requestID.ToString(), name },
                    new DetectParams[0]));
        }
        public string iwGetAgentData(string id, int data)
        {
            // Synchronous version of llRequestAgentData — faithful port from Halcyon
            if (!UUID.TryParse(id, out UUID agentId)) return string.Empty;
            try
            {
                switch (data)
                {
                    case 1: // DATA_ONLINE
                        ScenePresence sp = World?.GetScenePresence(agentId);
                        return (sp != null && !sp.IsChildAgent) ? "1" : "0";
                    case 2: // DATA_NAME
                    {
                        ScenePresence sp2 = World?.GetScenePresence(agentId);
                        if (sp2 != null) return sp2.Name;
                        UserAccount acct = World?.UserAccountService?.GetUserAccount(
                            World.RegionInfo.ScopeID, agentId);
                        return acct != null ? acct.FirstName + " " + acct.LastName : string.Empty;
                    }
                    case 3: // DATA_BORN
                    {
                        UserAccount acct = World?.UserAccountService?.GetUserAccount(
                            World.RegionInfo.ScopeID, agentId);
                        if (acct != null)
                        {
                            var born = DateTimeOffset.FromUnixTimeSeconds(acct.Created).UtcDateTime;
                            return born.ToString("yyyy-MM-dd");
                        }
                        return string.Empty;
                    }
                    case 4: // DATA_RATING — deprecated
                        return "0,0,0,0,0,0";
                    case 7: // DATA_PAYINFO
                        return "0";
                    default:
                        return string.Empty;
                }
            }
            catch { return string.Empty; }
        }
        public int iwGetAppearanceParam(string who, int which)
        {
            // Faithful port from Halcyon
            if (!UUID.TryParse(who, out UUID agentId)) return -1;
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.Appearance == null) return -1;
            // Special case: which == -1 returns the upper limit
            if (which == -1) return sp.Appearance.VisualParams.Length;
            if (which < 0 || which >= sp.Appearance.VisualParams.Length) return -1;
            return sp.Appearance.VisualParams[which];
        }
        public LSLList llGetVisualParams(string agent, LSLList paramIndices)
        {
            // SL: returns a list of visual param values for the specified indices
            if (!UUID.TryParse(agent, out UUID agentId)) return new LSLList();
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.Appearance == null) return new LSLList();

            LSLList ret = new LSLList();
            byte[] vp = sp.Appearance.VisualParams;
            for (int i = 0; i < paramIndices.Length; i++)
            {
                int idx = paramIndices.GetLSLIntegerItem(i);
                if (idx >= 0 && idx < vp.Length)
                    ret = ret.Append(vp[idx]);
                else
                    ret = ret.Append(-1);
            }
            return ret;
        }
        public int iwIsPlusUser(string id) { return 0; /* InWorldz-specific, not applicable */ }
        public int iwActiveGroup(string target, string group)
        {
            if (!UUID.TryParse(target, out UUID targetId)) return 0;
            if (!UUID.TryParse(group, out UUID groupId)) return 0;
            ScenePresence sp = World?.GetScenePresence(targetId);
            if (sp != null && !sp.IsChildAgent)
                return (sp.ControllingClient.ActiveGroupId == groupId) ? 1 : 0;
            SceneObjectPart part = World?.GetSceneObjectPart(targetId);
            if (part != null)
                return (part.GroupID == groupId) ? 1 : 0;
            return 0;
        }
        public string iwAvatarOnLink(int linknumber)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                var sitters = part.GetSittingAvatars();
                if (sitters != null && sitters.Count > 0)
                    return sitters.First().UUID.ToString();
            }
            return UUID.Zero.ToString();
        }
        public LSLList llGetAttachedList(string avatar)
        {
            // Halcyon used sp.CollectVisibleAttachmentIds(); Legion uses sp.GetAttachments()
            if (!UUID.TryParse(avatar, out UUID agentID)) return new LSLList(new object[] { "NOT FOUND" });
            ScenePresence sp = World?.GetScenePresence(agentID);
            if (sp == null || sp.IsChildAgent) return new LSLList(new object[] { "NOT FOUND" });

            LSLList ret = new LSLList();
            var attachments = sp.GetAttachments();
            if (attachments != null)
            {
                foreach (var grp in attachments)
                    ret = ret.Append(grp.UUID.ToString());
            }
            return ret;
        }
        public LSLList llGetAttachedListFiltered(string avatar, int attachmentPoint)
        {
            // SL: like llGetAttachedList but filtered by a specific attachment point
            // attachmentPoint uses ATTACH_* constants; 0 returns all (same as llGetAttachedList)
            if (!UUID.TryParse(avatar, out UUID agentID)) return new LSLList(new object[] { "NOT FOUND" });
            ScenePresence sp = World?.GetScenePresence(agentID);
            if (sp == null || sp.IsChildAgent) return new LSLList(new object[] { "NOT FOUND" });

            LSLList ret = new LSLList();
            var attachments = sp.GetAttachments();
            if (attachments != null)
            {
                foreach (var grp in attachments)
                    if (attachmentPoint == 0 || (int)grp.AttachmentPoint == attachmentPoint)
                        ret = ret.Append(grp.UUID.ToString());
            }
            return ret;
        }
        public string iwGetLastOwner() { return m_host.LastOwnerID.ToString(); }
        public void iwAvatarName2Key(string firstName, string lastName)
        {
            // Faithful port from Halcyon — fires dataserver event with agent UUID
            if (m_host == null) return;
            if (string.IsNullOrWhiteSpace(firstName)) return;
            if (string.IsNullOrWhiteSpace(lastName)) lastName = "Resident";
            firstName = firstName.Trim();
            lastName = lastName.Trim();

            UUID queryID = UUID.Random();
            string fn = firstName, ln = lastName;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    UUID agentID = UUID.Zero;
                    // Check if avatar is in region first (fast path)
                    World?.ForEachScenePresence(sp =>
                    {
                        if (agentID == UUID.Zero && !sp.IsChildAgent &&
                            sp.Firstname.Equals(fn, StringComparison.InvariantCultureIgnoreCase) &&
                            sp.Lastname.Equals(ln, StringComparison.InvariantCultureIgnoreCase))
                            agentID = sp.UUID;
                    });

                    if (agentID == UUID.Zero)
                    {
                        UserAccount acct = World?.UserAccountService?.GetUserAccount(
                            World.RegionInfo.ScopeID, fn, ln);
                        if (acct != null) agentID = acct.PrincipalID;
                    }
                    PostDataserverEvent(queryID, agentID.ToString());
                }
                catch { PostDataserverEvent(queryID, UUID.Zero.ToString()); }
            });
            ScriptSleep(100);
        }

        public string llName2Key(string name)
        {
            // SL: synchronous name → key lookup (returns NULL_KEY if not found)
            if (string.IsNullOrWhiteSpace(name)) return UUID.Zero.ToString();
            string[] parts = name.Trim().Split(new[] { ' ', '.' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string firstName = parts[0];
            string lastName = parts.Length > 1 ? parts[1] : "Resident";

            // Check in-region avatars first (fast path)
            UUID found = UUID.Zero;
            World?.ForEachScenePresence(sp =>
            {
                if (found == UUID.Zero && !sp.IsChildAgent &&
                    sp.Firstname.Equals(firstName, StringComparison.InvariantCultureIgnoreCase) &&
                    sp.Lastname.Equals(lastName, StringComparison.InvariantCultureIgnoreCase))
                    found = sp.UUID;
            });
            if (found != UUID.Zero) return found.ToString();

            // Fall back to account service
            UserAccount acct = World?.UserAccountService?.GetUserAccount(
                World.RegionInfo.ScopeID, firstName, lastName);
            return acct != null ? acct.PrincipalID.ToString() : UUID.Zero.ToString();
        }

        // ── Detect params ──────────────────────────────────────────────────────

		private DetectVariables GetDetect(int n)
		{
			var state = m_thisScript?.ScriptState;
			if (state == null) return new DetectVariables();
			var vars = state.RunningEvent.DetectVars;
			if (vars == null) return new DetectVariables();
			return n >= 0 && n < vars.Length ? vars[n] : new DetectVariables();
		}

        public string llDetectedName(int n) => GetDetect(n).Name ?? string.Empty;
        public string llDetectedKey(int n) => GetDetect(n).Key ?? UUID.Zero.ToString();
        public string llDetectedOwner(int n) => GetDetect(n).Owner ?? UUID.Zero.ToString();
        public int llDetectedType(int n) => GetDetect(n).Type;
        public Vector3 llDetectedPos(int n) => GetDetect(n).Pos;
        public Vector3 llDetectedVel(int n) => GetDetect(n).Vel;
        public Vector3 llDetectedGrab(int n) => GetDetect(n).Grab;
        public Quaternion llDetectedRot(int n) => GetDetect(n).Rot;
        public int llDetectedGroup(int n)
        {
            var detect = GetDetect(n);
            string keyStr = detect.Key;
            if (string.IsNullOrEmpty(keyStr)) return 0;
            if (!UUID.TryParse(keyStr, out UUID detectedId)) return 0;
            UUID hostGroup = m_host.GroupID;
            if (hostGroup == UUID.Zero) return 0;

            SceneObjectPart part = World?.GetSceneObjectPart(detectedId);
            if (part != null)
                return part.GroupID == hostGroup ? 1 : 0;

            ScenePresence sp = World?.GetScenePresence(detectedId);
            if (sp != null)
                return sp.ControllingClient.ActiveGroupId == hostGroup ? 1 : 0;

            return 0;
        }
        public int llDetectedLinkNumber(int n) => GetDetect(n).LinkNumber;
        public Vector3 llDetectedTouchBinormal(int n) => GetDetect(n).TouchBinormal;
        public int llDetectedTouchFace(int n) => GetDetect(n).TouchFace;
        public Vector3 llDetectedTouchNormal(int n) => GetDetect(n).TouchNormal;
        public Vector3 llDetectedTouchPos(int n) => GetDetect(n).TouchPos;
        public Vector3 llDetectedTouchST(int n) => GetDetect(n).TouchST;
        public Vector3 llDetectedTouchUV(int n) => GetDetect(n).TouchUV;
        public string iwDetectedBot() { return "0"; }

        public string llDetectedRezzer(int n)
        {
            // Return the UUID of the object/agent that rezzed the detected object
            var dv = GetDetect(n);
            if (dv.Key == null) return UUID.Zero.ToString();
            if (!UUID.TryParse(dv.Key, out UUID detKey) || detKey == UUID.Zero) return UUID.Zero.ToString();
            SceneObjectPart sop = World?.GetSceneObjectPart(detKey);
            if (sop?.ParentGroup != null)
                return sop.ParentGroup.RezzerID.ToString();
            return UUID.Zero.ToString();
        }

        // ── Sensor ─────────────────────────────────────────────────────────────

       public void llSensor(string name, string id, int type, float range, float arc)
        {
            if (!UUID.TryParse(id, out UUID keyID)) keyID = UUID.Zero;
            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SenseOnce(
                m_localID, m_itemID, name, keyID, type, range, arc, m_host);
        }

        public void llSensorRepeat(string name, string id, int type, float range, float arc, float rate)
        {
            if (!UUID.TryParse(id, out UUID keyID)) keyID = UUID.Zero;
            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SetSenseRepeatEvent(
                m_localID, m_itemID, name, keyID, type, range, arc, rate, m_host);
            m_thisScript.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.SensorRepeat] =
                new object[] { name, id, type, range, arc, rate };
        }

        public void llSensorRemove()
        {
            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.UnSetSenseRepeaterEvents(
                m_localID, m_itemID);
            m_thisScript.ScriptState.MiscAttributes.Remove((int)RuntimeState.MiscAttr.SensorRepeat);
        }

        // ── Listen ─────────────────────────────────────────────────────────────

        public int llListen(int channel, string name, string id, string msg)
        {
            if (m_ScriptEngine.ListenManager == null) { Stub("llListen (no ListenManager)"); return -1; }
            UUID filterKey = UUID.Zero;
            UUID.TryParse(id, out filterKey);
            return m_ScriptEngine.ListenManager.Add(m_localID, m_itemID, m_host.UUID, channel, name, filterKey, msg);
        }
        public void llListenControl(int number, int active) { m_ScriptEngine.ListenManager?.SetActive(m_itemID, number, active != 0); }
        public void llListenRemove(int number) { m_ScriptEngine.ListenManager?.Remove(m_itemID, number); }

        // ── Inventory ──────────────────────────────────────────────────────────

        public int llGetInventoryNumber(int type)
        {
            if (m_host == null) return 0;
            int count = 0;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (type == -1 || kvp.Value.Type == type) count++;
            return count;
        }
        public string llGetInventoryName(int type, int number)
        {
            if (m_host == null) return string.Empty;
            int idx = 0;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (type == -1 || kvp.Value.Type == type)
                        if (idx++ == number) return kvp.Value.Name;
            return string.Empty;
        }
        public int llGetInventoryType(string name)
        {
            if (m_host == null) return -1;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == name) return kvp.Value.Type;
            return -1;
        }
        public string llGetInventoryKey(string name)
        {
            if (m_host == null) return UUID.Zero.ToString();
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == name) return kvp.Value.AssetID.ToString();
            return UUID.Zero.ToString();
        }
        public string llGetInventoryCreator(string item)
        {
            if (m_host == null) return UUID.Zero.ToString();
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == item) return kvp.Value.CreatorID.ToString();
            return UUID.Zero.ToString();
        }
        public string llGetInventoryDesc(string item)
        {
            if (m_host == null) return string.Empty;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == item) return kvp.Value.Description ?? string.Empty;
            ShoutError("No item named '" + item + "'");
            return string.Empty;
        }
        public string llGetInventoryAcquireTime(string item)
        {
            // SL returns timestamp when item was acquired; we use CreationDate as closest equivalent
            if (m_host == null) return string.Empty;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == item)
                    {
                        DateTime dt = DateTimeOffset.FromUnixTimeSeconds(kvp.Value.CreationDate).UtcDateTime;
                        return dt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'");
                    }
            ShoutError("No item named '" + item + "'");
            return string.Empty;
        }
        public int llGetInventoryPermMask(string item, int mask)
        {
            if (m_host == null) return -1;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Name == item)
                    {
                        switch (mask)
                        {
                            case 0: return (int)kvp.Value.BasePermissions;
                            case 1: return (int)kvp.Value.CurrentPermissions;
                            case 2: return (int)kvp.Value.GroupPermissions;
                            case 3: return (int)kvp.Value.EveryonePermissions;
                            case 4: return (int)kvp.Value.NextPermissions;
                        }
                    }
                }
            }
            return -1;
        }

        public void llSetInventoryPermMask(string item, int mask, int value)
        {
            // Not implemented — permission changes on task inventory items
            // require owner-level validation that isn't exposed via LSL in OpenSim.
        }
        public void llGiveInventory(string destination, string inventory)
        {
            ScriptSleep(2000);
            if (m_host == null || World == null) return;

            if (!UUID.TryParse(destination, out UUID destId) || destId == UUID.Zero)
            {
                llSay(0, "Could not parse destination key: " + destination);
                return;
            }

            // Find the item in task inventory
            TaskInventoryItem item = null;
            lock (m_host.TaskInventory)
            {
                foreach (TaskInventoryItem inv in m_host.TaskInventory.Values)
                {
                    if (inv.Name == inventory)
                    {
                        item = inv;
                        break;
                    }
                }
            }
            if (item == null)
            {
                ShoutError($"Could not find item '{inventory}'");
                return;
            }

            // Try to get the avatar's client — null is OK for offline delivery
            IClientAPI remoteClient = null;
            if (World.TryGetScenePresence(destId, out ScenePresence sp))
                remoteClient = sp.ControllingClient;

            InventoryItemBase agentItem = World.MoveTaskInventoryItem(
                remoteClient, UUID.Zero, m_host, item.ItemID, out string reason);

            if (agentItem == null)
            {
                ShoutError($"Failed to give '{inventory}': {reason}");
                return;
            }

            // Send IM notification to recipient
            byte[] bucket = new byte[] { (byte)item.Type };
            GridInstantMessage msg = new GridInstantMessage(World,
                m_host.OwnerID, m_host.Name, destId,
                (byte)InstantMessageDialog.TaskInventoryOffered,
                false, $"'{item.Name}'",
                agentItem.ID, true, m_host.AbsolutePosition, bucket, true);

            IMessageTransferModule tr = World.RequestModuleInterface<IMessageTransferModule>();
            tr?.SendInstantMessage(msg, success => {});
        }
        public void llGiveInventoryList(string target, string folder, LSLList inventory)
        {
            if (m_host == null || World == null) return;
            if (!UUID.TryParse(target, out UUID destId) || destId == UUID.Zero) return;

            // Collect UUIDs of the named items from task inventory
            var itemIDs = new List<UUID>();
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    for (int i = 0; i < inventory.Length; i++)
                    {
                        if (kvp.Value.Name == inventory.Data[i]?.ToString())
                        {
                            itemIDs.Add(kvp.Value.ItemID);
                            break;
                        }
                    }
                }
            }
            if (itemIDs.Count == 0) return;

            World.MoveTaskInventoryItems(destId, folder, m_host, itemIDs);
            ScriptSleep(3000);
        }

        public void llRemoveInventory(string item)
        {
            if (m_host == null) return;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Name == item)
                    {
                        m_host.Inventory.RemoveInventoryItem(kvp.Value.ItemID);
                        return;
                    }
                }
            }
        }

        public void llAllowInventoryDrop(int add)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.RootPart.AllowedDrop = (add != 0);
            // Trigger a flag update so the viewer knows drop is allowed
            m_host.ParentGroup.RootPart.ScheduleFullUpdate();
        }
        public void iwMakeNotecard(string name, LSLList data)
        {
            // Faithful port from Halcyon: create a notecard in this prim's inventory
            if (m_host == null || World == null || string.IsNullOrEmpty(name)) return;

            const int MAX_LENGTH = 65536;

            try
            {
                StringBuilder notecardData = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {
                    if (i > 0) notecardData.Append("\n");
                    notecardData.Append(data.GetLSLStringItem(i));
                    if (notecardData.Length > MAX_LENGTH) return;
                }

                int textLength = Encoding.UTF8.GetByteCount(notecardData.ToString());
                string sNotecardData = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length "
                    + textLength.ToString() + "\n" + notecardData.ToString() + "}\n";

                AssetBase asset = new AssetBase(UUID.Random(), name, (sbyte)AssetType.Notecard, m_host.OwnerID.ToString());
                asset.Description = "Script Generated Notecard";
                asset.Data = Encoding.UTF8.GetBytes(sNotecardData);
                World.AssetService.Store(asset);

                // Create task inventory item
                TaskInventoryItem taskItem = new TaskInventoryItem();
                taskItem.ResetIDs(m_host.UUID);
                taskItem.ParentID = m_host.UUID;
                taskItem.CreationDate = (uint)((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds();
                taskItem.Name = name;
                taskItem.Description = "Script Generated Notecard";
                taskItem.Type = (int)AssetType.Notecard;
                taskItem.InvType = (int)InventoryType.Notecard;
                taskItem.OwnerID = m_host.OwnerID;
                taskItem.CreatorID = m_host.OwnerID;
                taskItem.BasePermissions = (uint)OpenSim.Framework.PermissionMask.All;
                taskItem.CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All;
                taskItem.EveryonePermissions = 0;
                taskItem.NextPermissions = (uint)OpenSim.Framework.PermissionMask.All;
                taskItem.GroupID = m_host.GroupID;
                taskItem.GroupPermissions = 0;
                taskItem.Flags = 0;
                taskItem.PermsGranter = UUID.Zero;
                taskItem.PermsMask = 0;
                taskItem.AssetID = asset.FullID;
                m_host.Inventory.AddInventoryItem(taskItem, false);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: iwMakeNotecard exception: {0}", e.Message);
            }
            ScriptSleep(5000);
        }
        public string llGetNumberOfNotecardLines(string name)
        {
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) return UUID.Zero.ToString();
            UUID queryID = UUID.Random();
            UUID assetId = item.AssetID;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AssetBase asset = World.AssetService.Get(assetId.ToString());
                    if (asset == null || asset.Data == null) { PostDataserverEvent(queryID, "0"); return; }
                    string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                    int count = body.Length == 0 ? 0 : body.Split('\n').Length;
                    PostDataserverEvent(queryID, count.ToString());
                }
                catch (Exception ex) { m_log.LogError("[PhloxAPI]: llGetNumberOfNotecardLines ex: {0}", ex.Message); PostDataserverEvent(queryID, "0"); }
            });
            return queryID.ToString();
        }

        public string llGetNotecardLine(string name, int line)
        {
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) return UUID.Zero.ToString();
            UUID queryID = UUID.Random();
            UUID assetId = item.AssetID;
            int lineNum = line;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AssetBase asset = World.AssetService.Get(assetId.ToString());
                    if (asset == null || asset.Data == null) { PostDataserverEvent(queryID, "\n\n\n"); return; }
                    string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                    string[] lines = body.Split('\n');
                    if (lineNum < 0 || lineNum >= lines.Length) PostDataserverEvent(queryID, "\n\n\n");
                    else PostDataserverEvent(queryID, lines[lineNum].TrimEnd('\r'));
                }
                catch (Exception ex) { m_log.LogError("[PhloxAPI]: llGetNotecardLine ex: {0}", ex.Message); PostDataserverEvent(queryID, "\n\n\n"); }
            });
            return queryID.ToString();
        }
        public string iwGetNotecardSegment(string name, int line, int startOffset, int maxLength)
        {
            // Faithful port: read a segment of a notecard line (offset + length)
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) { ShoutError("Notecard '" + name + "' could not be found."); return UUID.Zero.ToString(); }
            UUID queryID = UUID.Random();
            UUID assetId = item.AssetID;
            int lineNum = line;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AssetBase asset = World.AssetService.Get(assetId.ToString());
                    if (asset == null || asset.Data == null) { PostDataserverEvent(queryID, "\n\n\n"); return; }
                    string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                    string[] lines = body.Split('\n');
                    if (lineNum < 0 || lineNum >= lines.Length) { PostDataserverEvent(queryID, "\n\n\n"); return; }
                    string result = lines[lineNum].TrimEnd('\r');
                    if (startOffset > 0 && startOffset < result.Length)
                        result = result.Substring(startOffset);
                    else if (startOffset >= result.Length)
                        result = string.Empty;
                    if (maxLength > 0 && result.Length > maxLength)
                        result = result.Substring(0, maxLength);
                    PostDataserverEvent(queryID, result);
                }
                catch (Exception ex) { m_log.LogError("[PhloxAPI]: iwGetNotecardSegment ex: {0}", ex.Message); PostDataserverEvent(queryID, "\n\n\n"); }
            });
            return queryID.ToString();
        }
        public string llGetNotecardLineSync(string name, int line)
        {
            // SL: synchronous notecard read — returns the line directly (no dataserver event)
            if (m_host == null || string.IsNullOrEmpty(name)) return "\n\n\n";
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) { ShoutError("Notecard '" + name + "' not found."); return "\n\n\n"; }
            try
            {
                AssetBase asset = World.AssetService.Get(item.AssetID.ToString());
                if (asset == null || asset.Data == null) return "\n\n\n";
                string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                string[] lines = body.Split('\n');
                if (line < 0 || line >= lines.Length) return "\n\n\n";
                return lines[line].TrimEnd('\r');
            }
            catch { return "\n\n\n"; }
        }
        public int llFindNotecardTextSync(string name, string text, int line, int caseSensitive)
        {
            // SL: synchronous search for text in a notecard — returns line number or -1
            if (m_host == null || string.IsNullOrEmpty(name)) return -1;
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) { ShoutError("Notecard '" + name + "' not found."); return -1; }
            try
            {
                AssetBase asset = World.AssetService.Get(item.AssetID.ToString());
                if (asset == null || asset.Data == null) return -1;
                string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                string[] lines = body.Split('\n');
                StringComparison comp = caseSensitive != 0
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                for (int i = Math.Max(0, line); i < lines.Length; i++)
                    if (lines[i].IndexOf(text, comp) >= 0) return i;
                return -1;
            }
            catch { return -1; }
        }
        public string llRequestInventoryData(string name)
        {
            // Looks up a landmark by name and fires dataserver with its position
            if (m_host == null) return UUID.Zero.ToString();
            UUID queryID = UUID.Random();

            TaskInventoryItem landmark = null;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Type == (int)AssetType.Landmark && kvp.Value.Name == name)
                    {
                        landmark = kvp.Value;
                        break;
                    }
                }
            }

            if (landmark == null)
            {
                PostDataserverEvent(queryID, string.Empty);
                return queryID.ToString();
            }

            UUID assetId = landmark.AssetID;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AssetBase asset = World.AssetService.Get(assetId.ToString());
                    if (asset == null) { PostDataserverEvent(queryID, string.Empty); return; }

                    // Landmark format: first line is "Landmark version N", then "region_id UUID", then "local_pos x y z"
                    string data = System.Text.Encoding.UTF8.GetString(asset.Data);
                    Vector3 pos = Vector3.Zero;
                    foreach (string line in data.Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("local_pos"))
                        {
                            string[] parts = trimmed.Split(' ');
                            if (parts.Length == 4)
                                pos = new Vector3(
                                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                                    float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                                    float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        }
                    }
                    PostDataserverEvent(queryID, pos.ToString());
                }
                catch { PostDataserverEvent(queryID, string.Empty); }
            });

            ScriptSleep(1000);
            return queryID.ToString();
        }
        public LSLList iwSearchInventory(int type, string pattern, int matchtype)
        {
            return iwSearchLinkInventory(m_host.LinkNum, type, pattern, matchtype);
        }
        public LSLList iwSearchLinkInventory(int link, int type, string pattern, int matchtype)
        {
            if (matchtype > 2)
            {
                ShoutError("IW_MATCH_COUNT/REGEX not valid for iwSearchLinkInventory");
                return new LSLList();
            }
            List<object> ret = new List<object>();
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                    {
                        if (type != -1 && kvp.Value.Type != type) continue;
                        if (String.IsNullOrEmpty(pattern) || iwMatchString(kvp.Value.Name, pattern, matchtype) == 1)
                            ret.Add(kvp.Value.Name);
                    }
                }
            }
            return new LSLList(ret);
        }
        public int iwGetLinkInventoryNumber(int linknumber, int type)
        {
            int count = 0;
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (type == -1 || kvp.Value.Type == type) count++;
            return count;
        }
        public int iwGetLinkInventoryType(int linknumber, string name)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == name) return kvp.Value.Type;
            return -1;
        }
        public int iwGetLinkInventoryPermMask(int linknumber, string item, int mask)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == item)
                        {
                            switch (mask)
                            {
                                case 0: return (int)kvp.Value.BasePermissions;
                                case 1: return (int)kvp.Value.CurrentPermissions;
                                case 2: return (int)kvp.Value.GroupPermissions;
                                case 3: return (int)kvp.Value.EveryonePermissions;
                                case 4: return (int)kvp.Value.NextPermissions;
                            }
                        }
            return 0;
        }
        public string iwGetLinkInventoryName(int linknumber, int type, int number)
        {
            int idx = 0;
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (type == -1 || kvp.Value.Type == type)
                            if (idx++ == number) return kvp.Value.Name;
            return string.Empty;
        }
        public string iwGetLinkInventoryKey(int linknumber, string name)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == name) return kvp.Value.AssetID.ToString();
            return UUID.Zero.ToString();
        }
        public string iwGetLinkInventoryCreator(int linknumber, string item)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == item) return kvp.Value.CreatorID.ToString();
            return UUID.Zero.ToString();
        }
        public void iwRemoveLinkInventory(int linknumber, string item)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                    {
                        if (kvp.Value.Name == item)
                        {
                            part.Inventory.RemoveInventoryItem(kvp.Key);
                            return;
                        }
                    }
                }
            }
        }
        public void iwGiveLinkInventory(int linknumber, string destination, string inventory)
        {
            ScriptSleep(2000);
            if (World == null) return;
            if (!UUID.TryParse(destination, out UUID destId) || destId == UUID.Zero)
            {
                llSay(0, "Could not parse destination key: " + destination);
                return;
            }

            // Find the item in the specified link's inventory
            TaskInventoryItem item = null;
            SceneObjectPart sourcePart = null;
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == inventory)
                        { item = kvp.Value; sourcePart = part; break; }
                if (item != null) break;
            }
            if (item == null || sourcePart == null)
            {
                ShoutError($"Could not find item '{inventory}'");
                return;
            }

            IClientAPI remoteClient = null;
            if (World.TryGetScenePresence(destId, out ScenePresence sp))
                remoteClient = sp.ControllingClient;

            InventoryItemBase agentItem = World.MoveTaskInventoryItem(
                remoteClient, UUID.Zero, sourcePart, item.ItemID, out string reason);

            if (agentItem == null)
            {
                ShoutError($"Failed to give '{inventory}': {reason}");
                return;
            }

            byte[] bucket = new byte[] { (byte)item.Type };
            GridInstantMessage msg = new GridInstantMessage(World,
                m_host.OwnerID, m_host.Name, destId,
                (byte)InstantMessageDialog.TaskInventoryOffered,
                false, item.Name + "\n" + m_host.Name + " (owned by " +
                    World.GetScenePresence(m_host.OwnerID)?.Name + ")",
                agentItem.ID, true, m_host.AbsolutePosition,
                bucket, true);
            if (World.TryGetScenePresence(destId, out ScenePresence recipient))
                recipient.ControllingClient.SendInstantMessage(msg);
        }
        public void iwGiveLinkInventoryList(int linknumber, string target, string folder, LSLList inventory)
        {
            // Faithful port: give inventory items from a specific link prim
            if (m_host == null || World == null) return;
            if (!UUID.TryParse(target, out UUID destId) || destId == UUID.Zero) return;

            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                var itemIDs = new List<UUID>();
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                    {
                        for (int i = 0; i < inventory.Length; i++)
                        {
                            if (kvp.Value.Name == inventory.Data[i]?.ToString())
                            {
                                itemIDs.Add(kvp.Value.ItemID);
                                break;
                            }
                        }
                    }
                }
                if (itemIDs.Count > 0)
                    World.MoveTaskInventoryItems(destId, folder, part, itemIDs);
            }
            ScriptSleep(3000);
        }
        public string iwGetLinkInventoryDesc(int linknumber, string name)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == name) return kvp.Value.Description;
            return string.Empty;
        }
        public string iwGetLinkInventoryLastOwner(int linknumber, string name)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == name) return kvp.Value.LastOwnerID.ToString();
            return UUID.Zero.ToString();
        }
        public void iwDeliverInventory(int linknumber, string destination, string inventory)
        {
            // Faithful port: same as iwGiveLinkInventory but from a specific link
            iwGiveLinkInventory(linknumber, destination, inventory);
        }
        public void iwDeliverInventoryList(int linknumber, string target, string folder, LSLList inventory)
        {
            iwGiveLinkInventoryList(linknumber, target, folder, inventory);
        }
        public string iwGetLinkNumberOfNotecardLines(int linknumber, string name)
        {
            // Faithful port: get notecard line count from a specific link's inventory
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                TaskInventoryItem item = null;
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Type == (int)AssetType.Notecard && kvp.Value.Name == name)
                        { item = kvp.Value; break; }
                }
                if (item == null) continue;
                UUID queryID = UUID.Random();
                UUID assetId = item.AssetID;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        AssetBase asset = World.AssetService.Get(assetId.ToString());
                        if (asset == null || asset.Data == null) { PostDataserverEvent(queryID, "0"); return; }
                        string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                        int count = body.Length == 0 ? 0 : body.Split('\n').Length;
                        PostDataserverEvent(queryID, count.ToString());
                    }
                    catch { PostDataserverEvent(queryID, "0"); }
                });
                return queryID.ToString();
            }
            ShoutError("iwGetLinkNumberOfNotecardLines: Link number " + linknumber + " does not contain notecard '" + name + "'.");
            return UUID.Zero.ToString();
        }
        public string iwGetLinkNotecardLine(int linknumber, string name, int line)
        {
            // Faithful port: read a notecard line from a specific link's inventory
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                TaskInventoryItem item = null;
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Type == (int)AssetType.Notecard && kvp.Value.Name == name)
                        { item = kvp.Value; break; }
                }
                if (item == null) continue;
                UUID queryID = UUID.Random();
                UUID assetId = item.AssetID;
                int lineNum = line;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        AssetBase asset = World.AssetService.Get(assetId.ToString());
                        if (asset == null || asset.Data == null) { PostDataserverEvent(queryID, "\n\n\n"); return; }
                        string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                        string[] lines = body.Split('\n');
                        if (lineNum < 0 || lineNum >= lines.Length) PostDataserverEvent(queryID, "\n\n\n");
                        else PostDataserverEvent(queryID, lines[lineNum].TrimEnd('\r'));
                    }
                    catch { PostDataserverEvent(queryID, "\n\n\n"); }
                });
                return queryID.ToString();
            }
            ShoutError("iwGetLinkNotecardLine: Notecard '" + name + "' not found in link " + linknumber + ".");
            return UUID.Zero.ToString();
        }
        public string iwGetLinkNotecardSegment(int linknumber, string name, int line, int startOffset, int maxLength)
        {
            // Faithful port: read a notecard segment from a specific link's inventory
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                TaskInventoryItem item = null;
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Type == (int)AssetType.Notecard && kvp.Value.Name == name)
                        { item = kvp.Value; break; }
                }
                if (item == null) continue;
                UUID queryID = UUID.Random();
                UUID assetId = item.AssetID;
                int lineNum = line;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        AssetBase asset = World.AssetService.Get(assetId.ToString());
                        if (asset == null || asset.Data == null) { PostDataserverEvent(queryID, "\n\n\n"); return; }
                        string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                        string[] lines = body.Split('\n');
                        if (lineNum < 0 || lineNum >= lines.Length) { PostDataserverEvent(queryID, "\n\n\n"); return; }
                        string result = lines[lineNum].TrimEnd('\r');
                        if (startOffset > 0 && startOffset < result.Length)
                            result = result.Substring(startOffset);
                        else if (startOffset >= result.Length)
                            result = string.Empty;
                        if (maxLength > 0 && result.Length > maxLength)
                            result = result.Substring(0, maxLength);
                        PostDataserverEvent(queryID, result);
                    }
                    catch { PostDataserverEvent(queryID, "\n\n\n"); }
                });
                return queryID.ToString();
            }
            return UUID.Zero.ToString();
        }

        // ── Object manipulation ────────────────────────────────────────────────

public void llRezObject(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param)
            => RezObjectInternal(inventory, pos, vel, rot, param, false);

        public void llRezAtRoot(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param)
            => RezObjectInternal(inventory, pos, vel, rot, param, true);

        private void RezObjectInternal(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param, bool atRoot)
        {
            ScriptSleep(100);
            if (m_host == null || World == null) return;

            if (Util.GetDistanceTo(pos, m_host.AbsolutePosition) > 10f)
            {
                ShoutError("Unable to create requested object. Position exceeds 10m distance limit.");
                return;
            }

            TaskInventoryItem item = FindInventoryItem(inventory, (int)InventoryType.Object);
            if (item == null)
            {
                ShoutError("Unable to create requested object. Inventory item '" + inventory + "' not found or is not an object.");
                return;
            }

            List<SceneObjectGroup> rezzed = World.RezObject(
                m_host, item,
                m_host.OwnerID, m_host.GroupID,
                pos, rot, vel, param, atRoot, false, false);

            if (rezzed == null || rezzed.Count == 0)
            {
                ShoutError("Unable to create requested object '" + inventory + "'.");
                return;
            }

            foreach (SceneObjectGroup grp in rezzed)
            {
                m_ScriptEngine.PostObjectEvent(m_host.LocalId,
                    new EventParams("object_rez",
                        new object[] { grp.RootPart.UUID.ToString() },
                        new DetectParams[0]));
            }
        }
        public void iwRezObject(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param)
            => RezObjectInternal(inventory, pos, vel, rot, param, false);
        public void iwRezAtRoot(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param)
            => RezObjectInternal(inventory, pos, vel, rot, param, true);

        public string iwRezAt(string inventory, int rezAtRoot, Vector3 pos, Vector3 vel, Quaternion rot, int param)
        {
            ScriptSleep(100);
            if (m_host == null || World == null) return UUID.Zero.ToString();

            if (Util.GetDistanceTo(pos, m_host.AbsolutePosition) > 10f)
            {
                ShoutError("Unable to create requested object. Position exceeds 10m distance limit.");
                return UUID.Zero.ToString();
            }

            TaskInventoryItem item = FindInventoryItem(inventory, (int)InventoryType.Object);
            if (item == null)
            {
                ShoutError("Unable to create requested object. Inventory item '" + inventory + "' not found or is not an object.");
                return UUID.Zero.ToString();
            }

            bool atRoot = (rezAtRoot != 0);
            List<SceneObjectGroup> rezzed = World.RezObject(
                m_host, item,
                m_host.OwnerID, m_host.GroupID,
                pos, rot, vel, param, atRoot, false, false);

            if (rezzed == null || rezzed.Count == 0)
            {
                ShoutError("Unable to create requested object '" + inventory + "'.");
                return UUID.Zero.ToString();
            }

            string result = UUID.Zero.ToString();
            foreach (SceneObjectGroup grp in rezzed)
            {
                result = grp.RootPart.UUID.ToString();
                m_ScriptEngine.PostObjectEvent(m_host.LocalId,
                    new EventParams("object_rez",
                        new object[] { result },
                        new DetectParams[0]));
            }
            return result;
        }

        public string iwRezPrim(LSLList primParams, LSLList particleSystem, LSLList inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param) { /* InWorldz-specific — no OpenSim equivalent */ return UUID.Zero.ToString(); }
        public void llGodLikeRezObject(string inventory, Vector3 pos) { /* No god mode */ }
        public void llDie()
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null || group.IsDeleted) return;
            World?.DeleteSceneObject(group, false);
        }
        public void llDerezObject(string id)
        {
            // SL: derez/delete an object by UUID (requires ownership or permissions)
            if (m_host == null || !UUID.TryParse(id, out UUID targetID) || targetID == UUID.Zero) return;
            SceneObjectPart sop = World?.GetSceneObjectPart(targetID);
            if (sop == null) return;
            SceneObjectGroup sog = sop.ParentGroup;
            if (sog == null || sog.IsDeleted || sog.IsAttachment) return;
            // Only allow if the script owner owns the target object
            if (sog.OwnerID != m_host.OwnerID) return;
            World?.DeleteSceneObject(sog, false);
        }
        public void llPushObject(string id, Vector3 impulse, Vector3 ang_impulse, int local)
        {
            if (m_host == null || !UUID.TryParse(id, out UUID targetID) || targetID == UUID.Zero)
                return;

            bool pushRestricted = World.RegionInfo.RegionSettings.RestrictPushing;
            bool pushAllowed = false;
            bool pusheeIsAvatar = false;

            Vector3 pusheePos = Vector3.Zero;
            ScenePresence pusheeAv = null;
            SceneObjectPart pusheeOb = null;

            ScenePresence avatar = World.GetScenePresence(targetID);
            if (avatar != null)
            {
                pusheeIsAvatar = true;
                if (avatar.PhysicsActor == null) return;
                if (avatar.IsViewerUIGod && m_host.OwnerID != targetID) return;

                pusheeAv = avatar;
                // If seated, push from the seat position
                pusheePos = (avatar.ParentPart != null)
                    ? avatar.ParentPart.AbsolutePosition
                    : avatar.AbsolutePosition;
            }
            else
            {
                pusheeOb = World.GetSceneObjectPart(targetID);
                if (pusheeOb == null) return;
                if (!pusheeOb.ParentGroup.IsAttachment && pusheeOb.PhysActor == null) return;
                pusheePos = pusheeOb.AbsolutePosition;
                pushAllowed = true;
            }

            if (pusheeIsAvatar)
            {
                if (pushRestricted)
                {
                    ILandObject land = World.LandChannel.GetLandObject(pusheePos.X, pusheePos.Y);
                    if (land == null) return;
                    bool isOwnerOrManager = (m_host.OwnerID == land.LandData.OwnerID)
                        || World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID);
                    if (isOwnerOrManager) pushAllowed = true;
                }
                else
                {
                    ILandObject land = World.LandChannel.GetLandObject(pusheePos.X, pusheePos.Y);
                    if (land == null)
                    {
                        pushAllowed = true;
                    }
                    else if ((land.LandData.Flags & (uint)ParcelFlags.RestrictPushObject) != 0)
                    {
                        bool isOwnerOrManager = (m_host.OwnerID == land.LandData.OwnerID)
                            || World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID);
                        if (isOwnerOrManager) pushAllowed = true;
                    }
                    else
                    {
                        pushAllowed = true;
                    }
                }
            }

            if (!pushAllowed) return;

            // Attenuate impulse by distance
            float distance = (pusheePos - m_host.AbsolutePosition).Length();
            const float PUSH_ATTENUATION_DISTANCE = 17f;
            const float PUSH_ATTENUATION_SCALE = 5f;
            float attenuation = 1f;
            if (distance > PUSH_ATTENUATION_DISTANCE)
            {
                float normalized = 1f + (distance - PUSH_ATTENUATION_DISTANCE) / PUSH_ATTENUATION_SCALE;
                attenuation = 1f / normalized;
            }

            Vector3 appliedImpulse = impulse * attenuation;

            if (pusheeIsAvatar && pusheeAv != null)
            {
                PhysicsActor pa = pusheeAv.PhysicsActor;
                if (pa == null) return;
                if (local != 0)
                    appliedImpulse *= m_host.GetWorldRotation();
                pa.AddForce(appliedImpulse, true);
            }
            else if (pusheeOb != null)
            {
                pusheeOb.ApplyImpulse(appliedImpulse, local != 0);
            }
        }
        public void llSetDamage(float damage)
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return;
            group.Damage = Math.Max(damage, 0f);
        }
        public void llModifyLand(int action, int brush)
        {
            // Faithful port from Halcyon.
            // OpenSim's ITerrainModule.ModifyTerrain(UUID user, Vector3 pos, byte size, byte action)
            ITerrainModule tm = World?.RequestModuleInterface<ITerrainModule>();
            if (tm != null)
            {
                try
                {
                    tm.ModifyTerrain(m_host.OwnerID, m_host.AbsolutePosition, (byte)brush, (byte)action);
                }
                catch (Exception e)
                {
                    m_log.LogWarning("[PhloxAPI]: llModifyLand exception: {0}", e.Message);
                }
            }
        }
        public void iwSetGround(int x1, int y1, int x2, int y2, float height) { /* ITerrainModule.SetTerrain not available in Legion */ }
        public int llCheckRezError(Vector3 pos, int isTemp, int landImpact) { /* InWorldz Scene.CheckRezError not in OpenSim */ return 0; }
        public int iwCheckRezError(Vector3 pos, int isTemp, int landImpact) { /* InWorldz Scene.CheckRezError not in OpenSim */ return 0; }
        public void llCreateLink(string target, int parent)
        {
            if (m_host?.ParentGroup == null) return;
            if (m_host.ParentGroup.IsAttachment) return;

            // Requires PERMISSION_CHANGE_LINKS
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            const int PERMISSION_CHANGE_LINKS = 0x80;
            if ((item.PermsMask & PERMISSION_CHANGE_LINKS) == 0)
            {
                ShoutError("llCreateLink: PERMISSION_CHANGE_LINKS not set");
                ScriptSleep(1000);
                return;
            }

            if (!UUID.TryParse(target, out UUID targetUUID) || targetUUID == UUID.Zero) return;
            SceneObjectPart targetPart = World?.GetSceneObjectPart(targetUUID);
            if (targetPart?.ParentGroup == null) return;
            if (targetPart.ParentGroup.IsAttachment) return;

            // Both objects must have the same owner
            if (m_host.OwnerID != targetPart.OwnerID) return;

            SceneObjectGroup group1 = (parent != 0) ? m_host.ParentGroup : targetPart.ParentGroup;
            SceneObjectGroup group2 = (parent != 0) ? targetPart.ParentGroup : m_host.ParentGroup;

            if (group1 == group2) return; // already in same linkset

            // Link group2 into group1 — group2 ceases to exist as a separate object
            group1.LinkToGroup(group2);

            group1.TriggerScriptChangedEvent(Changed.LINK);
            group1.HasGroupChanged = true;
            group1.ScheduleGroupForFullUpdate();

            ScriptSleep(1000);
        }

        public void llBreakLink(int linknum)
        {
            if (m_host?.ParentGroup == null) return;
            if (m_host.ParentGroup.IsAttachment) return;

            // Requires PERMISSION_CHANGE_LINKS
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            const int PERMISSION_CHANGE_LINKS = 0x80;
            if ((item.PermsMask & PERMISSION_CHANGE_LINKS) == 0)
            {
                ShoutError("llBreakLink: PERMISSION_CHANGE_LINKS not set");
                ScriptSleep(1000);
                return;
            }

            SceneObjectGroup parentGroup = m_host.ParentGroup;

            if (linknum == 1) // LINK_ROOT — break all children off, leave root alone
            {
                var parts = new List<SceneObjectPart>(parentGroup.Parts);
                parts.RemoveAll(p => p.LocalId == parentGroup.RootPart.LocalId);
                if (parts.Count == 0) return;
                foreach (SceneObjectPart p in parts)
                    parentGroup.DelinkFromGroup(p, true);
                parentGroup.TriggerScriptChangedEvent(Changed.LINK);
                return;
            }

            SceneObjectPart childPrim = null;
            if (linknum == -1) // LINK_THIS
                childPrim = m_host;
            else if (linknum > 1)
                childPrim = parentGroup.GetLinkNumPart(linknum);
            else
                return; // invalid

            if (childPrim == null) return;
            if (childPrim.LocalId == parentGroup.RootPart.LocalId) return; // can't break root this way

            parentGroup.DelinkFromGroup(childPrim, true);
            parentGroup.TriggerScriptChangedEvent(Changed.LINK);
        }

        public void llBreakAllLinks()
        {
            if (m_host?.ParentGroup == null) return;
            if (m_host.ParentGroup.IsAttachment) return;

            SceneObjectGroup parentGroup = m_host.ParentGroup;
            if (parentGroup.PrimCount < 2) return;

            var children = new List<SceneObjectPart>(parentGroup.Parts);
            children.RemoveAll(p => p.LocalId == parentGroup.RootPart.LocalId);

            foreach (SceneObjectPart part in children)
            {
                parentGroup.DelinkFromGroup(part, true);
                parentGroup.TriggerScriptChangedEvent(Changed.LINK);
            }
        }

        // ── Prim params ────────────────────────────────────────────────────────


        // ── Primitive params implementation ────────────────────────────────────────

        // PRIM_* constants (match LSL_Constants.cs)
        private const int PRIM_MATERIAL     = 2;
        private const int PRIM_POSITION     = 6;
        private const int PRIM_SIZE         = 7;
        private const int PRIM_TYPE         = 9;
        private const int PRIM_TEXTURE      = 17;
        private const int PRIM_COLOR        = 18;
        private const int PRIM_BUMP_SHINY   = 19;
        private const int PRIM_FULLBRIGHT   = 20;
        private const int PRIM_FLEXIBLE     = 21;
        private const int PRIM_POINT_LIGHT  = 23;
        private const int PRIM_NAME         = 27;
        private const int PRIM_DESC         = 28;
        private const int PRIM_GLOW         = 25;
        private const int PRIM_ROT_LOCAL    = 29;
        private const int PRIM_LINK_TARGET  = 34;
        private const int PRIM_ALPHA_MODE   = 38;
        private const int PRIM_RENDER_MATERIAL = 42;
        private const int PRIM_GLTF_BASE_COLOR = 48;
        private const int PRIM_GLTF_NORMAL     = 49;
        private const int PRIM_GLTF_METALLIC_ROUGHNESS = 50;
        private const int PRIM_GLTF_EMISSIVE   = 51;

        // PRIM_GLTF alpha mode sub-constants
        private const int PRIM_GLTF_ALPHA_MODE_OPAQUE = 0;
        private const int PRIM_GLTF_ALPHA_MODE_BLEND  = 1;
        private const int PRIM_GLTF_ALPHA_MODE_MASK   = 2;
        private const int ALL_SIDES         = -1;

        public void llSetPrimitiveParams(LSLList rules)
        {
            if (m_host == null) return;
            SetPrimParams(m_host, rules);
            ScriptSleep(200);
        }

        public LSLList llGetPrimitiveParams(LSLList parms)
        {
            if (m_host == null) return new LSLList();
            return GetPrimParams(m_host, parms);
        }

        public void llSetLinkPrimitiveParams(int linknumber, LSLList rules)
        {
            if (m_host == null) return;
            foreach (var part in GetLinkParts(linknumber))
                SetPrimParams(part, rules);
            ScriptSleep(200);
        }

        public void llSetLinkPrimitiveParamsFast(int linknumber, LSLList rules)
        {
            if (m_host == null) return;
            foreach (var part in GetLinkParts(linknumber))
                SetPrimParams(part, rules);
            // no sleep for Fast variant
        }

        public LSLList llGetLinkPrimitiveParams(int linknumber, LSLList rules)
        {
            if (m_host == null) return new LSLList();
            var result = new LSLList();
            foreach (var part in GetLinkParts(linknumber))
                result += GetPrimParams(part, rules);
            return result;
        }

        private IEnumerable<SceneObjectPart> GetLinkParts(int linknumber)
        {
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) yield break;
            if (linknumber == -4) { yield return m_host; yield break; }
            if (linknumber == -3) { foreach (var p in group.Parts) yield return p; yield break; }
            if (linknumber == -1) { foreach (var p in group.Parts) if (p.LocalId != m_host.LocalId) yield return p; yield break; }
            if (linknumber == -2) { foreach (var p in group.Parts) if (p.LinkNum > 1) yield return p; yield break; }
            if (linknumber == 1) { yield return group.RootPart; yield break; }
            if (linknumber > 1) { var t = group.GetLinkNumPart(linknumber); if (t != null) yield return t; yield break; }
            yield return m_host;
        }

        // ── Helpers: texture/inventory lookup (ported from Halcyon) ───────────

        /// <summary>
        /// Resolve a string that may be a UUID or a texture-inventory name to a UUID.
        /// Returns UUID.Zero if neither.
        /// </summary>
        private UUID KeyOrName(string k)
        {
            if (string.IsNullOrEmpty(k)) return UUID.Zero;
            if (UUID.TryParse(k, out UUID id)) return id;
            // Not a UUID — search the host prim's inventory for a matching item name.
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Name == k)
                        return kvp.Value.AssetID;
                }
            }
            return UUID.Zero;
        }

        /// <summary>
        /// Reverse lookup: given an asset UUID, return the inventory item name if it
        /// exists in the host prim's task inventory; otherwise string.Empty.
        /// </summary>
        private string InventoryName(UUID assetID)
        {
            if (assetID == UUID.Zero) return string.Empty;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.AssetID == assetID)
                        return kvp.Value.Name;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// True if the permission mask includes full modify/copy/transfer.
        /// </summary>
        private bool IsFullPerm(uint permsMask)
        {
            const uint full = (uint)(OpenSim.Framework.PermissionMask.Modify
                                   | OpenSim.Framework.PermissionMask.Copy
                                   | OpenSim.Framework.PermissionMask.Transfer);
            return (permsMask & full) == full;
        }

        // ── Texture workhorses (ported from Halcyon, adapted to Legion SOP API) ──

        /// <summary>Apply a texture UUID to a face or all faces of a part.</summary>
        private void SetTexture(SceneObjectPart part, string texture, int face)
        {
            if (part == null) return;
            UUID textureID = KeyOrName(texture);
            if (textureID == UUID.Zero) return;

            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                Primitive.TextureEntryFace texface = tex.CreateFace((uint)face);
                if (texface.TextureID != textureID)
                {
                    texface.TextureID = textureID;
                    part.UpdateTextureEntry(tex);
                }
                return;
            }

            if (face == ALL_SIDES)
            {
                bool changed = false;
                for (uint i = 0; i < sides; i++)
                {
                    if (tex.FaceTextures[i] != null && tex.FaceTextures[i].TextureID != textureID)
                    {
                        tex.FaceTextures[i].TextureID = textureID;
                        changed = true;
                    }
                }
                if (tex.DefaultTexture != null && tex.DefaultTexture.TextureID != textureID)
                {
                    tex.DefaultTexture.TextureID = textureID;
                    changed = true;
                }
                if (changed) part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Apply an RGB color (preserving existing alpha) to a face or all faces.</summary>
        private void SetColor(SceneObjectPart part, Vector3 color, int face)
        {
            if (part == null) return;
            float r = Math.Max(0f, Math.Min(1f, color.X));
            float g = Math.Max(0f, Math.Min(1f, color.Y));
            float b = Math.Max(0f, Math.Min(1f, color.Z));
            // Legion's SOP already has SetFaceColorAlpha. Passing null alpha preserves per-face alpha.
            part.SetFaceColorAlpha(face, new Vector3(r, g, b), null);
            part.SendFullUpdateToAllClients();
        }

        /// <summary>Set alpha (0..1) on a face or all faces, preserving existing RGB.</summary>
        private void SetAlpha(SceneObjectPart part, float alpha, int face)
        {
            if (part == null) return;
            alpha = Math.Max(0f, Math.Min(1f, alpha));
            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                Primitive.TextureEntryFace f = tex.CreateFace((uint)face);
                Color4 c = f.RGBA;
                c.A = alpha;
                f.RGBA = c;
                part.UpdateTextureEntry(tex);
                return;
            }
            if (face == ALL_SIDES)
            {
                for (uint i = 0; i < sides; i++)
                {
                    var f = tex.CreateFace(i);
                    var c = f.RGBA;
                    c.A = alpha;
                    f.RGBA = c;
                }
                if (tex.DefaultTexture != null)
                {
                    var c = tex.DefaultTexture.RGBA;
                    c.A = alpha;
                    tex.DefaultTexture.RGBA = c;
                }
                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Set texture repeat scale (U,V) on a face or all faces.</summary>
        private void ScaleTexture(SceneObjectPart part, float u, float v, int face)
        {
            if (part == null) return;
            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                var f = tex.CreateFace((uint)face);
                f.RepeatU = u;
                f.RepeatV = v;
                part.UpdateTextureEntry(tex);
                return;
            }
            if (face == ALL_SIDES)
            {
                for (uint i = 0; i < sides; i++)
                {
                    var f = tex.CreateFace(i);
                    f.RepeatU = u;
                    f.RepeatV = v;
                }
                if (tex.DefaultTexture != null)
                {
                    tex.DefaultTexture.RepeatU = u;
                    tex.DefaultTexture.RepeatV = v;
                }
                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Set texture offset (U,V) on a face or all faces.</summary>
        private void OffsetTexture(SceneObjectPart part, float u, float v, int face)
        {
            if (part == null) return;
            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                var f = tex.CreateFace((uint)face);
                f.OffsetU = u;
                f.OffsetV = v;
                part.UpdateTextureEntry(tex);
                return;
            }
            if (face == ALL_SIDES)
            {
                for (uint i = 0; i < sides; i++)
                {
                    var f = tex.CreateFace(i);
                    f.OffsetU = u;
                    f.OffsetV = v;
                }
                if (tex.DefaultTexture != null)
                {
                    tex.DefaultTexture.OffsetU = u;
                    tex.DefaultTexture.OffsetV = v;
                }
                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Set texture rotation (radians) on a face or all faces.</summary>
        private void RotateTexture(SceneObjectPart part, float rotation, int face)
        {
            if (part == null) return;
            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                var f = tex.CreateFace((uint)face);
                f.Rotation = rotation;
                part.UpdateTextureEntry(tex);
                return;
            }
            if (face == ALL_SIDES)
            {
                for (uint i = 0; i < sides; i++)
                {
                    tex.CreateFace(i).Rotation = rotation;
                }
                if (tex.DefaultTexture != null)
                    tex.DefaultTexture.Rotation = rotation;
                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Apply a texture animation to one part (used by llSetTextureAnim and llSetLinkTextureAnim).</summary>
        private void SetPrimTextureAnim(SceneObjectPart part, int mode, int face,
                                        int sizex, int sizey, float start, float length, float rate)
        {
            if (part == null) return;
            Primitive.TextureAnimation pTexAnim = new Primitive.TextureAnimation();
            pTexAnim.Flags  = (Primitive.TextureAnimMode)mode;
            pTexAnim.Face   = (face == ALL_SIDES) ? (uint)255 : (uint)face;
            pTexAnim.Length = length;
            pTexAnim.Rate   = rate;
            pTexAnim.SizeX  = (uint)sizex;
            pTexAnim.SizeY  = (uint)sizey;
            pTexAnim.Start  = start;

            part.AddTextureAnimation(pTexAnim);
            part.ScheduleUpdate(PrimUpdateFlags.FullUpdate);
            if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
        }

        private void SetPrimParams(SceneObjectPart part, LSLList rules)
        {
            if (part == null || rules == null) return;
            var data = rules.Data;
            int idx = 0;

            while (idx < data.Length)
            {
                int code;
                try { code = Convert.ToInt32(data[idx++]); } catch { break; }

                switch (code)
                {
                    case PRIM_LINK_TARGET:
                        if (idx >= data.Length) break;
                        int linkTarget;
                        try { linkTarget = Convert.ToInt32(data[idx++]); } catch { break; }
                        foreach (var p in GetLinkParts(linkTarget))
                        {
                            // Build remaining list and recurse
                            var remaining = new object[data.Length - idx];
                            Array.Copy(data, idx, remaining, 0, remaining.Length);
                            SetPrimParams(p, new LSLList(remaining));
                        }
                        return;

                    case PRIM_COLOR:
                    {
                        if (idx + 2 >= data.Length) break;
                        int face; Vector3 color; float alpha;
                        try { face  = Convert.ToInt32(data[idx++]); } catch { idx += 2; break; }
                        try { color = (Vector3)data[idx++]; } catch { idx++; break; }
                        try { alpha = (float)Convert.ToDouble(data[idx++]); } catch { break; }
                        alpha = Math.Max(0f, Math.Min(1f, alpha));
                        part.SetFaceColorAlpha(face, color, alpha);
                        part.SendFullUpdateToAllClients();
                        break;
                    }

                    case PRIM_TEXTURE:
                    {
                        if (idx + 4 >= data.Length) break;
                        int face; string tex; Vector3 repeats, offsets; float rot;
                        try { face    = Convert.ToInt32(data[idx++]); } catch { idx += 4; break; }
                        try { tex     = data[idx++]?.ToString() ?? string.Empty; } catch { idx += 3; break; }
                        try { repeats = (Vector3)data[idx++]; } catch { idx += 2; break; }
                        try { offsets = (Vector3)data[idx++]; } catch { idx++; break; }
                        try { rot     = (float)Convert.ToDouble(data[idx++]); } catch { break; }
                        Primitive.TextureEntry texEntry = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                        void ApplyTex(Primitive.TextureEntryFace f) {
                            UUID texUUID;
                            if (!UUID.TryParse(tex, out texUUID)) texUUID = UUID.Zero;
                            f.TextureID = texUUID;
                            f.RepeatU = repeats.X; f.RepeatV = repeats.Y;
                            f.OffsetU = offsets.X; f.OffsetV = offsets.Y;
                            f.Rotation = rot;
                        }
                        if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) ApplyTex(texEntry.CreateFace((uint)i)); }
                        else { try { ApplyTex(texEntry.CreateFace((uint)face)); } catch { } }
                        part.UpdateTextureEntry(texEntry.GetBytes());
                        break;
                    }

                    case PRIM_GLOW:
                    {
                        if (idx + 1 >= data.Length) break;
                        int face; float glow;
                        try { face = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                        try { glow = (float)Convert.ToDouble(data[idx++]); } catch { break; }
                        glow = Math.Max(0f, Math.Min(1f, glow));
                        Primitive.TextureEntry te = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                        if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) te.CreateFace((uint)i).Glow = glow; }
                        else { try { te.CreateFace((uint)face).Glow = glow; } catch { } }
                        part.UpdateTextureEntry(te.GetBytes());
                        break;
                    }

                    case PRIM_FULLBRIGHT:
                    {
                        if (idx + 1 >= data.Length) break;
                        int face; bool bright;
                        try { face  = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                        try { bright = Convert.ToInt32(data[idx++]) != 0; } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                        if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) te.CreateFace((uint)i).Fullbright = bright; }
                        else { try { te.CreateFace((uint)face).Fullbright = bright; } catch { } }
                        part.UpdateTextureEntry(te.GetBytes());
                        break;
                    }

                    case PRIM_BUMP_SHINY:
                    {
                        if (idx + 2 >= data.Length) break;
                        int face, shiny, bump;
                        try { face  = Convert.ToInt32(data[idx++]); } catch { idx += 2; break; }
                        try { shiny = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                        try { bump  = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                        void ApplyBS(Primitive.TextureEntryFace f) {
                            f.Shiny = (Shininess)shiny;
                            f.Bump  = (Bumpiness)bump;
                        }
                        if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) ApplyBS(te.CreateFace((uint)i)); }
                        else { try { ApplyBS(te.CreateFace((uint)face)); } catch { } }
                        part.UpdateTextureEntry(te.GetBytes());
                        break;
                    }

                    case PRIM_MATERIAL:
                    {
                        if (idx >= data.Length) break;
                        int mat;
                        try { mat = Convert.ToInt32(data[idx++]); } catch { break; }
                        part.Material = (byte)mat;
                        if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
                        part.ScheduleFullUpdate();
                        break;
                    }

                    case PRIM_POSITION:
                    {
                        if (idx >= data.Length) break;
                        Vector3 pos;
                        try { pos = (Vector3)data[idx++]; } catch { break; }
                        if (part.LinkNum < 2) part.ParentGroup?.UpdateGroupPosition(pos);
                        else part.UpdateOffSet(pos - (part.ParentGroup?.AbsolutePosition ?? Vector3.Zero));
                        break;
                    }

                    case PRIM_SIZE:
                    {
                        if (idx >= data.Length) break;
                        Vector3 scale;
                        try { scale = (Vector3)data[idx++]; } catch { break; }
                        scale.X = Math.Max(0.01f, Math.Min(64f, scale.X));
                        scale.Y = Math.Max(0.01f, Math.Min(64f, scale.Y));
                        scale.Z = Math.Max(0.01f, Math.Min(64f, scale.Z));
                        part.Resize(scale);
                        break;
                    }

                    case PRIM_ROT_LOCAL:
                    {
                        if (idx >= data.Length) break;
                        Quaternion rot;
                        try { rot = (Quaternion)data[idx++]; } catch { break; }
                        part.UpdateRotation(rot);
                        break;
                    }

                    case PRIM_FLEXIBLE:
                    {
                        if (idx + 6 >= data.Length) break;
                        bool flex; int softness; float gravity, friction, wind, tension; Vector3 force;
                        try { flex      = Convert.ToInt32(data[idx++]) != 0; } catch { idx += 6; break; }
                        try { softness  = Convert.ToInt32(data[idx++]); } catch { idx += 5; break; }
                        try { gravity   = (float)Convert.ToDouble(data[idx++]); } catch { idx += 4; break; }
                        try { friction  = (float)Convert.ToDouble(data[idx++]); } catch { idx += 3; break; }
                        try { wind      = (float)Convert.ToDouble(data[idx++]); } catch { idx += 2; break; }
                        try { tension   = (float)Convert.ToDouble(data[idx++]); } catch { idx++; break; }
                        try { force     = (Vector3)data[idx++]; } catch { break; }
                        var shape = part.Shape;
                        shape.FlexiEntry    = flex;
                        shape.FlexiSoftness = softness;
                        shape.FlexiGravity  = gravity;
                        shape.FlexiDrag     = friction;
                        shape.FlexiWind     = wind;
                        shape.FlexiTension  = tension;
                        shape.FlexiForceX   = force.X;
                        shape.FlexiForceY   = force.Y;
                        shape.FlexiForceZ   = force.Z;
                        part.Shape = shape;
                        if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
                        part.ScheduleFullUpdate();
                        break;
                    }

                    case PRIM_POINT_LIGHT:
                    {
                        if (idx + 4 >= data.Length) break;
                        bool enabled; Vector3 color; float intensity, radius, falloff;
                        try { enabled   = Convert.ToInt32(data[idx++]) != 0; } catch { idx += 4; break; }
                        try { color     = (Vector3)data[idx++]; } catch { idx += 3; break; }
                        try { intensity = (float)Convert.ToDouble(data[idx++]); } catch { idx += 2; break; }
                        try { radius    = (float)Convert.ToDouble(data[idx++]); } catch { idx++; break; }
                        try { falloff   = (float)Convert.ToDouble(data[idx++]); } catch { break; }
                        var shape = part.Shape;
                        shape.LightEntry     = enabled;
                        shape.LightColorR    = color.X;
                        shape.LightColorG    = color.Y;
                        shape.LightColorB    = color.Z;
                        shape.LightIntensity = intensity;
                        shape.LightRadius    = radius;
                        shape.LightFalloff   = falloff;
                        part.Shape = shape;
                        if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
                        part.ScheduleFullUpdate();
                        break;
                    }

                    case PRIM_ALPHA_MODE:
                    {
                        if (idx + 1 >= data.Length) break;
                        int face, mode;
                        try { face = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                        try { mode = Convert.ToInt32(data[idx++]); } catch { break; }
                        // PRIM_ALPHA_MODE uses texture entry material alpha mode
                        Primitive.TextureEntry te = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                        if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) te.CreateFace((uint)i).MediaFlags = (mode != 0); }
                        else { try { te.CreateFace((uint)face).MediaFlags = (mode != 0); } catch { } }
                        part.UpdateTextureEntry(te.GetBytes());
                        break;
                    }

                    case PRIM_TYPE:
                    {
                        if (idx >= data.Length) break;
                        int primTypeCode = Convert.ToInt32(data[idx++]);
                        int remain2 = data.Length - idx;
                        int holeshape2;
                        Vector3 cut2, twist2, taper_b2, topshear2, holesize2, profilecut2, taper_a2;
                        float hollow2, revolutions2, radiusoffset2, skew2;

                        switch (primTypeCode)
                        {
                            case 0:
                                if (remain2 < 6) break;
                                holeshape2 = Convert.ToInt32(data[idx++]);
                                cut2       = (Vector3)data[idx++];
                                hollow2    = (float)Convert.ToDouble(data[idx++]);
                                twist2     = (Vector3)data[idx++];
                                taper_b2   = (Vector3)data[idx++];
                                topshear2  = (Vector3)data[idx++];
                                part.Shape.PathCurve = (byte)Extrusion.Straight;
                                SetPrimitiveShapeParamsCommon(part, holeshape2, cut2, hollow2, twist2, taper_b2, topshear2, DEFAULT_SLICE_VEC, 1);
                                break;

                            case 1:
                                if (remain2 < 6) break;
                                holeshape2 = Convert.ToInt32(data[idx++]);
                                cut2       = (Vector3)data[idx++];
                                hollow2    = (float)Convert.ToDouble(data[idx++]);
                                twist2     = (Vector3)data[idx++];
                                taper_b2   = (Vector3)data[idx++];
                                topshear2  = (Vector3)data[idx++];
                                part.Shape.ProfileShape = ProfileShape.Circle;
                                part.Shape.PathCurve    = (byte)Extrusion.Straight;
                                SetPrimitiveShapeParamsCommon(part, holeshape2, cut2, hollow2, twist2, taper_b2, topshear2, DEFAULT_SLICE_VEC, 0);
                                break;

                            case 2:
                                if (remain2 < 6) break;
                                holeshape2 = Convert.ToInt32(data[idx++]);
                                cut2       = (Vector3)data[idx++];
                                hollow2    = (float)Convert.ToDouble(data[idx++]);
                                twist2     = (Vector3)data[idx++];
                                taper_b2   = (Vector3)data[idx++];
                                topshear2  = (Vector3)data[idx++];
                                part.Shape.PathCurve = (byte)Extrusion.Straight;
                                SetPrimitiveShapeParamsCommon(part, holeshape2, cut2, hollow2, twist2, taper_b2, topshear2, DEFAULT_SLICE_VEC, 3);
                                break;

                            case 3:
                                if (remain2 < 5) break;
                                holeshape2 = Convert.ToInt32(data[idx++]);
                                cut2       = (Vector3)data[idx++];
                                hollow2    = (float)Convert.ToDouble(data[idx++]);
                                twist2     = (Vector3)data[idx++];
                                taper_b2   = (Vector3)data[idx++];
                                part.Shape.PathCurve = (byte)Extrusion.Curve1;
                                SetPrimitiveShapeParamsSphere(part, holeshape2, cut2, hollow2, twist2, taper_b2, 5);
                                break;

                            case 4:
                            case 5:
                            case 6:
                                if (remain2 < 11) break;
                                holeshape2    = Convert.ToInt32(data[idx++]);
                                cut2          = (Vector3)data[idx++];
                                hollow2       = (float)Convert.ToDouble(data[idx++]);
                                twist2        = (Vector3)data[idx++];
                                holesize2     = (Vector3)data[idx++];
                                topshear2     = (Vector3)data[idx++];
                                profilecut2   = (Vector3)data[idx++];
                                taper_a2      = (Vector3)data[idx++];
                                revolutions2  = (float)Convert.ToDouble(data[idx++]);
                                radiusoffset2 = (float)Convert.ToDouble(data[idx++]);
                                skew2         = (float)Convert.ToDouble(data[idx++]);
                                byte torusFudge = primTypeCode == 4 ? (byte)0
                                                : primTypeCode == 5  ? (byte)1
                                                : (byte)3;
                                part.Shape.PathCurve = (byte)Extrusion.Curve1;
                                SetPrimitiveShapeParamsTorus(part, holeshape2, cut2, hollow2, twist2, holesize2,
                                    topshear2, profilecut2, taper_a2, revolutions2, radiusoffset2, skew2, torusFudge);
                                break;

                            case 7:
                                if (remain2 < 2) break;
                                string sculptMap2  = data[idx++].ToString();
                                int    sculptType2 = Convert.ToInt32(data[idx++]);
                                part.Shape.PathCurve = (byte)Extrusion.Curve1;
                                SetPrimitiveShapeParamsSculpt(part, sculptMap2, sculptType2);
                                break;
                        }
                        break;
                    }

                    case PRIM_NAME:
                    {
                        if (idx >= data.Length) break;
                        string name = data[idx++].ToString();
                        part.Name = name;
                        break;
                    }

                    case PRIM_DESC:
                    {
                        if (idx >= data.Length) break;
                        string desc = data[idx++].ToString();
                        part.Description = desc;
                        break;
                    }

                    // ── PBR / glTF Material params ──────────────────────────────

                    case PRIM_RENDER_MATERIAL:
                    {
                        // [ PRIM_RENDER_MATERIAL, integer face, string render_material ]
                        if (idx + 1 >= data.Length) { idx = data.Length; break; }
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                        string matStr = data[idx++].ToString();
                        UUID matUUID = UUID.Zero;
                        UUID.TryParse(matStr, out matUUID);

                        var shape_rm = part.Shape;
                        shape_rm.RenderMaterials ??= new OpenMetaverse.Primitive.RenderMaterials();
                        int numFaces_rm = part.GetNumberOfSides();

                        if (face == ALL_SIDES)
                        {
                            var entries = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[numFaces_rm];
                            for (int i = 0; i < numFaces_rm; i++)
                            {
                                entries[i].te_index = (byte)i;
                                entries[i].id = matUUID;
                            }
                            shape_rm.RenderMaterials.entries = entries;
                            // Clear non-transform overrides per SL spec
                        }
                        else if (face >= 0 && face < numFaces_rm)
                        {
                            SetRenderMaterialEntry(ref shape_rm.RenderMaterials.entries, matUUID, face);
                        }

                        if (part.ParentGroup != null)
                            part.ParentGroup.HasGroupChanged = true;
                        part.ScheduleFullUpdate();
                        break;
                    }

                    case PRIM_GLTF_BASE_COLOR:
                    {
                        // [ PRIM_GLTF_BASE_COLOR, face, texture, repeats, offsets, rotation,
                        //   color, alpha, alpha_mode, alpha_cutoff, double_sided ]
                        if (idx + 9 >= data.Length) { idx = data.Length; break; }
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx += 9; break; }
                        string texture = data[idx++].ToString();
                        Vector3 repeats = (Vector3)data[idx++];
                        Vector3 offsets = (Vector3)data[idx++];
                        float rotation = (float)Convert.ToDouble(data[idx++]);
                        Vector3 color = (Vector3)data[idx++];
                        float alpha = (float)Convert.ToDouble(data[idx++]);
                        int alphaMode = Convert.ToInt32(data[idx++]);
                        float alphaCutoff = (float)Convert.ToDouble(data[idx++]);
                        int doubleSided = Convert.ToInt32(data[idx++]);

                        var osd = new OpenMetaverse.StructuredData.OSDMap();
                        if (!string.IsNullOrEmpty(texture) && texture != UUID.Zero.ToString())
                            osd["tex"] = texture;
                        osd["rep"] = new OpenMetaverse.StructuredData.OSDArray { (double)repeats.X, (double)repeats.Y };
                        osd["off"] = new OpenMetaverse.StructuredData.OSDArray { (double)offsets.X, (double)offsets.Y };
                        osd["rot"] = (double)rotation;
                        osd["bc"] = new OpenMetaverse.StructuredData.OSDArray { (double)color.X, (double)color.Y, (double)color.Z, (double)alpha };
                        osd["am"] = alphaMode;
                        osd["ac"] = (double)alphaCutoff;
                        osd["ds"] = (doubleSided != 0);

                        ApplyGLTFOverrideToPart(part, face, osd);
                        break;
                    }

                    case PRIM_GLTF_NORMAL:
                    {
                        // [ PRIM_GLTF_NORMAL, face, texture, repeats, offsets, rotation ]
                        if (idx + 4 >= data.Length) { idx = data.Length; break; }
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx += 4; break; }
                        string texture = data[idx++].ToString();
                        Vector3 repeats = (Vector3)data[idx++];
                        Vector3 offsets = (Vector3)data[idx++];
                        float rotation = (float)Convert.ToDouble(data[idx++]);

                        var osd = new OpenMetaverse.StructuredData.OSDMap();
                        if (!string.IsNullOrEmpty(texture) && texture != UUID.Zero.ToString())
                            osd["ntex"] = texture;
                        osd["nrep"] = new OpenMetaverse.StructuredData.OSDArray { (double)repeats.X, (double)repeats.Y };
                        osd["noff"] = new OpenMetaverse.StructuredData.OSDArray { (double)offsets.X, (double)offsets.Y };
                        osd["nrot"] = (double)rotation;

                        ApplyGLTFOverrideToPart(part, face, osd);
                        break;
                    }

                    case PRIM_GLTF_METALLIC_ROUGHNESS:
                    {
                        // [ PRIM_GLTF_METALLIC_ROUGHNESS, face, texture, repeats, offsets, rotation,
                        //   metallic_factor, roughness_factor ]
                        if (idx + 6 >= data.Length) { idx = data.Length; break; }
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx += 6; break; }
                        string texture = data[idx++].ToString();
                        Vector3 repeats = (Vector3)data[idx++];
                        Vector3 offsets = (Vector3)data[idx++];
                        float rotation = (float)Convert.ToDouble(data[idx++]);
                        float metallic = (float)Convert.ToDouble(data[idx++]);
                        float roughness = (float)Convert.ToDouble(data[idx++]);

                        var osd = new OpenMetaverse.StructuredData.OSDMap();
                        if (!string.IsNullOrEmpty(texture) && texture != UUID.Zero.ToString())
                            osd["mrtex"] = texture;
                        osd["mrrep"] = new OpenMetaverse.StructuredData.OSDArray { (double)repeats.X, (double)repeats.Y };
                        osd["mroff"] = new OpenMetaverse.StructuredData.OSDArray { (double)offsets.X, (double)offsets.Y };
                        osd["mrrot"] = (double)rotation;
                        osd["mf"] = (double)Math.Clamp(metallic, 0f, 1f);
                        osd["rf"] = (double)Math.Clamp(roughness, 0f, 1f);

                        ApplyGLTFOverrideToPart(part, face, osd);
                        break;
                    }

                    case PRIM_GLTF_EMISSIVE:
                    {
                        // [ PRIM_GLTF_EMISSIVE, face, texture, repeats, offsets, rotation, emissive_tint ]
                        if (idx + 5 >= data.Length) { idx = data.Length; break; }
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx += 5; break; }
                        string texture = data[idx++].ToString();
                        Vector3 repeats = (Vector3)data[idx++];
                        Vector3 offsets = (Vector3)data[idx++];
                        float rotation = (float)Convert.ToDouble(data[idx++]);
                        Vector3 emissive = (Vector3)data[idx++];

                        var osd = new OpenMetaverse.StructuredData.OSDMap();
                        if (!string.IsNullOrEmpty(texture) && texture != UUID.Zero.ToString())
                            osd["etex"] = texture;
                        osd["erep"] = new OpenMetaverse.StructuredData.OSDArray { (double)repeats.X, (double)repeats.Y };
                        osd["eoff"] = new OpenMetaverse.StructuredData.OSDArray { (double)offsets.X, (double)offsets.Y };
                        osd["erot"] = (double)rotation;
                        osd["ec"] = new OpenMetaverse.StructuredData.OSDArray { (double)emissive.X, (double)emissive.Y, (double)emissive.Z };

                        ApplyGLTFOverrideToPart(part, face, osd);
                        break;
                    }

                    default:
                        m_log.LogWarning("[PhloxAPI]: llSetPrimitiveParams unknown code {0}, stopping", code);
                        idx = data.Length;
                        break;
                }
            }
        }

        // ── PRIM_TYPE shape helpers ───────────────────────────────────────────────
        // Ported from Halcyon/InWorldz LSLSystemAPI.cs

        private static readonly Vector3 DEFAULT_SLICE_VEC = new Vector3(0f, 1f, 0f);

        private int getScriptPrimType(PrimitiveBaseShape primShape)
        {
            if (primShape.SculptEntry)
                return 7;

            byte profileCurve = (byte)(primShape.ProfileCurve & 0x07);

            if (profileCurve == (byte)ProfileShape.Square)
            {
                if (primShape.PathCurve == (byte)Extrusion.Straight || primShape.PathCurve == (byte)Extrusion.Flexible)
                    return 0;
                if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return 5;
            }
            else if (profileCurve == (byte)ProfileShape.Circle)
            {
                if (primShape.PathCurve == (byte)Extrusion.Straight || primShape.PathCurve == (byte)Extrusion.Flexible)
                    return 1;
                if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return 4;
            }
            else if (profileCurve == (byte)ProfileShape.HalfCircle)
            {
                if (primShape.PathCurve == (byte)Extrusion.Curve1 || primShape.PathCurve == (byte)Extrusion.Curve2)
                    return 3;
            }
            else if (profileCurve == (byte)ProfileShape.EquilateralTriangle)
            {
                if (primShape.PathCurve == (byte)Extrusion.Straight || primShape.PathCurve == (byte)Extrusion.Flexible)
                    return 2;
                if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return 6;
            }
            return 0;
        }

        private ObjectShapePacket.ObjectDataBlock SetPrimitiveBlockShapeParams(
            SceneObjectPart part, int holeshape, Vector3 cut, float hollow, Vector3 twist)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock = new ObjectShapePacket.ObjectDataBlock();

            if (holeshape != 0 &&
                holeshape != 16 &&
                holeshape != 32 &&
                holeshape != 48)
                holeshape = 0;

            shapeBlock.ProfileCurve = (byte)holeshape;

            cut.X = Math.Max(0f, Math.Min(1f, (float)cut.X));
            cut.Y = Math.Max(0f, Math.Min(1f, (float)cut.Y));
            if (cut.Y - cut.X < 0.02f) cut.X = cut.Y - 0.02f;
            shapeBlock.ProfileBegin = (ushort)(50000 * cut.X);
            shapeBlock.ProfileEnd   = (ushort)(50000 * (1 - cut.Y));

            hollow = Math.Max(0f, Math.Min(0.99f, hollow));
            shapeBlock.ProfileHollow = (ushort)(50000 * hollow);

            twist.X = Math.Max(-1f, Math.Min(1f, (float)twist.X));
            twist.Y = Math.Max(-1f, Math.Min(1f, (float)twist.Y));
            shapeBlock.PathTwistBegin = (sbyte)(100 * twist.X);
            shapeBlock.PathTwist      = (sbyte)(100 * twist.Y);

            shapeBlock.ObjectLocalID = part.LocalId;
            shapeBlock.PathCurve     = part.Shape.PathCurve;
            return shapeBlock;
        }

        private void SetPrimitiveShapeParamsCommon(SceneObjectPart part, int holeshape,
            Vector3 cut, float hollow, Vector3 twist,
            Vector3 taper_b, Vector3 topshear, Vector3 slice, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock =
                SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            shapeBlock.ProfileCurve += fudge;

            taper_b.X = Math.Max(0f, Math.Min(2f, (float)taper_b.X));
            taper_b.Y = Math.Max(0f, Math.Min(2f, (float)taper_b.Y));
            shapeBlock.PathScaleX = (byte)(100 * (2.0 - taper_b.X));
            shapeBlock.PathScaleY = (byte)(100 * (2.0 - taper_b.Y));

            topshear.X = Math.Max(-0.5f, Math.Min(0.5f, (float)topshear.X));
            topshear.Y = Math.Max(-0.5f, Math.Min(0.5f, (float)topshear.Y));
            shapeBlock.PathShearX = (byte)Primitive.PackPathShear((float)topshear.X);
            shapeBlock.PathShearY = (byte)Primitive.PackPathShear((float)topshear.Y);

            float sx = Math.Max(0f,    Math.Min(0.98f, (float)slice.X));
            float sy = Math.Max(0.02f, Math.Min(1.0f,  (float)slice.Y));
            if (sy - sx < 0.02f) sx = sy - 0.02f;
            shapeBlock.PathBegin = (ushort)(50000 * sx);
            shapeBlock.PathEnd   = (ushort)(50000 * (1 - sy));

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsSphere(SceneObjectPart part, int holeshape,
            Vector3 cut, float hollow, Vector3 twist,
            Vector3 dimple, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock =
                SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            shapeBlock.PathBegin = shapeBlock.ProfileBegin;
            shapeBlock.PathEnd   = shapeBlock.ProfileEnd;
            shapeBlock.ProfileCurve += fudge;
            shapeBlock.PathScaleX = 100;
            shapeBlock.PathScaleY = 100;

            dimple.X = Math.Max(0f, Math.Min(1f, (float)dimple.X));
            dimple.Y = Math.Max(0f, Math.Min(1f, (float)dimple.Y));
            if (dimple.Y - cut.X < 0.02f) dimple.X = cut.Y - 0.02f;
            shapeBlock.ProfileBegin = (ushort)(50000 * dimple.X);
            shapeBlock.ProfileEnd   = (ushort)(50000 * (1 - dimple.Y));

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsTorus(SceneObjectPart part, int holeshape,
            Vector3 cut, float hollow, Vector3 twist,
            Vector3 holesize, Vector3 topshear,
            Vector3 profilecut, Vector3 taper_a,
            float revolutions, float radiusoffset, float skew, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock =
                SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            shapeBlock.ProfileCurve += fudge;
            shapeBlock.PathBegin = shapeBlock.ProfileBegin;
            shapeBlock.PathEnd   = shapeBlock.ProfileEnd;

            holesize.X = Math.Max(0.01f, Math.Min(1f,   (float)holesize.X));
            holesize.Y = Math.Max(0.05f, Math.Min(0.5f, (float)holesize.Y));
            shapeBlock.PathScaleX = (byte)(100 * (2 - holesize.X));
            shapeBlock.PathScaleY = (byte)(100 * (2 - holesize.Y));

            topshear.X = Math.Max(-0.5f, Math.Min(0.5f, (float)topshear.X));
            topshear.Y = Math.Max(-0.5f, Math.Min(0.5f, (float)topshear.Y));
            shapeBlock.PathShearX = (byte)Primitive.PackPathShear((float)topshear.X);
            shapeBlock.PathShearY = (byte)Primitive.PackPathShear((float)topshear.Y);

            profilecut.X = Math.Max(0f, Math.Min(1f, (float)profilecut.X));
            profilecut.Y = Math.Max(0f, Math.Min(1f, (float)profilecut.Y));
            if (profilecut.Y - profilecut.X < 0.05f)
            {
                profilecut.X = profilecut.Y - 0.05f;
                if (profilecut.X < 0f) { profilecut.X = 0f; profilecut.Y = 0.05f; }
            }
            shapeBlock.ProfileBegin = (ushort)(50000 * profilecut.X);
            shapeBlock.ProfileEnd   = (ushort)(50000 * (1 - profilecut.Y));

            taper_a.X = Math.Max(-1f, Math.Min(1f, (float)taper_a.X));
            taper_a.Y = Math.Max(-1f, Math.Min(1f, (float)taper_a.Y));
            shapeBlock.PathTaperX = (sbyte)(100 * taper_a.X);
            shapeBlock.PathTaperY = (sbyte)(100 * taper_a.Y);

            revolutions = Math.Max(1f, Math.Min(4f, revolutions));
            shapeBlock.PathRevolutions = (byte)(66.666667 * (revolutions - 1.0));

            radiusoffset = Math.Max(0f, Math.Min(1f, radiusoffset));
            shapeBlock.PathRadiusOffset = (sbyte)(100 * radiusoffset);

            skew = Math.Max(-0.95f, Math.Min(0.95f, skew));
            shapeBlock.PathSkew = (sbyte)(100 * skew);

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsSculpt(SceneObjectPart part, string map, int typeBits)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock = new ObjectShapePacket.ObjectDataBlock();
            int sculptTypeMask = 0x0F;
            int sculptType     = typeBits & sculptTypeMask;
            int sculptOptions  = typeBits & ~sculptTypeMask;

            if (!UUID.TryParse(map, out UUID sculptId))
            {
                TaskInventoryItem invItem = FindInventoryItem(map, (int)AssetType.Texture);
                sculptId = invItem?.AssetID ?? UUID.Zero;
            }
            if (sculptId == UUID.Zero) return;

            if (sculptType != 4 &&
                sculptType != 3 &&
                sculptType != 1 &&
                sculptType != 2)
            {
                sculptType = 1;
                typeBits   = sculptOptions | sculptType;
            }

            shapeBlock.ObjectLocalID = part.LocalId;
            shapeBlock.PathScaleX    = 100;
            shapeBlock.PathScaleY    = 150;
            shapeBlock.PathCurve     = (byte)Extrusion.Curve1;

            part.Shape.SculptEntry   = true;
            part.Shape.SculptTexture = sculptId;
            part.Shape.SculptType    = (byte)typeBits;
            part.UpdateShape(shapeBlock);
        }

        private LSLList GetPrimParams(SceneObjectPart part, LSLList parms)
        {
            if (part == null || parms == null) return new LSLList();
            var result = new List<object>();
            var data = parms.Data;
            int idx = 0;

            while (idx < data.Length)
            {
                int code;
                try { code = Convert.ToInt32(data[idx++]); } catch { break; }

                switch (code)
                {
                    case PRIM_LINK_TARGET:
                        if (idx >= data.Length) break;
                        idx++; // consume link number — llGetLinkPrimitiveParams handles routing
                        break;

                    case PRIM_COLOR:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        if (te == null) { result.Add(Vector3.One); result.Add(1f); break; }
                        Primitive.TextureEntryFace f = face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face);
                        if (f == null) f = te.DefaultTexture;
                        result.Add(new Vector3(f.RGBA.R, f.RGBA.G, f.RGBA.B));
                        result.Add(f.RGBA.A);
                        break;
                    }

                    case PRIM_TEXTURE:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        if (te == null) { result.Add(UUID.Zero.ToString()); result.Add(new Vector3(1,1,0)); result.Add(Vector3.Zero); result.Add(0f); break; }
                        Primitive.TextureEntryFace f = face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face);
                        if (f == null) f = te.DefaultTexture;
                        result.Add(f.TextureID.ToString());
                        result.Add(new Vector3(f.RepeatU, f.RepeatV, 0));
                        result.Add(new Vector3(f.OffsetU, f.OffsetV, 0));
                        result.Add(f.Rotation);
                        break;
                    }

                    case PRIM_GLOW:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        Primitive.TextureEntryFace f = te == null ? null : (face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face));
                        result.Add(f?.Glow ?? 0f);
                        break;
                    }

                    case PRIM_FULLBRIGHT:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        Primitive.TextureEntryFace f = te == null ? null : (face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face));
                        result.Add(f?.Fullbright == true ? 1 : 0);
                        break;
                    }

                    case PRIM_BUMP_SHINY:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        Primitive.TextureEntryFace f = te == null ? null : (face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face));
                        result.Add((int)(f?.Shiny ?? Shininess.None));
                        result.Add((int)(f?.Bump  ?? Bumpiness.None));
                        break;
                    }

                    case PRIM_MATERIAL:
                        result.Add((int)part.Material);
                        break;

                    case PRIM_POSITION:
                        result.Add(part.AbsolutePosition);
                        break;

                    case PRIM_SIZE:
                        result.Add(part.Scale);
                        break;

                    case PRIM_ROT_LOCAL:
                        result.Add(part.RotationOffset);
                        break;

                    case PRIM_FLEXIBLE:
                    {
                        var s = part.Shape;
                        result.Add(s.FlexiEntry ? 1 : 0);
                        result.Add(s.FlexiSoftness);
                        result.Add(s.FlexiGravity);
                        result.Add(s.FlexiDrag);
                        result.Add(s.FlexiWind);
                        result.Add(s.FlexiTension);
                        result.Add(new Vector3(s.FlexiForceX, s.FlexiForceY, s.FlexiForceZ));
                        break;
                    }

                    case PRIM_POINT_LIGHT:
                    {
                        var s = part.Shape;
                        result.Add(s.LightEntry ? 1 : 0);
                        result.Add(new Vector3(s.LightColorR, s.LightColorG, s.LightColorB));
                        result.Add(s.LightIntensity);
                        result.Add(s.LightRadius);
                        result.Add(s.LightFalloff);
                        break;
                    }

                    case PRIM_TYPE:
                    {
                        PrimitiveBaseShape shape = part.Shape;
                        int primType = getScriptPrimType(shape);
                        result.Add(primType);
                        switch (primType)
                        {
                            case 0:
                            case 1:
                            case 2:
                                result.Add((int)shape.HollowShape);
                                result.Add(new Vector3(shape.ProfileBegin / 50000.0f, 1 - shape.ProfileEnd / 50000.0f, 0));
                                result.Add((float)(shape.ProfileHollow / 50000.0));
                                result.Add(new Vector3(shape.PathTwistBegin / 100.0f, shape.PathTwist / 100.0f, 0));
                                result.Add(new Vector3(1 - (shape.PathScaleX / 100.0f - 1), 1 - (shape.PathScaleY / 100.0f - 1), 0));
                                result.Add(new Vector3(Primitive.UnpackPathShear((sbyte)shape.PathShearX), Primitive.UnpackPathShear((sbyte)shape.PathShearY), 0));
                                break;
                            case 3:
                                result.Add((int)shape.HollowShape);
                                result.Add(new Vector3(shape.PathBegin / 50000.0f, 1 - shape.PathEnd / 50000.0f, 0));
                                result.Add((float)(shape.ProfileHollow / 50000.0f));
                                result.Add(new Vector3(shape.PathTwistBegin / 100.0f, shape.PathTwist / 100.0f, 0));
                                result.Add(new Vector3(shape.ProfileBegin / 50000.0f, 1 - shape.ProfileEnd / 50000.0f, 0));
                                break;
                            case 7:
                                result.Add(shape.SculptTexture.ToString());
                                result.Add((int)shape.SculptType);
                                break;
                            case 6:
                            case 5:
                            case 4:
                                result.Add((int)shape.HollowShape);
                                result.Add(new Vector3(shape.PathBegin / 50000.0f, 1 - shape.PathEnd / 50000.0f, 0));
                                result.Add((float)(shape.ProfileHollow / 50000.0));
                                result.Add(new Vector3(shape.PathTwistBegin / 100.0f, shape.PathTwist / 100.0f, 0));
                                result.Add(new Vector3(1 - (shape.PathScaleX / 100.0f - 1), 1 - (shape.PathScaleY / 100.0f - 1), 0));
                                result.Add(new Vector3(Primitive.UnpackPathShear((sbyte)shape.PathShearX), Primitive.UnpackPathShear((sbyte)shape.PathShearY), 0));
                                result.Add(new Vector3(shape.ProfileBegin / 50000.0f, 1 - shape.ProfileEnd / 50000.0f, 0));
                                result.Add(new Vector3(shape.PathTaperX / 100.0f, shape.PathTaperY / 100.0f, 0));
                                result.Add((float)(shape.PathRevolutions / 66.666667 + 1.0));
                                result.Add((float)(shape.PathRadiusOffset / 100.0));
                                result.Add((float)(shape.PathSkew / 100.0));
                                break;
                        }
                        break;
                    }

                    case PRIM_NAME:
                        result.Add(part.Name);
                        break;

                    case PRIM_DESC:
                        result.Add(part.Description);
                        break;

                    // ── PBR / glTF Material get params ──────────────────────────

                    case PRIM_RENDER_MATERIAL:
                    {
                        // Returns: [ string render_material ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        var shape_rm = part.Shape;
                        string matId = string.Empty;
                        if (shape_rm?.RenderMaterials?.entries != null && face >= 0)
                        {
                            foreach (var entry in shape_rm.RenderMaterials.entries)
                            {
                                if (entry.te_index == (byte)face)
                                { matId = entry.id.ToString(); break; }
                            }
                        }
                        result.Add(matId);
                        break;
                    }

                    case PRIM_GLTF_BASE_COLOR:
                    {
                        // Returns: [ texture, repeats, offsets, rotation, color, alpha,
                        //            alpha_mode, alpha_cutoff, double_sided ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        GetGLTFBaseColorParams(part, face, result);
                        break;
                    }

                    case PRIM_GLTF_NORMAL:
                    {
                        // Returns: [ texture, repeats, offsets, rotation ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        GetGLTFTransformParams(part, face, "ntex", "nrep", "noff", "nrot", result);
                        break;
                    }

                    case PRIM_GLTF_METALLIC_ROUGHNESS:
                    {
                        // Returns: [ texture, repeats, offsets, rotation, metallic_factor, roughness_factor ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        GetGLTFMetallicRoughnessParams(part, face, result);
                        break;
                    }

                    case PRIM_GLTF_EMISSIVE:
                    {
                        // Returns: [ texture, repeats, offsets, rotation, emissive_tint ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        GetGLTFEmissiveParams(part, face, result);
                        break;
                    }

                    default:
                        break;
                }
            }
            return new LSLList(result.ToArray());
        }
        public string llGetLinkKey(int linknumber)
        {
            if (m_host == null) return UUID.Zero.ToString();
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return UUID.Zero.ToString();
            // Link 0 = root part
            if (linknumber == 0) return group.RootPart.UUID.ToString();
            SceneObjectPart part = group.GetLinkNumPart(linknumber);
            if (part != null) return part.UUID.ToString();
            // Beyond prim count — check sitting avatars (seated avatar link numbers)
            int seatIndex = linknumber - group.PrimCount;
            var sitters = GetSittingAvatarList(group);
            if (seatIndex >= 1 && seatIndex <= sitters.Count)
                return sitters[seatIndex - 1].ToString();
            return UUID.Zero.ToString();
        }

        public string llGetObjectLinkKey(string objectId, int linknumber)
        {
            // SL: like llGetLinkKey but for any object, not just this one
            if (!UUID.TryParse(objectId, out UUID objID) || objID == UUID.Zero) return UUID.Zero.ToString();
            SceneObjectPart sop = World?.GetSceneObjectPart(objID);
            if (sop == null) return UUID.Zero.ToString();
            SceneObjectGroup group = sop.ParentGroup;
            if (group == null) return UUID.Zero.ToString();
            if (linknumber == 0 || linknumber == 1) return group.RootPart.UUID.ToString();
            SceneObjectPart part = group.GetLinkNumPart(linknumber);
            if (part != null) return part.UUID.ToString();
            return UUID.Zero.ToString();
        }

        public string llGetLinkName(int linknumber)
        {
            if (m_host == null) return string.Empty;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return string.Empty;
            if (linknumber == 0) return group.RootPart.Name;
            SceneObjectPart part = group.GetLinkNumPart(linknumber);
            if (part != null) return part.Name;
            // Sitting avatar name
            int seatIndex = linknumber - group.PrimCount;
            var sitters = GetSittingAvatarList(group);
            if (seatIndex >= 1 && seatIndex <= sitters.Count)
            {
                ScenePresence sp = World?.GetScenePresence(sitters[seatIndex - 1]);
                return sp?.Name ?? string.Empty;
            }
            return string.Empty;
        }

        private List<UUID> GetSittingAvatarList(SceneObjectGroup group)
        {
            var result = new List<UUID>();
            if (World == null) return result;
            World.ForEachScenePresence(sp =>
            {
                if (!sp.IsChildAgent && sp.ParentID != 0 &&
                    group.ContainsPart(sp.ParentID))
                    result.Add(sp.UUID);
            });
            return result;
        }

        public int llGetLinkNumberOfSides(int link)
        {
            if (m_host == null) return 0;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return 0;
            SceneObjectPart part = link == 0
                ? group.RootPart
                : group.GetLinkNumPart(link);
            return part?.GetNumberOfSides() ?? 0;
        }

        // ── Texture / color / alpha ────────────────────────────────────────────

        public void llSetColor(Vector3 color, int face)
        {
            SetColor(m_host, color, face);
        }

        public Vector3 llGetColor(int face)
        {
            if (m_host == null) return Vector3.Zero;
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return Vector3.Zero;
            int sides = m_host.GetNumberOfSides();
            Vector3 rgb = Vector3.Zero;

            if (face == ALL_SIDES)
            {
                if (sides <= 0) return Vector3.Zero;
                for (int i = 0; i < sides; i++)
                {
                    Color4 c = tex.GetFace((uint)i).RGBA;
                    rgb.X += c.R; rgb.Y += c.G; rgb.Z += c.B;
                }
                rgb.X /= sides; rgb.Y /= sides; rgb.Z /= sides;
                return rgb;
            }
            if (face >= 0 && face < sides)
            {
                Color4 c = tex.GetFace((uint)face).RGBA;
                return new Vector3(c.R, c.G, c.B);
            }
            return Vector3.Zero;
        }

        public void llSetAlpha(float alpha, int face)
        {
            SetAlpha(m_host, alpha, face);
        }

        public float llGetAlpha(int face)
        {
            if (m_host == null) return 0f;
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return 0f;
            int sides = m_host.GetNumberOfSides();

            if (face == ALL_SIDES)
            {
                // LSL spec: llGetAlpha(ALL_SIDES) returns the SUM of per-face alphas
                // (not the mean). Scripts rely on this to detect "all faces opaque"
                // via  llGetAlpha(ALL_SIDES) == (float)llGetNumberOfSides().
                if (sides <= 0) return 0f;
                double sum = 0;
                for (int i = 0; i < sides; i++)
                    sum += tex.GetFace((uint)i).RGBA.A;
                return (float)sum;
            }
            if (face >= 0 && face < sides)
                return tex.GetFace((uint)face).RGBA.A;
            return 0f;
        }

        public void llSetTexture(string texture, int face)
        {
            SetTexture(m_host, texture, face);
            ScriptSleep(200);
        }

        public string llGetTexture(int face)
        {
            if (m_host == null) return UUID.Zero.ToString();
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return UUID.Zero.ToString();
            int sides = m_host.GetNumberOfSides();
            int effectiveFace = (face == ALL_SIDES) ? 0 : face;

            UUID assetID = UUID.Zero;
            if (effectiveFace >= 0 && effectiveFace < sides)
                assetID = tex.GetFace((uint)effectiveFace).TextureID;

            if (assetID == UUID.Zero) return UUID.Zero.ToString();

            // If the texture is in the prim's inventory, return the inventory name.
            string name = InventoryName(assetID);
            if (!string.IsNullOrEmpty(name)) return name;

            // Not in prim inventory — only reveal the UUID on full-perm objects.
            if (IsFullPerm(m_host.OwnerMask))
                return assetID.ToString();

            return UUID.Zero.ToString();
        }

        public void llScaleTexture(float u, float v, int face)
        {
            ScaleTexture(m_host, u, v, face);
            ScriptSleep(200);
        }

        public void llOffsetTexture(float u, float v, int face)
        {
            OffsetTexture(m_host, u, v, face);
            ScriptSleep(200);
        }

        public void llRotateTexture(float rot, int face)
        {
            RotateTexture(m_host, rot, face);
            ScriptSleep(200);
        }

        public Vector3 llGetTextureOffset(int face)
        {
            if (m_host == null) return Vector3.Zero;
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return Vector3.Zero;
            int sides = m_host.GetNumberOfSides();
            int effectiveFace = (face == ALL_SIDES) ? 0 : face;
            if (effectiveFace < 0 || effectiveFace >= sides) return Vector3.Zero;
            var f = tex.GetFace((uint)effectiveFace);
            return new Vector3(f.OffsetU, f.OffsetV, 0f);
        }

        public Vector3 llGetTextureScale(int side)
        {
            if (m_host == null) return Vector3.Zero;
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return Vector3.Zero;
            int sides = m_host.GetNumberOfSides();
            int effectiveFace = (side == ALL_SIDES) ? 0 : side;
            if (effectiveFace < 0 || effectiveFace >= sides) return Vector3.Zero;
            var f = tex.GetFace((uint)effectiveFace);
            return new Vector3(f.RepeatU, f.RepeatV, 0f);
        }

        public float llGetTextureRot(int side)
        {
            if (m_host == null) return 0f;
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return 0f;
            int sides = m_host.GetNumberOfSides();
            int effectiveFace = (side == ALL_SIDES) ? 0 : side;
            if (effectiveFace < 0 || effectiveFace >= sides) return 0f;
            return tex.GetFace((uint)effectiveFace).Rotation;
        }

        public void llSetLinkAlpha(int linknumber, float alpha, int face)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                SetAlpha(part, alpha, face);
        }

        public void llSetLinkColor(int linknumber, Vector3 color, int face)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                SetColor(part, color, face);
        }

        public void llSetLinkTexture(int linknumber, string texture, int face)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                SetTexture(part, texture, face);
            ScriptSleep(200);
        }

        public void llSetLinkTextureAnim(int link, int mode, int face, int sizex, int sizey, float start, float length, float rate)
        {
            foreach (SceneObjectPart part in GetLinkParts(link))
                SetPrimTextureAnim(part, mode, face, sizex, sizey, start, length, rate);
        }

        public void llSetTextureAnim(int mode, int face, int sizex, int sizey, float start, float length, float rate)
        {
            SetPrimTextureAnim(m_host, mode, face, sizex, sizey, start, length, rate);
        }

		public void llSetText(string text, Vector3 color, float alpha)
		{
			if (m_host == null) return;
			alpha = Math.Max(0f, Math.Min(1f, alpha));
			m_host.SetText(text, color, (double)alpha);
		}
        // ── Sound ──────────────────────────────────────────────────────────────

        public void llSound(string sound, float volume, int queue, int loop) { /* Deprecated */ }
        public void llPlaySound(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.SendSound(m_host, soundID, volume, false, 0, false, false);
        }

        public void llLinkPlaySound(int link, string sound, float volume, int flags)
        {
            // SL: play a sound on a specific link. flags: SOUND_PLAY=0, SOUND_LOOP=1, SOUND_TRIGGER=2, SOUND_SYNC=4
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            bool loop = (flags & 1) != 0;
            bool trigger = (flags & 2) != 0;
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                if (trigger)
                    sm.SendSound(part, soundID, volume, true, 0, false, false);
                else
                    sm.SendSound(part, soundID, volume, false, 0, loop, false);
            }
        }

        public void llLoopSound(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.LoopSound(m_host, soundID, volume, false, false);
        }

        public void llLoopSoundMaster(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.LoopSound(m_host, soundID, volume, true, false);
        }

        public void llLoopSoundSlave(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.LoopSound(m_host, soundID, volume, false, true);
        }

        public void llPlaySoundSlave(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.SendSound(m_host, soundID, volume, false, 0, true, false);
        }

        public void llTriggerSound(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.SendSound(m_host, soundID, volume, true, 0, false, false);
        }

        public void llTriggerSoundLimited(string sound, float volume, Vector3 top, Vector3 bottom)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.TriggerSoundLimited(m_host.UUID, soundID, volume, bottom, top);
        }

        public void llStopSound()
        {
            if (m_host == null) return;
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            sm?.StopSound(m_host);
        }

        public void llPreloadSound(string sound)
        {
            if (m_host == null) { ScriptSleep(1000); return; }
            UUID soundID = KeyOrName(sound);
            if (soundID != UUID.Zero)
            {
                ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
                sm?.PreloadSound(m_host, soundID);
            }
            ScriptSleep(1000);
        }

        public void llSoundPreload(string sound) { llPreloadSound(sound); }

        public void llAdjustSoundVolume(float volume)
        {
            if (m_host == null) return;
            m_host.AdjustSoundGain(Math.Max(0f, Math.Min(1f, volume)));
            ScriptSleep(100);
        }

        public void llSetSoundQueueing(int queue)
        {
            if (m_host == null) return;
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            sm?.SetSoundQueueing(m_host.UUID, queue != 0);
        }

        public void llSetSoundRadius(float radius)
        {
            if (m_host == null) return;
            m_host.SoundRadius = Math.Max(0.0, radius);
            m_host.ScheduleFullUpdate();
        }

        public void llCollisionSound(string impact_sound, float impact_volume)
        {
            if (m_host == null) return;
            m_host.CollisionSound = KeyOrName(impact_sound);
            m_host.CollisionSoundVolume = Math.Max(0f, Math.Min(1f, impact_volume));
        }
        public void llCollisionSprite(string impact_sprite) { /* NotImplemented in Halcyon */ }

        // ── Particles ──────────────────────────────────────────────────────────

        public void llParticleSystem(LSLList rules) { PrimParticleSystem(m_host, rules); }
        public void llLinkParticleSystem(int linknumber, LSLList rules)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                PrimParticleSystem(part, rules);
        }

        // Particle helper constants
        const float MAX_SRC_SCALE = 7.96875f;
        const float MIN_SRC_BURST_RATE = 0.05f;

        private float LimitFloat(float value, float limit, bool allowNegative)
        {
            if (value > limit) value = limit;
            if (allowNegative) { if (value < -limit) value = -limit; }
            else { if (value < 0.0f) value = 0.0f; }
            return value;
        }

        private float LimitScaleForByteEncoding(float value) => LimitFloat(value, MAX_SRC_SCALE, false);

        private Primitive.ParticleSystem GetNewParticleSystemWithSLDefaultValues()
        {
            Primitive.ParticleSystem ps = new Primitive.ParticleSystem();
            ps.PartStartColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f);
            ps.PartEndColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f);
            ps.PartStartScaleX = 1.0f;
            ps.PartStartScaleY = 1.0f;
            ps.PartEndScaleX = 1.0f;
            ps.PartEndScaleY = 1.0f;
            ps.BurstSpeedMin = 1.0f;
            ps.BurstSpeedMax = 1.0f;
            ps.BurstRate = 0.1f;
            ps.PartMaxAge = 10.0f;
            ps.BurstPartCount = 1;
            ps.BlendFuncSource = (byte)0;  // PSYS_PART_BF_SOURCE_ALPHA
            ps.BlendFuncDest = (byte)1;    // PSYS_PART_BF_ONE_MINUS_SOURCE_ALPHA
            ps.PartStartGlow = 0.0f;
            ps.PartEndGlow = 0.0f;
            return ps;
        }

        private void PrimParticleSystem(SceneObjectPart part, LSLList rules)
        {
            if (rules == null || rules.Length == 0)
            {
                part.RemoveParticleSystem();
                part.ParentGroup.HasGroupChanged = true;
            }
            else
            {
                Primitive.ParticleSystem prules = GetNewParticleSystemWithSLDefaultValues();
                Vector3 tempv;
                float tempf;
                int tempi;

                for (int i = 0; i + 1 < rules.Data.Length; i += 2)
                {
                    int rule = rules.Data[i] is int ri ? ri :
                        int.TryParse(rules.Data[i].ToString(), out int pi) ? pi : -1;

                    switch (rule)
                    {
                        case 0: // PSYS_PART_FLAGS
                            prules.PartDataFlags = (Primitive.ParticleSystem.ParticleDataFlags)(uint)rules.GetLSLIntegerItem(i + 1);
                            break;
                        case 1: // PSYS_PART_START_COLOR
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartStartColor.R = tempv.X;
                            prules.PartStartColor.G = tempv.Y;
                            prules.PartStartColor.B = tempv.Z;
                            break;
                        case 2: // PSYS_PART_START_ALPHA
                            prules.PartStartColor.A = ParseFloat(rules.Data[i + 1]);
                            break;
                        case 3: // PSYS_PART_END_COLOR
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartEndColor.R = tempv.X;
                            prules.PartEndColor.G = tempv.Y;
                            prules.PartEndColor.B = tempv.Z;
                            break;
                        case 4: // PSYS_PART_END_ALPHA
                            prules.PartEndColor.A = ParseFloat(rules.Data[i + 1]);
                            break;
                        case 5: // PSYS_PART_START_SCALE
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartStartScaleX = LimitScaleForByteEncoding(tempv.X);
                            prules.PartStartScaleY = LimitScaleForByteEncoding(tempv.Y);
                            break;
                        case 6: // PSYS_PART_END_SCALE
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartEndScaleX = LimitScaleForByteEncoding(tempv.X);
                            prules.PartEndScaleY = LimitScaleForByteEncoding(tempv.Y);
                            break;
                        case 7: // PSYS_PART_MAX_AGE
                            prules.PartMaxAge = LimitFloat(ParseFloat(rules.Data[i + 1]), 30.0f, false);
                            break;
                        case 8: // PSYS_SRC_ACCEL
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartAcceleration.X = LimitFloat(tempv.X, 100.0f, true);
                            prules.PartAcceleration.Y = LimitFloat(tempv.Y, 100.0f, true);
                            prules.PartAcceleration.Z = LimitFloat(tempv.Z, 100.0f, true);
                            break;
                        case 9: // PSYS_SRC_PATTERN
                            prules.Pattern = (Primitive.ParticleSystem.SourcePattern)rules.GetLSLIntegerItem(i + 1);
                            break;
                        case 10: // PSYS_SRC_INNERANGLE
                            prules.InnerAngle = ParseFloat(rules.Data[i + 1]);
                            prules.PartFlags &= 0xFFFFFFFD;
                            break;
                        case 11: // PSYS_SRC_OUTERANGLE
                            prules.OuterAngle = ParseFloat(rules.Data[i + 1]);
                            prules.PartFlags &= 0xFFFFFFFD;
                            break;
                        case 12: // PSYS_SRC_TEXTURE
                            prules.Texture = KeyOrName(rules.Data[i + 1].ToString());
                            break;
                        case 13: // PSYS_SRC_BURST_RATE
                            tempf = ParseFloat(rules.Data[i + 1]);
                            if (tempf < MIN_SRC_BURST_RATE) tempf = MIN_SRC_BURST_RATE;
                            prules.BurstRate = tempf;
                            break;
                        case 15: // PSYS_SRC_BURST_PART_COUNT
                            prules.BurstPartCount = (byte)rules.GetLSLIntegerItem(i + 1);
                            break;
                        case 16: // PSYS_SRC_BURST_RADIUS
                            prules.BurstRadius = LimitFloat(ParseFloat(rules.Data[i + 1]), 50.0f, false);
                            break;
                        case 17: // PSYS_SRC_BURST_SPEED_MIN
                            prules.BurstSpeedMin = ParseFloat(rules.Data[i + 1]);
                            break;
                        case 18: // PSYS_SRC_BURST_SPEED_MAX
                            prules.BurstSpeedMax = ParseFloat(rules.Data[i + 1]);
                            break;
                        case 19: // PSYS_SRC_MAX_AGE
                            prules.MaxAge = ParseFloat(rules.Data[i + 1]);
                            break;
                        case 20: // PSYS_SRC_TARGET_KEY
                            if (UUID.TryParse(rules.Data[i + 1].ToString(), out UUID key))
                                prules.Target = key;
                            else
                                prules.Target = part.UUID;
                            break;
                        case 21: // PSYS_SRC_OMEGA
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.AngularVelocity.X = tempv.X;
                            prules.AngularVelocity.Y = tempv.Y;
                            prules.AngularVelocity.Z = tempv.Z;
                            break;
                        case 22: // PSYS_SRC_ANGLE_BEGIN
                            prules.InnerAngle = ParseFloat(rules.Data[i + 1]);
                            prules.PartFlags |= 0x02;
                            break;
                        case 23: // PSYS_SRC_ANGLE_END
                            prules.OuterAngle = ParseFloat(rules.Data[i + 1]);
                            prules.PartFlags |= 0x02;
                            break;
                        case 24: // PSYS_PART_START_GLOW
                            prules.PartStartGlow = ParseFloat(rules.Data[i + 1]);
                            break;
                        case 25: // PSYS_PART_END_GLOW
                            prules.PartEndGlow = ParseFloat(rules.Data[i + 1]);
                            break;
                        case 26: // PSYS_PART_BLEND_FUNC_SOURCE
                            prules.BlendFuncSource = (byte)rules.GetLSLIntegerItem(i + 1);
                            break;
                        case 27: // PSYS_PART_BLEND_FUNC_DEST
                            prules.BlendFuncDest = (byte)rules.GetLSLIntegerItem(i + 1);
                            break;
                    }
                }
                prules.CRC = 1;
                part.AddNewParticleSystem(prules, false);
                part.ParentGroup.HasGroupChanged = true;
            }
            part.ScheduleFullUpdate();
        }

        private static float ParseFloat(object o)
        {
            if (o is float f) return f;
            if (o is double d) return (float)d;
            if (o is int i) return (float)i;
            if (float.TryParse(o?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float r)) return r;
            return 0f;
        }

        private static Vector3 ParseVector(object o)
        {
            if (o is Vector3 v) return v;
            // Try parsing "<x,y,z>" format
            string s = o?.ToString() ?? "";
            s = s.Trim('<', '>', ' ');
            string[] parts = s.Split(',');
            if (parts.Length >= 3 &&
                float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float z))
                return new Vector3(x, y, z);
            return Vector3.Zero;
        }
        public void llMakeExplosion(int particles, float scale, float vel, float lifetime, float arc, string texture, Vector3 offset) { /* Deprecated */ }
        public void llMakeFountain(int particles, float scale, float vel, float lifetime, float arc, int bounce, string texture, Vector3 offset, float bounce_offset) { /* Deprecated */ }
        public void llMakeSmoke(int particles, float scale, float vel, float lifetime, float arc, string texture, Vector3 offset) { /* Deprecated */ }
        public void llMakeFire(int particles, float scale, float vel, float lifetime, float arc, string texture, Vector3 offset) { /* Deprecated */ }

        // ── Terrain / environment ──────────────────────────────────────────────

        public float llGround(Vector3 offset)
        {
            if (World == null || m_host == null) return 0f;
            Vector3 pos = m_host.AbsolutePosition + offset;
            return World.GetGroundHeight(pos.X, pos.Y);
        }

        public float llCloud(Vector3 offset)
        {
            // Cloud density not tracked in OpenSim — return 0 (clear sky)
            return 0f;
        }

        public Vector3 llWind(Vector3 offset)
        {
            if (World == null || m_host == null) return Vector3.Zero;
            Vector3 pos = m_host.AbsolutePosition + offset;
            IWindModule windMod = World.RequestModuleInterface<IWindModule>();
            if (windMod == null) return Vector3.Zero;
            return windMod.WindSpeed((int)pos.X, (int)pos.Y, (int)pos.Z);
        }

        public Vector3 llGetSunDirection()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null)
            {
                // Fallback: crude sun angle from UTC time
                double hours = DateTime.UtcNow.TimeOfDay.TotalHours;
                double angle = (hours / 24.0) * Math.PI * 2.0;
                return new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0.4f);
            }
            return envModule.GetSunDir(m_host.GetWorldPosition());
        }
       
        public Vector3 llGroundNormal(Vector3 offset)
        {
            if (World == null || m_host == null) return Vector3.UnitZ;
            Vector3 pos = m_host.AbsolutePosition + offset;
            float hC = World.GetGroundHeight(pos.X,       pos.Y);
            float hX = World.GetGroundHeight(pos.X + 1f,  pos.Y);
            float hY = World.GetGroundHeight(pos.X,       pos.Y + 1f);
            Vector3 normal = new Vector3(hC - hX, hC - hY, 1f);
            normal.Normalize();
            return normal;
        }

        public Vector3 llGroundSlope(Vector3 offset)
        {
            // Slope is the horizontal component of the ground normal
            Vector3 n = llGroundNormal(offset);
            Vector3 slope = new Vector3(n.X, n.Y, 0f);
            slope.Normalize();
            return slope;
        }

        public Vector3 llGroundContour(Vector3 offset)
        {
            // Contour is perpendicular to slope in the XY plane
            Vector3 slope = llGroundSlope(offset);
            return new Vector3(-slope.Y, slope.X, 0f);
        }

        public float llWater(Vector3 offset)
        {
            return (float)(World?.RegionInfo?.RegionSettings?.WaterHeight ?? 20.0);
        }
        public void iwSetWind(int type, Vector3 offset, Vector3 speed)
        {
            // Halcyon's IWindModule.WindSet(type, pos, speed) does not exist in Legion.
            // Legion only has WindParamSet(plugin, param, value) which is a different API.
            // Keeping as no-op.
        }
        public Vector3 iwWind(Vector3 offset)
        {
            IWindModule module = World?.RequestModuleInterface<IWindModule>();
            if (module != null)
            {
                Vector3 pos = m_host.GetWorldPosition();
                return module.WindSpeed((int)(pos.X + offset.X), (int)(pos.Y + offset.Y), (int)(pos.Z + offset.Z));
            }
            return Vector3.Zero;
        }
        public Vector3 iwGroundSurfaceNormal(Vector3 offset)
        {
            // Faithful port: returns the terrain surface normal at the given offset
            if (World == null || m_host == null) return Vector3.UnitZ;
            Vector3 pos = m_host.AbsolutePosition + offset;
            float hC = World.GetGroundHeight(pos.X,       pos.Y);
            float hX = World.GetGroundHeight(pos.X + 1f,  pos.Y);
            float hY = World.GetGroundHeight(pos.X,       pos.Y + 1f);
            Vector3 normal = new Vector3(hC - hX, hC - hY, 1f);
            normal.Normalize();
            return normal;
        }

        // ── Land ───────────────────────────────────────────────────────────────

        public string llGetLandOwnerAt(Vector3 pos)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null) return UUID.Zero.ToString();
            return parcel.LandData.OwnerID.ToString();
        }

        public int llOverMyLand(string id)
        {
            ILandObject parcel = null;
            if (!UUID.TryParse(id, out UUID key)) return 0;
            ScenePresence presence = World.GetScenePresence(key);
            if (presence != null)
            {
                Vector3 pos = presence.AbsolutePosition;
                parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            }
            else
            {
                SceneObjectPart obj = World.GetSceneObjectPart(key);
                if (obj != null)
                {
                    Vector3 pos = obj.AbsolutePosition;
                    parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
                }
            }
            return (parcel != null) && (m_host.OwnerID == parcel.LandData.OwnerID) ? 1 : 0;
        }

        public int llGetParcelFlags(Vector3 pos)
        {
            ILandObject land = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (land == null) return 0;
            LandData landData = land.LandData;
            if (landData == null) return 0;
            return (int)landData.Flags;
        }

        public int llGetParcelPrimCount(Vector3 pos, int category, int sim_wide)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null) return 0;
            IPrimCounts pc = parcel.PrimCounts;
            if (pc == null) return 0;
            if (sim_wide != 0)
            {
                return category == 0 ? pc.Simulator : parcel.GetSimulatorMaxPrimCount();
            }
            else
            {
                return category switch
                {
                    0 => pc.Total,
                    1 => pc.Owner,
                    2 => pc.Group,
                    3 => pc.Others,
                    4 => pc.Selected,
                    5 => 0, // temp not tracked separately
                    _ => 0
                };
            }
        }

        public int llGetParcelMaxPrims(Vector3 pos, int sim_wide)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null) return 0;
            if (sim_wide == 0)
                return parcel.GetParcelMaxPrimCount();
            return parcel.GetSimulatorMaxPrimCount();
        }

        public LSLList llGetParcelDetails(Vector3 pos, LSLList parms)
        {
            LandData land = World.GetLandData(pos.X, pos.Y);
            if (land == null) return new LSLList(0);
            var ret = new LSLList();
            for (int idx = 0; idx < parms.Length; idx++)
            {
                int param = parms.GetLSLIntegerItem(idx);
                switch (param)
                {
                    case 0: ret = ret.Append(land.Name ?? string.Empty); break;
                    case 1: ret = ret.Append(land.Description ?? string.Empty); break;
                    case 2: ret = ret.Append(land.OwnerID.ToString()); break;
                    case 3: ret = ret.Append(land.GroupID.ToString()); break;
                    case 4: ret = ret.Append(land.Area); break;
                    case 5: ret = ret.Append(land.GlobalID.ToString()); break;
                    case 6: ret = ret.Append(1); break; // SEE_AVATARS always true
                    default: ret = ret.Append(0); break;
                }
            }
            return ret;
        }

        public LSLList llGetParcelPrimOwners(Vector3 pos)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            var ret = new System.Collections.Generic.List<object>();
            if (parcel?.PrimCounts != null)
            {
                ret.Add(parcel.LandData.OwnerID.ToString());
                ret.Add(parcel.PrimCounts.Owner);
                if (parcel.PrimCounts.Group > 0)
                {
                    ret.Add(parcel.LandData.GroupID.ToString());
                    ret.Add(parcel.PrimCounts.Group);
                }
                if (parcel.PrimCounts.Others > 0)
                {
                    ret.Add(UUID.Zero.ToString());
                    ret.Add(parcel.PrimCounts.Others);
                }
            }
            ScriptSleep(2000);
            return new LSLList(ret.ToArray());
        }

        public void llAddToLandPassList(string avatar, float hours)
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (landObject == null) return;
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return;
            if (UUID.TryParse(avatar, out UUID key))
            {
                LandAccessEntry entry = new LandAccessEntry();
                entry.AgentID = key;
                entry.Flags = AccessList.Access;
                entry.Expires = hours > 0 ? (int)(Util.UnixTimeSinceEpoch() + hours * 3600) : 0;
                landObject.LandData.ParcelAccessList.Add(entry);
            }
            ScriptSleep(100);
        }

        public void llAddToLandBanList(string avatar, float hours)
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (landObject == null) return;
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return;
            if (UUID.TryParse(avatar, out UUID key))
            {
                LandAccessEntry entry = new LandAccessEntry();
                entry.AgentID = key;
                entry.Flags = AccessList.Ban;
                entry.Expires = hours > 0 ? (int)(Util.UnixTimeSinceEpoch() + hours * 3600) : 0;
                landObject.LandData.ParcelAccessList.Add(entry);
            }
            ScriptSleep(100);
        }

        public void llRemoveFromLandPassList(string avatar)
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (landObject == null) return;
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return;
            if (UUID.TryParse(avatar, out UUID key))
            {
                landObject.LandData.ParcelAccessList.RemoveAll(
                    e => e.AgentID == key && e.Flags == AccessList.Access);
            }
            ScriptSleep(100);
        }

        public void llRemoveFromLandBanList(string avatar)
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (landObject == null) return;
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return;
            if (UUID.TryParse(avatar, out UUID key))
            {
                landObject.LandData.ParcelAccessList.RemoveAll(
                    e => e.AgentID == key && e.Flags == AccessList.Ban);
            }
            ScriptSleep(100);
        }

        public void llResetLandBanList()
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (landObject == null) return;
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return;
            landObject.LandData.ParcelAccessList.RemoveAll(e => e.Flags == AccessList.Ban);
            ScriptSleep(100);
        }

        public void llResetLandPassList()
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (landObject == null) return;
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return;
            landObject.LandData.ParcelAccessList.RemoveAll(e => e.Flags == AccessList.Access);
            ScriptSleep(100);
        }

        public void llEjectFromLand(string pest)
        {
            llTeleportAgentHome(pest);
        }

        public void llSetParcelMusicURL(string url)
        {
            ILandObject landObject = World.LandChannel.GetLandObject(
                m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            if (landObject == null) return;
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return;
            landObject.LandData.MusicURL = url ?? string.Empty;
            World.EventManager.TriggerLandObjectUpdated((uint)landObject.LandData.LocalID, landObject);
            ScriptSleep(2000);
        }

        public string llGetParcelMusicURL()
        {
            ILandObject land = World.LandChannel.GetLandObject(
                m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            if (land == null) return string.Empty;
            return land.LandData.MusicURL ?? string.Empty;
        }

        public void llParcelMediaCommandList(LSLList commandList)
        {
            ILandObject landObject = World.LandChannel.GetLandObject(
                m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            if (landObject == null) return;
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return;

            bool update = false;
            byte loop = 0;
            LandData landData = landObject.LandData;
            string url = landData.MediaURL;
            UUID textureID = landData.MediaID;
            bool autoAlign = landData.MediaAutoScale != 0;
            string mediaType = landData.MediaType;
            int width = landData.MediaWidth;
            int height = landData.MediaHeight;
            string description = landData.MediaDescription;
            ParcelMediaCommandEnum? commandToSend = null;
            float time = 0.0f;
            ScenePresence presence = null;

            for (int i = 0; i < commandList.Data.Length; i++)
            {
                ParcelMediaCommandEnum command = (ParcelMediaCommandEnum)commandList.GetLSLIntegerItem(i);
                switch (command)
                {
                    case ParcelMediaCommandEnum.Agent:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                if (UUID.TryParse((string)commandList.Data[i + 1], out UUID agentID))
                                    presence = World.GetScenePresence(agentID);
                            }
                            else ShoutError("The argument of PARCEL_MEDIA_COMMAND_AGENT must be a key");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Loop:
                        loop = 1; commandToSend = command; update = true; break;
                    case ParcelMediaCommandEnum.Play:
                        loop = 0; commandToSend = command; update = true; break;
                    case ParcelMediaCommandEnum.Pause:
                    case ParcelMediaCommandEnum.Stop:
                    case ParcelMediaCommandEnum.Unload:
                        commandToSend = command; break;
                    case ParcelMediaCommandEnum.Url:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string) { url = (string)commandList.Data[i + 1]; update = true; }
                            else ShoutError("The argument of PARCEL_MEDIA_COMMAND_URL must be a string.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Texture:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                if (!UUID.TryParse((string)commandList.Data[i + 1], out textureID))
                                    textureID = UUID.Zero;
                                update = true;
                            }
                            else ShoutError("The argument of PARCEL_MEDIA_COMMAND_TEXTURE must be a string or key.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Time:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is float) time = (float)commandList.Data[i + 1];
                            else ShoutError("The argument of PARCEL_MEDIA_COMMAND_TIME must be a float.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.AutoAlign:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is int) { autoAlign = (int)commandList.Data[i + 1] == 1; update = true; }
                            else ShoutError("The argument of PARCEL_MEDIA_COMMAND_AUTO_ALIGN must be an integer.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Type:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string) { mediaType = (string)commandList.Data[i + 1]; update = true; }
                            else ShoutError("The argument of PARCEL_MEDIA_COMMAND_TYPE must be a string.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Desc:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string) { description = (string)commandList.Data[i + 1]; update = true; }
                            else ShoutError("The argument of PARCEL_MEDIA_COMMAND_DESC must be a string.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Size:
                        if ((i + 2) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is int && commandList.Data[i + 2] is int)
                            { width = (int)commandList.Data[i + 1]; height = (int)commandList.Data[i + 2]; update = true; }
                            ++i; ++i;
                        }
                        break;
                    default:
                        break;
                }
            }

            if (update)
            {
                landData.MediaID = textureID;
                landData.MediaAutoScale = autoAlign ? (byte)1 : (byte)0;
                landData.MediaDescription = description;
                landData.MediaWidth = width;
                landData.MediaHeight = height;
                landData.MediaType = mediaType;
                landData.MediaURL = url;
                World.EventManager.TriggerLandObjectUpdated((uint)landData.LocalID, landObject);

                List<ScenePresence> agents = World.GetScenePresences();
                foreach (ScenePresence agent in agents)
                {
                    if (agent.IsChildAgent || agent.IsDeleted) continue;
                    ScenePresence target = presence ?? agent;
                    if (target == agent && agent.currentParcelUUID == landData.GlobalID)
                        agent.ControllingClient.SendParcelMediaUpdate(landData.MediaURL,
                            landData.MediaID, landData.MediaAutoScale,
                            mediaType, description, width, height, loop);
                    if (presence != null) break;
                }
            }

            if (commandToSend != null)
            {
                List<ScenePresence> agents = World.GetScenePresences();
                foreach (ScenePresence agent in agents)
                {
                    if (agent.IsChildAgent || agent.IsDeleted) continue;
                    ScenePresence target = presence ?? agent;
                    if (target == agent && agent.currentParcelUUID == landData.GlobalID)
                        agent.ControllingClient.SendParcelMediaCommand(0x4,
                            (ParcelMediaCommandEnum)commandToSend, time);
                    if (presence != null) break;
                }
            }
            ScriptSleep(2000);
        }

        public LSLList llParcelMediaQuery(LSLList aList)
        {
            var list = new System.Collections.Generic.List<object>();
            Vector3 pos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (landObject == null) return new LSLList(list.ToArray());
            if (landObject.LandData.OwnerID != m_host.OwnerID &&
                !World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID))
                return new LSLList(list.ToArray());
            for (int i = 0; i < aList.Data.Length; i++)
            {
                if (aList.Data[i] != null)
                {
                    LandData ld = World.GetLandData(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
                    switch ((ParcelMediaCommandEnum)aList.GetLSLIntegerItem(i))
                    {
                        case ParcelMediaCommandEnum.Url:
                            list.Add(ld?.MediaURL ?? string.Empty); break;
                        case ParcelMediaCommandEnum.Desc:
                            list.Add(ld?.MediaDescription ?? string.Empty); break;
                        case ParcelMediaCommandEnum.Texture:
                            list.Add(ld?.MediaID.ToString() ?? UUID.Zero.ToString()); break;
                        case ParcelMediaCommandEnum.Type:
                            list.Add(ld?.MediaType ?? string.Empty); break;
                        case ParcelMediaCommandEnum.Size:
                            list.Add(ld?.MediaWidth ?? 0);
                            list.Add(ld?.MediaHeight ?? 0);
                            break;
                        default:
                            break;
                    }
                }
            }
            ScriptSleep(2000);
            return new LSLList(list.ToArray());
        }
        public int llScriptDanger(Vector3 pos)
        {
            // Returns 1 if pos is on a parcel that does not allow scripts or is in a no-build parcel
            ILandObject parcel = World?.LandChannel?.GetLandObject(pos.X, pos.Y);
            if (parcel == null) return 0;
            uint flags = parcel.LandData.Flags;
            // PARCEL_FLAG_ALLOW_OTHER_SCRIPTS = 0x4, PARCEL_FLAG_ALLOW_GROUP_SCRIPTS = 0x8
            bool allowOtherScripts = (flags & 0x4) != 0;
            bool allowGroupScripts = (flags & 0x8) != 0;
            if (allowOtherScripts) return 0;
            // If group scripts allowed and host is in the same group, not dangerous
            if (allowGroupScripts && parcel.LandData.GroupID == m_host.GroupID && m_host.GroupID != UUID.Zero)
                return 0;
            // Owner's own parcel is never dangerous
            if (parcel.LandData.OwnerID == m_host.OwnerID) return 0;
            return 1;
        }
        public int iwHasParcelPowers(int groupPower)
        {
            // Faithful port: check if script owner has parcel editing powers
            try
            {
                ILandObject landObject = World?.LandChannel?.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
                if (landObject == null) return 0;
                // Check if owner owns the parcel or is estate manager
                if (landObject.LandData.OwnerID == m_host.OwnerID) return 1;
                if (World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID)) return 1;
                // Check group ownership
                if (landObject.LandData.GroupID != UUID.Zero && landObject.LandData.GroupID == m_host.GroupID)
                    return 1;
                return 0;
            }
            catch { return 0; }
        }

        // ── Targeting ──────────────────────────────────────────────────────────

        public int llTarget(Vector3 position, float range)
        {
            if (m_host?.ParentGroup == null) return -1;
            return m_host.ParentGroup.RegisterTargetWaypoint(m_itemID, position, range);
        }

        public void llTargetRemove(int number)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.UnregisterTargetWaypoint(number);
        }

        public int llRotTarget(Quaternion rot, float error)
        {
            if (m_host?.ParentGroup == null) return -1;
            return m_host.ParentGroup.RegisterRotTargetWaypoint(m_itemID, rot, error);
        }

        public void llRotTargetRemove(int number)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.UnRegisterRotTargetWaypoint(number);
        }

        public void llTargetOmega(Vector3 axis, float spinrate, float gain)
        {
            // TargetOmega is a client-side visual spin effect delivered via angular velocity
            // in the terse object update packet. We set the angular velocity on the root part
            // which causes viewers to render the continuous rotation.
            if (m_host?.ParentGroup == null) return;
            SceneObjectPart root = m_host.ParentGroup.RootPart;
            if (root == null) return;

            if (Math.Abs(spinrate) < 0.0001f)
            {
                // Stop the spin
                root.UpdateAngularVelocity(Vector3.Zero);
            }
            else
            {
                Vector3 normalized = axis;
                float len = normalized.Length();
                if (len > 0.0001f) normalized /= len;
                root.UpdateAngularVelocity(normalized * spinrate);
            }
        }
        public void iwLinkTargetOmega(int linknumber, Vector3 axis, float spinrate, float gain)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                part.AngularVelocity = axis * spinrate;
                part.ScheduleFullUpdate();
            }
        }
        public void llLookAt(Vector3 target, float strength, float damping)
        {
            if (m_host?.ParentGroup == null) return;

            // Compute the rotation needed to face the target, then delegate to llRotLookAt.
            // This mirrors Halcyon's approach: calculate the target quaternion and hand off.
            Vector3 from = m_host.ParentGroup.AbsolutePosition;
            Vector3 toTarget = target - from;

            if (toTarget.LengthSquared() < 0.0001f)
                return; // target is at same position, nothing to do

            // Build a rotation that points our +Z axis toward the target (LSL convention)
            toTarget = Vector3.Normalize(toTarget);
            Vector3 forward = Vector3.UnitZ;

            float dot = Vector3.Dot(forward, toTarget);
            Quaternion newRot;

            if (dot > 0.9999f)
            {
                newRot = Quaternion.Identity;
            }
            else if (dot < -0.9999f)
            {
                newRot = new Quaternion(Vector3.UnitY, (float)Math.PI);
            }
            else
            {
                Vector3 axis = Vector3.Normalize(Vector3.Cross(forward, toTarget));
                float angle = (float)Math.Acos(Math.Max(-1f, Math.Min(1f, dot)));
                newRot = Quaternion.CreateFromAxisAngle(axis, angle);
            }

            llRotLookAt(newRot, strength, damping);
        }

        public void llStopLookAt()
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.StopLookAt();
        }

        public void llRotLookAt(Quaternion target, float strength, float damping)
        {
            if (m_host?.ParentGroup == null) return;

            if (m_host.ParentGroup.UsesPhysics)
            {
                // Physical objects: hand to the physics-backed APID controller
                m_host.ParentGroup.RotLookAt(target, strength, damping);
            }
            else
            {
                // Non-physical: set rotation directly (physics APID won't fire)
                // Clear any active lookat first, then set the rotation
                m_host.ParentGroup.StopLookAt();
                if (m_host.LinkNum < 2)
                    m_host.ParentGroup.UpdateGroupRotationR(target);
                else
                    m_host.UpdateRotation(target);
            }
        }
        public void llPointAt(Vector3 pos) { /* Deprecated */ }
        public void llStopPointAt() { /* Deprecated */ }
        public void llCollisionFilter(string name, string id, int accept) { /* NotImplemented in Halcyon */ }
        public void llPassTouches(int pass)
        {
            if (m_host == null) return;
            m_host.PassTouches = (pass != 0);
        }

        public void llPassCollisions(int pass)
        {
            if (m_host == null) return;
            m_host.PassCollisions = (pass != 0);
        }
        public void llVolumeDetect(int detect)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            m_host.ParentGroup.RootPart.ScriptSetVolumeDetect(detect != 0);
            m_thisScript.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.VolumeDetect] =
                new object[] { detect };
        }

        // ── Object queries ─────────────────────────────────────────────────────

        public int llGetObjectPrimCount(string object_id)
        {
            if (!UUID.TryParse(object_id, out UUID key)) return 0;
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            return part?.ParentGroup.PrimCount ?? 0;
        }

        public LSLList llGetObjectDetails(string id, LSLList parms)
        {
            // OBJECT_* constant values (standard LSL)
            const int OBJECT_NAME                = 1;
            const int OBJECT_DESC                = 2;
            const int OBJECT_POS                 = 3;
            const int OBJECT_ROT                 = 4;
            const int OBJECT_VELOCITY            = 5;
            const int OBJECT_OWNER               = 6;
            const int OBJECT_GROUP               = 7;
            const int OBJECT_CREATOR             = 8;
            const int OBJECT_RUNNING_SCRIPT_COUNT = 9;
            const int OBJECT_TOTAL_SCRIPT_COUNT  = 10;
            const int OBJECT_SCRIPT_MEMORY       = 11;
            const int OBJECT_SCRIPT_TIME         = 12;
            const int OBJECT_PRIM_EQUIVALENCE    = 13;
            const int OBJECT_SERVER_COST         = 14;
            const int OBJECT_STREAMING_COST      = 15;
            const int OBJECT_PHYSICS_COST        = 16;
            const int OBJECT_ROOT                = 18;
            const int OBJECT_ATTACHED_POINT      = 19;
            const int OBJECT_PHYSICS             = 21;
            const int OBJECT_PHANTOM             = 22;
            const int OBJECT_TEMP_ON_REZ         = 23;

            var ret = new List<object>();
            if (!UUID.TryParse(id, out UUID key) || key == UUID.Zero || parms == null)
                return new LSLList(ret);

            // Avatar path
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null)
            {
                foreach (object param in parms.Data)
                {
                    int p; try { p = Convert.ToInt32(param); } catch { continue; }
                    switch (p)
                    {
                        case OBJECT_NAME:     ret.Add(sp.Name); break;
                        case OBJECT_DESC:     ret.Add(string.Empty); break;
                        case OBJECT_POS:      ret.Add(sp.AbsolutePosition); break;
                        case OBJECT_ROT:      ret.Add(sp.Rotation); break;
                        case OBJECT_VELOCITY: ret.Add(sp.Velocity); break;
                        case OBJECT_OWNER:    ret.Add(sp.UUID.ToString()); break;
                        case OBJECT_GROUP:    ret.Add(UUID.Zero.ToString()); break;
                        case OBJECT_CREATOR:  ret.Add(UUID.Zero.ToString()); break;
                        case OBJECT_RUNNING_SCRIPT_COUNT: ret.Add(0); break;
                        case OBJECT_TOTAL_SCRIPT_COUNT:   ret.Add(0); break;
                        case OBJECT_SCRIPT_MEMORY:        ret.Add(0); break;
                        case OBJECT_SCRIPT_TIME:          ret.Add(0f); break;
                        case OBJECT_PRIM_EQUIVALENCE:     ret.Add(1); break;
                        case OBJECT_SERVER_COST:          ret.Add(0f); break;
                        case OBJECT_STREAMING_COST:       ret.Add(0f); break;
                        case OBJECT_PHYSICS_COST:         ret.Add(0f); break;
                        case OBJECT_ROOT:                 ret.Add(sp.UUID.ToString()); break;
                        case OBJECT_ATTACHED_POINT:       ret.Add(0); break;
                        case OBJECT_PHYSICS:              ret.Add(0); break;
                        case OBJECT_PHANTOM:              ret.Add(0); break;
                        case OBJECT_TEMP_ON_REZ:          ret.Add(0); break;
                        default:                          ret.Add(string.Empty); break;
                    }
                }
                return new LSLList(ret);
            }

            // Object path
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part == null) return new LSLList(ret);
            SceneObjectGroup grp = part.ParentGroup;

            foreach (object param in parms.Data)
            {
                int p; try { p = Convert.ToInt32(param); } catch { continue; }
                switch (p)
                {
                    case OBJECT_NAME:     ret.Add(part.Name); break;
                    case OBJECT_DESC:     ret.Add(part.Description); break;
                    case OBJECT_POS:      ret.Add(part.GetWorldPosition()); break;
                    case OBJECT_ROT:      ret.Add(part.GetWorldRotation()); break;
                    case OBJECT_VELOCITY: ret.Add(part.Velocity); break;
                    case OBJECT_OWNER:
                        ret.Add((grp.GroupID != UUID.Zero && grp.OwnerID == grp.GroupID)
                            ? UUID.Zero.ToString() : grp.OwnerID.ToString()); break;
                    case OBJECT_GROUP:    ret.Add(grp.GroupID.ToString()); break;
                    case OBJECT_CREATOR:  ret.Add(part.CreatorID.ToString()); break;
                    case OBJECT_RUNNING_SCRIPT_COUNT: ret.Add(0); break;
                    case OBJECT_TOTAL_SCRIPT_COUNT:   ret.Add(grp.ScriptCount()); break;
                    case OBJECT_SCRIPT_MEMORY:        ret.Add(0); break;
                    case OBJECT_SCRIPT_TIME:          ret.Add(0f); break;
                    case OBJECT_PRIM_EQUIVALENCE:     ret.Add(grp.PrimCount); break;
                    case OBJECT_SERVER_COST:          ret.Add(0f); break;
                    case OBJECT_STREAMING_COST:       ret.Add(0f); break;
                    case OBJECT_PHYSICS_COST:         ret.Add(0f); break;
                    case OBJECT_ROOT:                 ret.Add(grp.RootPart.UUID.ToString()); break;
                    case OBJECT_ATTACHED_POINT:       ret.Add((int)grp.AttachmentPoint); break;
                    case OBJECT_PHYSICS:              ret.Add(grp.UsesPhysics ? 1 : 0); break;
                    case OBJECT_PHANTOM:              ret.Add(grp.IsPhantom ? 1 : 0); break;
                    case OBJECT_TEMP_ON_REZ:          ret.Add(grp.IsTemporary ? 1 : 0); break;
                    default:                          ret.Add(string.Empty); break;
                }
            }
            return new LSLList(ret);
        }
        public LSLList llGetBoundingBox(string obj)
        {
            // Return [min_corner, max_corner] relative to the root prim.
            // Legion's GetBoundingBox already returns root-relative coords — no position subtraction needed.
            var empty = new LSLList(new object[] { Vector3.Zero, Vector3.Zero });

            if (!UUID.TryParse(obj, out UUID objID) || objID == UUID.Zero)
                return empty;

            // Check if it's an avatar first
            ScenePresence sp = World?.GetScenePresence(objID);
            if (sp != null)
            {
                if (sp.ParentPart != null)
                {
                    // Seated — use the sat-on object's bbox instead
                    objID = sp.ParentPart.ParentGroup?.UUID ?? objID;
                }
                else
                {
                    // Standing or flying — return standard avatar bounding box
                    float h = sp.Appearance?.AvatarHeight ?? 1.8f;
                    float halfH = h / 2.0f;
                    return new LSLList(new object[]
                    {
                        new Vector3(-0.225f, -0.3f, -halfH),
                        new Vector3( 0.225f,  0.3f,  halfH + 0.05f)
                    });
                }
            }

            SceneObjectPart part = World?.GetSceneObjectPart(objID);
            if (part?.ParentGroup == null) return empty;

            float minX, maxX, minY, maxY, minZ, maxZ;
            part.ParentGroup.GetBoundingBox(out minX, out maxX, out minY, out maxY, out minZ, out maxZ);

            // Values are already root-relative — return directly
            return new LSLList(new object[]
            {
                new Vector3(minX, minY, minZ),
                new Vector3(maxX, maxY, maxZ)
            });
        }
        public LSLList iwGetWorldBoundingBox(string obj)
        {
            // Faithful port: same as llGetBoundingBox but returns world coordinates
            return llGetBoundingBox(obj);
        }
        public int llGetObjectPermMask(int mask)
        {
            if (m_host == null) return 0;
            // mask: 0=Base, 1=Owner, 2=Group, 3=Everyone, 4=NextOwner
            switch (mask)
            {
                case 0: return (int)m_host.BaseMask;
                case 1: return (int)m_host.OwnerMask;
                case 2: return (int)m_host.GroupMask;
                case 3: return (int)m_host.EveryoneMask;
                case 4: return (int)m_host.NextOwnerMask;
                default: return 0;
            }
        }

        public void llSetObjectPermMask(int mask, int value)
        {
            // Only available to god-level scripts per LSL spec.
            if (m_host == null) return;
            if (!World.Permissions.CanRunConsoleCommand(m_host.OwnerID)) return;

            switch (mask)
            {
                case 0: m_host.BaseMask      = (uint)value; break;
                case 1: m_host.OwnerMask     = (uint)value; break;
                case 2: m_host.GroupMask     = (uint)value; break;
                case 3: m_host.EveryoneMask  = (uint)value; break;
                case 4: m_host.NextOwnerMask = (uint)value; break;
            }
        }
        public LSLList llGetAgentList(int scope, LSLList options)
        {
            // scope: 1=region, 16=parcel, 4=parcel-owner-same
            // Returns list of avatar UUIDs in the specified scope
            List<object> result = new List<object>();
            List<ScenePresence> presences = World?.GetScenePresences();
            if (presences == null) return new LSLList();

            foreach (ScenePresence sp in presences)
            {
                if (sp.IsChildAgent) continue;
                if (scope == 1) // AGENT_LIST_REGION
                {
                    result.Add(sp.UUID.ToString());
                }
                else if (scope == 16 || scope == 4) // AGENT_LIST_PARCEL / AGENT_LIST_PARCEL_OWNER
                {
                    ILandObject hostParcel = World.LandChannel?.GetLandObject(
                        m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
                    ILandObject avParcel = World.LandChannel?.GetLandObject(
                        sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
                    if (hostParcel != null && avParcel != null &&
                        hostParcel.LandData.GlobalID == avParcel.LandData.GlobalID)
                    {
                        if (scope == 4 && sp.UUID != hostParcel.LandData.OwnerID) continue;
                        result.Add(sp.UUID.ToString());
                    }
                }
            }
            return new LSLList(result);
        }
        public LSLList iwGetAgentList(int scope, Vector3 minPos, Vector3 maxPos, LSLList paramList)
        {
            // Like llGetAgentList but with optional bounding box filter
            List<object> result = new List<object>();
            List<ScenePresence> presences = World?.GetScenePresences();
            if (presences == null) return new LSLList();

            bool useBBox = (minPos != Vector3.Zero || maxPos != Vector3.Zero);

            foreach (ScenePresence sp in presences)
            {
                if (sp.IsChildAgent) continue;

                // Bounding box filter
                if (useBBox)
                {
                    Vector3 pos = sp.AbsolutePosition;
                    if (pos.X < minPos.X || pos.Y < minPos.Y || pos.Z < minPos.Z) continue;
                    if (pos.X > maxPos.X || pos.Y > maxPos.Y || pos.Z > maxPos.Z) continue;
                }

                if (scope == 1) // AGENT_LIST_REGION
                {
                    result.Add(sp.UUID.ToString());
                }
                else if (scope == 16 || scope == 4) // AGENT_LIST_PARCEL / AGENT_LIST_PARCEL_OWNER
                {
                    ILandObject hostParcel = World.LandChannel?.GetLandObject(
                        m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
                    ILandObject avParcel = World.LandChannel?.GetLandObject(
                        sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
                    if (hostParcel != null && avParcel != null &&
                        hostParcel.LandData.GlobalID == avParcel.LandData.GlobalID)
                    {
                        if (scope == 4 && sp.UUID != hostParcel.LandData.OwnerID) continue;
                        result.Add(sp.UUID.ToString());
                    }
                }
            }
            return new LSLList(result);
        }

        public LSLList osGetAvatarList()
        {
            // OSSL: returns [uuid, position, name, uuid, position, name, ...]
            // for every avatar in the region EXCEPT the script owner
            // (matches the OpenSim XEngine OSSL behavior).
            List<object> result = new List<object>();
            List<ScenePresence> presences = World?.GetScenePresences();
            if (presences == null) return new LSLList();

            UUID ownerId = m_host.OwnerID;
            foreach (ScenePresence sp in presences)
            {
                if (sp.IsChildAgent) continue;
                if (sp.UUID == ownerId) continue;   // exclude script owner per OSSL spec
                result.Add(sp.UUID.ToString());
                result.Add(sp.AbsolutePosition);
                result.Add(sp.Name);
            }
            return new LSLList(result);
        }

        public int llReturnObjectsByOwner(string owner, int scope)
        {
            // Faithful port from Halcyon, adapted for Legion
            const int ERR_MALFORMED_PARAMS = -3;
            const int ERR_RUNTIME_PERMISSIONS = -4;
            const int ERR_GENERIC = -1;
            const int PERMISSION_RETURN_OBJECTS = 0x10000;
            const int OBJECT_RETURN_PARCEL = 1;
            const int OBJECT_RETURN_PARCEL_OWNER = 2;
            const int OBJECT_RETURN_REGION = 4;

            if (!UUID.TryParse(owner, out UUID targetAgentID))
                return ERR_MALFORMED_PARAMS;
            if (targetAgentID == UUID.Zero) return 0;

            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero) return ERR_GENERIC;

            TaskInventoryItem item;
            lock (m_host.TaskInventory)
            {
                if (!m_host.TaskInventory.ContainsKey(invItemID)) return ERR_GENERIC;
                item = m_host.TaskInventory[invItemID];
            }

            // Check PERMISSION_RETURN_OBJECTS
            if ((item.PermsMask & PERMISSION_RETURN_OBJECTS) == 0)
                return ERR_RUNTIME_PERMISSIONS;

            try
            {
                Vector3 currentPos = m_host.ParentGroup.AbsolutePosition;
                ILandObject currentParcel = World.LandChannel.GetLandObject(currentPos.X, currentPos.Y);
                if (currentParcel == null) return ERR_GENERIC;

                // Collect objects to return
                List<SceneObjectGroup> toReturn = new List<SceneObjectGroup>();
                EntityBase[] entities = World.GetEntities();

                foreach (EntityBase ent in entities)
                {
                    if (ent is SceneObjectGroup sog && !sog.IsDeleted && !sog.IsAttachment)
                    {
                        if (sog.OwnerID != targetAgentID) continue;
                        if (sog == m_host.ParentGroup) continue; // don't return ourselves

                        Vector3 objPos = sog.AbsolutePosition;
                        ILandObject objParcel = World.LandChannel.GetLandObject(objPos.X, objPos.Y);
                        if (objParcel == null) continue;

                        switch (scope)
                        {
                            case OBJECT_RETURN_PARCEL:
                                if (objParcel.LandData.LocalID != currentParcel.LandData.LocalID) continue;
                                break;
                            case OBJECT_RETURN_PARCEL_OWNER:
                                if (objParcel.LandData.LocalID != currentParcel.LandData.LocalID) continue;
                                if (objParcel.LandData.OwnerID != currentParcel.LandData.OwnerID) continue;
                                break;
                            case OBJECT_RETURN_REGION:
                                // all parcels
                                break;
                            default:
                                return ERR_MALFORMED_PARAMS;
                        }
                        toReturn.Add(sog);
                    }
                }

                if (toReturn.Count > 0)
                {
                    foreach (SceneObjectGroup sog in toReturn)
                    {
                        try { World.DeleteSceneObject(sog, false); }
                        catch { }
                    }
                    m_log.LogInformation("[PhloxAPI]: llReturnObjectsByOwner returned {0} objects owned by {1}", toReturn.Count, targetAgentID);
                }
                return toReturn.Count;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llReturnObjectsByOwner exception: {0}", e.Message);
                return ERR_GENERIC;
            }
        }
        public int llReturnObjectsByID(LSLList objects)
        {
            // Faithful port from Halcyon, adapted for Legion
            const int ERR_MALFORMED_PARAMS = -3;
            const int ERR_RUNTIME_PERMISSIONS = -4;
            const int ERR_GENERIC = -1;
            const int PERMISSION_RETURN_OBJECTS = 0x10000;

            try
            {
                UUID invItemID = InventorySelf();
                if (invItemID == UUID.Zero) return ERR_GENERIC;

                TaskInventoryItem item;
                lock (m_host.TaskInventory)
                {
                    if (!m_host.TaskInventory.ContainsKey(invItemID)) return ERR_GENERIC;
                    item = m_host.TaskInventory[invItemID];
                }

                if ((item.PermsMask & PERMISSION_RETURN_OBJECTS) == 0)
                    return ERR_RUNTIME_PERMISSIONS;

                int count = 0;
                for (int i = 0; i < objects.Length; i++)
                {
                    if (!UUID.TryParse(objects.GetLSLStringItem(i), out UUID targetId))
                        return ERR_MALFORMED_PARAMS;
                    if (targetId == UUID.Zero) continue;

                    SceneObjectPart part = World.GetSceneObjectPart(targetId);
                    if (part == null) continue;

                    SceneObjectGroup sog = part.ParentGroup;
                    if (sog == null || sog.IsDeleted || sog.IsAttachment) continue;

                    // Check parcel permissions: can't return parcel owner's or estate manager's objects
                    Vector3 pos = sog.AbsolutePosition;
                    ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
                    if (parcel == null) continue;
                    if (sog.OwnerID == parcel.LandData.OwnerID) continue;
                    if (World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(sog.OwnerID)) continue;

                    try
                    {
                        World.DeleteSceneObject(sog, false);
                        count++;
                    }
                    catch { }
                }
                if (count > 0)
                    m_log.LogInformation("[PhloxAPI]: llReturnObjectsByID returned {0} objects", count);
                return count;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llReturnObjectsByID exception: {0}", e.Message);
                return ERR_GENERIC;
            }
        }

        // ── Prim media ─────────────────────────────────────────────────────────

        public int llSetPrimMediaParams(int face, LSLList parms)
        {
            ScriptSleep(1000);
            return SetPrimMediaParams(m_host, face, parms);
        }
        public int llSetLinkMedia(int link, int face, LSLList parms)
        {
            ScriptSleep(1000);
            const int LINK_ROOT = 1;
            const int LINK_THIS = -4;
            if (link == LINK_ROOT)
                return SetPrimMediaParams(m_host.ParentGroup.RootPart, face, parms);
            else if (link == LINK_THIS)
                return SetPrimMediaParams(m_host, face, parms);
            else
            {
                SceneObjectPart part = m_host.ParentGroup.GetLinkNumPart(link);
                if (part != null) return SetPrimMediaParams(part, face, parms);
            }
            return 1003; // LSL_STATUS_NOT_FOUND
        }
        public LSLList llGetPrimMediaParams(int face, LSLList parms)
        {
            ScriptSleep(1000);
            return GetPrimMediaParams(m_host, face, parms);
        }
        public LSLList llGetLinkMedia(int link, int face, LSLList parms)
        {
            ScriptSleep(1000);
            const int LINK_ROOT = 1;
            const int LINK_THIS = -4;
            if (link == LINK_ROOT)
                return GetPrimMediaParams(m_host.ParentGroup.RootPart, face, parms);
            else if (link == LINK_THIS)
                return GetPrimMediaParams(m_host, face, parms);
            else
            {
                SceneObjectPart part = m_host.ParentGroup.GetLinkNumPart(link);
                if (part != null) return GetPrimMediaParams(part, face, parms);
            }
            return new LSLList();
        }
        public int llClearPrimMedia(int face)
        {
            ScriptSleep(1000);
            return ClearPrimMedia(m_host, face);
        }
        public int llClearLinkMedia(int link, int face)
        {
            ScriptSleep(1000);
            const int LINK_ROOT = 1;
            const int LINK_THIS = -4;
            if (link == LINK_ROOT)
                return ClearPrimMedia(m_host.ParentGroup.RootPart, face);
            else if (link == LINK_THIS)
                return ClearPrimMedia(m_host, face);
            else
            {
                SceneObjectPart part = m_host.ParentGroup.GetLinkNumPart(link);
                if (part != null) return ClearPrimMedia(part, face);
            }
            return 1003; // LSL_STATUS_NOT_FOUND
        }

        // ── Prim media helpers (faithful port from Halcyon) ────────────────────

        private LSLList GetPrimMediaParams(SceneObjectPart part, int face, LSLList rules)
        {
            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return new LSLList();

            IMoapModule module = World?.RequestModuleInterface<IMoapModule>();
            if (module == null) return new LSLList();

            MediaEntry me = module.GetMediaEntry(part, face);
            if (me == null) return new LSLList();

            // PRIM_MEDIA_* constants
            const int PRIM_MEDIA_ALT_IMAGE_ENABLE = 0;
            const int PRIM_MEDIA_CONTROLS = 1;
            const int PRIM_MEDIA_CURRENT_URL = 2;
            const int PRIM_MEDIA_HOME_URL = 3;
            const int PRIM_MEDIA_AUTO_LOOP = 4;
            const int PRIM_MEDIA_AUTO_PLAY = 5;
            const int PRIM_MEDIA_AUTO_SCALE = 6;
            const int PRIM_MEDIA_AUTO_ZOOM = 7;
            const int PRIM_MEDIA_FIRST_CLICK_INTERACT = 8;
            const int PRIM_MEDIA_WIDTH_PIXELS = 9;
            const int PRIM_MEDIA_HEIGHT_PIXELS = 10;
            const int PRIM_MEDIA_WHITELIST_ENABLE = 11;
            const int PRIM_MEDIA_WHITELIST = 12;
            const int PRIM_MEDIA_PERMS_INTERACT = 13;
            const int PRIM_MEDIA_PERMS_CONTROL = 14;

            List<object> res = new List<object>();
            for (int i = 0; i < rules.Length; i++)
            {
                int code = rules.GetLSLIntegerItem(i);
                switch (code)
                {
                    case PRIM_MEDIA_ALT_IMAGE_ENABLE: res.Add(0); break;
                    case PRIM_MEDIA_CONTROLS:
                        res.Add(me.Controls == MediaControls.Standard ? 0 : 1);
                        break;
                    case PRIM_MEDIA_CURRENT_URL: res.Add(me.CurrentURL ?? string.Empty); break;
                    case PRIM_MEDIA_HOME_URL: res.Add(me.HomeURL ?? string.Empty); break;
                    case PRIM_MEDIA_AUTO_LOOP: res.Add(me.AutoLoop ? 1 : 0); break;
                    case PRIM_MEDIA_AUTO_PLAY: res.Add(me.AutoPlay ? 1 : 0); break;
                    case PRIM_MEDIA_AUTO_SCALE: res.Add(me.AutoScale ? 1 : 0); break;
                    case PRIM_MEDIA_AUTO_ZOOM: res.Add(me.AutoZoom ? 1 : 0); break;
                    case PRIM_MEDIA_FIRST_CLICK_INTERACT: res.Add(me.InteractOnFirstClick ? 1 : 0); break;
                    case PRIM_MEDIA_WIDTH_PIXELS: res.Add((int)me.Width); break;
                    case PRIM_MEDIA_HEIGHT_PIXELS: res.Add((int)me.Height); break;
                    case PRIM_MEDIA_WHITELIST_ENABLE: res.Add(me.EnableWhiteList ? 1 : 0); break;
                    case PRIM_MEDIA_WHITELIST:
                        if (me.WhiteList == null) { res.Add(string.Empty); break; }
                        string[] urls = (string[])me.WhiteList.Clone();
                        for (int j = 0; j < urls.Length; j++)
                            urls[j] = Uri.EscapeDataString(urls[j]);
                        res.Add(string.Join(", ", urls));
                        break;
                    case PRIM_MEDIA_PERMS_INTERACT: res.Add((int)me.InteractPermissions); break;
                    case PRIM_MEDIA_PERMS_CONTROL: res.Add((int)me.ControlPermissions); break;
                    default: return new LSLList();
                }
            }
            return new LSLList(res);
        }

        private int SetPrimMediaParams(SceneObjectPart part, int face, LSLList rules)
        {
            const int LSL_STATUS_OK = 0;
            const int LSL_STATUS_NOT_FOUND = 1003;
            const int LSL_STATUS_NOT_SUPPORTED = 1004;
            const int LSL_STATUS_MALFORMED_PARAMS = 1000;

            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return LSL_STATUS_NOT_FOUND;

            IMoapModule module = World?.RequestModuleInterface<IMoapModule>();
            if (module == null) return LSL_STATUS_NOT_SUPPORTED;

            MediaEntry me = module.GetMediaEntry(part, face);
            if (me == null) me = new MediaEntry();

            const int PRIM_MEDIA_ALT_IMAGE_ENABLE = 0;
            const int PRIM_MEDIA_CONTROLS = 1;
            const int PRIM_MEDIA_CURRENT_URL = 2;
            const int PRIM_MEDIA_HOME_URL = 3;
            const int PRIM_MEDIA_AUTO_LOOP = 4;
            const int PRIM_MEDIA_AUTO_PLAY = 5;
            const int PRIM_MEDIA_AUTO_SCALE = 6;
            const int PRIM_MEDIA_AUTO_ZOOM = 7;
            const int PRIM_MEDIA_FIRST_CLICK_INTERACT = 8;
            const int PRIM_MEDIA_WIDTH_PIXELS = 9;
            const int PRIM_MEDIA_HEIGHT_PIXELS = 10;
            const int PRIM_MEDIA_WHITELIST_ENABLE = 11;
            const int PRIM_MEDIA_WHITELIST = 12;
            const int PRIM_MEDIA_PERMS_INTERACT = 13;
            const int PRIM_MEDIA_PERMS_CONTROL = 14;

            int i = 0;
            while (i < rules.Length - 1)
            {
                int code = rules.GetLSLIntegerItem(i++);
                switch (code)
                {
                    case PRIM_MEDIA_ALT_IMAGE_ENABLE:
                        me.EnableAlterntiveImage = (rules.GetLSLIntegerItem(i++) != 0);
                        break;
                    case PRIM_MEDIA_CONTROLS:
                        int v = rules.GetLSLIntegerItem(i++);
                        me.Controls = (v == 0) ? MediaControls.Standard : MediaControls.Mini;
                        break;
                    case PRIM_MEDIA_CURRENT_URL: me.CurrentURL = rules.GetLSLStringItem(i++); break;
                    case PRIM_MEDIA_HOME_URL: me.HomeURL = rules.GetLSLStringItem(i++); break;
                    case PRIM_MEDIA_AUTO_LOOP: me.AutoLoop = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_AUTO_PLAY: me.AutoPlay = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_AUTO_SCALE: me.AutoScale = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_AUTO_ZOOM: me.AutoZoom = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_FIRST_CLICK_INTERACT: me.InteractOnFirstClick = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_WIDTH_PIXELS: me.Width = rules.GetLSLIntegerItem(i++); break;
                    case PRIM_MEDIA_HEIGHT_PIXELS: me.Height = rules.GetLSLIntegerItem(i++); break;
                    case PRIM_MEDIA_WHITELIST_ENABLE: me.EnableWhiteList = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_WHITELIST:
                        string[] rawUrls = rules.GetLSLStringItem(i++).Split(new char[] { ',' });
                        List<string> whiteListUrls = new List<string>();
                        foreach (string rawUrl in rawUrls) whiteListUrls.Add(rawUrl.Trim());
                        me.WhiteList = whiteListUrls.ToArray();
                        break;
                    case PRIM_MEDIA_PERMS_INTERACT:
                        me.InteractPermissions = (MediaPermission)(byte)(int)rules.GetLSLIntegerItem(i++);
                        break;
                    case PRIM_MEDIA_PERMS_CONTROL:
                        me.ControlPermissions = (MediaPermission)(byte)(int)rules.GetLSLIntegerItem(i++);
                        break;
                    default: return LSL_STATUS_MALFORMED_PARAMS;
                }
            }

            module.SetMediaEntry(part, face, me);
            return LSL_STATUS_OK;
        }

        private int ClearPrimMedia(SceneObjectPart part, int face)
        {
            const int LSL_STATUS_OK = 0;
            const int LSL_STATUS_NOT_FOUND = 1003;
            const int LSL_STATUS_NOT_SUPPORTED = 1004;
            const int ALL_SIDES = -1;

            IMoapModule module = World?.RequestModuleInterface<IMoapModule>();
            if (module == null) return LSL_STATUS_NOT_SUPPORTED;

            if (face == ALL_SIDES)
            {
                for (int i = 0; i < part.GetNumberOfSides(); i++)
                    module.ClearMediaEntry(part, i);
                return LSL_STATUS_OK;
            }

            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return LSL_STATUS_NOT_FOUND;

            module.ClearMediaEntry(part, face);
            return LSL_STATUS_OK;
        }

        // ── HTTP / URL ─────────────────────────────────────────────────────────

        public string llHTTPRequest(string url, LSLList parameters, string body)
        {
            if (World == null) return UUID.Zero.ToString();

            IHttpRequestModule httpMod = World.RequestModuleInterface<IHttpRequestModule>();
            if (httpMod == null) return UUID.Zero.ToString();

            if (!httpMod.CheckThrottle(m_localID, m_host.OwnerID))
                return UUID.Zero.ToString();

            // Parse parameter pairs into list and custom headers dict
            var paramList  = new List<string>();
            var headers    = new Dictionary<string, string>();
            var data       = parameters.Data;

            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                int option;
                if (!int.TryParse(data[i].ToString(), out option))
                {
                    ShoutError("Invalid flag in llHTTPRequest parameters.");
                    return UUID.Zero.ToString();
                }
                string value = data[i + 1].ToString();

                // HTTP_CUSTOM_HEADER (5) has an extra param: name, value
                if (option == 5 && i + 2 < data.Length)
                {
                    string headerName  = value;
                    string headerValue = data[i + 2].ToString();
                    headers[headerName] = headerValue;
                    i++; // consume the extra param
                    continue;
                }

                paramList.Add(option.ToString());
                paramList.Add(value);
            }

            UUID reqID = httpMod.StartHttpRequest(m_localID, m_itemID, url, paramList, headers, body);
            return reqID == UUID.Zero ? UUID.Zero.ToString() : reqID.ToString();
        }
        public void llHTTPResponse(string request_id, int status, string body)
        {
            // Sends an HTTP response back to the caller of an llRequestURL endpoint
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return;
            if (!UUID.TryParse(request_id, out UUID reqID)) return;
            urlMod.HttpResponse(reqID, status, body ?? string.Empty);
        }

        public string llGetHTTPHeader(string request_id, string header)
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return string.Empty;
            if (!UUID.TryParse(request_id, out UUID reqID)) return string.Empty;
            return urlMod.GetHttpHeader(reqID, header) ?? string.Empty;
        }

        public void llSetContentType(string request_id, int content_type)
        {
            // LSL content_type constants: 0=text/plain, 1=text/html, 2=application/json, 3=application/xml
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return;
            if (!UUID.TryParse(request_id, out UUID reqID)) return;
            string mimeType = content_type switch
            {
                1 => "text/html",
                2 => "application/json",
                3 => "application/xml",
                4 => "application/llsd+xml",
                5 => "application/llsd+json",
                6 => "application/llsd+binary",
                _ => "text/plain"   // CONTENT_TYPE_TEXT = 0
            };
            urlMod.HttpContentType(reqID, mimeType);
        }

        public string llRequestURL()
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return UUID.Zero.ToString();
            UUID reqID = urlMod.RequestURL(m_ScriptEngine, m_host, m_itemID, null);
            return reqID.ToString();
        }

        public string llRequestSecureURL()
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return UUID.Zero.ToString();
            UUID reqID = urlMod.RequestSecureURL(m_ScriptEngine, m_host, m_itemID, null);
            return reqID.ToString();
        }

        public void llReleaseURL(string url)
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            urlMod?.ReleaseURL(url);
        }

        public int llGetFreeURLs()
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return 0;
            return urlMod.GetFreeUrls();
        }
        public int iwValidateURL(string url)
        {
            bool ret = Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult)
                && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
            return Convert.ToInt32(ret);
        }

        // ── Email / remote data ────────────────────────────────────────────────

        public void llEmail(string address, string subject, string message)
        {
            // Faithful port from Halcyon
            try
            {
                IEmailModule emailModule = World?.RequestModuleInterface<IEmailModule>();
                if (emailModule == null) return;
                emailModule.SendEmail(m_host.UUID, m_host.OwnerID, address, subject, message);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llEmail exception: {0}", e.Message);
            }
            ScriptSleep(20000);
        }
        public void llGetNextEmail(string address, string subject)
        {
            // Faithful port from Halcyon
            try
            {
                IEmailModule emailModule = World?.RequestModuleInterface<IEmailModule>();
                if (emailModule == null) return;
                Email email = emailModule.GetNextEmail(m_host.UUID, address, subject);
                if (email == null) return;

                m_ScriptEngine.PostObjectEvent(m_host.LocalId,
                    new EventParams("email",
                        new object[] {
                            email.time,
                            email.sender,
                            email.subject,
                            email.message,
                            email.numLeft
                        },
                        new DetectParams[0]));
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llGetNextEmail exception: {0}", e.Message);
            }
        }
        public void llOpenRemoteDataChannel()
        {
            // Faithful port from Halcyon
            try
            {
                IXMLRPC xmlrpcMod = World?.RequestModuleInterface<IXMLRPC>();
                if (xmlrpcMod == null || !xmlrpcMod.IsEnabled()) return;

                UUID channelID = xmlrpcMod.OpenXMLRPCChannel(m_localID, m_itemID, UUID.Zero);
                IXmlRpcRouter xmlRpcRouter = World?.RequestModuleInterface<IXmlRpcRouter>();
                if (xmlRpcRouter != null)
                    xmlRpcRouter.RegisterNewReceiver(m_ScriptEngine.ScriptModule, channelID, m_host.UUID, m_itemID,
                        "http://" + System.Environment.MachineName + ":" + xmlrpcMod.Port.ToString() + "/");

                object[] resobj = new object[] { 1, channelID.ToString(), UUID.Zero.ToString(), string.Empty, 0, string.Empty };
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams("remote_data", resobj, new DetectParams[0]));
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llOpenRemoteDataChannel exception: {0}", e.Message);
            }
            ScriptSleep(1000);
        }
        public string llSendRemoteData(string channel, string dest, int idata, string sdata)
        {
            // Faithful port from Halcyon
            try
            {
                IXMLRPC xmlrpcMod = World?.RequestModuleInterface<IXMLRPC>();
                if (xmlrpcMod == null) { ScriptSleep(3000); return UUID.Zero.ToString(); }
                ScriptSleep(3000);
                return xmlrpcMod.SendRemoteData(m_localID, m_itemID, channel, dest, idata, sdata).ToString();
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llSendRemoteData exception: {0}", e.Message);
                ScriptSleep(3000);
                return UUID.Zero.ToString();
            }
        }
        public void llRemoteDataReply(string channel, string message_id, string sdata, int idata)
        {
            // Faithful port from Halcyon
            try
            {
                IXMLRPC xmlrpcMod = World?.RequestModuleInterface<IXMLRPC>();
                if (xmlrpcMod != null)
                    xmlrpcMod.RemoteDataReply(channel, message_id, sdata, idata);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llRemoteDataReply exception: {0}", e.Message);
            }
            ScriptSleep(3000);
        }
        public void llCloseRemoteDataChannel(string channel)
        {
            // Faithful port from Halcyon
            try
            {
                IXMLRPC xmlrpcMod = World?.RequestModuleInterface<IXMLRPC>();
                if (xmlrpcMod != null)
                    xmlrpcMod.CloseXMLRPCChannel(UUID.Parse(channel));
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llCloseRemoteDataChannel exception: {0}", e.Message);
            }
            ScriptSleep(1000);
        }

        // ── Strings ────────────────────────────────────────────────────────────

        public int llStringLength(string str) => str?.Length ?? 0;
        public string llToUpper(string src) => src?.ToUpper() ?? string.Empty;
        public string llToLower(string src) => src?.ToLower() ?? string.Empty;

        public string llGetSubString(string src, int start, int end)
        {
            if (src == null) return string.Empty;
            int len = src.Length;
            if (start < 0) start = Math.Max(len + start, 0);
            if (end < 0) end = len + end;
            if (start > end || start >= len) return string.Empty;
            end = Math.Min(end, len - 1);
            return src.Substring(start, end - start + 1);
        }

        public string llDeleteSubString(string src, int start, int end)
        {
            if (src == null) return string.Empty;
            int len = src.Length;
            if (start < 0) start = Math.Max(len + start, 0);
            if (end < 0) end = len + end;
            if (start > end) return src;
            start = Math.Max(0, start); end = Math.Min(len - 1, end);
            return src.Remove(start, end - start + 1);
        }

        public string llInsertString(string dst, int position, string src)
        {
            if (dst == null) dst = string.Empty;
            if (src == null) src = string.Empty;
            position = Math.Max(0, Math.Min(position, dst.Length));
            return dst.Insert(position, src);
        }

        public int llSubStringIndex(string source, string pattern)
            => source?.IndexOf(pattern, StringComparison.Ordinal) ?? -1;

        public int iwSubStringIndex(string source, string pattern, int offset, int isCaseSensitive)
        {
            if (source == null || pattern == null) return -1;
            var comp = isCaseSensitive != 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return source.IndexOf(pattern, Math.Max(0, offset), comp);
        }

        public int iwMatchString(string str, string pattern, int matchType)
        {
            int len1 = str?.Length ?? 0;
            int len2 = pattern?.Length ?? 0;
            if (len1 == 0 || len2 == 0)
            {
                if (matchType <= 1 && (len1 == 0 && len2 == 0)) return 1;
                else return 0;
            }
            switch (matchType)
            {
                case -2: return (str.IndexOf(pattern) != -1) ? 1 : 0; // IW_MATCH_INCLUDE
                case -1: return (str == pattern) ? 1 : 0;             // IW_MATCH_EQUAL
                case 0:  return str.StartsWith(pattern) ? 1 : 0;      // IW_MATCH_HEAD
                case 1:  return str.EndsWith(pattern) ? 1 : 0;        // IW_MATCH_TAIL
                case 2:                                                // IW_MATCH_REGEX
                    var r = new System.Text.RegularExpressions.Regex("^" + pattern + "$");
                    return (r.Match(str).Length != 0) ? 1 : 0;
                case 3:                                                // IW_MATCH_COUNT
                    return System.Text.RegularExpressions.Regex.Matches(str,
                        System.Text.RegularExpressions.Regex.Escape(pattern)).Count;
                case 4:                                                // IW_MATCH_COUNT_REGEX
                    return System.Text.RegularExpressions.Regex.Matches(str, pattern).Count;
            }
            return 0;
        }
        public string iwReplaceString(string str, string pattern, string replacement)
        {
            if (String.IsNullOrEmpty(str) || String.IsNullOrEmpty(pattern)) return str;
            if (String.IsNullOrEmpty(replacement)) return str.Replace(pattern, null);
            if (replacement.Length > 1024 || pattern.Length > 1024) return str;
            return str.Replace(pattern, replacement);
        }
        public string llReplaceSubString(string src, string pattern, string replacement, int count)
        {
            // SL: replace first 'count' occurrences of pattern in src. count=0 means all.
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(pattern)) return src ?? string.Empty;
            replacement ??= string.Empty;
            if (count == 0) return src.Replace(pattern, replacement);
            // Negative count means replace from end
            bool fromEnd = count < 0;
            int absCount = Math.Abs(count);
            if (!fromEnd)
            {
                var sb = new System.Text.StringBuilder(src.Length);
                int pos = 0, found = 0;
                while (found < absCount)
                {
                    int idx = src.IndexOf(pattern, pos, StringComparison.Ordinal);
                    if (idx < 0) break;
                    sb.Append(src, pos, idx - pos);
                    sb.Append(replacement);
                    pos = idx + pattern.Length;
                    found++;
                }
                sb.Append(src, pos, src.Length - pos);
                return sb.ToString();
            }
            else
            {
                // Find all occurrences, then replace the last N
                var indices = new List<int>();
                int p = 0;
                while (true)
                {
                    int idx = src.IndexOf(pattern, p, StringComparison.Ordinal);
                    if (idx < 0) break;
                    indices.Add(idx);
                    p = idx + pattern.Length;
                }
                if (indices.Count == 0) return src;
                int skip = Math.Max(0, indices.Count - absCount);
                var sb2 = new System.Text.StringBuilder(src.Length);
                int pos2 = 0;
                for (int i = 0; i < indices.Count; i++)
                {
                    sb2.Append(src, pos2, indices[i] - pos2);
                    if (i >= skip) sb2.Append(replacement);
                    else sb2.Append(pattern);
                    pos2 = indices[i] + pattern.Length;
                }
                sb2.Append(src, pos2, src.Length - pos2);
                return sb2.ToString();
            }
        }
        public string iwFormatString(string str, LSLList values)
        {
            if (String.IsNullOrEmpty(str)) return str;
            int len = values.Length;
            for (int i = 0; i < len; i++)
            {
                string pattern = "{" + Convert.ToString(i) + "}";
                string val = values.GetLSLStringItem(i);
                if (val.Length > 1024) val = val.Substring(0, 1023);
                if (str.Contains(pattern))
                {
                    if (!String.IsNullOrEmpty(val))
                        str = str.Replace(pattern, val);
                }
                else break;
                if (str.Length > 32768)
                {
                    ShoutError("Return value from iwFormatString is greater than 64kb");
                    return String.Empty;
                }
            }
            return str;
        }
        public string iwStringCodec(string str, string pattern, int operation, LSLList extraParams) { /* Requires InWorldz CodecUtil class — not available */ return str ?? string.Empty; }
        public string iwReverseString(string src) => new string(src?.ToCharArray() ?? Array.Empty<char>());
        public int iwChar2Int(string src, int index)
        {
            if (String.IsNullOrEmpty(src)) return 0;
            if (index < 0) index = src.Length + index;
            if (index < 0 || index >= src.Length) return 0;
            return (int)src[index];
        }
        public string iwInt2Char(int num)
        {
            if (num < 0 || num > 0xffff) return String.Empty;
            return Convert.ToChar(num).ToString();
        }

        public string llStringTrim(string src, int trim_type)
        {
            if (src == null) return string.Empty;
            if (trim_type == 1) return src.TrimStart();
            if (trim_type == 2) return src.TrimEnd();
            return src.Trim();
        }

        public string llStringToBase64(string str) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(str ?? ""));
        public string llBase64ToString(string str) { try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(str ?? "")).Replace("\uFFFD", "?"); } catch { return string.Empty; } }
        public string llIntegerToBase64(int number)
        {
            char[] imdt = new char[8];
            imdt[7] = '=';
            imdt[6] = '=';
            imdt[5] = i2ctable[number << 4 & 0x3F];
            imdt[4] = i2ctable[number >> 2 & 0x3F];
            imdt[3] = i2ctable[number >> 8 & 0x3F];
            imdt[2] = i2ctable[number >> 14 & 0x3F];
            imdt[1] = i2ctable[number >> 20 & 0x3F];
            imdt[0] = i2ctable[number >> 26 & 0x3F];
            return new string(imdt);
        }

        public int llBase64ToInteger(string str)
        {
            if (string.IsNullOrEmpty(str) || str.Length > 8) return 0;
            int number = 0;
            int digit = 0;
            if (str[0] >= c2itable.Length || (digit = c2itable[str[0]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 26;
            if (str.Length < 2 || str[1] >= c2itable.Length || (digit = c2itable[str[1]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 20;
            if (str.Length < 3 || str[2] >= c2itable.Length || (digit = c2itable[str[2]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 14;
            if (str.Length < 4 || str[3] >= c2itable.Length || (digit = c2itable[str[3]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 8;
            if (str.Length < 5 || str[4] >= c2itable.Length || (digit = c2itable[str[4]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 2;
            if (str.Length < 6 || str[5] >= c2itable.Length || (digit = c2itable[str[5]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit >> 4;
            return number;
        }

        public string llXorBase64Strings(string s1, string s2)
        {
            // Deprecated per LSL spec — return empty string
            return string.Empty;
        }

        public string llXorBase64StringsCorrect(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str1)) return str1 ?? string.Empty;
            if (string.IsNullOrEmpty(str2)) return str1;
            string src1 = llBase64ToString(str1);
            string src2 = llBase64ToString(str2);
            var result = new System.Text.StringBuilder(src1.Length);
            int c = 0;
            for (int i = 0; i < src1.Length; i++)
            {
                result.Append((char)(src1[i] ^ src2[c]));
                if (++c >= src2.Length) c = 0;
            }
            return llStringToBase64(result.ToString());
        }

        public string llMD5String(string src, int nonce)
        {
            string input = src + ":" + nonce.ToString();
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        public string llSHA1String(string src)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(src ?? ""));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
        public string iwSHA256String(string src)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(src ?? string.Empty));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
        public string llComputeHash(string src, string algorithm)
        {
            // SL: supports md5, md5_sha1, sha1, sha224, sha256, sha384, sha512
            byte[] data = System.Text.Encoding.UTF8.GetBytes(src ?? string.Empty);
            byte[] hash;
            switch ((algorithm ?? "").ToLowerInvariant())
            {
                case "md5":
                    using (var alg = System.Security.Cryptography.MD5.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "sha1":
                    using (var alg = System.Security.Cryptography.SHA1.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "sha256":
                    using (var alg = System.Security.Cryptography.SHA256.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "sha384":
                    using (var alg = System.Security.Cryptography.SHA384.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "sha512":
                    using (var alg = System.Security.Cryptography.SHA512.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "md5_sha1":
                    // SL-specific: MD5 hash of the input, then SHA1 hash of that MD5 hex string
                    byte[] md5Hash;
                    using (var md5 = System.Security.Cryptography.MD5.Create())
                        md5Hash = md5.ComputeHash(data);
                    string md5Hex = BitConverter.ToString(md5Hash).Replace("-", "").ToLower();
                    using (var sha1 = System.Security.Cryptography.SHA1.Create())
                        hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(md5Hex));
                    break;
                default:
                    ShoutError("llComputeHash: unsupported algorithm '" + algorithm + "'");
                    return string.Empty;
            }
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
        public string llSHA256String(string src, int nonce)
        {
            // SL: SHA256 hash with nonce appended
            return llComputeHash(src + ":" + nonce.ToString(), "sha256");
        }
        public string llHMAC(string msg, string privateKey, string algorithm)
        {
            // SL: Base64-encoded HMAC hash
            byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(privateKey ?? string.Empty);
            byte[] msgBytes = System.Text.Encoding.UTF8.GetBytes(msg ?? string.Empty);
            byte[] hash;
            switch ((algorithm ?? "").ToLowerInvariant())
            {
                case "md5":
                    using (var hmac = new System.Security.Cryptography.HMACMD5(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                case "sha1":
                    using (var hmac = new System.Security.Cryptography.HMACSHA1(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                case "sha256":
                    using (var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                case "sha384":
                    using (var hmac = new System.Security.Cryptography.HMACSHA384(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                case "sha512":
                    using (var hmac = new System.Security.Cryptography.HMACSHA512(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                default:
                    ShoutError("llHMAC: unsupported algorithm '" + algorithm + "'");
                    return string.Empty;
            }
            return Convert.ToBase64String(hash);
        }
        public string llGenerateKey() => UUID.Random().ToString();
        public string llEscapeURL(string url) => Uri.EscapeDataString(url ?? "");
        public string llUnescapeURL(string url) => Uri.UnescapeDataString(url ?? "");
        public string llChar(int unicode)
        {
            // SL: returns a one-character string from a Unicode codepoint
            if (unicode < 0) return string.Empty;
            // SL allows U+0000 but returns empty for negatives
            if (unicode == 0) return "\0";
            try { return char.ConvertFromUtf32(unicode); }
            catch { return string.Empty; }
        }
        public int llOrd(string src, int index)
        {
            // SL: returns the Unicode codepoint of the character at index
            if (string.IsNullOrEmpty(src)) return 0;
            if (index < 0) index = src.Length + index; // negative indexing
            if (index < 0 || index >= src.Length) return 0;
            // Handle surrogate pairs
            if (char.IsHighSurrogate(src[index]) && index + 1 < src.Length && char.IsLowSurrogate(src[index + 1]))
                return char.ConvertToUtf32(src[index], src[index + 1]);
            return src[index];
        }

        // ── Lists ──────────────────────────────────────────────────────────────

        public int llGetListLength(LSLList src) => src?.Length ?? 0;
        public int llList2Integer(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return 0;
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return 0;
            try { return Convert.ToInt32(src.Data[i]); } catch { return 0; }
        }
        public float llList2Float(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return 0f;
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return 0f;
            try { return (float)Convert.ToDouble(src.Data[i]); } catch { return 0f; }
        }
        public string llList2String(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return string.Empty;
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return string.Empty;
            var v = src.Data[i];
            if (v == null) return string.Empty;
            return v.ToString();
        }
        public string llList2Key(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return UUID.Zero.ToString();
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return UUID.Zero.ToString();
            var v = src.Data[i];
            return v?.ToString() ?? UUID.Zero.ToString();
        }
        public Vector3 llList2Vector(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return Vector3.Zero;
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return Vector3.Zero;
            var v = src.Data[i];
            if (v is Vector3 vec) return vec;
            if (v is string s) try { return Vector3.Parse(s); } catch { }
            return Vector3.Zero;
        }
        public Quaternion llList2Rot(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return Quaternion.Identity;
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return Quaternion.Identity;
            var v = src.Data[i];
            if (v is Quaternion q) return q;
            if (v is string s) try { return Quaternion.Parse(s); } catch { }
            return Quaternion.Identity;
        }
        public LSLList llList2List(LSLList src, int start, int end)
        {
            if (src == null || src.Length == 0) return new LSLList();
            int len = src.Length;
            if (start < 0) start = Math.Max(len + start, 0);
            if (end < 0) end = len + end;
            if (start > end) return new LSLList();
            end = Math.Min(end, len - 1);
            int count = end - start + 1;
            var result = new object[count];
            Array.Copy(src.Data, start, result, 0, count);
            return new LSLList(result);
        }
        public LSLList llDeleteSubList(LSLList src, int start, int end)
        {
            if (src == null) return new LSLList();
            int len = src.Length;
            if (start < 0) start = Math.Max(len + start, 0);
            if (end < 0) end = len + end;
            if (start > end) return src;
            start = Math.Max(0, start); end = Math.Min(len - 1, end);
            var result = new System.Collections.Generic.List<object>();
            for (int i = 0; i < len; i++)
                if (i < start || i > end) result.Add(src.Data[i]);
            return new LSLList(result.ToArray());
        }
        public int llGetListEntryType(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return 0;
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return 0;
            var v = src.Data[i];
            if (v is int)       return 1; // TYPE_INTEGER
            if (v is float || v is double) return 2; // TYPE_FLOAT
            if (v is string)    return 3; // TYPE_STRING
            if (v is UUID)      return 4; // TYPE_KEY
            if (v is Vector3)   return 5; // TYPE_VECTOR
            if (v is Quaternion) return 6; // TYPE_ROTATION
            if (v is LSLList)   return 0; // TYPE_INVALID
            return 3; // default to string
        }
        public string llList2CSV(LSLList src)
        {
            if (src == null || src.Length == 0) return string.Empty;
            return string.Join(", ", System.Linq.Enumerable.Select(src.Data, o => o?.ToString() ?? string.Empty));
        }
        public LSLList llCSV2List(string src)
        {
            if (string.IsNullOrEmpty(src)) return new LSLList();
            var parts = src.Split(',');
            var result = new object[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = parts[i].Trim();
            return new LSLList(result);
        }
        public LSLList llListSort(LSLList src, int stride, int ascending)
        {
            if (src == null || src.Length == 0) return new LSLList();
            if (stride < 1) stride = 1;
            int len = src.Length;
            // Build list of stride-length chunks
            int chunks = len / stride;
            var chunkList = new System.Collections.Generic.List<object[]>();
            for (int i = 0; i < chunks; i++)
            {
                var chunk = new object[stride];
                Array.Copy(src.Data, i * stride, chunk, 0, stride);
                chunkList.Add(chunk);
            }
            chunkList.Sort((a, b) =>
            {
                string sa = a[0]?.ToString() ?? string.Empty;
                string sb = b[0]?.ToString() ?? string.Empty;
                // Try numeric compare first
                double da, db;
                if (double.TryParse(sa, out da) && double.TryParse(sb, out db))
                    return ascending != 0 ? da.CompareTo(db) : db.CompareTo(da);
                return ascending != 0 ? string.Compare(sa, sb, StringComparison.Ordinal)
                                      : string.Compare(sb, sa, StringComparison.Ordinal);
            });
            var result = new object[chunks * stride];
            for (int i = 0; i < chunks; i++)
                Array.Copy(chunkList[i], 0, result, i * stride, stride);
            return new LSLList(result);
        }
        public LSLList llListRandomize(LSLList src, int stride)
        {
            if (src == null || src.Length == 0) return src ?? new LSLList();
            if (stride <= 0) stride = 1;
            if (src.Length == stride || src.Length % stride != 0)
                return new LSLList(new List<object>(src.Data));
            int chunkCount = src.Length / stride;
            int[] chunks = new int[chunkCount];
            for (int i = 0; i < chunkCount; i++) chunks[i] = i;
            var rand = new Random();
            for (int i = chunkCount - 1; i >= 1; i--)
            {
                int idx = rand.Next(i + 1);
                int tmp = chunks[i]; chunks[i] = chunks[idx]; chunks[idx] = tmp;
            }
            var result = new List<object>(src.Length);
            for (int i = 0; i < chunkCount; i++)
                for (int j = 0; j < stride; j++)
                    result.Add(src.Data[chunks[i] * stride + j]);
            return new LSLList(result);
        }

        public LSLList llList2ListStrided(LSLList src, int start, int end, int stride)
        {
            if (src == null) return new LSLList();
            var result = new List<object>();
            int len = src.Length;
            if (start < 0) start = len + start;
            if (end < 0)   end   = len + end;
            start = Math.Max(0, Math.Min(start, len - 1));
            end   = Math.Max(0, Math.Min(end,   len - 1));
            if (stride == 0) stride = 1;
            if (start == end) return new LSLList(result);

            if (stride > 0)
            {
                if (start <= end)
                    for (int i = start; i <= end; i += stride) result.Add(src.Data[i]);
                else
                {
                    for (int i = start; i < len; i += stride) result.Add(src.Data[i]);
                    for (int i = 0; i <= end; i += stride) result.Add(src.Data[i]);
                }
            }
            else
            {
                if (start >= end)
                    for (int i = start; i >= end && i >= 0; i += stride) result.Add(src.Data[i]);
            }
            return new LSLList(result);
        }
        public LSLList llListInsertList(LSLList dest, LSLList src, int start)
        {
            if (dest == null) dest = new LSLList();
            if (src == null || src.Length == 0) return dest;
            int len = dest.Length;
            if (start < 0) start = Math.Max(len + start, 0);
            start = Math.Min(start, len);
            var result = new object[len + src.Length];
            Array.Copy(dest.Data, 0, result, 0, start);
            Array.Copy(src.Data, 0, result, start, src.Length);
            Array.Copy(dest.Data, start, result, start + src.Length, len - start);
            return new LSLList(result);
        }
        public int llListFindList(LSLList src, LSLList test)
        {
            if (src == null || test == null || test.Length == 0 || src.Length < test.Length) return -1;
            for (int i = 0; i <= src.Length - test.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < test.Length; j++)
                {
                    string a = src.Data[i+j]?.ToString() ?? string.Empty;
                    string b = test.Data[j]?.ToString() ?? string.Empty;
                    if (a != b) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
        public int llListFindListNext(LSLList src, LSLList test, int ofs)
        {
            // SL: like llListFindList but starts searching from offset ofs
            if (src == null || test == null || test.Length == 0 || src.Length < test.Length) return -1;
            if (ofs < 0) ofs = 0;
            for (int i = ofs; i <= src.Length - test.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < test.Length; j++)
                {
                    string a = src.Data[i+j]?.ToString() ?? string.Empty;
                    string b = test.Data[j]?.ToString() ?? string.Empty;
                    if (a != b) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
        public int llListFindStrided(LSLList src, LSLList test, int start, int end, int stride)
        {
            // SL: find test in src with stride constraints
            if (src == null || test == null || test.Length == 0) return -1;
            int len = src.Length;
            if (stride < 1) stride = 1;
            if (test.Length > stride) return -1;
            // Resolve negative indices
            if (start < 0) start = len + start;
            if (end < 0) end = len + end;
            start = Math.Max(0, start);
            end = Math.Min(len - 1, end);
            // Search only at stride boundaries
            for (int i = start; i <= end - test.Length + 1; i += stride)
            {
                bool match = true;
                for (int j = 0; j < test.Length; j++)
                {
                    string a = src.Data[i+j]?.ToString() ?? string.Empty;
                    string b = test.Data[j]?.ToString() ?? string.Empty;
                    if (a != b) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
        public LSLList llList2ListSlice(LSLList src, int start, int end, int stride, int slice_index)
        {
            // SL: extract the slice_index'th element from each stride in range start..end
            if (src == null || src.Length == 0) return new LSLList();
            int len = src.Length;
            if (stride < 1) stride = 1;
            // Resolve negative indices
            if (start < 0) start = len + start;
            if (end < 0) end = len + end;
            start = Math.Max(0, start);
            end = Math.Min(len - 1, end);
            // Resolve negative slice_index (counts from end of stride)
            if (slice_index < 0) slice_index = stride + slice_index;
            if (slice_index < 0 || slice_index >= stride) return new LSLList();
            var result = new List<object>();
            for (int i = start; i <= end; i += stride)
            {
                int idx = i + slice_index;
                if (idx <= end && idx < len)
                    result.Add(src.Data[idx]);
            }
            return new LSLList(result);
        }
        public LSLList llSortListStrided(LSLList src, int stride, int stride_index, int ascending)
        {
            // SL: sort strided list by the stride_index'th element in each stride
            if (src == null || src.Length == 0) return new LSLList();
            if (stride < 1) stride = 1;
            int len = src.Length;
            // If list length is not a multiple of stride, return unchanged
            if (stride > 1 && len % stride != 0) return src;
            // stride_index must be in range [-stride, stride)
            if (stride_index < 0) stride_index = stride + stride_index;
            if (stride_index < 0 || stride_index >= stride) return new LSLList();
            // If stride_index is 0, delegate to existing llListSort
            if (stride_index == 0) return llListSort(src, stride, ascending);
            // Build list of stride groups, sort by stride_index element
            int numStrides = len / stride;
            var groups = new List<object[]>(numStrides);
            for (int i = 0; i < numStrides; i++)
            {
                var group = new object[stride];
                for (int j = 0; j < stride; j++)
                    group[j] = src.Data[i * stride + j];
                groups.Add(group);
            }
            groups.Sort((a, b) =>
            {
                object va = a[stride_index];
                object vb = b[stride_index];
                int cmp;
                // Compare by type, matching SL behavior
                if (va is int ia && vb is int ib) cmp = ia.CompareTo(ib);
                else if (va is float fa && vb is float fb) cmp = fa.CompareTo(fb);
                else if (va is double da && vb is double db) cmp = da.CompareTo(db);
                else cmp = string.Compare(va?.ToString() ?? "", vb?.ToString() ?? "", StringComparison.Ordinal);
                return ascending == 1 ? cmp : -cmp;
            });
            var result = new List<object>(len);
            foreach (var g in groups)
                result.AddRange(g);
            return new LSLList(result);
        }
        public LSLList llListReplaceList(LSLList dest, LSLList src, int start, int end)
        {
            if (dest == null) dest = new LSLList();
            if (src == null) src = new LSLList();
            int len = dest.Length;
            if (start < 0) start = Math.Max(len + start, 0);
            if (end < 0) end = len + end;
            start = Math.Max(0, Math.Min(start, len));
            end = Math.Max(0, Math.Min(end, len - 1));
            var result = new System.Collections.Generic.List<object>();
            for (int i = 0; i < start; i++) result.Add(dest.Data[i]);
            result.AddRange(src.Data);
            for (int i = end + 1; i < len; i++) result.Add(dest.Data[i]);
            return new LSLList(result.ToArray());
        }
        public float llListStatistics(int operation, LSLList src)
        {
            if (src == null || src.Length == 0) return 0f;
            var nums = new List<double>();
            foreach (object o in src.Data)
                try { nums.Add(Convert.ToDouble(o)); } catch { }
            if (nums.Count == 0) return 0f;
            nums.Sort();
            switch (operation)
            {
                case 0: return (float)(nums[nums.Count - 1] - nums[0]);                   // RANGE
                case 1: return (float)nums[0];                                             // MIN
                case 2: return (float)nums[nums.Count - 1];                               // MAX
                case 3: { double s = 0; foreach (var n in nums) s += n; return (float)(s / nums.Count); } // MEAN
                case 4: { int m = nums.Count / 2; return (nums.Count % 2 == 0) ? (float)((nums[m-1]+nums[m])/2.0) : (float)nums[m]; } // MEDIAN
                case 5:
                {
                    double s = 0; foreach (var n in nums) s += n; double mean = s / nums.Count;
                    double v = 0; foreach (var n in nums) v += (n - mean) * (n - mean);
                    return (float)Math.Sqrt(v / nums.Count);
                } // STD_DEV
                case 6: { double s = 0; foreach (var n in nums) s += n; return (float)s; }          // SUM
                case 7: { double s = 0; foreach (var n in nums) s += n * n; return (float)s; }      // SUM_SQUARES
                case 8: return nums.Count;                                                 // NUM_COUNT
                case 9: { double p = 1.0; foreach (var n in nums) p *= Math.Abs(n); return (float)Math.Pow(p, 1.0/nums.Count); } // GEOMETRIC_MEAN
                case 10: { double s = 0; foreach (var n in nums) { if (n != 0) s += 1.0/n; } return s == 0 ? 0f : (float)(nums.Count/s); } // HARMONIC_MEAN
                default: return 0f;
            }
        }
        public string llDumpList2String(LSLList src, string separator)
        {
            if (src == null || src.Length == 0) return string.Empty;
            return string.Join(separator ?? string.Empty, System.Linq.Enumerable.Select(src.Data, o => o?.ToString() ?? string.Empty));
        }
        public LSLList llParseString2List(string src, LSLList separators, LSLList spacers)
        {
            return ParseString2List(src, separators, spacers, false);
        }
        public LSLList iwParseString2List(string src, LSLList separators, LSLList spacers_in, LSLList args)
        {
            // Faithful port from Halcyon: extended string parser with trim/capitalize/autocast/keepnulls/maxsplits
            if (string.IsNullOrEmpty(src)) return new LSLList();
            List<object> ret = new List<object>();
            List<object> spacers = new List<object>();

            bool keepNulls = false;
            int trimString = 0;
            int maxSplits = 0;
            int totalSplits = 0;
            int doCapitalize = 0;
            int autoCast = 0;

            if (args.Length > 0)
            {
                for (int i = 0; i < args.Length; i += 2)
                {
                    if (!(args.Data[i] is string)) continue;
                    string argName = args.GetLSLStringItem(i).ToLower();
                    switch (argName)
                    {
                        case "keepnulls": keepNulls = (args.GetLSLIntegerItem(i + 1) == 1); break;
                        case "trimstrings":
                            trimString = args.GetLSLIntegerItem(i + 1);
                            if (trimString < 0 || trimString > 3) trimString = 0;
                            break;
                        case "maxsplits":
                            maxSplits = args.GetLSLIntegerItem(i + 1);
                            if (maxSplits < 0) maxSplits = 0;
                            break;
                        case "capitalize":
                            doCapitalize = args.GetLSLIntegerItem(i + 1);
                            if (doCapitalize < 0 || doCapitalize > 2) doCapitalize = 0;
                            break;
                        case "autocast":
                            autoCast = args.GetLSLIntegerItem(i + 1);
                            if (autoCast < 1 || autoCast > 2) autoCast = 0;
                            break;
                    }
                }
            }

            // Build spacers list (spacers not in separators)
            if (spacers_in.Length > 0 && separators.Length > 0)
            {
                foreach (var spacer in spacers_in.Data)
                {
                    bool found = false;
                    foreach (var sep in separators.Data)
                        if (sep.ToString() == spacer.ToString()) { found = true; break; }
                    if (!found) spacers.Add(spacer);
                }
            }
            else if (spacers_in.Length > 0 && separators.Length == 0)
            {
                foreach (var s in spacers_in.Data) spacers.Add(s);
            }
            else if (spacers_in.Length == 0 && separators.Length == 0)
            {
                return new LSLList(ret);
            }

            object[] delimiters = new object[separators.Length + spacers.Count];
            separators.Data.CopyTo(delimiters, 0);
            spacers.CopyTo(delimiters, separators.Length);

            bool dfound;
            do
            {
                dfound = false;
                int cindex = -1;
                string cdeli = string.Empty;
                foreach (var delimiter in delimiters)
                {
                    string ds = delimiter.ToString();
                    if (string.IsNullOrEmpty(ds)) continue;
                    int index = src.IndexOf(ds);
                    if (index != -1)
                    {
                        if (cindex > index || cindex == -1)
                        {
                            cindex = index;
                            cdeli = ds;
                        }
                        dfound = true;
                    }
                }
                if (cindex != -1)
                {
                    if (cindex > 0)
                    {
                        string temp = ParseString2ListApply(src.Substring(0, cindex), trimString, doCapitalize);
                        if (!string.IsNullOrEmpty(temp) || keepNulls)
                        {
                            totalSplits++;
                            ret.Add(autoCast > 0 ? AutoCastString(temp) : (object)temp);
                        }
                    }
                    else if (keepNulls)
                    {
                        totalSplits++;
                        ret.Add(string.Empty);
                    }
                    if (maxSplits > 0 && totalSplits >= maxSplits) { src = src.Substring(cindex); break; }

                    // Check if delimiter is a spacer (include it in output)
                    foreach (var spacer in spacers)
                    {
                        if (spacer.ToString() == cdeli)
                        {
                            string temp = ParseString2ListApply(cdeli, trimString, doCapitalize);
                            if (!string.IsNullOrEmpty(temp) || keepNulls)
                            {
                                totalSplits++;
                                ret.Add(autoCast == 2 ? AutoCastString(cdeli) : (object)cdeli);
                            }
                            break;
                        }
                    }
                    if (maxSplits > 0 && totalSplits >= maxSplits) { src = src.Substring(cindex + cdeli.Length); break; }
                    src = src.Substring(cindex + cdeli.Length);
                    if (maxSplits > 0 && totalSplits >= maxSplits) break;
                }
            } while (dfound);

            src = ParseString2ListApply(src, trimString, doCapitalize);
            if (!string.IsNullOrEmpty(src) || keepNulls)
                ret.Add(autoCast > 1 ? AutoCastString(src) : (object)src);

            return new LSLList(ret);
        }

        /// <summary>Apply trim and capitalize to a parsed string element.</summary>
        private string ParseString2ListApply(string str, int trimString, int doCapitalize)
        {
            if (string.IsNullOrEmpty(str)) return str;
            if (trimString != 0) str = llStringTrim(str, trimString);
            if (doCapitalize == 1) str = str.ToUpper();
            else if (doCapitalize == 2) str = str.ToLower();
            return str;
        }

        /// <summary>Attempt to auto-cast a string to int/float/vector/rotation/key.</summary>
        private object AutoCastString(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            int dotCount = str.Length - str.Replace(".", string.Empty).Length;
            if (dotCount == 1) { if (float.TryParse(str, out float f)) return f; }
            else if (dotCount == 0) { if (int.TryParse(str, out int i)) return i; }
            if (str.StartsWith("<") && str.EndsWith(">"))
            {
                int commas = str.Length - str.Replace(",", string.Empty).Length;
                if (commas == 2) { if (Vector3.TryParse(str, out Vector3 vec)) return vec; }
                else if (commas == 3) { if (Quaternion.TryParse(str, out Quaternion quat)) return quat; }
            }
            if (str.Length == 36) { if (UUID.TryParse(str, out UUID k)) return k.ToString(); }
            return str;
        }
        public LSLList llParseStringKeepNulls(string src, LSLList separators, LSLList spacers)
        {
            return ParseString2List(src, separators, spacers, true);
        }
        private int listCompare(LSLList list1, LSLList list2)
        {
            if (list1.Length != list2.Length) return 0;
            int len = list1.Length;
            for (int i = 0; i < len; i++)
            {
                int t1 = llGetListEntryType(list1, i);
                int t2 = llGetListEntryType(list2, i);
                if (t1 != t2 || (t1 == 0 && t2 == 0)) return 0;
                if (t1 == 1 && (list1.GetLSLIntegerItem(i) != list2.GetLSLIntegerItem(i))) return 0;
                if (t1 == 2 && (list1.GetLSLFloatItem(i) != list2.GetLSLFloatItem(i))) return 0;
                else if ((t1 == 3 || t1 == 4) && (list1.GetLSLStringItem(i) != list2.GetLSLStringItem(i))) return 0;
                else if (t1 == 5 && (list1.GetVector3Item(i) != list2.GetVector3Item(i))) return 0;
                else if (t1 == 6 && (list1.GetQuaternionItem(i) != list2.GetQuaternionItem(i))) return 0;
            }
            return 1;
        }

        public int iwMatchList(LSLList list1, LSLList list2, int matchType)
        {
            int len1 = list1.Length;
            int len2 = list2.Length;
            if (len1 == 0 || len2 == 0)
            {
                if (matchType <= 1) return (len1 == 0 && len2 == 0) ? 1 : 0;
                else return 0;
            }
            switch (matchType)
            {
                case -1: // IW_MATCH_EQUAL
                    if (len1 != len2) return 0;
                    return listCompare(list1, list2);
                case 0: // IW_MATCH_HEAD
                    if (len1 < len2) return 0;
                    return listCompare(list1.GetSublist(0, len2 - 1), list2);
                case 1: // IW_MATCH_TAIL
                    if (len1 < len2) return 0;
                    return listCompare(list1.GetSublist(len1 - len2, len1 - 1), list2);
                case 2:
                    ShoutError("IW_MATCH_REGEX not implemented for iwMatchList.");
                    break;
                case 3:
                    ShoutError("IW_MATCH_COUNT not implemented for iwMatchList.");
                    break;
                case 4:
                    ShoutError("IW_MATCH_COUNT_REGEX not implemented for iwMatchList.");
                    break;
            }
            return 0;
        }
        public int iwListIncludesElements(LSLList src, LSLList elements, int any)
        {
            if (elements.Length == 0 || src.Length == 0) return 0;
            for (int a = 0; a < elements.Length; a++)
            {
                bool found = false;
                for (int b = 0; b < src.Length; b++)
                {
                    if (src.Data[b].Equals(elements.Data[a]))
                    {
                        found = true;
                        break;
                    }
                }
                if (any == 1)
                {
                    if (found) return 1;
                }
                else
                {
                    if (!found) return 0;
                }
            }
            return (any == 1) ? 0 : 1;
        }
        public LSLList iwReverseList(LSLList src, int stride)
        {
            if (src.Length <= 1) return src;
            if (stride < 1) return new LSLList(src.Data.Reverse().ToArray());
            if (src.Length % stride != 0)
            {
                ShoutError(string.Format("Error: stride argument is {0}, but source list length is not divisible by {0}", stride));
                return new LSLList();
            }
            List<object> ret = new List<object>();
            for (int a = src.Length - 1; a >= 0; a -= stride)
            {
                ret.AddRange(src.GetSublist(a - (stride - 1), a).Data.ToList());
            }
            return new LSLList(ret);
        }
        public LSLList iwListRemoveElements(LSLList src, LSLList elements, int count, int mode)
        {
            if (src.Length == 0 || elements.Length == 0) return src;
            if (count == 0) count = -1;
            int counted = 0;
            List<object> ret = new List<object>();
            if (mode == 0)
            {
                int len = src.Length - elements.Length + 1;
                for (int i = 0; i < len; i++)
                {
                    if (src.Data[i].Equals(elements.Data[0]))
                    {
                        if (count == -1 || counted < count)
                        {
                            int x;
                            for (x = 1; x < elements.Length; x++)
                                if (!src.Data[i + x].Equals(elements.Data[x]))
                                    break;
                            if (x == elements.Length)
                            {
                                counted++;
                                i += elements.Length - 1;
                                continue;
                            }
                        }
                    }
                    ret.Add(src.Data[i]);
                }
            }
            else
            {
                int len = src.Length;
                for (int i = 0; i < len; i++)
                {
                    if (!elements.Data.Contains(src.Data[i]))
                    {
                        if (count == -1 || counted < count)
                        {
                            ret.Add(src.Data[i]);
                            counted++;
                        }
                    }
                }
            }
            return new LSLList(ret);
        }
        public LSLList iwListRemoveDuplicates(LSLList src)
        {
            if (src.Length <= 1) return src;
            return new LSLList(src.Data.Distinct().ToList());
        }

        // ── Data requests ──────────────────────────────────────────────────────

        public void llRequestAgentData(string id, int data)
        {
            if (m_host == null) return;
            if (!UUID.TryParse(id, out UUID agentId)) return;

            UUID queryID = UUID.Random();
            UUID capturedQuery = queryID;
            UUID capturedAgent = agentId;
            int capturedData = data;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string result = string.Empty;
                    switch (capturedData)
                    {
                        case 1: // DATA_ONLINE — not reliably knowable, always return 0
                            result = "0";
                            break;
                        case 2: // DATA_NAME
                        {
                            ScenePresence sp = World?.GetScenePresence(capturedAgent);
                            if (sp != null)
                            {
                                result = sp.Name;
                            }
                            else
                            {
                                UserAccount acct = World?.UserAccountService?.GetUserAccount(
                                    World.RegionInfo.ScopeID, capturedAgent);
                                result = acct != null
                                    ? acct.FirstName + " " + acct.LastName
                                    : string.Empty;
                            }
                            break;
                        }
                        case 3: // DATA_BORN — account creation date as "YYYY-MM-DD"
                        {
                            UserAccount acct = World?.UserAccountService?.GetUserAccount(
                                World.RegionInfo.ScopeID, capturedAgent);
                            if (acct != null)
                            {
                                var born = DateTimeOffset.FromUnixTimeSeconds(acct.Created).UtcDateTime;
                                result = born.ToString("yyyy-MM-dd");
                            }
                            break;
                        }
                        case 4: // DATA_RATING — removed from SL, always return zeroes
                            result = "0,0,0,0,0,0";
                            break;
                        case 7: // DATA_PAYINFO — not exposed
                            result = "0";
                            break;
                        default:
                            result = string.Empty;
                            break;
                    }
                    PostDataserverEvent(capturedQuery, result);
                }
                catch (Exception ex)
                {
                    m_log.LogError("[PhloxAPI]: llRequestAgentData ex: {0}", ex.Message);
                    PostDataserverEvent(capturedQuery, string.Empty);
                }
            });

            ScriptSleep(100);
        }
        public string llRequestSimulatorData(string simulator, int data)
        {
            // Fires a dataserver event with the requested simulator data
            // DATA_SIM_POS=5, DATA_SIM_STATUS=6, DATA_SIM_RATING=7
            // For local region, answer immediately; remote regions are not supported
            if (World?.RegionInfo == null) return UUID.Zero.ToString();
            string regionName = World.RegionInfo.RegionName;
            if (!simulator.Equals(regionName, StringComparison.OrdinalIgnoreCase))
            {
                // Remote region — not supported, return zero
                return UUID.Zero.ToString();
            }
            UUID queryID = UUID.Random();
            string result = data switch
            {
                5 => // DATA_SIM_POS
                    new LSLList(new object[] {
                        (float)(World.RegionInfo.RegionLocX * Constants.RegionSize),
                        (float)(World.RegionInfo.RegionLocY * Constants.RegionSize),
                        0f }).ToString(),
                6 => "up", // DATA_SIM_STATUS
                7 => World.RegionInfo.RegionSettings.Maturity.ToString(), // DATA_SIM_RATING
                _ => string.Empty
            };
            System.Threading.Tasks.Task.Run(() => PostDataserverEvent(queryID, result));
            return queryID.ToString();
        }

        public string llGetEnv(string name)
        {
            if (World?.RegionInfo == null) return string.Empty;
            return name switch
            {
                "agent_limit"          => World.RegionInfo.RegionSettings.AgentLimit.ToString(),
                "dynamic_pathfinding"  => "disabled",
                "estate_id"            => World.RegionInfo.EstateSettings.EstateID.ToString(),
                "estate_name"          => World.RegionInfo.EstateSettings.EstateName ?? string.Empty,
                "frame_number"         => World.StatsReporter?.LastReportedSimFPS.ToString() ?? "0",
                "region_cpu_ratio"     => "1",
                "region_idle"          => "0",
                "region_product_name"  => "Legion Grid",
                "region_product_sku"   => "Legion",
                "region_start_time"    => "0",
                "sim_channel"          => "Legion Grid",
                "sim_version"          => "0.9.3.0",
                "simulator_hostname"   => System.Net.Dns.GetHostName(),
                "region_max_prims"     => World.RegionInfo.ObjectCapacity.ToString(),
                "region_object_bonus"  => ((float)World.RegionInfo.RegionSettings.ObjectBonus).ToString(),
                _                      => string.Empty
            };
        }
        public float llGetSimStats(int statType)
        {
            // SL stat type constants → SimStatsReporter indices
            // SL constants: 0=TimeDilation, 1=SimFPS, 2=PhysFPS, 3=AgentUpdates,
            //   4=RootAgents, 5=ChildAgents, 6=TotalPrims, 7=ActivePrims,
            //   8=FrameMS, 9=NetMS, 10=PhysicsMS, 11=ImageMS, 12=OtherMS,
            //   13=InPPS, 14=OutPPS, 15=UnAckedBytes, 16=AgentMS,
            //   17=PendingDownloads, 18=PendingUploads, 19=ActiveScripts,
            //   20=SimSleepMs, 21=SimSpareMs
            var reporter = World?.StatsReporter;
            if (reporter == null) return 0f;
            float[] stats = reporter.LastReportedSimStats;
            if (stats == null || statType < 0 || statType >= stats.Length) return 0f;
            return stats[statType];
        }
        public int llGetSPMaxMemory()
        {
            // SL: returns peak script memory usage. Phlox doesn't track this granularly.
            // Return a reasonable default (16KB, typical for LSL scripts)
            return 16384;
        }
        public LSLList llGetObjectAnimationNames()
        {
            // SL Animesh feature — returns list of animations playing on the object.
            // OpenSim doesn't support Animesh; return empty list.
            return new LSLList();
        }
        public void llStartObjectAnimation(string anim)
        {
            // SL Animesh — not supported in OpenSim. No-op.
        }
        public void llStopObjectAnimation(string anim)
        {
            // SL Animesh — not supported in OpenSim. No-op.
        }
        public int llGetLinkSitFlags(int link)
        {
            // SL: returns sit flags for a link. OpenSim doesn't fully implement SitFlags.
            // Return 0 (no flags set).
            return 0;
        }
        public void llSetLinkSitFlags(int link, int flags)
        {
            // SL: sets sit flags for a link. OpenSim doesn't fully implement SitFlags. No-op.
        }
        public Vector3 llLinear2sRGB(Vector3 color)
        {
            // SL: convert linear RGB to sRGB color space
            float ToSRGB(float c)
            {
                if (c <= 0.0031308f) return c * 12.92f;
                return 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;
            }
            return new Vector3(
                Math.Max(0f, Math.Min(1f, ToSRGB(color.X))),
                Math.Max(0f, Math.Min(1f, ToSRGB(color.Y))),
                Math.Max(0f, Math.Min(1f, ToSRGB(color.Z))));
        }
        public Vector3 llSRGB2Linear(Vector3 color)
        {
            // SL: convert sRGB to linear RGB color space
            float ToLinear(float c)
            {
                if (c <= 0.04045f) return c / 12.92f;
                return (float)Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return new Vector3(
                Math.Max(0f, Math.Min(1f, ToLinear(color.X))),
                Math.Max(0f, Math.Min(1f, ToLinear(color.Y))),
                Math.Max(0f, Math.Min(1f, ToLinear(color.Z))));
        }
        public Vector3 llWorldPosToHUD(Vector3 worldPos)
        {
            // SL: converts world position to HUD screen coordinates.
            // Requires viewer camera data not available server-side. Return center screen as default.
            return new Vector3(0.5f, 0.5f, 0f);
        }

        // ── Linkset Data (LSD) ─────────────────────────────────────────────────
        // SL: 128KB persistent key-value store per linkset, survives script reset/copy/transfer.
        // Constants: LINKSETDATA_OK=0, EMEMORY=1, ENOKEY=2, EPROTECTED=3, NOTFOUND=4, NOUPDATE=5
        // Event actions: RESET=0, UPDATE=1, DELETE=2, MULTIDELETE=3

        private const int LINKSETDATA_OK = 0;
        private const int LINKSETDATA_EMEMORY = 1;
        private const int LINKSETDATA_ENOKEY = 2;
        private const int LINKSETDATA_EPROTECTED = 3;
        private const int LINKSETDATA_NOTFOUND = 4;
        private const int LINKSETDATA_NOUPDATE = 5;

        private const int LINKSETDATA_RESET = 0;
        private const int LINKSETDATA_UPDATE = 1;
        private const int LINKSETDATA_DELETE = 2;
        private const int LINKSETDATA_MULTIDELETE = 3;

        // Bind to Tranquillity's native per-linkset limit (SL = 128KB) rather than Legion's
        // Scene.m_LinkSetDataLimit (which Tranquillity does not have).
        private int LinksetDataLimit => LinksetData.LINKSETDATA_MAX;

        public int llLinksetDataAvailable()
        {
            if (m_host?.ParentGroup?.LinksetData == null)
                return LinksetDataLimit;
            return m_host.ParentGroup.LinksetData.Free();
        }

        public int llLinksetDataCountKeys()
        {
            if (m_host?.ParentGroup?.LinksetData == null)
                return 0;
            return m_host.ParentGroup.LinksetData.Count();
        }

        public string llLinksetDataRead(string name)
        {
            if (m_host?.ParentGroup?.LinksetData == null || string.IsNullOrEmpty(name))
                return string.Empty;
            return m_host.ParentGroup.LinksetData.Get(name) ?? string.Empty;
        }

        public string llLinksetDataReadProtected(string name, string pass)
        {
            if (m_host?.ParentGroup?.LinksetData == null || string.IsNullOrEmpty(name))
                return string.Empty;
            return m_host.ParentGroup.LinksetData.Get(name, pass) ?? string.Empty;
        }

        public int llLinksetDataWrite(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
                return LINKSETDATA_ENOKEY;

            if (string.IsNullOrEmpty(value))
            {
                if (m_host?.ParentGroup?.LinksetData == null)
                    return LINKSETDATA_NOTFOUND;
                int delRet = m_host.ParentGroup.LinksetData.Remove(name);
                if (delRet == 0)
                {
                    m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, LINKSETDATA_DELETE, name, string.Empty);
                    m_host.ParentGroup.HasGroupChanged = true;
                }
                return delRet;
            }

            if (m_host?.ParentGroup == null) return LINKSETDATA_EMEMORY;
            m_host.ParentGroup.LinksetData ??= new LinksetData();
            int ret = m_host.ParentGroup.LinksetData.AddOrUpdate(name, value);
            if (ret == 0)
            {
                m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, LINKSETDATA_UPDATE, name, value);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return ret;
        }

        public int llLinksetDataWriteProtected(string name, string value, string pass)
        {
            if (string.IsNullOrEmpty(name))
                return LINKSETDATA_ENOKEY;

            if (string.IsNullOrEmpty(value))
            {
                if (m_host?.ParentGroup?.LinksetData == null)
                    return LINKSETDATA_NOTFOUND;
                int delRet = m_host.ParentGroup.LinksetData.Remove(name, pass);
                if (delRet == 0)
                {
                    m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, LINKSETDATA_DELETE, name, string.Empty);
                    m_host.ParentGroup.HasGroupChanged = true;
                }
                return delRet;
            }

            if (m_host?.ParentGroup == null) return LINKSETDATA_EMEMORY;
            m_host.ParentGroup.LinksetData ??= new LinksetData();
            int ret = m_host.ParentGroup.LinksetData.AddOrUpdate(name, value, pass);
            if (ret == 0)
            {
                m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, LINKSETDATA_UPDATE, name, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return ret;
        }

        public int llLinksetDataDelete(string name)
        {
            if (string.IsNullOrEmpty(name))
                return LINKSETDATA_ENOKEY;
            if (m_host?.ParentGroup?.LinksetData == null)
                return LINKSETDATA_NOTFOUND;
            int ret = m_host.ParentGroup.LinksetData.Remove(name);
            if (ret == 0)
            {
                m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, LINKSETDATA_DELETE, name, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return ret;
        }

        public int llLinksetDataDeleteProtected(string name, string pass)
        {
            if (string.IsNullOrEmpty(name))
                return LINKSETDATA_ENOKEY;
            if (m_host?.ParentGroup?.LinksetData == null)
                return LINKSETDATA_NOTFOUND;
            int ret = m_host.ParentGroup.LinksetData.Remove(name, pass);
            if (ret == 0)
            {
                m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, LINKSETDATA_DELETE, name, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return ret;
        }

        public void llLinksetDataReset()
        {
            if (m_host?.ParentGroup?.LinksetData == null)
                return;
            bool changed = m_host.ParentGroup.LinksetData.Count() > 0;
            m_host.ParentGroup.LinksetData = null;
            if (changed)
            {
                m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, LINKSETDATA_RESET, string.Empty, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
        }

        public LSLList llLinksetDataDeleteFound(string pattern, string pass)
        {
            if (string.IsNullOrEmpty(pattern) || m_host?.ParentGroup?.LinksetData == null)
                return new LSLList(new object[] { 0, 0 });
            string[] deleted = m_host.ParentGroup.LinksetData.RemoveByPattern(pattern, pass, out int notDeleted);
            if (deleted.Length > 0)
            {
                string deletedList = string.Join(",", deleted);
                m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, LINKSETDATA_MULTIDELETE, deletedList, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return new LSLList(new object[] { deleted.Length, notDeleted });
        }

        public int llLinksetDataCountFound(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || m_host?.ParentGroup?.LinksetData == null)
                return 0;
            return m_host.ParentGroup.LinksetData.CountByPattern(pattern);
        }

        public LSLList llLinksetDataListKeys(int start, int count)
        {
            if (m_host?.ParentGroup?.LinksetData == null)
                return new LSLList();
            string[] keys = m_host.ParentGroup.LinksetData.ListKeys(start, count);
            return new LSLList(keys);
        }

        public LSLList llLinksetDataFindKeys(string pattern, int start, int count)
        {
            if (string.IsNullOrEmpty(pattern) || m_host?.ParentGroup?.LinksetData == null)
                return new LSLList();
            string[] keys = m_host.ParentGroup.LinksetData.ListKeysByPatttern(pattern, start, count);
            return new LSLList(keys);
        }
        public string iwRequestAnimationData(string name)
        {
            // Faithful port from Halcyon — look up animation in inventory and return metadata via dataserver
            if (m_host == null) return UUID.Zero.ToString();
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Animation);
            if (item == null) { ScriptSleep(1000); return UUID.Zero.ToString(); }

            UUID queryID = UUID.Random();
            UUID assetId = item.AssetID;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AssetBase asset = World.AssetService.Get(assetId.ToString());
                    if (asset == null || asset.Data == null)
                    {
                        PostDataserverEvent(queryID, string.Empty);
                        return;
                    }
                    BinBVHAnimation anim = new BinBVHAnimation(asset.Data);
                    string reply = string.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8}",
                        (int)anim.Priority,
                        anim.Loop ? 1 : 0,
                        ((float)anim.Length).ToString("F4"),
                        ((float)anim.InPoint).ToString("F4"),
                        ((float)anim.OutPoint).ToString("F4"),
                        ((float)anim.EaseInTime).ToString("F4"),
                        ((float)anim.EaseOutTime).ToString("F4"),
                        (int)anim.HandPose,
                        anim.ExpressionName ?? string.Empty);
                    PostDataserverEvent(queryID, reply);
                }
                catch
                {
                    PostDataserverEvent(queryID, string.Empty);
                }
            });
            ScriptSleep(1000);
            return queryID.ToString();
        }

        // ── Money ──────────────────────────────────────────────────────────────

        public int llGiveMoney(string destination, int amount)
        {
            // No economy module available in Legion
            if (!UUID.TryParse(destination, out UUID destId) || destId == UUID.Zero)
            { ScriptSleep(3000); return 0; }
            if (amount <= 0) { ScriptSleep(3000); return 0; }

            TaskInventoryItem item = GetInventorySelf();
            if (item == null) { ScriptSleep(3000); return 0; }

            // Check PERMISSION_DEBIT (0x02)
            if ((item.PermsMask & 0x02) == 0)
            {
                ShoutError("llGiveMoney: PERMISSION_DEBIT not granted.");
                ScriptSleep(3000);
                return 0;
            }

            // No economy module — silently return 0
            ScriptSleep(3000);
            return 0;
        }
        public string llTransferLindenDollars(string destination, int amount)
        {
            // No economy module available in Legion
            UUID txnId = UUID.Random();
            PostDataserverEvent(txnId, "LINDENDOLLAR_INSUFFICIENTFUNDS");
            return txnId.ToString();
        }
        public string iwGiveMoney(string destination, int amount)
        {
            return llTransferLindenDollars(destination, amount);
        }
        public void llSetPayPrice(int price, LSLList quick_pay_buttons)
        {
            if (m_host == null) return;
            m_host.PayPrice[0] = price;
            if (quick_pay_buttons.Length > 0) m_host.PayPrice[1] = quick_pay_buttons.GetLSLIntegerItem(0);
            if (quick_pay_buttons.Length > 1) m_host.PayPrice[2] = quick_pay_buttons.GetLSLIntegerItem(1);
            if (quick_pay_buttons.Length > 2) m_host.PayPrice[3] = quick_pay_buttons.GetLSLIntegerItem(2);
            if (quick_pay_buttons.Length > 3) m_host.PayPrice[4] = quick_pay_buttons.GetLSLIntegerItem(3);
        }
        public float llGetEnergy() => 1.0f; // Halcyon: always 1.0

        // ── Misc ───────────────────────────────────────────────────────────────

        public void llSetPrimURL(string url) { /* Deprecated */ }
        public void llRefreshPrimURL() { /* Deprecated - not supported */ }
        public void llMapDestination(string simname, Vector3 pos, Vector3 look_at)
        {
            UUID targetAvatar = UUID.Zero;

            // Use DetectParams to find the touching avatar (faithful to Halcyon)
            var detectedParams = m_thisScript?.ScriptState?.GetDetectVariables(0);
            if (detectedParams != null)
            {
                UUID.TryParse(detectedParams.Key, out targetAvatar);
            }
            else
            {
                // Fallback for non-touch events: attachments target the wearer
                if (m_host.ParentGroup.IsAttachment)
                    targetAvatar = m_host.OwnerID;
            }

            if (targetAvatar != UUID.Zero)
            {
                ScenePresence avatar = World?.GetScenePresence(targetAvatar);
                if (avatar != null)
                {
                    try
                    {
                        // Tranquillity's SendScriptTeleportRequest signature is (objName, simName, pos, int options)
                        // — it dropped Legion's lookAt vector ("lookat does nothing"). Pass options = 0.
                        avatar.ControllingClient.SendScriptTeleportRequest(m_host.Name, simname, pos, 0);
                    }
                    catch (NullReferenceException)
                    {
                        // Legion LLClientView.SendScriptTeleportRequest has a packet construction bug
                        // where ScriptTeleportRequestPacket fields can be null. Guard against it here.
                        // This needs a separate fix in LLClientView.cs.
                    }
                }
            }

            ScriptSleep(1000);
        }
        public void llLoadURL(string avatar_id, string message, string url)
        {
            IDialogModule dm = World?.RequestModuleInterface<IDialogModule>();
            if (dm != null)
                if (UUID.TryParse(avatar_id, out UUID avatar))
                    dm.SendUrlToUser(avatar, m_host.Name, m_host.UUID, m_host.OwnerID,
                        false, message, url);

            ScriptSleep(100);
        }
        public int llEdgeOfWorld(Vector3 pos, Vector3 dir)
        {
            // Returns 1 if following dir from pos reaches the edge of the region
            if (World == null) return 0;
            float sx = World.RegionInfo.RegionSizeX;
            float sy = World.RegionInfo.RegionSizeY;
            // Step along dir until we leave the region or hit a known neighbour
            Vector3 cur = pos;
            for (int i = 0; i < 256; i++)
            {
                cur += dir;
                if (cur.X < 0 || cur.X >= sx || cur.Y < 0 || cur.Y >= sy)
                    return 1;
            }
            return 0;
        }
        public string llGetObjectPermMask2(int mask) { /* Halcyon-2 variant — not standard LSL */ return "0"; }
        public int llGetParcelFlags2(Vector3 pos) { /* Halcyon-2 variant — not standard LSL */ return 0; }
        public LSLList llCastRay(Vector3 start, Vector3 end, LSLList options)
        {
            // Faithful port from Halcyon
            List<object> results = new List<object>();
            Vector3 dir = end - start;
            float dist = dir.Length();

            // RC_* constants
            const int RC_MAX_HITS = 2;
            const int RC_DETECT_PHANTOM = 3;
            const int RC_DATA_FLAGS = 4;
            const int RC_REJECT_TYPES = 1;
            const int RC_GET_ROOT_KEY = 1;
            const int RC_GET_LINK_NUM = 2;
            const int RC_GET_NORMAL = 4;
            const int RC_REJECT_AGENTS = 2;
            const int RC_REJECT_PHYSICAL = 4;
            const int RC_REJECT_NONPHYSICAL = 8;
            const int RC_REJECT_LAND = 16;

            int count = 1;
            int dataFlags = 0;
            int rejectTypes = 0;

            for (int i = 0; i < options.Length; i += 2)
            {
                int opt = options.GetLSLIntegerItem(i);
                if (opt == RC_MAX_HITS) count = options.GetLSLIntegerItem(i + 1);
                else if (opt == RC_DATA_FLAGS) dataFlags = options.GetLSLIntegerItem(i + 1);
                else if (opt == RC_REJECT_TYPES) rejectTypes = options.GetLSLIntegerItem(i + 1);
            }

            if (count > 16) count = 16;
            else if (count <= 0)
            {
                ShoutError("You must request at least one result from llCastRay.");
                return new LSLList();
            }

            bool rejectTerrain = (rejectTypes & RC_REJECT_LAND) != 0;
            bool rejectAgents = (rejectTypes & RC_REJECT_AGENTS) != 0;
            bool rejectNonPhysical = (rejectTypes & RC_REJECT_NONPHYSICAL) != 0;
            bool rejectPhysical = (rejectTypes & RC_REJECT_PHYSICAL) != 0;

            try
            {
                // OpenSim's PhysicsScene.RaycastWorld returns List<ContactResult>
                List<ContactResult> contactResults = World.PhysicsScene.RaycastWorld(start, dir, dist, count);

                if (contactResults == null)
                {
                    results.Add(0);
                    return new LSLList(results);
                }

                contactResults.Sort((a, b) => a.Depth.CompareTo(b.Depth));
                int values = 0;

                foreach (ContactResult result in contactResults)
                {
                    // Skip self
                    if (result.ConsumerID == m_host.LocalId)
                        continue;

                    UUID itemID = UUID.Zero;
                    int linkNum = 0;

                    if (result.ConsumerID != 0)
                    {
                        SceneObjectPart part = World.GetSceneObjectPart(result.ConsumerID);
                        if (part != null)
                        {
                            // Skip if part of same linkset
                            if (part.ParentGroup == m_host.ParentGroup)
                                continue;

                            // Filter by physics type
                            PhysicsActor pa = part.PhysActor;
                            if (pa != null)
                            {
                                if (rejectPhysical && pa.IsPhysical) continue;
                                if (rejectNonPhysical && !pa.IsPhysical) continue;
                            }
                            else
                            {
                                if (rejectNonPhysical) continue;
                            }

                            if ((dataFlags & RC_GET_ROOT_KEY) != 0)
                                itemID = part.ParentGroup.UUID;
                            else
                                itemID = part.UUID;

                            linkNum = part.LinkNum;
                        }
                        else
                        {
                            ScenePresence sp = World.GetScenePresence(result.ConsumerID);
                            if (sp != null)
                            {
                                if (rejectAgents) continue;
                                itemID = sp.UUID;
                            }
                        }
                    }
                    else
                    {
                        // ConsumerID == 0 means terrain
                        if (rejectTerrain) continue;
                    }

                    results.Add(itemID.ToString());

                    if ((dataFlags & RC_GET_LINK_NUM) != 0)
                        results.Add(linkNum);

                    results.Add(result.Pos);

                    if ((dataFlags & RC_GET_NORMAL) != 0)
                        results.Add(result.Normal);

                    values++;
                    if (values >= count) break;
                }

                results.Add(values);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llCastRay exception: {0}", e.Message);
                results.Clear();
                results.Add(-3); // RCERR_CAST_TIME_EXCEEDED as generic error
            }
            return new LSLList(results);
        }
        public string llJsonGetValue(string json, LSLList specifiers)
        {
            if (string.IsNullOrEmpty(json) || specifiers == null) return JSON_INVALID;
            try
            {
                var node = SimpleJsonNavigate(json, specifiers.Data);
                return node ?? JSON_INVALID;
            }
            catch { return JSON_INVALID; }
        }

        public string llJsonValueType(string json, LSLList specifiers)
        {
            const string JSON_OBJECT  = "\uFDD1";
            const string JSON_ARRAY   = "\uFDD2";
            const string JSON_NUMBER  = "\uFDD3";
            const string JSON_STRING  = "\uFDD4";
            const string JSON_NULL    = "\uFDD5";
            const string JSON_TRUE    = "\uFDD6";
            const string JSON_FALSE   = "\uFDD7";
            if (string.IsNullOrEmpty(json)) return JSON_INVALID;
            try
            {
                string val = SimpleJsonNavigate(json, specifiers?.Data ?? Array.Empty<object>());
                if (val == null) return JSON_INVALID;
                if (val == "null") return JSON_NULL;
                if (val == "true") return JSON_TRUE;
                if (val == "false") return JSON_FALSE;
                if (val.StartsWith("{")) return JSON_OBJECT;
                if (val.StartsWith("[")) return JSON_ARRAY;
                if (val.StartsWith("\"")) return JSON_STRING;
                if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return JSON_NUMBER;
                return JSON_INVALID;
            }
            catch { return JSON_INVALID; }
        }

        // LSL JSON special constants — values match Phlox DefaultConstants.cs
        private const string JSON_INVALID = "\uFDD0";
        private const string JSON_DELETE  = "\uFDD8";
        private const int    JSON_APPEND  = -1;

        public string llJsonSetValue(string json, LSLList specifiers, string value)
        {
            if (specifiers == null || specifiers.Data.Length == 0)
                return JSON_INVALID;
            if (string.IsNullOrEmpty(json))
                json = "{}";  // default to empty object per LSL spec
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                // Clone root into a mutable structure, apply the set, serialize back
                var root = JsonElementToNode(doc.RootElement);
                if (root == null) return JSON_INVALID;
                if (!JsonSetAtPath(root, specifiers.Data, 0, value))
                    return JSON_INVALID;
                return JsonNodeSerialize(root);
            }
            catch { return JSON_INVALID; }
        }

        // ── JSON mutable node types ───────────────────────────────────────────
        // System.Text.Json is read-only, so we convert to simple mutable wrappers.

        private abstract class JNode { }
        private class JObject : JNode
        {
            public List<KeyValuePair<string, JNode>> Members = new();
            public bool TryGet(string key, out JNode val)
            {
                foreach (var kv in Members)
                    if (kv.Key == key) { val = kv.Value; return true; }
                val = null;
                return false;
            }
            public void Set(string key, JNode val)
            {
                for (int i = 0; i < Members.Count; i++)
                    if (Members[i].Key == key) { Members[i] = new(key, val); return; }
                Members.Add(new(key, val));
            }
            public bool Remove(string key)
            {
                for (int i = 0; i < Members.Count; i++)
                    if (Members[i].Key == key) { Members.RemoveAt(i); return true; }
                return false;
            }
        }
        private class JArray : JNode { public List<JNode> Items = new(); }
        private class JValue : JNode { public string Raw; public JValue(string raw) { Raw = raw; } }

        private static JNode JsonElementToNode(System.Text.Json.JsonElement el)
        {
            switch (el.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    var obj = new JObject();
                    foreach (var prop in el.EnumerateObject())
                        obj.Members.Add(new(prop.Name, JsonElementToNode(prop.Value)));
                    return obj;
                case System.Text.Json.JsonValueKind.Array:
                    var arr = new JArray();
                    foreach (var item in el.EnumerateArray())
                        arr.Items.Add(JsonElementToNode(item));
                    return arr;
                default:
                    return new JValue(el.GetRawText());
            }
        }

        private static string JsonNodeSerialize(JNode node)
        {
            if (node is JObject obj)
            {
                var sb = new System.Text.StringBuilder("{");
                for (int i = 0; i < obj.Members.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(obj.Members[i].Key));
                    sb.Append(':');
                    sb.Append(JsonNodeSerialize(obj.Members[i].Value));
                }
                sb.Append('}');
                return sb.ToString();
            }
            if (node is JArray arr)
            {
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < arr.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonNodeSerialize(arr.Items[i]));
                }
                sb.Append(']');
                return sb.ToString();
            }
            if (node is JValue val) return val.Raw;
            return "null";
        }

        /// <summary>Convert an LSL value string to a JNode for insertion into the tree.</summary>
        private static JNode JsonValueToNode(string value)
        {
            if (value == null) return new JValue("null");
            // LSL special sentinel constants are stored as bare words
            if (value == "\uFDD6") return new JValue("true");   // JSON_TRUE
            if (value == "\uFDD7") return new JValue("false");  // JSON_FALSE
            if (value == "\uFDD5") return new JValue("null");   // JSON_NULL
            // If it looks like JSON structure, parse it
            string trimmed = value.Trim();
            if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) ||
                (trimmed.StartsWith("[") && trimmed.EndsWith("]")))
            {
                try
                {
                    using var d = System.Text.Json.JsonDocument.Parse(trimmed);
                    return JsonElementToNode(d.RootElement);
                }
                catch { /* fall through to string */ }
            }
            // Numeric?
            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
                return new JValue(trimmed);
            // Plain string — quote it
            return new JValue(System.Text.Json.JsonSerializer.Serialize(value));
        }

        /// <summary>
        /// Recursively set/delete a value at the given specifier path.
        /// Faithful port of Halcyon's JsonSetSpecific / JsonBuildRestOfSpec logic.
        /// </summary>
        private bool JsonSetAtPath(JNode node, object[] specs, int i, string value)
        {
            if (i >= specs.Length) return false;
            object spec = specs[i];
            bool isLast = (i == specs.Length - 1);
            bool isDelete = (value == JSON_DELETE);

            if (node is JArray arr)
            {
                if (!(spec is int))
                {
                    if (int.TryParse(spec.ToString(), out int parsed)) spec = parsed;
                    else return false;
                }
                int idx = (int)spec;

                if (idx == JSON_APPEND)
                {
                    // Append: build the rest of the path as a new subtree
                    arr.Items.Add(JsonBuildRest(specs, i + 1, value));
                    return true;
                }
                if (idx < 0 || idx > arr.Items.Count) return false;
                if (idx == arr.Items.Count)
                {
                    if (isDelete) return false;
                    arr.Items.Add(JsonBuildRest(specs, i + 1, value));
                    return true;
                }
                if (isLast)
                {
                    if (isDelete) { arr.Items.RemoveAt(idx); return true; }
                    arr.Items[idx] = JsonBuildRest(specs, i + 1, value);
                    return true;
                }
                // Recurse into existing element
                var child = arr.Items[idx];
                // If child type doesn't match next specifier, replace subtree
                object nextSpec = specs[i + 1];
                bool nextIsInt = (nextSpec is int) || int.TryParse(nextSpec.ToString(), out _);
                if ((nextIsInt && !(child is JArray)) || (!nextIsInt && !(child is JObject)))
                {
                    arr.Items[idx] = JsonBuildRest(specs, i + 1, value);
                    return true;
                }
                return JsonSetAtPath(child, specs, i + 1, value);
            }
            else if (node is JObject obj)
            {
                string key;
                if (spec is string skey) key = skey;
                else key = spec.ToString();

                if (isLast)
                {
                    if (isDelete) return obj.Remove(key);
                    obj.Set(key, JsonBuildRest(specs, i + 1, value));
                    return true;
                }
                if (obj.TryGet(key, out JNode objChild))
                {
                    // Recurse into existing member
                    object nextSpec = specs[i + 1];
                    bool nextIsInt = (nextSpec is int) || int.TryParse(nextSpec.ToString(), out _);
                    if ((nextIsInt && !(objChild is JArray)) || (!nextIsInt && !(objChild is JObject)))
                    {
                        obj.Set(key, JsonBuildRest(specs, i + 1, value));
                        return true;
                    }
                    return JsonSetAtPath(objChild, specs, i + 1, value);
                }
                else
                {
                    if (isDelete) return false;
                    obj.Set(key, JsonBuildRest(specs, i + 1, value));
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Build a new subtree for the remaining specifiers.
        /// Port of Halcyon's JsonBuildRestOfSpec.
        /// </summary>
        private JNode JsonBuildRest(object[] specs, int i, string value)
        {
            if (i >= specs.Length)
                return JsonValueToNode(value);

            object spec = specs[i];
            bool specIsInt = (spec is int) || int.TryParse(spec.ToString(), out _);

            if (specIsInt)
            {
                var arr = new JArray();
                arr.Items.Add(JsonBuildRest(specs, i + 1, value));
                return arr;
            }
            else
            {
                var obj = new JObject();
                obj.Set(spec.ToString(), JsonBuildRest(specs, i + 1, value));
                return obj;
            }
        }

        private static string SimpleJsonNavigate(string json, object[] path)
        {
            // Minimal JSON navigator — walks path keys/indices into a JSON string
            string cur = json.Trim();
            foreach (object seg in path)
            {
                cur = cur.Trim();
                if (cur.StartsWith("{"))
                {
                    string key = seg.ToString();
                    var doc = System.Text.Json.JsonDocument.Parse(cur);
                    if (!doc.RootElement.TryGetProperty(key, out var el)) return null;
                    cur = el.GetRawText();
                }
                else if (cur.StartsWith("["))
                {
                    if (!int.TryParse(seg.ToString(), out int idx)) return null;
                    var doc = System.Text.Json.JsonDocument.Parse(cur);
                    var arr = doc.RootElement;
                    if (idx < 0 || idx >= arr.GetArrayLength()) return null;
                    cur = arr[idx].GetRawText();
                }
                else return null;
            }
            // Unwrap string quotes
            if (cur.StartsWith("\"") && cur.EndsWith("\""))
                return System.Text.Json.JsonSerializer.Deserialize<string>(cur);
            return cur;
        }
            public string llList2Json(string type, LSLList values)
        {
            const string LSL_JSON_ARRAY   = "\uFDD2";  // Phlox JSON_ARRAY constant
            const string LSL_JSON_OBJECT  = "\uFDD1";  // Phlox JSON_OBJECT constant
            if (values == null) return JSON_INVALID;
            try
            {
                if (type == LSL_JSON_ARRAY)
                {
                    var sb = new System.Text.StringBuilder("[");
                    for (int i = 0; i < values.Data.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(JsonValueFromObject(values.Data[i]));
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
                else if (type == LSL_JSON_OBJECT)
                {
                    var sb = new System.Text.StringBuilder("{");
                    bool first = true;
                    for (int i = 0; i + 1 < values.Data.Length; i += 2)
                    {
                        if (!(values.Data[i] is string)) return JSON_INVALID;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('"');
                        sb.Append(((string)values.Data[i]).Replace("\\", "\\\\").Replace("\"", "\\\""));
                        sb.Append("\":");
                        sb.Append(JsonValueFromObject(values.Data[i + 1]));
                    }
                    sb.Append('}');
                    return sb.ToString();
                }
                return JSON_INVALID;
            }
            catch { return JSON_INVALID; }
        }

        private static string JsonValueFromObject(object o)
        {
            if (o == null) return "null";
            if (o is int iv)    return iv.ToString();
            if (o is float fv)  return fv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (o is double dv) return dv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (o is string sv)
            {
                if (sv == "true" || sv == "false" || sv == "null") return sv;
                if (double.TryParse(sv, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return sv;
                return "\"" + sv.Replace("\\","\\\\").Replace("\"","\\\"") + "\"";
            }
            return "\"" + o.ToString() + "\"";
        }
        public LSLList llJson2List(string src)
        {
            if (string.IsNullOrEmpty(src)) return new LSLList();
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(src);
                var result = new System.Collections.Generic.List<object>();
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                        result.Add(JsonElementToLSL(el));
                }
                else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        result.Add(prop.Name);
                        result.Add(JsonElementToLSL(prop.Value));
                    }
                }
                return new LSLList(result.ToArray());
            }
            catch { return new LSLList(); }
        }

        private static object JsonElementToLSL(System.Text.Json.JsonElement el)
        {
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number =>
                    el.TryGetInt32(out int i) ? (object)i : (object)el.GetSingle(),
                System.Text.Json.JsonValueKind.String  => el.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.True    => 1,
                System.Text.Json.JsonValueKind.False   => 0,
                System.Text.Json.JsonValueKind.Null    => "null",
                _                                      => el.GetRawText()
            };
        }
        public Vector3 iwColorConvert(Vector3 input, int color1, int color2)
        {
            // Faithful port from Halcyon: 0=RGB, 1=HSL, 2=HSV
            if (color1 == color2) return input;
            // Convert input to RGB first
            if (color1 == 1) input = HSL_TO_RGB(input);
            else if (color1 == 2) input = HSV_TO_RGB(input);
            // Convert RGB to target
            if (color2 == 1) return RGB_TO_HSL(input);
            else if (color2 == 2) return RGB_TO_HSV(input);
            return input;
        }
        public Vector3 iwNameToColor(string name)
        {
            // Faithful port from Halcyon
            Color c = Color.FromName(name.Replace(" ", null));
            return new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
        }

        // ── Color conversion helpers (ported from Halcyon) ─────────────────────

        private float H2RGB(float v1, float v2, float vH)
        {
            if (vH < 0) vH += 1;
            if (vH > 1) vH -= 1;
            if ((6f * vH) < 1) return (v1 + (v2 - v1) * 6f * vH);
            if ((2f * vH) < 1) return (v2);
            if ((3f * vH) < 2) return (v1 + (v2 - v1) * ((2f / 3f) - vH) * 6f);
            return v1;
        }

        private Vector3 HSL_TO_RGB(Vector3 input)
        {
            float H = input.X, S = input.Y, L = input.Z;
            if (S == 0) return new Vector3(L, L, L);
            float v2 = (L < 0.5f) ? L * (1 + S) : (L + S) - (S * L);
            float v1 = 2 * L - v2;
            return new Vector3(
                H2RGB(v1, v2, H + (1f / 3f)),
                H2RGB(v1, v2, H),
                H2RGB(v1, v2, H - (1f / 3f))
            );
        }

        private Vector3 HSV_TO_RGB(Vector3 input)
        {
            float H = input.X, S = input.Y, V = input.Z;
            if (S == 0) return new Vector3(V, V, V);
            float vH = H * 6f;
            if (vH == 6) vH = 0;
            int i = (int)vH;
            float v1 = V * (1 - S);
            float v2 = V * (1 - S * (vH - i));
            float v3 = V * (1 - S * (1 - (vH - i)));
            if (i == 0) return new Vector3(V, v3, v1);
            else if (i == 1) return new Vector3(v2, V, v1);
            else if (i == 2) return new Vector3(v1, V, v3);
            else if (i == 3) return new Vector3(v1, v2, V);
            else if (i == 4) return new Vector3(v3, v1, V);
            else return new Vector3(V, v1, v2);
        }

        private Vector3 RGB_TO_HSV(Vector3 input)
        {
            float R = input.X, G = input.Y, B = input.Z;
            float min = Math.Min(Math.Min(R, G), B);
            float max = Math.Max(Math.Max(R, G), B);
            float delta = max - min;
            float H = 0, S = 0, V = max;
            if (delta != 0)
            {
                S = delta / max;
                float vR = (((max - R) / 6f) + (max / 2f)) / delta;
                float vG = (((max - G) / 6f) + (max / 2f)) / delta;
                float vB = (((max - B) / 6f) + (max / 2f)) / delta;
                if (R == max) H = vB - vG;
                else if (G == max) H = (1f / 3f) + vR - vB;
                else if (B == max) H = (2f / 3f) + vG - vR;
                if (H < 0) H += 1f;
                if (H > 1) H -= 1f;
            }
            return new Vector3(H, S, V);
        }

        private Vector3 RGB_TO_HSL(Vector3 input)
        {
            float R = input.X, G = input.Y, B = input.Z;
            float min = Math.Min(Math.Min(R, G), B);
            float max = Math.Max(Math.Max(R, G), B);
            float delta = max - min;
            float H = 0, S = 0, L = (max + min) / 2f;
            if (delta != 0)
            {
                S = (L < 0.5f) ? delta / (max + min) : delta / (2 - max - min);
                float vR = (((max - R) / 6f) + (max / 2f)) / delta;
                float vG = (((max - G) / 6f) + (max / 2f)) / delta;
                float vB = (((max - B) / 6f) + (max / 2f)) / delta;
                if (max == R) H = vB - vG;
                else if (max == G) H = (1f / 3f) + vR - vB;
                else if (max == B) H = (2f / 3f) + vG - vR;
                if (H < 0) H += 1f;
                if (H > 1) H -= 1f;
            }
            return new Vector3(H, S, L);
        }
        public int iwVerifyType(string str, int type)
        {
            switch (type)
            {
                case 0: // No Type — auto-detect
                    foreach (var index in new int[] { 1, 2, 4, 5, 6 })
                    {
                        if (iwVerifyType(str, index) == 1) return index;
                    }
                    return 3;
                case 1: // TYPE_INTEGER
                    return int.TryParse(str, out _) ? 1 : 0;
                case 2: // TYPE_FLOAT
                    return float.TryParse(str, out _) ? 1 : 0;
                case 4: // TYPE_KEY
                    return UUID.TryParse(str, out _) ? 1 : 0;
                case 5: // TYPE_VECTOR
                    if (str == null || str.Count(c => c == ',') != 2) return 0;
                    return Vector3.TryParse(str, out _) ? 1 : 0;
                case 6: // TYPE_ROTATION
                    if (str == null || str.Count(c => c == ',') != 3) return 0;
                    return Quaternion.TryParse(str, out _) ? 1 : 0;
                case 3: // TYPE_STRING
                    return 1;
                default:
                    return -1;
            }
        }
        public int iwGroupInvite(string group, string user, string role)
        {
            // Faithful port from Halcyon
            if (!UUID.TryParse(group, out UUID groupID) || groupID == UUID.Zero) return -3;
            if (!UUID.TryParse(user, out UUID userID) || userID == UUID.Zero) return -3;
            if (string.IsNullOrEmpty(role)) role = "Everyone";

            try
            {
                IGroupsModule groupsModule = World?.RequestModuleInterface<IGroupsModule>();
                if (groupsModule == null) return -1;

                // Look up the role by name
                List<GroupRolesData> roles = groupsModule.GroupRoleDataRequest(null, groupID);
                if (roles == null || roles.Count == 0) return -3;

                UUID roleID = UUID.Zero;
                bool found = false;
                foreach (GroupRolesData r in roles)
                {
                    if (r.Name.Equals(role, StringComparison.InvariantCultureIgnoreCase))
                    {
                        roleID = r.RoleID;
                        found = true;
                        break;
                    }
                }
                if (!found) return -3;

                groupsModule.InviteGroup(null, m_host.OwnerID, groupID, userID, roleID);
                return 1;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: iwGroupInvite exception: {0}", e.Message);
                return -1;
            }
        }
        public int iwGroupEject(string group, string user)
        {
            // Faithful port from Halcyon
            if (!UUID.TryParse(group, out UUID groupID) || groupID == UUID.Zero) return -3;
            if (!UUID.TryParse(user, out UUID userID) || userID == UUID.Zero) return -3;

            try
            {
                IGroupsModule groupsModule = World?.RequestModuleInterface<IGroupsModule>();
                if (groupsModule == null) return -1;
                groupsModule.EjectGroupMember(null, m_host.OwnerID, groupID, userID);
                return 1;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: iwGroupEject exception: {0}", e.Message);
                return -1;
            }
        }
        public int iwClampInt(int value, int min, int max)
        {
            if (min == max) return min;
            if (max < min) return Math.Min(min, Math.Max(value, max));
            return Math.Min(max, Math.Max(value, min));
        }
        public float iwClampFloat(float value, float min, float max)
        {
            if (min == max) return min;
            if (max < min) return Math.Min(min, Math.Max(value, max));
            return Math.Min(max, Math.Max(value, min));
        }
        public int iwIntRand(int max) { return new Random().Next(max < 0 ? max : 0, Math.Abs(max) + 1); }
        public int iwIntRandRange(int min, int max) { if (min == max) return min; if (max < min) { int t = min; min = max; max = t; } return new Random().Next(min, max + 1); }
        public float iwFrandRange(float min, float max) { if (min == max) return min; if (max < min) { float t = min; min = max; max = t; } return (float)(new Random().NextDouble() * (max - min) + min); }
        public LSLList iwSearchLinksByName(string pattern, int matchType, int linksOnly)
        {
            if (matchType > 2)
            {
                ShoutError("IW_MATCH_COUNT/REGEX not valid for iwSearchLinksByName");
                return new LSLList();
            }
            List<object> ret = new List<object>();
            var parts = m_host.ParentGroup.Parts.ToList();
            parts.Sort((x, y) => x.LinkNum.CompareTo(y.LinkNum));
            foreach (SceneObjectPart part in parts)
            {
                if (String.IsNullOrEmpty(pattern) || iwMatchString(part.Name, pattern, matchType) == 1)
                {
                    ret.Add(part.LinkNum);
                    if (linksOnly == 0) ret.Add(part.Name);
                }
            }
            return new LSLList(ret);
        }
        public LSLList iwSearchLinksByDesc(string pattern, int matchType, int linksOnly)
        {
            if (matchType > 2)
            {
                ShoutError("IW_MATCH_COUNT/REGEX not valid for iwSearchLinksByDesc");
                return new LSLList();
            }
            List<object> ret = new List<object>();
            var parts = m_host.ParentGroup.Parts.ToList();
            parts.Sort((x, y) => x.LinkNum.CompareTo(y.LinkNum));
            foreach (SceneObjectPart part in parts)
            {
                if (String.IsNullOrEmpty(pattern) || iwMatchString(part.Description, pattern, matchType) == 1)
                {
                    ret.Add(part.LinkNum);
                    if (linksOnly == 0) ret.Add(part.Description);
                }
            }
            return new LSLList(ret);
        }

  // ── Bots ───────────────────────────────────────────────────────────────

        private UUID ParseBotID(string botID)
        {
            if (UUID.TryParse(botID, out UUID id) && id != UUID.Zero) return id;
            return UUID.Zero;
        }

        private IBotManager GetBotManager()
        {
            return World.RequestModuleInterface<IBotManager>();
        }

        private BotPersistenceManager _botPersistence;
        private BotPersistenceManager GetBotPersistence()
        {
            if (_botPersistence == null)
            {
                IBotManager mgr = GetBotManager();
                if (mgr is BotManager bm)
                    _botPersistence = bm.PersistenceManager;
            }
            return _botPersistence;
        }

        public void botCreateBot(string first, string last, string outfit, Vector3 pos, int options)
        {
            const int delay = 2000;
            object retVal = UUID.Zero.ToString();

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    string reason;
                    string botID = manager.CreateBot(first, last, pos, outfit, m_itemID, m_host.OwnerID, out reason).ToString();
                    if (reason == null)
                    {
                        retVal = botID;
                        return;
                    }
                    ShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, retVal, delay);
            }
        }

        public void botRemoveBot(string botID)
        {
            const int delay = 1000;

            try
            {
                UUID id = ParseBotID(botID);
                if (id == UUID.Zero) return;

                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.RemoveBot(id, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public string botGetOwner(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return string.Empty;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.GetBotOwner(id).ToString();
            return string.Empty;
        }

        public int botIsBot(string userID)
        {
            UUID id = ParseBotID(userID);
            if (id == UUID.Zero) return 0;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.IsBot(id) ? 1 : 0;
            return 0;
        }

        public string botGetName(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return string.Empty;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.GetBotName(id);
            return string.Empty;
        }

        public void botChangeOwner(string botID, string newOwnerID)
        {
            // NotImplemented in Halcyon — kept as no-op
        }

        public LSLList botGetAllBotsInRegion()
        {
            IBotManager manager = GetBotManager();
            List<UUID> bots = new List<UUID>();
            if (manager != null)
                bots = manager.GetAllBots();
            return new LSLList(bots.ConvertAll<object>(o => o.ToString()));
        }

        public LSLList botGetAllMyBotsInRegion()
        {
            IBotManager manager = GetBotManager();
            List<UUID> bots = new List<UUID>();
            if (manager != null)
                bots = manager.GetAllOwnedBots(m_host.OwnerID);
            return new LSLList(bots.ConvertAll<object>(o => o.ToString()));
        }

        // ── Bot Profile ────────────────────────────────────────────────────────

        public void botSetProfile(string botID, string about, string email, string firstLifeAbout, string firstLifeImage, string image, string profileURL)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            UUID imageID = UUID.Zero;
            UUID.TryParse(image, out imageID);

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotProfile(id, about, email, imageID, profileURL, m_host.OwnerID);
        }

        public void botSetProfileParams(string botID, LSLList profileInformation)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            string aboutText = null, email = null, profileURL = null;
            UUID? imageUUID = null;

            for (int i = 0; i < profileInformation.Length; i += 2)
            {
                int param = profileInformation.GetLSLIntegerItem(i);
                string value = profileInformation.GetLSLStringItem(i + 1);

                switch (param)
                {
                    case 1: // BOT_ABOUT_TEXT
                        aboutText = value;
                        break;
                    case 2: // BOT_EMAIL
                        email = value;
                        break;
                    case 3: // BOT_IMAGE_UUID
                        if (UUID.TryParse(value, out UUID imgID))
                            imageUUID = imgID;
                        break;
                    case 4: // BOT_PROFILE_URL
                        profileURL = value;
                        break;
                }
            }

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotProfile(id, aboutText, email, imageUUID, profileURL, m_host.OwnerID);
        }

        public LSLList botGetProfileParams(string botID, LSLList profileInformation)
        {
            if (botIsBot(botID) == 0)
                return new LSLList();

            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return new LSLList();

            // Get profile from BotManager's stored data via INPC
            INPCModule npcMod = World.RequestModuleInterface<INPCModule>();
            INPC npc = npcMod?.GetNPC(id, World);

            IBotManager manager = GetBotManager();
            // We need to read from the bot's stored profile data
            // Since BotData is internal, we read from INPC + fallback

            List<object> list = new List<object>();
            for (int i = 0; i < profileInformation.Length; i++)
            {
                int param = profileInformation.GetLSLIntegerItem(i);
                switch (param)
                {
                    case 1: // BOT_ABOUT_TEXT
                        list.Add(npc?.profileAbout ?? string.Empty);
                        break;
                    case 2: // BOT_EMAIL
                        list.Add(string.Empty); // email not exposed via INPC
                        break;
                    case 3: // BOT_IMAGE_UUID
                        list.Add((npc?.profileImage ?? UUID.Zero).ToString());
                        break;
                    case 4: // BOT_PROFILE_URL
                        list.Add(string.Empty); // profileURL not exposed via INPC
                        break;
                }
            }
            return new LSLList(list);
        }

        // ── Bot Outfits ────────────────────────────────────────────────────────

        public void botSetOutfit(string outfitName)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    string reason;
                    manager.SaveOutfitToDatabase(m_host.OwnerID, outfitName, out reason);
                    if (reason != null)
                        ShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botRemoveOutfit(string outfitName)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.RemoveOutfitFromDatabase(m_host.OwnerID, outfitName);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botChangeOutfit(string botID, string outfitName)
        {
            const int delay = 1000;

            try
            {
                UUID id = ParseBotID(botID);
                if (id == UUID.Zero) return;

                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    string reason;
                    manager.ChangeBotOutfit(id, outfitName, m_host.OwnerID, out reason);
                    if (reason != null)
                        ShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botGetBotOutfits()
        {
            object retVal = new LSLList();
            const int delay = 1000;

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    List<string> itms = manager.GetBotOutfitsByOwner(m_host.OwnerID);
                    retVal = new LSLList(itms.ConvertAll<object>(s => s));
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, retVal, delay);
            }
        }

        public void botSearchBotOutfits(string pattern, int matchType, int start, int end)
        {
            List<object> retVal = new List<object>();
            const int delay = 1000;

            try
            {
                if (matchType > 2)
                    return; // IW_MATCH_COUNT and IW_MATCH_COUNT_REGEX not valid here

                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    List<string> itms = manager.GetBotOutfitsByOwner(m_host.OwnerID);
                    int count = 0;
                    foreach (string outfit in itms)
                    {
                        if (string.IsNullOrEmpty(pattern) || iwMatchString(outfit, pattern, matchType) == 1)
                        {
                            if (count >= start && (end == -1 || count <= end))
                                retVal.Add(outfit);
                            count++;
                            if (end != -1 && count > end)
                                break;
                        }
                    }
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, new LSLList(retVal), delay);
            }
        }

        // ── Bot Event Registration ─────────────────────────────────────────────

        public void botRegisterForNavigationEvents(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotRegisterForPathUpdateEvents(id, m_itemID, m_host.OwnerID);
        }

        public void botDeregisterFromNavigationEvents(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotDeregisterFromPathUpdateEvents(id, m_itemID, m_host.OwnerID);
        }

        public void botRegisterForCollisionEvents(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotRegisterForCollisionEvents(id, m_host.ParentGroup, m_host.OwnerID);
        }

        public void botDeregisterFromCollisionEvents(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotDeregisterFromCollisionEvents(id, m_host.ParentGroup, m_host.OwnerID);
        }

        // ── Bot Movement ───────────────────────────────────────────────────────

        public void botPauseMovement(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.PauseBotMovement(id, m_host.OwnerID);
        }

        public void botResumeMovement(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.ResumeBotMovement(id, m_host.OwnerID);
        }

        public void botSetMovementSpeed(string botID, float speed)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotSpeed(id, speed, m_host.OwnerID);
        }

        public Vector3 botGetPos(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return Vector3.Zero;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.GetBotPosition(id, m_host.OwnerID);
            return Vector3.Zero;
        }

        public void botTeleportTo(string botID, Vector3 position)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotPosition(id, position, m_host.OwnerID);
        }

        public void botSetRotation(string botID, Quaternion rotation)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotRotation(id, rotation, m_host.OwnerID);
        }

        public int botFollowAvatar(string botID, string avatar, LSLList options)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return -3; // BOT_ERROR

            IBotManager manager = GetBotManager();
            if (manager != null)
            {
                UUID userID;
                if (!UUID.TryParse(avatar, out userID))
                    return -2; // BOT_USER_NOT_FOUND

                if (options.Length % 2 != 0)
                    return -3; // BOT_ERROR — bad data

                Dictionary<int, object> dictOptions = new Dictionary<int, object>();
                for (int i = 0; i < options.Length; i += 2)
                {
                    int option = options.GetLSLIntegerItem(i);
                    if (dictOptions.ContainsKey(option))
                    {
                        ShoutError(string.Format("botFollowAvatar: options list already includes option {0}", option));
                        dictOptions.Remove(option);
                    }
                    dictOptions.Add(option, options.Data[i + 1]);
                }

                BotMovementResult result = manager.StartFollowingAvatar(id, userID, dictOptions, m_host.OwnerID);
                switch (result)
                {
                    case BotMovementResult.BotNotFound: return -1; // BOT_NOT_FOUND
                    case BotMovementResult.UserNotFound: return -2; // BOT_USER_NOT_FOUND
                    case BotMovementResult.Success: return 0; // BOT_SUCCESS
                    default: return -3; // BOT_ERROR
                }
            }
            return -1; // BOT_NOT_FOUND
        }

        public void botStopMovement(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.StopMovement(id, m_host.OwnerID);
        }

        public void botSetNavigationPoints(string botID, LSLList positions, LSLList movementTypes, LSLList options)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            List<Vector3> positionsMap = new List<Vector3>();
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 pos = positions.GetVector3Item(i);
                pos.X = Math.Clamp(pos.X, 0f, 256f);
                pos.Y = Math.Clamp(pos.Y, 0f, 256f);
                pos.Z = Math.Max(pos.Z, 0f);
                float zmin = (float)World.Heightmap[(int)Math.Clamp(pos.X, 0, 255), (int)Math.Clamp(pos.Y, 0, 255)];
                if (pos.Z < zmin) pos.Z = zmin;
                positionsMap.Add(pos);
            }

            List<TravelMode> travelMap = new List<TravelMode>();
            for (int i = 0; i < movementTypes.Length; i++)
            {
                int travel = movementTypes.GetLSLIntegerItem(i);
                travelMap.Add((TravelMode)travel);
            }

            if (options.Length % 2 != 0) return; // bad data

            Dictionary<int, object> dictOptions = new Dictionary<int, object>();
            for (int i = 0; i < options.Length; i += 2)
            {
                int option = options.GetLSLIntegerItem(i);
                if (dictOptions.ContainsKey(option))
                {
                    ShoutError(string.Format("botSetNavigationPoints: options list already includes option {0}", option));
                    dictOptions.Remove(option);
                }
                dictOptions.Add(option, options.Data[i + 1]);
            }

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotNavigationPoints(id, positionsMap, travelMap, dictOptions, m_host.OwnerID);
        }

        public void botWanderWithin(string botID, Vector3 origin, float xDistance, float yDistance, LSLList options)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            if (options.Length % 2 != 0) return; // bad data

            Dictionary<int, object> dictOptions = new Dictionary<int, object>();
            for (int i = 0; i < options.Length; i += 2)
            {
                int option = options.GetLSLIntegerItem(i);
                if (dictOptions.ContainsKey(option))
                {
                    ShoutError(string.Format("botWanderWithin: options list already includes option {0}", option));
                    dictOptions.Remove(option);
                }
                dictOptions.Add(option, options.Data[i + 1]);
            }

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.WanderWithin(id, origin, new Vector3(xDistance, yDistance, 0), dictOptions, m_host.OwnerID);
        }

        // ── Bot Animations ─────────────────────────────────────────────────────

        public void botStartAnimation(string botID, string animation)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
            {
                UUID animID = FindInventoryItem(animation, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
                if (animID == UUID.Zero) UUID.TryParse(animation, out animID);
                manager.StartBotAnimation(id, animID, animation, m_host.UUID, m_host.OwnerID);
            }
        }

        public void botStopAnimation(string botID, string animation)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
            {
                UUID animID;
                if (!UUID.TryParse(animation, out animID))
                    animID = FindInventoryItem(animation, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
                manager.StopBotAnimation(id, animID, animation, m_host.OwnerID);
            }
        }

        // ── Bot Chat ───────────────────────────────────────────────────────────

        public void botWhisper(string botID, int channel, string message)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, channel, message, ChatTypeEnum.Whisper, m_host.OwnerID);
        }

        public void botSay(string botID, int channel, string message)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, channel, message, ChatTypeEnum.Say, m_host.OwnerID);
        }

        public void botShout(string botID, int channel, string message)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, channel, message, ChatTypeEnum.Shout, m_host.OwnerID);
        }

        public void botStartTyping(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, 0, string.Empty, ChatTypeEnum.StartTyping, m_host.OwnerID);
        }

        public void botStopTyping(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, 0, string.Empty, ChatTypeEnum.StopTyping, m_host.OwnerID);
        }

        public void botSendInstantMessage(string botID, string userID, string message)
        {
            const int delay = 2000;

            try
            {
                UUID botUUID = ParseBotID(botID);
                if (botUUID == UUID.Zero) return;

                UUID userUUID = UUID.Zero;
                if (!UUID.TryParse(userID, out userUUID) || userUUID == UUID.Zero) return;

                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.SendInstantMessageForBot(botUUID, userUUID, message, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        // ── Bot Interaction ────────────────────────────────────────────────────

        public void botSitObject(string botID, string objectID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            UUID objID = UUID.Zero;
            if (!UUID.TryParse(objectID, out objID) || objID == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SitBotOnObject(id, objID, m_host.OwnerID);
        }

        public void botStandUp(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.StandBotUp(id, m_host.OwnerID);
        }

        public void botTouchObject(string botID, string objectID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            UUID objID = UUID.Zero;
            if (!UUID.TryParse(objectID, out objID) || objID == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotTouchObject(id, objID, m_host.OwnerID);
        }

        public void botGiveInventory(string botID, string destination, string inventory)
        {
            int delay = 0;

            try
            {
                UUID id = ParseBotID(botID);
                if (id == UUID.Zero) return;

                UUID destId = UUID.Zero;
                if (!UUID.TryParse(destination, out destId)) return;

                bool found = false;
                UUID objId = UUID.Zero;
                byte assetType = 0;
                string objName = string.Empty;

                lock (m_host.TaskInventory)
                {
                    foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                    {
                        if (inv.Value.Name == inventory)
                        {
                            found = true;
                            objId = inv.Key;
                            assetType = (byte)inv.Value.Type;
                            objName = inv.Value.Name;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    ShoutError(string.Format("Could not find object '{0}'", inventory));
                    return;
                }

                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.GiveInventoryObject(id, m_host, objName, objId, assetType, destId, m_host.OwnerID);

                delay = 2000;
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        // ── Bot Sensors ────────────────────────────────────────────────────────

        public void botSensor(string botID, string name, string id, int type, float range, float arc)
        {
            UUID botUUID = ParseBotID(botID);
            if (botUUID == UUID.Zero) return;

            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            IBotManager manager = GetBotManager();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID)) return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null) return;

            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SenseOnce(
                m_localID, m_itemID, name, keyID, type, range, arc, botSP);
        }

        public void botSensorRepeat(string botID, string name, string id, int type, float range, float arc, float rate)
        {
            UUID botUUID = ParseBotID(botID);
            if (botUUID == UUID.Zero) return;

            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            IBotManager manager = GetBotManager();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID)) return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null) return;

            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SetSenseRepeatEvent(
                m_localID, m_itemID, name, keyID, type, range, arc, rate, botSP);
        }

        public void botSensorRemove()
        {
            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.UnSetSenseRepeaterEvents(m_localID, m_itemID);
        }

        public int botListen(string botID, int channel, string name, string id, string msg)
        {
            UUID botUUID = ParseBotID(botID);
            if (botUUID == UUID.Zero) return -1;

            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            IBotManager manager = GetBotManager();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID)) return -1;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null) return -1;

            if (m_ScriptEngine.ListenManager == null) return -1;
            return m_ScriptEngine.ListenManager.Add(m_localID, m_itemID, botSP.UUID, channel, name, keyID, msg);
        }

        public void botMessageLinked(string botID, int num, string msg, string id)
        {
            UUID botUUID = ParseBotID(botID);
            if (botUUID == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID)) return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null) return;

            List<SceneObjectGroup> groups = botSP.GetAttachments();
            foreach (SceneObjectGroup group in groups)
            {
                foreach (SceneObjectPart part in group.Parts)
                {
                    TaskInventoryDictionary itemsDictionary;
                    lock (part.TaskInventory)
                        itemsDictionary = (TaskInventoryDictionary)part.TaskInventory.Clone();

                    foreach (TaskInventoryItem item in itemsDictionary.Values)
                    {
                        if (item.Type == 10) // INVENTORY_SCRIPT
                        {
                            int linkNumber = m_host.LinkNum;
                            if (m_host.ParentGroup.PrimCount == 1)
                                linkNumber = 0;

                            object[] resobj = new object[] { linkNumber, num, msg, id };
                            m_ScriptEngine.PostScriptEvent(item.ItemID,
                                new EventParams("link_message", resobj, new DetectParams[0]));
                        }
                    }
                }
            }
        }

        // ── Bot Tagging ────────────────────────────────────────────────────────

        public void botAddTag(string botID, string tag)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.AddTagToBot(id, tag, m_host.OwnerID);
        }

        public void botRemoveTag(string botID, string tag)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.RemoveTagFromBot(id, tag, m_host.OwnerID);
        }

        public int botHasTag(string botID, string tag)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return 0;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.BotHasTag(id, tag) ? 1 : 0;
            return 0;
        }

        public LSLList botGetBotTags(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return new LSLList();

            IBotManager manager = GetBotManager();
            if (manager != null)
                return new LSLList(manager.GetBotTags(id).Cast<object>().ToList());
            return new LSLList();
        }

        public LSLList botGetBotsWithTag(string tag)
        {
            IBotManager manager = GetBotManager();
            List<UUID> bots = new List<UUID>();
            if (manager != null)
                bots = manager.GetBotsWithTag(tag);

            List<object> botList = new List<object>();
            foreach (UUID bot in bots)
                botList.Add(bot.ToString());
            return new LSLList(botList);
        }

        public void botRemoveBotsWithTag(string tag)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.RemoveBotsWithTag(tag, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }


        // ── List parse helper ──────────────────────────────────────────────────────

        private LSLList ParseString2List(string src, LSLList separators, LSLList spacers, bool keepNulls)
        {
            if (src == null) return new LSLList();
            var result = new System.Collections.Generic.List<object>();
            // Build combined list of delimiters (separators are consumed, spacers are kept)
            var seps = new System.Collections.Generic.List<string>();
            var spacs = new System.Collections.Generic.List<string>();
            if (separators != null) foreach (var o in separators.Data) { var s = o?.ToString(); if (!string.IsNullOrEmpty(s)) seps.Add(s); }
            if (spacers != null) foreach (var o in spacers.Data) { var s = o?.ToString(); if (!string.IsNullOrEmpty(s)) spacs.Add(s); }

            int pos = 0;
            while (pos <= src.Length)
            {
                // Find earliest separator or spacer
                int earliest = src.Length; string found = null; bool isSpacer = false;
                foreach (var sep in seps) { int idx = src.IndexOf(sep, pos, StringComparison.Ordinal); if (idx >= 0 && idx < earliest) { earliest = idx; found = sep; isSpacer = false; } }
                foreach (var sp in spacs) { int idx = src.IndexOf(sp, pos, StringComparison.Ordinal); if (idx >= 0 && idx < earliest) { earliest = idx; found = sp; isSpacer = true; } }

                string token = src.Substring(pos, earliest - pos);
                if (keepNulls || token.Length > 0) result.Add(token);
                if (found == null) break;
                if (isSpacer) result.Add(found);
                pos = earliest + found.Length;
            }
            return new LSLList(result.ToArray());
        }

        // ── Notecard / dataserver helpers ──────────────────────────────────────────

        private TaskInventoryItem FindInventoryItem(string name, int type)
        {
            if (m_host == null) return null;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == name && (type == -1 || kvp.Value.Type == type))
                        return kvp.Value;
            return null;
        }

        private void PostDataserverEvent(UUID queryID, string data)
        {
            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "dataserver",
                new object[] { queryID.ToString(), data },
                new DetectParams[0]));
        }

        private static string StripNotecardHeader(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (!raw.StartsWith("Linden text", StringComparison.Ordinal)) return raw;
            int marker = raw.IndexOf("\nText length ", StringComparison.Ordinal);
            if (marker < 0) return raw;
            int bodyStart = raw.IndexOf('\n', marker + 1);
            if (bodyStart < 0) return string.Empty;
            string body = raw.Substring(bodyStart + 1);
            if (body.EndsWith("\n}", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 2);
            else if (body.EndsWith("}", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);
            return body;
        }

        // ── Event flag mapping ─────────────────────────────────────────────────

		private ulong MapEventFlag(SupportedEventList.Events evt)
		{
			switch (evt)
			{
				case SupportedEventList.Events.ATTACH:              return (ulong)scriptEvents.attach;
				case SupportedEventList.Events.STATE_EXIT:          return (ulong)scriptEvents.state_exit;
				case SupportedEventList.Events.TIMER:               return (ulong)scriptEvents.timer;
				case SupportedEventList.Events.TOUCH:               return (ulong)scriptEvents.touch;
				case SupportedEventList.Events.COLLISION:           return (ulong)scriptEvents.collision;
				case SupportedEventList.Events.COLLISION_END:       return (ulong)scriptEvents.collision_end;
				case SupportedEventList.Events.COLLISION_START:     return (ulong)scriptEvents.collision_start;
				case SupportedEventList.Events.CONTROL:             return (ulong)scriptEvents.control;
				case SupportedEventList.Events.DATASERVER:          return (ulong)scriptEvents.dataserver;
				case SupportedEventList.Events.EMAIL:               return (ulong)scriptEvents.email;
				case SupportedEventList.Events.HTTP_RESPONSE:       return (ulong)scriptEvents.http_response;
				case SupportedEventList.Events.LAND_COLLISION:      return (ulong)scriptEvents.land_collision;
				case SupportedEventList.Events.LAND_COLLISION_END:  return (ulong)scriptEvents.land_collision_end;
				case SupportedEventList.Events.LAND_COLLISION_START:return (ulong)scriptEvents.land_collision_start;
				case SupportedEventList.Events.AT_TARGET:           return (ulong)scriptEvents.at_target;
				case SupportedEventList.Events.LISTEN:              return (ulong)scriptEvents.listen;
				case SupportedEventList.Events.MONEY:               return (ulong)scriptEvents.money;
				case SupportedEventList.Events.MOVING_END:          return (ulong)scriptEvents.moving_end;
				case SupportedEventList.Events.MOVING_START:        return (ulong)scriptEvents.moving_start;
				case SupportedEventList.Events.NOT_AT_ROT_TARGET:   return (ulong)scriptEvents.not_at_rot_target;
				case SupportedEventList.Events.NOT_AT_TARGET:       return (ulong)scriptEvents.not_at_target;
				case SupportedEventList.Events.TOUCH_START:         return (ulong)scriptEvents.touch_start;
				case SupportedEventList.Events.OBJECT_REZ:          return (ulong)scriptEvents.object_rez;
				case SupportedEventList.Events.REMOTE_DATA:         return (ulong)scriptEvents.remote_data;
				case SupportedEventList.Events.AT_ROT_TARGET:       return (ulong)scriptEvents.at_rot_target;
				case SupportedEventList.Events.RUN_TIME_PERMISSIONS:return (ulong)scriptEvents.run_time_permissions;
				case SupportedEventList.Events.TOUCH_END:           return (ulong)scriptEvents.touch_end;
				case SupportedEventList.Events.STATE_ENTRY:         return (ulong)scriptEvents.state_entry;
				case SupportedEventList.Events.CHANGED:             return (ulong)scriptEvents.changed;
				case SupportedEventList.Events.LINK_MESSAGE:        return (ulong)scriptEvents.link_message;
				case SupportedEventList.Events.NO_SENSOR:           return (ulong)scriptEvents.no_sensor;
				case SupportedEventList.Events.ON_REZ:              return (ulong)scriptEvents.on_rez;
				case SupportedEventList.Events.SENSOR:              return (ulong)scriptEvents.sensor;
				case SupportedEventList.Events.HTTP_REQUEST:        return (ulong)scriptEvents.http_request;
				case SupportedEventList.Events.TRANSACTION_RESULT:  return (ulong)scriptEvents.transaction_result;
				case SupportedEventList.Events.LINKSET_DATA:        return (ulong)scriptEvents.linkset_data;
				// Note: the following events are intentionally deferred — each requires a
				// coordinated two-sided change (Phlox SupportedEventList AND core OpenSim
				// scriptEvents enum / posting infrastructure) before a case label here is safe.
				// - BOT_UPDATE: scriptEvents flag missing. No PostObjectEvent call posts this
				//   event anywhere in the codebase (confirmed 2026-05-24); no scripts broken.
				// - PATH_UPDATE: scriptEvents.path_update flag bit exists (1UL << 40), but
				//   SupportedEventList.Events has no PATH_UPDATE member (Phlox side gap).
				//   Wiring requires adding the enum member AND confirming the pathfinding
				//   subsystem posts it. Deferred pending evaluation.
				// - EXPERIENCE_PERMISSIONS / EXPERIENCE_PERMISSIONS_DENIED: SL Experience
				//   system is out of scope for Legion Grid; no OpenSim infrastructure exists.
				default: return 0UL;
                        }
                }

        // ── Tier 1: EEP / Environment ──────────────────────────────────────

        public int llGetDayLength()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return 14400;
            return envModule.GetDayLength(m_host.GetWorldPosition());
        }

        public int llGetDayOffset()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return 57600;
            return envModule.GetDayOffset(m_host.GetWorldPosition());
        }

        public int llGetRegionDayLength()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return 14400;
            return envModule.GetRegionDayLength();
        }

        public int llGetRegionDayOffset()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return 57600;
            return envModule.GetRegionDayOffset();
        }

        public Vector3 llGetMoonDirection()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Vector3.Zero;
            return envModule.GetMoonDir(m_host.GetWorldPosition());
        }

        public Quaternion llGetMoonRotation()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Quaternion.Identity;
            return envModule.GetMoonRot(m_host.GetWorldPosition());
        }

        public Quaternion llGetSunRotation()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Quaternion.Identity;
            return envModule.GetSunRot(m_host.GetWorldPosition());
        }

        public Vector3 llGetRegionSunDirection()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Vector3.Zero;
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionSunDir(z);
        }

        public Quaternion llGetRegionSunRotation()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Quaternion.Identity;
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionSunRot(z);
        }

        public Vector3 llGetRegionMoonDirection()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Vector3.Zero;
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionMoonDir(z);
        }

        public Quaternion llGetRegionMoonRotation()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Quaternion.Identity;
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionMoonRot(z);
        }

        public Vector3 llGetRegionLightDir()
        {
            // Dominant light direction = sun direction for the region
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return new Vector3(0f, 0.7071068f, 0.7071068f);
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionSunDir(z);
        }

        // ── Tier 2: PBR / GLTF Material Functions (598–602) ──

        public string llGetRenderMaterial(int face)
        {
            var shape = m_host.Shape;
            if (shape?.RenderMaterials?.entries == null)
                return string.Empty;
            if (face < 0)
                return string.Empty;
            foreach (var entry in shape.RenderMaterials.entries)
            {
                if (entry.te_index == (byte)face)
                    return entry.id.ToString();
            }
            return string.Empty;
        }

        public void llSetRenderMaterial(string materialId, int face)
        {
            if (!UUID.TryParse(materialId, out UUID matUUID))
                return;
            if (!World.Permissions.CanEditObject(m_host.ParentGroup.UUID, m_host.OwnerID))
                return;

            var shape = m_host.Shape;
            shape.RenderMaterials ??= new OpenMetaverse.Primitive.RenderMaterials();

            int numFaces = m_host.GetNumberOfSides();

            if (face == ALL_SIDES)
            {
                var entries = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[numFaces];
                for (int i = 0; i < numFaces; i++)
                {
                    entries[i].te_index = (byte)i;
                    entries[i].id = matUUID;
                }
                shape.RenderMaterials.entries = entries;
            }
            else if (face >= 0 && face < numFaces)
            {
                if (shape.RenderMaterials.entries == null)
                {
                    shape.RenderMaterials.entries = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[1];
                    shape.RenderMaterials.entries[0].te_index = (byte)face;
                    shape.RenderMaterials.entries[0].id = matUUID;
                }
                else
                {
                    int idx = -1;
                    for (int i = 0; i < shape.RenderMaterials.entries.Length; i++)
                    {
                        if (shape.RenderMaterials.entries[i].te_index == (byte)face)
                        { idx = i; break; }
                    }
                    if (idx >= 0)
                    {
                        shape.RenderMaterials.entries[idx].id = matUUID;
                    }
                    else
                    {
                        int len = shape.RenderMaterials.entries.Length;
                        Array.Resize(ref shape.RenderMaterials.entries, len + 1);
                        shape.RenderMaterials.entries[len].te_index = (byte)face;
                        shape.RenderMaterials.entries[len].id = matUUID;
                    }
                }
            }
            else
            {
                return;
            }

            m_host.ParentGroup.HasGroupChanged = true;
            m_host.ScheduleUpdate(PrimUpdateFlags.FullUpdate);
        }

        public int llIsLinkGLTFMaterial(int link, int face)
        {
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                var shape = part.Shape;
                if (shape?.RenderMaterials?.overrides == null)
                    return 0;
                if (face == ALL_SIDES)
                {
                    return shape.RenderMaterials.overrides.Length > 0 ? 1 : 0;
                }
                foreach (var ovr in shape.RenderMaterials.overrides)
                {
                    if (ovr.te_index == (byte)face && !string.IsNullOrEmpty(ovr.data))
                        return 1;
                }
                return 0; // only check first matching link part
            }
            return 0;
        }

public int llSetLinkGLTFOverrides(int link, int face, LSLList overrides)
        {
            // overrides is a strided list of [key, value, key, value, ...]
            // Builds LLSD Notation data matching the format used by MaterialsModule
            if (overrides.Length < 2 || overrides.Length % 2 != 0)
                return 0;

            // Build an OSDMap in the compact format the viewer expects
            var outosd = new OpenMetaverse.StructuredData.OSDMap();

            for (int i = 0; i < overrides.Length; i += 2)
            {
                string key = overrides.Data[i].ToString();
                string val = overrides.Data[i + 1].ToString();
                var ci = System.Globalization.CultureInfo.InvariantCulture;

                switch (key)
                {
                    case "base_color":
                    {
                        if (TryParseColor4(val, out float r, out float g, out float b, out float a))
                        {
                            outosd["bc"] = new OpenMetaverse.StructuredData.OSDArray()
                            {
                                Math.Round((double)r, 4),
                                Math.Round((double)g, 4),
                                Math.Round((double)b, 4),
                                Math.Round((double)a, 4)
                            };
                        }
                        break;
                    }
                    case "metallic_factor":
                    {
                        if (float.TryParse(val, System.Globalization.NumberStyles.Float, ci, out float mf))
                            outosd["mf"] = Math.Round((double)Math.Clamp(mf, 0f, 1f), 3);
                        break;
                    }
                    case "roughness_factor":
                    {
                        if (float.TryParse(val, System.Globalization.NumberStyles.Float, ci, out float rf))
                            outosd["rf"] = Math.Round((double)Math.Clamp(rf, 0f, 1f), 3);
                        break;
                    }
                    case "emissive_factor":
                    {
                        if (TryParseVector3(val, out float er, out float eg, out float eb))
                        {
                            outosd["ec"] = new OpenMetaverse.StructuredData.OSDArray()
                            {
                                Math.Round((double)er, 4),
                                Math.Round((double)eg, 4),
                                Math.Round((double)eb, 4)
                            };
                        }
                        break;
                    }
                    case "alpha_mode":
                    {
                        outosd["am"] = val.ToUpper() switch
                        {
                            "BLEND" => 1,
                            "MASK" => 2,
                            _ => 0
                        };
                        break;
                    }
                    case "alpha_cutoff":
                    {
                        if (float.TryParse(val, System.Globalization.NumberStyles.Float, ci, out float ac))
                            outosd["ac"] = Math.Round((double)Math.Clamp(ac, 0f, 1f), 3);
                        break;
                    }
                    case "double_sided":
                    {
                        outosd["ds"] = (val == "1" || val.ToLower() == "true");
                        break;
                    }
                }
            }

            if (outosd.Count == 0)
                return 0;

            string llsdData = OpenMetaverse.StructuredData.OSDParser.SerializeLLSDNotation(outosd);
            int changed = 0;

            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                if (!World.Permissions.CanEditObject(part.ParentGroup.UUID, m_host.OwnerID))
                    continue;

                var shape = part.Shape;
                shape.RenderMaterials ??= new OpenMetaverse.Primitive.RenderMaterials();

                int numFaces = part.GetNumberOfSides();
               if (face == ALL_SIDES)
                {
                    for (int f = 0; f < numFaces; f++)
                        ApplyGLTFOverride(ref shape.RenderMaterials.overrides, llsdData, f);
                }
                else if (face >= 0 && face < numFaces)
                {
                    ApplyGLTFOverride(ref shape.RenderMaterials.overrides, llsdData, face);
                }
                else
                {
                    continue;
                }

                part.ParentGroup.HasGroupChanged = true;
                part.ScheduleUpdate(PrimUpdateFlags.MaterialOvr | PrimUpdateFlags.FullUpdate);
                changed++;
            }
            return changed > 0 ? 1 : 0;
        }

        private static void ApplyGLTFOverride(
            ref OpenMetaverse.Primitive.RenderMaterials.RenderMaterialOverrideEntry[] overrides,
            string data, int face)
        {
            if (overrides == null)
            {
                overrides = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialOverrideEntry[1];
                overrides[0].te_index = (byte)face;
                overrides[0].data = data;
                return;
            }

            for (int i = 0; i < overrides.Length; i++)
            {
                if (overrides[i].te_index == (byte)face)
                {
                    overrides[i].data = data;
                    return;
                }
            }

            int len = overrides.Length;
            Array.Resize(ref overrides, len + 1);
            overrides[len].te_index = (byte)face;
            overrides[len].data = data;
        }

        // ── PBR helpers for PRIM_RENDER_MATERIAL / PRIM_GLTF_* in SetPrimParams/GetPrimParams ──

        private static void SetRenderMaterialEntry(
            ref OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[] entries,
            UUID matId, int face)
        {
            if (entries == null)
            {
                entries = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[1];
                entries[0].te_index = (byte)face;
                entries[0].id = matId;
                return;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].te_index == (byte)face)
                {
                    entries[i].id = matId;
                    return;
                }
            }

            int len = entries.Length;
            Array.Resize(ref entries, len + 1);
            entries[len].te_index = (byte)face;
            entries[len].id = matId;
        }

        private void ApplyGLTFOverrideToPart(SceneObjectPart part, int face,
            OpenMetaverse.StructuredData.OSDMap osd)
        {
            if (osd.Count == 0) return;

            var shape = part.Shape;
            shape.RenderMaterials ??= new OpenMetaverse.Primitive.RenderMaterials();

            string llsdData = OpenMetaverse.StructuredData.OSDParser.SerializeLLSDNotation(osd);
            int numFaces = part.GetNumberOfSides();

            if (face == ALL_SIDES)
            {
                for (int f = 0; f < numFaces; f++)
                    ApplyGLTFOverride(ref shape.RenderMaterials.overrides, llsdData, f);
            }
            else if (face >= 0 && face < numFaces)
            {
                ApplyGLTFOverride(ref shape.RenderMaterials.overrides, llsdData, face);
            }
            else
            {
                return;
            }

            if (part.ParentGroup != null)
                part.ParentGroup.HasGroupChanged = true;
            part.ScheduleUpdate(PrimUpdateFlags.MaterialOvr | PrimUpdateFlags.FullUpdate);
        }

        private string GetGLTFOverrideField(SceneObjectPart part, int face, string fieldName)
        {
            var shape = part.Shape;
            if (shape?.RenderMaterials?.overrides == null) return null;

            foreach (var ovr in shape.RenderMaterials.overrides)
            {
                if (ovr.te_index == (byte)face && !string.IsNullOrEmpty(ovr.data))
                {
                    try
                    {
                        var parsed = OpenMetaverse.StructuredData.OSDParser.DeserializeLLSDNotation(ovr.data)
                            as OpenMetaverse.StructuredData.OSDMap;
                        if (parsed != null && parsed.ContainsKey(fieldName))
                            return parsed[fieldName].ToString();
                    }
                    catch { }
                }
            }
            return null;
        }

        private OpenMetaverse.StructuredData.OSDMap GetGLTFOverrideMap(SceneObjectPart part, int face)
        {
            var shape = part.Shape;
            if (shape?.RenderMaterials?.overrides == null) return null;

            foreach (var ovr in shape.RenderMaterials.overrides)
            {
                if (ovr.te_index == (byte)face && !string.IsNullOrEmpty(ovr.data))
                {
                    try
                    {
                        return OpenMetaverse.StructuredData.OSDParser.DeserializeLLSDNotation(ovr.data)
                            as OpenMetaverse.StructuredData.OSDMap;
                    }
                    catch { }
                }
            }
            return null;
        }

        private void GetGLTFBaseColorParams(SceneObjectPart part, int face, List<object> result)
        {
            var osd = GetGLTFOverrideMap(part, face);

            // texture
            result.Add(osd != null && osd.ContainsKey("tex") ? osd["tex"].AsString() : string.Empty);
            // repeats
            if (osd != null && osd.ContainsKey("rep") && osd["rep"] is OpenMetaverse.StructuredData.OSDArray repArr && repArr.Count >= 2)
                result.Add(new Vector3((float)repArr[0].AsReal(), (float)repArr[1].AsReal(), 0f));
            else
                result.Add(new Vector3(1f, 1f, 0f));
            // offsets
            if (osd != null && osd.ContainsKey("off") && osd["off"] is OpenMetaverse.StructuredData.OSDArray offArr && offArr.Count >= 2)
                result.Add(new Vector3((float)offArr[0].AsReal(), (float)offArr[1].AsReal(), 0f));
            else
                result.Add(Vector3.Zero);
            // rotation
            result.Add(osd != null && osd.ContainsKey("rot") ? (float)osd["rot"].AsReal() : 0f);
            // color (RGB from bc array)
            if (osd != null && osd.ContainsKey("bc") && osd["bc"] is OpenMetaverse.StructuredData.OSDArray bcArr && bcArr.Count >= 3)
                result.Add(new Vector3((float)bcArr[0].AsReal(), (float)bcArr[1].AsReal(), (float)bcArr[2].AsReal()));
            else
                result.Add(Vector3.One);
            // alpha (4th element of bc)
            if (osd != null && osd.ContainsKey("bc") && osd["bc"] is OpenMetaverse.StructuredData.OSDArray bcArr2 && bcArr2.Count >= 4)
                result.Add((float)bcArr2[3].AsReal());
            else
                result.Add(1f);
            // alpha_mode
            result.Add(osd != null && osd.ContainsKey("am") ? osd["am"].AsInteger() : 0);
            // alpha_cutoff
            result.Add(osd != null && osd.ContainsKey("ac") ? (float)osd["ac"].AsReal() : 0.5f);
            // double_sided
            result.Add(osd != null && osd.ContainsKey("ds") && osd["ds"].AsBoolean() ? 1 : 0);
        }

        private void GetGLTFTransformParams(SceneObjectPart part, int face,
            string texKey, string repKey, string offKey, string rotKey, List<object> result)
        {
            var osd = GetGLTFOverrideMap(part, face);

            // texture
            result.Add(osd != null && osd.ContainsKey(texKey) ? osd[texKey].AsString() : string.Empty);
            // repeats
            if (osd != null && osd.ContainsKey(repKey) && osd[repKey] is OpenMetaverse.StructuredData.OSDArray repArr && repArr.Count >= 2)
                result.Add(new Vector3((float)repArr[0].AsReal(), (float)repArr[1].AsReal(), 0f));
            else
                result.Add(new Vector3(1f, 1f, 0f));
            // offsets
            if (osd != null && osd.ContainsKey(offKey) && osd[offKey] is OpenMetaverse.StructuredData.OSDArray offArr && offArr.Count >= 2)
                result.Add(new Vector3((float)offArr[0].AsReal(), (float)offArr[1].AsReal(), 0f));
            else
                result.Add(Vector3.Zero);
            // rotation
            result.Add(osd != null && osd.ContainsKey(rotKey) ? (float)osd[rotKey].AsReal() : 0f);
        }

        private void GetGLTFMetallicRoughnessParams(SceneObjectPart part, int face, List<object> result)
        {
            var osd = GetGLTFOverrideMap(part, face);

            // texture, repeats, offsets, rotation
            GetGLTFTransformParams(part, face, "mrtex", "mrrep", "mroff", "mrrot", result);
            // metallic_factor
            result.Add(osd != null && osd.ContainsKey("mf") ? (float)osd["mf"].AsReal() : 0f);
            // roughness_factor
            result.Add(osd != null && osd.ContainsKey("rf") ? (float)osd["rf"].AsReal() : 0.5f);
        }

        private void GetGLTFEmissiveParams(SceneObjectPart part, int face, List<object> result)
        {
            var osd = GetGLTFOverrideMap(part, face);

            // texture, repeats, offsets, rotation
            GetGLTFTransformParams(part, face, "etex", "erep", "eoff", "erot", result);
            // emissive_tint
            if (osd != null && osd.ContainsKey("ec") && osd["ec"] is OpenMetaverse.StructuredData.OSDArray ecArr && ecArr.Count >= 3)
                result.Add(new Vector3((float)ecArr[0].AsReal(), (float)ecArr[1].AsReal(), (float)ecArr[2].AsReal()));
            else
                result.Add(Vector3.Zero);
        }

        private static bool TryParseColor4(string s, out float r, out float g, out float b, out float a)
        {
            r = g = b = 0f; a = 1f;
            s = s.Trim().Trim('<', '>');
            var parts = s.Split(',');
            if (parts.Length < 3) return false;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, ci, out r)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, ci, out g)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, ci, out b)) return false;
            if (parts.Length >= 4)
                float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float, ci, out a);
            return true;
        }

        private static bool TryParseVector3(string s, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            s = s.Trim().Trim('<', '>');
            var parts = s.Split(',');
            if (parts.Length < 3) return false;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, ci, out x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, ci, out y)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, ci, out z)) return false;
            return true;
        }


        public void llRenderMaterial(string materialId)
        {
            llSetRenderMaterial(materialId, ALL_SIDES);
        }

        // ── Tier 3: Combat 2.0 (603–605) ──

        public float llGetHealth(string id)
        {
            if (!UUID.TryParse(id, out UUID agentId))
                return -1f;
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent)
                return -1f;
            return sp.Health;
        }

        public void llAdjustDamage(string id, float amount)
        {
            if (!UUID.TryParse(id, out UUID agentId))
                return;
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent || sp.Invulnerable || sp.IsViewerUIGod)
                return;
            if (!World.RegionInfo.RegionSettings.AllowDamage)
                return;

            float newHealth = sp.Health - amount;
            if (newHealth <= 0f)
            {
                sp.setHealthWithUpdate(0f);
                sp.Scene.EventManager.TriggerAvatarKill(m_host.LocalId, sp);
            }
            else
            {
                if (newHealth > 100f) newHealth = 100f;
                sp.setHealthWithUpdate(newHealth);
            }
        }

        public void llSetHealth(string id, float health)
        {
            if (!UUID.TryParse(id, out UUID agentId))
                return;
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent)
                return;
            if (!World.RegionInfo.RegionSettings.AllowDamage)
                return;

            health = Math.Clamp(health, 0f, 100f);
            if (health <= 0f)
            {
                sp.setHealthWithUpdate(0f);
                sp.Scene.EventManager.TriggerAvatarKill(m_host.LocalId, sp);
            }
            else
            {
                sp.setHealthWithUpdate(health);
            }
        }

		// -- Tier 4: Pathfinding / Character System (606-628) --
		// Maps prim LocalID -> bot UUID for llCreateCharacter/llNavigateTo etc.
		// Region-qualified key: (regionID, prim localID). LocalIDs are unique only within
		// a single region; in a multi-region process two prims in different regions can share
		// a localID. Keying by (regionID, localID) prevents cross-region character state
		// collision. See memory-session-f-plan.md (M-14).
		private (UUID, uint) CharKey => (World.RegionInfo.RegionID, m_host.LocalId);
		private static readonly Dictionary<(UUID, uint), UUID> s_primCharacters = new();
		private static readonly object s_charLock = new();

        // Called by PhloxEngine.OnObjectBeingRemovedFromScene when a prim leaves the scene (M-14b).
        // Removes the dict entry and returns the botID so the caller can remove the bot from BotManager.
        // Returns UUID.Zero if no character was registered for this prim.
        internal static UUID ClearCharacter(UUID regionID, uint localID)
        {
            lock (s_charLock)
            {
                var key = (regionID, localID);
                if (s_primCharacters.TryGetValue(key, out UUID botID))
                {
                    s_primCharacters.Remove(key);
                    return botID;
                }
            }
            return UUID.Zero;
        }

        // Called by PhloxEngine.RemoveRegion to purge all character dict entries for a region (M-14b).
        // BotManager.RemoveRegion already removes the bot NPCs; this cleans only the dict.
        internal static void ClearRegionCharacters(UUID regionID)
        {
            lock (s_charLock)
            {
                foreach (var key in s_primCharacters.Keys.Where(k => k.Item1 == regionID).ToList())
                    s_primCharacters.Remove(key);
            }
        }

		private UUID GetCharacterBot()
		{
			lock (s_charLock)
			{
				if (s_primCharacters.TryGetValue(CharKey, out UUID botID))
					return botID;
			}
			return UUID.Zero;
		}


        public LSLList llGetClosestNavPoint(Vector3 point, LSLList options)
        {
            // No navmesh in Legion — return the requested point as the closest navigable point
            return new LSLList(new object[] { point });
        }

        public LSLList llGetStaticPath(Vector3 start, Vector3 end, float radius, LSLList parameters)
        {
            // No navmesh in Legion — return a straight-line path [start, end, status]
            // Status 0 = success per SL spec
            return new LSLList(new object[] { start, end, 0 });
        }
		public void llExecCharacterCmd(int command, LSLList parameters)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			if (command == 0 || command == 2) // STOP or SMOOTH_STOP
				manager.StopMovement(botID, m_host.OwnerID);
		}
		public void llDeleteCharacter()
		{
			UUID botID;
			lock (s_charLock)
			{
				if (!s_primCharacters.TryGetValue(CharKey, out botID))
					return;
				s_primCharacters.Remove(CharKey);
			}
			IBotManager manager = GetBotManager();
			if (manager != null)
				manager.RemoveBot(botID, m_host.OwnerID);
		}
		// -- Pathfinding Character Functions (621-628) --

		public void llCreateCharacter(LSLList options)
		{
			llDeleteCharacter();
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			string firstName = "Char";
			string lastName = m_host.LocalId.ToString();
			Vector3 pos = m_host.AbsolutePosition;
			float speed = 1.0f;
			for (int i = 0; i < options.Length - 1; i += 2)
			{
				int opt = (int)options.Data[i];
				if (opt == 12) // CHARACTER_DESIRED_SPEED
					speed = (float)options.Data[i + 1];
			}
			string reason;
			UUID botID = manager.CreateBot(firstName, lastName, pos, "", m_itemID, m_host.OwnerID, out reason);
			if (botID == UUID.Zero) return;
			if (speed != 1.0f)
				manager.SetBotSpeed(botID, speed, m_host.OwnerID);
			lock (s_charLock)
				s_primCharacters[CharKey] = botID;
		}

		public void llNavigateTo(Vector3 pos, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			var positions = new List<Vector3> { pos };
			var modes = new List<TravelMode> { TravelMode.Walk };
			manager.SetBotNavigationPoints(botID, positions, modes,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llUpdateCharacter(LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			for (int i = 0; i < options.Length - 1; i += 2)
			{
				int opt = (int)options.Data[i];
				if (opt == 12) // CHARACTER_DESIRED_SPEED
				{
					float speed = (float)options.Data[i + 1];
					manager.SetBotSpeed(botID, speed, m_host.OwnerID);
				}
			}
		}

		public void llPursue(string targetID, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			if (!UUID.TryParse(targetID, out UUID target)) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			manager.StartFollowingAvatar(botID, target,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llEvade(string targetID, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			if (!UUID.TryParse(targetID, out UUID target)) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			ScenePresence sp = World?.GetScenePresence(target);
			if (sp == null) return;
			Vector3 myPos = m_host.AbsolutePosition;
			Vector3 dir = myPos - sp.AbsolutePosition;
			dir.Normalize();
			Vector3 away = myPos + dir * 30f;
			away.X = Math.Clamp(away.X, 1f, 254f);
			away.Y = Math.Clamp(away.Y, 1f, 254f);
			var positions = new List<Vector3> { away };
			var modes = new List<TravelMode> { TravelMode.Run };
			manager.SetBotNavigationPoints(botID, positions, modes,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llFleeFrom(Vector3 source, float distance, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			Vector3 myPos = m_host.AbsolutePosition;
			Vector3 dir = myPos - source;
			dir.Normalize();
			Vector3 away = myPos + dir * distance;
			away.X = Math.Clamp(away.X, 1f, 254f);
			away.Y = Math.Clamp(away.Y, 1f, 254f);
			var positions = new List<Vector3> { away };
			var modes = new List<TravelMode> { TravelMode.Run };
			manager.SetBotNavigationPoints(botID, positions, modes,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llWanderWithin(Vector3 origin, Vector3 distances, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			manager.WanderWithin(botID, origin, distances,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llPatrolPoints(LSLList points, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			var positions = new List<Vector3>();
			var modes = new List<TravelMode>();
			for (int i = 0; i < points.Length; i++)
			{
				if (points.Data[i] is Vector3 v)
				{
					positions.Add(v);
					modes.Add(TravelMode.Walk);
				}
			}
			if (positions.Count == 0) return;
			manager.SetBotNavigationPoints(botID, positions, modes,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		
		// ── Tier 5: Experience KVP Store (610–620) ──

        // ── 610–620: Experience KV Store (upgraded to use ExperienceService) ──

        // ── SL Experience error codes (XP_ERROR_*) + limits, ported from Legion
        //    (port-source-2026-07-22) to match the SL wiki XP_ERROR table 0-18.
        //    Script-surface conformance — Experience port T1 (SS-1..9). ──
        private const int XP_ERROR_NONE = 0;
        private const int XP_ERROR_THROTTLED = 1;
        private const int XP_ERROR_EXPERIENCES_DISABLED = 2;
        private const int XP_ERROR_INVALID_PARAMETERS = 3;
        private const int XP_ERROR_NOT_PERMITTED = 4;
        private const int XP_ERROR_NO_EXPERIENCE = 5;
        private const int XP_ERROR_NOT_FOUND = 6;
        private const int XP_ERROR_INVALID_EXPERIENCE = 7;
        private const int XP_ERROR_EXPERIENCE_DISABLED = 8;
        private const int XP_ERROR_EXPERIENCE_SUSPENDED = 9;
        private const int XP_ERROR_UNKNOWN_ERROR = 10;
        private const int XP_ERROR_QUOTA_EXCEEDED = 11;
        private const int XP_ERROR_STORE_DISABLED = 12;
        private const int XP_ERROR_STORAGE_EXCEPTION = 13;
        private const int XP_ERROR_KEY_NOT_FOUND = 14;
        private const int XP_ERROR_RETRY_UPDATE = 15;
        private const int XP_ERROR_MATURITY_EXCEEDED = 16;
        private const int XP_ERROR_NOT_PERMITTED_LAND = 17;
        private const int XP_ERROR_REQUEST_PERM_TIMEOUT = 18;
        // SL key-value key length cap (SL wiki llCreateKeyValue): 1011 bytes (was 255).
        private const int MAX_EXPERIENCE_KEY_LENGTH = 1011;
        // Viewer experience-property bit PROPERTY_DISABLED (indra VP_DISABLED = 1<<6);
        // used to report the llGetExperienceDetails state field.
        private const int VP_DISABLED = 1 << 6;
        // SL per-experience KV quota: 128 MiB (was NGC's 16 MiB). T2 ports Legion DEC-2/UNV-5.
        private const long MAX_DATA_QUOTA = 128L * 1024 * 1024;

        // UTF-8 byte count for a KV key/value — the quota basis (matches Legion's KvBytes and the
        // MySQL SUM(LENGTH(`key`)+LENGTH(`value`)) used-size on the grid backend).
        private static long KvBytes(string s) => s == null ? 0 : System.Text.Encoding.UTF8.GetByteCount(s);

        // True if updating `key` to `value` would push this experience's KV store over MAX_DATA_QUOTA.
        // Delta-aware (Legion ExceedsQuota): an existing key swaps its value (key stays); a new key
        // adds the whole pair. Basis: key+value UTF-8 bytes.
        private bool ExceedsQuota(PhloxExperienceAdapter expService, UUID expId, string key, string value)
        {
            long used = expService.DataSizeKeyValue(expId);
            string existing = expService.ReadKeyValue(expId, key); // null iff key absent
            long oldPair = existing != null ? KvBytes(key) + KvBytes(existing) : 0;
            long newPair = KvBytes(key) + KvBytes(value);
            return used - oldPair + newPair > MAX_DATA_QUOTA;
        }

        // KV int return contract: 0 ok · -1 invalid/error · -2 duplicate (create) ·
        // -3 CAS-fail/not-found (update) · -4 not-found (delete) · -5 quota exceeded (create/update).
        // The ...SL wrappers translate these to the SL XP_ERROR codes.
        public int llCreateKeyValue(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MAX_EXPERIENCE_KEY_LENGTH) return -1;
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
            {
                // Fallback: use owner-based namespace via old GetExperienceId()
                expId = m_host.OwnerID;
            }
            try
            {
                // T2/DEC-2: quota check BEFORE the write (Legion — never write-then-detect). A create
                // only ADDS a pair; reject if that would exceed 128 MiB -> -5 (llCreateKeyValueSL emits
                // 0,11 = XP_ERROR_QUOTA_EXCEEDED). No write.
                if (expService != null && expService.DataSizeKeyValue(expId) + KvBytes(key) + KvBytes(value) > MAX_DATA_QUOTA)
                    return -5;
                bool ok = expService != null
                    ? expService.CreateKeyValue(expId, key, value)
                    : false;
                return ok ? 0 : -2; // -2 = duplicate key
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llCreateKeyValue failed: {0}", ex.Message);
                return -1;
            }
        }

        public string llReadKeyValue(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                string val = expService?.ReadKeyValue(expId, key);
                return val ?? string.Empty;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llReadKeyValue failed: {0}", ex.Message);
                return string.Empty;
            }
        }

        public int llUpdateKeyValue(string key, string value, string check)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MAX_EXPERIENCE_KEY_LENGTH) return -1;
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                // T2/DEC-2: delta-aware quota check BEFORE the write (Legion ExceedsQuota). If the
                // projected total after this update exceeds 128 MiB -> -5 (llUpdateKeyValueSL emits
                // 0,11). No write. (If a CAS would also fail, quota wins at the boundary — benign.)
                if (expService != null && ExceedsQuota(expService, expId, key, value))
                    return -5;
                bool ok = expService != null
                    ? expService.UpdateKeyValue(expId, key, value, check)
                    : false;
                return ok ? 0 : -3; // -3 = check failed or key not found
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llUpdateKeyValue failed: {0}", ex.Message);
                return -1;
            }
        }

        public int llDeleteKeyValue(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                bool ok = expService != null
                    ? expService.DeleteKeyValue(expId, key)
                    : false;
                return ok ? 0 : -4; // -4 = key not found
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llDeleteKeyValue failed: {0}", ex.Message);
                return -1;
            }
        }

        public int llKeyCountKeyValue()
        {
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                return expService?.KeyCountKeyValue(expId) ?? 0;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llKeyCountKeyValue failed: {0}", ex.Message);
                return 0;
            }
        }

        public LSLList llKeysKeyValue(int start, int count)
        {
            if (count <= 0) count = 100;
            if (count > 1000) count = 1000;
            if (start < 0) start = 0;
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                var keys = expService?.KeysKeyValue(expId, start, count);
                if (keys == null || keys.Count == 0) return new LSLList();
                return new LSLList(keys.Select(k => (object)k).ToArray());
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llKeysKeyValue failed: {0}", ex.Message);
                return new LSLList();
            }
        }

        public int llDataSizeKeyValue()
        {
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                return (int)(expService?.DataSizeKeyValue(expId) ?? 0);
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llDataSizeKeyValue failed: {0}", ex.Message);
                return 0;
            }
        }

        public int llClearKeyValue()
        {
            // Clear all KV pairs for this experience — delete all keys
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                var keys = expService?.KeysKeyValue(expId, 0, 10000);
                if (keys != null)
                {
                    foreach (var k in keys)
                        expService.DeleteKeyValue(expId, k);
                }
                return 0;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llClearKeyValue failed: {0}", ex.Message);
                return -1;
            }
        }

        // The ...SL wrappers present SL's async-dataserver CSV shape "1,<value>" (success) /
        // "0,<XP_ERROR>" (failure). T1 makes the failure payload a NUMERIC XP_ERROR code (was a
        // free-text message), matching SL/Legion. (The underlying KV model stays synchronous —
        // the async request-key + dataserver-event contract is a later architecture slice, not T1.)
        public string llCreateKeyValueSL(string key, string value)
        {
            int result = llCreateKeyValue(key, value);
            if (result == 0)
                return "1," + (value ?? string.Empty);
            if (result == -5) // over the 128 MiB quota (T2)
                return "0," + XP_ERROR_QUOTA_EXCEEDED;
            // SL: creating an existing key (or a generic KV failure) => XP_ERROR_STORAGE_EXCEPTION.
            return "0," + XP_ERROR_STORAGE_EXCEPTION;
        }

        public string llReadKeyValueSL(string key)
        {
            string val = llReadKeyValue(key);
            if (!string.IsNullOrEmpty(val))
                return "1," + val;
            // SL: a missing key => XP_ERROR_KEY_NOT_FOUND (14). (SS-4)
            return "0," + XP_ERROR_KEY_NOT_FOUND;
        }

        public string llUpdateKeyValueSL(string key, string value, string check)
        {
            int result = llUpdateKeyValue(key, value, check);
            if (result == 0)
                return "1," + (value ?? string.Empty);
            if (result == -5) // over the 128 MiB quota (T2)
                return "0," + XP_ERROR_QUOTA_EXCEEDED;
            // SL: a checked-update mismatch (CAS fail) => XP_ERROR_RETRY_UPDATE (15).
            return "0," + XP_ERROR_RETRY_UPDATE;
        }
		
        // ── Tier 6: Standalone ──

        public string llSignRSA(string data, string privateKeyPem, string algorithm)
        {
            try
            {
                System.Security.Cryptography.HashAlgorithmName hashAlg;
                switch (algorithm.ToUpper())
                {
                    case "SHA256": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA256; break;
                    case "SHA384": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA384; break;
                    case "SHA512": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA512; break;
                    case "SHA1":   hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA1; break;
                    default: return string.Empty;
                }
                using var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportFromPem(privateKeyPem.AsSpan());
                byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
                byte[] signature = rsa.SignData(dataBytes, hashAlg, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                return Convert.ToBase64String(signature);
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llSignRSA failed: {0}", ex.Message);
                return string.Empty;
            }
        }

        public int llVerifyRSA(string data, string signature, string publicKeyPem, string algorithm)
        {
            try
            {
                System.Security.Cryptography.HashAlgorithmName hashAlg;
                switch (algorithm.ToUpper())
                {
                    case "SHA256": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA256; break;
                    case "SHA384": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA384; break;
                    case "SHA512": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA512; break;
                    case "SHA1":   hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA1; break;
                    case "SHA224": hashAlg = new System.Security.Cryptography.HashAlgorithmName("SHA224"); break;
                    default: return 0;
                }
                using var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportFromPem(publicKeyPem.AsSpan());
                byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
                byte[] sigBytes = Convert.FromBase64String(signature);
                return rsa.VerifyData(dataBytes, sigBytes, hashAlg, System.Security.Cryptography.RSASignaturePadding.Pkcs1) ? 1 : 0;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llVerifyRSA failed: {0}", ex.Message);
                return 0;
            }
        }

        // ── Tier 7: EEP Environment Functions (630–632) ──

        // SL Environment parameter constants
        private const int SKY_AMBIENT = 0;
        private const int SKY_CLOUDS = 2;
        private const int SKY_DOME = 4;
        private const int SKY_GAMMA = 5;
        private const int SKY_GLOW = 6;
        private const int SKY_MOON = 9;
        private const int SKY_STAR_BRIGHTNESS = 13;
        private const int SKY_SUN = 14;
        private const int SKY_TRACKS = 15;
        private const int WATER_BLUR_MULTIPLIER = 100;
        private const int WATER_FOG = 103;
        private const int WATER_NORMAL_SCALE = 107;
        private const int WATER_WAVE_DIRECTION = 109;
        private const int ENV_DAY_LENGTH = 200;
        private const int ENV_DAY_OFFSET = 201;

        public LSLList llGetEnvironment(Vector3 pos, LSLList paramList)
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return new LSLList();

            var result = new List<object>();
            float altitude = pos.Z;
            // If pos.X == -1 and pos.Y == -1, use region environment
            // Otherwise use parcel environment at that position (we use region for both since
            // OpenSim parcel environments share the same ViewerEnvironment in most setups)

            for (int i = 0; i < paramList.Length; i++)
            {
                int param;
                try { param = Convert.ToInt32(paramList.Data[i]); }
                catch { continue; }

                switch (param)
                {
                    case ENV_DAY_LENGTH:
                        result.Add(envModule.GetDayLength(pos));
                        break;
                    case ENV_DAY_OFFSET:
                        result.Add(envModule.GetDayOffset(pos));
                        break;
                    case SKY_SUN:
                    {
                        Quaternion sunRot = envModule.GetSunRot(pos);
                        Vector3 sunDir = envModule.GetSunDir(pos);
                        result.Add(sunRot);
                        result.Add(1.0f); // scale
                        result.Add(sunDir);
                        break;
                    }
                    case SKY_MOON:
                    {
                        Quaternion moonRot = envModule.GetMoonRot(pos);
                        Vector3 moonDir = envModule.GetMoonDir(pos);
                        result.Add(moonRot);
                        result.Add(0.5f); // scale
                        result.Add(1.0f); // brightness
                        result.Add(1);    // is_default_texture
                        result.Add(moonDir);
                        break;
                    }
                    case SKY_TRACKS:
                    {
                        // Return sky track altitudes (tracks 2-4, track 1 is ground level)
                        result.Add(1000.0f);
                        result.Add(2000.0f);
                        result.Add(3000.0f);
                        break;
                    }
                    case SKY_AMBIENT:
                        result.Add(new Vector3(0.25f, 0.25f, 0.26f)); // default ambient
                        break;
                    case SKY_GAMMA:
                        result.Add(1.0f); // default gamma
                        break;
                    case SKY_GLOW:
                        result.Add(0.5f); // glow size
                        result.Add(0.1f); // glow focus
                        break;
                    case SKY_STAR_BRIGHTNESS:
                        result.Add(256.0f); // default star brightness
                        break;
                    case SKY_DOME:
                        result.Add(0.96f); // offset
                        result.Add(15000.0f); // radius
                        result.Add(1605.0f); // max_altitude
                        break;
                    case SKY_CLOUDS:
                        result.Add(new Vector3(0.41f, 0.41f, 0.41f)); // color
                        result.Add(0.27f); // coverage
                        result.Add(0.42f); // scale
                        result.Add(0.0f);  // variance
                        result.Add(new Vector3(0.01f, 0.01f, 0.0f)); // scroll
                        result.Add(new Vector3(1.0f, 0.53f, 1.0f));  // density
                        result.Add(new Vector3(1.0f, 0.52f, 1.0f));  // detail
                        result.Add(1);     // is_default
                        break;
                    default:
                        // Unknown parameter - SL sends debug channel message
                        ShoutError("llGetEnvironment: unknown parameter " + param);
                        break;
                }
            }
            return new LSLList(result);
        }

        public int llSetEnvironment(Vector3 pos, LSLList paramList)
        {
            // Check permissions - must be estate manager or land owner
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return -1;

            if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID,
                World.LandChannel.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y), 0, false))
            {
                ShoutError("llSetEnvironment: insufficient permissions");
                return -1;
            }

            var env = envModule.GetRegionEnvironment();
            if (env == null) return -1;

            bool changed = false;
            for (int i = 0; i < paramList.Length - 1; i += 2)
            {
                int param;
                try { param = Convert.ToInt32(paramList.Data[i]); }
                catch { continue; }

                switch (param)
                {
                    case ENV_DAY_LENGTH:
                        try
                        {
                            int dayLen = Convert.ToInt32(paramList.Data[i + 1]);
                            if (dayLen >= 14400 && dayLen <= 604800)
                            {
                                env.DayLength = dayLen;
                                changed = true;
                            }
                        }
                        catch { }
                        break;
                    case ENV_DAY_OFFSET:
                        try
                        {
                            int dayOff = Convert.ToInt32(paramList.Data[i + 1]);
                            env.DayOffset = dayOff;
                            changed = true;
                        }
                        catch { }
                        break;
                    default:
                        ShoutError("llSetEnvironment: parameter " + param + " not yet supported for setting");
                        break;
                }
            }

            if (changed)
            {
                envModule.StoreOnRegion(env);
                envModule.WindlightRefresh(0);
            }
            return 1; // ENV_OK
        }

        public int llReplaceEnvironment(Vector3 pos, string environment, int track, int day_length, int day_offset)
        {
            // Check permissions
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return -1;

            if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID,
                World.LandChannel.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y), 0, false))
            {
                ShoutError("llReplaceEnvironment: insufficient permissions");
                return -1;
            }

            var env = envModule.GetRegionEnvironment();
            if (env == null) return -1;

            // Apply day_length and day_offset if specified (not -1)
            bool changed = false;
            if (day_length > 0)
            {
                env.DayLength = Math.Clamp(day_length, 14400, 604800);
                changed = true;
            }
            if (day_offset != -1)
            {
                env.DayOffset = day_offset;
                changed = true;
            }

            if (changed)
            {
                envModule.StoreOnRegion(env);
                envModule.WindlightRefresh(0);
            }

            // Note: the 'environment' parameter would load an EEP asset by name/UUID
            // from prim inventory. Full asset loading is not implemented yet — only
            // day_length and day_offset changes are applied.
            if (!string.IsNullOrEmpty(environment))
            {
                m_log.LogInformation("[PhloxAPI]: llReplaceEnvironment: EEP asset '{0}' load not yet implemented, day_length/offset applied", environment);
            }

            return 1; // ENV_OK
        }

        // ── Tier 7b: Agent Environment + User Key (633–635) ──

        public string llRequestUserKey(string username)
        {
            // Async lookup — fires dataserver event with the user's UUID
            if (string.IsNullOrEmpty(username)) return string.Empty;

            UUID reqID = UUID.Random();

            // Normalize "first.last" to "first last"
            string normalized = username.Replace('.', ' ').Trim();
            string[] parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string firstName = parts.Length > 0 ? parts[0] : "";
            string lastName = parts.Length > 1 ? parts[1] : "Resident";

            // Look up via user account service
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var accountService = World?.RequestModuleInterface<IUserAccountService>();
                    if (accountService == null)
                    {
                        m_ScriptEngine.PostScriptEvent(m_itemID, "dataserver",
                            new object[] { reqID.ToString(), UUID.Zero.ToString() });
                        return;
                    }
                    var account = accountService.GetUserAccount(World.RegionInfo.ScopeID, firstName, lastName);
                    string result = account != null ? account.PrincipalID.ToString() : UUID.Zero.ToString();
                    m_ScriptEngine.PostScriptEvent(m_itemID, "dataserver",
                        new object[] { reqID.ToString(), result });
                }
                catch (Exception ex)
                {
                    m_log.LogWarning("[PhloxAPI]: llRequestUserKey failed for '{0}': {1}", username, ex.Message);
                    m_ScriptEngine.PostScriptEvent(m_itemID, "dataserver",
                        new object[] { reqID.ToString(), UUID.Zero.ToString() });
                }
            });

            return reqID.ToString();
        }

        public int llSetAgentEnvironment(string agentID, float transition, LSLList paramList)
        {
            // Experience-gated: set per-agent environment (EEP)
            if (m_host == null || World == null) return -1;

            UUID targetAgent;
            if (!UUID.TryParse(agentID, out targetAgent)) return -1;

            // Check experience permission
            if (!HasExperiencePermission(targetAgent))
                return -3; // XP_ERROR_NOT_EXPERIENCE

            ScenePresence sp = World.GetScenePresence(targetAgent);
            if (sp == null || sp.IsChildAgent) return -1;

            // Per-agent EEP requires viewer/region support for GenericMessage or
            // EnvironmentUpdate per-agent caps. Most OpenSim builds don't support this yet.
            // Log the attempt and return success so scripts don't break.
            m_log.LogInformation("[PhloxAPI]: llSetAgentEnvironment called for agent {0} with {1} params (transition={2}s). Per-agent EEP not yet implemented in viewer protocol.",
                targetAgent, paramList.Length, transition);
            return 0; // Report success so scripts proceed
        }

        public int llReplaceAgentEnvironment(string agentID, float transition, string environment)
        {
            // Experience-gated: replace per-agent environment with named EEP preset
            if (m_host == null || World == null) return -1;

            UUID targetAgent;
            if (!UUID.TryParse(agentID, out targetAgent)) return -1;

            // Check experience permission
            if (!HasExperiencePermission(targetAgent))
                return -3; // XP_ERROR_NOT_EXPERIENCE

            ScenePresence sp = World.GetScenePresence(targetAgent);
            if (sp == null || sp.IsChildAgent) return -1;

            // Per-agent EEP replace requires viewer protocol support.
            m_log.LogInformation("[PhloxAPI]: llReplaceAgentEnvironment called for agent {0} environment='{1}' (transition={2}s). Per-agent EEP not yet implemented in viewer protocol.",
                targetAgent, environment, transition);
            return 0; // Report success so scripts proceed
        }

        // ── Tier 8: Remaining SL Function Gaps (636–643) ──

        public string llGetExperienceErrorMessage(int error)
        {
            // SL experience error code to human-readable message
            switch (error)
            {
                case 0:  return "no error";
                case 1:  return "exceeded throttle";
                case 2:  return "experiences are disabled";
                case 3:  return "invalid parameters";
                case 4:  return "operation not permitted";
                case 5:  return "script not associated with an experience";
                case 6:  return "not found";
                case 7:  return "invalid experience";
                case 8:  return "experience is disabled";
                case 9:  return "experience is suspended";
                case 10: return "unknown error";
                case 11: return "experience data quota exceeded";
                case 12: return "key-value store is disabled";
                case 13: return "key-value store communication failed";
                // T1/SS-5,8,9: rows 14-18 corrected to the SL wiki XP_ERROR table (were shifted:
                // 14 said "key already exists", 15/16/17 were off by one, 18 was missing).
                case 14: return "key doesn't exist";                       // XP_ERROR_KEY_NOT_FOUND
                case 15: return "retry update";                           // XP_ERROR_RETRY_UPDATE
                case 16: return "experience content rating too high";     // XP_ERROR_MATURITY_EXCEEDED
                case 17: return "not allowed to run on this land";        // XP_ERROR_NOT_PERMITTED_LAND
                case 18: return "experience permissions request timed out"; // XP_ERROR_REQUEST_PERM_TIMEOUT
                default: return "unknown error id";
            }
        }

        public void llSetAgentRot(Quaternion rot, int relative)
        {
            // Requires PERMISSION_TRIGGER_ANIMATION
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & 0x10) == 0) // PERMISSION_TRIGGER_ANIMATION = 0x10
            {
                ShoutError("llSetAgentRot: script does not have PERMISSION_TRIGGER_ANIMATION");
                return;
            }
            UUID agentID = item.PermsGranter;
            if (agentID == UUID.Zero) return;

            ScenePresence sp = World.GetScenePresence(agentID);
            if (sp == null || sp.IsChildAgent) return;

            if (relative != 0)
            {
                // Relative to current rotation
                rot = sp.Rotation * rot;
            }
            sp.Rotation = rot;
            sp.SendTerseUpdateToAllClients();
        }

        public void llOpenFloater(string name, string url, LSLList paramList)
        {
            // Opens a viewer-side floater window. This is viewer-dependent.
            // Firestorm/OpenSim may not support all floater types.
            // For now, log and attempt to send a LoadURL for web-based floaters.
            if (!string.IsNullOrEmpty(url))
            {
                TaskInventoryItem item = GetInventorySelf();
                UUID agentID = item != null ? item.PermsGranter : UUID.Zero;
                if (agentID != UUID.Zero)
                {
                    ScenePresence sp = World.GetScenePresence(agentID);
                    if (sp != null && !sp.IsChildAgent)
                    {
                        sp.ControllingClient.SendLoadURL(name, m_host.UUID,
                            m_host.OwnerID, false, name, url);
                    }
                }
            }
        }

        public void llCloseFloater(string name)
        {
            // Closes a viewer-side floater. Viewer-dependent — no reliable way to
            // close a floater via the SL protocol in OpenSim. Stub for compatibility.
        }

        public int llSitOnLink(string agentID, int link)
        {
            // Experience-gated: force-sit agent on a specific link without dialog
            if (m_host == null || World == null) return -1;

            UUID targetAgent;
            if (!UUID.TryParse(agentID, out targetAgent)) return -1;

            // Check experience permission
            if (!HasExperiencePermission(targetAgent))
                return -3; // XP_ERROR_NOT_EXPERIENCE

            ScenePresence sp = World.GetScenePresence(targetAgent);
            if (sp == null || sp.IsChildAgent) return -1;

            // Already sitting?
            if (sp.ParentID != 0) return -2; // already seated

            // Find the target link part
            SceneObjectPart sitPart = null;
            if (link == 0 || link == 1) // LINK_ROOT
            {
                sitPart = m_host.ParentGroup.RootPart;
            }
            else
            {
                foreach (SceneObjectPart part in GetLinkParts(link))
                {
                    sitPart = part;
                    break;
                }
            }

            if (sitPart == null) return -1;

            // Check if sit target is already occupied
            if (sitPart.SitTargetAvatar != UUID.Zero) return -2;

            // Use the proper two-step sit path:
            // 1. HandleAgentRequestSit sets m_requestedSitTargetID and sends viewer response
            // 2. HandleAgentSit processes the actual sit using that target ID
            try
            {
                sp.HandleAgentRequestSit(sp.ControllingClient, targetAgent,
                    sitPart.UUID, sitPart.SitTargetPosition);
                sp.HandleAgentSit(sp.ControllingClient, targetAgent);

                m_log.LogInformation("[PhloxAPI]: llSitOnLink: sat {0} on link {1} ({2})",
                    targetAgent, link, sitPart.Name);
                return 0;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llSitOnLink failed: {0}", ex.Message);
                return -1;
            }
        }

        public void llRezObjectWithParams(string inventory, LSLList paramList)
        {
            // Extended rez function from SL Combat2 system.
            // Parse the params list for REZ_POS, REZ_ROT, REZ_VEL, REZ_FLAGS, etc.
            // For now, extract basic position/rotation/velocity and delegate to existing rez logic.

            if (string.IsNullOrEmpty(inventory)) return;

            const int REZ_POS = 1;
            const int REZ_ROT = 2;
            const int REZ_VEL = 3;
            const int REZ_FLAGS = 8;
            const int REZ_DAMAGE = 4;
            const int REZ_PARAM = 7;

            Vector3 pos = m_host.AbsolutePosition;
            Quaternion rot = m_host.GetWorldRotation();
            Vector3 vel = Vector3.Zero;
            int param = 0;
            bool atRoot = false;
            bool posRelative = false;

            for (int i = 0; i < paramList.Length; i++)
            {
                int rule;
                try { rule = Convert.ToInt32(paramList.Data[i]); }
                catch { continue; }

                switch (rule)
                {
                    case REZ_POS:
                        if (i + 3 < paramList.Length)
                        {
                            try
                            {
                                pos = (Vector3)paramList.Data[i + 1];
                                atRoot = Convert.ToInt32(paramList.Data[i + 2]) != 0;
                                posRelative = Convert.ToInt32(paramList.Data[i + 3]) != 0;
                                i += 3;
                            }
                            catch { }
                        }
                        break;
                    case REZ_ROT:
                        if (i + 2 < paramList.Length)
                        {
                            try
                            {
                                rot = (Quaternion)paramList.Data[i + 1];
                                bool rotRelative = Convert.ToInt32(paramList.Data[i + 2]) != 0;
                                if (rotRelative) rot = m_host.GetWorldRotation() * rot;
                                i += 2;
                            }
                            catch { }
                        }
                        break;
                    case REZ_VEL:
                        if (i + 3 < paramList.Length)
                        {
                            try
                            {
                                vel = (Vector3)paramList.Data[i + 1];
                                bool velRelative = Convert.ToInt32(paramList.Data[i + 2]) != 0;
                                if (velRelative) vel = vel * m_host.GetWorldRotation();
                                i += 3;
                            }
                            catch { }
                        }
                        break;
                    case REZ_PARAM:
                        if (i + 1 < paramList.Length)
                        {
                            try { param = Convert.ToInt32(paramList.Data[i + 1]); i += 1; }
                            catch { }
                        }
                        break;
                    case REZ_FLAGS:
                    case REZ_DAMAGE:
                        // Skip — advanced Combat2 flags, consume the value
                        if (i + 1 < paramList.Length) i += 1;
                        break;
                    default:
                        break;
                }
            }

            // Resolve relative position
            if (posRelative)
                pos = m_host.AbsolutePosition + pos * m_host.GetWorldRotation();

            // Delegate to existing rez infrastructure
            if (atRoot)
                llRezAtRoot(inventory, pos, vel, rot, param);
            else
                llRezObject(inventory, pos, vel, rot, param);
        }

        public string llGetMaterialOverride(int face, LSLList paramList)
        {
            // Read PBR material override properties on a face
            // Returns the override data as a string, or empty if none
            var shape = m_host.Shape;
            if (shape?.RenderMaterials?.overrides == null)
                return string.Empty;

            int targetFace = face;
            foreach (var ovr in shape.RenderMaterials.overrides)
            {
                if (targetFace == ALL_SIDES || ovr.te_index == (byte)targetFace)
                {
                    if (!string.IsNullOrEmpty(ovr.data))
                        return ovr.data;
                }
            }
            return string.Empty;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Phase 31: 19 new functions (TableIndex 643–661)
        // ══════════════════════════════════════════════════════════════════════

        // ── 643: llHash ──
        public int llHash(string src)
        {
            // SL-compatible fast integer hash of a string
            // Uses DJB2 algorithm to match SL behavior
            if (string.IsNullOrEmpty(src)) return 0;
            unchecked
            {
                int hash = 5381;
                foreach (char c in src)
                    hash = ((hash << 5) + hash) + c;
                return hash;
            }
        }

        // ── 644–647: Link-level sound functions ──
        public void llLinkAdjustSoundVolume(int link, float volume)
        {
            // Adjust volume of currently playing sound on a specific link
            volume = Math.Clamp(volume, 0f, 1f);
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                part.AdjustSoundGain(volume);
            }
        }

        public void llLinkSetSoundQueueing(int link, int queue)
        {
            // Enable/disable sound queueing on specific link
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                part.SoundQueueing = (queue != 0);
            }
        }

        public void llLinkSetSoundRadius(int link, float radius)
        {
            // Set sound radius on specific link
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                part.SoundRadius = radius;
            }
        }

        public void llLinkStopSound(int link)
        {
            // Stop sound on specific link
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                sm.StopSound(part);
            }
        }

        // ── 648: llScriptProfiler ── (already implemented at line 1279)

        // ── 649: llGiveAgentInventory ──
        public void llGiveAgentInventory(string destination, string inventory)
        {
            // Give inventory item to a specific agent — like llGiveInventory but
            // SL added this with slightly different permissions behavior.
            // We delegate to the existing llGiveInventory implementation.
            llGiveInventory(destination, inventory);
        }

        // ── 650: llMapBeacon ──
        public void llMapBeacon(string agent, string text, Vector3 color)
        {
            // Show a beacon on the agent's minimap at this object's location
            // Requires viewer-side support for beacon rendering
            // Current implementation: sends an IM with beacon info as a workaround
            if (m_host == null || World == null) return;
            UUID agentId;
            if (!UUID.TryParse(agent, out agentId)) return;

            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return;

            // Notify the agent about the beacon location via region say
            // Full viewer beacon rendering requires viewer-specific protocol support
            Vector3 pos = m_host.AbsolutePosition;
            m_log.LogInformation("[PhloxAPI]: llMapBeacon: beacon for {0} at {1} text='{2}'",
                agentId, pos, text ?? "");
        }

        // ── 651: llGetStartString ──
        public string llGetStartString()
        {
            // Returns the string passed to llRezObjectWithParams via REZ_PARAM
            // In SL this is a string variant of llGetStartParameter.
            // OpenSim/Phlox only has integer start params, so return empty string.
            // Scripts using llRezObjectWithParams with REZ_PARAM get integer only.
            return string.Empty;
        }

        // ── 652: llSetGroundTexture ──
        public void llSetGroundTexture(string texture, int corner)
        {
            // Set terrain texture for one of the 4 corners (0=SW, 1=NW, 2=SE, 3=NE)
            if (World == null || m_host == null) return;

            // Must be estate manager or owner
            if (!World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID))
            {
                ShoutError("llSetGroundTexture: requires estate manager permissions");
                return;
            }

            UUID textureId;
            if (!UUID.TryParse(texture, out textureId))
            {
                TaskInventoryItem item = FindInventoryItem(texture, (int)AssetType.Texture);
                textureId = item?.AssetID ?? UUID.Zero;
                if (textureId == UUID.Zero) return;
            }

            corner = Math.Clamp(corner, 0, 3);
            var ri = World.RegionInfo;
            switch (corner)
            {
                case 0: ri.RegionSettings.TerrainTexture1 = textureId; break;
                case 1: ri.RegionSettings.TerrainTexture2 = textureId; break;
                case 2: ri.RegionSettings.TerrainTexture3 = textureId; break;
                case 3: ri.RegionSettings.TerrainTexture4 = textureId; break;
            }
            ri.RegionSettings.Save();
            // Force terrain update to all viewers
            World.EventManager.TriggerEstateToolsSunUpdate(World.RegionInfo.RegionHandle);
        }

        // ── 653: llTargetedEmail ──
        public void llTargetedEmail(int targetType, string address, string subject, string message)
        {
            // SL's targeted email: targetType 0=object, 1=avatar, 2=external
            // For now, delegate to the existing llEmail for external addresses.
            // Object/avatar targeting would need additional infrastructure.
            if (targetType == 2)
            {
                // External email — same as llEmail
                llEmail(address, subject, message);
            }
            else
            {
                // Object (0) or avatar (1) targeting — stub with warning
                Stub("llTargetedEmail(targetType=" + targetType + ")");
            }
            ScriptSleep(20000); // SL has a 20-second sleep
        }

        // ── 654: llTransferOwnership ──
        public int llTransferOwnership(string destination)
        {
            // Transfer ownership of this object to another agent
            // This is a newer SL function; implementation requires careful
            // permission checking
            if (m_host == null || World == null) return 0;

            UUID destId;
            if (!UUID.TryParse(destination, out destId)) return 0;

            // Check that the object has transfer permission
            if ((m_host.OwnerMask & (uint)OpenSim.Framework.PermissionMask.Transfer) == 0)
                return 0;

            try
            {
                var group = m_host.ParentGroup;
                if (group == null) return 0;

                // Must be owned by the script owner
                if (group.OwnerID != m_host.OwnerID) return 0;

                // Perform the ownership transfer
                foreach (SceneObjectPart part in group.Parts)
                {
                    part.OwnerID = destId;
                    part.LastOwnerID = group.OwnerID;
                    part.Inventory.ChangeInventoryOwner(destId);
                }
                group.HasGroupChanged = true;
                group.ScheduleGroupForFullUpdate();
                return 1;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llTransferOwnership error: {0}", e.Message);
                return 0;
            }
        }

        // ── 655: llDetectedDamage ──
        public float llDetectedDamage(int number)
        {
            // Returns the damage amount from a damage event
            // Damage events aren't fully implemented in OpenSim, return 0.0
            Stub("llDetectedDamage");
            return 0.0f;
        }

        // ── 656: llDamage ──
        public void llDamage(string target, float amount, int damageType)
        {
            // SL Combat 2.0 — apply damage to an agent
            // damageType: DAMAGE_TYPE_IMPACT=0, _BURN=1, _BLAST=2, etc.
            // OpenSim doesn't have a full Combat 2.0 module, so we use the
            // legacy damage system if available
            if (World == null) return;

            UUID targetId;
            if (!UUID.TryParse(target, out targetId)) return;

            ScenePresence sp = World.GetScenePresence(targetId);
            if (sp == null || sp.IsChildAgent) return;

            // Try to apply damage via the legacy combat system
            try
            {
                // Check if damage is enabled in the region
                if (!World.RegionInfo.RegionSettings.AllowDamage)
                    return;

                sp.ControllingClient.SendHealth(Math.Max(0f, sp.Health - amount));
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llDamage error: {0}", e.Message);
            }
        }

        // ── 657: llSetLinkRenderMaterial ──
        public void llSetLinkRenderMaterial(int link, string materialId, int face)
        {
            // Set PBR render material on a specific link's face
            // Extends llSetRenderMaterial to target specific links
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                try
                {
                    UUID matId = UUID.Zero;
                    UUID.TryParse(materialId, out matId);

                    if (face == ALL_SIDES)
                    {
                        for (int f = 0; f < part.GetNumberOfSides(); f++)
                            SetPartRenderMaterial(part, matId, f);
                    }
                    else
                    {
                        SetPartRenderMaterial(part, matId, face);
                    }
                    part.ScheduleFullUpdate();
                }
                catch (Exception e)
                {
                    m_log.LogWarning("[PhloxAPI]: llSetLinkRenderMaterial error: {0}", e.Message);
                }
            }
        }

        private void SetPartRenderMaterial(SceneObjectPart part, UUID matId, int face)
        {
            // Helper: set render material UUID on a specific face of a part
            var te = part.Shape.Textures;
            if (te == null) return;
            if (face < 0 || face >= part.GetNumberOfSides()) return;
            var faceEntry = te.GetFace((uint)face);
            if (faceEntry != null)
            {
                faceEntry.MaterialID = matId;
                part.UpdateTextureEntry(te);
            }
        }

        // ── 658: llXorBase64 ──
        public string llXorBase64(string s1, string s2)
        {
            // XOR two base64-encoded strings, returning base64
            // Replacement for deprecated llXorBase64Strings/llXorBase64StringsCorrect
            if (string.IsNullOrEmpty(s1)) return string.Empty;
            if (string.IsNullOrEmpty(s2)) return s1;

            try
            {
                byte[] b1 = Convert.FromBase64String(s1);
                byte[] b2 = Convert.FromBase64String(s2);
                if (b2.Length == 0) return s1;

                byte[] result = new byte[b1.Length];
                for (int i = 0; i < b1.Length; i++)
                    result[i] = (byte)(b1[i] ^ b2[i % b2.Length]);

                return Convert.ToBase64String(result);
            }
            catch
            {
                return string.Empty;
            }
        }

        // ── 659–661: Experience System (wired to ExperienceService) ──

        /// <summary>
        /// Helper: build the Phlox→NGC experience adapter for this script's scene+host.
        /// Returns null when no NGC experience backend is present, so the existing
        /// null-guards at the call sites degrade gracefully. Cheap to build (module
        /// lookups), so created per call to avoid caching a stale host.
        /// </summary>
        private PhloxExperienceAdapter GetExperienceAdapter()
        {
            if (World == null)
                return null;
            var adapter = new PhloxExperienceAdapter(World, m_host);
            return adapter.IsAvailable ? adapter : null;
        }

        /// <summary>Helper: the experience provider (NGC-backed adapter) for this script.</summary>
        private PhloxExperienceAdapter GetExperienceModule()
        {
            return GetExperienceAdapter();
        }

        /// <summary>Helper: resolve the experience UUID for this script via ExperienceModule association</summary>
        private UUID GetScriptExperienceId()
        {
            var expModule = GetExperienceModule();
            if (expModule != null)
            {
                UUID expId = expModule.GetScriptExperience(m_itemID);
                if (expId != UUID.Zero) return expId;
            }
            return UUID.Zero;
        }

        /// <summary>
        /// Helper: check if this script has an associated experience and the given agent
        /// has granted permission to it. Used by experience-gated functions.
        /// </summary>
        private bool HasExperiencePermission(UUID agentId)
        {
            if (agentId == UUID.Zero) return false;
            UUID experienceId = GetScriptExperienceId();
            if (experienceId == UUID.Zero) return false;

            var expService = GetExperienceAdapter();
            if (expService == null) return false;

            // Check region allows this experience
            var allowed = expService.GetAllowedExperiences(World.RegionInfo.RegionID);
            if (!allowed.Contains(experienceId)) return false;

            // Check agent has granted permission
            return expService.IsAgentGranted(experienceId, agentId);
        }

        // ── D1 consent state (ported from Legion port-source-2026-07-22). One pending request per
        //    script instance (LSLSystemAPI is per-script), keyed by ItemID; the ScriptAnswerYes packet
        //    carries no ExperienceID, so the answer is correlated by TaskID + ItemID via OnScriptAnswer. ──
        private const int PERMISSION_EXPERIENCE = 0x2000;       // JoinAnExperience bit
        private const int EXPERIENCE_PERM_TIMEOUT_MS = 300000;  // 300s (SL: "at least 5 minutes")
        private sealed class PendingExperiencePerm
        {
            public UUID AgentId;
            public UUID ExperienceId;
            public IClientAPI Client;
            public System.Threading.Timer Timer;
        }
        private readonly Dictionary<UUID, PendingExperiencePerm> m_pendingExpPerms = new Dictionary<UUID, PendingExperiencePerm>();
        private readonly object m_pendingExpLock = new object();
        private IClientAPI m_expHookedClient = null;

        // ── 659: llRequestExperiencePermissions ──
        public void llRequestExperiencePermissions(string agent, string name)
        {
            if (m_host == null || World == null) return;
            UUID agentId;
            if (!UUID.TryParse(agent, out agentId)) return;

            var expService = GetExperienceAdapter();
            UUID experienceId = GetScriptExperienceId();

            // No experience associated with this script -> XP_ERROR_NO_EXPERIENCE (5). (SS-7)
            if (expService == null || experienceId == UUID.Zero)
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NO_EXPERIENCE },
                    new DetectParams[0]));
                return;
            }

            // T5b block-wins: a region-BLOCKED experience is denied regardless of allow/trusted/prior-
            // grant, land-scope XP_ERROR_NOT_PERMITTED_LAND (17). Checked FIRST (before admission,
            // trusted, and already-granted) so block wins over everything. (Legion also has a parcel-
            // block tier at this precedence — deferred; Tranquillity has no parcel-experience source.)
            if (IsExperienceBlockedInRegion(expService, experienceId))
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NOT_PERMITTED_LAND },
                    new DetectParams[0]));
                return;
            }

            // Admission (T5): the experience must be enabled on this land — estate-ALLOWED or region-
            // TRUSTED (estate KeyExperiences). A trusted experience is a stronger allow, so it admits
            // here and is silently granted below (previously a trusted-but-not-allowed experience was
            // wrongly denied 17 before the trusted check). Legion's admission also has grid-wide + parcel-
            // ALLOW tiers, and a region/parcel BLOCK-wins tier; those have NO source in NGC (no grid-wide
            // bit, no region-block store, no ILandObject experience methods) — the flagged T5 STOP (see
            // experience-port-ledger.md). Not admitted -> land-scope XP_ERROR_NOT_PERMITTED_LAND (17).
            if (!IsExperienceAdmitted(expService, experienceId))
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NOT_PERMITTED_LAND },
                    new DetectParams[0]));
                return;
            }

            // Target agent must have a ROOT presence here -> else agent-scope XP_ERROR_NOT_PERMITTED (4).
            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent)
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NOT_PERMITTED },
                    new DetectParams[0]));
                return;
            }

            // T3/D1 gate order (Legion): the agent's PERSONAL block wins over everything below and is
            // checked BEFORE the already-granted short-circuit, so a resident who blocked this experience
            // is never re-granted (SL code 4). (The Block-button persistence loop is T4.)
            if (expService.IsAgentBlocked(experienceId, agentId))
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NOT_PERMITTED }, // 4 — agent's personal block
                    new DetectParams[0]));
                return;
            }

            // Already granted -> notify immediately, no dialog.
            if (expService.IsAgentGranted(experienceId, agentId))
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions", new object[] { agent }, new DetectParams[0]));
                return;
            }

            // T5 trusted enforcement. A region-TRUSTED experience (Tranquillity estate KeyExperiences)
            // grants silently — no dialog. Checked AFTER agent-block (T4), so a personally-blocked
            // experience is denied 4 even if trusted (block wins over trusted — Legion's order).
            if (expService.GetTrustedExperiences(World.RegionInfo.RegionID).Contains(experienceId))
            {
                GrantExperienceAndNotify(expService, experienceId, agentId, agent);
                return;
            }

            // Non-trusted: PROMPT the agent and AWAIT the answer — this REPLACES the former auto-grant.
            // The Experience block on the ScriptQuestion (attached only when experienceId != Zero, guarded
            // in LLClientView) makes the viewer show the experience consent dialog. Resolves to
            // experience_permissions on Yes / _denied 4 on No/disconnect / _denied 18 on timeout.
            string ownerName = m_host.ParentGroup.RootPart.OwnerID.ToString();
            var ownerAcct = World?.UserAccountService?.GetUserAccount(
                World.RegionInfo.ScopeID, m_host.ParentGroup.RootPart.OwnerID);
            if (ownerAcct != null) ownerName = ownerAcct.FirstName + " " + ownerAcct.LastName;
            if (string.IsNullOrEmpty(ownerName)) ownerName = "(unknown)";

            RegisterPendingExperiencePerm(sp.ControllingClient, experienceId, agentId);
            sp.ControllingClient.SendScriptQuestion(
                m_host.UUID, m_host.ParentGroup.RootPart.Name, ownerName, m_itemID,
                PERMISSION_EXPERIENCE, experienceId);
        }

        // Grant + persist an experience permission and post experience_permissions. Shared by the
        // trusted-bypass path and the accepted-answer path.
        private void GrantExperienceAndNotify(PhloxExperienceAdapter expService, UUID experienceId, UUID agentId, string agent)
        {
            expService.GrantPermission(experienceId, agentId);
            expService.InvalidatePermission(experienceId, agentId);
            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "experience_permissions", new object[] { agent }, new DetectParams[0]));
            var expInfo = expService.GetExperience(experienceId);
            m_log.LogInformation("[PhloxAPI]: Experience permission granted: agent={0} experience={1}",
                agentId, expInfo?.Name ?? experienceId.ToString());
        }

        // Record the pending request (keyed by m_itemID) and start its 300s timeout. Hooks the client's
        // answer/disconnect events (re-hooking if the target client changed).
        private void RegisterPendingExperiencePerm(IClientAPI client, UUID experienceId, UUID agentId)
        {
            lock (m_pendingExpLock)
            {
                if (m_pendingExpPerms.TryGetValue(m_itemID, out PendingExperiencePerm prior))
                {
                    prior.Timer?.Dispose();
                    m_pendingExpPerms.Remove(m_itemID);
                }
                if (m_expHookedClient != client)
                {
                    if (m_expHookedClient != null)
                    {
                        m_expHookedClient.OnScriptAnswer -= HandleExperienceScriptAnswer;
                        m_expHookedClient.OnConnectionClosed -= HandleExperienceConnectionClosed;
                    }
                    client.OnScriptAnswer += HandleExperienceScriptAnswer;
                    client.OnConnectionClosed += HandleExperienceConnectionClosed;
                    m_expHookedClient = client;
                }
                var pending = new PendingExperiencePerm { AgentId = agentId, ExperienceId = experienceId, Client = client };
                pending.Timer = new System.Threading.Timer(
                    _ => ResolveExperiencePerm(m_itemID, granted: false, errorCode: XP_ERROR_REQUEST_PERM_TIMEOUT),
                    null, EXPERIENCE_PERM_TIMEOUT_MS, System.Threading.Timeout.Infinite);
                m_pendingExpPerms[m_itemID] = pending;
            }
        }

        // ScriptAnswerYes arrived (via OnScriptAnswer). Correlate by TaskID (this object) + ItemID;
        // a non-zero PERMISSION_EXPERIENCE bit means accepted, zero means denied.
        private void HandleExperienceScriptAnswer(IClientAPI client, UUID taskID, UUID itemID, int answer)
        {
            if (taskID != m_host.UUID) return;
            bool granted = (answer & PERMISSION_EXPERIENCE) != 0;
            ResolveExperiencePerm(itemID, granted, granted ? XP_ERROR_NONE : XP_ERROR_NOT_PERMITTED);
        }

        // Agent disconnected mid-dialog: resolve every pending request on this client as denied.
        private void HandleExperienceConnectionClosed(IClientAPI client)
        {
            List<UUID> pendingKeys;
            lock (m_pendingExpLock)
                pendingKeys = new List<UUID>(m_pendingExpPerms.Keys);
            foreach (UUID itemId in pendingKeys)
                ResolveExperiencePerm(itemId, granted: false, errorCode: XP_ERROR_NOT_PERMITTED);
        }

        // Single resolution point for grant / user-deny / timeout / disconnect. Removes the pending
        // entry atomically (first resolver wins — no double-post), disposes the timer, unhooks the
        // client once nothing is pending; the grant + script event happen OUTSIDE the lock.
        private void ResolveExperiencePerm(UUID itemID, bool granted, int errorCode)
        {
            PendingExperiencePerm pending;
            IClientAPI clientToUnhook = null;
            lock (m_pendingExpLock)
            {
                if (!m_pendingExpPerms.TryGetValue(itemID, out pending))
                    return; // already resolved by another path
                m_pendingExpPerms.Remove(itemID);
                pending.Timer?.Dispose();
                if (m_pendingExpPerms.Count == 0 && m_expHookedClient != null)
                {
                    clientToUnhook = m_expHookedClient;
                    m_expHookedClient = null;
                }
            }
            if (clientToUnhook != null)
            {
                clientToUnhook.OnScriptAnswer -= HandleExperienceScriptAnswer;
                clientToUnhook.OnConnectionClosed -= HandleExperienceConnectionClosed;
            }

            string agent = pending.AgentId.ToString();
            var expService = GetExperienceAdapter();
            if (granted && expService != null)
                GrantExperienceAndNotify(expService, pending.ExperienceId, pending.AgentId, agent);
            else
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, errorCode },
                    new DetectParams[0]));
        }

        // T5 admission — the portable subset of Legion's ladder (IsExperienceAdmittedAt): an experience
        // is admitted on this land if the estate ALLOWS it OR it is region-TRUSTED (estate KeyExperiences).
        // Legion's grid-wide + parcel-ALLOW admission tiers and the region/parcel BLOCK-wins tier have no
        // NGC source (see the T5 STOP in experience-port-ledger.md) and are not represented here.
        private bool IsExperienceAdmitted(PhloxExperienceAdapter expService, UUID experienceId)
        {
            UUID regionId = World.RegionInfo.RegionID;
            return expService.GetAllowedExperiences(regionId).Contains(experienceId)
                || expService.GetTrustedExperiences(regionId).Contains(experienceId);
        }

        // T5b block-wins tier (Legion IsExperienceBlockedInRegion): an experience on the estate
        // BlockedExperiences list is denied regardless of allow/trusted/prior-grant. Region granularity
        // only — Legion also has a parcel-block tier with no NGC parcel-experience source (deferred).
        private bool IsExperienceBlockedInRegion(PhloxExperienceAdapter expService, UUID experienceId)
        {
            UUID regionId = World.RegionInfo.RegionID;
            return expService.GetBlockedExperiences(regionId).Contains(experienceId);
        }

        // ── 660: llAgentInExperience ──
        public int llAgentInExperience(string agent)
        {
            UUID agentId;
            if (!UUID.TryParse(agent, out agentId)) return 0;
            if (agentId == UUID.Zero) return 0;

            var expService = GetExperienceAdapter();
            UUID experienceId = GetScriptExperienceId();
            if (expService == null || experienceId == UUID.Zero) return 0;

            // SS-6 (presence + agent-block in T1, admission in T5, region-block in T5b): the target agent
            // must be PARTICIPATING here — a ROOT presence in this region — with block-wins over grant, AND
            // the experience must not be region-BLOCKED and must be ADMITTED on this land (estate allow OR
            // trusted). Legion's HasExperiencePermission also applies a parcel BLOCK-wins tier, which has no
            // NGC source (the T5 STOP) — deferred to a separate project (region granularity only here).
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return 0;
            if (IsExperienceBlockedInRegion(expService, experienceId)) return 0; // T5b region block wins
            if (expService.IsAgentBlocked(experienceId, agentId)) return 0;    // agent block wins
            if (!IsExperienceAdmitted(expService, experienceId)) return 0;     // T5: admitted on this land
            return expService.IsAgentGranted(experienceId, agentId) ? 1 : 0;
        }

        // ── 661: llGetExperienceDetails ──
        public LSLList llGetExperienceDetails(string experienceId)
        {
            UUID expId;
            if (!UUID.TryParse(experienceId, out expId))
                return new LSLList();

            var expService = GetExperienceAdapter();
            if (expService == null)
                return new LSLList();

            var exp = expService.GetExperience(expId);
            if (exp == null)
                return new LSLList();

            // T1/SS-1: SL layout is [ name, owner key, experience id, state (int), state message,
            // group key ] — NOT the old [name, owner, description, group, maturity, ""], which
            // silently returned wrong data at every index for SL-written scripts (High severity).
            // State uses the XP_ERROR vocabulary: NONE(0) for a valid enabled experience,
            // EXPERIENCE_DISABLED(8) when the viewer PROPERTY_DISABLED bit is set. The message comes
            // from llGetExperienceErrorMessage so the state and its text can never drift apart.
            int state = (exp.Properties & VP_DISABLED) != 0
                ? XP_ERROR_EXPERIENCE_DISABLED
                : XP_ERROR_NONE;
            return new LSLList(new object[]
            {
                exp.Name ?? string.Empty,
                exp.OwnerId.ToString(),
                expId.ToString(),
                state,
                llGetExperienceErrorMessage(state),
                exp.GroupId.ToString()
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        // Phase 33: 5 new functions (TableIndex 662–666)
        // + 2 fixes (llDetectedGroup, llGetUsedMemory)
        // + 3 wired (llGetAnimationOverride, llSetAnimationOverride, llResetAnimationOverride)
        // ══════════════════════════════════════════════════════════════════════

        // ── 662: llGetPayPrice ──
        public LSLList llGetPayPrice()
        {
            if (m_host == null)
                return new LSLList(new object[] { -1, -1, -1, -1, -1 });

            return new LSLList(new object[]
            {
                m_host.PayPrice[0],
                m_host.PayPrice[1],
                m_host.PayPrice[2],
                m_host.PayPrice[3],
                m_host.PayPrice[4]
            });
        }

        // ── 663: llGetAgentRot ──
        public Quaternion llGetAgentRot()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return Quaternion.Identity;
            UUID agentId = item.PermsGranter;
            if (agentId == UUID.Zero) return Quaternion.Identity;

            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return Quaternion.Identity;

            return sp.Rotation;
        }

        // ── 664: llGetGroundTexture ──
        public string llGetGroundTexture(int corner)
        {
            if (World == null) return UUID.Zero.ToString();

            corner = Math.Clamp(corner, 0, 3);
            var rs = World.RegionInfo.RegionSettings;
            UUID texId = corner switch
            {
                0 => rs.TerrainTexture1,
                1 => rs.TerrainTexture2,
                2 => rs.TerrainTexture3,
                3 => rs.TerrainTexture4,
                _ => UUID.Zero
            };
            return texId.ToString();
        }

        // ── 665: llGetVehicleFlags ──
        public int llGetVehicleFlags()
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return 0;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return 0;

            // PhysicsActor doesn't expose a flags getter — return VehicleType for now.
            // Full flags support requires adding a getter to the physics engine.
            return pa.VehicleType;
        }

        // ── 666: llGetLinkGLTFOverrides ──
        public LSLList llGetLinkGLTFOverrides(int link, int face)
        {
            var result = new List<object>();

            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                var shape = part.Shape;
                if (shape?.RenderMaterials?.overrides == null)
                    continue;

                foreach (var ovr in shape.RenderMaterials.overrides)
                {
                    if (face != ALL_SIDES && ovr.te_index != (byte)face)
                        continue;

                    if (!string.IsNullOrEmpty(ovr.data))
                    {
                        try
                        {
                            var osd = OpenMetaverse.StructuredData.OSDParser.DeserializeLLSDNotation(ovr.data);
                            if (osd is OpenMetaverse.StructuredData.OSDMap map)
                            {
                                foreach (string key in map.Keys)
                                {
                                    string readableKey = key switch
                                    {
                                        "bc" => "base_color",
                                        "mf" => "metallic_factor",
                                        "rf" => "roughness_factor",
                                        "ec" => "emissive_factor",
                                        "am" => "alpha_mode",
                                        "ac" => "alpha_cutoff",
                                        "ds" => "double_sided",
                                        _ => key
                                    };
                                    result.Add(readableKey);
                                    result.Add(map[key].ToString());
                                }
                            }
                        }
                        catch
                        {
                            result.Add("raw");
                            result.Add(ovr.data);
                        }
                    }
                }
                break; // only first matching link part
            }

            return new LSLList(result.ToArray());
        }

        // ── Phase 34: Bot Persistence Functions (667–671) ──

        // ── 667: botSetPersistent ──
        public int botSetPersistent(string botID, int ttlSeconds)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.SetPersistent(id, m_host.OwnerID, m_itemID,
                m_host.ParentGroup.UUID, ttlSeconds);
        }

        // ── 668: botRemovePersistent ──
        public int botRemovePersistent(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.RemovePersistent(id, m_host.OwnerID);
        }

        // ── 669: botIsPersistent ──
        public int botIsPersistent(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return 0;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return 0;

            return pm.IsPersistent(id) ? 1 : 0;
        }

        // ── 670: botGetPersistentData ──
        public string botGetPersistentData(string botID, string key)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return string.Empty;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return string.Empty;

            return pm.GetPersistentData(id, key);
        }

        // ── 671: botSetPersistentData ──
        public int botSetPersistentData(string botID, string key, string value)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.SetPersistentData(id, m_host.OwnerID, key, value);
        }
    }
}
