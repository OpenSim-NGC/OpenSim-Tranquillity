using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Several regions' engines starting at once on one bytecode folder. Each loader checks the
/// <c>.schema_version</c> stamp when it is built (PhloxScriptLoader.EnsureCacheSchemaVersion). Before, the stamp was read
/// outside the method's try block and every loader checked at the same time, so one could read while another wrote it
/// (IOException out of the constructor) and several could purge. Now the check is guarded and runs one
/// loader at a time: no exception, exactly one purge when the stamp is old or missing, none when it is current, and the
/// current stamp at the end.
/// Each test uses its own folder through a test seam (PhloxEngine.BytecodeCacheDir), so the class runs in parallel.
/// </summary>
public class CacheSchemaStampConcurrencyTests
{
    private const int Loaders = 8;
    private const int Rounds = 60;

    private readonly ITestOutputHelper _out;
    public CacheSchemaStampConcurrencyTests(ITestOutputHelper o) => _out = o;

    private static int CurrentSchema => (int)typeof(global::Phlox.ScriptEngine.PhloxScriptLoader)
        .GetField("CACHE_SCHEMA_VERSION", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private sealed record Round(int Purges, List<Exception> Failures, string Stamp, int PlxLeft);

    /// <summary>
    /// Starts <see cref="Loaders"/> loaders on <paramref name="dir"/> together: released at one moment, each then waits a
    /// random 0-1.5 ms (a spin; a sleep is 15 ms on Windows), so some check the stamp while another is writing it.
    /// </summary>
    private static Round StartTogether(string dir, Random rnd)
    {
        long[] stagger = Enumerable.Range(0, Loaders).Select(i => i == 0 ? 0L : rnd.NextInt64(0, System.Diagnostics.Stopwatch.Frequency * 3 / 2000)).ToArray();
        var failures = new List<Exception>();
        var loaders = new global::Phlox.ScriptEngine.PhloxScriptLoader[Loaders];
        using var go = new Barrier(Loaders);
        var threads = Enumerable.Range(0, Loaders).Select(i => new Thread(() =>
        {
            var engine = new global::Phlox.ScriptEngine.PhloxEngine { BytecodeCacheDir = dir };
            go.SignalAndWait();
            long until = System.Diagnostics.Stopwatch.GetTimestamp() + stagger[i];
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(20);
            try { loaders[i] = new global::Phlox.ScriptEngine.PhloxScriptLoader(null, null, () => { }, engine); }
            catch (Exception e) { lock (failures) failures.Add(e); }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(30)), "a loader did not finish starting"));

        foreach (var l in loaders) l?.Stop();
        string stampFile = Path.Combine(dir, ".schema_version");
        return new Round(
            loaders.Count(l => l != null && l.PurgedCache),
            failures,
            File.Exists(stampFile) ? File.ReadAllText(stampFile).Trim() : null,
            Directory.GetFiles(dir, "*.plx", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("old")]
    [InlineData("garbage")]
    [InlineData("current")]
    public void LoadersStartingAtOnceCheckTheStampOnceAndConsistently(string stamp)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "ScriptEngines", "Phlox", "phlox55-stamp",
                                   Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        bool purgeExpected = stamp != "current";
        int exceptions = 0, wrongPurges = 0, wrongStamps = 0, plxKeptWrongly = 0;
        var rnd = new Random(55);
        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                string dir = Path.Combine(root, "r" + round);
                Directory.CreateDirectory(Path.Combine(dir, "ab"));
                File.WriteAllText(Path.Combine(dir, "ab", "cached.plx"), "bytecode");
                string stampFile = Path.Combine(dir, ".schema_version");
                if (stamp == "old") File.WriteAllText(stampFile, (CurrentSchema - 1).ToString());
                else if (stamp == "garbage") File.WriteAllText(stampFile, "not a number");
                else if (stamp == "current") File.WriteAllText(stampFile, CurrentSchema.ToString());

                var r = StartTogether(dir, rnd);
                exceptions += r.Failures.Count;
                foreach (var f in r.Failures.Take(1)) _out.WriteLine($"round {round}: {f.GetType().Name}: {f.Message}");
                if (r.Purges != (purgeExpected ? 1 : 0)) { wrongPurges++; _out.WriteLine($"round {round}: {r.Purges} purges"); }
                if (r.Stamp != CurrentSchema.ToString()) { wrongStamps++; _out.WriteLine($"round {round}: stamp '{r.Stamp}'"); }
                if (r.PlxLeft != (purgeExpected ? 0 : 1)) plxKeptWrongly++;
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        _out.WriteLine($"{stamp}: {Rounds} rounds x {Loaders} loaders: exceptions={exceptions} wrongPurgeCounts={wrongPurges} " +
                       $"wrongStamps={wrongStamps} plxWrong={plxKeptWrongly}");
        Assert.Equal(0, exceptions);
        Assert.Equal(0, wrongPurges);
        Assert.Equal(0, wrongStamps);
        Assert.Equal(0, plxKeptWrongly);
    }
}
