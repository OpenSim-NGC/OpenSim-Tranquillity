using System.Reflection;
using InWorldz.Phlox.Serialization;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The bytecode cache schema was bumped because 17 IW_POWER_* constants changed value and cached bytecode
/// still carries the old -1. A bump purges the cached .plx files (PhloxScriptLoader.EnsureCacheSchemaVersion); each
/// script is then compiled again from its source and its saved state is restored onto the new bytecode by
/// SerializedRuntimeState.ToRuntimeStateFor: globals, LSL state, queue, timers and listens are kept; an in-progress
/// event survives when the bytecode is unchanged and is dropped (resumed idle) when it changed.
/// These are round trips through two engines, like RestoredScriptResumeTests, with the real on-disk cache in between.
/// </summary>
[Collection("phlox-state")]
public class CacheSchemaBumpTests
{
    // The loader's relative paths (PhloxScriptLoader.CACHE_DIR and its stamp). Harnesses get a folder per test
    // class; this class deliberately keeps the production folder (its engines are built with it), which is why it stays
    // in "phlox-state" and runs alone.
    private const string CacheDir = SchedulerHarness.ProductionBytecodeDir;
    private static readonly string VersionFile = Path.Combine(CacheDir, ".schema_version");

    private readonly ITestOutputHelper _out;
    public CacheSchemaBumpTests(ITestOutputHelper o) => _out = o;

    private static int CurrentSchema => (int)typeof(global::Phlox.ScriptEngine.PhloxScriptLoader)
        .GetField("CACHE_SCHEMA_VERSION", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private static string CachePath(UUID assetId)
        => Path.Combine(CacheDir, assetId.ToString().Substring(0, 2), assetId + ".plx");

    /// <summary>Put bytecode in the disk cache for <paramref name="assetId"/> as the loader writes it.</summary>
    private static void WriteCache(UUID assetId, string source)
    {
        var compiled = ExprRunner.CompileLsl(source);
        compiled.AssetId = assetId;
        string path = CachePath(assetId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var f = File.Open(path, FileMode.Create);
        ProtoBuf.Serializer.Serialize(f, SerializedScript.FromCompiledScript(compiled));
    }

    /// <summary>The cache as the schema before this one left it on disk.</summary>
    private static void StampPreviousSchema() => File.WriteAllText(VersionFile, (CurrentSchema - 1).ToString());

    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            if (!h.PumpOnceBusy()) Thread.Sleep(1);
        }
        return true;
    }

    private const string PowerScript =
        "integer power;\n" +
        "integer count;\n" +
        "default {\n" +
        "    state_entry() { power = IW_POWER_FREEZE_EJECT; count = 7; llSay(0, \"entry \" + (string)power); }\n" +
        "    touch_start(integer n) { count = count + 1;\n" +
        "        llSay(0, \"touch \" + (string)count + \" saved \" + (string)power + \" now \" + (string)IW_POWER_FREEZE_EJECT); }\n" +
        "}\n";

    [Fact]
    public void AScriptCompiledUnderTheOldSchemaIsRecompiledAndKeepsItsGlobals()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        using (var h1 = new SchedulerHarness(bytecodeDir: CacheDir))
        {
            // The old table wrote IW_POWER_FREEZE_EJECT as 4294967296, which the assembler loads as -1.
            WriteCache(assetId, PowerScript.Replace("IW_POWER_FREEZE_EJECT", "4294967296"));
            h1.RezScript(PowerScript, assetId, itemId);
            Assert.True(PumpUntil(h1, () => h1.Said.Contains("entry -1"), TimeSpan.FromSeconds(20)),
                "the old bytecode did not run from the cache: " + h1.Diagnose(itemId));
            h1.PostTouch(itemId);
            Assert.True(PumpUntil(h1, () => h1.Said.Contains("touch 8 saved -1 now -1"), TimeSpan.FromSeconds(20)),
                "old bytecode: " + string.Join(" | ", h1.Said));
            h1.SaveState(itemId);
        }

        StampPreviousSchema();
        using var h2 = new SchedulerHarness(bytecodeDir: CacheDir);
        Assert.False(File.Exists(CachePath(assetId)), "the bump did not purge the old bytecode");
        Assert.Equal(CurrentSchema.ToString(), File.ReadAllText(VersionFile).Trim());

