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

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Services.SimulationService
{
    public class SimulationDataService : ISimulationDataService
    {
        protected readonly ISimulationDataStore _simulationDataStore;

        public SimulationDataService(ISimulationDataStore simulationDataStore)
        {
            _simulationDataStore = simulationDataStore;
        }

        public void StoreObject(SceneObjectGroup obj, UUID regionUUID)
        {
            uint flags = obj.RootPart.GetEffectiveObjectFlags();
            if ((flags & (uint)(PrimFlags.Temporary | PrimFlags.TemporaryOnRez)) != 0)
                return;

            _simulationDataStore.StoreObject(obj, regionUUID);
        }

        public void RemoveObject(UUID uuid, UUID regionUUID)
        {
            _simulationDataStore.RemoveObject(uuid, regionUUID);
        }

        public void StorePrimInventory(UUID primID, ICollection<TaskInventoryItem> items)
        {
            _simulationDataStore.StorePrimInventory(primID, items);
        }

        public List<SceneObjectGroup> LoadObjects(UUID regionUUID)
        {
            return _simulationDataStore.LoadObjects(regionUUID);
        }

        public void StoreTerrain(TerrainData terrain, UUID regionID)
        {
            _simulationDataStore.StoreTerrain(terrain, regionID);
        }

        public void StoreBakedTerrain(TerrainData terrain, UUID regionID)
        {
            _simulationDataStore.StoreBakedTerrain(terrain, regionID);
        }

        public void StoreTerrain(double[,] terrain, UUID regionID)
        {
            _simulationDataStore.StoreTerrain(terrain, regionID);
        }

        public double[,] LoadTerrain(UUID regionID)
        {
            return _simulationDataStore.LoadTerrain(regionID);
        }

        public TerrainData LoadTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            return _simulationDataStore.LoadTerrain(regionID, pSizeX, pSizeY, pSizeZ);
        }

        public TerrainData LoadBakedTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            return _simulationDataStore.LoadBakedTerrain(regionID, pSizeX, pSizeY, pSizeZ);
        }

        public void StoreLandObject(ILandObject Parcel)
        {
            _simulationDataStore.StoreLandObject(Parcel);
        }

        public void RemoveLandObject(UUID globalID)
        {
            _simulationDataStore.RemoveLandObject(globalID);
        }

        public List<LandData> LoadLandObjects(UUID regionUUID)
        {
            return _simulationDataStore.LoadLandObjects(regionUUID);
        }

        public void StoreRegionSettings(RegionSettings rs)
        {
            _simulationDataStore.StoreRegionSettings(rs);
        }

        public RegionSettings LoadRegionSettings(UUID regionUUID)
        {
            return _simulationDataStore.LoadRegionSettings(regionUUID);
        }

        public string LoadRegionEnvironmentSettings(UUID regionUUID)
        {
            return _simulationDataStore.LoadRegionEnvironmentSettings(regionUUID);
        }

        public void StoreRegionEnvironmentSettings(UUID regionUUID, string settings)
        {
            _simulationDataStore.StoreRegionEnvironmentSettings(regionUUID, settings);
        }

        public void RemoveRegionEnvironmentSettings(UUID regionUUID)
        {
            _simulationDataStore.RemoveRegionEnvironmentSettings(regionUUID);
        }

        public UUID[] GetObjectIDs(UUID regionID)
        {
            return _simulationDataStore.GetObjectIDs(regionID);
        }

        public void SaveExtra(UUID regionID, string name, string val)
        {
            _simulationDataStore.SaveExtra(regionID, name, val);
        }

        public void RemoveExtra(UUID regionID, string name)
        {
            _simulationDataStore.RemoveExtra(regionID, name);
        }

        public Dictionary<string, string> GetExtra(UUID regionID)
        {
            return _simulationDataStore.GetExtra(regionID);
        }
    }
}
