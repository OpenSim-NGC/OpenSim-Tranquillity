/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
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

// Ported from Halcyon/InWorldz to Legion Grid (dotnet10-modernization)
// Adaptations:
//   - OpenSim.Framework.Communications.Cache removed (not present in modern OpenSim)
//   - Bot/ScenePresence scanning paths removed (iw* bot functions not ported)
//   - PostScriptEvent by itemID used for sensor/no_sensor events

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;

namespace OpenSim.Region.ScriptEngine.Shared.Api.Plugins
{
    public class SensorRepeat
    {
        public AsyncCommandManager m_CmdManager;

        public SensorRepeat(AsyncCommandManager CmdManager)
        {
            m_CmdManager = CmdManager;
            maximumRange = CmdManager.m_ScriptEngine.Config.GetDouble("SensorMaxRange", 96.0d);
            maximumToReturn = CmdManager.m_ScriptEngine.Config.GetInt("SensorMaxResults", 16);
        }

        private readonly object SenseLock = new object();

        private const int AGENT    = 1;
        private const int ACTIVE   = 2;
        private const int PASSIVE  = 4;
        private const int SCRIPTED = 8;

        private double maximumRange    = 96.0;
        private int    maximumToReturn = 16;

        private class SenseRepeatClass
        {
            public uint        localID;
            public UUID        itemID;
            public double      interval;
            public DateTime    next;
            public string      name;
            public UUID        keyID;
            public int         type;
            public double      range;
            public double      arc;
            public ISceneEntity host;
        }

        private class SensedEntity : IComparable
        {
            public SensedEntity(double detectedDistance, UUID detectedID)
            {
                distance = detectedDistance;
                itemID   = detectedID;
            }

            public int CompareTo(object obj)
            {
                if (obj is not SensedEntity ent)
                    throw new InvalidOperationException();
                if (ent.distance < distance) return 1;
                if (ent.distance > distance) return -1;
                return 0;
            }

            public UUID   itemID;
            public double distance;
        }

        private List<SenseRepeatClass> SenseRepeaters     = new List<SenseRepeatClass>();
        private readonly object        SenseRepeatListLock = new object();

        public void SetSenseRepeatEvent(uint m_localID, UUID m_itemID,
            string name, UUID keyID, int type, double range,
            double arc, double sec, ISceneEntity host)
        {
            // Always remove first in case this is a re-set
            UnSetSenseRepeaterEvents(m_localID, m_itemID);
            if (sec == 0)
                return;

            SenseRepeatClass ts = new SenseRepeatClass();
            ts.localID   = m_localID;
            ts.itemID    = m_itemID;
            ts.interval  = sec;
            ts.name      = name;
            ts.keyID     = keyID;
            ts.type      = type;
            ts.range     = range > maximumRange ? maximumRange : range;
            ts.arc       = arc;
            ts.host      = host;
            ts.next      = DateTime.UtcNow.AddSeconds(ts.interval);

            lock (SenseRepeatListLock)
                SenseRepeaters.Add(ts);
        }

        public void UnSetSenseRepeaterEvents(uint m_localID, UUID m_itemID)
        {
            lock (SenseRepeatListLock)
            {
                SenseRepeaters = SenseRepeaters.FindAll(
                    ts => ts.localID != m_localID || ts.itemID != m_itemID);
            }
        }

        public void CheckSenseRepeaterEvents()
        {
            if (SenseRepeaters.Count == 0)
                return;

            lock (SenseRepeatListLock)
            {
                foreach (SenseRepeatClass ts in SenseRepeaters)
                {
                    if (ts.next < DateTime.UtcNow)
                    {
                        SensorSweep(ts);
                        ts.next = DateTime.UtcNow.AddSeconds(ts.interval);
                    }
                }
            }
        }

        public void SenseOnce(uint m_localID, UUID m_itemID,
            string name, UUID keyID, int type,
            double range, double arc, ISceneEntity host)
        {
            SenseRepeatClass ts = new SenseRepeatClass();
            ts.localID   = m_localID;
            ts.itemID    = m_itemID;
            ts.interval  = 0;
            ts.name      = name;
            ts.keyID     = keyID;
            ts.type      = type;
            ts.range     = range > maximumRange ? maximumRange : range;
            ts.arc       = arc;
            ts.host      = host;
            SensorSweep(ts);
        }

