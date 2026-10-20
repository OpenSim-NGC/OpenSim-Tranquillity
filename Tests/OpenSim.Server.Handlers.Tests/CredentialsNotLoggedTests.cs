/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Groups;
using OpenSim.Server.Handlers.Hypergrid;
using OpenSim.Services.AuthenticationService;
using OpenSim.Services.HypergridService;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Each test drives the real service with a fake credential and asserts that the value never reaches the log.
/// They share <see cref="LogCapture"/>, which replaces the process-wide logger factory, so they run in one
/// serial collection.
/// </summary>
[Collection("LogCapture")]
public partial class CredentialsNotLoggedTests
{
    private static string FakeSecret(string kind) => kind + "-" + Guid.NewGuid().ToString("N");


    [Xunit.Fact]
    public void VerifyAgentLogsOutcomeButNeitherToken()
    {
        string stored = FakeSecret("stored-service-token");
        string presented = FakeSecret("presented-service-token");
        UUID sessionID = UUID.Random();

        IHGTravelingData database = StubProxy.Create<IHGTravelingData>((m, a) =>
            m.Name == nameof(IHGTravelingData.Get)
                ? (true, new HGTravelingData
                {
                    SessionID = sessionID,
                    UserID = UUID.Random(),
                    Data = new Dictionary<string, string>
                    {
                        ["GridExternalName"] = "http://grid.example/",
                        ["ServiceToken"] = stored,
                        ["ClientIPAddress"] = "192.0.2.10"
                    }
                })
                : (false, null));
        UserAgentService service = UninitializedService.Create<UserAgentService>(("m_Database", database));

        using LogCapture log = new();
        Xunit.Assert.False(service.VerifyAgent(sessionID, presented));
        Xunit.Assert.True(service.VerifyAgent(sessionID, stored));

        Xunit.Assert.False(log.Contains(stored), "stored token reached the log");
        Xunit.Assert.False(log.Contains(presented), "presented token reached the log");
        Xunit.Assert.Equal(LogLevel.Debug, log.LevelOf("token did not match"));
        Xunit.Assert.Equal(LogLevel.Debug, log.LevelOf("token matched"));
        Xunit.Assert.True(log.Contains(sessionID.ToString()));
    }

    [Xunit.Fact]
    public void HGFriendsRejectedKeyIsNotLogged()
    {
        string presentedKey = FakeSecret("presented-friends-key");
        UUID sessionID = UUID.Random();
        IUserAgentService agents = StubProxy.Create<IUserAgentService>((m, a) =>
            m.Name == nameof(IUserAgentService.VerifyAgent) ? (true, false) : (false, null));
        HGFriendsServerPostHandler handler = new(StubProxy.Create<IHGFriendsService>(), agents, null);

        MethodInfo process = typeof(HGFriendsServerPostHandler)
            .GetMethod("ProcessRequest", BindingFlags.Instance | BindingFlags.NonPublic);
        using MemoryStream body = new(Encoding.UTF8.GetBytes(
            $"METHOD=getfriendperms&KEY={presentedKey}&SESSIONID={sessionID}&PRINCIPALID={UUID.Random()}&FRIEND={UUID.Random()}"));

        using LogCapture log = new();
        process.Invoke(handler, new object[] { "/hgfriends", body, null, null });

        Xunit.Assert.False(log.Contains(presentedKey), "presented key reached the log");
        Xunit.Assert.Equal(LogLevel.Warning, log.LevelOf("did not match existing key"));
        Xunit.Assert.True(log.Contains(sessionID.ToString()));
    }
}

[CollectionDefinition("LogCapture", DisableParallelization = true)]
public class LogCaptureCollection
{
}
