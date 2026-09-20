using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace OpenSimNGC.Appearance.Baking.Tests.Golden;

/// <summary>
/// The golden harness. Each subdirectory of Golden/ that holds a manifest.json is one **reference set**:
/// an avatar's worn outfit plus the reference bakes (LL compositor output, captured via the client-bake path
/// named in that manifest). The authority is the LL compositor, never the capturing client (Ledger P-1).
/// <list type="bullet">
///   <item><c>truly-stock/</c> — Truly Bazar, stock Library outfit (S0b).</item>
///   <item><c>aleric-max/</c> — Aleric Fenwood, a richer outfit: socks, jacket and a tattoo (S1b, Ledger Q-11).</item>
/// </list>
/// Fixtures are fetched per set by <c>fetch-fixtures.sh &lt;set&gt;</c> into <c>&lt;set&gt;/fixtures/</c> (gitignored);
/// when they are absent the test reports that and returns without asserting anything.
///
/// <para><see cref="reference_set_versus_library_bakes"/> bakes at the manifest's <c>bakeSize</c> — which is the
/// shipped <c>[Appearance] BakeSize</c> (ADR-008: 1024), not the reference's own size — and asserts, per channel:
/// RGB (mean |d| &lt;= 4, at most 5% of pixels with |d| &gt; 8 — both skipped when the reference alpha is entirely
/// zero, as for a bald hair), alpha (mean |d| &lt;= 2) and the 5th component, the morph mask (mean |d| &lt;= 4 and,
/// unless the reference's mask is uniform, at most 5% of pixels with |d| &gt; 8). It writes the table and the full
/// per-layer decision log to <c>Golden/last-run-&lt;set&gt;.txt</c>. The numbers came first (S0b), the thresholds after.</para>
///
/// <para><see cref="bake_size_sweep"/> (S1b Part 2) repeats the comparison at 512, 1024 and 2048 and reports the
/// encoded byte size per channel per size. It asserts only that every channel encodes and decodes at the size asked
/// for: it is the evidence for ADR-008's default, not a gate on it.</para>
/// </summary>
public class GoldenTests
{
    private readonly ITestOutputHelper _out;
    public GoldenTests(ITestOutputHelper output) { _out = output; }

    private static string GoldenDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>Every set present: a subdirectory holding a manifest.json.</summary>
    public static IEnumerable<object[]> Sets()
        => Directory.EnumerateDirectories(GoldenDir())
                    .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                    .Select(d => Path.GetFileName(d)!)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .Select(n => new object[] { n });

    private sealed record Manifest(string Avatar, string Outfit, string Captured,
        [property: System.Text.Json.Serialization.JsonPropertyName("captured_via")] string? CapturedVia,
        int BakeSize, Dictionary<string, string> Goldens);
    private sealed record AvatarRow(int Type, int Index, string ItemId, string AssetId);
    private sealed record AvatarJson(string PrincipalId, List<AvatarRow> Wearables, List<int> VisualParams);

