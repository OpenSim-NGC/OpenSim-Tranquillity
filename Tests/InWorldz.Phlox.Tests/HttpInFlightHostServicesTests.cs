/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Phlox.ScriptEngine.AsyncCommand.Plugins;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The HTTP request plugin's in-flight caps (per object and per region) are switched by the HttpInFlightThrottle setting of the
/// host services of the manager the plugin belongs to, so an engine other than Phlox's, which passes its own host services to
/// the manager, switches them with its own setting. A manager with no host services set takes them from its engine, as Phlox's
/// does, so the setting in [InWorldz.Phlox] keeps working.
/// </summary>
// No test reaches a network service: the plugin is started with a function that makes up a request id. No process-wide state: the class runs in parallel.
public class HttpInFlightHostServicesTests
{
    private static IScriptHostServices Switch(bool throttle) =>
        Fake<IScriptHostServices>.Create((m, a) => m.Name == "get_" + nameof(IScriptHostServices.HttpInFlightThrottle) ? throttle : null);

    private static UUID Start(HttpRequest plugin, UUID item, UUID obj, out bool capped) =>
        plugin.Start(item, obj, () => UUID.Random(), out capped);

    [Fact]
    public void HostServicesThatSwitchTheCapsOffLeaveARequestPastTheObjectsCapUncapped()
    {
        using var r = new ApiCallRig();   // the Phlox engine's own setting stays on
        r.H.Engine.AsyncCommands.HostServices = Switch(false);
        HttpRequest plugin = r.H.Engine.AsyncCommands.HttpRequestPlugin;
        UUID obj = UUID.Random();

        for (int i = 0; i < 2 * HttpRequest.MaxInFlightPerObject; i++)
        {
            UUID id = Start(plugin, r.Item, obj, out bool capped);
            Assert.False(capped, "request " + i + " was capped");
            Assert.NotEqual(UUID.Zero, id);
        }
        Assert.Equal(2 * HttpRequest.MaxInFlightPerObject, plugin.OutstandingCount);
    }

    [Fact]
    public void HostServicesThatSwitchTheCapsOnCapTheRequestPastTheObjectsCapWhateverTheEngineSays()
    {
        using var r = new ApiCallRig(cfg => cfg.Configs["InWorldz.Phlox"].Set("HttpInFlightThrottle", "false"));
        r.H.Engine.AsyncCommands.HostServices = Switch(true);
        HttpRequest plugin = r.H.Engine.AsyncCommands.HttpRequestPlugin;
        UUID obj = UUID.Random();

        for (int i = 0; i < HttpRequest.MaxInFlightPerObject; i++)
            Assert.NotEqual(UUID.Zero, Start(plugin, r.Item, obj, out _));
        UUID refused = Start(plugin, r.Item, obj, out bool capped);

        Assert.True(capped);
        Assert.Equal(UUID.Zero, refused);
        Assert.Equal(HttpRequest.MaxInFlightPerObject, plugin.OutstandingCount);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void AManagerWithNoHostServicesSetTakesTheSwitchFromItsEngine(string setting, bool capsApply)
    {
        using var r = new ApiCallRig(cfg => cfg.Configs["InWorldz.Phlox"].Set("HttpInFlightThrottle", setting));
        HttpRequest plugin = r.H.Engine.AsyncCommands.HttpRequestPlugin;
        UUID obj = UUID.Random();

        for (int i = 0; i < HttpRequest.MaxInFlightPerObject; i++)
            Assert.NotEqual(UUID.Zero, Start(plugin, r.Item, obj, out _));
        UUID eleventh = Start(plugin, r.Item, obj, out bool capped);

        Assert.Equal(capsApply, capped);
        Assert.Equal(capsApply, eleventh == UUID.Zero);
    }
}
