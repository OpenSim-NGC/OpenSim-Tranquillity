using System.Reflection;
using InWorldz.Phlox.Types;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A guard against the failure mode that once stopped this work short: adding
/// built-in overloads must not move any existing function's dispatch index.
///
/// <para>
/// <c>SyscallShim._shimMap</c> is a <b>positional</b> <c>ShimCall[]</c> (<c>SyscallShim.cs:81</c>)
/// indexed by <c>FunctionSig.TableIndex</c>. A changed index is therefore not a build error and not
/// a wrong answer at compile time — it is a different function running, silently, in live content.
/// The baseline is every one of the 674 built-ins as they stood at `34fb6d201b`, before any of this
/// landed; new overloads must append above it and nothing else may move.
/// </para>
/// </summary>
public class DispatchIndexGuardTests
{
    private static Dictionary<string, int> Baseline()
    {
        var dir = Path.GetDirectoryName(typeof(DispatchIndexGuardTests).Assembly.Location);
        var path = Path.Combine(dir!, "dispatch-baseline.txt");
        Assert.True(File.Exists(path), $"the baseline must ship beside the tests: {path}");

        var map = new Dictionary<string, int>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ');
            map[parts[0]] = int.Parse(parts[1]);
        }
        return map;
    }

    /// <summary>Every built-in's TableIndex, however the table is now shaped.</summary>
    private static Dictionary<string, int> Current()
    {
        var map = new Dictionary<string, int>();
        foreach (var sig in AllSigs())
        {
            // The first signature under a name keeps the historical index; overloads added later
            // take fresh ones above the baseline, so the bare name must still map to what it did.
            if (!map.ContainsKey(sig.FunctionName) || sig.TableIndex < map[sig.FunctionName])
                map[sig.FunctionName] = sig.TableIndex;
        }
        return map;
    }

    /// <summary>
    /// Read the table whatever its shape - a flat dictionary of FunctionSig, or one of lists -
    /// so this guard keeps working across the change it exists to police.
    /// </summary>
    internal static IEnumerable<FunctionSig> AllSigs()
    {
        object table = typeof(Defaults).GetField("SystemMethods", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        foreach (var value in ((System.Collections.IDictionary)table).Values)
        {
            if (value is FunctionSig one) { yield return one; continue; }
            foreach (FunctionSig many in (System.Collections.IEnumerable)value) yield return many;
        }
    }

    [Fact]
    public void NoExistingBuiltinChangedItsDispatchIndex()
    {
        var baseline = Baseline();
        var current = Current();

        var moved = new List<string>();
        var gone = new List<string>();
        foreach (var (name, was) in baseline)
        {
            if (!current.TryGetValue(name, out var now)) { gone.Add(name); continue; }
            if (now != was) moved.Add($"{name}: {was} -> {now}");
        }

        Assert.True(gone.Count == 0, "built-ins vanished from the table: " + string.Join(", ", gone));
        Assert.True(moved.Count == 0,
            "a moved TableIndex is a different function at runtime, silently: " + string.Join(", ", moved));
        // 674 at 34fb6d201b; then two NEW names (llsRGB2Linear, llListSortStrided) - the
        // four SL-arity overloads share existing names and do not add entries. Regenerated with
        // RegenerateBaseline below, never by hand.
        // The type-chosen overloads added eight signatures but only ONE new NAME (osSetDynamicTextureDataFace): the other
        // seven are type-discriminated overloads of names already here, and the baseline is keyed by name.
        Assert.Equal(932, baseline.Count);
    }

    /// <summary>
    /// The baseline is REGENERATED from the table, not hand-edited: run this one test with
    /// PHLOX_REGEN_BASELINE=1 in the environment and it rewrites dispatch-baseline.txt in the SOURCE
    /// tree from Current(). Without the variable it is a no-op that passes, so it can live here.
    /// </summary>
    [Fact]
    public void RegenerateBaseline()
    {
        if (Environment.GetEnvironmentVariable("PHLOX_REGEN_BASELINE") != "1") return;
        var here = Path.GetDirectoryName(typeof(DispatchIndexGuardTests).Assembly.Location)!;
        var src = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "dispatch-baseline.txt"));
        Assert.True(File.Exists(src), src);
        var header = File.ReadAllLines(src).TakeWhile(l => l.StartsWith("#")).ToList();
        var body = Current().OrderBy(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value);
        File.WriteAllLines(src, header.Concat(body));
    }

    [Fact]
    public void EveryTableIndexIsStillUniqueAndContiguousFromZero()
    {
        // Two signatures sharing an index would dispatch one to the other's shim; a gap would
        // index past a null entry in _shimMap.
        var indices = AllSigs().Select(s => s.TableIndex).OrderBy(i => i).ToList();
        Assert.Equal(indices.Count, indices.Distinct().Count());
        for (var i = 0; i < indices.Count; i++)
            Assert.Equal(i, indices[i]);
    }

    [Fact]
    public void EveryTableIndexHasAShim()
    {
        // _shimMap is positional; an entry whose index is past the end of it would throw at the
        // first call rather than at load.
        var field = typeof(InWorldz.Phlox.Glue.SyscallShim)
            .GetField("_shimMap", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var shims = (System.Array)field!.GetValue(null)!;

        var highest = AllSigs().Max(s => s.TableIndex);
        Assert.True(highest < shims.Length,
            $"TableIndex {highest} has no shim: _shimMap has {shims.Length} entries");
        foreach (var sig in AllSigs())
            Assert.True(shims.GetValue(sig.TableIndex) is not null,
                $"{sig.FunctionName} dispatches to index {sig.TableIndex}, which is null in _shimMap");
    }
}

