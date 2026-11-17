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
        /// <summary>The script is being unloaded: its listens go, and the record of them the script keeps stays.</summary>
        void Forget(UUID itemID);
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

    /// <summary>
    /// What the listen manager needs of the script engine that owns it: where a heard message goes, the engine's settings,
    /// and the three calls it makes about a script (mark it for saving, tell its owner of an error, wake the engine).
    /// PhloxExecutionScheduler implements it for Phlox.
    /// </summary>
    public interface IListenEventSink
    {
        /// <summary>The engine's own settings section; may be null.</summary>
        Nini.Config.IConfig EngineConfig { get; }
        /// <summary>Queues a heard message (a listen event) for the script.</summary>
        void PostEvent(UUID itemId, InWorldz.Phlox.VM.PostedEvent evt);
        /// <summary>The script changed outside any event of its own (a listen switched off), so it is marked for saving.</summary>
        void ScriptChangedOutsideARun(UUID itemId);
        /// <summary>Tells the script's owner of an error, on the debug channel.</summary>
        void ShoutError(UUID itemId, string message);
        /// <summary>Asks the engine to run its next pass.</summary>
        void Wake();
    }
}
