/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetExperienceDetails(NULL_KEY) answers for the script's own Experience. SL wiki llGetExperienceDetails: "If
/// experience_id is NULL_KEY, then information about the script's experience is returned. In this situation, if the
/// script isn't associated with an experience, an empty list is returned."
/// </summary>
// Runs in parallel: each test has its own harness and its own Experience module registered on its own scene.
public class ExperienceDetailsOfTheScriptTests
{
    private const string Phlox = "InWorldz.Phlox";
    private const int RegionStart = 0;

    private const string Asker = @"
        default {
            state_entry() { llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""details="" + llList2CSV(llGetExperienceDetails(NULL_KEY))); }
        }";

    /// <summary>The region knows one Experience and its details.</summary>
    internal class KnownExperience : DispatchProxy
    {
        private ExperienceInfo m_info;

        public static IExperienceModule Create(ExperienceInfo info)
        {
            var proxy = Create<IExperienceModule, KnownExperience>();
            ((KnownExperience)(object)proxy).m_info = info;
            return proxy;
        }

        protected override object Invoke(MethodInfo m, object[] a)
        {
            if (m.Name == nameof(IExperienceModule.GetExperienceInfo))
                return (UUID)a[0] == m_info.public_id ? m_info : null;
            var rt = m.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private static string Ask(UUID itemExperience, ExperienceInfo known)
    {
        using var h = new SchedulerHarness();
        h.Scene.RegisterModuleInterface<IExperienceModule>(KnownExperience.Create(known));
        UUID item = UUID.Random();
        var inv = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, item, UUID.Random(), "asker", Asker);
        inv.ExperienceID = itemExperience;
        Assert.True(h.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
        h.Prim.ParentGroup.ResumeScripts();
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("details=", StringComparison.Ordinal))), SavedStateRig.SaidText(h));
        return h.Said.First(s => s.StartsWith("details=", StringComparison.Ordinal));
    }

    private static ExperienceInfo Example() => new ExperienceInfo
    {
        public_id = UUID.Random(),
        owner_id = UUID.Random(),
        group_id = UUID.Random(),
        name = "Example Experience"
    };

    [Fact]
    public void AScriptInAnExperienceGetsItsOwnExperiencesDetails()
    {
        ExperienceInfo xp = Example();
        string said = Ask(xp.public_id, xp);
        string[] parts = said.Substring("details=".Length).Split(", ");
        Assert.Equal(6, parts.Length);
        Assert.Equal("Example Experience", parts[0]);
        Assert.Equal(xp.owner_id.ToString(), parts[1]);
        Assert.Equal(xp.public_id.ToString(), parts[2]);
        Assert.Equal("0", parts[3]);   // XP_ERROR_NONE: an enabled Experience
        Assert.Equal(xp.group_id.ToString(), parts[5]);
    }

    [Fact]
    public void AScriptInNoExperienceGetsAnEmptyList()
    {
        Assert.Equal("details=", Ask(UUID.Zero, Example()));
    }
}
