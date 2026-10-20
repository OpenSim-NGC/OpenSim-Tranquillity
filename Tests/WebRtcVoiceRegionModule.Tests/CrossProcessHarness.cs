/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Shared fakes for tests that stand in for one region-server process: a scene with root or child
 * presences, an event queue that records what was enqueued to whom, a message-transfer module that
 * records the carrier messages a process sends to other processes, and a presence service that says
 * which agents are online.
 */
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using PresenceInfo = OpenSim.Services.Interfaces.PresenceInfo;
using OpenSim.Tests.Common;

namespace osWebRtcVoice.Tests
{
    internal static class CrossProcessHarness
    {
        /// <summary>Sets a private instance (or static) field, as the module's own tests do.</summary>
        internal static void SetPrivate(object target, string field, object value)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? t.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(t.Name, field);
            f.SetValue(f.IsStatic ? null : target, value);
        }

        internal static object GetPrivate(object target, string field)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(t.Name, field);
            return f.GetValue(target);
        }

        internal static object Invoke(object target, string method, params object[] args)
        {
            MethodInfo m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException(target.GetType().Name, method);
            return m.Invoke(target, args);
        }

        /// <summary>A scene for one region-server process, wired to the fakes below.</summary>
        internal sealed class Process
        {
            public Scene Scene { get; }
            public RecordingEventQueue Queue { get; } = new RecordingEventQueue();
            public RecordingTransfer Transfer { get; } = new RecordingTransfer();
            public FakePresence Presence { get; } = new FakePresence();

            public Process(string name = "Region One", uint x = 1000, uint y = 1000)
            {
                Scene = new SceneHelpers().SetupScene(name, UUID.Random(), x, y);
                Scene.RegisterModuleInterface<IEventQueue>(Queue);
                Scene.RegisterModuleInterface<IMessageTransferModule>(Transfer);
                UseFakePresence();
            }

            // Scene caches its presence service on first use. SceneHelpers needs the real local one while it
            // adds a presence, so the cache is cleared for that and set to the fake afterwards.
            private void UseFakePresence() => SetBase(Scene, "m_PresenceService", Presence);

            public ScenePresence AddRoot(UUID agent)
            {
                SetBase(Scene, "m_PresenceService", null);
                ScenePresence sp = SceneHelpers.AddScenePresence(Scene, agent);
                UseFakePresence();
                return sp;
            }

            public ScenePresence AddChild(UUID agent)
            {
                ScenePresence sp = AddRoot(agent);
                sp.IsChildAgent = true;
                return sp;
            }

            private static void SetBase(Scene scene, string field, object value)
            {
                for (Type t = scene.GetType(); t != null; t = t.BaseType)
                {
                    FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
                    if (f != null) { f.SetValue(scene, value); return; }
                }
                throw new MissingFieldException(scene.GetType().Name, field);
            }
        }

        internal sealed class FakePresence : IPresenceService
        {
            private readonly HashSet<UUID> _online = new();
            public void SetOnline(params UUID[] agents) { foreach (UUID a in agents) _online.Add(a); }

            public PresenceInfo[] GetAgents(string[] userIDs)
            {
                List<PresenceInfo> found = new();
                foreach (string id in userIDs)
                    if (UUID.TryParse(id, out UUID u) && _online.Contains(u))
                        found.Add(new PresenceInfo { UserID = id, RegionID = UUID.Random() });
                return found.ToArray();
            }

            public bool LoginAgent(string userID, UUID sessionID, UUID secureSessionID) => true;
            public bool LogoutAgent(UUID sessionID) => true;
            public bool LogoutRegionAgents(UUID regionID) => true;
            public bool ReportAgent(UUID sessionID, UUID regionID) => true;
            public PresenceInfo GetAgent(UUID sessionID) => null;
        }

        internal sealed class RecordingTransfer : IMessageTransferModule
        {
            public readonly List<GridInstantMessage> Sent = new();
            /// <summary>The ToRootOnly flag each message in <see cref="Sent"/> was sent with, in the same order.</summary>
            public readonly List<bool> SentRootOnly = new();
            public event UndeliveredMessage OnUndeliveredMessage { add { } remove { } }

            public void SendInstantMessage(GridInstantMessage im, MessageResultNotification result, bool ToRootOnly = false)
            {
                Sent.Add(im);
                SentRootOnly.Add(ToRootOnly);
                result?.Invoke(true);
            }

            public void HandleUndeliverableMessage(GridInstantMessage im, MessageResultNotification result) { }
        }

        /// <summary>Records which agents had an event enqueued; every other member is unreachable from the code under test.</summary>
        internal sealed class RecordingEventQueue : IEventQueue
        {
            public readonly List<UUID> Enqueued = new();
            /// <summary>Each agent-list update sent: the session, the agent it went to and the entries.</summary>
            public readonly List<(UUID Session, UUID To, List<GroupChatListAgentUpdateData> Updates)> AgentLists = new();
            /// <summary>The body of the last event built, per recipient of each Enqueue.</summary>
            public readonly List<(string Name, OSD Body, UUID To)> Events = new();
            private string _lastName;
            private OSD _lastBody;

            public byte[] BuildEvent(string eventName, OSD eventBody) { _lastName = eventName; _lastBody = eventBody; return new byte[] { 1 }; }
            public bool Enqueue(byte[] o, UUID avatarID) { Enqueued.Add(avatarID); Events.Add((_lastName, _lastBody, avatarID)); return true; }
            public bool Enqueue(OSD o, UUID avatarID) => throw new NotSupportedException();
            public bool Enqueue(osUTF8 o, UUID avatarID) => throw new NotSupportedException();
            public void EnableSimulator(ulong handle, IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void EstablishAgentCommunication(UUID avatarID, IPEndPoint endPoint, string capsPath, ulong regionHandle, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void TeleportFinishEvent(ulong regionHandle, byte simAccess, IPEndPoint regionExternalEndPoint, uint locationID, uint flags, string capsURL, UUID agentID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, IPEndPoint newRegionExternalEndPoint, string capsURL, UUID avatarID, UUID sessionID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void ChatterboxInvitation(UUID sessionID, string sessionName, UUID fromAgent, string message, UUID toAgent, string fromName, byte dialog, uint timeStamp, bool offline, int parentEstateID, Vector3 position, uint ttl, UUID transactionID, bool fromGroup, byte[] binaryBucket) => throw new NotSupportedException();
            public void ChatterBoxSessionStartReply(UUID sessionID, string sessionName, int type, bool voiceEnabled, bool voiceModerated, UUID tmpSessionID, bool sucess, string error, UUID toAgent) { }
            public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID toAgent, List<GroupChatListAgentUpdateData> updates)
                => AgentLists.Add((sessionID, toAgent, updates));
            public void ChatterBoxForceClose(UUID toAgent, UUID sessionID, string reason) => throw new NotSupportedException();
            public void GroupMembershipData(UUID receiverAgent, GroupMembershipData[] data) => throw new NotSupportedException();
            public void ScriptRunningEvent(UUID objectID, UUID itemID, bool running, UUID avatarID) => throw new NotSupportedException();
            public void partPhysicsProperties(uint localID, byte physhapetype, float density, float friction, float bounce, float gravmod, UUID avatarID) => throw new NotSupportedException();
            public void WindlightRefreshEvent(int interpolate, UUID avatarID) => throw new NotSupportedException();
            public void SendEnvironmentUpdate(UUID experience_id, UUID agent_id, EnvironmentUpdate update) => throw new NotSupportedException();
            public void SendBulkUpdateInventoryItem(InventoryItemBase item, UUID avatarID, UUID? transationID = null) => throw new NotSupportedException();
            public osUTF8 StartEvent(string eventName) => throw new NotSupportedException();
            public osUTF8 StartEvent(string eventName, int cap) => throw new NotSupportedException();
            public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<byte[]> message) => throw new NotSupportedException();
            public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<string> message) => throw new NotSupportedException();
            public byte[] EndEventToBytes(osUTF8 sb) => throw new NotSupportedException();
        }
    }
}
