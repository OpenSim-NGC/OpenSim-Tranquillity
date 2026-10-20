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
    [Xunit.Theory]
    [Xunit.InlineData("fake-groups-secret-key", "set")]
    [Xunit.InlineData("", "not set")]
    public void GroupsConnectorLogsWhetherKeyIsSetNotTheKey(string key, string expected)
    {
        string secret = key.Length > 0 ? FakeSecret(key) : key;
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Groups").Set("SecretKey", secret);

        using LogCapture log = new();
        // The service behind the connector needs a database plugin that this config does not name, so the
        // constructor throws after it has logged; the log line is what is under test.
        Xunit.Assert.ThrowsAny<Exception>(() =>
            new GroupsServiceRobustConnector(config, StubProxy.Create<IHttpServer>(), "Groups"));

        if (secret.Length > 0)
            Xunit.Assert.False(log.Contains(secret), "secret key reached the log");
        Xunit.Assert.Equal(LogLevel.Debug, log.LevelOf("Starting with secret key " + expected));
    }

    [Xunit.Fact]
    public void HGGroupsServiceDoesNotLogEitherAccessToken()
    {
        string stored = FakeSecret("stored-group-token");
        string presented = FakeSecret("presented-group-token");
        UUID groupID = UUID.Random();
        string agentID = UUID.Random().ToString();

        IGroupsData database = StubProxy.Create<IGroupsData>((m, a) =>
            m.Name == nameof(IGroupsData.RetrieveMember)
                ? (true, new MembershipData
                {
                    GroupID = groupID,
                    PrincipalID = agentID,
                    Data = new Dictionary<string, string> { ["AccessToken"] = stored }
                })
                : (false, null));
        HGGroupsService service = UninitializedService.Create<HGGroupsService>(("m_Database", database));

        using LogCapture log = new();
        // RemoveAgentFromGroup checks the token itself; GetGroupRecord goes through the private VerifyToken.
        Xunit.Assert.False(service.RemoveAgentFromGroup(agentID, agentID, groupID, presented));
        Xunit.Assert.Null(service.GetGroupRecord(agentID, groupID, "Example Group", presented));

        Xunit.Assert.False(log.Contains(stored), "stored token reached the log");
        Xunit.Assert.False(log.Contains(presented), "presented token reached the log");
        Xunit.Assert.Equal(2, log.Messages.Count(m => m.Contains("did not match the stored one")));
    }
}
