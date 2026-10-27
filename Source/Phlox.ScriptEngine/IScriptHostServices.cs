/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using Phlox.ScriptEngine.AsyncCommand;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// Seam A: what LSLSystemAPI needs from the engine that hosts its scripts, so that an engine other than PhloxEngine
    /// can construct and host it. The members are the ones the API used of PhloxEngine; PhloxEngine implements the
    /// interface with the members it already has, so Phlox behaves as before.
    /// </summary>
    public interface IScriptHostServices
    {
        // ---- the region and the engine's settings ----
        string Name { get; }
        Scene World { get; }
        IConfigSource ConfigSource { get; }
        IScriptModule ScriptModule { get; }
        ServiceCallDeferralMode ServiceCallDeferral { get; }
        bool AllowGodFunctions { get; }
        bool AutomaticLinkPermission { get; }
        float MinTimerInterval { get; }

        // Halcyon's anti-abuse slowdowns, each on or off.
        bool ResetThrottle { get; }
        bool ChatThrottle { get; }
        bool BotThrottle { get; }
        bool PhysicsThrottle { get; }
        bool LinkMessageThrottle { get; }
        bool NotecardThrottle { get; }
        bool FormatStringThrottle { get; }
        bool HttpInFlightThrottle { get; }
        bool NotecardHalcyonTiming { get; }
        bool NotecardCacheEnabled { get; }
        PhloxNotecardCache NotecardCache { get; }
        int PhysicsFrameTimeAvg { get; }

        // ---- services the engine hands out; null when it has none ----
        IScriptOsslChecks Ossl { get; }
        IScriptListenService ListenManager { get; }
        IScriptStateTracker StateManager { get; }
        AsyncCommandManager AsyncCommands { get; }

        // ---- calls into the engine ----
        void SysReturn(UUID itemId, object retValue, int delay);
        void SysReturnSequenced(UUID itemId, object retValue, int delay, int seq);
        bool PostScriptEvent(UUID itemID, EventParams parms);
        bool PostObjectEvent(uint localID, EventParams parms);
        bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value);
        int PostDataserverToPrim(SceneObjectPart part, UUID skip, UUID asker, string queryId, string data);
        bool HasScript(UUID itemID, out bool running);
        void SetTimerEvent(uint localID, UUID itemID, float sec);
        void SetMinEventDelay(UUID itemID, float seconds);
        void SetItemRunningFlag(uint localId, UUID itemId, bool running);
        void ResetScript(UUID itemID);
        void ApiResetScript(UUID itemID);
        void RequestParcelCheck(UUID itemId);
        void SendToWorldComm(Action<IWorldComm> send);
        float GetEventQueueFreeSpacePercentage(UUID itemID);
        void ExperienceGrantRestored();
        void ExperienceCannotRun(UUID experience, int code);
    }
}
