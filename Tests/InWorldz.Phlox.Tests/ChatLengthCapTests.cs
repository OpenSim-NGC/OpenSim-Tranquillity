/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Object chat is cut to SL's limits before anyone hears it. llSay (SL wiki): "msg can be a maximum of 1024 bytes"
/// and "If a multibyte character ends up on the 1024 byte boundary, it is discarded and not split into invalid bytes";
/// llShout, llWhisper and llRegionSayTo state the same 1024 bytes. llRegionSay: "If msg is longer than 1024 characters
/// it is truncated to 1024 characters." A run-time error goes llSay's way, so it is cut as llSay.
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class ChatLengthCapTests
{
    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(null, h.Prim, h.Prim.LocalId, UUID.Random());

    /// <summary>A two-byte character in UTF-8 (U+00E9).</summary>
    private static readonly string E = ((char)0xE9).ToString();

    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    private static string LastSaid(SchedulerHarness h)
    {
        var said = h.Said;
        Assert.NotEmpty(said);
        return said[said.Count - 1];
    }

    private static void Speak(LSLSystemAPI api, string fn, string msg)
    {
        switch (fn)
        {
            case "llSay": api.llSay(5, msg); break;
            case "llShout": api.llShout(5, msg); break;
            case "llWhisper": api.llWhisper(5, msg); break;
            case "llRegionSay": api.llRegionSay(5, msg); break;
            default: throw new ArgumentException(fn);
        }
    }

    [Theory]
    [InlineData("llSay")]
    [InlineData("llShout")]
    [InlineData("llWhisper")]
    public void ChatIsCutTo1024Bytes(string fn)
    {
        using var h = new SchedulerHarness();
        Speak(Api(h), fn, new string('a', 2000));
        Assert.Equal(new string('a', 1024), LastSaid(h));
    }

    [Theory]
    [InlineData("llSay")]
    [InlineData("llShout")]
    [InlineData("llWhisper")]
    public void ChatOfExactly1024BytesIsSentWhole(string fn)
    {
        using var h = new SchedulerHarness();
        string msg = string.Concat(Enumerable.Repeat(E, 512));
        Speak(Api(h), fn, msg);
        Assert.Equal(msg, LastSaid(h));
    }

    [Fact]
    public void AMultibyteCharacterOnTheBoundaryIsDroppedWhole()
    {
        // 1023 one-byte characters and one two-byte character: 1025 bytes. Cutting at 1024 would split it.
        using var h = new SchedulerHarness();
        Api(h).llSay(5, new string('a', 1023) + E);
        string got = LastSaid(h);
        Assert.Equal(new string('a', 1023), got);
        Assert.Equal(1023, Bytes(got));
    }

    [Fact]
    public void RegionSayIsCutTo1024Characters()
    {
        using var h = new SchedulerHarness();
        string msg = string.Concat(Enumerable.Repeat(E, 1500));
        Api(h).llRegionSay(5, msg);
        Assert.Equal(string.Concat(Enumerable.Repeat(E, 1024)), LastSaid(h));
    }

    [Fact]
    public void RegionSayDoesNotSplitASurrogatePair()
    {
        // 1023 characters, then U+1F9E1 (two UTF-16 code units): character 1024 would be half of it.
        using var h = new SchedulerHarness();
        Api(h).llRegionSay(5, new string('a', 1023) + char.ConvertFromUtf32(0x1F9E1) + "tail");
        Assert.Equal(new string('a', 1023), LastSaid(h));
    }

    [Fact]
    public void AnErrorIsCutAsLlSayIs()
    {
        using var h = new SchedulerHarness();
        Api(h).ShoutError(new string('e', 3000));
        string got = LastSaid(h);
        Assert.Equal(1024, Bytes(got));
        Assert.StartsWith("Script error: eee", got);
    }

    [Fact]
    public void RegionSayToIsCutTo1024Bytes()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "target", UUID.Random());
        target.AbsolutePosition = new Vector3(100, 100, 30);
        h.RezScriptInto(target.RootPart,
            "default { state_entry() { llListen(5, \"\", NULL_KEY, \"\"); llSay(99, \"ready\"); } " +
            "listen(integer c, string n, key k, string m) { llSay(99, \"len \" + (string)llStringLength(m)); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("ready")), "the listener never started");

        UUID speaker = h.RezScript("default { state_entry() { } }");
        Assert.True(h.PumpUntil(() => h.RunStateOf(speaker) == "Waiting"), "the speaker never loaded");
        LoadedApi(h, speaker).llRegionSayTo(target.RootPart.UUID.ToString(), 5, new string('a', 2000));
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("len "))), "the listener heard nothing");
        Assert.Contains("len 1024", h.Said);
    }

    private static LSLSystemAPI LoadedApi(SchedulerHarness h, UUID item)
    {
        object exe = Field(h.Engine, "m_ExeScheduler");
        return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[item];
    }

    private static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }
}