        private void SensorSweep(SenseRepeatClass ts)
        {
            if (ts.host == null)
                return;

            List<SensedEntity> sensedEntities = new List<SensedEntity>();

            if ((ts.type & AGENT) != 0 && (ts.type & SCRIPTED) == 0)
                sensedEntities.AddRange(doAgentSensor(ts));

            if ((ts.type & SCRIPTED) != 0 || (ts.type & PASSIVE) != 0 || (ts.type & ACTIVE) != 0)
                sensedEntities.AddRange(doObjectSensor(ts));

            lock (SenseLock)
            {
                if (sensedEntities.Count == 0)
                {
                    m_CmdManager.m_ScriptEngine.PostScriptEvent(ts.itemID,
                        new EventParams("no_sensor", new object[0],
                        Array.Empty<DetectParams>()));
                }
                else
                {
                    sensedEntities.Sort();

                    List<DetectParams> detected = new List<DetectParams>();
                    foreach (SensedEntity se in sensedEntities)
                    {
                        try
                        {
                            DetectParams detect = new DetectParams();
                            detect.Key = se.itemID;
                            detect.Populate(m_CmdManager.m_ScriptEngine.World);
                            detected.Add(detect);
                        }
                        catch
                        {
                            // Object deleted or avatar gone during Populate — skip
                        }

                        if (detected.Count == maximumToReturn)
                            break;
                    }

                    if (detected.Count == 0)
                    {
                        m_CmdManager.m_ScriptEngine.PostScriptEvent(ts.itemID,
                            new EventParams("no_sensor", new object[0],
                            Array.Empty<DetectParams>()));
                    }
                    else
                    {
                        m_CmdManager.m_ScriptEngine.PostScriptEvent(ts.itemID,
                            new EventParams("sensor",
                            new object[] { detected.Count },
                            detected.ToArray()));
                    }
                }
            }
        }

        private List<SensedEntity> doObjectSensor(SenseRepeatClass ts)
        {
            List<EntityBase>   Entities;
            List<SensedEntity> sensedEntities = new List<SensedEntity>();

            if (ts.keyID != UUID.Zero)
            {
                if (!m_CmdManager.m_ScriptEngine.World.Entities.TryGetValue(ts.keyID, out EntityBase e) || e == null)
                    return sensedEntities;
                Entities = new List<EntityBase> { e };
            }
            else
            {
                Entities = new List<EntityBase>(m_CmdManager.m_ScriptEngine.World.GetEntities());
            }

            ISceneEntity SensePoint    = ts.host;
            Vector3      fromRegionPos = SensePoint.AbsolutePosition;

            Quaternion r           = GetWorldRotation(SensePoint);
            Vector3    forward_dir = new Vector3(1, 0, 0) * r;
            double     mag_fwd     = Vector3.Mag(forward_dir);
            Vector3    ZeroVector  = Vector3.Zero;
            bool       nameSearch  = !string.IsNullOrEmpty(ts.name);

            foreach (EntityBase ent in Entities)
            {
                bool keep = true;

                if (nameSearch && ent.Name != ts.name) continue;
                if (ent.IsDeleted)                     continue;
                if (ent is not SceneObjectGroup sog)   continue;
                // InTransit not available on SceneObjectGroup in this OpenSim version

                Vector3 toRegionPos = ent.AbsolutePosition;
                float   dx          = toRegionPos.X - fromRegionPos.X;
                float   dy          = toRegionPos.Y - fromRegionPos.Y;
                float   dz          = toRegionPos.Z - fromRegionPos.Z;

                double dis = (Math.Abs(dx) > ts.range || Math.Abs(dy) > ts.range || Math.Abs(dz) > ts.range)
                    ? ts.range + 1.0
                    : Math.Sqrt(dx * dx + dy * dy + dz * dz);

                if (!keep || dis > ts.range || ts.host.UUID == ent.UUID)
                    continue;

                int objtype = 0;
                SceneObjectPart part = sog.RootPart;
                if (sog.AttachmentPoint != 0) continue;

                if (part.Inventory.ContainsScripts())
                    objtype |= ACTIVE | SCRIPTED;
                else if (part.Velocity.Equals(ZeroVector))
                    objtype |= PASSIVE;
                else
                    objtype |= ACTIVE;

                if ((ts.type & objtype) == 0)
                    continue;

                if (ts.arc < Math.PI)
                {
                    try
                    {
                        Vector3 obj_dir = toRegionPos - fromRegionPos;
                        double  dot     = Vector3.Dot(forward_dir, obj_dir);
                        double  mag_obj = Vector3.Mag(obj_dir);
                        double  ang_obj = Math.Acos(dot / (mag_fwd * mag_obj));
                        if (ang_obj > ts.arc) keep = false;
                    }
                    catch { }
                }

                if (keep)
                    sensedEntities.Add(new SensedEntity(dis, ent.UUID));
            }

            return sensedEntities;
        }

