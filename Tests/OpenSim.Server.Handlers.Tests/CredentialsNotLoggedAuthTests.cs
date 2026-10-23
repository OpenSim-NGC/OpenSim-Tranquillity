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

public partial class CredentialsNotLoggedTests
{
    [Xunit.Fact]
    public void WebkeyAuthenticationFailureDoesNotLogEitherKey()
    {
        UUID principalID = UUID.Random();
        string stored = UUID.Random().ToString();
        string presented = UUID.Random().ToString();

        IAuthenticationData database = StubProxy.Create<IAuthenticationData>((m, a) =>
            m.Name == nameof(IAuthenticationData.Get)
                ? (true, new AuthenticationData
                {
                    PrincipalID = principalID,
                    Data = new Dictionary<string, object> { ["webLoginKey"] = stored }
                })
                : (false, null));
        WebkeyAuthenticationService service =
            UninitializedService.Create<WebkeyAuthenticationService>(("m_Database", database));

        using LogCapture log = new();
        Xunit.Assert.True(string.IsNullOrEmpty(service.Authenticate(principalID, presented, 30)));

        Xunit.Assert.False(log.Contains(stored), "stored key reached the log");
        Xunit.Assert.False(log.Contains(presented), "presented key reached the log");
        Xunit.Assert.Equal(LogLevel.Debug, log.LevelOf("web login auth failed"));
    }

    [Xunit.Fact]
    public void PresenceLoginDoesNotLogSecureSessionId()
    {
        UUID sessionID = UUID.Random();
        UUID secureSessionID = UUID.Random();
        string userID = UUID.Random().ToString();
        IPresenceData database = StubProxy.Create<IPresenceData>((m, a) =>
            m.Name == nameof(IPresenceData.Store) ? (true, true) : (false, null));
        OpenSim.Services.PresenceService.PresenceService service =
            UninitializedService.Create<OpenSim.Services.PresenceService.PresenceService>(("m_Database", database));

        using LogCapture log = new();
        Xunit.Assert.True(service.LoginAgent(userID, sessionID, secureSessionID));

        Xunit.Assert.False(log.Contains(secureSessionID.ToString()), "secure session id reached the log");
        Xunit.Assert.Equal(LogLevel.Debug, log.LevelOf("LoginAgent: session " + sessionID));
    }
}
