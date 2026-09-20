using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSimNGC.Appearance.Baking;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S5 Part 1 — the change trigger. A rebake happens when the region has finished applying an outfit change
/// <b>and</b> persisted it, never on the arrival of the change (Ledger Q-16, Design Brief §4.6).
///
/// <para>
/// Both signals a change produces reach that point. The legacy route queues an appearance save in
/// <c>Client_OnAvatarNowWearing</c> (<c>AvatarFactoryModule.cs:1292</c>); the cap route queues one too rather
/// than baking on arrival. <c>SaveAppearance</c> then raises <c>OnAvatarAppearanceChange</c> immediately after
/// <c>SetAppearanceAssets</c> and <c>AvatarService.SetAppearance</c>, which is the one moment the wearables have
/// resolved asset ids and the result is stored.
/// </para>
/// </summary>
public class ChangeTriggerTests
{
    private readonly ITestOutputHelper _out;
    public ChangeTriggerTests(ITestOutputHelper output) { _out = output; }

    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");
    // An arbitrary fixed instant. Deliberately not a timestamp from any log: the spacings in these tests are
    // constructed, not observed (Ledger Q-6).
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ServerSideBakingRegion On(TimeSpan? debounce = null)
        => new(true, new CofHandshake()) { ChangeDebounce = debounce ?? TimeSpan.FromSeconds(2) };

    private static ServerSideBakingRegion Off()
        => new(false, new CofHandshake());

    // ------------------------------------------------------------------ one change, one bake

    [Fact]
    public void AChangeOnAnSsbRegionTriggersExactlyOneBake()
    {
        var region = On();

        Assert.True(region.TryClaimChangeBake(Agent, T0));

        // the same change producing a second signal claims nothing more
        Assert.False(region.TryClaimChangeBake(Agent, T0));
    }

    /// <summary>
    /// A single outfit change produces more than one signal, and a slam produces several. The exact spread is
    /// unmeasured (Ledger Q-6), so the window is sized against the 5 s save delay rather than against it;
    /// everything inside the window collapses into the one bake that was already claimed. The spacings below are
    /// illustrative, not observations.
    /// </summary>
    [Fact]
    public void ABurstOfSignalsCoalescesToOne()
    {
        var region = On(TimeSpan.FromSeconds(2));
        var claims = 0;

        // a pair close together, plus the extra signals a slam adds, all inside one second
        foreach (var ms in new[] { 0, 310, 420, 655, 980 })
            if (region.TryClaimChangeBake(Agent, T0.AddMilliseconds(ms))) claims++;

        Assert.Equal(1, claims);
        _out.WriteLine("5 signals spanning 980 ms -> 1 bake");
    }

