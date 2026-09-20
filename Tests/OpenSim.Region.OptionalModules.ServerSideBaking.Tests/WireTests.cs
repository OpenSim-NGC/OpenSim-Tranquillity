using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S3 Part 1 — the wire. The §4.3 COF handshake in all four of its branches, the per-region flag, and the one
/// thing ADR-001 will not tolerate: that a flag-off region's <c>AvatarAppearance</c> packet is byte-for-byte the
/// packet it was before server-side baking existed.
/// </summary>
public class WireTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");
    private static readonly DateTime T0 = new(2026, 9, 4, 21, 0, 0, DateTimeKind.Utc);

    private static int NeverCalled() => throw new InvalidOperationException("the folder must not be re-read on this branch");

    // ------------------------------------------------------------------ the four handshake branches

    [Fact]
    public void Handshake_Equal_Bakes()
    {
        var h = new CofHandshake();
        var d = h.Decide(Agent, clientVersion: 7, serverVersion: 7, NeverCalled, T0);

        Assert.Equal(CofVerdict.Bake, d.Verdict);
        Assert.True(d.Success);
        Assert.Equal(7, d.Version);
        Assert.Equal(0, h.MismatchesFor(Agent));
    }

    [Fact]
    public void Handshake_ClientBehind_IsStaleAndQuotesTheServersVersion()
    {
        var h = new CofHandshake();
        var d = h.Decide(Agent, clientVersion: 5, serverVersion: 9, NeverCalled, T0);

        Assert.Equal(CofVerdict.Stale, d.Verdict);
        Assert.False(d.Success);
        Assert.Equal(9, d.Version);           // this is the `expected` the viewer re-requests with
        Assert.Equal(1, h.MismatchesFor(Agent));
    }

    /// <summary>
    /// The viewer is ahead because it changed the COF by a path this sim has not seen — with AIS live, that is
    /// the normal case. The folder is re-read once, and if the write has landed by then it is a plain match.
    /// </summary>
    [Fact]
    public void Handshake_ClientAhead_RereadsOnceAndBakesWhenItCatchesUp()
    {
        var h = new CofHandshake();
        var rereads = 0;
        int Reread() { rereads++; return 11; }

        var d = h.Decide(Agent, clientVersion: 11, serverVersion: 9, Reread, T0);

        Assert.Equal(1, rereads);
        Assert.Equal(CofVerdict.Bake, d.Verdict);
        Assert.Equal(11, d.Version);
        Assert.Equal(0, h.MismatchesFor(Agent));
    }

    /// <summary>
    /// Still ahead after the re-read: the sim cannot honestly claim the viewer's number, so it quotes the freshly
    /// read one and lets the viewer come back. Exactly one re-read, never a loop of them.
    /// </summary>
    [Fact]
    public void Handshake_ClientStillAheadAfterTheReread_IsStaleWithTheFreshVersion()
    {
        var h = new CofHandshake();
        var rereads = 0;
        int Reread() { rereads++; return 10; }

        var d = h.Decide(Agent, clientVersion: 12, serverVersion: 9, Reread, T0);

        Assert.Equal(1, rereads);
        Assert.Equal(CofVerdict.Stale, d.Verdict);
        Assert.Equal(10, d.Version);          // the re-read value, not the stale 9
        Assert.Equal(1, h.MismatchesFor(Agent));
    }

    [Fact]
    public void Handshake_ARereadThatThrows_DoesNotEscapeAndStillAnswers()
    {
        var h = new CofHandshake();
        var d = h.Decide(Agent, clientVersion: 12, serverVersion: 9, () => throw new TimeoutException("inventory service"), T0);

        Assert.Equal(CofVerdict.Stale, d.Verdict);
        Assert.Equal(9, d.Version);
    }

    // ------------------------------------------------------------------ anti-livelock (Ledger R-2)

    [Fact]
    public void Handshake_TooManyMismatchesInTheWindow_BakesAnyway()
    {
        var h = new CofHandshake { MaxMismatches = 3, Window = TimeSpan.FromSeconds(30) };

        Assert.Equal(CofVerdict.Stale, h.Decide(Agent, 5, 9, NeverCalled, T0).Verdict);
        Assert.Equal(CofVerdict.Stale, h.Decide(Agent, 5, 9, NeverCalled, T0.AddSeconds(1)).Verdict);

        var third = h.Decide(Agent, 5, 9, NeverCalled, T0.AddSeconds(2));
        Assert.Equal(CofVerdict.LivelockBake, third.Verdict);
        Assert.True(third.Success);
        Assert.Equal(9, third.Version);                 // baked at the server's version, per §4.3
        Assert.Contains("3 mismatches", third.Reason);

        // and it starts over afterwards, so the next disagreement is refused normally again
        Assert.Equal(0, h.MismatchesFor(Agent));
        Assert.Equal(CofVerdict.Stale, h.Decide(Agent, 5, 9, NeverCalled, T0.AddSeconds(3)).Verdict);
    }

    [Fact]
    public void Handshake_MismatchesOutsideTheWindowDoNotAccumulate()
    {
        var h = new CofHandshake { MaxMismatches = 3, Window = TimeSpan.FromSeconds(30) };

        Assert.Equal(CofVerdict.Stale, h.Decide(Agent, 5, 9, NeverCalled, T0).Verdict);
        Assert.Equal(CofVerdict.Stale, h.Decide(Agent, 5, 9, NeverCalled, T0.AddSeconds(10)).Verdict);
        // past the window: the count restarts rather than tripping the rule
        Assert.Equal(CofVerdict.Stale, h.Decide(Agent, 5, 9, NeverCalled, T0.AddSeconds(41)).Verdict);
        Assert.Equal(1, h.MismatchesFor(Agent));
        Assert.Equal(CofVerdict.Stale, h.Decide(Agent, 5, 9, NeverCalled, T0.AddSeconds(42)).Verdict);
    }

    [Fact]
    public void Handshake_ASuccessfulBakeClearsTheAgentsCounter()
    {
        var h = new CofHandshake { MaxMismatches = 3 };
        h.Decide(Agent, 5, 9, NeverCalled, T0);
        h.Decide(Agent, 5, 9, NeverCalled, T0.AddSeconds(1));
        Assert.Equal(2, h.MismatchesFor(Agent));

        Assert.Equal(CofVerdict.Bake, h.Decide(Agent, 9, 9, NeverCalled, T0.AddSeconds(2)).Verdict);
        Assert.Equal(0, h.MismatchesFor(Agent));
    }

    [Fact]
    public void Handshake_CountersArePerAgent()
    {
        var h = new CofHandshake { MaxMismatches = 2 };
        var other = UUID.Random();

        Assert.Equal(CofVerdict.Stale, h.Decide(Agent, 5, 9, NeverCalled, T0).Verdict);
        Assert.Equal(CofVerdict.Stale, h.Decide(other, 5, 9, NeverCalled, T0).Verdict);
        Assert.Equal(1, h.MismatchesFor(Agent));
        Assert.Equal(1, h.MismatchesFor(other));
    }

    // ------------------------------------------------------------------ the per-region flag

    private static IConfigSource Config(params (string Section, string Key, string Value)[] entries)
    {
        var src = new IniConfigSource();
        foreach (var (section, key, value) in entries)
            (src.Configs[section] ?? src.AddConfig(section)).Set(key, value);
        return src;
    }

    [Theory]
    // simulatorDefault, region section, region value  ->  expected
    [InlineData(false, null, null, false)]              // nothing anywhere: off, which is what every shipped ini says
    [InlineData(true, null, null, true)]                // simulator-wide on, no region section
    [InlineData(false, "Ebony", "true", true)]          // the case S3 ships for: one region opts in
    [InlineData(true, "Ebony", "false", false)]         // and a region can opt out of a simulator-wide on
    [InlineData(false, "Elm", "true", false)]           // a section for a different region does not apply
    public void FlagResolvesPerRegionLikeAisEnabledDoes(bool simulatorDefault, string section, string value, bool expected)
    {
        var config = section is null ? Config() : Config((section, "ServerSideBaking", value));
        Assert.Equal(expected, ServerSideBakingRegion.ResolveEnabled(simulatorDefault, config, "Ebony"));
    }

    [Fact]
    public void FlagFallsBackToTheSimulatorDefaultWhenThereIsNoConfigOrNoRegionName()
    {
        Assert.True(ServerSideBakingRegion.ResolveEnabled(true, null, "Ebony"));
        Assert.False(ServerSideBakingRegion.ResolveEnabled(false, null, "Ebony"));
        Assert.True(ServerSideBakingRegion.ResolveEnabled(true, Config(("Ebony", "ServerSideBaking", "false")), null));
    }

    // ------------------------------------------------------------------ what the flag gates

    [Fact]
    public void AFlagOffRegionNeverReportsABakedCofVersion()
    {
        var off = new ServerSideBakingRegion(false, new CofHandshake());
        Assert.False(off.ServerSideBakingEnabled);
        Assert.Equal(-1, off.BakedCofVersion(Agent));

        // a console bake on a flag-off region still writes faces and sends, but must not reach the wire
        off.RecordBake(Agent, 42);
        Assert.Equal(-1, off.BakedCofVersion(Agent));
    }

    [Fact]
    public void AFlagOnRegionReportsTheVersionItBakedAtAndForgetsOnClose()
    {
        var on = new ServerSideBakingRegion(true, new CofHandshake());
        Assert.Equal(-1, on.BakedCofVersion(Agent));

        on.RecordBake(Agent, 42);
        Assert.Equal(42, on.BakedCofVersion(Agent));

        on.RecordBake(Agent, 43);
        Assert.Equal(43, on.BakedCofVersion(Agent));

        // a negative version is "no bake", never recorded
        on.RecordBake(Agent, -1);
        Assert.Equal(43, on.BakedCofVersion(Agent));

        on.Forget(Agent);
        Assert.Equal(-1, on.BakedCofVersion(Agent));
        Assert.Equal(0, on.Handshake.MismatchesFor(Agent));
    }

    // ------------------------------------------------------------------ ADR-001: the flag-off packet is unchanged

    /// <summary>
    /// The AvatarAppearance packet body as <c>LLClientView.SendAppearance</c> writes it, replayed here field for
    /// field so the two forms can be compared without a UDP server. The layout under test is the one thing S3
    /// changed in a hot path: the AppearanceData block, which was a hard-coded count of 0 and is now a count of 0
    /// or one 9-byte block.
    /// </summary>
    private static byte[] AppearanceBody(byte[] textureEntry, byte[] visualParams, float hover, int cofVersion)
    {
        var data = new byte[4096];
        int pos = 0;
        Agent.ToBytes(data, pos); pos += 16;
        data[pos++] = 0;

        int len = textureEntry.Length;
        if (len == 0) { data[pos++] = 0; data[pos++] = 0; }
        else
        {
            data[pos++] = (byte)len;
            data[pos++] = (byte)(len >> 8);
            Buffer.BlockCopy(textureEntry, 0, data, pos, len); pos += len;
        }

        len = visualParams.Length;
        data[pos++] = (byte)len;
        if (len > 0) Buffer.BlockCopy(visualParams, 0, data, pos, len);
        pos += len;

        if (cofVersion < 0)
        {
            data[pos++] = 0;
        }
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
        Utils.FloatToBytesSafepos(hover, data, pos); pos += 4;
        return data[..pos];
    }

    /// <summary>
    /// The pre-S3 form, transcribed from the code as it stood at 95c3eefbbe: a single zero byte where the
    /// AppearanceData count goes, and nothing else different.
    /// </summary>
    private static byte[] AppearanceBodyBeforeS3(byte[] textureEntry, byte[] visualParams, float hover)
    {
        var data = new byte[4096];
        int pos = 0;
        Agent.ToBytes(data, pos); pos += 16;
        data[pos++] = 0;

        int len = textureEntry.Length;
        if (len == 0) { data[pos++] = 0; data[pos++] = 0; }
        else
        {
            data[pos++] = (byte)len;
            data[pos++] = (byte)(len >> 8);
            Buffer.BlockCopy(textureEntry, 0, data, pos, len); pos += len;
        }

        len = visualParams.Length;
        data[pos++] = (byte)len;
        if (len > 0) Buffer.BlockCopy(visualParams, 0, data, pos, len);
        pos += len;

        data[pos++] = 0;                       // "// no AppearanceData"
        data[pos++] = 1;
        Utils.FloatToBytesSafepos(0, data, pos); pos += 4;
        Utils.FloatToBytesSafepos(0, data, pos); pos += 4;
        Utils.FloatToBytesSafepos(hover, data, pos); pos += 4;
        return data[..pos];
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 218)]
    [InlineData(300, 0)]                       // a TextureEntry over 255 bytes, to exercise the two-byte length
    public void OnAFlagOffRegionTheAppearancePacketIsByteIdenticalToTheOldOne(int teLength, int vpLength)
    {
        var te = new byte[teLength];
        for (var i = 0; i < te.Length; i++) te[i] = (byte)(i * 7 + 1);
        var vp = new byte[vpLength];
        for (var i = 0; i < vp.Length; i++) vp[i] = (byte)(255 - i);

        // -1 is what SendAppearanceToAgentNF passes when the region has no baking module, or the flag is off, or
        // this sim has not baked the avatar
        Assert.Equal(AppearanceBodyBeforeS3(te, vp, 1.25f), AppearanceBody(te, vp, 1.25f, -1));
    }

    [Fact]
    public void OnAFlagOnRegionTheAppearanceCarriesExactlyOneNineByteAppearanceDataBlock()
    {
        var te = new byte[45];
        var vp = new byte[218];

        var without = AppearanceBody(te, vp, 0f, -1);
        var with = AppearanceBody(te, vp, 0f, 7);

        // count byte plus AppearanceVersion(1) + CofVersion(4) + Flags(4)
        Assert.Equal(without.Length + 9, with.Length);

        var at = 16 + 1 + 2 + te.Length + 1 + vp.Length;
        Assert.Equal(0, without[at]);
        Assert.Equal(1, with[at]);              // one block
        Assert.Equal(1, with[at + 1]);          // AppearanceVersion = 1 (V5)
        Assert.Equal(7, BitConverter.ToInt32(with, at + 2));
        Assert.Equal(0u, BitConverter.ToUInt32(with, at + 6));
        // everything before the block is untouched
        Assert.Equal(without[..at], with[..at]);
    }

    /// <summary>
    /// The two forms above are only worth anything if they match the shipped code. This reads
    /// <c>LLClientView.SendAppearance</c>'s source and asserts the branch is there, so a change to the packet
    /// writer that is not mirrored here fails rather than passing silently.
    /// </summary>
    [Fact]
    public void TheReplayedLayoutMatchesWhatLLClientViewActuallyWrites()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var file = Path.Combine(root, "Source", "OpenSim.Region.ClientStack.LindenUDP", "LLClientView.cs");
        Assert.True(File.Exists(file), file);
        var src = File.ReadAllText(file);

        var i = src.IndexOf("public void SendAppearance(UUID targetID, byte[] visualParams, byte[] textureEntry, float hover, int cofVersion)", StringComparison.Ordinal);
        Assert.True(i > 0, "the AppearanceData-bearing SendAppearance overload is gone or renamed");
        var body = src[i..(i + 3000)];

        Assert.Contains("if (cofVersion < 0)", body);
        Assert.Contains("data[pos++] = 1;", body);
        Assert.Contains("Utils.IntToBytesSafepos(cofVersion, data, pos); pos += 4;", body);
        Assert.Contains("Utils.UIntToBytesSafepos(0, data, pos); pos += 4;", body);

        // and the four-argument overload still exists and still means "no AppearanceData"
        Assert.Contains("public void SendAppearance(UUID targetID, byte[] visualParams, byte[] textureEntry, float hover)\n        => SendAppearance(targetID, visualParams, textureEntry, hover, -1);",
            src.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    /// <summary>RegionProtocols keeps bit 63 and gains bit 0 only behind the flag.</summary>
    [Fact]
    public void RegionProtocolsSetsBitZeroOnlyWhenTheFlagIsOn()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var src = File.ReadAllText(Path.Combine(root, "Source", "OpenSim.Region.ClientStack.LindenUDP", "LLClientView.cs")).Replace("\r\n", "\n");

        Assert.Contains("ulong regionProtocols = 1UL << 63;", src);
        Assert.Contains("if (m_scene.RequestModuleInterface<IServerSideBakingRegion>() is { ServerSideBakingEnabled: true })\n            regionProtocols |= 1UL;", src);
        Assert.Contains("zc.AddUInt64(regionProtocols);", src);
        Assert.DoesNotContain("zc.AddUInt64(1UL << 63);", src);

        // the arithmetic itself, so the constants cannot rot
        Assert.Equal(0x8000000000000000UL, 1UL << 63);
        Assert.Equal(0x8000000000000001UL, (1UL << 63) | 1UL);
    }
}
