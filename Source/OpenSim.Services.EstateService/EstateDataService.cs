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
using OpenSim.Services.Interfaces;
using OpenSim.Data;
using OpenSim.Framework;

namespace OpenSim.Services.EstateService
{
    public class EstateDataService : IEstateDataService
    {
        protected IEstateDataStore m_database;

        public EstateDataService(IEstateDataStore estateDataStore)
        {
            m_database = estateDataStore;
        }

        public EstateSettings LoadEstateSettings(UUID regionID, bool create)
        {
            return m_database.LoadEstateSettings(regionID, create);
        }

        public EstateSettings LoadEstateSettings(int estateID)
        {
            return m_database.LoadEstateSettings(estateID);
        }

        public EstateSettings CreateNewEstate(int estateID = 0)
        {
            return m_database.CreateNewEstate(estateID);
        }

        public List<EstateSettings> LoadEstateSettingsAll()
        {
            return m_database.LoadEstateSettingsAll();
        }

        public void StoreEstateSettings(EstateSettings es)
        {
            m_database.StoreEstateSettings(es);
        }

        public List<int> GetEstates(string search)
        {
            return m_database.GetEstates(search);
        }

        public List<int> GetEstatesAll()
        {
            return m_database.GetEstatesAll();
        }

        public List<int> GetEstatesByOwner(UUID ownerID)
        {
            return m_database.GetEstatesByOwner(ownerID);
        }

        public bool LinkRegion(UUID regionID, int estateID)
        {
            return m_database.LinkRegion(regionID, estateID);
        }

        public List<UUID> GetRegions(int estateID)
        {
            return m_database.GetRegions(estateID);
        }

        public bool DeleteEstate(int estateID)
        {
            return m_database.DeleteEstate(estateID);
        }
    }
}
