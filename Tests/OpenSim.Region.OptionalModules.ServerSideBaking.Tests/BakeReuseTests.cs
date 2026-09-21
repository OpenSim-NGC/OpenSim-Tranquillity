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
/// S2 Part 1: persistence in the avatar service's key/value table and the input-hash skip (ADR-004). No Scene, no
/// SceneHelpers — a fake asset service, a fake avatar service, and Truly Bazar's golden fixtures as the outfit.
/// Every test skips (vacuously, with a console line) when the fixtures have not been fetched.
/// </summary>
public class BakeReuseTests
{
    private readonly ITestOutputHelper _out;
    public BakeReuseTests(ITestOutputHelper output) { _out = output; }

    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");
    private const int Size = 128;          // small and fast; the fidelity of the pixels is the golden harness's job
    private const int CofVersion = 42;

    private static string FixtureDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Source", "OpenSimNGC.Appearance.Baking.Tests", "Golden", "truly-stock", "fixtures"));

    private const string SkipNote = "SKIPPED: golden fixtures not fetched (Source/OpenSimNGC.Appearance.Baking.Tests/Golden/truly-stock/fixtures)";
    private static bool FixturesPresent => File.Exists(Path.Combine(FixtureDir(), "avatar.json"));

    /// <summary>A backend that counts the calls it gets and the channels each one asked for.</summary>
    private sealed class CountingBackend : IBakeBackend
    {
        private readonly IBakeBackend m_inner;
        public CountingBackend(IBakeBackend inner) { m_inner = inner; }
        public int Calls;
        public readonly List<BakeChannel> Composited = new();
        public Task<IReadOnlyList<BakeResult>> BakeAsync(BakeRequest r, CancellationToken ct)
        {
            Calls++;
            var task = m_inner.BakeAsync(r, ct);
            Composited.AddRange(task.GetAwaiter().GetResult().Select(x => x.Channel));
            return task;
        }
    }

    private sealed class Rig
    {
        public required FakeAssetService Assets;
        public required FakeAvatarService Avatars;
        public required AvatarWearable[] Wearables;
        public required byte[] VisualParams;
        public required TexLayerCompositor Compositor;
        public AvatarAppearance Appearance = new();

        public (BakeOutcome Outcome, CountingBackend Backend) Bake(int size = Size, int cof = CofVersion)
        {
            var backend = new CountingBackend(new SkiaBakeBackend(Compositor) { Quality = 0.5 });
            var outcome = BakeOrchestrator.Run(Agent, BakeReason.Console, Wearables, VisualParams, Appearance,
                Assets, Avatars, backend, Compositor, size, cof, CancellationToken.None);
            return (outcome, backend);
        }

        public UUID Face(BakeChannel ch) => Appearance.Texture.FaceTextures[BakeOrchestrator.FaceOf(ch)]?.TextureID ?? UUID.Zero;
        public Dictionary<string, string> Keys => Avatars.Records.TryGetValue(Agent, out var d) ? d : new Dictionary<string, string>();
    }

