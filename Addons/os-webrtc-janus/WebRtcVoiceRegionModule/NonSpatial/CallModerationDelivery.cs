/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What a group call's members are told about moderation, and the check on their group text.
 *
 * ROSTER FLAGS. The stock viewer shows its moderator menu in a group session only when its own entry in the
 * participant list says is_moderator (llfloaterimcontainer.cpp, LLFloaterIMContainer::isGroupModerator, which
 * reads the speaker's mIsModerator, set from ChatterBoxSessionAgentListUpdates info.is_moderator in
 * llspeakers.cpp). The groups module sends its entries with the flag false, so after a member joins the call the
 * module sends the member's own entry again with the flag true when the group's powers say so. The viewer applies
 * the latest entry it receives. can_voice_chat is true on every entry we send, as for conferences: a false value
 * is read by the viewer as a refusal.
 *
 * A TEXT MUTE reaches the muted member as the same entry with mutes.text true; the line itself is dropped by
 * the group chat gate before the groups module delivers it.
 *
 * MODERATED MODE ("mute all") reaches a member as ChatterBoxSessionUpdate info.moderated_mode.voice, which is
 * what lets the viewer's "unmute all" work (llspeakers.cpp, LLIMSpeakerMgr::processSessionUpdate).
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice.NonSpatial
{
    public static class CallModerationDelivery
    {
        public const string InstrumentTag = "[CALL ROSTER]";

        /// <summary>One entry for a member of a group call, ENTER, with the flags set from what is known.</summary>
        public static GroupChatListAgentUpdateData Entry(UUID agent, bool isModerator, bool mutedText)
            => new GroupChatListAgentUpdateData(agent, /*cv*/true, isModerator, mutedText, /*enter*/true);

        /// <summary>Send <paramref name="recipient"/> the entries. The decision word is the delivery's.</summary>
        public static string SendEntries(IEnumerable<Scene> scenes, UUID recipient, UUID sessionId,
                                         List<GroupChatListAgentUpdateData> entries, Func<Scene, IEventQueue> queueOf = null)
            => A2AAgentListDelivery.Deliver(scenes, recipient, sessionId, entries, queueOf);

        /// <summary>Tell one agent the call's moderated mode.</summary>
        public static string SendModeratedMode(IEnumerable<Scene> scenes, UUID recipient, UUID sessionId, bool moderated,
                                               Func<Scene, IEventQueue> queueOf = null)
        {
            Scene scene = A2AInviteDelivery.ResolveCalleeScene(scenes, recipient, out _);
            if (scene == null)
                return A2AAgentListDelivery.DecisionNoPresence;
            IEventQueue queue;
            try { queue = queueOf != null ? queueOf(scene) : scene.RequestModuleInterface<IEventQueue>(); }
            catch { queue = null; }
            if (queue == null)
                return A2AAgentListDelivery.DecisionNoQueue;
            try
            {
                queue.ChatterBoxSessionUpdate(sessionId, recipient, moderated);
                return A2AAgentListDelivery.DecisionSent;
            }
            catch
            {
                return A2AAgentListDelivery.DecisionSendFailed;
            }
        }

        public static string Line(UUID recipient, UUID sessionId, string what, string decision)
            => $"{InstrumentTag} agent={recipient} session-id={sessionId} what={what} decision={decision}";
    }

    /// <summary>
    /// The check the groups module asks before it delivers a line of group chat: a member whose text a moderator
    /// muted gets nothing through. Without the feature the module registers none, so every line goes as before.
    /// A record read from the grid service is kept for a few seconds, so a busy group's chat costs the service one
    /// question and not one per line; a change made in this process drops it at once.
    /// </summary>
    public sealed class CallGroupChatGate : IGroupChatGate
    {
        /// <summary>
        /// How long a copy of a call record is kept for the lines sent in between. A mute made in this process drops it at once
        /// (<see cref="Forget"/>); only a mute made through another region server takes this long to show.
        /// </summary>
        public static readonly TimeSpan Kept = TimeSpan.FromSeconds(10);

        private readonly INonSpatialSessions _sessions;
        private readonly bool _cache;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly Dictionary<UUID, (NonSpatialVoiceSession Session, DateTime Until)> _kept
            = new Dictionary<UUID, (NonSpatialVoiceSession, DateTime)>();

        /// <param name="cache">True for a record that is a copy (the grid service); the engine's own record is live.</param>
        public CallGroupChatGate(INonSpatialSessions sessions, bool cache, Func<DateTime> clock = null)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _cache = cache;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public bool MaySend(UUID groupID, UUID senderID)
        {
            NonSpatialVoiceSession session = Lookup(groupID);
            return session is null || !session.IsTextMuted(senderID);
        }

        /// <summary>Forget what was kept for this group (a moderation change was just made).</summary>
        public void Forget(UUID groupID)
        {
            lock (_lock)
                _kept.Remove(groupID);
        }

        private NonSpatialVoiceSession Lookup(UUID groupID)
        {
            if (!_cache)
                return _sessions.Get(groupID);
            DateTime now = _clock();
            lock (_lock)
                if (_kept.TryGetValue(groupID, out var held) && held.Until > now)
                    return held.Session;
            NonSpatialVoiceSession fresh = _sessions.Get(groupID, UUID.Zero, out bool unavailable);
            if (unavailable)
                return null;   // a line is not held back because the service did not answer
            lock (_lock)
            {
                if (_kept.Count > 512) _kept.Clear();
                _kept[groupID] = (fresh, now + Kept);
            }
            return fresh;
        }
    }
}
