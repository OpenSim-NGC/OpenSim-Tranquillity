/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Switches that need the slvoice mixer, turned on against Janus's stock AudioBridge plugin.
 *
 * The stock plugin ignores what these switches send it: it does not enforce a join capability, it rejects the
 * per-listener visibility requests, and it has no voice connectors. Left on, the region would carry on as if they
 * worked and say nothing (a join capability is minted and sent that nothing checks), or fail only on the first
 * request. So at start each one that is on logs one warning naming the switch and the plugin, and is switched off in
 * the configuration the modules share. Calling it again, or from another module, is harmless: a switch that has been
 * turned off is not reported twice.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public static class MixerOnlySwitches
    {
        private static readonly ILogger s_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[WEBRTC VOICE]";

        /// <summary>The [WebRtcVoice] switches that only work with the slvoice mixer.</summary>
        public static readonly IReadOnlyList<string> Names = new[]
        {
            "JoinCapabilityEnabled",
            "VisibilityFeederEnabled",
            "VisibilityEmitEnabled",
            "VisibilityArmingEnabled",
            "ConnectorsEnabled",
            "PositionFeedEnabled",
        };

        /// <summary>The plugin the region attaches to: [JanusWebRtcVoice] PluginName, or the stock plugin when unset.</summary>
        public static string PluginNameOf(IConfigSource pConfig)
        {
            IConfig janus = pConfig?.Configs["JanusWebRtcVoice"];
            return janus is null ? WebRtcJanusService.DefaultPluginName : WebRtcJanusService.ReadPluginName(janus);
        }

        /// <summary>True for Janus's stock AudioBridge plugin. Any other plugin name is taken to be a mixer
        /// that implements the slvoice protocol, as before.</summary>
        public static bool IsStockPlugin(string pPluginName)
            => string.Equals(pPluginName, WebRtcJanusService.DefaultPluginName, StringComparison.OrdinalIgnoreCase);

        /// <summary>When the plugin is the stock one, warns about and switches off every mixer-only switch that is on.
        /// Returns the names that were switched off.</summary>
        public static IReadOnlyList<string> DisableOnStockPlugin(IConfigSource pConfig, ILogger pLog = null)
        {
            var disabled = new List<string>();
            IConfig voice = pConfig?.Configs["WebRtcVoice"];
            string plugin = PluginNameOf(pConfig);
            if (voice is null || !IsStockPlugin(plugin))
                return disabled;
            ILogger log = pLog ?? s_log;
            foreach (string name in Names)
            {
                if (!voice.GetBoolean(name, false))
                    continue;
                log.LogWarning("{LogHeader} [WebRtcVoice] {Switch} is true, but [JanusWebRtcVoice] PluginName is {Plugin}, " +
                    "Janus's stock AudioBridge, which cannot do this. The switch stays off. It needs the slvoice mixer " +
                    "(PluginName = janus.plugin.slvoice).", LogHeader, name, plugin);
                voice.Set(name, "false");
                disabled.Add(name);
            }
            return disabled;
        }
    }
}
