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

using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenCaps;

public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("AgentPreferencesModule", typeof(AgentPreferencesModule), "AgentPreferencesModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("AvatarPickerSearchModule", typeof(AvatarPickerSearchModule), "AvatarPickerSearchModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("BunchOfCapsModule", typeof(BunchOfCapsModule), "BunchOfCapsModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("EventQueueGetModule", typeof(EventQueueGetModule), "EventQueueGetModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("ObjectAdd", typeof(ObjectAdd), "ObjectAdd", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("UploadObjectAssetModule", typeof(UploadObjectAssetModule), "UploadObjectAssetModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("DisplayNameModule", typeof(DisplayNameModule), "DisplayNameModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("EstateAccessCapModule", typeof(EstateAccessCapModule), "EstateAccessCapModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("EstateChangeInfoCapModule", typeof(EstateChangeInfoCapModule), "EstateChangeInfoCapModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("ExperienceModule", typeof(ExperienceModule), "ExperienceModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("FetchInventory2Module", typeof(FetchInventory2Module), "FetchInventory2Module", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("FetchLibDescModule", typeof(FetchLibDescModule), "FetchLibDescModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("GetAssetsModule", typeof(GetAssetsModule), "GetAssetsModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("MeshUploadFlagModule", typeof(MeshUploadFlagModule), "MeshUploadFlagModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("RegionConsoleModule", typeof(RegionConsoleModule), "RegionConsoleModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("ServerReleaseNotesModule", typeof(ServerReleaseNotesModule), "ServerReleaseNotesModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("SimulatorFeaturesModule", typeof(SimulatorFeaturesModule), "SimulatorFeaturesModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("UploadBakedTextureModule", typeof(UploadBakedTextureModule), "UploadBakedTextureModule", "0.9"));
        registry.Register("/OpenSim/RegionModules", new PluginDescriptor("WebFetchInvDescModule", typeof(WebFetchInvDescModule), "WebFetchInvDescModule", "0.9"));
    }
}
