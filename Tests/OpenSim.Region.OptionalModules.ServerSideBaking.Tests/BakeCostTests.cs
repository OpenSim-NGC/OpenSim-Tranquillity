using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSimNGC.Appearance.Baking;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S2 Part 2 — Ledger Q-10: where does the bake second go? The live cold bake was 2788 ms for five channels at
/// BakeSize 1024 (Truly) and 3078 ms (Aleric), with no instrumentation to say which phase owned it. This runs the
/// same two outfits through the same orchestrator at the same size with <see cref="BakeTimings"/> attached, cold
/// and then warm, and writes the split to <c>Golden/last-run-cost-&lt;set&gt;.txt</c> next to the golden harness's
/// own reports.
///
/// <para>
/// <b>What this measures and what it does not.</b> Decode, composite and encode are the real thing: the same
/// library code, the same textures, the same size, the same quality the module ships. The two I/O phases are not:
/// the asset service here is a <see cref="FakeAssetService"/>, a dictionary in memory, so its fetch and store
/// figures are a floor of roughly zero rather than an estimate of what MySQL and Robust cost. That is exactly what
/// makes the arithmetic useful — the CPU phases are measured, so on the live sim asset I/O is whatever is left
/// over, and the run's own INFO line now prints both halves.
/// </para>
///
/// <para>It asserts only what must not regress: a warm run composites nothing and its whole cost is a rounding error.</para>
/// </summary>
public class BakeCostTests
{
    private readonly ITestOutputHelper _out;
    public BakeCostTests(ITestOutputHelper output) { _out = output; }

    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    /// <summary>Production settings: ADR-008's shipped size and the module's default encode quality.</summary>
    private const int LiveBakeSize = 1024;
    private const double LiveQuality = 0.85;

    private static string GoldenDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Source", "OpenSimNGC.Appearance.Baking.Tests", "Golden"));

    public static IEnumerable<object[]> Sets()
        => new[] { new object[] { "truly-stock" }, new object[] { "aleric-max" } };

