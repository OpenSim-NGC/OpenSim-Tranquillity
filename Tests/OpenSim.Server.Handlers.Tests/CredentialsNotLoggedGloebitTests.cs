/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using System.Reflection;
using Gloebit.GloebitMoneyModule;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The Gloebit money module logs its callbacks to help an operator follow the authorization and transaction
/// flows. Each of these drives the entry point with fake credentials. The code after the log line needs a
/// configured Gloebit account store, so the calls are allowed to fail once they have logged.
/// </summary>
[Collection("LogCapture")]
public class GloebitCredentialLogTests
{
    private static string FakeSecret(string kind) => kind + "-" + Guid.NewGuid().ToString("N");

    private static void IgnoreFailureAfterLogging(Action call)
    {
        try { call(); }
        catch (Exception) { }
    }

    [Xunit.Fact]
    public void AuthorizationCallbackDoesNotLogTheCodeOrState()
    {
        string code = FakeSecret("fake-authorization-code");
        string state = FakeSecret("fake-authorization-state");
        GloebitAPIWrapper wrapper = UninitializedService.Create<GloebitAPIWrapper>();
        Hashtable request = new()
        {
            ["agentId"] = UUID.Random().ToString(),
            ["code"] = code,
            ["state"] = state
        };

        using LogCapture log = new();
        IgnoreFailureAfterLogging(() => wrapper.authComplete_func(request));

        Xunit.Assert.False(log.Contains(code), "authorization code reached the log");
        Xunit.Assert.False(log.Contains(state), "authorization state reached the log");
        Xunit.Assert.True(log.Contains("agentId: " + request["agentId"]), "the other parameters are still logged");
    }

    [Xunit.Fact]
    public void TransactionCallbackDoesNotLogTheCallbackKey()
    {
        string key = FakeSecret("fake-callback-key");
        GloebitAPIWrapper wrapper = UninitializedService.Create<GloebitAPIWrapper>();
        Hashtable request = new()
        {
            ["id"] = UUID.Random().ToString(),
            ["state"] = "enact",
            ["key"] = key
        };

        using LogCapture log = new();
        IgnoreFailureAfterLogging(() => wrapper.transactionState_func(request));

        Xunit.Assert.False(log.Contains(key), "callback key reached the log");
        Xunit.Assert.True(log.Contains("state: enact"), "the other parameters are still logged");
    }

    [Xunit.Fact]
    public void BuyRequestDoesNotLogTheSecureSessionId()
    {
        string secureSession = UUID.Random().ToString();
        GloebitMoneyModule module = UninitializedService.Create<GloebitMoneyModule>();
        XmlRpcRequest request = new();
        request.Params.Add(new Hashtable
        {
            ["agentId"] = UUID.Random().ToString(),
            ["confirm"] = "confirm-code",
            ["currencyBuy"] = 100,
            ["estimatedCost"] = 10,
            ["secureSessionId"] = secureSession
        });
        MethodInfo buy = typeof(GloebitMoneyModule).GetMethod("buy_func", BindingFlags.Instance | BindingFlags.NonPublic);

        using LogCapture log = new();
        IgnoreFailureAfterLogging(() =>
            buy.Invoke(module, new object[] { request, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9000) }));

        Xunit.Assert.False(log.Contains(secureSession), "secure session id reached the log");
        Xunit.Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, log.LevelOf("buy_func agentId"));
    }
}
