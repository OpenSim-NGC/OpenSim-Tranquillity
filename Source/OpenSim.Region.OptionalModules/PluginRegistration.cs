/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
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

using System.Reflection;
using OpenSim.Framework;

namespace OpenSim.Region.OptionalModules;

public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        RegisterByName(registry, "/OpenSim/RegionModules", "IRCStackModule", "OpenSim.Region.OptionalModules.Agent.InternetRelayClientView.IRCStackModule", "IRCStackModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "J2KDecoderCommandModule", "OpenSim.Region.OptionalModules.Agent.TextureSender.J2KDecoderCommandModule", "J2KDecoderCommandModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LindenUDPInfoModule", "OpenSim.Region.OptionalModules.UDP.Linden.LindenUDPInfoModule", "LindenUDPInfoModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "AssetInfoModule", "OpenSim.Region.OptionalModules.Asset.AssetInfoModule", "AssetInfoModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "AnimationsCommandModule", "OpenSim.Region.OptionalModules.Avatar.Animations.AnimationsCommandModule", "AnimationsCommandModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "AppearanceInfoModule", "OpenSim.Region.OptionalModules.Avatar.Appearance.AppearanceInfoModule", "AppearanceInfoModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "IRCBridgeModule", "OpenSim.Region.OptionalModules.Avatar.Chat.IRCBridgeModule", "IRCBridgeModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "ConciergeModule", "OpenSim.Region.OptionalModules.Avatar.Concierge.ConciergeModule", "ConciergeModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "FriendsCommandModule", "OpenSim.Region.OptionalModules.Avatar.Friends.FriendsCommandsModule", "FriendsCommandModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "AnimationsCommandModule", "OpenSim.Region.OptionalModules.Avatar.SitStand.SitStandCommandModule", "AnimationsCommandModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "FreeSwitchVoiceModule", "OpenSim.Region.OptionalModules.Avatar.Voice.FreeSwitchVoice.FreeSwitchVoiceModule", "FreeSwitchVoiceModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "VivoxVoiceModule", "OpenSim.Region.OptionalModules.Avatar.Voice.VivoxVoice.VivoxVoiceModule", "VivoxVoiceModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "GroupsMessagingModule", "OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups.GroupsMessagingModule", "GroupsMessagingModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "GroupsModule", "OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups.GroupsModule", "GroupsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "XmlRpcGroupsServicesConnectorModule", "OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups.XmlRpcGroupsServicesConnectorModule", "XmlRpcGroupsServicesConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "DataSnapshotManager", "OpenSim.Region.DataSnapshot.DataSnapshotManager", "DataSnapshotManager");
        RegisterByName(registry, "/OpenSim/RegionModules", "WebSocketEchoModule", "OpenSim.Region.OptionalModules.WebSocketEchoModule.WebSocketEchoModule", "WebSocketEchoModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "EtcdMonitoringModule", "OpenSim.Region.OptionalModules.Framework.Monitoring.EtcdMonitoringModule", "EtcdMonitoringModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "MaterialsModule", "OpenSim.Region.OptionalModules.Materials.MaterialsModule", "MaterialsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "PhysicsParameters", "OpenSim.Region.OptionalModules.PhysicsParameters.PhysicsParameters", "PhysicsParameters");
        RegisterByName(registry, "/OpenSim/RegionModules", "PrimLimitsModule", "OpenSim.Region.OptionalModules.PrimLimitsModule", "PrimLimitsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "JsonStoreCommandsModule", "OpenSim.Region.OptionalModules.Scripting.JsonStore.JsonStoreCommandsModule", "JsonStoreCommandsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "JsonStoreModule", "OpenSim.Region.OptionalModules.Scripting.JsonStore.JsonStoreModule", "JsonStoreModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "JsonStoreScriptModule", "OpenSim.Region.OptionalModules.Scripting.JsonStore.JsonStoreScriptModule", "JsonStoreScriptModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "RegionReadyModule", "OpenSim.Region.OptionalModules.Scripting.RegionReady.RegionReadyModule", "RegionReadyModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "XmlRpcGridRouter", "OpenSim.Region.OptionalModules.Scripting.XmlRpcGridRouterModule.XmlRpcGridRouter", "XmlRpcGridRouter");
        RegisterByName(registry, "/OpenSim/RegionModules", "XmlRpcRouter", "OpenSim.Region.OptionalModules.Scripting.XmlRpcRouterModule.XmlRpcRouter", "XmlRpcRouter");
        RegisterByName(registry, "/OpenSim/RegionModules", "WebStatsModule", "OpenSim.Region.UserStatistics.WebStatsModule", "WebStatsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "CameraOnlyMode", "OpenSim.Region.OptionalModules.ViewerSupport.CameraOnlyModeModule", "CameraOnlyMode");
        RegisterByName(registry, "/OpenSim/RegionModules", "DynamicFloater", "OpenSim.Region.OptionalModules.ViewerSupport.DynamicFloaterModule", "DynamicFloater");
        RegisterByName(registry, "/OpenSim/RegionModules", "DynamicMenu", "OpenSim.Region.OptionalModules.ViewerSupport.DynamicMenuModule", "DynamicMenu");
        RegisterByName(registry, "/OpenSim/RegionModules", "GodNamesModule", "OpenSim.Region.OptionalModules.ViewerSupport.GodNamesModule", "GodNamesModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "SpecialUI", "OpenSim.Region.OptionalModules.ViewerSupport.SpecialUIModule", "SpecialUI");
        RegisterByName(registry, "/OpenSim/RegionModules", "AutoBackupModule", "OpenSim.Region.OptionalModules.World.AutoBackup.AutoBackupModule", "AutoBackupModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "DTLNSLMoneyModule", "OpenSim.Region.OptionalModules.World.Currency.DTLNSLMoneyModule", "DTLNSLMoneyModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "SampleMoneyModule", "OpenSim.Region.OptionalModules.World.MoneyModule.SampleMoneyModule", "SampleMoneyModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "NPCModule", "OpenSim.Region.OptionalModules.World.NPC.NPCModule", "NPCModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "SceneCommandsModule", "OpenSim.Region.OptionalModules.Avatar.Attachments.SceneCommandsModule", "SceneCommandsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "TreePopulatorModule", "OpenSim.Region.OptionalModules.World.TreePopulator.TreePopulatorModule", "TreePopulatorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "WorldViewModule", "OpenSim.Region.OptionalModules.World.WorldView.WorldViewModule", "WorldViewModule");
    }

    private static void RegisterByName(PluginRegistry registry, string extensionPath, string id, string typeName, string displayName)
    {
        Assembly assembly = typeof(PluginRegistration).Assembly;
        Type type = assembly.GetType(typeName, false);
        if (type == null)
            return;

        registry.Register(
            extensionPath,
            new PluginDescriptor(id, type, displayName, "0.9"));
    }
}
