/*
 * Copyright (c) 2026, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml;
using Nini.Config;
using OpenMetaverse;
using OpenSim.ConsoleClient;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers.HttpServer;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The remote console refuses a session id it does not hold: CloseSession and ReadResponses for such an id answer
/// 404, as SessionCommand already did, and a closed session's ReadResponses is no longer served. The console client
/// shows every line the server sends, with its own level, and takes the server's prompt; it used to split each line
/// on ':' into three fields and drop the rest. When it connects to a console waiting at its prompt with a log line said
/// after it, it shows that line and keeps the prompt StartSession named.
/// </summary>
[Collection(MainConsoleCollection.Name)]
public class RemoteConsoleSessionTests : IDisposable
{
    private const string User = "console-user";
    private const string Pass = "console-pass";
    private const string ConsolePrompt = "Region (root) # ";

    private readonly BaseHttpServer m_server;
    private readonly uint m_port;
    private readonly TestRemoteConsole m_console;
    private readonly ICommandConsole m_previous = MainConsole.Instance;

    /// <summary>A RemoteConsole whose ReadLine the test can let go without input.</summary>
    private sealed class TestRemoteConsole : RemoteConsole
    {
        public TestRemoteConsole() : base(ConsolePrompt) { }

        public void Release() => m_DataEvent.Set();
    }

    public RemoteConsoleSessionTests()
    {
        m_port = FreePort();
        m_server = new BaseHttpServer(m_port);

        IniConfigSource config = new();
        IConfig network = config.AddConfig("Network");
        network.Set("ConsoleUser", User);
        network.Set("ConsolePass", Pass);

        m_console = new TestRemoteConsole();
        m_console.ReadConfig(config);
        m_console.SetServer(m_server);
        MainConsole.Instance = m_console;

        m_server.Start(false, true);
    }

    public void Dispose()
    {
        m_server.Stop();
        // Never put back null: other test classes (GridUserRegionLogoutTests) set a console only when there is none.
        MainConsole.Instance = m_previous ?? new MockConsole();
    }

    private static uint FreePort()
    {
        TcpListener free = new(IPAddress.Loopback, 0);
        free.Start();
        uint port = (uint)((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        return port;
    }

    private (HttpStatusCode Status, string Body) Post(string path, string form)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        using HttpRequestMessage message = new(HttpMethod.Post, $"http://127.0.0.1:{m_port}{path}")
        {
            Content = new StringContent(form, Encoding.ASCII, "application/x-www-form-urlencoded"),
        };
        using HttpResponseMessage response = client.Send(message);
        return (response.StatusCode, response.Content.ReadAsStringAsync().Result);
    }

    private UUID StartSession() => StartSession(out _);

    private UUID StartSession(out string body)
    {
        (HttpStatusCode status, body) = Post("/StartSession/", $"USER={User}&PASS={Pass}");
        Assert.Equal(HttpStatusCode.OK, status);
        XmlDocument doc = new();
        doc.LoadXml(body);
        XmlNode? session = doc.GetElementsByTagName("SessionID")[0];
        Assert.NotNull(session);
        return UUID.Parse(session.InnerText);
    }

    [Fact]
    public void ClosingAnUnknownSessionIsRefused()
    {
        var (status, body) = Post("/CloseSession/", "ID=" + UUID.Random());

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.DoesNotContain("OK", body);
    }

    [Fact]
    public void ReadingAnUnknownSessionIsRefused()
    {
        var (status, _) = Post("/ReadResponses/" + UUID.Random() + "/", string.Empty);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public void ALiveSessionIsStillRead()
    {
        UUID id = StartSession();
        MainConsole.Instance.Output("a line for the client");

        var (status, body) = Post("/ReadResponses/" + id + "/", string.Empty);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("a line for the client", body);
    }

    [Fact]
    public void AClosedSessionIsClosedOnceAndNoLongerRead()
    {
        UUID id = StartSession();

        var (first, body) = Post("/CloseSession/", "ID=" + id);
        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Contains("<Result>OK</Result>", body);

        Assert.Equal(HttpStatusCode.NotFound, Post("/CloseSession/", "ID=" + id).Status);
        Assert.Equal(HttpStatusCode.NotFound, Post("/ReadResponses/" + id + "/", string.Empty).Status);
    }

    /// <summary>Records what the client shows and the prompt it is asked to read with.</summary>
    private sealed class RecordingConsole : CommandConsole
    {
        public readonly List<string> Shown = new();
        public readonly List<string> Levels = new();
        public readonly List<string> Prompts = new();

        public RecordingConsole() : base("Client> ") { }

        public override void Output(string format) => Output(format, null!);

        public override void Output(string format, params object[] components)
        {
            string level = string.Empty;
            if (components != null && components.Length > 0 && components[0] is ConsoleLevel cl)
            {
                level = cl.ToString();
                components = components[1..];
            }
            Shown.Add(components == null || components.Length == 0 ? format : string.Format(format, components));
            Levels.Add(level);
        }

        public override string ReadLine(string p, bool isCommand, bool e)
        {
            Prompts.Add(p);
            return string.Empty;
        }
    }

    /// <summary>
    /// Hands a ReadResponses reply to the client. Its next poll goes to a closed port and fails on its own; the
    /// recording console is still in place while it does, and the failure only writes to stderr.
    /// </summary>
    private static RecordingConsole ClientReads(string reply)
    {
        RecordingConsole console = new();
        MainConsole.Instance = console;
        OpenSimConsoleClient.ReadResponses($"http://127.0.0.1:{FreePort()}/ReadResponses/x/", string.Empty, reply);
        return console;
    }

    [Fact]
    public void TheClientShowsEveryLineTheServerSends()
    {
        // As RemoteConsole.GetEvents writes them: the text as said, the level and the prompt flag as attributes.
        RecordingConsole console = ClientReads(
            "<?xml version=\"1.0\"?><ConsoleSession>" +
            "<Line Number=\"1\" Level=\"\" Prompt=\"false\" Command=\"false\" Input=\"false\">Version: Tranquillity 1.3</Line>" +
            "<Line Number=\"2\" Level=\"\" Prompt=\"false\" Command=\"false\" Input=\"false\">no colon here</Line>" +
            "<Line Number=\"3\" Level=\"error\" Prompt=\"false\" Command=\"false\" Input=\"false\">12:00:01 - [MAIN]: a: b: c {0}</Line>" +
            "<Line Number=\"4\" Level=\"\" Prompt=\"true\" Command=\"true\" Input=\"false\">Region (root) # </Line>" +
            "</ConsoleSession>");

        Assert.Equal(new[] { "Version: Tranquillity 1.3", "no colon here", "12:00:01 - [MAIN]: a: b: c {0}" }, console.Shown);
        Assert.Equal(new[] { "", "", "error" }, console.Levels);
        Assert.Equal(new[] { "Region (root) # " }, console.Prompts);
    }

    [Fact]
    public void TheClientStillReadsTheOlderNumberLevelTextForm()
    {
        RecordingConsole console = ClientReads(
            "<?xml version=\"1.0\"?><ConsoleSession>" +
            "<Line>7:info:hello: world</Line>" +
            "<Line>just text</Line>" +
            "<Line>8::+++Region # </Line>" +
            "</ConsoleSession>");

        Assert.Equal(new[] { "hello: world", "just text" }, console.Shown);
        Assert.Equal(new[] { "info", "" }, console.Levels);
        Assert.Equal(new[] { "+++Region # " }, console.Prompts);
    }

    [Fact]
    public void AClientConnectingAfterALogLineShowsItAndKeepsThePrompt()
    {
        // A region's console waits at its prompt while the log goes on, so the last line of the scrollback a new
        // session is sent is a log line, and GetEvents marks it Prompt="true" (the console is waiting for input).
        using ManualResetEventSlim prompted = new();
        m_console.OnOutput += line => { if (line == ConsolePrompt.Trim()) prompted.Set(); };
        Thread reader = new(() => m_console.ReadLine(ConsolePrompt, true, true)) { IsBackground = true };
        reader.Start();
        string login;
        string lines;
        try
        {
            Assert.True(prompted.Wait(TimeSpan.FromSeconds(5)));
            m_console.Output("[SCENE]: a log line after the prompt");

            UUID id = StartSession(out login);
            HttpStatusCode status;
            (status, lines) = Post("/ReadResponses/" + id + "/", string.Empty);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("Prompt=\"true\"", lines);
        }
        finally
        {
            m_console.Release();
            reader.Join(TimeSpan.FromSeconds(5));
        }

        RecordingConsole console = new();
        MainConsole.Instance = console;
        // LoginReply's own first poll goes to the client's unset host and fails on its own (stderr only).
        OpenSimConsoleClient.LoginReply(string.Empty, string.Empty, login);
        OpenSimConsoleClient.ReadResponses($"http://127.0.0.1:{FreePort()}/ReadResponses/x/", string.Empty, lines);

        Assert.Equal(new[] { ConsolePrompt.Trim(), "[SCENE]: a log line after the prompt" }, console.Shown);
        Assert.Equal(new[] { ConsolePrompt }, console.Prompts);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public class MainConsoleCollection
{
    public const string Name = "MainConsole";
}
