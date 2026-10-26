/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Glue;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// ILSLSystemAPI is the interface another script engine calls to reach the same ll, os, iw and bot implementations
/// LSLSystemAPI gives Phlox. It only names what the class already has: these tests hold it to the class's public
/// functions, signature for signature and default for default, and check that a call through it reaches the class.
/// </summary>
// Runs in parallel: the reflection tests share no state, the call test has its own harness.
public class LslSystemApiSeamTests
{
    static bool IsFunction(MethodInfo m)
        => !m.IsStatic && !m.IsSpecialName && System.Text.RegularExpressions.Regex.IsMatch(m.Name, "^(ll|os|iw|bot)[A-Z]");

    static string TypeName(Type t) => t.FullName ?? t.Name;

    /// <summary>Name, return type and parameter types, by-reference and params markers included.</summary>
    static string Signature(MethodInfo m)
        => TypeName(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p =>
            (p.IsDefined(typeof(ParamArrayAttribute)) ? "params " : "") + TypeName(p.ParameterType))) + ")";

    static List<MethodInfo> ClassFunctions()
        => typeof(LSLSystemAPI).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(IsFunction).ToList();

    static List<MethodInfo> InterfaceFunctions()
        => typeof(ILSLSystemAPI).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).ToList();

    [Fact]
    public void TheSystemApiClassImplementsTheInterface()
    {
        Assert.True(typeof(ILSLSystemAPI).IsAssignableFrom(typeof(LSLSystemAPI)),
            "LSLSystemAPI does not implement ILSLSystemAPI");
    }

    [Fact]
    public void EveryPublicFunctionOfTheClassIsOnTheInterface()
    {
        var onInterface = new HashSet<string>(InterfaceFunctions().Select(Signature));
        var missing = ClassFunctions().Select(Signature).Where(s => !onInterface.Contains(s)).OrderBy(s => s).ToList();
        Assert.True(ClassFunctions().Count > 900, "found only " + ClassFunctions().Count + " public functions on the class");
        Assert.True(missing.Count == 0, missing.Count + " public functions are not on the interface, first: " + string.Join("; ", missing.Take(5)));
    }

    [Fact]
    public void TheInterfaceDeclaresNothingTheClassDoesNotHave()
    {
        var onClass = new HashSet<string>(ClassFunctions().Select(Signature));
        var extra = InterfaceFunctions().Select(Signature).Where(s => !onClass.Contains(s)).OrderBy(s => s).ToList();
        Assert.True(extra.Count == 0, "the interface declares what the class lacks: " + string.Join("; ", extra.Take(5)));
        Assert.True(InterfaceFunctions().Count > 900, "the interface declares only " + InterfaceFunctions().Count + " functions");
    }

    [Fact]
    public void TheInterfaceKeepsEveryParameterNameAndDefaultTheClassHas()
    {
        var onClass = ClassFunctions().ToDictionary(Signature);
        var wrong = new List<string>();
        int compared = 0;
        foreach (var im in InterfaceFunctions())
        {
            if (!onClass.TryGetValue(Signature(im), out var cm)) continue;
            var ip = im.GetParameters();
            var cp = cm.GetParameters();
            for (int i = 0; i < ip.Length; i++)
            {
                compared++;
                if (ip[i].HasDefaultValue != cp[i].HasDefaultValue
                    || (cp[i].HasDefaultValue && !Equals(ip[i].DefaultValue, cp[i].DefaultValue))
                    || ip[i].Name != cp[i].Name)
                    wrong.Add(im.Name + " parameter " + ip[i].Name);
            }
        }
        Assert.True(compared > 1000, "only " + compared + " parameters were compared");
        Assert.True(wrong.Count == 0, "parameters differ from the class: " + string.Join("; ", wrong.Take(5)));
    }

    [Fact]
    public void AnInterfaceCallReachesTheSameImplementationAsACallOnTheClass()
    {
        using var rig = new ApiCallRig();
        var cls = rig.Api;
        var api = cls as ILSLSystemAPI;
        Assert.NotNull(api);
        Assert.Equal(cls.llSin(1f), api.llSin(1f));
        Assert.Equal(3, api.llStringLength("abc"));
        Assert.Equal(cls.llGetKey(), api.llGetKey());
        Assert.Equal(rig.H.Prim.UUID.ToString(), api.llGetKey());
        Assert.InRange(api.llGetLocalTime(), 0, int.MaxValue);
        object[] args = { 1f };
        Assert.Equal(cls.NeedsService("llSin", args), api.NeedsService("llSin", args));
    }
}