    private static Rig Load()
    {
        var dir = FixtureDir();
        var assets = new FakeAssetService();
        foreach (var f in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(f);
            sbyte type = ext switch { ".bodypart" => (sbyte)AssetType.Bodypart, ".clothing" => (sbyte)AssetType.Clothing, ".j2c" => (sbyte)AssetType.Texture, _ => -1 };
            if (type < 0) continue;
            var id = Path.GetFileNameWithoutExtension(f);
            assets.Put(new AssetBase(new UUID(id), id, type, Agent.ToString()) { Data = File.ReadAllBytes(f) });
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "avatar.json")));
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();
        foreach (var w in doc.RootElement.GetProperty("wearables").EnumerateArray())
            wearables[w.GetProperty("type").GetInt32()].Add(new UUID(w.GetProperty("itemId").GetString()), new UUID(w.GetProperty("assetId").GetString()));
        var vp = doc.RootElement.GetProperty("visualParams").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        return new Rig { Assets = assets, Avatars = new FakeAvatarService(), Wearables = wearables, VisualParams = vp, Compositor = new TexLayerCompositor() };
    }

    /// <summary>The channels a bake of Truly's stock outfit produces.</summary>
    private static readonly BakeChannel[] Live = { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair };

    /// <summary>
    /// Replace one worn wearable with a different asset that differs only in one stored parameter value. Same
    /// wearable type, same textures — the point is that the input hash of the channels that type feeds changes
    /// and no other channel's does.
    /// </summary>
    private static UUID MutateWearable(Rig rig, WearableType type)
    {
        var slot = rig.Wearables[(int)type];
        var oldId = slot[0].AssetID;
        var text = Encoding.UTF8.GetString(rig.Assets.Assets[oldId.ToString()].Data);
        var parsed = WearableParser.Parse(text);
        // the LLWearable body writes one "<id> <value>" line per parameter, unindented (see any .clothing fixture)
        var pid = parsed.Params.Keys.OrderBy(k => k).First();
        var idx = text.IndexOf($"\n{pid} ", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"could not find parameter {pid} in the {type} wearable to change");
        var end = text.IndexOf('\n', idx + 1);
        var mutated = text[..idx] + $"\n{pid} 0.123456" + text[end..];
        Assert.NotEqual(text, mutated);
        Assert.Equal(0.123456f, WearableParser.Parse(mutated).Params[pid], 5);

        var newId = UUID.Random();
        rig.Assets.Put(new AssetBase(newId, newId.ToString(), (sbyte)AssetType.Clothing, Agent.ToString()) { Data = Encoding.UTF8.GetBytes(mutated) });
        rig.Wearables[(int)type] = new AvatarWearable();
        rig.Wearables[(int)type].Add(slot[0].ItemID, newId);
        return newId;
    }

    // ------------------------------------------------------------------ 1. the index is written

    [Fact]
    public void FirstBake_WritesTheAdr004KeysThroughTheOrdinaryAvatarService()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();

        var (outcome, _) = rig.Bake();

        Assert.Equal(5, outcome.Count(ChannelStatus.Baked));
        Assert.True(outcome.IndexWritten);
        Assert.Equal(1, rig.Avatars.SetItemsCalls);       // one batched SetItems, not one call per key

        var keys = rig.Keys;
        foreach (var ch in Live)
        {
            var stored = outcome.Channels.Single(c => c.Channel == ch);
            Assert.Equal(stored.AssetId.ToString(), keys[$"Bake:{ch}"]);
            Assert.Equal(stored.InputHash, keys[$"BakeHash:{ch}"]);
            Assert.Equal(stored.AssetId, rig.Face(ch));
        }
        Assert.Equal(CofVersion.ToString(), keys["BakeCOFVersion"]);
        Assert.Equal(Size.ToString(), keys["BakeSize"]);
        Assert.True(DateTime.TryParse(keys["BakeUpdated"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var updated));
        Assert.True((DateTime.UtcNow - updated.ToUniversalTime()).TotalMinutes < 5, $"BakeUpdated {keys["BakeUpdated"]} should be now, in UTC");
        // no key for a channel the outfit never produced
        Assert.DoesNotContain("Bake:Skirt", keys.Keys);
        // every key fits the Avatars table's Name varchar(32)
        Assert.All(keys.Keys, k => Assert.True(k.Length <= 32, k));

        // it round-trips through the same reader the bake path uses
        var index = BakeIndex.Read(rig.Avatars, Agent);
        Assert.Equal(Size, index.Size);
        Assert.Equal(CofVersion, index.CofVersion);
        Assert.Equal(5, index.Bakes.Count);
    }

    // ------------------------------------------------------------------ 2. unchanged inputs -> all Reused, zero composites

    [Fact]
    public void UnchangedInputs_ReuseEveryChannelAndCompositeNothing()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();
        var (first, _) = rig.Bake();
        var firstIds = Live.ToDictionary(ch => ch, ch => first.Channels.Single(c => c.Channel == ch).AssetId);
        rig.Assets.ResetOps();

        var (second, backend) = rig.Bake();

        Assert.Equal(5, second.Count(ChannelStatus.Reused));
        Assert.Equal(0, second.Count(ChannelStatus.Baked));
        Assert.Equal(0, second.Count(ChannelStatus.Failed));
        // the backend was never even called: with nothing to bake there is no request to make
        Assert.Equal(0, backend.Calls);
        Assert.Empty(backend.Composited);
        // nothing was stored and nothing was deleted
        Assert.Empty(rig.Assets.Stored);
        Assert.DoesNotContain(rig.Assets.Ops, o => o.StartsWith("delete "));
        Assert.Empty(second.Superseded);
        // not one texture was fetched — only the seven wearable assets, which the hash needs
        var fetched = rig.Assets.Fetched.ToList();
        var wearableIds = rig.Wearables.SelectMany(w => Enumerable.Range(0, w.Count).Select(i => w[i].AssetID))
                                       .Where(id => !id.IsZero()).Select(id => id.ToString()).ToHashSet();
        Assert.All(fetched, f => Assert.Contains(f, wearableIds));
        // the faces still point at the same assets and the index still resolves
        foreach (var ch in Live)
        {
            Assert.Equal(firstIds[ch], second.Channels.Single(c => c.Channel == ch).AssetId);
            Assert.Equal(firstIds[ch], rig.Face(ch));
            Assert.NotNull(rig.Assets.GetUnchecked(firstIds[ch].ToString()));
        }
        _out.WriteLine($"second run: {second.Count(ChannelStatus.Reused)} reused, {backend.Calls} backend calls, "
                     + $"{fetched.Count} asset fetches (all wearables), {rig.Assets.Stored.Count} stores");
    }

    // ------------------------------------------------------------------ 3. one wearable changed -> only its channels re-bake

    /// <summary>
    /// Truly wears an Undershirt. <c>avatar_lad.xml</c> gives the <c>upper_undershirt</c> local texture to the
    /// <c>upper_body</c> layer set and to no other, so <see cref="TexLayerCompositor.WearableOf"/> maps that slot
    /// to <see cref="WearableKind.Undershirt"/> and <c>Upper</c> is the only channel the type feeds. Changing the
    /// undershirt must therefore re-bake Upper and reuse Head, Lower, Eyes and Hair — the whole point of hashing
    /// per channel rather than per outfit.
    /// </summary>
    [Fact]
    public void OneWearableChanged_RebakesOnlyTheChannelsThatWearableTypeFeeds()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();
        var (first, _) = rig.Bake();
        var firstIds = Live.ToDictionary(ch => ch, ch => first.Channels.Single(c => c.Channel == ch).AssetId);

        var expected = BakeOrchestrator.ChannelsFedBy(WearableKind.Undershirt, rig.Compositor).ToHashSet();
        Assert.Equal(new HashSet<BakeChannel> { BakeChannel.Upper }, expected);   // stated, not merely derived

        MutateWearable(rig, WearableType.Undershirt);
        rig.Assets.ResetOps();

        var (second, backend) = rig.Bake();

        Assert.Equal(1, backend.Calls);
        Assert.Equal(new[] { BakeChannel.Upper }, backend.Composited);
        Assert.Equal(ChannelStatus.Baked, second.Channels.Single(c => c.Channel == BakeChannel.Upper).Status);
        foreach (var ch in Live.Where(c => c != BakeChannel.Upper))
        {
            Assert.Equal(ChannelStatus.Reused, second.Channels.Single(c => c.Channel == ch).Status);
            Assert.Equal(firstIds[ch], rig.Face(ch));
        }
        Assert.NotEqual(firstIds[BakeChannel.Upper], rig.Face(BakeChannel.Upper));
        Assert.Single(rig.Assets.Stored);
        // and the index now carries the new Upper and the old everything-else
        var keys = rig.Keys;
        Assert.Equal(rig.Face(BakeChannel.Upper).ToString(), keys["Bake:Upper"]);
        Assert.Equal(firstIds[BakeChannel.Head].ToString(), keys["Bake:Head"]);
    }

    // ------------------------------------------------------------------ 4. a stored hash whose asset is gone

    [Fact]
    public void StoredHashWhoseAssetHasVanished_IsNotTrustedAndTheChannelRebakes()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();
        var (first, _) = rig.Bake();
        var headId = first.Channels.Single(c => c.Channel == BakeChannel.Head).AssetId;

        // the asset goes; the index still claims it, hash and all
        Assert.True(rig.Assets.Remove(headId.ToString()));
        Assert.Equal(headId.ToString(), rig.Keys["Bake:Head"]);
        rig.Assets.ResetOps();

        var (second, backend) = rig.Bake();

        Assert.Equal(new[] { BakeChannel.Head }, backend.Composited);
        var head = second.Channels.Single(c => c.Channel == BakeChannel.Head);
        Assert.Equal(ChannelStatus.Baked, head.Status);
        Assert.NotEqual(headId, head.AssetId);
        Assert.Equal(head.AssetId, rig.Face(BakeChannel.Head));
        Assert.Equal(4, second.Count(ChannelStatus.Reused));
        Assert.Contains(second.Notes, n => n.Contains("vanished"));
        // superseding an asset that is already gone deletes nothing
        Assert.Empty(second.Superseded);
    }

    // ------------------------------------------------------------------ 5. a BakeSize change invalidates everything

    [Fact]
    public void BakeSizeChange_RebakesEveryChannel()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();
        var (first, _) = rig.Bake(size: 128);
        var firstIds = Live.ToDictionary(ch => ch, ch => first.Channels.Single(c => c.Channel == ch).AssetId);
        rig.Assets.ResetOps();

        var (second, backend) = rig.Bake(size: 256);

        Assert.Equal(5, second.Count(ChannelStatus.Baked));
        Assert.Equal(0, second.Count(ChannelStatus.Reused));
        Assert.Equal(Live.OrderBy(c => c), backend.Composited.OrderBy(c => c));
        Assert.Equal("256", rig.Keys["BakeSize"]);
        foreach (var ch in Live)
        {
            Assert.NotEqual(firstIds[ch], rig.Face(ch));
            // the hash itself changed, not only the BakeSize key: size is inside BakeHash
            Assert.NotEqual(first.Channels.Single(c => c.Channel == ch).InputHash, rig.Keys[$"BakeHash:{ch}"]);
        }
        // every one of the five old assets was superseded
        Assert.Equal(5, second.Superseded.Count);
        Assert.All(firstIds.Values, id => Assert.Contains(id, second.Superseded));
    }

    // ------------------------------------------------------------------ 6. supersede ordering

    [Fact]
    public void Supersede_DeletesThePreviousAssetAndOnlyAfterTheNewOneIsStored()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();
        var (first, _) = rig.Bake();
        var oldUpper = first.Channels.Single(c => c.Channel == BakeChannel.Upper).AssetId;
        MutateWearable(rig, WearableType.Undershirt);
        rig.Assets.ResetOps();

        var (second, _) = rig.Bake();

        var newUpper = second.Channels.Single(c => c.Channel == BakeChannel.Upper).AssetId;
        Assert.NotEqual(oldUpper, newUpper);
        Assert.Equal(new[] { oldUpper }, second.Superseded);
        Assert.Null(rig.Assets.GetUnchecked(oldUpper.ToString()));
        Assert.NotNull(rig.Assets.GetUnchecked(newUpper.ToString()));

        // the order on the asset service: the new bake is stored, then the old one is deleted. Never the reverse,
        // or a failing store would leave the avatar with a face pointing at nothing.
        var store = rig.Assets.Ops.IndexOf("store " + newUpper);
        var delete = rig.Assets.Ops.IndexOf("delete " + oldUpper);
        Assert.True(store >= 0 && delete >= 0, string.Join(" | ", rig.Assets.Ops));
        Assert.True(store < delete, $"store must precede delete: {string.Join(" | ", rig.Assets.Ops)}");

        // and nothing a face still points at was touched: the four reused channels keep their assets
        foreach (var ch in Live.Where(c => c != BakeChannel.Upper))
            Assert.NotNull(rig.Assets.GetUnchecked(rig.Face(ch).ToString()));
    }

    /// <summary>
    /// The supersede rule is "never delete something a face still points at", and it is a rule about the faces,
    /// not about bookkeeping: an index that names an asset which some other channel's face is using must not have
    /// that asset deleted out from under it.
    /// </summary>
    [Fact]
    public void Supersede_RefusesToDeleteAnAssetAnotherFaceStillPointsAt()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();
        var (first, _) = rig.Bake();
        var oldUpper = first.Channels.Single(c => c.Channel == BakeChannel.Upper).AssetId;

        // contrive the collision: some other channel's face is pointing at Upper's old bake
        rig.Appearance.Texture.CreateFace((uint)BakeOrchestrator.FaceOf(BakeChannel.Skirt)).TextureID = oldUpper;
        MutateWearable(rig, WearableType.Undershirt);
        rig.Assets.ResetOps();

        var (second, _) = rig.Bake();

        Assert.Equal(ChannelStatus.Baked, second.Channels.Single(c => c.Channel == BakeChannel.Upper).Status);
        Assert.Empty(second.Superseded);
        Assert.NotNull(rig.Assets.GetUnchecked(oldUpper.ToString()));
        Assert.DoesNotContain(rig.Assets.Ops, o => o == "delete " + oldUpper);
    }

    // ------------------------------------------------------------------ 7. the hazard this index lives with

    /// <summary>
    /// Pins the behaviour behind <see cref="BakeIndex"/>'s hazard note. <c>AvatarService.SetAvatar</c> deletes
    /// every row for the agent before rewriting the appearance-derived keys (AvatarService.cs:93), so any
    /// appearance save destroys the bake index. The consequence must be a re-bake and never a broken face: the
    /// index can be wholly absent, never half-right.
    /// </summary>
    [Fact]
    public void AnAppearanceSaveWipesTheIndex_AndTheNextBakeSimplyRebakes()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();
        var (first, _) = rig.Bake();
        Assert.Equal(5, first.Count(ChannelStatus.Baked));

        rig.Avatars.SetAppearance(Agent, new AvatarAppearance());

        Assert.Empty(BakeIndex.Read(rig.Avatars, Agent).Bakes);
        var (second, backend) = rig.Bake();
        Assert.Equal(5, second.Count(ChannelStatus.Baked));
        Assert.Equal(0, second.Count(ChannelStatus.Reused));
        Assert.Equal(5, backend.Composited.Count);
        // the old assets are orphaned, not deleted: with the index gone, supersede has nothing to go on
        Assert.Empty(second.Superseded);
        foreach (var ch in Live)
            Assert.NotNull(rig.Assets.GetUnchecked(first.Channels.Single(c => c.Channel == ch).AssetId.ToString()));
    }

    // ------------------------------------------------------------------ 8. no avatar service at all

    [Fact]
    public void WithNoAvatarService_EverythingBakesAndNothingIsPersisted()
    {
        if (!FixturesPresent) { Console.WriteLine(SkipNote); return; }
        var rig = Load();
        var appearance = new AvatarAppearance();

        var outcome = BakeOrchestrator.Run(Agent, BakeReason.Console, rig.Wearables, rig.VisualParams, appearance,
            rig.Assets, null, new SkiaBakeBackend(rig.Compositor) { Quality = 0.5 }, rig.Compositor, Size, 0, CancellationToken.None);

        Assert.Equal(5, outcome.Count(ChannelStatus.Baked));
        Assert.Equal(0, outcome.Count(ChannelStatus.Reused));
        Assert.False(outcome.IndexWritten);
        Assert.Empty(rig.Avatars.Records);
    }
}
