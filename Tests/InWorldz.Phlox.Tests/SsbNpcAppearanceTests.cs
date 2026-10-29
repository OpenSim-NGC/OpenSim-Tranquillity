using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An NPC created from a server-baked owner renders as a cloud on a server-side-baking region.
///
/// <para>
/// The NPC's appearance is a clone of the owner's (<c>NPCModule.CreateNPC</c>, <c>BotManager.CreateBot</c>):
/// the baked faces point at the owner's stored bake assets and the visual params are the owner's byte for byte,
/// including <c>AppearanceMessage_Version</c> (id 11000, index 251), which the owner's viewer sends as 1 once it
/// is on the server-bake path. The region never bakes an NPC (<c>ServerSideBakingModule</c> returns on
/// <c>IsNPC</c> for both the login and the change bake), so <c>BakedCofVersion(npc)</c> is -1 and the NPC's
/// appearance goes out with no <c>AppearanceData</c> block - but with the inherited parameter still saying 1.
/// The viewer prefers the parameter (<c>resolve_appearance_version</c>), puts the NPC on the server-bake path,
/// and fetches every baked face from the appearance service under the <b>NPC's</b> UUID, which has no bake
/// index: 404 on every channel, and the avatar never leaves the cloud state.
/// </para>
///
/// <para>
/// The fix under test: a presence this region did not bake must not claim a server bake. For an NPC the
/// parameter goes out as 0, which is exactly the message every avatar carried before SSB and which Firestorm
/// answers by fetching the (real, stored) bake assets from the region as it always did.
/// </para>
/// </summary>
public class SsbNpcAppearanceTests
{
    private readonly ITestOutputHelper _out;
    public SsbNpcAppearanceTests(ITestOutputHelper o) => _out = o;

    private const int VersionIndex = AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX;

    /// <summary>An observer whose client records every AvatarAppearance the region sends it.</summary>
    private sealed class CapturingClient : TestClient
    {
        public readonly List<(UUID Agent, byte[] Params, byte[] Te, int Cof)> Appearances = new();
        public CapturingClient(AgentCircuitData acd, Scene scene) : base(acd, scene) { }
        public override void SendAppearance(UUID agentID, byte[] visualParams, byte[] textureEntry, float hover, int cofVersion)
        {
            lock (Appearances) Appearances.Add((agentID, visualParams, textureEntry, cofVersion));
        }
        public override void SendAppearance(UUID agentID, byte[] visualParams, byte[] textureEntry, float hover)
        {
            lock (Appearances) Appearances.Add((agentID, visualParams, textureEntry, -1));
        }
        public (UUID Agent, byte[] Params, byte[] Te, int Cof) LastFor(UUID agent)
        {
            lock (Appearances) return Appearances.Last(a => a.Agent == agent);
        }
    }

    private static SchedulerHarness NpcScene()
    {
        var h = new SchedulerHarness(cfg =>
        {
            cfg.AddConfig("NPC").Set("Enabled", "true");
            cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            cfg.AddConfig("Chat");
        });
        SceneHelpers.SetupSceneModules(h.Scene, h.Config,
            new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(), new NPCModule(),
            new BasicInventoryAccessModule(), new BotManager(), new ChatModule());
        Assert.NotNull(h.Scene.RequestModuleInterface<IBotManager>());
        return h;
    }

    /// <summary>
    /// What the owner looks like after this region baked them: every baked face points at a stored bake asset,
    /// and the parameter the viewer sends back on the server-bake path is 1
    /// (<c>F32_to_U8(1.0, 0, 255)</c>, <c>llagent.cpp sendAgentSetAppearance</c>).
    /// </summary>
    private static Dictionary<int, UUID> MakeBaked(ScenePresence owner)
    {
        var bakes = new Dictionary<int, UUID>();
        foreach (int face in AvatarAppearance.BAKE_INDICES)
        {
            var id = UUID.Random();
            bakes[face] = id;
            owner.Appearance.Texture.CreateFace((uint)face).TextureID = id;
        }
        var vp = new byte[Math.Max(owner.Appearance.VisualParams?.Length ?? 0, VersionIndex + 1)];
        if (owner.Appearance.VisualParams is { } old) Array.Copy(old, vp, old.Length);
        vp[VersionIndex] = 1;
        owner.Appearance.VisualParams = vp;
        return bakes;
    }

    private static CapturingClient AddObserver(Scene scene)
    {
        var acd = SceneHelpers.GenerateAgentData(UUID.Random());
        var client = new CapturingClient(acd, scene);
        SceneHelpers.AddScenePresence(scene, client, acd);
        return client;
    }

    private static Dictionary<int, UUID> BakedFaces(byte[] textureEntryBytes)
    {
        var te = new Primitive.TextureEntry(textureEntryBytes, 0, textureEntryBytes.Length);
        return AvatarAppearance.BAKE_INDICES.ToDictionary(f => (int)f, f => te.FaceTextures[f]?.TextureID ?? te.DefaultTexture.TextureID);
    }

    // ------------------------------------------------------------------ PART 1 (d): the dump

