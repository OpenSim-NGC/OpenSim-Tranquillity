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
        protected ISimulationDataStore m_database;

        public SimulationDataService(ISimulationDataStore simulationDataStore)
        {
            m_database = simulationDataStore;
        }

        public void StoreObject(SceneObjectGroup obj, UUID regionUUID)
        {
            uint flags = obj.RootPart.GetEffectiveObjectFlags();
            if ((flags & (uint)(PrimFlags.Temporary | PrimFlags.TemporaryOnRez)) != 0)
                return;

            m_database.StoreObject(obj, regionUUID);
        }

        public void RemoveObject(UUID uuid, UUID regionUUID)
        {
            m_database.RemoveObject(uuid, regionUUID);
        }

        public void StorePrimInventory(UUID primID, ICollection<TaskInventoryItem> items)
        {
            m_database.StorePrimInventory(primID, items);
        }

        public List<SceneObjectGroup> LoadObjects(UUID regionUUID)
        {
            return m_database.LoadObjects(regionUUID);
        }

        public void StoreTerrain(TerrainData terrain, UUID regionID)
        {
            m_database.StoreTerrain(terrain, regionID);
        }

        public void StoreBakedTerrain(TerrainData terrain, UUID regionID)
        {
            m_database.StoreBakedTerrain(terrain, regionID);
        }

        public void StoreTerrain(double[,] terrain, UUID regionID)
        {
            m_database.StoreTerrain(terrain, regionID);
        }

        public double[,] LoadTerrain(UUID regionID)
        {
            return m_database.LoadTerrain(regionID);
        }

        public TerrainData LoadTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            return m_database.LoadTerrain(regionID, pSizeX, pSizeY, pSizeZ);
        }

        public TerrainData LoadBakedTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            return m_database.LoadBakedTerrain(regionID, pSizeX, pSizeY, pSizeZ);
        }

        public void StoreLandObject(ILandObject Parcel)
        {
            m_database.StoreLandObject(Parcel);
        }

        public void RemoveLandObject(UUID globalID)
        {
            m_database.RemoveLandObject(globalID);
        }

        public List<LandData> LoadLandObjects(UUID regionUUID)
        {
            return m_database.LoadLandObjects(regionUUID);
        }

        public void StoreRegionSettings(RegionSettings rs)
        {
            m_database.StoreRegionSettings(rs);
        }

        public RegionSettings LoadRegionSettings(UUID regionUUID)
        {
            return m_database.LoadRegionSettings(regionUUID);
        }

        public string LoadRegionEnvironmentSettings(UUID regionUUID)
        {
            return m_database.LoadRegionEnvironmentSettings(regionUUID);
        }

        public void StoreRegionEnvironmentSettings(UUID regionUUID, string settings)
        {
            m_database.StoreRegionEnvironmentSettings(regionUUID, settings);
        }

        public void RemoveRegionEnvironmentSettings(UUID regionUUID)
        {
            m_database.RemoveRegionEnvironmentSettings(regionUUID);
        }

        public UUID[] GetObjectIDs(UUID regionID)
        {
            return m_database.GetObjectIDs(regionID);
        }

        public void SaveExtra(UUID regionID, string name, string val)
        {
            m_database.SaveExtra(regionID, name, val);
        }

        public void RemoveExtra(UUID regionID, string name)
        {
            m_database.RemoveExtra(regionID, name);
        }

        public Dictionary<string, string> GetExtra(UUID regionID)
        {
            return m_database.GetExtra(regionID);
        }
    }
}
