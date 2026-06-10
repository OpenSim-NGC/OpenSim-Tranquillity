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

// This file previously contained PluginManager, a Mono.Addins-based wrapper for
// remote plugin repository and install/enable/disable operations. It has been
// retired as part of the Mono.Addins -> DotNetCorePlugins migration.

namespace OpenSim.Framework;

/// <summary>
/// Retired stub. Plugin repository management via Mono.Addins is no longer supported.
/// </summary>
public class PluginManager
{
    public PluginManager() { }

    public bool InstallPlugin(int ndx, out Dictionary<string, object> result)
    {
        result = new Dictionary<string, object>();
        return false;
    }

    public void UnInstall(int ndx) { }

    public void ListInstalledAddins(out Dictionary<string, object> result)
    {
        result = new Dictionary<string, object>();
    }

    public void ListAvailable(out Dictionary<string, object> result)
    {
        result = new Dictionary<string, object>();
    }

    public void ListUpdates() { }

    public string Update() => "Plugin repository management is not available.";

    public bool AddRepository(string repo) => false;

    public void GetRepository() { }

    public void RemoveRepository(string[] args) { }

    public void EnableRepository(string[] args) { }

    public void DisableRepository(string[] args) { }

    public void ListRepositories(out Dictionary<string, object> result)
    {
        result = new Dictionary<string, object>();
    }

    public bool AddinInfo(int ndx, out Dictionary<string, object> result)
    {
        result = new Dictionary<string, object>();
        return false;
    }

    public void DisablePlugin(string[] args) { }

    public void EnablePlugin(string[] args) { }
}
