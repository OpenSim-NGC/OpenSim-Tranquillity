/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A notecard read whose answer never reaches the script. SL (llGetNotecardLine,
/// https://wiki.secondlife.com/wiki/LlGetNotecardLine): when the notecard is missing or not a notecard, an error goes out on
/// DEBUG_CHANNEL; the dataserver page (https://wiki.secondlife.com/wiki/Dataserver) says any dataserver request can fail, and
/// that a response may never come. What these tests pin is what the region's log holds
/// when the asset service has no notecard to give (a null result, a result of another type, a throw), and what becomes of
/// an answer that is produced just as the region stops: one still in the scheduler's incoming queue is saved with the
/// script and given to it after the restart; a fetch still in flight is not (the request is not saved).
/// The log is captured through LoggerProvider and the asset service is replaced on the scene, both process-wide
/// in effect, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class NotecardFetchLossTests : IDisposable
{
    private const string Phlox = "InWorldz.Phlox";
    private const int RegionStart = 0;
    private const int DebugChannel = 0x7FFFFFFF;

    // The script asks on a touch; the answer is said as "ds [text]".
    private const string Reader = @"
        default {
            state_entry() { llSay(0, ""entry""); }
            touch_start(integer n) { llGetNotecardLine(""card"", 0); llSay(0, ""asked""); }
            dataserver(key q, string d) { llSay(0, ""ds ["" + d + ""]""); }
        }";

    private readonly Capture m_log = new();
    private readonly ILoggerFactory m_previous;
    private readonly List<IDisposable> m_toDispose = new();

    public NotecardFetchLossTests()
    {
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = m_log;
    }

    public void Dispose()
    {
        LoggerProvider.LoggerFactory = m_previous;
        foreach (IDisposable d in m_toDispose) try { d.Dispose(); } catch { }
    }

    private SchedulerHarness NewHarness()
    {
        var h = new SchedulerHarness();
        m_toDispose.Add(h);
        return h;
    }

    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Text)> m_lines = new();
        public (LogLevel Level, string Text)[] Lines { get { lock (m_lines) return m_lines.ToArray(); } }
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (m_c.m_lines) m_c.m_lines.Add((logLevel, formatter(state, exception))); }
        }
    }

    /// <summary>The region's asset service, with one asset's fetch answered as the test says: null, thrown, or held until released.</summary>
    public class FetchOf : DispatchProxy
    {
        public IAssetService Inner;
        public string Id;
        public Func<object> Answer;                // returns what Get gives (null allowed) or throws
        public ManualResetEventSlim Gate;          // when set, Get waits for it first
        public int Gets;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "Get" && args.Length == 1 && args[0] as string == Id)
            {
                Interlocked.Increment(ref Gets);
                Gate?.Wait(TimeSpan.FromSeconds(60));
                if (Answer != null) return Answer();
            }
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
        }

        public static FetchOf Install(Scene scene, UUID asset)
        {
            var proxy = Create<IAssetService, FetchOf>();
            var f = (FetchOf)(object)proxy;
            f.Inner = scene.AssetService;
            f.Id = asset.ToString();
            typeof(Scene).GetField("m_AssetService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(scene, proxy);
            return f;
        }
    }

    private static byte[] NotecardBytes(string text)
        => Encoding.UTF8.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length "
                                  + Encoding.UTF8.GetByteCount(text) + "\n" + text + "}");

    private static void AddCardItem(SchedulerHarness h, UUID asset, UUID itemId)
        => h.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = "card", AssetID = asset, ItemID = itemId, Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard
        }, true);

    private static void StoreCard(SchedulerHarness h, UUID asset, string text)
        => h.Scene.AssetService.Store(AssetHelpers.CreateAsset(asset, AssetType.Notecard, NotecardBytes(text), h.Prim.OwnerID));

    private static LSLSystemAPI ApiOf(SchedulerHarness h, UUID item)
        => ((Dictionary<UUID, LSLSystemAPI>)SavedStateRig.Field(SavedStateRig.Exe(h), "m_Apis"))[item];

    private static int Incoming(SchedulerHarness h)
    {
        object q = SavedStateRig.Field(SavedStateRig.Exe(h), "m_PendingEvents");
        lock (q) return ((ICollection)q).Count;
    }

    private static List<string> DebugChannelErrors(SchedulerHarness h)
        => h.SaidOn.Where(m => m.Channel == DebugChannel).Select(m => m.Message).ToList();

    /// <summary>Touch the script and pump until it has made its read.</summary>
    private static void Ask(SchedulerHarness h, UUID item)
    {
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("asked"), TimeSpan.FromSeconds(30)), SavedStateRig.SaidText(h));
    }

    private (LogLevel Level, string Text)[] LinesAbout(UUID asset)
        => m_log.Lines.Where(l => l.Text.Contains(asset.ToString())).ToArray();

    // ── what the region log holds when the fetch yields nothing ──────────────────

    [Fact]
    public void ANullFromTheAssetServiceForANotecardInThePrimIsLoggedAsOneWarning()
    {
        var h = NewHarness();
        UUID asset = UUID.Random();
        AddCardItem(h, asset, UUID.Random());
        FetchOf fetch = FetchOf.Install(h.Scene, asset);
        fetch.Answer = () => null;
        UUID item = h.RezScript(Reader);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry"), TimeSpan.FromSeconds(30)), SavedStateRig.SaidText(h));

        Ask(h, item);
        Assert.True(h.PumpUntil(() => DebugChannelErrors(h).Count > 0, TimeSpan.FromSeconds(30)), SavedStateRig.SaidText(h));

        // The debug channel has its error, as before; the script gets no answer and its request stays owed.
        Assert.Equal(new[] { "Script error: Notecard 'card' could not be found." }, DebugChannelErrors(h));
        Assert.Equal(1, fetch.Gets);
        Assert.Equal(1, ApiOf(h, item).PendingDataserverCount);

        // And the region log names the notecard, its asset and the script, once, as a warning.
        var lines = LinesAbout(asset);
        Assert.True(lines.Length == 1, "the log lines naming the asset: [" + string.Join(" | ", lines.Select(l => l.Level + " " + l.Text)) + "]");
        Assert.Equal(LogLevel.Warning, lines[0].Level);
        Assert.StartsWith("[PhloxAPI]:", lines[0].Text);
        Assert.Contains("'card'", lines[0].Text);
        Assert.Contains(item.ToString(), lines[0].Text);
        Assert.Contains("script1", lines[0].Text);

        h.PumpFor(TimeSpan.FromMilliseconds(300));   // the answer that did not come is still not here
        Assert.DoesNotContain(h.Said, s => s.StartsWith("ds ["));
    }

    [Fact]
    public void AFetchThatGivesAnAssetOfAnotherTypeIsLoggedAsOneWarning()
    {
        var h = NewHarness();
        UUID asset = UUID.Random();
        AddCardItem(h, asset, UUID.Random());
        h.Scene.AssetService.Store(AssetHelpers.CreateAsset(asset, AssetType.Texture, new byte[] { 1, 2, 3 }, h.Prim.OwnerID));
        UUID item = h.RezScript(Reader);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry"), TimeSpan.FromSeconds(30)), SavedStateRig.SaidText(h));

        Ask(h, item);
        Assert.True(h.PumpUntil(() => DebugChannelErrors(h).Count > 0, TimeSpan.FromSeconds(30)), SavedStateRig.SaidText(h));

        Assert.Equal(new[] { "Script error: Notecard 'card' could not be found." }, DebugChannelErrors(h));
        var warnings = LinesAbout(asset).Where(l => l.Level == LogLevel.Warning).ToArray();
        Assert.True(warnings.Length == 1, "the warnings naming the asset: " + warnings.Length);
        Assert.Contains("'card'", warnings[0].Text);
        Assert.Contains(item.ToString(), warnings[0].Text);
    }

    [Fact]
    public void ATimeoutThrownByTheAssetServiceIsLoggedAsOneWarning()
    {
        var h = NewHarness();
        UUID asset = UUID.Random();
        AddCardItem(h, asset, UUID.Random());
        FetchOf fetch = FetchOf.Install(h.Scene, asset);
        fetch.Answer = () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.", new TimeoutException());
        UUID item = h.RezScript(Reader);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry"), TimeSpan.FromSeconds(30)), SavedStateRig.SaidText(h));

        Ask(h, item);
        Assert.True(h.PumpUntil(() => DebugChannelErrors(h).Count > 0, TimeSpan.FromSeconds(30)), SavedStateRig.SaidText(h));

        Assert.Equal(new[] { "Script error: Notecard 'card' could not be found." }, DebugChannelErrors(h));
        Assert.Equal(1, ApiOf(h, item).PendingDataserverCount);
        var warnings = LinesAbout(asset).Where(l => l.Level == LogLevel.Warning).ToArray();
        Assert.True(warnings.Length == 1, "the warnings naming the asset: " + warnings.Length + " of [" +
                    string.Join(" | ", m_log.Lines.Where(l => l.Text.Contains("llGetNotecardLine") || l.Text.Contains(asset.ToString())).Select(l => l.Level + " " + l.Text)) + "]");
        Assert.Contains("'card'", warnings[0].Text);
        Assert.Contains(item.ToString(), warnings[0].Text);
        h.PumpFor(TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(h.Said, s => s.StartsWith("ds ["));
    }

    // ── an answer as the region stops ────────────────────────────────────────────

    private readonly UUID m_item = UUID.Random(), m_script = UUID.Random(), m_card = UUID.Random(), m_cardItem = UUID.Random();

    private SchedulerHarness Region(out FetchOf fetch)
    {
        var h = NewHarness();
        StoreCard(h, m_card, "hello");
        AddCardItem(h, m_card, m_cardItem);
        fetch = FetchOf.Install(h.Scene, m_card);
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, m_item, m_script, "reader", Reader);
        Assert.Equal(1, h.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h.Prim.ParentGroup.ResumeScripts();
        Assert.True(h.PumpUntil(() => h.InterpreterFor(m_item) != null), "the script did not load");
        return h;
    }

    [Fact]
    public void AnAnswerInTheSchedulersIncomingQueueAtRegionStopIsGivenToTheScriptAfterTheRestart()
    {
        var h1 = Region(out FetchOf fetch);
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
        fetch.Gate = new ManualResetEventSlim(false);
        Ask(h1, m_item);
        h1.PumpUntilIdle(TimeSpan.FromSeconds(2));   // the script is waiting for its answer
        Assert.Equal(0, Incoming(h1));

        // The fetch ends and its answer is posted to the scheduler; the region stops before the scheduler takes it up.
        fetch.Gate.Set();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (Incoming(h1) == 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.Equal(1, Incoming(h1));
        Assert.DoesNotContain(h1.Said, s => s.StartsWith("ds ["));
        h1.StopRegionAsTheSimulatorDoes();

        // The next start: the same script from the saved row, in a new region.
        var h2 = NewHarness();
        TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, m_item, m_script, "reader", Reader);
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(m_item) != null), "the script did not load");
        bool got = h2.PumpUntil(() => h2.Said.Contains("ds [hello]"), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("entry", h2.Said);   // restored, not started fresh
        Assert.True(got, "the answer produced before the stop never reached the script: " + SavedStateRig.SaidText(h2));
    }

    [Fact]
    public void AFetchStillInFlightAtRegionStopIsNotAskedAgainAfterTheRestart()
    {
        // Pins what is so today: the pending request is not saved, so the restored script waits for an answer nobody sends.
        var h1 = Region(out FetchOf fetch);
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
        fetch.Gate = new ManualResetEventSlim(false);
        Ask(h1, m_item);
        h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
        var until = DateTime.UtcNow.AddSeconds(30);
        while (fetch.Gets == 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.Equal(1, fetch.Gets);   // the fetch is in flight, held in the asset service
        h1.StopRegionAsTheSimulatorDoes();

        var h2 = NewHarness();
        FetchOf fetch2 = FetchOf.Install(h2.Scene, m_card);
        StoreCard(h2, m_card, "hello");
        AddCardItem(h2, m_card, m_cardItem);
        TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, m_item, m_script, "reader", Reader);
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(m_item) != null), "the script did not load");
        h2.PumpUntilIdle(TimeSpan.FromSeconds(3));

        // The late end of the first region's fetch finds nothing to post to; the new region never asks again.
        fetch.Gate.Set();
        h2.PumpFor(TimeSpan.FromMilliseconds(500));
        Assert.Equal("Waiting", h2.RunStateOf(m_item));
        Assert.DoesNotContain("entry", h2.Said);
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("ds ["));
        Assert.Equal(0, fetch2.Gets);
        Assert.Equal(0, ApiOf(h2, m_item).PendingDataserverCount);
    }
}
