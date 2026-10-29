using System.Collections;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script saved again while its previous load is still pending: the latest save wins. The instance that runs, the
/// bytecode cached for the item's asset and what a region restart runs all come from the latest save's asset, and each
/// save's editor gets that save's own compile errors. A superseded load never installs, and never reports or caches
/// under a newer save.
///
/// <para>Every save goes through the region's own steps (Scene.CapsUpdateTaskInventoryScriptAsset): the new asset stored,
/// RemoveScriptInstance, the item given the new asset, then the rez and the editor's error query on one thread. Nothing
/// waits on a clock to make a load pending: the harness pumps the loader only when the test does, so a load posted and not
/// pumped is queued, and a compile is held by the loader's own per-instance hook. No process-wide state: runs in parallel.</para>
/// </summary>
public class ResaveWhileLoadingTests
{
    private readonly ITestOutputHelper _out;
    public ResaveWhileLoadingTests(ITestOutputHelper o) => _out = o;

    private const string Phlox = "InWorldz.Phlox";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    /// <summary>A good script: says "{m} start" on state_entry and "{m} touched" on a touch.</summary>
    private static string Good(string m)
        => $"default {{ state_entry() {{ llSay(0, \"{m} start\"); }} touch_start(integer t) {{ llSay(0, \"{m} touched\"); }} }}";

    /// <summary>A script with one syntax error (a missing ';') on <paramref name="line"/>, so each save's errors are its own.</summary>
    private static string Broken(string m, int line)
        => new string('\n', line - 1) + $"default {{ state_entry() {{ llSay(0, \"{m} start\") }} }}";

    private static void AssertOwnError(IList errors, int line)
    {
        var e = errors.Cast<string>().ToArray();
        Assert.True(e.Length == 1 && e[0].StartsWith($"({line},") && e[0].Contains("Error"),
            $"expected one error on line {line}, got [{string.Join(" | ", e)}]");
    }

    private static UUID StoreAsset(SchedulerHarness h, UUID owner, string source)
    {
        var text = new OpenMetaverse.Assets.AssetScriptText { Source = source };
        text.Encode();
        var asset = AssetHelpers.CreateAsset(UUID.Random(), AssetType.LSLText, text.AssetData, owner);
        h.Scene.AssetService.Store(asset);
        return asset.FullID;
    }

    /// <summary>The first half of a Save, on the test thread: the new asset stored, the old instance removed, the item given the new asset.</summary>
    private static UUID Resave(SchedulerHarness h, UUID itemId, string source)
    {
        var item = h.Prim.Inventory.GetInventoryItem(itemId);
        UUID asset = StoreAsset(h, item.OwnerID, source);
        h.Prim.Inventory.RemoveScriptInstance(itemId, false);
        item.AssetID = asset;
        h.Prim.Inventory.UpdateInventoryItem(item);
        return asset;
    }

