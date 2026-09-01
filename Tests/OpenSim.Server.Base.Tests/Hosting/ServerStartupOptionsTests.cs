/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Xunit;
using OpenSim.Server.Base.Hosting;

namespace OpenSim.Server.Base.Tests.Hosting;

public class ServerStartupOptionsTests
{
    [Fact]
    public void Defaults_AreSetCorrectly()
    {
        var opts = new ServerStartupOptions();

        Assert.Equal(string.Empty, opts.LogConfig);
        Assert.Empty(opts.IniFiles);
        Assert.Equal(string.Empty, opts.IniMaster);
        Assert.Equal("config", opts.IniDirectory);
        Assert.Equal("local", opts.ConsoleType);
    }

    [Fact]
    public void InitProperties_AreStored()
    {
        var opts = new ServerStartupOptions
        {
            LogConfig    = "my.config",
            IniFiles     = ["a.ini", "b.ini"],
            IniMaster    = "master.ini",
            IniDirectory = "cfg",
            ConsoleType  = "rest",
        };

        Assert.Equal("my.config",  opts.LogConfig);
        Assert.Equal(["a.ini", "b.ini"], opts.IniFiles);
        Assert.Equal("master.ini", opts.IniMaster);
        Assert.Equal("cfg",        opts.IniDirectory);
        Assert.Equal("rest",       opts.ConsoleType);
    }
}
