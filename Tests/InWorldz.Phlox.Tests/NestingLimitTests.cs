using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Nesting limits are COUNTED, so a script compiles or fails the same way whatever the JIT has done:
/// The earlier stack-based guard tripped at ~3,017 levels in a fresh process and ~9,739 in a warm one, so the
/// same script compiled on a running region and failed in the harness. Every case runs in a child process
/// (PhloxCompileProbe) - "cold" is a fresh process per case, "warm" one process that compiled 100 ordinary
/// scripts first - and both must give the same outcome. The child also means a real stack overflow kills only it.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class NestingLimitTests
{
    private readonly ITestOutputHelper _out;
    public NestingLimitTests(ITestOutputHelper o) => _out = o;

    // The limits (InWorldz.Phlox.Compiler.NestingLimits); chosen from measurement, see PhloxKnownDefects.
    private const int ExprLimit = 1000;
    private const int BlockLimit = 500;
    private const int ChainLimit = 2500;
    private const int AssignLimit = 64;

    private static readonly string[] LslExprKinds = { "paren", "neg", "not", "bitnot", "cast", "call", "list" };
    private static readonly string[] LslBlockKinds = { "block", "bare" };
    private static readonly string[] LuaExprKinds = { "paren", "neg", "not", "table", "concat", "call" };
    private static readonly string[] LuaBlockKinds = { "func", "block" };

    private static string ExprMsg => $"expression nested too deeply (limit {ExprLimit})";
    private static string BlockMsg => $"blocks nested too deeply (limit {BlockLimit})";
    private static string ChainMsg => $"else-if chain too long (limit {ChainLimit})";
    private static string AssignMsg => $"assignment chain too long (limit {AssignLimit})";

    // ---- running the probe ----

    private static string ProbePath => Path.Combine(Path.GetDirectoryName(typeof(NestingLimitTests).Assembly.Location)!, "PhloxCompileProbe.dll");

    /// <summary>Run the probe once; returns spec -> result line ("DIED ..." if the process did not finish the case).</summary>
    private static Dictionary<string, string> Probe(string mode, IEnumerable<string> specs)
    {
        var list = specs.ToList();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(ProbePath);
        psi.ArgumentList.Add(mode);
        foreach (var s in list) psi.ArgumentList.Add(s);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        bool exited = p.WaitForExit(120_000);   // a case that runs away is a failure, not a hung test run
        if (!exited) { try { p.Kill(true); } catch { } }
        var lines = stdout.Result.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("CASE ")).ToList();
        var result = new Dictionary<string, string>();
        foreach (var s in list)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith("CASE " + s + " "));
            result[s] = line != null ? line.Substring(("CASE " + s + " ").Length)
                : $"DIED exit={(exited ? p.ExitCode.ToString("X8") : "timeout")} {stderr.Result.Split('\n').FirstOrDefault(l => l.Contains("overflow") || l.Contains("Exception"))?.Trim()}";
        }
        return result;
    }

    /// <summary>Each spec cold (its own fresh process, four at a time) and all of them warm (one process).</summary>
    private (Dictionary<string, string> cold, Dictionary<string, string> warm) ColdAndWarm(IReadOnlyList<string> specs)
    {
        var cold = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        Parallel.ForEach(specs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, s => cold[s] = Probe("cold", new[] { s })[s]);
        var warm = Probe("warm", specs);
        foreach (var s in specs) _out.WriteLine($"{s,-24} cold: {cold[s]}\n{"",-24} warm: {warm[s]}");
        return (new Dictionary<string, string>(cold), warm);
    }

    private static void Expect(List<string> failures, string spec, string mode, string result, bool compiles, string message)
    {
        if (compiles && !result.StartsWith("COMPILED")) failures.Add($"{spec} {mode}: expected to compile, got {result}");
        if (!compiles && !(result.StartsWith("ERROR") && result.Contains(message))) failures.Add($"{spec} {mode}: expected '{message}', got {result}");
    }

    private void AtAndPast(string lang, string[] kinds, int limit, string message)
    {
        var specs = kinds.SelectMany(k => new[] { $"{lang}:{k}:{limit}", $"{lang}:{k}:{limit + 1}" }).ToList();
        var (cold, warm) = ColdAndWarm(specs);
        var failures = new List<string>();
        foreach (var k in kinds)
            foreach (var (mode, r) in new[] { ("cold", cold), ("warm", warm) })
            {
                // A list inside a list is a compile error at any depth (SL; Halcyon's CheckListLiteral), so the deepest
                // list the limit allows reaches the type check and fails there: that error, not the limit or a crash.
                if (lang == "lsl" && k == "list")
                {
                    string atLimit = r[$"{lang}:{k}:{limit}"];
                    if (!(atLimit.StartsWith("ERROR") && atLimit.Contains("A list can not contain another list") && !atLimit.Contains(message)))
                        failures.Add($"{lang}:{k}:{limit} {mode}: expected the nested-list error, got {atLimit}");
                }
                else
                    Expect(failures, $"{lang}:{k}:{limit}", mode, r[$"{lang}:{k}:{limit}"], true, message);
                Expect(failures, $"{lang}:{k}:{limit + 1}", mode, r[$"{lang}:{k}:{limit + 1}"], false, message);
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ---- the tests ----

    [Fact] public void LslExpressionsCompileAtTheLimitAndFailPastItColdAndWarm() => AtAndPast("lsl", LslExprKinds, ExprLimit, ExprMsg);
    [Fact] public void LslBlocksCompileAtTheLimitAndFailPastItColdAndWarm() => AtAndPast("lsl", LslBlockKinds, BlockLimit, BlockMsg);
    [Fact] public void LslAssignmentChainsCompileAtTheLimitAndFailPastItColdAndWarm() => AtAndPast("lsl", new[] { "assign" }, AssignLimit, AssignMsg);
    [Fact] public void SluaExpressionsCompileAtTheLimitAndFailPastItColdAndWarm() => AtAndPast("lua", LuaExprKinds, ExprLimit, ExprMsg);
    [Fact] public void SluaBlocksAndFunctionBodiesCompileAtTheLimitAndFailPastItColdAndWarm() => AtAndPast("lua", LuaBlockKinds, BlockLimit, BlockMsg);

    [Fact]
    public void AThousandBranchElseIfChainCompilesAndTheChainHasItsOwnLimitColdAndWarm()
    {
        var failures = new List<string>();
        foreach (var lang in new[] { "lsl", "lua" })
        {
            var specs = new[] { $"{lang}:elseif:1000", $"{lang}:elseif:{ChainLimit}", $"{lang}:elseif:{ChainLimit + 1}" };
            var (cold, warm) = ColdAndWarm(specs);
            foreach (var (mode, r) in new[] { ("cold", cold), ("warm", warm) })
            {
                Expect(failures, specs[0], mode, r[specs[0]], true, ChainMsg);
                Expect(failures, specs[1], mode, r[specs[1]], true, ChainMsg);
                Expect(failures, specs[2], mode, r[specs[2]], false, ChainMsg);
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void NothingOverflowsAtTwoHundredThousandLevelsColdOrWarm()
    {
        const int n = 200_000;
        var specs = LslExprKinds.Concat(LslBlockKinds).Append("assign").Select(k => $"lsl:{k}:{n}")
            .Concat(LuaExprKinds.Concat(LuaBlockKinds).Select(k => $"lua:{k}:{n}"))
            .Concat(new[] { $"lsl:elseif:{n}", $"lua:elseif:{n}" }).ToList();
        var (cold, warm) = ColdAndWarm(specs);
        var failures = new List<string>();
        foreach (var s in specs)
            foreach (var (mode, r) in new[] { ("cold", cold), ("warm", warm) })
            {
                string expected = s.Contains(":elseif:") ? ChainMsg : s.Contains(":assign:") ? AssignMsg
                    : (LslBlockKinds.Concat(LuaBlockKinds).Any(k => s.Contains($":{k}:")) ? BlockMsg : ExprMsg);
                Expect(failures, s, mode, r[s], false, expected);
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