    /// <summary>The whole Save as the region does it, CreateScriptInstanceEr included, pumped until the editor has its answer.</summary>
    private static (UUID Asset, ArrayList Errors) Save(SchedulerHarness h, UUID itemId, string source)
    {
        UUID asset = Resave(h, itemId, source);
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(itemId, 0, false, Phlox, 1));
        Assert.True(h.PumpUntil(() => save.IsCompleted, Cap), "the Save did not return");
        h.Prim.ParentGroup.ResumeScripts();
        return (asset, save.Result);
    }

    /// <summary>
    /// A Save whose rez is posted and whose editor is waiting, not yet answered: its own caps thread rezzes (the load
    /// is posted before this returns) and then asks for the errors, as CreateScriptInstanceEr does. Several of these on one
    /// item can be outstanding at once; CreateScriptInstanceEr itself keeps one error slot per item
    /// (SceneObjectPartInventory.m_scriptErrors), so concurrent saves of one item ask GetScriptErrors directly.
    /// </summary>
    private sealed class PendingSave
    {
        public UUID Asset;
        public readonly ManualResetEventSlim Posted = new();
        public ArrayList Errors;
        public readonly ManualResetEventSlim Answered = new();
    }

    private static PendingSave BeginSave(SchedulerHarness h, UUID itemId, string source)
    {
        var s = new PendingSave { Asset = Resave(h, itemId, source) };
        var t = new Thread(() =>
        {
            h.Prim.Inventory.CreateScriptInstance(itemId, 0, false, Phlox, 1);
            s.Posted.Set();
            s.Errors = h.Prim.Inventory.GetScriptErrors(itemId);
            s.Answered.Set();
        }) { IsBackground = true, Name = "test caps thread" };
        t.Start();
        Assert.True(s.Posted.Wait(Cap), "the save's rez was not posted");
        return s;
    }

    private static void AwaitAnswers(SchedulerHarness h, params PendingSave[] saves)
    {
        Assert.True(h.PumpUntil(() => saves.All(s => s.Answered.IsSet), Cap), "an editor was not answered");
        h.Prim.ParentGroup.ResumeScripts();
    }

    private static PhloxScriptLoader Loader(SchedulerHarness h) => (PhloxScriptLoader)h.Loader;

    private static int Count(SchedulerHarness h, string line) => h.Said.Count(s => s == line);

    private static UUID RunningAsset(SchedulerHarness h, UUID itemId)
    {
        var interp = h.InterpreterFor(itemId);
        Assert.NotNull(interp);
        var compiled = (global::InWorldz.Phlox.VM.CompiledScript)interp.GetType().GetProperty("Script")!.GetValue(interp)!;
        return compiled.AssetId;
    }

    private static string CachePath(SchedulerHarness h, UUID asset)
        => Path.Combine(h.BytecodeDir, asset.ToString()[..2], asset + ".plx");

    /// <summary>The running script answers a touch with its own marker: which code is running, whatever state it holds.</summary>
    private static void AssertRuns(SchedulerHarness h, UUID itemId, string marker)
    {
        h.PostTouch(itemId);
        Assert.True(h.PumpUntil(() => Count(h, marker + " touched") >= 1, Cap),
            $"'{marker}' did not answer the touch: [{string.Join(" | ", h.Said)}]");
    }

    /// <summary>
    /// A region restart: a new engine on the same bytecode folder, the item at the asset of its latest save, rezzed by
    /// the region-start path; the bytecode cache is in place, so the cached bytecode for that asset is what runs.
    /// </summary>
    private void AfterRestart(string bytecodeDir, UUID itemId, UUID asset, string source, Action<SchedulerHarness> check)
    {
        using var h2 = new SchedulerHarness(bytecodeDir: bytecodeDir);
        TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, itemId, asset, "resaved", source);
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, 0));
        h2.Prim.ParentGroup.ResumeScripts();
        check(h2);
    }

    /// <summary>The item rezzed with <paramref name="source"/>, its load posted and NOT pumped: still queued.</summary>
    private static UUID RezQueued(SchedulerHarness h, string source)
    {
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(),
            "resave" + Guid.NewGuid().ToString("N")[..6], source);
        Assert.True(h.Prim.Inventory.CreateScriptInstance(item.ItemID, 0, false, Phlox, 0));
        return item.ItemID;
    }

    private static UUID RezRunning(SchedulerHarness h, string marker)
    {
        UUID item = RezQueued(h, Good(marker));
        h.Prim.ParentGroup.ResumeScripts();
        Assert.True(h.PumpUntil(() => Count(h, marker + " start") >= 1, Cap), $"'{marker}' did not start");
        return item;
    }

    // ── a save while the previous load is still queued ──

    [Fact]
    public void ASaveWhileTheFirstLoadIsQueuedShowsTheSavesOwnErrorsAndRunsNothing()
    {
        using var h = new SchedulerHarness();
        UUID item = RezQueued(h, Good("q0"));
        var (asset, errors) = Save(h, item, Broken("q1", 3));
        _out.WriteLine($"editor: [{string.Join(" | ", errors.Cast<object>())}]");
        AssertOwnError(errors, 3);

        h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which the old text would have started
        Assert.Equal(0, Count(h, "q0 start"));
        Assert.Null(h.InterpreterFor(item));
        Assert.False(File.Exists(CachePath(h, asset)), "the broken save's asset has bytecode cached");
    }

    [Fact]
    public void ASaveWhileTheFirstLoadIsQueuedRunsAndCachesTheSaveAndARestartRunsIt()
    {
        string dir;
        UUID item, asset;
        using (var h = new SchedulerHarness())
        {
            dir = h.BytecodeDir;
            item = RezQueued(h, Good("r0"));
            ArrayList errors;
            (asset, errors) = Save(h, item, Good("r1"));
            Assert.Empty(errors);
            Assert.True(h.PumpUntil(() => Count(h, "r1 start") >= 1, Cap), $"the save did not start: [{string.Join(" | ", h.Said)}]");
            AssertRuns(h, item, "r1");
            Assert.Equal(0, Count(h, "r0 start"));
            Assert.Equal(0, Count(h, "r0 touched"));
            Assert.Equal(asset, RunningAsset(h, item));
            Assert.True(File.Exists(CachePath(h, asset)), "no bytecode cached for the save's asset");
        }
        AfterRestart(dir, item, asset, Good("r1"), h2 =>
        {
            AssertRuns(h2, item, "r1");
            Assert.Equal(0, Count(h2, "r0 touched"));
        });
    }

    /// <summary>
    /// The save's unload and its new asset are in, its rez not yet posted (the caps thread is still on its way), and the
    /// loader runs the old queued load in between: that load is superseded already and never starts.
    /// </summary>
    [Fact]
    public async Task AnOldLoadRunBeforeTheSavesRezIsPostedDoesNotStart()
    {
        using var h = new SchedulerHarness();
        UUID item = RezQueued(h, Good("m0"));
        UUID asset = Resave(h, item, Good("m1"));
        Assert.True(h.PumpUntil(() => !Loader(h).IsLoading(item), Cap), "the old load did not finish");
        Assert.Null(h.InterpreterFor(item));

        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(item, 0, false, Phlox, 1));
        Assert.True(h.PumpUntil(() => save.IsCompleted, Cap), "the Save did not return");
        h.Prim.ParentGroup.ResumeScripts();
        Assert.Empty(await save);
        Assert.True(h.PumpUntil(() => Count(h, "m1 start") >= 1, Cap), $"the save did not start: [{string.Join(" | ", h.Said)}]");
        AssertRuns(h, item, "m1");
        Assert.Equal(0, Count(h, "m0 start"));
        Assert.Equal(asset, RunningAsset(h, item));
    }

    /// <summary>
    /// The save's unload is posted while the old load still waits (the region removes the instance, then gives the item
    /// the new asset): loads and unloads are taken in the order they were posted, so the unload cancels the old load,
    /// which never starts, and the save's load runs the new asset.
    /// </summary>
    [Fact]
    public async Task AnOldLoadPostedBeforeTheSavesUnloadIsCancelledByIt()
    {
        using var h = new SchedulerHarness();
        UUID item = RezQueued(h, Good("k0"));
        h.Prim.Inventory.RemoveScriptInstance(item, false);   // the save's unload, posted after the old load
        h.Prim.ParentGroup.ResumeScripts();
        Assert.True(h.PumpUntil(() => !Loader(h).IsLoading(item), Cap), "the old load did not finish");
        Assert.Equal(0, Count(h, "k0 start"));

        var it = h.Prim.Inventory.GetInventoryItem(item);
        UUID asset = StoreAsset(h, it.OwnerID, Good("k1"));
        it.AssetID = asset;
        h.Prim.Inventory.UpdateInventoryItem(it);
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(item, 0, false, Phlox, 1));
        Assert.True(h.PumpUntil(() => save.IsCompleted, Cap), "the Save did not return");
        h.Prim.ParentGroup.ResumeScripts();
        Assert.Empty(await save);
        Assert.True(h.PumpUntil(() => Count(h, "k1 start") >= 1, Cap), $"the save did not start: [{string.Join(" | ", h.Said)}]");
        AssertRuns(h, item, "k1");
        Assert.Equal(0, Count(h, "k0 touched"));
        Assert.Equal(asset, RunningAsset(h, item));
    }

    // ── a save while the previous compile is running ──

    [Theory]
    [InlineData(false)]   // the held save compiles; the next save is broken
    [InlineData(true)]    // the held save is broken; the next save compiles
    public void ASaveWhileThePreviousCompileIsRunningRunsTheLatestAndAnswersEachEditor(bool heldIsBroken)
    {
        string dir;
        UUID item;
        PendingSave first, second;
        string firstSrc = heldIsBroken ? Broken("c1", 2) : Good("c1");
        string secondSrc = heldIsBroken ? Good("c2") : Broken("c2", 4);
        using (var h = new SchedulerHarness())
        {
            dir = h.BytecodeDir;
            item = RezRunning(h, "c0");

            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Loader(h).BeforeCompileForTest = text =>
            {
                if (!text.Contains("\"c1 start\"")) return;
                entered.Set();
                release.Wait(Cap);
            };
            try
            {
                first = BeginSave(h, item, firstSrc);
                Assert.True(h.PumpUntil(() => entered.IsSet, Cap), "the first save's compile did not start");
                second = BeginSave(h, item, secondSrc);
                for (int i = 0; i < 4; i++) h.PumpOnce();   // its unload and load are processed while the first compile is held
                Assert.False(first.Answered.IsSet);
            }
            finally { release.Set(); }
            AwaitAnswers(h, first, second);

            _out.WriteLine($"editor 1: [{string.Join(" | ", first.Errors.Cast<object>())}]  editor 2: [{string.Join(" | ", second.Errors.Cast<object>())}]");
            if (heldIsBroken) { AssertOwnError(first.Errors, 2); Assert.Empty(second.Errors); }
            else { Assert.Empty(first.Errors); AssertOwnError(second.Errors, 4); }

            if (heldIsBroken)
            {
                Assert.True(h.PumpUntil(() => Count(h, "c2 start") >= 1, Cap), $"the latest save did not start: [{string.Join(" | ", h.Said)}]");
                AssertRuns(h, item, "c2");
                Assert.Equal(second.Asset, RunningAsset(h, item));
                Assert.True(File.Exists(CachePath(h, second.Asset)));
            }
            else
            {
                h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which the superseded save would have started
                Assert.Null(h.InterpreterFor(item));
                Assert.False(File.Exists(CachePath(h, second.Asset)));
                Assert.True(File.Exists(CachePath(h, first.Asset)), "the held save's bytecode is cached under its own asset");
            }
            Assert.Equal(0, Count(h, "c1 start"));
        }
        AfterRestart(dir, item, second.Asset, secondSrc, h2 =>
        {
            if (heldIsBroken) AssertRuns(h2, item, "c2");
            else
            {
                h2.PumpFor(TimeSpan.FromSeconds(1));
                Assert.Null(h2.InterpreterFor(item));
            }
            Assert.Equal(0, Count(h2, "c1 touched"));
            Assert.Equal(0, Count(h2, "c1 start"));
        });
    }

    // ── three saves in quick succession, every load still queued ──

    [Theory]
    [InlineData(false)]   // good, broken, good: the last one runs
    [InlineData(true)]    // broken, good, broken: nothing runs
    public void ThreeQuickSavesRunTheLastAndAnswerEachEditorWithItsOwnErrors(bool middleGood)
    {
        string dir;
        UUID item;
        string[] src = middleGood
            ? new[] { Broken("t1", 5), Good("t2"), Broken("t3", 7) }
            : new[] { Good("t1"), Broken("t2", 5), Good("t3") };
        PendingSave[] saves;
        using (var h = new SchedulerHarness())
        {
            dir = h.BytecodeDir;
            item = RezRunning(h, "t0");
            saves = src.Select(s => BeginSave(h, item, s)).ToArray();   // no pump between them: every load queued
            AwaitAnswers(h, saves);
            for (int i = 0; i < 3; i++)
                _out.WriteLine($"editor {i + 1}: [{string.Join(" | ", saves[i].Errors.Cast<object>())}]");

            if (middleGood)
            {
                AssertOwnError(saves[0].Errors, 5);
                Assert.Empty(saves[1].Errors);
                AssertOwnError(saves[2].Errors, 7);
                h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which a superseded save would have started
                Assert.Null(h.InterpreterFor(item));
                Assert.Equal(0, Count(h, "t2 start"));
                Assert.False(File.Exists(CachePath(h, saves[2].Asset)));
            }
            else
            {
                Assert.Empty(saves[0].Errors);
                AssertOwnError(saves[1].Errors, 5);
                Assert.Empty(saves[2].Errors);
                Assert.True(h.PumpUntil(() => Count(h, "t3 start") >= 1, Cap), $"the last save did not start: [{string.Join(" | ", h.Said)}]");
                AssertRuns(h, item, "t3");
                Assert.Equal(saves[2].Asset, RunningAsset(h, item));
                Assert.True(File.Exists(CachePath(h, saves[2].Asset)));
                h.PumpFor(TimeSpan.FromSeconds(1));
                Assert.Equal(1, Count(h, "t3 start"));   // one instance, started once
                Assert.Equal(0, Count(h, "t1 start"));
            }
            Assert.Equal(1, Count(h, "t0 start"));   // the original ran once, before the saves
        }
        AfterRestart(dir, item, saves[2].Asset, src[2], h2 =>
        {
            if (middleGood)
            {
                h2.PumpFor(TimeSpan.FromSeconds(1));
                Assert.Null(h2.InterpreterFor(item));
            }
            else AssertRuns(h2, item, "t3");
            Assert.Equal(0, Count(h2, "t1 touched"));
            Assert.Equal(0, Count(h2, "t2 touched"));
        });
    }
}
