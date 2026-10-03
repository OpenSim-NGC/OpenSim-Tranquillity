using System;
using System.Collections.Generic;
using System.Linq;
using InWorldz.Phlox.Types;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Phlox chose a built-in's overload by the NUMBER of arguments, so two
/// signatures of one name and one arity could not both exist - five OSSL forms were left out for that reason
/// alone. The type pass now chooses among the signatures of the call's arity by the ARGUMENT TYPES, exact
/// match ahead of an LSL implicit widening, and hands the gen pass the symbol it picked; every such signature
/// gets a symbol name of its own.
/// </summary>
public class OverloadByTypeTests
{
    private readonly ITestOutputHelper _out;
    public OverloadByTypeTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private static SchedulerHarness Scene() => new SchedulerHarness(
        cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));

    /// <summary>
    /// Pump until the script's last line is said, up to 30 s, instead of a fixed second. Each
    /// llSay now pauses 15 ms, and under a full parallel run the second was not always enough.
    /// </summary>
    private static void PumpUntilSaid(SchedulerHarness h, string lastLinePrefix)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!h.Said.Any(s => s.StartsWith(lastLinePrefix, StringComparison.Ordinal)) && DateTime.UtcNow < until)
            h.PumpFor(TimeSpan.FromMilliseconds(50));
    }

    /// <summary>The two osSetPenColor forms differ only in the type of their second argument.</summary>
    [Fact]
    public void OsSetPenColorPicksTheStringFormOrTheVectorFormByType()
    {
        using var h = Scene();
        h.RezScript(@"default { state_entry() {
            llSay(0, ""name="" + osSetPenColor("""", ""Red""));
            llSay(0, ""vec="" + osSetPenColor("""", <1,0,0>));
        } }");
        PumpUntilSaid(h, "vec=");
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        // the colour-name form passes the name through; the vector form renders AARRGGBB - two shims
        Assert.Contains("name=PenColor Red; ", h.Said);
        Assert.Contains("vec=PenColor FFFF0000; ", h.Said);

        // and they are two table entries, under two symbol names
        var sigs = Defaults.SystemMethods["osSetPenColor"];
        var byString = sigs.Single(s => s.ParamTypes.SequenceEqual(new[] { VarType.String, VarType.String }));
        var byVector = sigs.Single(s => s.ParamTypes.SequenceEqual(new[] { VarType.String, VarType.Vector }));
        Assert.NotEqual(byString.TableIndex, byVector.TableIndex);
        Assert.NotEqual(Defaults.SymbolNameFor(byString), Defaults.SymbolNameFor(byVector));
        _out.WriteLine($"osSetPenColor: {Defaults.SymbolNameFor(byString)}={byString.TableIndex}, " +
                       $"{Defaults.SymbolNameFor(byVector)}={byVector.TableIndex}");
    }

    /// <summary>The other four forms the arity rule kept out, each against its same-arity sibling.</summary>
    [Fact]
    public void TheTypeDiscriminatedFormsReachTheirOwnShims()
    {
        using var h = Scene();
        h.RezScript(@"default { state_entry() {
            llSay(0, ""ff="" + (string)osApproxEquals(1.0, 1.0));
            llSay(0, ""vv="" + (string)osApproxEquals(<1,2,3>, <1,2,3>));
            llSay(0, ""vvno="" + (string)osApproxEquals(<1,2,3>, <1,2,4>));
            llSay(0, ""rr="" + (string)osApproxEquals(<0,0,0,1>, <0,0,0,1>));
            llSay(0, ""vvm="" + (string)osApproxEquals(<1,2,3>, <1,2,3.5>, 1.0));
            llSay(0, ""rrm="" + (string)osApproxEquals(<0,0,0,1>, <0,0,0.4,1>, 1.0));
            llSay(0, ""slerpv="" + (string)osSlerp(<1,0,0>, <0,1,0>, 1.0));
            llSay(0, ""slerpr="" + (string)osSlerp(<0,0,0,1>, <0,0,0,1>, 1.0));
        } }");
        PumpUntilSaid(h, "slerpr=");
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");

        Assert.Contains("ff=1", h.Said);
        Assert.Contains("vv=1", h.Said);
        Assert.Contains("vvno=0", h.Said);
        Assert.Contains("rr=1", h.Said);
        Assert.Contains("vvm=1", h.Said);      // inside the margin
        Assert.Contains("rrm=1", h.Said);
        // the vector form returns a VECTOR: the rotation shim would have pushed a four-component value
        Assert.Single(h.Said, s => s.StartsWith("slerpv=<") && s.Count(c => c == ',') == 2);
        Assert.Single(h.Said, s => s.StartsWith("slerpr=<") && s.Count(c => c == ',') == 3);
        Assert.DoesNotContain(h.SaidOn, s => s.Channel == DebugChannel);
    }

    /// <summary>
    /// The chooser itself: an exact type beats a widening, a widening still resolves, and the arity forms added
    /// earlier keep resolving exactly as they did.
    /// </summary>
    [Fact]
    public void SelectOverloadPrefersAnExactMatchAndStillAllowsTheLslWidenings()
    {
        // osSetProjectionParams: (int, key, float, float, float) / (int, int, key, float x3) / (key, int, key, float x3)
        var byLink = Defaults.SelectOverload("osSetProjectionParams",
            new VarType?[] { VarType.Integer, VarType.Integer, VarType.Key, VarType.Float, VarType.Float, VarType.Float },
            out var amb1);
        Assert.False(amb1.HasValue);
        Assert.Equal(VarType.Integer, byLink.Value.ParamTypes[0]);

        var byKey = Defaults.SelectOverload("osSetProjectionParams",
            new VarType?[] { VarType.Key, VarType.Integer, VarType.Key, VarType.Float, VarType.Float, VarType.Float },
            out var amb2);
        Assert.False(amb2.HasValue);
        Assert.Equal(VarType.Key, byKey.Value.ParamTypes[0]);
        Assert.NotEqual(byLink.Value.TableIndex, byKey.Value.TableIndex);

        // a string where a key is wanted is LSL's own interchange, and an integer widens to float
        var widened = Defaults.SelectOverload("osApproxEquals",
            new VarType?[] { VarType.Integer, VarType.Integer }, out var amb3);
        Assert.False(amb3.HasValue);
        Assert.Equal(VarType.Float, widened.Value.ParamTypes[0]);

        // nothing of that arity: no candidate, and the caller falls back to the arity path
        Assert.False(Defaults.SelectOverload("osSlerp", new VarType?[] { VarType.Vector }, out _).HasValue);

        // an unknown argument type (an error subtree) never invents an ambiguity
        Assert.True(Defaults.SelectOverload("osSetPenColor", new VarType?[] { null, null }, out var amb4).HasValue);
        Assert.False(amb4.HasValue);
    }

    /// <summary>Every signature in the table still has a symbol name of its own - no name collision.</summary>
    [Fact]
    public void EverySignatureHasItsOwnSymbolName()
    {
        var seen = new Dictionary<string, FunctionSig>();
        foreach (var sig in Defaults.AllMethods)
        {
            string name = Defaults.SymbolNameFor(sig);
            Assert.False(seen.ContainsKey(name),
                $"two signatures share the symbol name {name}: indices {(seen.TryGetValue(name, out var o) ? o.TableIndex : -1)} and {sig.TableIndex}");
            seen[name] = sig;
        }
        _out.WriteLine($"{seen.Count} signatures, {seen.Count} symbol names");
    }
}
