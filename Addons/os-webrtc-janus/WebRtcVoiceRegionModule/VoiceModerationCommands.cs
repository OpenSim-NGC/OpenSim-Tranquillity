/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Console surface for the sticky per-parcel voice-moderation state (VoiceModerationStore).
 *
 * WHY THIS EXISTS. A parcel voice mute is enforced by removing the muted avatar from the matrix
 * column of every listener (VisibilityRules rule 2b), and the mixer turns that exclusion into a
 * synthesised LEAVE - the roster row disappears. But the roster row is what an operator
 * right-clicks to issue the unmute. So the mute removes its own undo, and with the store being
 * in-memory and surfaced nowhere else, the only escapes were a region restart or a moderation op
 * aimed at somebody still visible. These commands are that missing escape, plus its mirror:
 *
 *   show voice moderation                                       - what is muted, where, on whose parcel
 *   voice moderation unmute <agent-uuid-or-name>                - clear one avatar's entry, roster or no roster
 *   voice moderation mute <agent-uuid-or-name> [parcel-local-id] - add one avatar's entry on one parcel
 *
 * The unmute deliberately does NOT go through a roster, a presence, or a click target. It reaches
 * the store directly, which is why it works on an avatar nobody can see.
 *
 * The mute writes through the SAME store call the SpatialVoiceModerationRequest CAP's
 * "mute" operand makes (svc.Moderation.MuteAgent(land.GlobalID, target)) - no second code path - and
 * the feeder picks it up on its next tick exactly as it does a viewer's mute, so the console proves
 * the real mechanism. The CAP permission-checks its REQUESTER (VoiceModerationAuth.MayModerate); the
 * console is operator-trusted, so that check is skipped and the write is logged "moderated by console".
 * The moderator EXEMPTION is a property of the TARGET and still applies: muting a parcel owner, estate
 * manager or group moderator records the entry but silences nothing, and the command says so.
 *
 * Registration follows this tree's idiom (WebRtcVoiceServiceModule.RegisterConsoleCommands, which
 * registers "show voice closing"): guard a null MainConsole for unit tests and embedded hosts,
 * register under the "Voice" help category, and report with plain indented MainConsole.Output
 * lines under a counted header.
 *
 * Region scoping follows the core idiom (LandManagementModule.HandleShowCommand): honour
 * MainConsole.Instance.ConsoleScene. A null ConsoleScene (the root prompt) means every region for the
 * listing and the unmute; a mute always needs exactly one region.
 */

using System;
using System.Collections.Generic;
using System.Reflection;

