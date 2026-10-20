/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Net;

using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;

using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice;

// The region console commands for moderating a group call or a conference (see CallModerationCommands).
public partial class WebRtcVoiceRegionModule
{
    private CallModerationCommands m_callCommands;
    private static bool s_callCommandsRegistered;

    /// <summary>The call moderation console commands, or null when call moderation is off.</summary>
    public CallModerationCommands CallCommands => m_callCommands;

    private void RegisterCallConsole()
    {
        m_callCommands = new CallModerationCommands(new CallConsoleHost(this));
        lock (typeof(WebRtcVoiceRegionModule))
        {
            if (s_callCommandsRegistered)
                return;
            s_callCommandsRegistered = true;
        }
        m_callCommands.Register();
    }

    private sealed class CallConsoleHost : ICallConsoleHost
    {
        private readonly WebRtcVoiceRegionModule m_module;
        public CallConsoleHost(WebRtcVoiceRegionModule module) { m_module = module; }

        public IReadOnlyList<NonSpatialVoiceSession> Calls() => m_module.m_nonSpatial?.Listing() ?? new List<NonSpatialVoiceSession>();

        public NonSpatialVoiceSession Call(UUID id) => m_module.m_nonSpatial?.Get(id);

        public string NameOf(UUID agent)
        {
            foreach (Scene scene in m_module.CallScenes())
            {
                ScenePresence sp = scene.GetScenePresence(agent);
                if (sp is not null && !sp.IsDeleted)
                    return sp.Name;
            }
            return null;
        }

        public bool IsModerator(NonSpatialVoiceSession call, UUID agent) => m_module.IsCallModerator(call.Owner, agent);

        public HttpStatusCode Moderate(NonSpatialVoiceSession call, ModerationOp op, UUID target, bool on, out string decision)
        {
            ChatSessionOutcome o = CallModerationHandler.Apply(m_module.m_nonSpatial, new CallEffects(m_module), call,
                                                               CallModerationCommands.Operator, op, target, on, "console");
            decision = DecisionOf(o.Instrument);
            return o.Status;
        }

        public CallRemovalResult Remove(UUID call, UUID target, string reason)
            => m_module.RemoveFromCall(call, CallModerationCommands.Operator, target, reason);

        public CallRemovalResult Readmit(UUID call, UUID target)
            => m_module.ReadmitToCall(call, CallModerationCommands.Operator, target);

        // The decision word of an instrument line ("... decision=moderation-applied op=..."), with what follows it.
        private static string DecisionOf(string instrument)
        {
            if (string.IsNullOrEmpty(instrument)) return string.Empty;
            int at = instrument.IndexOf("decision=", StringComparison.Ordinal);
            return at < 0 ? instrument : instrument.Substring(at + "decision=".Length);
        }
    }
}
