/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Console surface for the peer_ctl_batch sink's own counters.
 *
 * WHY THIS EXISTS. JanusPeerCtlBatchSink maintains LastSendFallback* and LastSendStats, but nothing
 * else reads them. A shadow-mode run has to report the sink's fallback counters, and a counter
 * nothing can read cannot be reported, so this is the minimal reader.
 *
 * STRICTLY READ-ONLY. It formats volatile ints already being maintained; it sends nothing, mutates
 * nothing, and cannot change the wire or the audio path. That is deliberate: it ships in the same
 * restart as [WebRtcVoice] VisibilityArmingEnabled=true, and a reader that cannot act is a reader
 * that cannot be a second candidate cause for anything a run observes.
 *
 * Region scoping and the snapshot supplier follow VoiceModerationCommands exactly: a COPY of the
 * per-region map, never the live dictionary, because a console handler writes to a terminal and must
 * not do that while holding the lock the CAP handler and RegionLoaded contend for.
 */

using System;
using System.Collections.Generic;
using System.Reflection;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public sealed class VoiceVisibilityCommands
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string logHeader = "[VOICE VISIBILITY CONSOLE]";

        private readonly Func<List<KeyValuePair<Scene, VoiceVisibilityService>>> m_regions;

        public VoiceVisibilityCommands(Func<List<KeyValuePair<Scene, VoiceVisibilityService>>> regions)
        {
            m_regions = regions;
        }

        public void Register()
        {
            if (MainConsole.Instance is null)
                return;   // unit tests / embedded hosts have no console

            MainConsole.Instance.Commands.AddCommand("Voice", false, "show voice visibility",
                "show voice visibility",
                "Show the peer_ctl_batch sink's counters for the most recent send, per region",
                "Every figure describes the MOST RECENT send only.\n"
                    + "Unplaced listeners/sources > 0 means the sim could not place an agent -\n"
                    + "no room record AND no parcel resolved for it - so it was LEFT OUT of the send rather than\n"
                    + "addressed at the estate room. Persistent non-zero is a bug to chase, not a fallback working.\n"
                    + "Of the inner-reply stats, skipped > 0 is real loss and anomalies > 0 is protocol drift;\n"
                    + "deferred > 0 is normal (the mixer replays those at join).\n"
                    + "Reports the region selected with \"change region\", or every region at the root prompt.",
                HandleShowVoiceVisibility);
        }

        // --- Handlers -------------------------------------------------------------------------

        private void HandleShowVoiceVisibility(string module, string[] args)
        {
            List<KeyValuePair<Scene, VoiceVisibilityService>> regions = SelectedRegions();
            if (regions.Count == 0)
            {
                MainConsole.Instance.Output(
                    "No region here is running the voice visibility feeder, so there is no sink to report (see VisibilityFeederEnabled).");
                return;
            }

            foreach (KeyValuePair<Scene, VoiceVisibilityService> kv in regions)
            {
                string region = kv.Key.RegionInfo.RegionName;
                JanusPeerCtlBatchSink sink = kv.Value.JanusSink;
                if (sink is null)
                {
                    MainConsole.Instance.Output(
                        "Region \"{0}\": matrix-only, no Janus peer_ctl sink (emission off, or [JanusWebRtcVoice] admin config missing).",
                        region);
                    continue;
                }

                foreach (string line in Format(region, sink, kv.Value.Unplaced))
                    MainConsole.Instance.Output(line);
            }
        }

        /// <summary>The report for one region, as lines. Pure and public so the contract is testable: the
        /// report must name the rooms the send RESOLVED, count what it could not place, and
        /// present the estate room as the region's own number rather than as anybody's address.</summary>
        public static IReadOnlyList<string> Format(string region, JanusPeerCtlBatchSink sink, int unplaced)
        {
            PeerCtlSendStats s = sink.LastSendStats;
            IReadOnlyList<int> rooms = sink.LastSendRoomNumbers;
            string roomList = rooms.Count == 0 ? "none" : string.Join(", ", rooms);
            return new List<string>
            {
                string.Format("Region \"{0}\": last send addressed {1} room(s): {2}; {3} agent(s) unplaced and "
                    + "omitted (estate room {4})",
                    region, sink.LastSendRooms, roomList, unplaced, sink.FallbackRoom),
                string.Format(
                    "  unplaced in the send       excl listeners {0}, excl sources {1}, mute listeners {2}, mute sources {3}",
                    sink.LastSendFallbackListeners, sink.LastSendFallbackSources,
                    sink.LastSendMuteFallbackListeners, sink.LastSendMuteFallbackSources),
                string.Format(
                    "  inner replies              parsed {0}, entries {1}, mute entries {2}, deferred {3} (normal), skipped {4} (loss), anomalies {5} (drift)",
                    s.RepliesParsed, s.Entries, s.MuteEntries, s.DeferredListeners, s.Skipped, s.Anomalies),
            };
        }

        // --- Helpers --------------------------------------------------------------------------

        /// The regions this invocation reports on: the one selected with "change region", else all.
        private List<KeyValuePair<Scene, VoiceVisibilityService>> SelectedRegions()
        {
            List<KeyValuePair<Scene, VoiceVisibilityService>> all = m_regions();
            IScene selected = MainConsole.Instance.ConsoleScene;
            if (selected is null)
                return all;
            all.RemoveAll(kv => !ReferenceEquals(kv.Key, selected));
            return all;
        }
    }
}
