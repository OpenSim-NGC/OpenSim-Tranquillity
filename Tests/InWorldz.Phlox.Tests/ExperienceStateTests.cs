/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An Experience its owner has disabled, or one that has been suspended, as the script sees it. SL wiki
/// llGetExperienceErrorMessage: XP_ERROR_EXPERIENCE_DISABLED (8) "The experience owner has temporarily disabled the
/// experience."; XP_ERROR_EXPERIENCE_SUSPENDED (9) "The experience has been suspended by Linden Lab customer support."
/// llRequestExperiencePermissions is refused with that code, and llGetExperienceDetails reports it as the state, as
/// YEngine does (LSL_Api.llRequestExperiencePermissions and llGetExperienceDetails: disabled first, then suspended).
/// The state is the Experience service's at the call.
/// </summary>
// Runs in parallel: each test has its own harness, avatar, Experience service stand-in and Experience module on its
// own scene; nothing process-wide is changed.
public class ExperienceStateTests
{
    private const int RegionStart = 0;
    private const string Phlox = "InWorldz.Phlox";
    private const int Disabled = (int)ExperienceFlags.Disabled;
    private const int Suspended = (int)ExperienceFlags.Suspended;

    /// <summary>Driven over channel 7 as "xp KEY"; a touch reports llGetExperienceDetails(NULL_KEY).</summary>
    private const string Script = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""details="" + llList2CSV(llGetExperienceDetails(NULL_KEY))); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                if (llList2String(w, 0) == ""xp"") llRequestExperiencePermissions(llList2Key(w, 1), """");
            }
            experience_permissions(key a) { llSay(0, ""xp="" + (string)a); }
            experience_permissions_denied(key a, integer r) { llSay(0, ""xpdenied="" + (string)a + "" "" + (string)r); }
        }";

    /// <summary>
    /// IExperienceService stand-in: one Experience whose properties a test changes, every permission change stored, and
    /// a switch that makes the Experience lookup fail as a service that cannot be reached does.
    /// </summary>
    public class StateService : DispatchProxy
    {
        public ExperienceInfo Info;
        public volatile bool LookupFails;
        public readonly ConcurrentDictionary<(UUID, UUID), bool> Permissions = new();

        public static StateService Create(ExperienceInfo info)
        {
            var proxy = DispatchProxy.Create<IExperienceService, StateService>();
            var me = (StateService)(object)proxy;
            me.Info = info;
            return me;
        }

        protected override object Invoke(MethodInfo m, object[] a)
        {
            switch (m.Name)
            {
                case nameof(IExperienceService.GetExperienceInfos):
                {
                    if (LookupFails) throw new InvalidOperationException("Experience service unreachable");
                    var ids = (UUID[])a[0];
                    // A copy, as the service hands out: a later change to the stored Experience is not seen through it.
                    return ids.Contains(Info.public_id)
                        ? new[] { new ExperienceInfo { public_id = Info.public_id, owner_id = Info.owner_id, group_id = Info.group_id,
                                                       name = Info.name, properties = Info.properties } }
                        : Array.Empty<ExperienceInfo>();
                }
                case nameof(IExperienceService.FetchExperiencePermissions):
                {
                    var agent = (UUID)a[0];
                    return Permissions.Where(p => p.Key.Item1 == agent).ToDictionary(p => p.Key.Item2, p => p.Value);
                }
                case nameof(IExperienceService.UpdateExperiencePermissions):
                {
                    var perm = (ExperiencePermission)a[2];
                    if (perm == ExperiencePermission.None) Permissions.TryRemove(((UUID)a[0], (UUID)a[1]), out _);
                    else Permissions[((UUID)a[0], (UUID)a[1])] = perm == ExperiencePermission.Allowed;
                    return true;
                }
            }
            Type rt = m.ReturnType;
            if (rt.IsArray) return Array.CreateInstance(rt.GetElementType()!, 0);
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    /// <summary>
    /// A region with the core's ExperienceModule and Experience X, which the estate allows; avatar A is here and has
    /// already granted X, so an enabled X is granted with no dialog. The script is in X.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H = new();
        public readonly ExperienceModule Module = new();
        public readonly StateService Service;
        public readonly UUID X = UUID.Random(), A = UUID.Random(), Item = UUID.Random();

        public Rig(int properties)
        {
            Service = StateService.Create(new ExperienceInfo
            {
                public_id = X, owner_id = UUID.Random(), group_id = UUID.Random(), name = "Example Experience", properties = properties
            });
            H.Scene.RegisterModuleInterface<IExperienceService>((IExperienceService)(object)Service);
            H.Scene.RegionInfo.EstateSettings.AllowedExperiences = new[] { X };
            var config = new IniConfigSource();
            config.AddConfig("Experience").Set("Enabled", "true");
            SceneHelpers.SetupSceneModules(H.Scene, config, Module);
            SceneHelpers.AddScenePresence(H.Scene, A);
            Assert.True(Module.SetExperiencePermissions(A, X, true));

            var inv = TaskInventoryHelpers.AddScript(H.Scene.AssetService, H.Prim, Item, UUID.Random(), "xp", Script);
            inv.ExperienceID = X;
            Assert.True(H.Prim.Inventory.CreateScriptInstance(Item, 0, false, Phlox, RegionStart));
            H.Prim.ParentGroup.ResumeScripts();
            Assert.True(H.PumpUntil(() => H.Said.Contains("entry")), SavedStateRig.SaidText(H));
        }

        public int Properties { set => Service.Info.properties = value; }

        /// <summary>The script's answer to llRequestExperiencePermissions(A): "xp=A" or "xpdenied=A code".</summary>
        public string Request()
        {
            static bool Answer(string s) => s.StartsWith("xp=", StringComparison.Ordinal) || s.StartsWith("xpdenied=", StringComparison.Ordinal);
            int before = H.Said.Count(Answer);
            H.Scene.SimChat("xp " + A, ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
            Assert.True(H.PumpUntil(() => H.Said.Count(Answer) > before), "no answer: " + SavedStateRig.SaidText(H));
            return H.Said.Last(Answer);
        }

        /// <summary>llGetExperienceDetails(NULL_KEY) as its six CSV fields.</summary>
        public string[] Details()
        {
            static bool Line(string s) => s.StartsWith("details=", StringComparison.Ordinal);
            int before = H.Said.Count(Line);
            H.PostTouch(Item);
            Assert.True(H.PumpUntil(() => H.Said.Count(Line) > before), SavedStateRig.SaidText(H));
            return H.Said.Last(Line).Substring("details=".Length).Split(", ");
        }

        public string Granted => "xp=" + A;
        public string Denied(int code) => "xpdenied=" + A + " " + code;

        public void Dispose() => H.Dispose();
    }

    // ── llRequestExperiencePermissions ──

    [Fact]
    public void AnEnabledExperienceIsGranted()
    {
        using var r = new Rig(0);
        Assert.Equal(r.Granted, r.Request());
    }

    [Fact]
    public void ADisabledExperienceIsRefusedWithExperienceDisabled()
    {
        using var r = new Rig(Disabled);
        Assert.Equal(r.Denied(8), r.Request());
    }

    [Fact]
    public void ASuspendedExperienceIsRefusedWithExperienceSuspended()
    {
        using var r = new Rig(Suspended);
        Assert.Equal(r.Denied(9), r.Request());
    }

    [Fact]
    public void AnExperienceBothDisabledAndSuspendedIsRefusedAsDisabled()
    {
        using var r = new Rig(Disabled | Suspended);
        Assert.Equal(r.Denied(8), r.Request());
    }

    [Fact]
    public void EachRequestIsJudgedByTheExperiencesStateAtThatRequest()
    {
        using var r = new Rig(Disabled);
        Assert.Equal(r.Denied(8), r.Request());
        r.Properties = 0;
        Assert.Equal(r.Granted, r.Request());
        r.Properties = Suspended;
        Assert.Equal(r.Denied(9), r.Request());
        r.Properties = 0;
        Assert.Equal(r.Granted, r.Request());
    }

    [Fact]
    public void AnExperienceTheServiceCannotLookUpIsRefusedWithNotFound()
    {
        // SL wiki llGetExperienceErrorMessage, XP_ERROR_NOT_FOUND (6): "The sim was unable to verify the validity of the
        // experience. Retrying after a short wait is advised."
        using var r = new Rig(0);
        r.Service.LookupFails = true;
        Assert.Equal(r.Denied(6), r.Request());
        r.Service.LookupFails = false;
        Assert.Equal(r.Granted, r.Request());
    }
}
