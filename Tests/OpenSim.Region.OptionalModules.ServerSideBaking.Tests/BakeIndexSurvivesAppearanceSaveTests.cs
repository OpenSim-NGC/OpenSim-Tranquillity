using System.Runtime.CompilerServices;
using System.Text.Json;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.AvatarService;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S3 Part 0 — Ledger Q-14. Against the <b>real</b> <see cref="AvatarService"/>, not a fake of it: an appearance
/// save must leave the ADR-004 bake index alone, and a bake after a save must still reuse.
/// </summary>
public class BakeIndexSurvivesAppearanceSaveTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    /// <summary>
    /// An in-memory <see cref="IAvatarData"/> shaped like the real <c>Avatars</c> table: one row per
    /// (PrincipalID, Name), <see cref="Store"/> an upsert on that pair, <c>Delete("PrincipalID", id)</c> removing
    /// every row for the principal. <see cref="OpenSim.Data.Null.NullAvatarData"/> cannot be used here — it keeps
    /// one object per principal and its <c>Store</c> overwrites the whole thing, so it loses every key but the
    /// last, which is not how MySQL/PGSQL/SQLite behave (<c>MySQLGenericTableHandler.Store</c> is a REPLACE INTO
    /// on the primary key <c>(PrincipalID, Name)</c>).
    /// </summary>
    private sealed class RowTable : IAvatarData
    {
        public readonly Dictionary<(UUID, string), string> Rows = new();
        public int DeleteAllCalls;

        public AvatarBaseData[] Get(string field, string val)
        {
            if (field != "PrincipalID" || !UUID.TryParse(val, out var id)) return Array.Empty<AvatarBaseData>();
            return Rows.Where(r => r.Key.Item1 == id)
                       .Select(r => new AvatarBaseData
                       {
                           PrincipalID = id,
                           Data = new Dictionary<string, string> { ["Name"] = r.Key.Item2, ["Value"] = r.Value },
                       })
                       .ToArray();
        }

        public bool Store(AvatarBaseData data)
        {
            Rows[(data.PrincipalID, data.Data["Name"])] = data.Data["Value"];
            return true;
        }

        public bool Delete(UUID principalID, string name) => Rows.Remove((principalID, name));

        public bool Delete(string field, string val)
        {
            if (field != "PrincipalID" || !UUID.TryParse(val, out var id)) return false;
            DeleteAllCalls++;
            foreach (var k in Rows.Keys.Where(k => k.Item1 == id).ToList()) Rows.Remove(k);
            return true;
        }

        public Dictionary<string, string> KeysOf(UUID id)
            => Rows.Where(r => r.Key.Item1 == id).ToDictionary(r => r.Key.Item2, r => r.Value);
    }

    /// <summary>The real service over the row table, through the S3 test seam.</summary>
    private sealed class TestableAvatarService : AvatarService
    {
        public TestableAvatarService(IAvatarData db) : base(new IniConfigSource(), db) { }
    }

    private static AvatarAppearance AppearanceWearing(params (WearableType Type, UUID Item, UUID Asset)[] worn)
    {
        var a = new AvatarAppearance();
        a.ClearWearables();
        foreach (var (type, item, asset) in worn) a.Wearables[(int)type].Add(item, asset);
        a.SetAttachment(5, UUID.Random(), UUID.Random());
        return a;
    }

    // ------------------------------------------------------------------ 1. the delete is still load-bearing

    /// <summary>
    /// The reason <c>SetAvatar</c> deletes everything first, pinned so the Q-14 fix cannot be "just stop
    /// deleting". Take a shirt off and its <c>Wearable</c> row must go with it; leave the row and
    /// <c>ToAvatarAppearance</c> puts the shirt back on, because it reads those keys additively.
    /// </summary>
    [Fact]
    public void AnAppearanceSaveStillDropsTheKeysTheNewAppearanceNoLongerHas()
    {
        var db = new RowTable();
        var svc = new TestableAvatarService(db);
        var shirtItem = UUID.Random();

        Assert.True(svc.SetAppearance(Agent, AppearanceWearing((WearableType.Shirt, shirtItem, UUID.Random()))));
        Assert.Contains(db.KeysOf(Agent).Keys, k => k.StartsWith("Wearable 4:"));

        // shirt off
        Assert.True(svc.SetAppearance(Agent, AppearanceWearing()));

        Assert.DoesNotContain(db.KeysOf(Agent).Keys, k => k.StartsWith("Wearable 4:"));
        var shirtSlot = svc.GetAppearance(Agent).Wearables[(int)WearableType.Shirt];
        for (var j = 0; j < shirtSlot.Count; j++)
            Assert.NotEqual(shirtItem, shirtSlot[j].ItemID);
    }

    // ------------------------------------------------------------------ 2. the index survives

    [Fact]
    public void AnAppearanceSaveLeavesTheBakeIndexIntact()
    {
        var db = new RowTable();
        var svc = new TestableAvatarService(db);
        var head = new StoredBake(UUID.Random(), new string('a', 64));
        var upper = new StoredBake(UUID.Random(), new string('b', 64));

        Assert.True(BakeIndex.Write(svc, Agent,
            new[]
            {
                new KeyValuePair<BakeChannel, StoredBake>(BakeChannel.Head, head),
                new KeyValuePair<BakeChannel, StoredBake>(BakeChannel.Upper, upper),
            },
            cofVersion: 42, bakeSize: 1024, updatedUtc: DateTime.UtcNow));

        var before = BakeIndex.Read(svc, Agent);
        Assert.Equal(2, before.Bakes.Count);

        // an ordinary appearance save, exactly as AvatarFactoryModule.SaveAppearance does it
        Assert.True(svc.SetAppearance(Agent, AppearanceWearing((WearableType.Shirt, UUID.Random(), UUID.Random()))));

        var after = BakeIndex.Read(svc, Agent);
        Assert.Equal(1024, after.Size);
        Assert.Equal(42, after.CofVersion);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal(2, after.Bakes.Count);
        Assert.Equal(head, after.Bakes[BakeChannel.Head]);
        Assert.Equal(upper, after.Bakes[BakeChannel.Upper]);

        // and the appearance itself really was rewritten, so this is not passing by doing nothing
        Assert.Equal(1, db.DeleteAllCalls);
        Assert.Contains(db.KeysOf(Agent).Keys, k => k.StartsWith("Wearable 4:"));
    }

    /// <summary>
    /// Every appearance key the avatar service writes or reads must fall outside the preserved namespace,
    /// otherwise preserving it would resurrect appearance the delete was there to remove.
    /// </summary>
    [Fact]
    public void NoAppearanceKeyIsCaughtByThePreservedPrefix()
    {
        var db = new RowTable();
        var svc = new TestableAvatarService(db);
        Assert.True(svc.SetAppearance(Agent, AppearanceWearing((WearableType.Shirt, UUID.Random(), UUID.Random()))));

        Assert.NotEmpty(db.KeysOf(Agent));
        Assert.All(db.KeysOf(Agent).Keys, k => Assert.False(AvatarDataKeys.IsPreserved(k), k));

        // the legacy names ToAvatarAppearance also reads, which are never written by AvatarData(AvatarAppearance)
        foreach (var legacy in new[] { "BodyItem", "BodyAsset", "SkinItem", "ShirtItem", "PantsAsset", "SkirtItem", "AvatarType", "Serial", "AvatarHeight", "VisualParams" })
            Assert.False(AvatarDataKeys.IsPreserved(legacy), legacy);

        // and every key the bake index writes must be inside it
        foreach (var ch in Enum.GetValues<BakeChannel>())
        {
            Assert.True(AvatarDataKeys.IsPreserved(BakeIndex.BakeKey(ch)), BakeIndex.BakeKey(ch));
            Assert.True(AvatarDataKeys.IsPreserved(BakeIndex.HashKey(ch)), BakeIndex.HashKey(ch));
        }
        foreach (var k in new[] { BakeIndex.CofVersionKey, BakeIndex.SizeKey, BakeIndex.UpdatedKey })
            Assert.True(AvatarDataKeys.IsPreserved(k), k);
    }

    /// <summary>A grid that has never baked must see exactly the old behaviour: one delete, nothing else.</summary>
    [Fact]
    public void WithNoBakeIndexTheSaveIsTheSameSingleDeleteItAlwaysWas()
    {
        var db = new RowTable();
        var svc = new TestableAvatarService(db);

        Assert.True(svc.SetAppearance(Agent, AppearanceWearing((WearableType.Pants, UUID.Random(), UUID.Random()))));
        Assert.True(svc.SetAppearance(Agent, AppearanceWearing()));

        Assert.Equal(2, db.DeleteAllCalls);
        Assert.All(db.KeysOf(Agent).Keys, k => Assert.False(AvatarDataKeys.IsPreserved(k)));
    }

    // ------------------------------------------------------------------ 3. a bake after a save still reuses

    private static string FixtureDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Source", "OpenSimNGC.Appearance.Baking.Tests", "Golden", "truly-stock", "fixtures"));

    private const string SkipNote = "SKIPPED: golden fixtures not fetched (Source/OpenSimNGC.Appearance.Baking.Tests/Golden/truly-stock/fixtures)";

    /// <summary>
    /// The whole point of Q-14, end to end: bake, let an appearance save run through the real service, bake again.
    /// Before the fix the second run recomposited all five channels. Now it reuses all five.
    /// </summary>
    [Fact]
    public void ABakeAfterAnAppearanceSaveStillReuses()
    {
        if (!File.Exists(Path.Combine(FixtureDir(), "avatar.json"))) { Console.WriteLine(SkipNote); return; }

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

        var compositor = new TexLayerCompositor();
        var svc = new TestableAvatarService(new RowTable());
        var appearance = new AvatarAppearance();

        BakeOutcome Bake() => BakeOrchestrator.Run(Agent, BakeReason.Console, wearables, vp, appearance, assets, svc,
            new SkiaBakeBackend(compositor) { Quality = 0.5 }, compositor, 128, 7, CancellationToken.None);

        var first = Bake();
        Assert.Equal(5, first.Count(ChannelStatus.Baked));
        Assert.True(first.IndexWritten);

        // the appearance save that used to destroy the index
        Assert.True(svc.SetAppearance(Agent, appearance));

        assets.ResetOps();   // Stored accumulates across a run; the first bake's five are not the claim here
        var second = Bake();
        Assert.Equal(5, second.Count(ChannelStatus.Reused));
        Assert.Equal(0, second.Count(ChannelStatus.Baked));
        Assert.Empty(assets.Stored);
        foreach (var ch in new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes, BakeChannel.Hair })
            Assert.Equal(first.Channels.Single(c => c.Channel == ch).AssetId,
                         second.Channels.Single(c => c.Channel == ch).AssetId);
    }
}
