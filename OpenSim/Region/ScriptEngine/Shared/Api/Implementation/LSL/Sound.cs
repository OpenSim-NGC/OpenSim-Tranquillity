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

using System;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Scripting;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        // Xantor 20080528 PlaySound updated so it accepts an objectinventory name -or- a key to a sound
        // 20080530 Updated to remove code duplication
        public void llPlaySound(string sound, double volume)
        {
            if (m_SoundModule == null)
                return;

            var soundID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound);
            if (soundID.IsZero())
                return;

            // send the sound, once, to all clients in range
            m_SoundModule.SendSound(m_host.UUID, soundID, volume, false, 0, false, false);
        }

        public void llLoopSound(string sound, double volume)
        {
            if (m_SoundModule == null)
                return;

            var soundID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound);
            if (soundID.IsZero())
                return;

            m_SoundModule.LoopSound(m_host.UUID, soundID, volume, false, false);
        }

        public void llLoopSoundMaster(string sound, double volume)
        {
            if (m_SoundModule == null)
                return;

            var soundID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound);
            if (soundID.IsZero())
                return;

            m_SoundModule.LoopSound(m_host.UUID, soundID, volume, true, false);
        }

        public void llLoopSoundSlave(string sound, double volume)
        {
            if (m_SoundModule == null)
                return;

            var soundID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound);
            if (soundID.IsZero())
                return;

            m_SoundModule.LoopSound(m_host.UUID, soundID, volume, false, true);
        }

        public void llPlaySoundSlave(string sound, double volume)
        {
            if (m_SoundModule == null)
                return;

            var soundID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound);
            if (soundID.IsZero())
                return;

            // send the sound, once, to all clients in range
            m_SoundModule.SendSound(m_host.UUID, soundID, volume, false, 0, true, false);
        }

        public void llTriggerSound(string sound, double volume)
        {
            if (m_SoundModule == null)
                return;

            var soundID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound);
            if (soundID.IsZero())
                return;

            // send the sound, once, to all clients in rangeTrigger or play an attached sound in this part's inventory.
            m_SoundModule.SendSound(m_host.UUID, soundID, volume, true, 0, false, false);
        }

        public void llStopSound()
        {
            if (m_SoundModule != null)
                m_SoundModule.StopSound(m_host.UUID);
        }

        public void llPreloadSound(string sound)
        {
            if (m_SoundModule == null)
                return;

            var soundID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound);
            if (soundID.IsZero())
                return;

            m_SoundModule.PreloadSound(m_host.UUID, soundID);
            ScriptSleep(m_sleepMsOnPreloadSound);
        }


        public void llAdjustSoundVolume(LSL_Float volume)
        {
            m_host.AdjustSoundGain(volume);
            ScriptSleep(m_sleepMsOnAdjustSoundVolume);
        }

        public void llLinkAdjustSoundVolume(LSL_Integer linknumber, LSL_Float volume)
        {
            var parts = GetLinkParts(linknumber);
            foreach (var part in parts) part.AdjustSoundGain(volume);
            ScriptSleep(m_sleepMsOnAdjustSoundVolume);
        }

        public void llSetSoundRadius(double radius)
        {
            m_host.SoundRadius = radius;
        }

        public void llLinkSetSoundRadius(int linknumber, double radius)
        {
            foreach (var sop in GetLinkParts(linknumber))
                sop.SoundRadius = radius;
        }

        public void llSetParcelMusicURL(string url)
        {
            var land = World.LandChannel.GetLandObject(m_host.AbsolutePosition);

            if (land.LandData.OwnerID != m_host.OwnerID)
                return;

            land.SetMusicUrl(url);

            ScriptSleep(m_sleepMsOnSetParcelMusicURL);
        }

        public LSL_Key llGetParcelMusicURL()
        {
            var land = World.LandChannel.GetLandObject(m_host.AbsolutePosition);

            if (land.LandData.OwnerID != m_host.OwnerID)
                return string.Empty;

            return land.GetMusicUrl();
        }

        public void llLinkPlaySound(LSL_Integer linknumber, string sound, double volume)
        {
            if (m_SoundModule == null)
                return;
            if (m_host.ParentGroup == null || m_host.ParentGroup.IsDeleted)
                return;

            SceneObjectPart sop;
            if (linknumber == ScriptBaseClass.LINK_THIS)
                sop = m_host;
            else if (linknumber < 0)
                return;
            else if (linknumber < 2)
                sop = m_host.ParentGroup.RootPart;
            else
                sop = m_host.ParentGroup.GetLinkNumPart(linknumber);

            if (sop == null)
                return;

            var soundID = ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound);
            if (soundID.IsZero())
                return;

            // send the sound, once, to all clients in range
            m_SoundModule.SendSound(sop.UUID, soundID, volume, false, 0, false, false);
        }

        public void llLinkStopSound(LSL_Integer linknumber)
        {
            if (m_SoundModule != null)
                foreach (var sop in GetLinkParts(linknumber))
                    m_SoundModule.StopSound(sop.UUID);
        }

        public void llTriggerSoundLimited(string sound, double volume, LSL_Vector top_north_east,
            LSL_Vector bottom_south_west)
        {
            if (m_SoundModule != null)
                m_SoundModule.TriggerSoundLimited(m_host.UUID,
                    ScriptUtils.GetAssetIdFromKeyOrItemName(m_host, sound, AssetType.Sound), volume,
                    bottom_south_west, top_north_east);
        }


        public void llSetSoundQueueing(int queue)
        {
            if (m_SoundModule != null)
                m_SoundModule.SetSoundQueueing(m_host.UUID, queue == ScriptBaseClass.TRUE.value);
        }

        public void llLinkSetSoundQueueing(int linknumber, int queue)
        {
            if (m_SoundModule != null)
                foreach (var sop in GetLinkParts(linknumber))
                    m_SoundModule.SetSoundQueueing(sop.UUID, queue == ScriptBaseClass.TRUE.value);
        }
    }
}