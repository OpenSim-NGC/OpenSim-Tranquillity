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
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    /**
     * These functions remain purely because it would break things if they were removed. In-world deprecation notices are instead provided. all functionality is removed
     */
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        public void llSoundPreload(string sound)
        {
            Deprecated("llSoundPreload", "Use llPreloadSound instead");
        }

        public void llSound(string sound, double volume, int queue, int loop)
        {
            Deprecated("llSound", "Use llPlaySound instead");
        }

        public void llTakeCamera(string avatar)
        {
            Deprecated("llTakeCamera", "Use llSetCameraParams instead");
        }

        public void llReleaseCamera(string avatar)
        {
            Deprecated("llReleaseCamera", "Use llClearCameraParams instead");
        }


        public void llRemoteLoadScript(string target, string name, int running, int start_param)
        {
            Deprecated("llRemoteLoadScript", "Use llRemoteLoadScriptPin instead");
            ScriptSleep(m_sleepMsOnRemoteLoadScript);
        }


        public void llRemoteDataSetRegion()
        {
            Deprecated("llRemoteDataSetRegion", "Use llOpenRemoteDataChannel instead");
        }

        public void llSetPrimURL(string url)
        {
            Deprecated("llSetPrimURL", "Use llSetPrimMediaParams instead");
            ScriptSleep(m_sleepMsOnSetPrimURL);
        }

        public void llRefreshPrimURL()
        {
            Deprecated("llRefreshPrimURL");
            ScriptSleep(m_sleepMsOnRefreshPrimURL);
        }

        public void llMakeExplosion(int particles, double scale, double vel, double lifetime, double arc,
            string texture, LSL_Vector offset)
        {
            Deprecated("llMakeExplosion", "Use llParticleSystem instead");
            ScriptSleep(m_sleepMsOnMakeExplosion);
        }

        public void llMakeFountain(int particles, double scale, double vel, double lifetime, double arc, int bounce,
            string texture, LSL_Vector offset, double bounce_offset)
        {
            Deprecated("llMakeFountain", "Use llParticleSystem instead");
            ScriptSleep(m_sleepMsOnMakeFountain);
        }

        public void llMakeSmoke(int particles, double scale, double vel, double lifetime, double arc, string texture,
            LSL_Vector offset)
        {
            Deprecated("llMakeSmoke", "Use llParticleSystem instead");
            ScriptSleep(m_sleepMsOnMakeSmoke);
        }

        public void llMakeFire(int particles, double scale, double vel, double lifetime, double arc, string texture,
            LSL_Vector offset)
        {
            Deprecated("llMakeFire", "Use llParticleSystem instead");
            ScriptSleep(m_sleepMsOnMakeFire);
        }
    }
}