/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using System.Reflection;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.ApplicationPlugins.RemoteController;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Remote admin hands the caller a short-lived authentication token. The caller needs it; the log does not.
/// </summary>
[Collection("LogCapture")]
public class RemoteAdminCredentialLogTests
{
    [Xunit.Fact]
    public void AuthenticateUserLogsTheOutcomeButNeitherPasswordNorToken()
    {
        string password = "fake-account-password-" + Guid.NewGuid().ToString("N");
        SceneHelpers sh = new();
        Scene scene = sh.SetupScene();
        UserAccountHelpers.CreateUserWithInventory(scene, "Test", "User", UUID.Random(), password);
        // The authentication service compares against the MD5 of the password, which is what a login presents.
        string passwordHash = OpenSim.Framework.Util.Md5Hash(password);

        IOpenSimBase application = StubProxy.Create<IOpenSimBase>((m, a) =>
            m.Name == "get_SceneManager" ? (true, sh.SceneManager) : (false, null));
        RemoteAdminPlugin plugin = UninitializedService.Create<RemoteAdminPlugin>(
            ("m_application", application));

        XmlRpcRequest request = new();
        request.Params.Add(new Hashtable
        {
            ["user_firstname"] = "Test",
            ["user_lastname"] = "User",
            ["user_password"] = passwordHash,
            ["token_lifetime"] = "20"
        });
        XmlRpcResponse response = new() { Value = new Hashtable() };
        MethodInfo method = typeof(RemoteAdminPlugin)
            .GetMethod("XmlRpcAuthenticateUserMethod", BindingFlags.Instance | BindingFlags.NonPublic);

        using LogCapture log = new();
        method.Invoke(plugin, new object[] { request, response, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9000) });

        Hashtable result = (Hashtable)response.Value;
        Xunit.Assert.Equal(true, result["success"]);
        string token = (string)result["token"];
        Xunit.Assert.False(string.IsNullOrEmpty(token), "the caller must still receive the token");

        Xunit.Assert.False(log.Contains(token), "token reached the log");
        Xunit.Assert.False(log.Contains(password), "password reached the log");
        Xunit.Assert.False(log.Contains(passwordHash), "password hash reached the log");
        Xunit.Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, log.LevelOf("identified, token issued"));
    }
}
