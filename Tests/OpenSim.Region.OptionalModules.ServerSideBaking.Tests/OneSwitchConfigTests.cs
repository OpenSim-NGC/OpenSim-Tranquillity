using Nini.Config;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S12. The operator contract: **two lines in the global config turn AIS v3 and server-side baking on for the
/// whole simulator, and no region is ever named.** A <c>[&lt;Region Name&gt;]</c> section is an optional override
/// — the way a single region opts *out* — and never the way to opt in.
///
/// <para>
/// The four cases are asserted for both modules together, because "one switch for the grid" is a property of the
/// pair, not of either alone: an operator who sets both globals and finds one lane on and the other off has been
/// let down whichever module is at fault.
/// </para>
/// </summary>
public class OneSwitchConfigTests
{
    private const string Ebony = "Ebony";
    private const string Elm = "Elm";
    private const string Transylvania = "Transylvania";

    /// <summary>A config source with the given sections and keys.</summary>
    private static IConfigSource Config(params (string Section, string Key, string Value)[] entries)
    {
        var source = new IniConfigSource();
        foreach (var (section, key, value) in entries)
            (source.Configs[section] ?? source.AddConfig(section)).Set(key, value);
        return source;
    }

    /// <summary>The global default each module reads in Initialise, from the same source a region sees.</summary>
    private static bool AisGlobal(IConfigSource source)
        => source.Configs[AISv3Module.ConfigSection]?.GetBoolean("Enabled", false) ?? false;

    private static bool SsbGlobal(IConfigSource source)
        => source.Configs[ServerSideBakingModule.ConfigSection]?.GetBoolean("ServerSideBaking", false) ?? false;

    /// <summary>Both lanes for one region, resolved exactly as the modules resolve them.</summary>
    private static (bool Ais, bool Ssb) On(IConfigSource source, string region)
        => (AISv3Module.ResolveEnabled(AisGlobal(source), source, region),
            ServerSideBakingRegion.ResolveEnabled(SsbGlobal(source), source, region));

    /// <summary>The two lines an operator adds, and nothing else. No region is named anywhere.</summary>
    private static IConfigSource GlobalOnly() => Config(
        (AISv3Module.ConfigSection, "Enabled", "true"),
        (ServerSideBakingModule.ConfigSection, "ServerSideBaking", "true"));

    // ------------------------------------------------------------------ (a) the whole point

    [Fact]
    public void a_global_true_and_no_region_section_turns_every_region_on()
    {
        var source = GlobalOnly();

        foreach (var region in new[] { Ebony, Elm, Transylvania })
        {
            var (ais, ssb) = On(source, region);
            Assert.True(ais, $"AIS must be on for {region} from the global switch alone");
            Assert.True(ssb, $"server-side baking must be on for {region} from the global switch alone");
        }
    }

    [Fact]
    public void the_two_global_lines_are_the_documented_keys_and_sections()
    {
        // If either name drifts, the line an operator was told to add stops working and nothing says so. These are
        // the exact section and key names written into OpenSimDefaults.ini and the design brief's config contract.
        Assert.Equal("AIS", AISv3Module.ConfigSection);
        Assert.Equal("Appearance", ServerSideBakingModule.ConfigSection);

        var source = GlobalOnly();
        Assert.True(AisGlobal(source), "[AIS] Enabled = true");
        Assert.True(SsbGlobal(source), "[Appearance] ServerSideBaking = true");
    }

    // ------------------------------------------------------------------ (b) opt out

    [Fact]
    public void b_a_region_section_of_false_opts_that_region_out_and_leaves_the_rest_on()
    {
        var source = GlobalOnly();
        source.AddConfig(Elm).Set("AIS_Enabled", "false");
        source.Configs[Elm].Set("ServerSideBaking", "false");

        var elm = On(source, Elm);
        Assert.False(elm.Ais, "Elm opted out of AIS");
        Assert.False(elm.Ssb, "Elm opted out of server-side baking");

        foreach (var region in new[] { Ebony, Transylvania })
        {
            var (ais, ssb) = On(source, region);
            Assert.True(ais, $"{region} is untouched by Elm's opt-out");
            Assert.True(ssb, $"{region} is untouched by Elm's opt-out");
        }
    }

    [Fact]
    public void a_region_section_that_says_nothing_about_these_keys_does_not_opt_out()
    {
        // Regions commonly have a section for other settings. Its mere existence must not turn the lanes off -
        // the region value is an override only when the KEY is there.
        var source = GlobalOnly();
        source.AddConfig(Ebony).Set("SomeOtherSetting", "42");

        var (ais, ssb) = On(source, Ebony);
        Assert.True(ais);
        Assert.True(ssb);
    }

    // ------------------------------------------------------------------ (c) today's behaviour, kept

    [Fact]
    public void c_global_false_and_a_region_section_of_true_turns_that_region_on()
    {
        // This is how Ebony runs today (config/OpenSim.ini:162-164) and it must keep working: the single-region
        // trial is what every flip so far has depended on.
        var source = Config(
            (Ebony, "AIS_Enabled", "true"),
            (Ebony, "ServerSideBaking", "true"));

        var ebony = On(source, Ebony);
        Assert.True(ebony.Ais);
        Assert.True(ebony.Ssb);

        var elm = On(source, Elm);
        Assert.False(elm.Ais, "a region with no section stays off when the global is off");
        Assert.False(elm.Ssb);
    }

    // ------------------------------------------------------------------ (d) nothing set

    [Fact]
    public void d_nothing_set_anywhere_is_off()
    {
        var source = Config();

        foreach (var region in new[] { Ebony, Elm, Transylvania })
        {
            var (ais, ssb) = On(source, region);
            Assert.False(ais, $"AIS must default off for {region}");
            Assert.False(ssb, $"server-side baking must default off for {region}");
        }
    }

    [Fact]
    public void a_null_config_source_or_an_unnamed_region_falls_back_to_the_global()
    {
        Assert.True(AISv3Module.ResolveEnabled(true, null, Ebony));
        Assert.False(AISv3Module.ResolveEnabled(false, null, Ebony));
        Assert.True(ServerSideBakingRegion.ResolveEnabled(true, null, Ebony));
        Assert.False(ServerSideBakingRegion.ResolveEnabled(false, null, Ebony));

        var source = GlobalOnly();
        Assert.True(AISv3Module.ResolveEnabled(AisGlobal(source), source, null));
        Assert.True(ServerSideBakingRegion.ResolveEnabled(SsbGlobal(source), source, ""));
    }

    // ------------------------------------------------------------------ which source decided

    [Fact]
    public void the_deciding_source_is_reported_so_the_flip_can_be_verified_from_the_log()
    {
        var source = GlobalOnly();
        source.AddConfig(Elm).Set("ServerSideBaking", "false");
        source.AddConfig(Transylvania).Set("SomeOtherSetting", "42");

        Assert.Equal("global", ServerSideBakingRegion.EnabledSource(source, Ebony));
        Assert.Equal("region section", ServerSideBakingRegion.EnabledSource(source, Elm));
        Assert.Equal("global", ServerSideBakingRegion.EnabledSource(source, Transylvania));

        source.AddConfig(Ebony).Set("AIS_Enabled", "true");
        Assert.Equal("region section", AISv3Module.EnabledSource(source, Ebony));
        Assert.Equal("global", AISv3Module.EnabledSource(source, Elm));
    }
}