using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace osWebRtcVoice
{
    public sealed class VoiceModerationCommands
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string logHeader = "[VOICE MODERATION CONSOLE]";

        // A snapshot supplier rather than the live dictionary: the module owns that dictionary and
        // its lock, and a console handler must not hold that lock while it does slow work (parcel
        // enumeration, name resolution, console I/O).
        private readonly Func<List<KeyValuePair<Scene, VoiceVisibilityService>>> m_regions;

        public VoiceModerationCommands(Func<List<KeyValuePair<Scene, VoiceVisibilityService>>> regions)
        {
            m_regions = regions;
        }

        public void Register()
        {
            if (MainConsole.Instance is null)
                return;   // unit tests / embedded hosts have no console

            MainConsole.Instance.Commands.AddCommand("Voice", false, "show voice moderation",
                "show voice moderation",
                "Show sticky per-parcel voice moderation state: muted avatars and mute-everyone, per parcel, per region",
                "State is in-memory and non-persistent: a region restart clears every mute.\n"
                    + "Only regions running the visibility feeder (VisibilityFeederEnabled) hold moderation state.\n"
                    + "Reports the region currently selected with \"change region\", or every region at the root prompt.",
                HandleShowVoiceModeration);

            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice moderation unmute",
                "voice moderation unmute <agent-uuid-or-name>",
                "Clear one avatar's parcel voice-moderation mute on every parcel of the selected region",
                "The target may be an agent UUID or an avatar name; a name is matched, case-insensitively,\n"
                    + "against the avatars that are ACTUALLY MUTED (see \"show voice moderation\"), not against\n"
                    + "the region roster - a muted avatar has been removed from the roster, which is the very\n"
                    + "trap this command exists to escape. An ambiguous name is refused with the candidate\n"
                    + "UUIDs listed; re-run with a UUID. This does NOT clear a parcel-wide mute-everyone:\n"
                    + "that one is still reachable from the viewer, because \"unmute everyone\" needs no\n"
                    + "roster row to click.",
                HandleVoiceModerationUnmute);

            MainConsole.Instance.Commands.AddCommand("Voice", false, "voice moderation mute",
                "voice moderation mute <agent-uuid-or-name> [parcel-local-id]",
                "Voice-mute one avatar on one parcel of the selected region (operator-trusted, same store as the viewer's moderation mute)",
                "Needs exactly one region: select it with \"change region <name>\". The target may be an agent\n"
                    + "UUID or the name of a root avatar in that region (case-insensitive; an ambiguous name is refused\n"
                    + "with the candidate UUIDs). The parcel is the given local id, else the parcel the avatar is standing\n"
                    + "on; an avatar not in the region needs the parcel id. A trailing number is read as the parcel id,\n"
                    + "so address an avatar whose last name is a number by UUID. The mute is enforced on the feeder's\n"
                    + "next tick; parcel owners, estate managers and group moderators are exempt from being muted.\n"
                    + "Undo with \"voice moderation unmute\".",
                HandleVoiceModerationMute);
        }

        // --- Handlers -------------------------------------------------------------------------

        private void HandleShowVoiceModeration(string module, string[] args)
        {
            List<KeyValuePair<Scene, VoiceVisibilityService>> regions = SelectedRegions();
            if (regions.Count == 0)
            {
                MainConsole.Instance.Output(
                    "No region here is running the voice visibility feeder, so no moderation state exists (see VisibilityFeederEnabled).");
                return;
            }

            foreach (KeyValuePair<Scene, VoiceVisibilityService> kv in regions)
            {
                Scene scene = kv.Key;
                IReadOnlyList<ParcelModerationView> views = kv.Value.Moderation.Snapshot();
                if (views.Count == 0)
                {
                    MainConsole.Instance.Output("Region \"{0}\": no voice moderation in force.", scene.RegionInfo.RegionName);
                    continue;
                }

                MainConsole.Instance.Output("Region \"{0}\": {1} parcel(s) with voice moderation in force:",
                    scene.RegionInfo.RegionName, views.Count);

                bool anyMuteEveryone = false;
                foreach (ParcelModerationView view in views)
                {
                    anyMuteEveryone |= view.MuteEveryone;

                    MainConsole.Instance.Output("  parcel {0} \"{1}\" mute-everyone: {2}",
                        view.ParcelGlobalId, ParcelNameFor(scene, view.ParcelGlobalId),
                        view.MuteEveryone ? "YES" : "no");

                    if (view.MutedAgents.Count == 0)
                    {
                        MainConsole.Instance.Output("    (no individually muted avatars)");
                        continue;
                    }

                    // Sort by resolved name so two runs read the same way; unresolved entries sort
                    // last under their UUID, which is the only handle they have.
                    List<KeyValuePair<string, UUID>> rows = new List<KeyValuePair<string, UUID>>();
                    foreach (UUID agentId in view.MutedAgents)
                        rows.Add(new KeyValuePair<string, UUID>(NameFor(scene, agentId), agentId));
                    rows.Sort(CompareRows);

                    foreach (KeyValuePair<string, UUID> row in rows)
                        MainConsole.Instance.Output("    muted agent {0} {1} ({2})",
                            row.Value,
                            row.Key ?? "(name unresolved)",
                            scene.GetScenePresence(row.Value) is null ? "not in this region" : "in region");
                }

                // Moderators are exempt source-side (VoiceModerationAuth, folded in by
                // FeederWorldFromScene), so a mute-everyone never silences the parcel owner or an
                // estate manager. Say so: an operator reading "mute-everyone: YES" next to an
                // audible avatar would otherwise reasonably file it as a bug.
                if (anyMuteEveryone)
                    MainConsole.Instance.Output(
                        "  note: parcel owner, estate managers and group moderators are exempt from mute-everyone.");
            }
        }

        private void HandleVoiceModerationUnmute(string module, string[] args)
        {
            // args: voice moderation unmute <token...>. Everything past the verb is rejoined so an
            // unquoted "First Last" works as well as a quoted one.
            if (args.Length < 4)
            {
                MainConsole.Instance.Output("Usage: voice moderation unmute <agent-uuid-or-name>");
                return;
            }
            string token = string.Join(" ", args, 3, args.Length - 3).Trim();

            List<KeyValuePair<Scene, VoiceVisibilityService>> regions = SelectedRegions();
            if (regions.Count == 0)
            {
                MainConsole.Instance.Output(
                    "No region here is running the voice visibility feeder, so no moderation state exists (see VisibilityFeederEnabled).");
                return;
            }

            // Candidates come from the moderation state itself, never from a roster - see
            // VoiceModerationTargets for why that choice is the whole point of this command.
            List<VoiceModerationCandidate> candidates = new List<VoiceModerationCandidate>();
            foreach (KeyValuePair<Scene, VoiceVisibilityService> kv in regions)
                foreach (ParcelModerationView view in kv.Value.Moderation.Snapshot())
                    foreach (UUID agentId in view.MutedAgents)
                        candidates.Add(new VoiceModerationCandidate(agentId, NameFor(kv.Key, agentId)));

            VoiceModerationTargetMatch match = VoiceModerationTargets.Resolve(
                token, candidates, out UUID target, out IReadOnlyList<VoiceModerationCandidate> ambiguous);

            if (match == VoiceModerationTargetMatch.Ambiguous)
            {
                MainConsole.Instance.Output("\"{0}\" matches {1} muted avatars; re-run with one of these UUIDs:",
                    token, ambiguous.Count);
                foreach (VoiceModerationCandidate c in ambiguous)
                    MainConsole.Instance.Output("  {0} {1}", c.AgentId, c.Name ?? "(name unresolved)");
                return;
            }

            if (match == VoiceModerationTargetMatch.NotFound)
            {
                MainConsole.Instance.Output(
                    "No muted avatar here is named \"{0}\", and it is not a UUID. Run \"show voice moderation\" to see what is muted.",
                    token);
                return;
            }

            // Which regions actually hold an entry for this agent? Snapshot once per region and
            // reuse it: the parcels to clear are read from it, and the store call below is the only
            // mutation.
            List<KeyValuePair<KeyValuePair<Scene, VoiceVisibilityService>, List<UUID>>> holders
                = new List<KeyValuePair<KeyValuePair<Scene, VoiceVisibilityService>, List<UUID>>>();
            foreach (KeyValuePair<Scene, VoiceVisibilityService> kv in regions)
            {
                List<UUID> parcels = new List<UUID>();
                foreach (ParcelModerationView view in kv.Value.Moderation.Snapshot())
                {
                    foreach (UUID agentId in view.MutedAgents)
                    {
                        if (agentId == target)
                        {
                            parcels.Add(view.ParcelGlobalId);
                            break;
                        }
                    }
                }
                if (parcels.Count > 0)
                    holders.Add(new KeyValuePair<KeyValuePair<Scene, VoiceVisibilityService>, List<UUID>>(kv, parcels));
            }

            if (holders.Count == 0)
            {
                MainConsole.Instance.Output("{0} is not voice-moderated on any parcel here; nothing to clear.", target);
                return;
            }

            // At the root prompt a bare UUID could name a mute in several regions. Clearing all of
            // them silently would be a wider action than the operator asked for, so refuse and make
            // them pick - the same "change region" discipline every other region-scoped command uses.
            if (holders.Count > 1 && MainConsole.Instance.ConsoleScene is null)
            {
                MainConsole.Instance.Output("{0} is voice-moderated in {1} regions:", target, holders.Count);
                foreach (KeyValuePair<KeyValuePair<Scene, VoiceVisibilityService>, List<UUID>> h in holders)
                    MainConsole.Instance.Output("  \"{0}\" ({1} parcel(s))", h.Key.Key.RegionInfo.RegionName, h.Value.Count);
                MainConsole.Instance.Output("Select one with \"change region <name>\" and re-run.");
                return;
            }

            foreach (KeyValuePair<KeyValuePair<Scene, VoiceVisibilityService>, List<UUID>> h in holders)
            {
                Scene scene = h.Key.Key;
                VoiceVisibilityService svc = h.Key.Value;
                foreach (UUID parcelGlobalId in h.Value)
                {
                    // No feeder wake is issued and none is needed: VoiceStateFeeder.Tick rebuilds
                    // the matrix from the live world unconditionally on every tick (the dirty flag
                    // is an early-wake hint, not a gate), so the next tick - within VisibilityTickMs,
                    // 250ms by default - already sees the cleared entry.
                    bool cleared = svc.Moderation.UnmuteAgent(parcelGlobalId, target);
                    MainConsole.Instance.Output("  {0} on parcel {1} \"{2}\" in \"{3}\"",
                        cleared ? "cleared voice mute" : "no voice mute found",
                        parcelGlobalId, ParcelNameFor(scene, parcelGlobalId), scene.RegionInfo.RegionName);
                    if (cleared)
                        m_log.LogInformation("{LogHeader} console unmute: agent {AgentId} on parcel {ParcelGlobalId} in region \"{RegionName}\"",
                            logHeader, target, parcelGlobalId, scene.RegionInfo.RegionName);
                }
            }

            MainConsole.Instance.Output(
                "The matrix picks this up on its next tick and the mixer restores audio. The roster row returns only if the avatar still holds a mixer session; if it does not, it reappears on their next voice reconnect.");
        }

        private void HandleVoiceModerationMute(string module, string[] args)
        {
            // args: voice moderation mute <token...> [parcel-local-id]
            List<string> words = new List<string>();
            for (int i = 3; i < args.Length; i++)
                words.Add(args[i]);
            if (!VoiceModerationTargets.ParseMuteArguments(words, out string token, out int? givenLocalId))
            {
                MainConsole.Instance.Output("Usage: voice moderation mute <agent-uuid-or-name> [parcel-local-id]");
                return;
            }

            List<KeyValuePair<Scene, VoiceVisibilityService>> regions = SelectedRegions();
            if (regions.Count == 0)
            {
                MainConsole.Instance.Output(
                    "No region here is running the voice visibility feeder, so a mute cannot be enforced (see VisibilityFeederEnabled).");
                return;
            }
            if (regions.Count > 1)
            {
                MainConsole.Instance.Output("A mute applies to one parcel of one region; select the region with \"change region <name>\" and re-run.");
                return;
            }
            Scene scene = regions[0].Key;
            VoiceVisibilityService svc = regions[0].Value;
            string regionName = scene.RegionInfo.RegionName;

            // Unlike the unmute, a mute targets somebody still audible, so a NAME resolves against the region's
            // root avatars. A UUID resolves without being present (then the parcel id is required).
            List<VoiceModerationCandidate> present = new List<VoiceModerationCandidate>();
            scene.ForEachRootScenePresence(sp => present.Add(new VoiceModerationCandidate(sp.UUID, sp.Name)));
            VoiceModerationTargetMatch match = VoiceModerationTargets.Resolve(
                token, present, out UUID target, out IReadOnlyList<VoiceModerationCandidate> ambiguous);

            if (match == VoiceModerationTargetMatch.Ambiguous)
            {
                MainConsole.Instance.Output("\"{0}\" matches {1} avatars in \"{2}\"; re-run with one of these UUIDs:",
                    token, ambiguous.Count, regionName);
                foreach (VoiceModerationCandidate c in ambiguous)
                    MainConsole.Instance.Output("  {0} {1}", c.AgentId, c.Name ?? "(name unresolved)");
                return;
            }
            if (match == VoiceModerationTargetMatch.NotFound)
            {
                MainConsole.Instance.Output("No avatar in \"{0}\" is named \"{1}\", and it is not a UUID.", regionName, token);
                return;
            }

            ILandChannel landChannel = scene.LandChannel;
            if (landChannel is null)
            {
                MainConsole.Instance.Output("Region \"{0}\" has no land data yet; nothing muted.", regionName);
                return;
            }

            ScenePresence targetSp = scene.GetScenePresence(target);
            int? standingLocalId = null;
            if (targetSp is not null && !targetSp.IsChildAgent)
                standingLocalId = landChannel.GetLandObject(targetSp.AbsolutePosition.X, targetSp.AbsolutePosition.Y)?.LandData?.LocalID;

            VoiceModerationParcelChoice choice = VoiceModerationTargets.ChooseMuteParcel(givenLocalId, standingLocalId, out int parcelLocalId);
            if (choice == VoiceModerationParcelChoice.NoParcel)
            {
                MainConsole.Instance.Output("{0} is not standing in \"{1}\"; name the parcel: voice moderation mute {0} <parcel-local-id>",
                    target, regionName);
                return;
            }

            LandData land = landChannel.GetLandObject(parcelLocalId)?.LandData;
            if (land is null)
            {
                MainConsole.Instance.Output("No parcel with local id {0} in \"{1}\"; nothing muted.", parcelLocalId, regionName);
                return;
            }

            // The SAME store write the SpatialVoiceModerationRequest "mute" operand makes. No feeder wake is needed:
            // the feeder rebuilds the matrix from the live world on every tick (see the unmute handler).
            svc.Moderation.MuteAgent(land.GlobalID, target);
            m_log.LogInformation("{LogHeader} console mute: agent {AgentId} on parcel {ParcelGlobalId} (\"{ParcelName}\") in region \"{RegionName}\" - moderated by console",
                logHeader, target, land.GlobalID, land.Name, regionName);

            MainConsole.Instance.Output("muted {0} {1} on parcel {2} \"{3}\" ({4}) in \"{5}\"{6}",
                target, NameFor(scene, target) ?? "(name unresolved)", land.LocalID, land.Name, land.GlobalID, regionName,
                choice == VoiceModerationParcelChoice.TargetPosition ? " - the parcel the avatar is standing on" : string.Empty);

            if (VoiceModerationAuth.MayModerate(scene, land, target))
                MainConsole.Instance.Output(
                    "  note: {0} may moderate voice on this parcel (owner, estate manager or group moderator) and is exempt; the entry is recorded but silences nothing.",
                    target);
        }

        // --- Helpers --------------------------------------------------------------------------

        /// Regions in scope: the one selected with "change region", or all of them at the root
        /// prompt. Mirrors LandManagementModule's ConsoleScene check, adapted to a shared module
        /// that owns several scenes rather than one.
        private List<KeyValuePair<Scene, VoiceVisibilityService>> SelectedRegions()
        {
            List<KeyValuePair<Scene, VoiceVisibilityService>> all = m_regions();
            IScene selected = MainConsole.Instance.ConsoleScene;
            if (selected is null)
                return all;
            all.RemoveAll(kv => !ReferenceEquals(kv.Key, selected));
            return all;
        }

        /// The parcel's name, or a marker. "(parcel no longer exists)" is not decoration: it is the
        /// visible symptom of moderation state orphaned by a parcel join or delete, which
        /// OnLandObjectRemoved is supposed to purge.
        private static string ParcelNameFor(Scene scene, UUID parcelGlobalId)
        {
            ILandChannel land = scene.LandChannel;
            if (land is null)
                return "(no land channel)";
            List<ILandObject> parcels = land.AllParcels();
            if (parcels is null)
                return "(unknown)";
            foreach (ILandObject lo in parcels)
            {
                if (lo?.LandData is not null && lo.LandData.GlobalID == parcelGlobalId)
                    return lo.LandData.Name;
            }
            return "(parcel no longer exists)";
        }

        /// The avatar's name, or NULL when it cannot be resolved. Null rather than a placeholder
        /// string on purpose: VoiceModerationTargets must not offer an unresolved entry as a
        /// name-match candidate, or every unresolved avatar would collide under one pseudo-name.
        private static string NameFor(Scene scene, UUID agentId)
        {
            IUserManagement users = scene.UserManagementModule;
            if (users is not null && users.GetUserName(agentId, out string first, out string last))
            {
                string name = (first + " " + last).Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            ScenePresence sp = scene.GetScenePresence(agentId);
            if (sp is not null && !string.IsNullOrWhiteSpace(sp.Name))
                return sp.Name;
            return null;
        }

        private static int CompareRows(KeyValuePair<string, UUID> a, KeyValuePair<string, UUID> b)
        {
            if (a.Key is null && b.Key is null)
                return string.CompareOrdinal(a.Value.ToString(), b.Value.ToString());
            if (a.Key is null)
                return 1;    // unresolved names sort last
            if (b.Key is null)
                return -1;
            int byName = string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(a.Value.ToString(), b.Value.ToString());
        }
    }
}