/// <summary>
/// The table's dictionary KEY must be the function's own name, or the name-keyed lookups
/// miss it. Found while building the dispatch baseline: <c>botRemoveBot</c> was keyed
/// <c>"Shim_botRemoveBot"</c>, so <c>Defaults.TryGetMethod("botRemoveBot")</c> returned false and
/// both the SLua bridge (<c>SLuaCompiler.cs:2118</c>) and the interpreter's method map
/// (<c>Interpreter.Actions.cs:2011</c>) silently had no entry for it. The compiler was unaffected —
/// it reads <c>FunctionName</c>, which was always right — which is why nothing ever failed loudly.
/// </summary>
public class TableKeyTests
{
    [Fact]
    public void EveryTableKeyIsItsFunctionName()
    {
        var wrong = new List<string>();
        foreach (var (key, sigs) in InWorldz.Phlox.Types.Defaults.SystemMethods)
            foreach (var sig in sigs)
                if (sig.FunctionName != key)
                    wrong.Add($"{key} -> {sig.FunctionName}");

        // SystemMethods is grouped BY FunctionName, so this holds by construction; the check that
        // bites is the raw table below.
        Assert.True(wrong.Count == 0, string.Join(", ", wrong));
    }

    [Fact]
    public void EveryRawTableKeyIsTheFunctionNameOrItsOverloadForm()
    {
        var raw = (System.Collections.IDictionary)typeof(InWorldz.Phlox.Types.Defaults)
            .GetField("RawMethods", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var wrong = new List<string>();
        foreach (System.Collections.DictionaryEntry e in raw)
        {
            var key = (string)e.Key;
            var sig = (InWorldz.Phlox.Types.FunctionSig)e.Value!;
            var expectedOverload = sig.FunctionName + InWorldz.Phlox.Types.Defaults.OverloadSeparator + sig.ParamTypes.Length;
            // A signature that shares its arity with another of the same name is keyed by type
            // as well, which is exactly what SymbolNameFor calls it. The arity-only form stays legal for
            // the entries that had it before the type codes existed.
            var expectedTyped = InWorldz.Phlox.Types.Defaults.SymbolNameFor(sig);
            if (key != sig.FunctionName && key != expectedOverload && key != expectedTyped)
                wrong.Add($"key '{key}' is none of '{sig.FunctionName}', '{expectedOverload}', '{expectedTyped}'");
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void TheNameKeyedLookupFindsBotRemoveBot()
    {
        Assert.True(InWorldz.Phlox.Types.Defaults.TryGetMethod("botRemoveBot", out var sig));
        Assert.Equal("botRemoveBot", sig.FunctionName);
    }
}
