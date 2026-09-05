/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE
 * CONTRIBUTORS BE LIABLE FOR ANY DAMAGES ARISING IN ANY WAY OUT OF THE USE OF
 * THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using OpenSim.Framework.TrustedHypergrid;
using Xunit;

namespace OpenSim.TrustedHypergrid.Tests;

public class HGUriNormalizerTests
{
    [Fact]
    public void CollapsesHostCaseAndDefaultPort()
    {
        // Design Brief §4 worked example: explicit :80 with mixed case host and the
        // implicit-port form must converge to one canonical value.
        string a = HGUriNormalizer.Normalize("http://Grid.Example:80/");
        string b = HGUriNormalizer.Normalize("http://grid.example/");

        Assert.Equal("http://grid.example:80/", a);
        Assert.Equal("http://grid.example:80/", b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void HttpsStaysDistinctFromHttp()
    {
        string https = HGUriNormalizer.Normalize("https://grid.example/");

        Assert.Equal("https://grid.example:443/", https);
        Assert.NotEqual(HGUriNormalizer.Normalize("http://grid.example/"), https);
    }

    [Fact]
    public void IsIdempotent()
    {
        string once = HGUriNormalizer.Normalize("http://Grid.Example:80");
        string twice = HGUriNormalizer.Normalize(once);

        Assert.Equal(once, twice);
        Assert.Equal("http://grid.example:80/", once);
    }

    [Fact]
    public void LowercasesSchemeAndAddsTrailingSlash()
    {
        Assert.Equal("http://grid.example:8002/", HGUriNormalizer.Normalize("HTTP://Grid.Example:8002"));
    }

    [Fact]
    public void RejectsBlankAndRelative()
    {
        Assert.Throws<ArgumentException>(() => HGUriNormalizer.Normalize(""));
        Assert.Throws<ArgumentException>(() => HGUriNormalizer.Normalize("   "));
        Assert.Throws<ArgumentException>(() => HGUriNormalizer.Normalize(null));
        Assert.Throws<ArgumentException>(() => HGUriNormalizer.Normalize("grid.example/foo"));
    }
}
