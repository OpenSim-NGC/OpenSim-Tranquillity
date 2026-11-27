/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using System.Reflection;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Region.DataSnapshot;
using OpenSim.Region.OptionalModules.World.Currency;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Optional region modules that logged a secret they had been configured with or had just been handed.
/// </summary>
[Collection("LogCapture")]
public class OptionalModulesCredentialLogTests
{
    private static string FakeSecret(string kind) => kind + "-" + Guid.NewGuid().ToString("N");
    private static Type RequireType(Assembly assembly, string name)
        => assembly.GetType(name, throwOnError: true) ?? throw new TypeLoadException(name);
    private static FieldInfo RequireField(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo RequireMethod(Type type, string name, BindingFlags flags)
        => type.GetMethod(name, flags) ?? throw new MissingMethodException(type.FullName, name);

    [Xunit.Fact]
    public void IrcChannelConfigurationLogsWhetherAccessPasswordIsSetNotThePassword()
    {
        string password = FakeSecret("fake-irc-access-password");
        Assembly modules = typeof(DataSnapshotManager).Assembly;
        Type channelState = RequireType(modules, "OpenSim.Region.OptionalModules.Avatar.Chat.ChannelState");
        Type regionState = RequireType(modules, "OpenSim.Region.OptionalModules.Avatar.Chat.RegionState");
        object region = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(regionState);
        foreach (string name in new[] { "Region", "Host", "LocX", "LocY", "IDK" })
            RequireField(regionState, name).SetValue(region, "test");

        IConfig config = new IniConfigSource().AddConfig("IRC");
        config.Set("access_password", password);
        MethodInfo open = RequireMethod(channelState, "OpenChannel", BindingFlags.Static | BindingFlags.NonPublic);

        using LogCapture log = new();
        // The configuration names no server, so OpenChannel throws once it has logged the settings it read and
        // before it opens a connection; the log lines are what is under test.
        TargetInvocationException thrown =
            Xunit.Assert.Throws<TargetInvocationException>(() => open.Invoke(null, new object[] { region, config }));
        Xunit.Assert.Contains("server missing",
            thrown.InnerException?.Message ?? throw new InvalidOperationException("OpenChannel threw without an inner exception."));

        Xunit.Assert.False(log.Contains(password), "access password reached the log");
        Xunit.Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, log.LevelOf("AccessPassword : set"));
    }

    [Xunit.Fact]
    public void DataSnapshotNotificationDoesNotLogTheSharedSecret()
    {
        UUID secret = UUID.Random();
        DataSnapshotManager manager = UninitializedService.Create<DataSnapshotManager>(("m_Secret", secret));
        manager.m_hostname = "test.example.org";
        manager.m_listener_port = "9000";
        MethodInfo notify = RequireMethod(typeof(DataSnapshotManager), "NotifyDataServices", BindingFlags.Instance | BindingFlags.NonPublic);

        using LogCapture log = new();
        // Nothing listens on this loopback port, so the request is refused at once; the line logged after the
        // request is the same whether or not it succeeds.
        notify.Invoke(manager, new object[] { "http://127.0.0.1:9/", "snapshot" });

        Xunit.Assert.False(log.Contains(secret.ToString()), "shared secret reached the log");
        Xunit.Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, log.LevelOf("data service http://127.0.0.1:9/ notified"));
    }

    [Xunit.Fact]
    public void MoneyModuleBuyRequestDoesNotLogSecureSessionId()
    {
        string secureSession = UUID.Random().ToString();
        DTLNSLMoneyModule module = UninitializedService.Create<DTLNSLMoneyModule>();
        XmlRpcRequest request = new();
        request.Params.Add(new Hashtable
        {
            ["agentId"] = UUID.Random().ToString(),
            ["confirm"] = "confirm-code",
            ["currencyBuy"] = 100,
            ["estimatedCost"] = 10,
            ["secureSessionId"] = secureSession
        });
        MethodInfo buy = RequireMethod(typeof(DTLNSLMoneyModule), "buy_func", BindingFlags.Instance | BindingFlags.NonPublic);

        using LogCapture log = new();
        buy.Invoke(module, new object[] { request, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9000) });

        Xunit.Assert.False(log.Contains(secureSession), "secure session id reached the log");
        Xunit.Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, log.LevelOf("buy_func agentId"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("SendMoneyHandler")]
    [Xunit.InlineData("MoveMoneyHandler")]
    public void MoneyModuleScriptRequestsDoNotLogTheAccessCodeOrItsHash(string handler)
    {
        string code = FakeSecret("fake-script-access-code");
        DTLNSLMoneyModule module = UninitializedService.Create<DTLNSLMoneyModule>(("m_debug", true));
        XmlRpcRequest request = new();
        request.Params.Add(new Hashtable
        {
            ["agentUUID"] = UUID.Random().ToString(),
            ["fromUUID"] = UUID.Random().ToString(),
            ["toUUID"] = UUID.Random().ToString(),
            ["amount"] = 5,
            ["secretAccessCode"] = code
        });
        IPEndPoint script = new(IPAddress.Parse("192.0.2.7"), 9000);
        string hash = BitConverter.ToString(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.ASCII.GetBytes(code + "_" + script.Address)))
            .ToLower().Replace("-", "");
        MethodInfo method = RequireMethod(typeof(DTLNSLMoneyModule), handler, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        using LogCapture log = new();
        method.Invoke(module, new object[] { request, script });

        Xunit.Assert.False(log.Contains(code), "access code reached the log");
        Xunit.Assert.False(log.Contains(hash), "access code hash reached the log");
        Xunit.Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, log.LevelOf("access code received from script at 192.0.2.7"));
    }
}
