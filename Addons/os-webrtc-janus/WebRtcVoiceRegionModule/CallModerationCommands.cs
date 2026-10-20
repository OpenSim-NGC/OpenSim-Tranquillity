/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Region console commands for moderating a group call or a conference. Conferences have no moderator menu in the stock
 * viewers, and a group call's removal has none either, so the operator's console is how they are moderated:
 *
 *   show voice calls                                   - the calls this region can see
 *   show voice call <call-id>                          - one call's members with their mute and removal state
 *   voice call mute <call-id> <agent-uuid> voice|text  - mute one member's voice or text
 *   voice call unmute <call-id> <agent-uuid> voice|text
 *   voice call mute-all <call-id>                      - mute everyone's voice, and whoever joins later
 *   voice call unmute-all <call-id>
 *   voice call remove <call-id> <agent-uuid> [reason]  - remove a member and keep them out
 *   voice call readmit <call-id> <agent-uuid>          - let a removed member back
 *
 * A call is named by its id: a group call by the group's id, a conference by the id "show voice calls" prints. Every
 * change goes through the same code the viewer's requests do, so a console mute is recorded, heard and shown in the
 * members' panels exactly as a moderator's is. The console is the operator's, so no group power is asked for.
 *
 * Nothing printed here is a credential: a call's token and a conference's text key never leave the session record.
 * The commands exist only when [WebRtcVoice] CallModerationEnabled is true.
 */

using System;
using System.Collections.Generic;
using System.Text;

