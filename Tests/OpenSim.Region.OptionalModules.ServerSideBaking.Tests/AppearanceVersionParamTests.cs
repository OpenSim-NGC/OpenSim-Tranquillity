using OpenMetaverse;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S4b — the appearance-version parameter. The <c>AppearanceData</c> block S3 added is not sufficient on its own:
/// the viewer reads the version from the block <b>and</b> from visual parameter 11000, prefers the parameter, and
/// throws the whole message away when the two disagree.
///
/// <list type="bullet">
///   <item><c>resolve_appearance_version</c> — both set and different → warns "inconsistent appearance_version
///     settings" and returns false (<c>llvoavatar.cpp:9663-9690</c>).</item>
///   <item>the caller then logs "bad appearance version info, discarding" and returns
///     (<c>llvoavatar.cpp:9720-9723</c>) — no TextureEntry applied, so no bake is ever fetched.</item>
///   <item>the parameter is transmitted as a byte through the parameter's own range, and id 11000 is
///     <c>value_min="0" value_max="255"</c>, so the mapping is the identity and the byte must literally equal the
///     block's version (<c>llvoavatar.cpp:9628-9630</c>, <c>:9650-9658</c>).</item>
/// </list>
/// </summary>
public class AppearanceVersionParamTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    /// <summary>A 253-byte parameter array shaped like the one a viewer actually uploads, with slot 251 at 0.</summary>
    private static byte[] Params()
    {
        var vp = new byte[253];
        for (var i = 0; i < vp.Length; i++) vp[i] = (byte)(i * 3 + 7);
        vp[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX] = 0;   // what a client-baking viewer sends
        return vp;
    }

    // ------------------------------------------------------------------ the index and the helper

    [Fact]
    public void TheParameterIndexIsTheOneTheEnumNames()
    {
        Assert.Equal(251, AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX);
        Assert.Equal((int)AvatarAppearance.VPElement._APPEARANCEMESSAGE_VERSION, AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX);
    }

    [Fact]
    public void TheHelperSetsOnlyThatByteAndNeverMutatesItsArgument()
    {
        var source = Params();
        var before = (byte[])source.Clone();

        var got = AvatarAppearance.WithAppearanceVersion(source, 1);

        Assert.NotSame(source, got);
        Assert.Equal(before, source);                                             // argument untouched
        Assert.Equal(1, got[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX]);
        for (var i = 0; i < source.Length; i++)
            if (i != AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX)
                Assert.Equal(source[i], got[i]);
    }

    [Fact]
    public void TheHelperIsAnIdentityWhenThereIsNothingToDo()
    {
        Assert.Null(AvatarAppearance.WithAppearanceVersion(null, 1));

        // already correct: same instance, so no allocation and no copy on the common warm path
        var already = Params();
        already[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX] = 1;
        Assert.Same(already, AvatarAppearance.WithAppearanceVersion(already, 1));

        // too short to carry the slot: returned as-is. The viewer will not find parameter 11000 among the
        // transmitted blocks either, and an absent parameter with a present field resolves to the field.
        var stub = new byte[10];
        Assert.Same(stub, AvatarAppearance.WithAppearanceVersion(stub, 1));
    }

    // ------------------------------------------------------------------ the two send paths

    /// <summary>
    /// The choice ScenePresence.SendAppearanceToAgentNF makes, replayed. Pinned against the source below so it
    /// cannot drift.
    /// </summary>
    /// <summary>
    /// The choice <c>ScenePresence.SendAppearanceToAgentNF</c> makes, replayed: 1 on a copy for an avatar this region
    /// baked; the stored array itself otherwise - except for an NPC, which is never baked anywhere and says 0 on a
    /// copy (SSB-NPC-1: its stored byte is the owner's, which is 1 on a server-bake region).
    /// </summary>
    private static byte[] ParamsForSend(byte[] stored, int cofVersion, bool isNpc = false)
        => cofVersion >= 0 ? AvatarAppearance.WithAppearanceVersion(stored, 1)
         : isNpc ? AvatarAppearance.WithAppearanceVersion(stored, 0)
         : stored;

    [Fact]
    public void OnAnSsbRegionTheParameterAndTheBlockAgree()
    {
        var stored = Params();
        const int cofVersion = 7;

        var sent = ParamsForSend(stored, cofVersion);
        var body = AppearanceBody(sent, cofVersion);

        // the parameter, as the viewer will read it out of the VisualParam blocks
        Assert.Equal(1, sent[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX]);

        // the block, as the viewer will read it out of AppearanceData
        var at = 16 + 1 + 2 + 0 + 1 + sent.Length;
        Assert.Equal(1, body[at]);                                  // one AppearanceData block
        Assert.Equal(1, body[at + 1]);                              // AppearanceVersion
        Assert.Equal(cofVersion, BitConverter.ToInt32(body, at + 2));

        // agreement is the whole point: parameter == field, so resolve_appearance_version returns true
        Assert.Equal(sent[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX], body[at + 1]);
    }

    [Fact]
    public void OnAFlagOffRegionNothingAboutTheAppearanceChanges()
    {
        var stored = Params();
        var storedBefore = (byte[])stored.Clone();

        var sent = ParamsForSend(stored, -1);

        // the very same array goes out, so the parameters cannot differ by construction
        Assert.Same(stored, sent);
        Assert.Equal(storedBefore, stored);
        Assert.Equal(0, sent[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX]);

        // and the packet body is byte-identical to the pre-S3 form
        Assert.Equal(AppearanceBodyBeforeS3(stored), AppearanceBody(sent, -1));
    }

    /// <summary>
    /// A sim-baked avatar and a client-baked one on the same simulator must not interfere: the helper's copy means
    /// the stored parameters are never touched, so an agent that walks from a flag-on region to a flag-off one
    /// still sends its own 0.
    /// </summary>
    [Fact]
    public void SendingOnAnSsbRegionDoesNotChangeWhatALaterFlagOffSendCarries()
    {
        var stored = Params();

        var ssb = ParamsForSend(stored, 3);
        Assert.Equal(1, ssb[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX]);

        var off = ParamsForSend(stored, -1);
        Assert.Same(stored, off);
        Assert.Equal(0, off[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX]);
        Assert.Equal(AppearanceBodyBeforeS3(stored), AppearanceBody(off, -1));
    }

    /// <summary>
    /// SSB-NPC-1: an NPC's stored parameters are a clone of its owner's, so on a server-bake region slot 251 holds
    /// the owner's 1 - while the region never baked the NPC and sends no block. A 1 beside no block sends the viewer
    /// to the appearance service under the NPC's UUID, where there is no index. The NPC's message says 0, on a
    /// copy: the stored clone is untouched, and the body is the pre-S3 form for the same parameters with 0 in it.
    /// </summary>
    [Fact]
    public void AnUnbakedNpcSaysZeroOnACopyWhateverItsOwnerStored()
    {
        var stored = Params();
        stored[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX] = 1;   // the owner's byte, inherited by the clone
        var storedBefore = (byte[])stored.Clone();

        var sent = ParamsForSend(stored, -1, isNpc: true);

        Assert.NotSame(stored, sent);
        Assert.Equal(storedBefore, stored);
        Assert.Equal(0, sent[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX]);
        var expected = (byte[])stored.Clone(); expected[AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX] = 0;
        Assert.Equal(AppearanceBodyBeforeS3(expected), AppearanceBody(sent, -1));

        // and a human with the same stored byte and no bake here is still sent as-is: the exception is the NPC alone
        Assert.Same(stored, ParamsForSend(stored, -1));
    }

    /// <summary>The replay above is only worth anything if it matches the shipped code.</summary>
    [Fact]
    public void TheReplayedChoiceMatchesWhatScenePresenceActuallyDoes()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var src = File.ReadAllText(Path.Combine(root, "Source", "OpenSim.Region.Framework", "Scenes", "ScenePresence.cs")).Replace("\r\n", "\n");

        Assert.Contains("byte[] visualParams;\n        if (cofVersion >= 0)\n            visualParams = AvatarAppearance.WithAppearanceVersion(Appearance.VisualParams, 1);\n        else if (IsNPC)", src);
        Assert.Contains("            visualParams = AvatarAppearance.WithAppearanceVersion(Appearance.VisualParams, 0);\n        else\n            visualParams = Appearance.VisualParams;", src);
        Assert.Contains("avatar.ControllingClient.SendAppearance(UUID, visualParams, Appearance.Texture.GetBakesBytes(), Appearance.AvatarPreferencesHoverZ, cofVersion);", src);
        // the stored array must never be written through
        Assert.DoesNotContain("Appearance.VisualParams[", src);
    }

    // ------------------------------------------------------------------ the packet body, as LLClientView writes it

    private static byte[] AppearanceBody(byte[] visualParams, int cofVersion)
    {
        var data = new byte[4096];
        int pos = 0;
        Agent.ToBytes(data, pos); pos += 16;
        data[pos++] = 0;
        data[pos++] = 0; data[pos++] = 0;                 // empty TextureEntry
        data[pos++] = (byte)visualParams.Length;
        Buffer.BlockCopy(visualParams, 0, data, pos, visualParams.Length); pos += visualParams.Length;
        if (cofVersion < 0) data[pos++] = 0;
        else
        {
            data[pos++] = 1;
            data[pos++] = 1;
            Utils.IntToBytesSafepos(cofVersion, data, pos); pos += 4;
            Utils.UIntToBytesSafepos(0, data, pos); pos += 4;
        }
        data[pos++] = 1;
        Utils.FloatToBytesSafepos(0, data, pos); pos += 4;
        Utils.FloatToBytesSafepos(0, data, pos); pos += 4;
        Utils.FloatToBytesSafepos(0, data, pos); pos += 4;
        return data[..pos];
    }

    private static byte[] AppearanceBodyBeforeS3(byte[] visualParams)
    {
        var data = new byte[4096];
        int pos = 0;
        Agent.ToBytes(data, pos); pos += 16;
        data[pos++] = 0;
        data[pos++] = 0; data[pos++] = 0;
        data[pos++] = (byte)visualParams.Length;
        Buffer.BlockCopy(visualParams, 0, data, pos, visualParams.Length); pos += visualParams.Length;
        data[pos++] = 0;                                   // "// no AppearanceData"
        data[pos++] = 1;
        Utils.FloatToBytesSafepos(0, data, pos); pos += 4;
        Utils.FloatToBytesSafepos(0, data, pos); pos += 4;
        Utils.FloatToBytesSafepos(0, data, pos); pos += 4;
        return data[..pos];
    }
}
