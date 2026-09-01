/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Xunit;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.Base.Hosting;

namespace OpenSim.Server.Base.Tests.Hosting;

public class ConsoleFactoryTests
{
    private readonly IConsoleFactory _factory = new ConsoleFactory();

    [Theory]
    [InlineData("mock")]
    [InlineData("MOCK")]
    [InlineData("Mock")]
    public void Create_Mock_ReturnsMockConsole(string type)
    {
        var console = _factory.Create(type, "Test> ");
        Assert.IsType<MockConsole>(console);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("BASIC")]
    public void Create_Basic_ReturnsCommandConsole(string type)
    {
        var console = _factory.Create(type, "Test> ");
        // CommandConsole is the concrete type for "basic"; LocalConsole also derives
        // from it, so we check for exact type.
        Assert.IsType<CommandConsole>(console);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("REST")]
    public void Create_Rest_ReturnsRemoteConsole(string type)
    {
        var console = _factory.Create(type, "Test> ");
        Assert.IsType<RemoteConsole>(console);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("LOCAL")]
    [InlineData("")]
    [InlineData("unrecognised")]
    [InlineData(null)]
    public void Create_LocalOrUnknown_ReturnsLocalConsole(string? type)
    {
        var console = _factory.Create(type!, "Test> ");
        Assert.IsType<LocalConsole>(console);
    }
}

public class ConsoleContextTests
{
    [Fact]
    public void Constructor_SetsConsoleProperty()
    {
        var mock = new MockConsole();
        var ctx  = new ConsoleContext(mock);
        Assert.Same(mock, ctx.Console);
    }

    [Fact]
    public void Constructor_SetsMainConsoleInstance()
    {
        var mock = new MockConsole();
        var ctx  = new ConsoleContext(mock);
        Assert.Same(mock, MainConsole.Instance);
    }

    [Fact]
    public void Constructor_NullConsole_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConsoleContext(null!));
    }
}
