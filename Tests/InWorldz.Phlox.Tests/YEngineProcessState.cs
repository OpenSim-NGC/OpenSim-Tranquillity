/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The process-wide state YEngine's OSSL implementation (OSSL_Api) keeps: the master switch, the owner-error flag, the threat
/// level, the delay and distance factors, the [OSSL] section it read, and the per-function permissions it has worked out. OSSL_Api
/// reads them once, when its first script in the process is made, so without a reset every later scene in the process runs under
/// the first scene's [OSSL] settings. A harness that stands YEngine up takes the gate (one at a time, because the settings are
/// shared) and puts the state back to its starting values, so its own [OSSL] section is the one read.
/// </summary>
internal static class YEngineProcessState
{
    private static readonly SemaphoreSlim s_gate = new SemaphoreSlim(1, 1);

    /// <summary>The longest a harness waits for another one with YEngine to be disposed. Tests finish in well under a minute.</summary>
    private static readonly TimeSpan GateWait = TimeSpan.FromMinutes(2);

    public static void Enter()
    {
        if (!s_gate.Wait(GateWait))
            throw new InvalidOperationException("another SchedulerHarness with YEngine has been up for " + GateWait.TotalSeconds
                + " s; YEngine keeps process-wide state, so only one can be up at a time. Is a harness with YEngine not disposed?");
        try { ResetOsslApi(); }
        catch { s_gate.Release(); throw; }
    }

    public static void Exit() => s_gate.Release();

    private static void ResetOsslApi()
    {
        Type api = typeof(OSSL_Api);
        Set(api, "m_doneSharedInit", false);
        Set(api, "m_OSFunctionsEnabled", false);
        Set(api, "m_PermissionErrortoOwner", false);
        Set(api, "m_MaxThreatLevel", ThreatLevel.VeryLow);
        Set(api, "m_ScriptDelayFactor", 1.0f);
        Set(api, "m_ScriptDistanceFactor", 1.0f);
        Set(api, "m_osslconfig", null);
        ((IDictionary)Field(api, "m_FunctionPerms").GetValue(null)).Clear();
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(type.FullName, name + " (OSSL_Api's process-wide state changed: update YEngineProcessState)");

    private static void Set(Type type, string name, object value) => Field(type, name).SetValue(null, value);
}