    [Fact]
    public void ASeparateChangeAfterTheWindowBakesAgain()
    {
        var region = On(TimeSpan.FromSeconds(2));

        Assert.True(region.TryClaimChangeBake(Agent, T0));
        Assert.False(region.TryClaimChangeBake(Agent, T0.AddSeconds(1.9)));
        Assert.True(region.TryClaimChangeBake(Agent, T0.AddSeconds(2.1)));

        // and the window is shorter than DelayBeforeAppearanceSave (5 s), so a change that completed its own
        // save cycle is never suppressed
        Assert.True(On().ChangeDebounce < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TheWindowIsPerAgent()
    {
        var region = On();
        var other = UUID.Random();

        Assert.True(region.TryClaimChangeBake(Agent, T0));
        Assert.True(region.TryClaimChangeBake(other, T0));
        Assert.False(region.TryClaimChangeBake(Agent, T0));
        Assert.False(region.TryClaimChangeBake(other, T0));
    }

    [Fact]
    public void AnAgentThatLeavesForgetsItsWindow()
    {
        var region = On();

        Assert.True(region.TryClaimChangeBake(Agent, T0));
        Assert.False(region.TryClaimChangeBake(Agent, T0));

        region.Forget(Agent);
        Assert.True(region.TryClaimChangeBake(Agent, T0));
    }

    // ------------------------------------------------------------------ flag-off regions

    [Fact]
    public void AFlagOffRegionTriggersNoBakeAtAll()
    {
        var region = Off();

        foreach (var ms in new[] { 0, 310, 5000, 60000 })
            Assert.False(region.TryClaimChangeBake(Agent, T0.AddMilliseconds(ms)));

        Assert.False(region.ServerSideBakingEnabled);
    }

    // ------------------------------------------------------------------ the send happens even with nothing recomputed

    private static string FixtureDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Source", "OpenSimNGC.Appearance.Baking.Tests", "Golden", "truly-stock", "fixtures"));

    private const string SkipNote = "SKIPPED: golden fixtures not fetched";

    /// <summary>
    /// The case the trigger exists for and the one most easily got wrong: an outfit change whose channels all
    /// hash the same as the stored bakes. Nothing is recomputed, nothing is stored — and the appearance must
    /// still go out, because the viewer is waiting for an <c>AvatarAppearance</c> it can accept and will not
    /// re-request one. <c>BakeAsync</c> sends when Baked + Reused &gt; 0, so Reused alone is enough; this asserts
    /// that condition against a real all-reused outcome from the orchestrator.
    /// </summary>
    [Fact]
    public void AChangeWhoseHashesAllMatchStillSendsTheAppearance()
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
        var avatars = new FakeAvatarService();
        var appearance = new AvatarAppearance();
        BakeOutcome Bake(BakeReason r) => BakeOrchestrator.Run(Agent, r, wearables, vp, appearance, assets, avatars,
            new SkiaBakeBackend(compositor) { Quality = 0.5 }, compositor, 128, 9, CancellationToken.None);

        var first = Bake(BakeReason.Login);
        Assert.Equal(5, first.Count(ChannelStatus.Baked));

        assets.ResetOps();
        var change = Bake(BakeReason.CofChanged);

        // nothing recomputed
        Assert.Equal(0, change.Count(ChannelStatus.Baked));
        Assert.Equal(5, change.Count(ChannelStatus.Reused));
        Assert.Empty(assets.Stored);

        // ...and the appearance still goes out: this is the condition BakeAsync sends on
        Assert.True(change.Count(ChannelStatus.Baked) + change.Count(ChannelStatus.Reused) > 0);
        Assert.Equal(BakeReason.CofChanged, change.Reason);
        // every face still points at a real stored bake, so the message the viewer gets is usable
        foreach (var c in change.Channels.Where(c => c.Status == ChannelStatus.Reused))
            Assert.NotNull(assets.GetUnchecked(c.AssetId.ToString()));
    }

    // ------------------------------------------------------------------ the shipped wiring

    /// <summary>
    /// The trigger point and both routes into it, pinned against the source so this cannot rot into a test of a
    /// helper nothing calls.
    /// </summary>
    [Fact]
    public void TheShippedWiringMatchesWhatIsTestedHere()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray())).Replace("\r\n", "\n");

        var factory = Read("Source", "OpenSim.Region.CoreModules", "Avatar", "AvatarFactory", "AvatarFactoryModule.cs");
        // the completion event is raised, and after the persist rather than before it
        var persist = factory.IndexOf("m_scene.AvatarService.SetAppearance(id, sp.Appearance);", StringComparison.Ordinal);
        var trigger = factory.IndexOf("m_scene.EventManager.TriggerAvatarAppearanceChanged(sp);", StringComparison.Ordinal);
        Assert.True(persist > 0 && trigger > persist, "the trigger must be raised after the appearance is persisted");
        Assert.DoesNotContain("//m_scene.EventManager.TriggerAvatarAppearanceChanged(sp);", factory);
        // the legacy route still queues a save
        Assert.Contains("QueueAppearanceSave(client.AgentId);", factory);

        var module = Read("Source", "OpenSim.Region.OptionalModules", "Avatar", "ServerSideBaking", "ServerSideBakingModule.cs");
        Assert.Contains("scene.EventManager.OnAvatarAppearanceChange += OnAvatarAppearanceChanged;", module);
        Assert.Contains("scene.EventManager.OnAvatarAppearanceChange -= OnAvatarAppearanceChanged;", module);
        Assert.Contains("BakeAsync(sp, BakeReason.CofChanged, CancellationToken.None)", module);
        Assert.Contains("region.TryClaimChangeBake(sp.UUID, DateTime.UtcNow)", module);
        // the cap route joins the same path instead of baking on arrival
        Assert.Contains("scene.AvatarFactory?.QueueAppearanceSave(agentID);", module);
        Assert.DoesNotContain("BakeAsync(sp, BakeReason.Cap,", module);
    }
}
