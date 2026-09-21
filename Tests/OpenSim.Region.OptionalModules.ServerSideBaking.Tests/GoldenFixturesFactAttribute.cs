using System.Runtime.CompilerServices;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that needs a golden reference set's fixtures.
/// <para>The sets live with the compositor's own harness, under
/// <c>Source/OpenSimNGC.Appearance.Baking.Tests/Golden/&lt;set&gt;/fixtures/</c>. They are one resident's real
/// worn assets, so they are not committed and no checkout has them until an operator generates them from their
/// own grid with <c>Golden/fetch-fixtures.sh</c>.</para>
/// <para>A test marked with this attribute is <b>skipped</b> when its set's fixtures are absent, rather than
/// returning early and reporting a pass it never earned. xunit reads <see cref="FactAttribute.Skip"/> at
/// discovery, which is why the check is in the constructor. This mirrors
/// <c>OpenSimNGC.Appearance.Baking.Tests.Golden.GoldenFactAttribute</c>; it is duplicated rather than shared
/// because this project references the baking library, not its test project.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GoldenFixturesFactAttribute : FactAttribute
{
    /// <summary>The Golden/ directory of the baking library's test project, relative to this source file.</summary>
    private static string GoldenDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Source", "OpenSimNGC.Appearance.Baking.Tests", "Golden"));

    /// <param name="set">The reference set's directory name, e.g. <c>truly-stock</c>.</param>
    public GoldenFixturesFactAttribute(string set)
    {
        Set = set;
        var fixtures = Path.Combine(GoldenDir(), set, "fixtures");
        if (!File.Exists(Path.Combine(fixtures, "avatar.json")))
        {
            Skip = $"no fixtures for golden reference set '{set}'. They are one resident's real assets and are " +
                   $"not committed. To generate them for your own grid: export the variables documented in the " +
                   $"header of Source/OpenSimNGC.Appearance.Baking.Tests/Golden/fetch-fixtures.sh (grid database " +
                   $"container and name, a file holding its root password, the Robust asset service URL and a " +
                   $"simulator asset cache), then run that script with the argument {set}. It writes {fixtures}. " +
                   $"Nothing is asserted without them.";
        }
    }

    /// <summary>The reference set this test reads.</summary>
    public string Set { get; }
}
