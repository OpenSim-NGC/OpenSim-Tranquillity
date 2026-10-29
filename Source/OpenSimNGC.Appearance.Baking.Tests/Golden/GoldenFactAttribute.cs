using System.Runtime.CompilerServices;
using Xunit;

namespace OpenSimNGC.Appearance.Baking.Tests.Golden;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that needs a reference set's fixtures.
/// <para>A reference set is a subdirectory of <c>Golden/</c> holding a committed <c>manifest.json</c> and an
/// uncommitted <c>fixtures/</c>. The fixtures are one resident's real worn assets and the reference bakes
/// captured from them, so they are not in the repository and no checkout has them until an operator generates
/// them from their own grid. Without them the test cannot assert anything.</para>
/// <para>Rather than returning early and reporting a pass it never earned, a test marked with this attribute
/// is <b>skipped</b> when its set's <c>fixtures/avatar.json</c> is absent, and the skip reason says how to
/// produce them. xunit evaluates <see cref="FactAttribute.Skip"/> at discovery, which is why the check is in
/// the constructor.</para>
/// <para>Adding a new reference set means adding its directory, its <c>manifest.json</c>, and a test method
/// per gate naming it; sets are named here rather than discovered so that a set whose fixtures are missing is
/// still visible in the run as a skip.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GoldenFactAttribute : FactAttribute
{
    /// <summary>The directory this source file sits in, which is <c>Golden/</c>.</summary>
    private static string GoldenDir([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;

    /// <param name="set">The reference set's directory name, e.g. <c>truly-stock</c>.</param>
    public GoldenFactAttribute(string set)
    {
        Set = set;
        var fixtures = Path.Combine(GoldenDir(), set, "fixtures");
        if (!File.Exists(Path.Combine(fixtures, "avatar.json")))
        {
            Skip = $"no fixtures for reference set '{set}'. They are one resident's real assets and are not " +
                   $"committed. To generate them for your own grid: export the variables documented in the " +
                   $"header of Golden/fetch-fixtures.sh (grid database container and name, a file holding its " +
                   $"root password, the Robust asset service URL and a simulator asset cache), then run " +
                   $"Golden/fetch-fixtures.sh {set}. It writes {fixtures}. Nothing is asserted without them.";
        }
    }

    /// <summary>The reference set this test reads.</summary>
    public string Set { get; }
}
