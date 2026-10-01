using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// In world, at a region start: <c>[PhloxState] Failed to load state ... "database is
/// locked"</c> and <c>Batch flush failed: "database is locked"</c> - three regions restoring in parallel
/// while StateManager.FlushLoop writes every 2.5 s, one SQLite file. A failed load is a script that
/// restarted from state_entry with its globals gone. This is that shape against a real temp DB with
/// the current connection setup: three regions = three PhloxEngines = THREE StateManagers on the one
/// file, each with its own flush loop; three loader threads (one per manager) each restoring 50 rows
/// while a writer per manager keeps its flush loop busy with 50 dirty scripts. One manager alone never
/// fails (0 in 10 rounds, tried first) because its writers already serialise on its own lock; the in-world
/// contention is between engines.
/// </summary>
[Collection("phlox-state")]
public class StateDbContentionTests
{
    private readonly ITestOutputHelper _out;
    public StateDbContentionTests(ITestOutputHelper o) => _out = o;

    private const int Engines = 3, RowsPerLoader = 50, DirtyScripts = 50;

    private static InWorldz.Phlox.VM.Interpreter Script(InWorldz.Phlox.VM.CompiledScript compiled)
    {
        var api = RecordingSystemApi.Create(out _);
        var shim = new InWorldz.Phlox.Glue.SyscallShim(call => call());
        shim.SystemAPI = api;
        var interp = new InWorldz.Phlox.VM.Interpreter(compiled, shim) { ItemId = UUID.Random() };
        shim.Interpreter = interp;
        return interp;
    }

    /// <summary>One round: (load failures, flush failures, null loads, first load error, first flush error).</summary>
    private (int loadFail, int flushFail, int nullLoads, string lastLoad, string lastFlush) Round(string dbFile)
    {
        var compiled = PhloxCompiler.CompileTo("integer g = 7; default { state_entry() { g = 8; } }", out var listener);
        Assert.False(listener.HasErrors(), listener.Report);
        compiled.AssetId = UUID.Random();

        // three engines' worth of state managers on the one file, as a multi-region process has
        var managers = Enumerable.Range(0, Engines).Select(_ => new StateManager(null, dbFile)).ToList();
        // the rows the loaders will restore, written the way shutdown writes them
        var rows = new List<InWorldz.Phlox.VM.Interpreter>();
        for (int i = 0; i < Engines * RowsPerLoader; i++) { var s = Script(compiled); rows.Add(s); managers[i % Engines].ScriptUnloaded(s); }
        // the scripts each flush loop will keep writing
        var dirty = Enumerable.Range(0, Engines).Select(_ => Enumerable.Range(0, DirtyScripts).Select(__ => Script(compiled)).ToList()).ToList();

        managers.ForEach(m => m.Start());
        int nullLoads = 0;
        using var go = new ManualResetEventSlim(false);
        bool stop = false;
        var writers = Enumerable.Range(0, Engines).Select(n => new Thread(() =>
        {
            go.Wait();
            while (!Volatile.Read(ref stop))
                foreach (var d in dirty[n]) managers[n].ScriptChanged(d);   // marks dirty and wakes that flush loop
        }) { IsBackground = true }).ToList();
        var loaders = Enumerable.Range(0, Engines).Select(n => new Thread(() =>
        {
            go.Wait();
            for (int i = 0; i < RowsPerLoader; i++)
            {
                var r = rows[n * RowsPerLoader + i];
                var st = managers[n].LoadState(r.ItemId, compiled.AssetId);
                if (st == null) Interlocked.Increment(ref nullLoads);
            }
        }) { IsBackground = true }).ToList();
        writers.ForEach(t => t.Start()); loaders.ForEach(t => t.Start());
        go.Set();
        loaders.ForEach(t => Assert.True(t.Join(60_000), "a loader did not finish"));
        Volatile.Write(ref stop, true);
        writers.ForEach(t => t.Join(10_000));
        managers.ForEach(m => { m.Stop(); m.Dispose(); });
        return (managers.Sum(m => m.LoadFailures), managers.Sum(m => m.FlushFailures), nullLoads,
                managers.Select(m => m.LastLoadError).FirstOrDefault(e => e != null),
                managers.Select(m => m.LastFlushError).FirstOrDefault(e => e != null));
    }

    [Fact]
    public void ThreeEnginesOnOneStateFileNeverSeeTheDatabaseLocked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "phlox11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        int loadFail = 0, flushFail = 0, nulls = 0;
        string lastLoad = null, lastFlush = null;
        for (int round = 1; round <= 10; round++)
        {
            var r = Round(Path.Combine(dir, $"round{round}.db"));
            _out.WriteLine($"round {round}: load failures {r.loadFail}, flush failures {r.flushFail}, null loads {r.nullLoads}" +
                           (r.lastLoad != null ? $" | load error: {r.lastLoad}" : "") +
                           (r.lastFlush != null ? $" | flush error: {r.lastFlush}" : ""));
            loadFail += r.loadFail; flushFail += r.flushFail; nulls += r.nullLoads;
            lastLoad ??= r.lastLoad; lastFlush ??= r.lastFlush;
        }
        Assert.True(loadFail == 0 && flushFail == 0 && nulls == 0,
            $"across 10 rounds: {loadFail} load failures, {flushFail} flush failures, {nulls} rows came back null. " +
            $"Load error: {lastLoad ?? "(none)"}; flush error: {lastFlush ?? "(none)"}");
    }
}