    private static readonly (string Key, BakeChannel Channel)[] ChannelKeys =
    {
        ("head", BakeChannel.Head), ("upper", BakeChannel.Upper), ("lower", BakeChannel.Lower), ("eyes", BakeChannel.Eyes), ("hair", BakeChannel.Hair),
        ("skirt", BakeChannel.Skirt), ("leftarm", BakeChannel.LeftArm), ("leftleg", BakeChannel.LeftLeg), ("aux1", BakeChannel.Aux1), ("aux2", BakeChannel.Aux2), ("aux3", BakeChannel.Aux3),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>One channel's comparison against its reference.</summary>
    private sealed record Row(string Key, double MeanRgb, double MeanA, double PctRgb, double MeanM, double PctM,
        string MRef, bool RefAlphaAllZero, int OurW, int OurH, int RefW, int RefH, bool RefAlpha, int Bytes, string GoldenId);

    private sealed class SetContext
    {
        public required string Name;
        public required string Dir;
        public required string Fixtures;
        public required Manifest Manifest;
        public required AvatarJson Avatar;
        public required List<WearableInput> Wearables;
        public required List<ParsedWearable> Parsed;
        public required Dictionary<UUID, TextureInput> Textures;
        public required List<string> NullSlots;
        public required Dictionary<int, float> VisualParams;
    }

    /// <summary>Loads a set, or returns null with a reason when its fixtures are not there.</summary>
    private static SetContext? Load(string set, out string? reason)
    {
        reason = null;
        var dir = Path.Combine(GoldenDir(), set);
        var fixtures = Path.Combine(dir, "fixtures");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")), JsonOpts)!;
        if (!Directory.Exists(fixtures) || !File.Exists(Path.Combine(fixtures, "avatar.json")))
        {
            reason = $"SKIPPED [{set}]: no fixtures at {fixtures}. Run Golden/fetch-fixtures.sh {set} (needs the Legion grid DB and Robust) to populate them; nothing is asserted without them.";
            return null;
        }

        var avatar = JsonSerializer.Deserialize<AvatarJson>(File.ReadAllText(Path.Combine(fixtures, "avatar.json")), JsonOpts)!;
        string Fixture(string uuid, params string[] exts)
        {
            foreach (var e in exts) { var p = Path.Combine(fixtures, $"{uuid}.{e}"); if (File.Exists(p)) return p; }
            throw new FileNotFoundException($"fixture {uuid} ({string.Join("/", exts)}) missing from {fixtures}; re-run fetch-fixtures.sh {set}");
        }

        // the request: every worn wearable's text, every texture they reference in a drawn slot.
        // A worn slot carrying the null asset id is still a worn wearable — the viewer counts wearables, not
        // textures (Docs/MORPH-MASK-PASS.md §2.4) — so it goes in with empty text and is named in the report.
        var wearables = new List<WearableInput>();
        var nullSlots = new List<string>();
        foreach (var row in avatar.Wearables.OrderBy(w => w.Type).ThenBy(w => w.Index))
        {
            if (!UUID.TryParse(row.AssetId, out var assetId) || assetId.IsZero())
            {
                nullSlots.Add($"{(WearableKind)row.Type}:{row.Index}");
                wearables.Add(new WearableInput(UUID.Zero, row.Type, ""));
                continue;
            }
            wearables.Add(new WearableInput(assetId, row.Type, File.ReadAllText(Fixture(row.AssetId, "bodypart", "clothing"), Encoding.UTF8)));
        }
        var parsed = wearables.Where(w => w.RawText.Length > 0).Select(w => WearableParser.Parse(w.RawText)).ToList();
        var textures = new Dictionary<UUID, TextureInput>();
        foreach (var pw in parsed)
            foreach (var (_, id) in pw.Textures)
            {
                if (id == UUID.Zero || id == BakeConstants.DefaultAvatarTexture || textures.ContainsKey(id)) continue;
                var p = Path.Combine(fixtures, $"{id}.j2c");
                if (File.Exists(p)) textures[id] = new TextureInput(id, File.ReadAllBytes(p));
            }

        return new SetContext
        {
            Name = set, Dir = dir, Fixtures = fixtures, Manifest = manifest, Avatar = avatar,
            Wearables = wearables, Parsed = parsed, Textures = textures, NullSlots = nullSlots,
            VisualParams = DecodeVisualParams(avatar.VisualParams),
        };
    }

    /// <summary>
    /// The avatar's VisualParams as the simulator sends them, decoded through the parameter table exactly as
    /// BakeOrchestrator.Resolve does. Wearables' own stored values still win; this fills in what none of them
    /// carries — which for a worn-but-assetless slot is everything (Docs/MORPH-MASK-PASS.md §2.4).
    /// </summary>
    private static Dictionary<int, float> DecodeVisualParams(List<int>? bytes)
    {
        var overlay = new Dictionary<int, float>();
        if (bytes is null || bytes.Count == 0) return overlay;
        var list = VisualParamEncoder.SendList(new TexLayerCompositor().Lad);
        if (bytes.Count != list.Count) return overlay;
        for (var i = 0; i < list.Count; i++)
            overlay[list[i].Id] = list[i].Min + bytes[i] / 255f * (list[i].Max - list[i].Min);
        return overlay;
    }

    /// <summary>Bakes the set at one size and compares every channel the manifest has a reference for.</summary>
    private static (IReadOnlyList<BakeResult> Results, List<Row> Rows, List<string> Failures) Compare(SetContext c, int size)
    {
        var request = new BakeRequest(c.Wearables, c.VisualParams, c.Textures, size);
        var results = new SkiaBakeBackend().Bake(request);
        var rows = new List<Row>();
        var failures = new List<string>();

        foreach (var (key, ch) in ChannelKeys)
        {
            if (!c.Manifest.Goldens.TryGetValue(key, out var goldenId)) continue;
            var ours = results.SingleOrDefault(r => r.Channel == ch) ?? throw new InvalidOperationException($"the library produced no {ch} bake");
            var mine = J2kCodec.Decode(ours.J2kBytes);
            var golden = J2kCodec.Decode(File.ReadAllBytes(Path.Combine(c.Fixtures, $"{goldenId}.j2c")));
            var a = mine.Resample(size, size);
            var b = golden.Resample(size, size);

            long sumRgb = 0, sumA = 0, over8 = 0;
            var n = size * size;
            for (var i = 0; i < n; i++)
            {
                int dr = Math.Abs(a.R[i] - b.R[i]), dg = Math.Abs(a.G[i] - b.G[i]), db = Math.Abs(a.B[i] - b.B[i]);
                sumRgb += dr + dg + db;
                sumA += Math.Abs(a.A[i] - b.A[i]);
                if (dr > 8 || dg > 8 || db > 8) over8++;
            }
            var meanRgb = sumRgb / (3.0 * n);
            var meanA = sumA / (double)n;
            var pct = 100.0 * over8 / n;

            // A channel whose reference alpha is entirely zero (a fully transparent bake, e.g. a bald hair) has no
            // visible RGB, so its two RGB assertions are skipped and the row says so; alpha is still asserted.
            var refAlphaAllZero = true;
            for (var i = 0; i < n && refAlphaAllZero; i++) if (b.A[i] > 2) refAlphaAllZero = false;
            if (!refAlphaAllZero && meanRgb > 4.0) failures.Add($"{key}: mean |dRGB| {meanRgb:F2} > 4.0");
            if (!refAlphaAllZero && pct > 5.0) failures.Add($"{key}: {pct:F2}% pixels |dRGB| > 8 exceeds 5%");
            if (meanA > 2.0) failures.Add($"{key}: mean |dA| {meanA:F2} > 2.0");

            // the 5th component: ours (always present) against the reference's (present on every viewer bake)
            if (a.Mask is null) throw new InvalidOperationException($"our {ch} bake has no 5th component");
            if (b.Mask is null) throw new InvalidOperationException($"reference {ch} bake {goldenId} has no 5th component");
            long sumM = 0, overM = 0; int refMin = 255, refMax = 0;
            for (var i = 0; i < n; i++)
            {
                var d = Math.Abs(a.Mask[i] - b.Mask[i]);
                sumM += d; if (d > 8) overM++;
                if (b.Mask[i] < refMin) refMin = b.Mask[i]; if (b.Mask[i] > refMax) refMax = b.Mask[i];
            }
            var meanM = sumM / (double)n;
            var pctM = 100.0 * overM / n;
            var uniform = refMax - refMin <= 2;   // a flat reference mask (no morph-mask layer worn): lossy coding jitters it by a level or two
            if (meanM > 4.0) failures.Add($"{key}: mean |dM| {meanM:F2} > 4.0");
            if (!uniform && pctM > 5.0) failures.Add($"{key}: {pctM:F2}% pixels |dM| > 8 exceeds 5%");

            rows.Add(new Row(key, meanRgb, meanA, pct, meanM, pctM, uniform ? $"uniform({refMin})" : $"{refMin}..{refMax}",
                refAlphaAllZero, mine.W, mine.H, golden.W, golden.H, golden.HasAlpha, ours.J2kBytes.Length, goldenId));
        }
        return (results, rows, failures);
    }

    private static void Header(StringBuilder report, SetContext c, int size)
    {
        var m = c.Manifest;
        report.AppendLine($"reference-bake run {DateTimeOffset.Now:O}  set={c.Name}  avatar={m.Avatar}  outfit={m.Outfit}  reference=LL compositor  captured={m.Captured} via {m.CapturedVia ?? "?"}  size={size}");
        report.AppendLine($"wearables: {string.Join(", ", c.Parsed.Select(p => $"{p.Kind}('{p.Name}', {p.Params.Count}p, {p.Textures.Count(t => t.Value != UUID.Zero && t.Value != BakeConstants.DefaultAvatarTexture)}t)"))}");
        if (c.NullSlots.Count > 0) report.AppendLine($"worn but assetless (contributes as an instance, no textures): {string.Join(", ", c.NullSlots)}");
        report.AppendLine($"visual params overlaid: {c.VisualParams.Count}");
        report.AppendLine($"textures supplied: {c.Textures.Count}");
    }

    private static void Table(StringBuilder report, List<Row> rows)
    {
        report.AppendLine("channel  meanAbsRGB  meanAbsA  pctRGB>8   meanAbsM  pctM>8  M-ref        ours(WxH)   reference(WxH,alpha)   bytes  reference-uuid");
        foreach (var r in rows)
            report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-8} {1,10:F2} {2,9:F2} {3,9:F2}% {4,10:F2} {5,7:F2}% {6,-12} {7,-11} {8,-22} {9,6} {10}{11}",
                r.Key, r.MeanRgb, r.MeanA, r.PctRgb, r.MeanM, r.PctM, r.MRef, $"{r.OurW}x{r.OurH}",
                $"{r.RefW}x{r.RefH},{(r.RefAlpha ? "a" : "-")}", r.Bytes, r.GoldenId,
                r.RefAlphaAllZero ? " [RGB assertions skipped: reference alpha is entirely zero]" : ""));
    }

    // ------------------------------------------------------------------ the threshold gate

    [Theory]
    [MemberData(nameof(Sets))]
    public void reference_set_versus_library_bakes(string set)
    {
        var c = Load(set, out var reason);
        if (c is null) { _out.WriteLine(reason!); return; }

        var size = c.Manifest.BakeSize;
        var (results, rows, failures) = Compare(c, size);

        var report = new StringBuilder();
        Header(report, c, size);
        report.AppendLine($"channels baked: {string.Join(", ", results.Select(r => r.Channel))}");
        var produced = results.Select(r => r.Channel).ToHashSet();
        report.AppendLine($"channels not produced (nothing worn feeds them): {string.Join(", ", Enum.GetValues<BakeChannel>().Where(ch => !produced.Contains(ch)))}");
        var refusals = results.FirstOrDefault()?.Fidelity.Refusals ?? Array.Empty<string>();
        report.AppendLine($"fidelity refusals: {(refusals.Count == 0 ? "none" : string.Join("; ", refusals))}");
        report.AppendLine();
        Table(report, rows);
        report.AppendLine();

        // the full per-layer decision log, every channel: this is the fidelity surface (S1b Part 3)
        foreach (var r in results)
        {
            report.AppendLine($"[{r.Channel}] hash={r.InputHash[..16]} bytes={r.J2kBytes.Length} missingTextures=[{string.Join(", ", r.Fidelity.MissingTextures)}] unsupportedLayers=[{string.Join(" | ", r.Fidelity.UnsupportedLayers)}]");
            foreach (var line in r.Fidelity.Notes) report.AppendLine($"    {line}");
        }

        if (failures.Count > 0) report.AppendLine($"threshold assertions FAILED: {string.Join("; ", failures)}");
        var text = report.ToString();
        File.WriteAllText(Path.Combine(GoldenDir(), $"last-run-{set}.txt"), text);
        _out.WriteLine(text);

        Assert.Equal(c.Manifest.Goldens.Count, rows.Count);
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // ------------------------------------------------------------------ S1b Part 2: bake size

    [Theory]
    [MemberData(nameof(Sets))]
    public void bake_size_sweep(string set)
    {
        var c = Load(set, out var reason);
        if (c is null) { _out.WriteLine(reason!); return; }

        var report = new StringBuilder();
        Header(report, c, 0);
        report.AppendLine("Reported per size: the same comparison, both images resampled to that size before differencing,");
        report.AppendLine("plus the encoded byte size of our bake. Evidence for ADR-008's default; asserts nothing about it.");

        var bytesBySize = new Dictionary<int, Dictionary<string, int>>();
        foreach (var size in new[] { 512, 1024, 2048 })
        {
            var (results, rows, failures) = Compare(c, size);
            report.AppendLine();
            report.AppendLine($"--- {c.Name} at {size} ---");
            Table(report, rows);
            report.AppendLine(failures.Count == 0
                ? "thresholds: all pass at this size"
                : $"thresholds at this size: {string.Join("; ", failures)}");
            bytesBySize[size] = rows.ToDictionary(r => r.Key, r => r.Bytes);

            foreach (var r in results)
            {
                var mine = J2kCodec.Decode(r.J2kBytes);
                Assert.Equal(size, mine.W);
                Assert.Equal(size, mine.H);
            }
        }

        report.AppendLine();
        report.AppendLine("encoded bytes per channel per size (our bake)");
        report.AppendLine("channel        512       1024       2048   1024/512  2048/1024");
        foreach (var key in bytesBySize[512].Keys)
        {
            double b512 = bytesBySize[512][key], b1024 = bytesBySize[1024][key], b2048 = bytesBySize[2048][key];
            report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-8} {1,10} {2,10} {3,10} {4,10:F2}x {5,9:F2}x",
                key, (int)b512, (int)b1024, (int)b2048, b1024 / b512, b2048 / b1024));
        }
        var totals = new[] { 512, 1024, 2048 }.Select(s => bytesBySize[s].Values.Sum()).ToArray();
        report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-8} {1,10} {2,10} {3,10}", "TOTAL", totals[0], totals[1], totals[2]));

        var text = report.ToString();
        File.WriteAllText(Path.Combine(GoldenDir(), $"last-run-{set}-sizes.txt"), text);
        _out.WriteLine(text);
    }
}
