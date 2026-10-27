/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// The listen registry as LSLSystemAPI uses it (llListen, llListenControl, llListenRemove, and the delivery of a
    /// directed message). PhloxListenManager implements it.
    /// </summary>
    public interface IScriptListenService
    {
        int Add(uint localID, UUID itemID, UUID hostID, int channel, string name, UUID key, string msg, int regexBitfield, ActiveListen saved);
        void SetActive(UUID itemID, int handle, bool active);
        void Remove(UUID itemID, int handle);
        void Remove(UUID itemID);
        void DeliverChat(ChatTypeEnum type, int channel, string speakerName, UUID speakerKey, string message, Vector3 speakerPosition, UUID destId);
    }

    /// <summary>The OSSL permission checks as LSLSystemAPI calls them. OsslGate implements it.</summary>
    public interface IScriptOsslChecks
    {
        bool Enabled { get; }
        /// <summary>Throws when OSSL functions are off altogether.</summary>
        void Check();
        /// <summary>Throws, with the reason, when the script may not call the function.</summary>
        void Check(ThreatLevel level, string function, Scene world, SceneObjectPart host, TaskInventoryItem item);
        /// <summary>Empty when the script may call the function, else the reason.</summary>
        string Test(ThreatLevel level, string function, Scene world, SceneObjectPart host, TaskInventoryItem item);
    }

    /// <summary>Told when a script has run, so that its state can be saved. Phlox's StateManager is behind it.</summary>
    public interface IScriptStateTracker
    {
        void ScriptChanged(IScriptRuntimeAccess script);
    }
}
