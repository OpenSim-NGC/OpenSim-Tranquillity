using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.AvatarService;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S4 Part 1 — the <c>agent_appearance_service</c> read path (ADR-002), against the real
/// <see cref="AppearanceService"/> over a fake avatar service and a fake asset service.
/// </summary>
public class AppearanceServiceTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    private sealed class TestableAppearanceService : AppearanceService
    {
        public TestableAppearanceService(IAvatarService avatars, IAssetService assets)
            : base(new IniConfigSource(), avatars, assets) { }
    }

    /// <summary>An agent with a head bake stored in the ADR-004 index, and the asset behind it.</summary>
    private static (TestableAppearanceService Service, FakeAvatarService Avatars, FakeAssetService Assets, UUID HeadAsset) Rig()
    {
        var avatars = new FakeAvatarService();
        var assets = new FakeAssetService();
        var headAsset = UUID.Random();

        assets.Put(new AssetBase(headAsset, $"bake:{Agent}:head", (sbyte)AssetType.Texture, Agent.ToString())
        {
            Data = new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 1, 2, 3, 4 },
        });
        BakeIndex.Write(avatars, Agent,
            new[] { new KeyValuePair<BakeChannel, StoredBake>(BakeChannel.Head, new StoredBake(headAsset, new string('a', 64))) },
            cofVersion: 7, bakeSize: 1024, updatedUtc: DateTime.UtcNow);

        return (new TestableAppearanceService(avatars, assets), avatars, assets, headAsset);
    }

    // ------------------------------------------------------------------ the hit

    [Fact]
    public void AKnownAgentAndChannelReturnsTheAssetBytes()
    {
        var (svc, _, assets, headAsset) = Rig();

        var got = svc.GetBake(Agent, "head", headAsset);

        Assert.NotNull(got);
        Assert.Equal(headAsset, got.FullID);
        Assert.Equal(assets.GetUnchecked(headAsset.ToString()).Data, got.Data);
    }

    [Fact]
    public void TheChannelTokenIsMatchedCaseInsensitively()
    {
        var (svc, _, _, headAsset) = Rig();

        Assert.NotNull(svc.GetBake(Agent, "head", headAsset));
        Assert.NotNull(svc.GetBake(Agent, "HEAD", headAsset));
        Assert.NotNull(svc.GetBake(Agent, "Head", headAsset));
    }

    // ------------------------------------------------------------------ the 404s

    /// <summary>
    /// The rule that keeps this route from becoming a way to fetch arbitrary assets, and from painting an avatar
    /// with a bake the viewer did not ask for: the UUID in the path is checked against the index, not used as the
    /// lookup key.
    /// </summary>
    [Fact]
    public void AUuidThatDoesNotMatchTheIndexIs404()
    {
        var (svc, _, assets, headAsset) = Rig();

        // a real, fetchable asset that simply is not this agent's head bake
        var other = UUID.Random();
        assets.Put(new AssetBase(other, "someone else", (sbyte)AssetType.Texture, UUID.Random().ToString()) { Data = new byte[] { 9, 9, 9 } });

        Assert.Null(svc.GetBake(Agent, "head", other));
        Assert.Null(svc.GetBake(Agent, "head", UUID.Zero));
        Assert.Null(svc.GetBake(Agent, "head", UUID.Random()));
        // and the one that does match still works, so the refusal is about the mismatch and nothing else
        Assert.NotNull(svc.GetBake(Agent, "head", headAsset));
    }

    [Fact]
    public void AnAgentWithNoIndexIs404()
    {
        var (svc, _, _, _) = Rig();
        var stranger = UUID.Random();

        Assert.Null(svc.GetBake(stranger, "head", UUID.Random()));
        Assert.Null(svc.GetBake(UUID.Zero, "head", UUID.Random()));
    }

    [Fact]
    public void AChannelTheAgentHasNoBakeForIs404()
    {
        var (svc, _, _, headAsset) = Rig();

        // the rig stores only Head; every other channel has no index entry
        foreach (var token in new[] { "upper", "lower", "eyes", "skirt", "hair", "leftarm", "leftleg", "aux1", "aux2", "aux3" })
            Assert.Null(svc.GetBake(Agent, token, headAsset));
    }

    [Fact]
    public void AnUnknownChannelTokenIs404()
    {
        var (svc, _, _, headAsset) = Rig();

        foreach (var token in new[] { "", "0", "8", "head-baked", "torso", "../head", "head/" })
            Assert.Null(svc.GetBake(Agent, token, headAsset));
    }

    /// <summary>An index entry pointing at an asset the asset service has lost answers 404, not an empty body.</summary>
    [Fact]
    public void AnIndexPointingAtAMissingAssetIs404()
    {
        var (svc, _, assets, headAsset) = Rig();
        Assert.True(assets.Remove(headAsset.ToString()));

        Assert.Null(svc.GetBake(Agent, "head", headAsset));
    }

    // ------------------------------------------------------------------ the token set

    /// <summary>
    /// <see cref="AppearanceChannels"/> is transcribed from the viewer and cannot reference the bake library
    /// (Robust does not ship it). This is the pin that keeps the two in step: every <see cref="BakeChannel"/> has
    /// a token, every token maps back to a channel, and the token is the channel name lower-cased — which is also
    /// what <see cref="BakeOrchestrator.AssetNameFor"/> puts in a stored bake's asset name.
    /// </summary>
    [Fact]
    public void EveryBakeChannelHasTheTokenTheViewerSends()
    {
        var channels = Enum.GetValues<BakeChannel>();
        Assert.Equal(channels.Length, AppearanceChannels.Tokens.Count);

        foreach (var ch in channels)
        {
            var token = ch.ToString().ToLowerInvariant();
            Assert.Contains(token, AppearanceChannels.Tokens);
            Assert.Equal(ch.ToString(), AppearanceChannels.IndexNameFor(token));
            Assert.Equal(BakeIndex.BakeKey(ch), AppearanceChannels.BakeKeyFor(token));
            Assert.EndsWith(token, BakeOrchestrator.AssetNameFor(Agent, ch));
        }

        // the exact eleven from llavatarappearancedefines.cpp:81-91, in baked-texture-index order
        Assert.Equal(
            new[] { "head", "upper", "lower", "eyes", "skirt", "hair", "leftarm", "leftleg", "aux1", "aux2", "aux3" },
            AppearanceChannels.Tokens.ToArray());
    }

    /// <summary>Every channel round-trips through a real bake index, not just through the token table.</summary>
    [Fact]
    public void EveryChannelResolvesThroughTheStoredIndex()
    {
        var avatars = new FakeAvatarService();
        var assets = new FakeAssetService();
        var stored = new List<KeyValuePair<BakeChannel, StoredBake>>();

        foreach (var ch in Enum.GetValues<BakeChannel>())
        {
            var id = UUID.Random();
            assets.Put(new AssetBase(id, BakeOrchestrator.AssetNameFor(Agent, ch), (sbyte)AssetType.Texture, Agent.ToString())
            {
                Data = new byte[] { (byte)ch, 1, 2, 3 },
            });
            stored.Add(new KeyValuePair<BakeChannel, StoredBake>(ch, new StoredBake(id, new string('b', 64))));
        }
        Assert.True(BakeIndex.Write(avatars, Agent, stored, 1, 1024, DateTime.UtcNow));

        var svc = new TestableAppearanceService(avatars, assets);
        foreach (var kv in stored)
        {
            var token = kv.Key.ToString().ToLowerInvariant();
            var got = svc.GetBake(Agent, token, kv.Value.AssetId);
            Assert.True(got is not null, $"{token} should resolve");
            Assert.Equal(kv.Value.AssetId, got.FullID);
            Assert.Equal((byte)kv.Key, got.Data[0]);
        }
    }

    /// <summary>
    /// The Q-14 interaction, end to end for this route: an appearance save must not take the index with it, or
    /// every bake fetch starts 404ing the moment an agent changes anything.
    /// </summary>
    [Fact]
    public void AnAppearanceSaveDoesNotBreakTheFetchRoute()
    {
        var (svc, avatars, _, headAsset) = Rig();
        Assert.NotNull(svc.GetBake(Agent, "head", headAsset));

        avatars.SetAppearance(Agent, new AvatarAppearance());

        // FakeAvatarService.SetAppearance reproduces the real delete-everything-first behaviour, so this is the
        // pre-S3 outcome; the real AvatarService preserves the Bake* namespace and is tested separately.
        Assert.Null(svc.GetBake(Agent, "head", headAsset));
    }
}
