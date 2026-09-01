/*
 * Copyright (c) Contributors, http://opensimulator.org/, http://www.nsl.tuis.ac.jp/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *	 * Redistributions of source code must retain the above copyright
 *	   notice, this list of conditions and the following disclaimer.
 *	 * Redistributions in binary form must reproduce the above copyright
 *	   notice, this list of conditions and the following disclaimer in the
 *	   documentation and/or other materials provided with the distribution.
 *	 * Neither the name of the OpenSim Project nor the
 *	   names of its contributors may be used to endorse or promote products
 *	   derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using Nini.Config;

/// <summary>
/// OpenSim Server MoneyServer
/// </summary>
namespace OpenSim.Server.MoneyServer;

/// <summary>
/// class Money Server Config Source
/// </summary>
public class MoneyServerConfigSource
{
    /// <summary>
    /// Ini Config Source
    /// </summary>
    public IniConfigSource m_config;

    /// <summary>
    /// Money Server Config Source
        /// </summary>
    public MoneyServerConfigSource(string iniMaster = "")
    {
        if (string.IsNullOrEmpty(iniMaster))
        {
            iniMaster = Path.Combine(Directory.GetCurrentDirectory(), "MoneyServer.ini");
        }

        if (File.Exists(iniMaster))
        {
            m_config = new IniConfigSource(iniMaster);
        }
        else
        {
            m_config = new IniConfigSource();
        }
    }

    /// <summary>
    /// Save config
    /// </summary>
    public void Save(string path)
    {
        m_config.Save(path);
    }

}
