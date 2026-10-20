/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Delivery of the avatar-to-avatar voice invitation to the callee, and of any other invitation that goes
 * through the same function.
 *
 * The event is a generic ChatterBoxInvitation built with IEventQueue.BuildEvent + Enqueue -- the same path
 * GroupsMessagingModule uses for its ChatterBoxSessionStartReply (GroupsMessagingModule.cs). The
 * IEventQueue.ChatterboxInvitation HELPER IS NOT USED: it emits an instantmessage-only body, which the
 * viewer routes to its IM branch and auto-accepts as text (llimview.cpp:5047-5195). The
 * voice branch (llimview.cpp:5196-5214) needs a `voice` map, which only a hand-built body can carry.
 *
 * Callee resolution follows the group module's GetActiveClient (GroupsMessagingModule.cs and the
 * method itself): every scene this shared module serves, root presence preferred. The invitation is
 * delivered ONLY to a root presence. A callee whose viewer stands in a neighbouring region holds a
 * child presence here, and the viewer's answer goes to the region it stands in -- which may be another
 * server process that has no record of the call. A callee with no root presence here gets nothing from
 * this function. The region module then rings such a callee through the signed carrier, which lands in the
 * process that holds the callee's root presence (A2ACarrierTransport); without a carrier secret the
 * caller still receives its credentials and rings out, and the Invited TTL cleans up.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public static class A2AInviteDelivery
    {
        public const string EventName = "ChatterBoxInvitation";
        public const string InstrumentTag = "[A2A INVITE]";

        public const string DecisionSent = "sent";
        public const string DecisionNoPresence = "callee-unreachable(no-presence)";
        public const string DecisionChildOnly = "callee-unreachable(child-presence-only)";
        public const string DecisionNoQueue = "callee-unreachable(no-event-queue)";
        public const string DecisionEnqueueFailed = "callee-unreachable(enqueue-failed)";

        /// <summary>
        /// Find the callee on this instance: the first scene holding a ROOT presence for the agent, else the
        /// first holding a child presence (reported through <paramref name="isChild"/>), else null. Mirrors
        /// GroupsMessagingModule.GetActiveClient. Delivery of an invitation uses only the root answer.
        /// </summary>
        public static Scene ResolveCalleeScene(IEnumerable<Scene> scenes, UUID callee, out bool isChild)
        {
            isChild = false;
            Scene childScene = null;
            if (scenes == null)
                return null;
            foreach (Scene scene in scenes)
            {
                ScenePresence sp = scene?.GetScenePresence(callee);
                if (sp == null || sp.IsDeleted)
                    continue;
                if (!sp.IsChildAgent)
                    return scene;
                childScene ??= scene;
            }
            if (childScene != null)
                isChild = true;
            return childScene;
        }

        /// <summary>
        /// Deliver <paramref name="body"/> to <paramref name="callee"/> as a ChatterBoxInvitation event and
        /// return the decision string for the instrument. Only a root presence is rung: a callee who is just
        /// a child presence here answers <see cref="DecisionChildOnly"/> and nothing is enqueued. Never throws
        /// into the caller's request.
        /// </summary>
        /// <param name="queueOf">Resolves a scene's event queue; defaults to RequestModuleInterface. Injectable for tests.</param>
        public static string Deliver(IEnumerable<Scene> scenes, UUID callee, OSDMap body, Func<Scene, IEventQueue> queueOf, out string regionName)
        {
            regionName = "-";
            Scene scene = ResolveCalleeScene(scenes, callee, out bool isChild);
            if (scene == null)
                return DecisionNoPresence;
            regionName = scene.RegionInfo?.RegionName ?? "-";
            if (isChild)
                return DecisionChildOnly;

            IEventQueue queue;
            try
            {
                queue = queueOf != null ? queueOf(scene) : scene.RequestModuleInterface<IEventQueue>();
            }
            catch
            {
                queue = null;
            }
            if (queue == null)
                return DecisionNoQueue;

            try
            {
                return queue.Enqueue(queue.BuildEvent(EventName, body), callee) ? DecisionSent : DecisionEnqueueFailed;
            }
            catch
            {
                return DecisionEnqueueFailed;
            }
        }

        /// <summary>One greppable line. Never contains the token (the body is not rendered here).</summary>
        public static string Line(UUID callee, UUID caller, UUID sessionId, string regionName, string decision)
            => $"{InstrumentTag} agent={callee} from={caller} session-id={sessionId} region={regionName} decision={decision}";
    }
}
