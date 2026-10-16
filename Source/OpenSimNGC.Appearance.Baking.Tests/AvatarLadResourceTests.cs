/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

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