        h2.RezScript(PowerScript, assetId, itemId);
        Assert.True(PumpUntil(h2, () => h2.InterpreterFor(itemId) != null, TimeSpan.FromSeconds(20)),
            "not recompiled: " + h2.Diagnose(itemId));
        h2.Pump(20);
        h2.PostTouch(itemId);
        Assert.True(PumpUntil(h2, () => h2.Said.Any(s => s.StartsWith("touch ")), TimeSpan.FromSeconds(20)),
            "no touch after the recompile: " + h2.Diagnose(itemId));
        _out.WriteLine("after the bump: " + string.Join(" | ", h2.Said));

        // Globals kept (count 8 -> 9, power still the -1 it was given), the new bytecode reads -32, no state_entry.
        Assert.Contains("touch 9 saved -1 now -32", h2.Said);
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("entry"));
        Assert.True(File.Exists(CachePath(assetId)), "the recompiled bytecode was not cached");
    }

    // Schema 4 -> 5: float literals keep their exact value now. The schema-4 compiler wrote 2147483520.0 as
    // "2147484000.0", which the assembler read as 2147483904 - so that is what the old bytecode holds.
    private const string FloatStateScript =
        "float big = 2147483520.0;\n" +
        "integer count;\n" +
        "default {\n" +
        "    state_entry() { count = 7; state counting; }\n" +
        "}\n" +
        "state counting {\n" +
        "    state_entry() { llSay(0, \"entry \" + (string)(integer)big); }\n" +
        "    touch_start(integer n) { count = count << 1;\n" +
        "        llSay(0, \"touch \" + (string)count + \" saved \" + (string)(integer)big + \" now \" + (string)(integer)2147483520.0); }\n" +
        "}\n";

    /// <summary>Schema 4 to 5 changed the float literals; a cache still stamped 4 is purged by any later schema too.</summary>
    [Fact]
    public void AScriptSavedUnderSchema4KeepsItsGlobalsAndStateUnder5()
    {
        Assert.True(CurrentSchema >= 5);
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        using (var h1 = new SchedulerHarness(bytecodeDir: CacheDir))
        {
            WriteCache(assetId, FloatStateScript.Replace("2147483520.0", "2147483904.0"));
            h1.RezScript(FloatStateScript, assetId, itemId);
            Assert.True(PumpUntil(h1, () => h1.Said.Contains("entry -2147483648"), TimeSpan.FromSeconds(20)),
                "the old bytecode did not run from the cache: " + h1.Diagnose(itemId));
            h1.PostTouch(itemId);
            Assert.True(PumpUntil(h1, () => h1.Said.Contains("touch 14 saved -2147483648 now -2147483648"), TimeSpan.FromSeconds(20)),
                "old bytecode: " + string.Join(" | ", h1.Said));
            h1.SaveState(itemId);
        }

        File.WriteAllText(VersionFile, "4");
        using var h2 = new SchedulerHarness(bytecodeDir: CacheDir);
        Assert.False(File.Exists(CachePath(assetId)), "the bump did not purge the old bytecode");
        Assert.Equal(CurrentSchema.ToString(), File.ReadAllText(VersionFile).Trim());

        h2.RezScript(FloatStateScript, assetId, itemId);
        Assert.True(PumpUntil(h2, () => h2.InterpreterFor(itemId) != null, TimeSpan.FromSeconds(20)),
            "not recompiled: " + h2.Diagnose(itemId));
        h2.Pump(20);
        h2.PostTouch(itemId);
        Assert.True(PumpUntil(h2, () => h2.Said.Any(s => s.StartsWith("touch ")), TimeSpan.FromSeconds(20)),
            "no touch after the recompile: " + h2.Diagnose(itemId));
        _out.WriteLine("after the bump: " + string.Join(" | ", h2.Said));

        // Globals kept (count 14 -> 28, big still the value it was saved with), still in state counting (no
        // state_entry), and the new bytecode's literal is exact.
        Assert.Contains("touch 28 saved -2147483648 now 2147483520", h2.Said);
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("entry"));
        Assert.True(File.Exists(CachePath(assetId)), "the recompiled bytecode was not cached");
    }

    private const string CountScript =
        "integer n;\n" +
        "default {\n" +
        "    state_entry() { llSay(0, \"entry {0}\"); }\n" +
        "    touch_start(integer t) { ++n; llSay(0, \"{0} \" + (string)n); }\n" +
        "}\n";

    /// <summary>
    /// Schema 5 to 6: an earlier loader could store one save's bytecode in the cache file of another save's asset, so a
    /// restart ran the wrong code. A cache stamped 5 is purged once at the first start: every script compiles again from
    /// its own source, once, and keeps its saved state; the next start purges nothing and compiles nothing.
    /// </summary>
    [Fact]
    public void ACacheStamped5IsPurgedOnceAndEveryScriptRecompilesOnceKeepingItsState()
    {
        Assert.True(CurrentSchema >= 6);
        string Src(string tag) => CountScript.Replace("{0}", tag);
        var (assetA, itemA, assetB, itemB) = (UUID.Random(), UUID.Random(), UUID.Random(), UUID.Random());

        using (var h1 = new SchedulerHarness(bytecodeDir: CacheDir))
        {
            h1.RezScript(Src("alpha"), assetA, itemA);
            h1.RezScript(Src("beta"), assetB, itemB);
            Assert.True(PumpUntil(h1, () => h1.Said.Contains("entry alpha") && h1.Said.Contains("entry beta"), TimeSpan.FromSeconds(20)),
                string.Join(" | ", h1.Said));
            h1.PostTouch(itemA);
            h1.PostTouch(itemB);
            Assert.True(PumpUntil(h1, () => h1.Said.Contains("alpha 1") && h1.Said.Contains("beta 1"), TimeSpan.FromSeconds(20)),
                string.Join(" | ", h1.Said));
            h1.SaveState(itemA);
            h1.SaveState(itemB);
        }

        // What the old loader could leave: alpha's cache file holding another save's bytecode, under a schema-5 stamp.
        WriteCache(assetA, Src("stale"));
        File.WriteAllText(VersionFile, "5");

        var compiled = new System.Collections.Concurrent.ConcurrentBag<string>();
        using (var h2 = new SchedulerHarness(bytecodeDir: CacheDir))
        {
            Assert.True(((global::Phlox.ScriptEngine.PhloxScriptLoader)h2.Loader).PurgedCache);
            Assert.False(File.Exists(CachePath(assetA)), "the bump did not purge the entry");
            Assert.Equal(CurrentSchema.ToString(), File.ReadAllText(VersionFile).Trim());
            ((global::Phlox.ScriptEngine.PhloxScriptLoader)h2.Loader).BeforeCompileForTest = compiled.Add;

            h2.RezScript(Src("alpha"), assetA, itemA);
            h2.RezScript(Src("beta"), assetB, itemB);
            Assert.True(PumpUntil(h2, () => h2.InterpreterFor(itemA) != null && h2.InterpreterFor(itemB) != null, TimeSpan.FromSeconds(20)),
                h2.Diagnose(itemA) + " / " + h2.Diagnose(itemB));
            h2.Pump(20);
            h2.PostTouch(itemA);
            h2.PostTouch(itemB);
            Assert.True(PumpUntil(h2, () => h2.Said.Contains("alpha 2") && h2.Said.Contains("beta 2"), TimeSpan.FromSeconds(20)),
                "state not kept or the wrong code ran: " + string.Join(" | ", h2.Said));
            _out.WriteLine("after the bump: " + string.Join(" | ", h2.Said));
            Assert.DoesNotContain(h2.Said, s => s.StartsWith("entry") || s.StartsWith("stale"));
            Assert.Equal(1, compiled.Count(t => t == Src("alpha")));
            Assert.Equal(1, compiled.Count(t => t == Src("beta")));
            Assert.Equal(2, compiled.Count);
        }

        compiled.Clear();
        using var h3 = new SchedulerHarness(bytecodeDir: CacheDir);
        Assert.False(((global::Phlox.ScriptEngine.PhloxScriptLoader)h3.Loader).PurgedCache);
        ((global::Phlox.ScriptEngine.PhloxScriptLoader)h3.Loader).BeforeCompileForTest = compiled.Add;
        h3.RezScript(Src("alpha"), assetA, itemA);
        h3.RezScript(Src("beta"), assetB, itemB);
        Assert.True(PumpUntil(h3, () => h3.InterpreterFor(itemA) != null && h3.InterpreterFor(itemB) != null, TimeSpan.FromSeconds(20)),
            h3.Diagnose(itemA) + " / " + h3.Diagnose(itemB));
        Assert.Empty(compiled);   // both from the cache the first start wrote
    }

    // Schema 6 -> 7: string literals keep their characters, and a name used before a local of that name is declared
    // means the global. The schema-6 compiler dropped the non-ASCII letters of the second string ("Gre") and read the
    // unset local instead of the global.
    private const string TextScript =
        "integer count = 5;\n" +
        "default {\n" +
        "    state_entry() { llSay(0, \"x\\\\\"); }\n" +
        "    touch_start(integer t) { llSay(0, \"Grüße \" + (string)count); integer count = 1; ++count; llSay(0, \"local \" + (string)count); }\n" +
        "}\n";

    /// <summary>Schema 6 to 7: a cache stamped 6 is purged once; each script recompiles from its source and keeps its globals.</summary>
    [Fact]
    public void ACacheStamped6IsPurgedAndTheScriptRecompilesKeepingItsGlobals()
    {
        Assert.True(CurrentSchema >= 7);
        const string setUp = "default { state_entry() { count = 9; llSay(0, \"set\"); } touch_start(integer t) { } }";
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        using (var h1 = new SchedulerHarness(bytecodeDir: CacheDir))
        {
            // Stands in for the schema-6 bytecode of TextScript: same globals, and it sets the global to 9 so the test
            // can see that the saved global is kept across the recompile.
            WriteCache(assetId, "integer count = 5;\n" + setUp);
            h1.RezScript(TextScript, assetId, itemId);
            Assert.True(PumpUntil(h1, () => h1.Said.Contains("set"), TimeSpan.FromSeconds(20)),
                "the old bytecode did not run from the cache: " + h1.Diagnose(itemId));
            h1.SaveState(itemId);
        }

        File.WriteAllText(VersionFile, "6");
        using var h2 = new SchedulerHarness(bytecodeDir: CacheDir);
        Assert.True(((global::Phlox.ScriptEngine.PhloxScriptLoader)h2.Loader).PurgedCache);
        Assert.False(File.Exists(CachePath(assetId)), "the bump did not purge the old bytecode");
        Assert.Equal(CurrentSchema.ToString(), File.ReadAllText(VersionFile).Trim());

        h2.RezScript(TextScript, assetId, itemId);
        Assert.True(PumpUntil(h2, () => h2.InterpreterFor(itemId) != null, TimeSpan.FromSeconds(20)),
            "not recompiled: " + h2.Diagnose(itemId));
        h2.Pump(20);
        h2.PostTouch(itemId);
        Assert.True(PumpUntil(h2, () => h2.Said.Any(s => s.StartsWith("local ")), TimeSpan.FromSeconds(20)),
            "no touch after the recompile: " + h2.Diagnose(itemId));
        _out.WriteLine("after the bump: " + string.Join(" | ", h2.Said));

        Assert.Contains("Grüße 9", h2.Said);       // the kept global, read before the local of that name
        Assert.Contains("local 2", h2.Said);
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("x"));   // no state_entry: the saved state was restored
        Assert.True(File.Exists(CachePath(assetId)), "the recompiled bytecode was not cached");
    }

    [Fact]
    public void AScriptWhoseBytecodeDidNotChangeResumesItsSleepAcrossTheBump()
    {
        const string sleepScript = "default { state_entry() { llSay(0, \"a\"); llSleep(2); llSay(0, \"b\"); } }";
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        using (var h1 = new SchedulerHarness(bytecodeDir: CacheDir))
        {
            h1.RezScript(sleepScript, assetId, itemId);
            Assert.True(PumpUntil(h1, () => h1.RunStateOf(itemId) == "Sleeping", TimeSpan.FromSeconds(20)),
                "did not reach llSleep: " + h1.Diagnose(itemId));
            Assert.True(File.Exists(CachePath(assetId)), "the compile was not cached");
            h1.SaveState(itemId);
        }

        StampPreviousSchema();
        using var h2 = new SchedulerHarness(bytecodeDir: CacheDir);
        Assert.False(File.Exists(CachePath(assetId)), "the bump did not purge the bytecode");

        h2.RezScript(sleepScript, assetId, itemId);
        Assert.True(PumpUntil(h2, () => h2.Said.Contains("b"), TimeSpan.FromSeconds(20)),
            "the sleep did not resume after the recompile: " + h2.Diagnose(itemId));
        Assert.DoesNotContain("a", h2.Said);
    }
}