        private List<SensedEntity> doAgentSensor(SenseRepeatClass ts)
        {
            List<ScenePresence> Presences;
            List<SensedEntity>  sensedEntities = new List<SensedEntity>();

            if (ts.keyID != UUID.Zero)
            {
                ScenePresence p = m_CmdManager.m_ScriptEngine.World.GetScenePresence(ts.keyID);
                if (p == null) return sensedEntities;
                Presences = new List<ScenePresence> { p };
            }
            else
            {
                Presences = m_CmdManager.m_ScriptEngine.World.GetScenePresences();
            }

            if (Presences.Count == 0)
                return sensedEntities;

            ISceneEntity SensePoint    = ts.host;
            Vector3      fromRegionPos = SensePoint.AbsolutePosition;
            Quaternion   r             = GetWorldRotation(SensePoint);
            Vector3      forward_dir   = new Vector3(1, 0, 0) * r;
            double       mag_fwd       = Vector3.Mag(forward_dir);
            bool         attached      = GetAttachmentPoint(SensePoint) != 0;
            bool         nameSearch    = !string.IsNullOrEmpty(ts.name);

            foreach (ScenePresence presence in Presences)
            {
                bool keep = true;

                if (presence.IsDeleted || presence.IsInTransit) continue;
                if (presence.IsChildAgent) keep = false;

                Vector3 toRegionPos = presence.AbsolutePosition;
                double  dis         = Math.Abs(Util.GetDistanceTo(toRegionPos, fromRegionPos));

                if (keep && dis <= ts.range)
                {
                    if (attached && presence.UUID == GetOwnerID(SensePoint))
                        keep = false;

                    if (keep && nameSearch && ts.name != presence.Name)
                        keep = false;

                    if (keep && ts.arc < Math.PI)
                    {
                        try
                        {
                            Vector3 obj_dir = toRegionPos - fromRegionPos;
                            double  dot     = Vector3.Dot(forward_dir, obj_dir);
                            double  mag_obj = Vector3.Mag(obj_dir);
                            double  ang_obj = Math.Acos(dot / (mag_fwd * mag_obj));
                            if (ang_obj > ts.arc) keep = false;
                        }
                        catch { }
                    }
                }
                else
                {
                    keep = false;
                }

                if (keep && presence.IsGod)
                    keep = false;

                if (keep)
                    sensedEntities.Add(new SensedEntity(dis, presence.UUID));

                if (nameSearch && ts.name == presence.Name)
                    return sensedEntities;
            }

            return sensedEntities;
        }

        private static Quaternion GetWorldRotation(ISceneEntity SensePoint)
        {
            if (SensePoint is SceneObjectPart sop) return sop.GetWorldRotation();
            if (SensePoint is ScenePresence sp)    return sp.Rotation;
            return Quaternion.Identity;
        }

        private static int GetAttachmentPoint(ISceneEntity SensePoint)
        {
            if (SensePoint is SceneObjectPart sop) return (int)sop.ParentGroup.AttachmentPoint;
            return 0;
        }

        private static UUID GetOwnerID(ISceneEntity SensePoint)
        {
            if (SensePoint is SceneObjectPart sop) return sop.OwnerID;
            return UUID.Zero;
        }

        public object[] GetSerializationData(UUID itemID)
        {
            List<object> data = new List<object>();

            foreach (SenseRepeatClass ts in SenseRepeaters)
            {
                if (ts.itemID == itemID && ts.host is SceneObjectPart)
                {
                    data.Add(ts.interval);
                    data.Add(ts.name);
                    data.Add(ts.keyID);
                    data.Add(ts.type);
                    data.Add(ts.range);
                    data.Add(ts.arc);
                }
            }

            return data.ToArray();
        }

        public void CreateFromData(uint localID, UUID itemID, UUID objectID, object[] data)
        {
            SceneObjectPart part =
                m_CmdManager.m_ScriptEngine.World.GetSceneObjectPart(objectID);

            if (part == null)
                return;

            int idx = 0;
            while (idx < data.Length)
            {
                SenseRepeatClass ts = new SenseRepeatClass();
                ts.localID  = localID;
                ts.itemID   = itemID;
                ts.interval = (double)data[idx];
                ts.name     = (string)data[idx + 1];
                ts.keyID    = (UUID)data[idx + 2];
                ts.type     = (int)data[idx + 3];
                ts.range    = (double)data[idx + 4];
                ts.arc      = (double)data[idx + 5];
                ts.host     = part;
                ts.next     = DateTime.UtcNow.AddSeconds(ts.interval);
                SenseRepeaters.Add(ts);
                idx += 6;
            }
        }
    }
}
