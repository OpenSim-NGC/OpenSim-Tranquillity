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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.Framework
{
    public interface IOpenSimBase
    {
        IRegistryCore ApplicationRegistry { get; }

        ConfigSettings ConfigurationSettings { get; set; }
        bool EnableInitialPluginLoad { get; set; }
        uint HttpServerPort { get; }
        bool LoadEstateDataService { get; set; }

        void CloseRegion(Scene scene);
        void CloseRegion(string name);
        bool CreateEstate(RegionInfo regInfo, Dictionary<string, EstateSettings> estatesByName, string estateName);
        void CreateRegion(RegionInfo regionInfo, bool portadd_flag, bool do_post_init, out IScene mscene);
        void CreateRegion(RegionInfo regionInfo, bool portadd_flag, out IScene scene);
        void CreateRegion(RegionInfo regionInfo, out IScene scene);
        void GetAvatarNumber(out int usernum);
        void GetRegionNumber(out int regionnum);
        void GetRunTime(out string starttime, out string uptime);
        bool PopulateRegionEstateInfo(RegionInfo regInfo);
        void RemoveRegion(Scene scene, bool cleanup);
        void RemoveRegion(string name, bool cleanUp);

        // From RegionApplicationBase
        IEstateDataService EstateDataService { get; }
        NetworkServersInfo NetServersInfo { get; }
        SceneManager SceneManager { get; }
        ISimulationDataService SimulationDataService { get; }

        //From ServerBase
        public string GetVersionText();
        public void Shutdown();
    }
}