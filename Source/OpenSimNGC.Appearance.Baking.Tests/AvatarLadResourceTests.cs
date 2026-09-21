using System.Xml.Linq;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSimNGC.Appearance.Baking.Tests;

public class AvatarLadResourceTests
{
    private const string ResourceName = "OpenSimNGC.Appearance.Baking.Data.avatar_lad.xml";

    [Fact]
    public void AvatarLad_IsEmbedded_AndContainsAtLeastOneLayerSet()
    {
        var asm = typeof(BakeHash).Assembly;

        using Stream? stream = asm.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);

        XDocument doc = XDocument.Load(stream!);
        Assert.Equal("linden_avatar", doc.Root?.Name.LocalName);

        int layerSets = doc.Descendants("layer_set").Count();
        Assert.True(layerSets >= 1, $"expected at least one <layer_set>, found {layerSets}");
    }
}