using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice
{
    /// <summary>What the console commands need from the region module.</summary>
    public interface ICallConsoleHost
    {
        IReadOnlyList<NonSpatialVoiceSession> Calls();
        NonSpatialVoiceSession Call(UUID id);
        string NameOf(UUID agent);
        bool IsModerator(NonSpatialVoiceSession call, UUID agent);
        System.Net.HttpStatusCode Moderate(NonSpatialVoiceSession call, ModerationOp op, UUID target, bool on, out string decision);
        WebRtcVoiceRegionModule.CallRemovalResult Remove(UUID call, UUID target, string reason);
        WebRtcVoiceRegionModule.CallRemovalResult Readmit(UUID call, UUID target);
    }

    public sealed class CallModerationCommands
    {
        /// <summary>The id a console change is recorded under ("by"): not an agent, so it is never a call member.</summary>
        public static readonly UUID Operator = new UUID("0c0de000-0000-4000-8000-0000000000c0");

        private readonly ICallConsoleHost m_host;

        /// <summary>Where lines go. The region console by default; a test sets it.</summary>
        public Action<string> Output { get; set; }

        public CallModerationCommands(ICallConsoleHost host, Action<string> output = null)
        {
            m_host = host ?? throw new ArgumentNullException(nameof(host));
            Output = output ?? (line => MainConsole.Instance?.Output(line));
        }

        public void Register()
        {
            if (MainConsole.Instance is null)
                return;   // unit tests / embedded hosts have no console

            MainConsole.Instance.Commands.AddCommand("Voice", false, "show voice calls",
                "show voice calls",
                "List the group calls and conferences this region can see",
                "Lists each call with its id, kind, seats, and how many of its members are muted or removed.\n"
                    + "With the grid session store a region lists the calls it holds a seat in; in a region server that keeps\n"
                    + "its own records it lists every call there. Use the id with \"show voice call\" and the commands below.",
                (module, args) => Execute(args));

            MainConsole.Instance.Commands.AddCommand("Voice", false, "show voice call",
                "show voice call <call-id>",
                "Show one call's members with their voice mute, text mute and removal",
                "The call id is a group's id (a group call) or the id \"show voice calls\" printed (a conference).\n"
                    + "Members are shown by agent id, which is what the other commands take.",
                (module, args) => Execute(args));

            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice call mute",
                "voice call mute <call-id> <agent-uuid> voice|text",
                "Mute one member's voice or text in a call",
                "A voice mute silences the member for everyone in the call; a text mute drops their lines in the call's\n"
                    + "text chat. A voice mute needs a voice server that can carry it out and a member who is in the call's\n"
                    + "room, or it is refused and nothing is recorded. A group moderator is never silenced.\n"
                    + "Undo with \"voice call unmute\".",
                (module, args) => Execute(args));

            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice call unmute",
                "voice call unmute <call-id> <agent-uuid> voice|text",
                "Lift a member's voice or text mute in a call",
                "While a call is muted for everyone, unmuting a member lets that member speak.",
                (module, args) => Execute(args));

            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice call mute-all",
                "voice call mute-all <call-id>",
                "Mute everyone's voice in a call, and whoever joins it later",
                "Group moderators are never silenced. The mute stays until \"voice call unmute-all\", or until the call's\n"
                    + "record is idle for eight hours.",
                (module, args) => Execute(args));

            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice call unmute-all",
                "voice call unmute-all <call-id>",
                "Lift the mute on everyone in a call",
                "Also clears the voice mutes of individual members.",
                (module, args) => Execute(args));

            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice call remove",
                "voice call remove <call-id> <agent-uuid> [reason]",
                "Remove a member from a call and keep them out of it",
                "The member's voice is dropped, their viewer is told to close the call, and they are refused every way back:\n"
                    + "a new call, accepting an invitation, being invited again and a voice provision. The removal lasts until\n"
                    + "the call ends, or for [WebRtcVoice] CallRemovalMinutes when that is set. A group member keeps the group's\n"
                    + "text; a conference member leaves the conference's text. The reason, if given, is recorded with the removal.",
                (module, args) => Execute(args));

            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice call readmit",
                "voice call readmit <call-id> <agent-uuid>",
                "Let a removed member back into a call",
                "The member is not seated again: they join as anyone does, by calling or accepting an invitation.",
                (module, args) => Execute(args));
        }

        // --- dispatch --------------------------------------------------------------------------

        /// <summary>Runs one command; <paramref name="words"/> is the whole command line split on spaces.</summary>
        public void Execute(string[] words)
        {
            if (words is null || words.Length < 3)
            {
                Usage();
                return;
            }
            string first = words[0].ToLowerInvariant(), second = words[1].ToLowerInvariant();
            if (first == "show" && second == "voice" && words[2].ToLowerInvariant() == "calls" && words.Length == 3)
                ShowCalls();
            else if (first == "show" && second == "voice" && words[2].ToLowerInvariant() == "call")
                ShowCall(words);
            else if (first == "voice" && second == "call" && words.Length >= 4)
                Change(words);
            else
                Usage();
        }

        private void Usage()
            => Output("Usage: show voice calls | show voice call <call-id> | voice call mute|unmute <call-id> <agent-uuid> voice|text "
                      + "| voice call mute-all|unmute-all <call-id> | voice call remove <call-id> <agent-uuid> [reason] "
                      + "| voice call readmit <call-id> <agent-uuid>");

        // --- listing ---------------------------------------------------------------------------

        private void ShowCalls()
        {
            IReadOnlyList<NonSpatialVoiceSession> calls = m_host.Calls();
            if (calls.Count == 0)
            {
                Output("No group call or conference to show.");
                return;
            }
            Output($"{calls.Count} call(s):");
            foreach (NonSpatialVoiceSession s in calls)
            {
                if (s.Type == NonSpatialSessionType.P2P) continue;
                string kind = s.Type == NonSpatialSessionType.Group ? $"group call (group {s.Owner})" : "conference";
                Output($"  {s.SessionId}  {kind}  seats {s.SeatsHeld}/{s.Cap}  chat {s.ChatMembers().Count}"
                       + $"  voice-moderated {(s.VoiceModerated ? "YES" : "no")}  voice-muted {s.VoiceMuted.Count}"
                       + $"  text-muted {s.TextMuted.Count}  removed {LiveRemovals(s)}");
            }
        }

        private static int LiveRemovals(NonSpatialVoiceSession s)
        {
            int n = 0;
            foreach (UUID a in s.Removed.Keys)
                if (s.IsRemoved(a, DateTime.UtcNow)) n++;
            return n;
        }

        private void ShowCall(string[] words)
        {
            if (words.Length != 4 || !UUID.TryParse(words[3], out UUID id))
            {
                Output("Usage: show voice call <call-id>");
                return;
            }
            NonSpatialVoiceSession s = m_host.Call(id);
            if (s is null)
            {
                Output($"No such call: {id}");
                return;
            }
            string kind = s.Type == NonSpatialSessionType.Group ? $"group call, group {s.Owner}" : "conference";
            Output($"Call {s.SessionId} ({kind}): seats {s.SeatsHeld}/{s.Cap}, voice-moderated {(s.VoiceModerated ? "YES" : "no")}, "
                   + $"moderation generation {s.ModerationGeneration}");

            HashSet<UUID> shown = new HashSet<UUID>();
            List<UUID> order = new List<UUID>();
            foreach (NonSpatialMember m in s.Members) if (shown.Add(m.AgentId)) order.Add(m.AgentId);
            foreach (UUID a in s.Removed.Keys) if (shown.Add(a)) order.Add(a);
            if (order.Count == 0)
            {
                Output("  (no members)");
                return;
            }
            DateTime now = DateTime.UtcNow;
            foreach (UUID a in order)
            {
                NonSpatialMember m = s.Find(a);
                string state = m is null ? "not in call"
                             : m.HoldsSeat ? (string.IsNullOrEmpty(m.ViewerSession) ? "seated" : "in voice")
                             : m.State == MemberState.Invited ? "invited"
                             : "left";
                string voice = s.IsVoiceMuted(a) ? (m_host.IsModerator(s, a) ? "muted (moderator, not silenced)" : "MUTED")
                             : s.VoiceAllowed.Contains(a) ? "allowed" : "-";
                string text = s.IsTextMuted(a) ? "MUTED" : "-";
                string removal = "-";
                CallRemoval r = s.RemovalOf(a);
                if (r is not null && r.IsLive(now))
                    removal = $"REMOVED by {(r.By == Operator ? "console" : r.By.ToString())}"
                              + (r.ExpiresUtc is null ? " until the call ends" : $" until {r.ExpiresUtc:u}")
                              + (r.Reason.Length > 0 ? $" ({r.Reason})" : string.Empty);
                string name = m_host.NameOf(a);
                Output($"  {a}  {(name is null ? "(not in this region)" : name)}  {state}  voice {voice}  text {text}  removal {removal}");
            }
        }

        // --- changes ---------------------------------------------------------------------------

        private void Change(string[] w)
        {
            string verb = w[2].ToLowerInvariant();
            bool all = verb == "mute-all" || verb == "unmute-all";
            if (!(verb is "mute" or "unmute" or "mute-all" or "unmute-all" or "remove" or "readmit"))
            {
                Usage();
                return;
            }
            if (!UUID.TryParse(w[3], out UUID callId))
            {
                Output("The call id must be a UUID (see \"show voice calls\").");
                return;
            }
            NonSpatialVoiceSession call = m_host.Call(callId);
            if (call is null)
            {
                Output($"No such call: {callId}");
                return;
            }
            if (call.Type == NonSpatialSessionType.P2P)
            {
                Output("A call between two people is not moderated.");
                return;
            }

            if (all)
            {
                if (w.Length != 4) { Usage(); return; }
                Report(m_host.Moderate(call, ModerationOp.VoiceModerated, UUID.Zero, verb == "mute-all", out string d), d,
                       verb == "mute-all" ? "voice muted for everyone" : "voice unmuted for everyone");
                return;
            }

            if (w.Length < 5 || !UUID.TryParse(w[4], out UUID agent) || agent == UUID.Zero)
            {
                Output("The agent must be a UUID (see \"show voice call " + callId + "\").");
                return;
            }

            if (verb == "remove")
            {
                string reason = w.Length > 5 ? string.Join(" ", w, 5, w.Length - 5) : "removed from the console";
                WebRtcVoiceRegionModule.CallRemovalResult r = m_host.Remove(callId, agent, reason);
                if (!r.Ok) Output($"Not removed: {r.Decision}");
                else if (!r.Changed) Output($"{agent} was already removed from call {callId}.");
                else Output($"{agent} removed from call {callId}; {r.Dropped} participant(s) dropped from the voice room."
                            + (r.Note is null ? string.Empty : " " + r.Note));
                return;
            }
            if (verb == "readmit")
            {
                if (w.Length != 5) { Usage(); return; }
                WebRtcVoiceRegionModule.CallRemovalResult r = m_host.Readmit(callId, agent);
                if (!r.Ok) Output($"Not readmitted: {r.Decision}");
                else if (!r.Changed) Output($"{agent} was not removed from call {callId}.");
                else Output($"{agent} may join call {callId} again. They are not seated: they join by calling or accepting an invitation.");
                return;
            }

            // mute / unmute <call> <agent> voice|text
            if (w.Length != 6 || !(w[5].ToLowerInvariant() is "voice" or "text"))
            {
                Output($"Usage: voice call {verb} <call-id> <agent-uuid> voice|text");
                return;
            }
            bool voiceKind = w[5].ToLowerInvariant() == "voice";
            bool on = verb == "mute";
            System.Net.HttpStatusCode status = m_host.Moderate(call, voiceKind ? ModerationOp.VoiceMute : ModerationOp.TextMute, agent, on, out string decision);
            string what = $"{agent}: {(voiceKind ? "voice" : "text")} {(on ? "muted" : "unmuted")}";
            if (status == System.Net.HttpStatusCode.OK && voiceKind && on && m_host.IsModerator(call, agent))
                what += " (a group moderator is recorded as muted but is never silenced)";
            Report(status, decision, what);
        }

        private void Report(System.Net.HttpStatusCode status, string decision, string done)
        {
            if (status == System.Net.HttpStatusCode.OK)
                Output(decision != null && decision.Contains("unchanged") ? "No change: " + done + " already." : "Done: " + done + ".");
            else
                Output($"Not done ({(int)status}): {decision}");
        }
    }
}