    [Fact]
    public void AnNpcClonedFromABakedOwnerCarriesTheOwnersBakesAndIsNeverBakedItself()
    {
        using var h = NpcScene();
        var region = new ServerSideBakingRegion(true, new CofHandshake());
        h.Scene.RegisterModuleInterface<IServerSideBakingRegion>(region);

        var owner = h.Scene.GetScenePresence(h.AddClient().AgentId);
        var bakes = MakeBaked(owner);
        region.RecordBake(owner.UUID, 7);

        var bots = h.Scene.RequestModuleInterface<IBotManager>();
        var npcId = bots.CreateBot("Test", "Npc", owner.AbsolutePosition + new Vector3(2, 0, 0), "", UUID.Zero, owner.UUID, true, true, out string reason);
        Assert.True(npcId != UUID.Zero, reason);
        var npc = h.Scene.GetScenePresence(npcId);
        Assert.True(npc.IsNPC);

        foreach (int face in AvatarAppearance.BAKE_INDICES)
        {
            var o = owner.Appearance.Texture.FaceTextures[face]?.TextureID;
            var n = npc.Appearance.Texture.FaceTextures[face]?.TextureID;
            _out.WriteLine($"face {face,2}: owner={o} npc={n}");
            Assert.Equal(bakes[face], n);                       // the clone copies the baked faces (copyBaked = true)
        }
        _out.WriteLine($"param[{VersionIndex}]: owner={owner.Appearance.VisualParams[VersionIndex]} npc={npc.Appearance.VisualParams[VersionIndex]}");
        Assert.Equal(1, npc.Appearance.VisualParams[VersionIndex]); // ... and the owner's appearance-version byte with them

        // the bake store is keyed by agent UUID, and the region never baked the NPC's
        Assert.Equal(7, region.BakedCofVersion(owner.UUID));
        Assert.Equal(-1, region.BakedCofVersion(npc.UUID));
    }

    // ------------------------------------------------------------------ the wire, before and after the fix

    [Fact]
    public void TheNpcsAppearanceMessageMustNotClaimAServerBakeItDoesNotHave()
    {
        using var h = NpcScene();
        var region = new ServerSideBakingRegion(true, new CofHandshake());
        h.Scene.RegisterModuleInterface<IServerSideBakingRegion>(region);

        var owner = h.Scene.GetScenePresence(h.AddClient().AgentId);
        var bakes = MakeBaked(owner);
        region.RecordBake(owner.UUID, 7);
        var observer = AddObserver(h.Scene);

        var bots = h.Scene.RequestModuleInterface<IBotManager>();
        var npcId = bots.CreateBot("Test", "Npc", owner.AbsolutePosition + new Vector3(2, 0, 0), "", UUID.Zero, owner.UUID, true, true, out string reason);
        Assert.True(npcId != UUID.Zero, reason);
        var npc = h.Scene.GetScenePresence(npcId);

        owner.SendAppearanceToAllOtherAgents();
        npc.SendAppearanceToAllOtherAgents();
        var forOwner = observer.LastFor(owner.UUID);
        var forNpc = observer.LastFor(npcId);
        _out.WriteLine($"owner: cof={forOwner.Cof} param[{VersionIndex}]={forOwner.Params[VersionIndex]}");
        _out.WriteLine($"npc:   cof={forNpc.Cof} param[{VersionIndex}]={forNpc.Params[VersionIndex]}");

        // control: the baked owner's message says 1 in both places, block and parameter, as the viewer requires
        Assert.Equal(7, forOwner.Cof);
        Assert.Equal(1, forOwner.Params[VersionIndex]);

        // the NPC's first appearance carries the owner's real bake ids ...
        var sent = BakedFaces(forNpc.Te);
        foreach (int face in AvatarAppearance.BAKE_INDICES) Assert.Equal(bakes[face], sent[face]);
        // ... no AppearanceData block, because the region never baked it ...
        Assert.Equal(-1, forNpc.Cof);
        // ... and therefore the parameter must say 0 too. Saying 1 here sends every viewer to the appearance
        // service under the NPC's UUID, where there is no index and every channel is a 404.
        Assert.Equal(0, forNpc.Params[VersionIndex]);

        // the fix never touches the stored appearance: the parameter is a property of the message
        Assert.Equal(1, npc.Appearance.VisualParams[VersionIndex]);
    }

    /// <summary>
    /// The same on a region with no baking module at all: an NPC is never server-baked anywhere, so its message
    /// says 0 there too, while the owner's stored parameters go out untouched as they always have.
    /// </summary>
    [Fact]
    public void OnARegionWithoutSsbTheNpcSaysZeroAndTheOwnerIsUntouched()
    {
        using var h = NpcScene();
        Assert.Null(h.Scene.RequestModuleInterface<IServerSideBakingRegion>());

        var owner = h.Scene.GetScenePresence(h.AddClient().AgentId);
        MakeBaked(owner);                                            // a stored byte of 1 from a session on an SSB region
        var observer = AddObserver(h.Scene);

        var bots = h.Scene.RequestModuleInterface<IBotManager>();
        var npcId = bots.CreateBot("Test", "Npc", owner.AbsolutePosition + new Vector3(2, 0, 0), "", UUID.Zero, owner.UUID, true, true, out string reason);
        Assert.True(npcId != UUID.Zero, reason);
        var npc = h.Scene.GetScenePresence(npcId);

        owner.SendAppearanceToAllOtherAgents();
        npc.SendAppearanceToAllOtherAgents();
        var forOwner = observer.LastFor(owner.UUID);
        var forNpc = observer.LastFor(npcId);

        Assert.Equal(-1, forOwner.Cof);
        Assert.Same(owner.Appearance.VisualParams, forOwner.Params);  // untouched, not even copied
        Assert.Equal(-1, forNpc.Cof);
        Assert.Equal(0, forNpc.Params[VersionIndex]);
    }
}