    [Theory]
    [MemberData(nameof(Sets))]
    public void where_the_bake_second_goes(string set)
    {
        var fixtures = Path.Combine(GoldenDir(), set, "fixtures");
        if (!File.Exists(Path.Combine(fixtures, "avatar.json")))
        {
            _out.WriteLine($"SKIPPED [{set}]: no fixtures at {fixtures}; run Golden/fetch-fixtures.sh {set}");
            return;
        }

        var assets = new FakeAssetService();
        foreach (var f in Directory.GetFiles(fixtures))
        {
            var ext = Path.GetExtension(f);
            sbyte type = ext switch { ".bodypart" => (sbyte)AssetType.Bodypart, ".clothing" => (sbyte)AssetType.Clothing, ".j2c" => (sbyte)AssetType.Texture, _ => -1 };
            if (type < 0) continue;
            var id = Path.GetFileNameWithoutExtension(f);
            assets.Put(new AssetBase(new UUID(id), id, type, Agent.ToString()) { Data = File.ReadAllBytes(f) });
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "avatar.json")));
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();
        foreach (var w in doc.RootElement.GetProperty("wearables").EnumerateArray())
            wearables[w.GetProperty("type").GetInt32()].Add(new UUID(w.GetProperty("itemId").GetString()), new UUID(w.GetProperty("assetId").GetString()));
        var vp = doc.RootElement.GetProperty("visualParams").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();

        var compositor = new TexLayerCompositor();
        var avatars = new FakeAvatarService();
        var appearance = new AvatarAppearance();

        // The compositor lazily loads avatar_lad.xml and 56 mask TGAs on first use and caches resampled masks.
        // On the live sim that happens once per region lifetime, not once per bake, so it is warmed away here
        // rather than charged to the cold bake — and how much it is, is reported.
        var warmup = System.Diagnostics.Stopwatch.StartNew();
        BakeOrchestrator.Run(Agent, BakeReason.Console, wearables, vp, new AvatarAppearance(), assets, null,
            new SkiaBakeBackend(compositor) { Quality = LiveQuality }, compositor, LiveBakeSize, 0, CancellationToken.None);
        warmup.Stop();

        var cold = BakeOrchestrator.Run(Agent, BakeReason.Console, wearables, vp, appearance, assets, avatars,
            new SkiaBakeBackend(compositor) { Quality = LiveQuality }, compositor, LiveBakeSize, 7, CancellationToken.None);
        var warm = BakeOrchestrator.Run(Agent, BakeReason.Console, wearables, vp, appearance, assets, avatars,
            new SkiaBakeBackend(compositor) { Quality = LiveQuality }, compositor, LiveBakeSize, 7, CancellationToken.None);

        var report = new StringBuilder();
        report.AppendLine($"bake cost run {DateTimeOffset.Now:O}  set={set}  size={LiveBakeSize}  quality={LiveQuality}");
        report.AppendLine($"asset service: in-memory fake, so the fetch and store columns are a floor (~0), not a grid figure");
        report.AppendLine($"compositor first-use warm-up (avatar_lad.xml + 56 mask TGAs + a full bake), charged to neither run below: {warmup.ElapsedMilliseconds} ms");
        report.AppendLine();
        Row(report, "cold", cold);
        Row(report, "warm", warm);
        report.AppendLine();
        report.AppendLine($"cold: {cold.Count(ChannelStatus.Baked)} baked, {cold.Count(ChannelStatus.Reused)} reused — {cold.Timings.Summary}");
        report.AppendLine($"warm: {warm.Count(ChannelStatus.Baked)} baked, {warm.Count(ChannelStatus.Reused)} reused — {warm.Timings.Summary}");

        var text = report.ToString();
        File.WriteAllText(Path.Combine(GoldenDir(), $"last-run-cost-{set}.txt"), text);
        _out.WriteLine(text);

        // the point of the whole slice: a warm bake composites nothing
        Assert.Equal(0, warm.Count(ChannelStatus.Baked));
        Assert.Equal(cold.Count(ChannelStatus.Baked), warm.Count(ChannelStatus.Reused));
        Assert.Equal(0, warm.Timings.ChannelsComposited);
        Assert.Equal(0, warm.Timings.TexturesDecoded);
        Assert.Equal(0, warm.Timings.AssetsStored);
        Assert.True(warm.ElapsedMs < Math.Max(50, cold.ElapsedMs / 4),
            $"a warm bake should be a small fraction of a cold one: {warm.ElapsedMs} ms against {cold.ElapsedMs} ms");
    }

    private static void Row(StringBuilder sb, string label, BakeOutcome o)
    {
        var t = o.Timings;
        var other = Math.Max(0, o.ElapsedMs - (long)t.Accounted.TotalMilliseconds);
        if (sb.Length > 0 && label == "cold")
            sb.AppendLine("run    total   fetch  decode  composite  encode   store   other    | fetch decode composite encode store  (% of total)");
        string Pct(TimeSpan v) => o.ElapsedMs == 0 ? "-" : (100.0 * v.TotalMilliseconds / o.ElapsedMs).ToString("F1", CultureInfo.InvariantCulture);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0,-6} {1,5} {2,7:F0} {3,7:F0} {4,10:F0} {5,7:F0} {6,7:F0} {7,7} | {8,5} {9,6} {10,9} {11,6} {12,5}",
            label, o.ElapsedMs, t.AssetFetch.TotalMilliseconds, t.Decode.TotalMilliseconds, t.Composite.TotalMilliseconds,
            t.Encode.TotalMilliseconds, t.AssetStore.TotalMilliseconds, other,
            Pct(t.AssetFetch), Pct(t.Decode), Pct(t.Composite), Pct(t.Encode), Pct(t.AssetStore)));
    }
}
