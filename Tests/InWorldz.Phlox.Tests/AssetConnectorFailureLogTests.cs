/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What the asset connector's REST request (SynchronousRestObjectRequester.MakeGetRequest, the call
/// AssetServicesConnector.Get makes) returns and logs when the asset server has no asset to give, against a listener on
/// the loopback address: a 404 returns null and logs nothing; a request that is not answered in time returns null and
/// logs at debug level only; an empty answer returns null and logs at debug level only; an answer sent without a
/// Content-Length (chunked) is still read. A script engine that asks for a notecard therefore gets a plain null for
/// each of these and no log line from the connector at the default level.
/// The log is captured through LoggerProvider, process-wide, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class AssetConnectorFailureLogTests : IDisposable
{
    private readonly Capture m_log = new();
    private readonly ILoggerFactory m_previous;
    private readonly HttpListener m_listener = new();
    private readonly string m_prefix;
    private readonly ManualResetEventSlim m_stop = new(false);

    public AssetConnectorFailureLogTests()
    {
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = m_log;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        m_prefix = "http://127.0.0.1:" + port + "/";
        m_listener.Prefixes.Add(m_prefix);
        m_listener.Start();
    }

    public void Dispose()
    {
        m_stop.Set();
        try { m_listener.Close(); } catch { }
        LoggerProvider.LoggerFactory = m_previous;
    }

    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Text)> m_lines = new();
        public (LogLevel Level, string Text)[] Lines { get { lock (m_lines) return m_lines.ToArray(); } }
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (m_c.m_lines) m_c.m_lines.Add((logLevel, formatter(state, exception))); }
        }
    }

    /// <summary>Answer the next request with <paramref name="answer"/> on another thread.</summary>
    private void Serve(Action<HttpListenerContext> answer)
    {
        var t = new Thread(() =>
        {
            try { answer(m_listener.GetContext()); } catch { }
        }) { IsBackground = true };
        t.Start();
    }

    private static byte[] AssetXml()
    {
        var asset = new AssetBase(UUID.Random(), "card", (sbyte)AssetType.Notecard, UUID.Zero.ToString()) { Data = new byte[] { 1, 2, 3 } };
        using var ms = new MemoryStream();
        new XmlSerializer(typeof(AssetBase)).Serialize(ms, asset);
        return ms.ToArray();
    }

    private AssetBase Get(int timeoutMs = 5000)
        => SynchronousRestObjectRequester.MakeGetRequest<AssetBase>(m_prefix + "assets/" + UUID.Random(), timeoutMs, null);

    /// <summary>The request's own lines (the process's one-time clock line is not).</summary>
    private (LogLevel Level, string Text)[] Lines()
        => m_log.Lines.Where(l => l.Text.Contains("SRestObjReq") || l.Text.Contains("LOGHTTP")).ToArray();

    private LogLevel Highest()
    {
        Assert.True(Lines().Length > 0, "no line at all");
        LogLevel top = Lines().Max(l => l.Level);
        Assert.True(top == LogLevel.Debug, "the lines: [" + string.Join(" | ", Lines().Select(l => l.Level + " " + l.Text)) + "]");
        return top;
    }

    [Fact]
    public void ANotFoundReturnsNullAndLogsNothing()
    {
        Serve(c => { c.Response.StatusCode = 404; c.Response.Close(); });
        Assert.Null(Get());
        Assert.Empty(Lines());
    }

    [Fact]
    public void ARequestNotAnsweredInTimeReturnsNullAndLogsAtDebugLevelOnly()
    {
        Serve(c => { m_stop.Wait(TimeSpan.FromSeconds(20)); });
        Assert.Null(Get(timeoutMs: 300));
        Assert.True(Lines().Length > 0, "the timeout left no line at all");
        Assert.Equal(LogLevel.Debug, Highest());
    }

    [Fact]
    public void AnEmptyAnswerReturnsNullAndLogsAtDebugLevelOnly()
    {
        Serve(c => { c.Response.StatusCode = 200; c.Response.ContentLength64 = 0; c.Response.Close(); });
        Assert.Null(Get());
        Assert.Equal(LogLevel.Debug, Highest());
    }

    [Fact]
    public void AnAnswerSentChunkedWithNoContentLengthIsRead()
    {
        byte[] body = AssetXml();
        Serve(c =>
        {
            c.Response.StatusCode = 200;
            c.Response.SendChunked = true;
            c.Response.OutputStream.Write(body, 0, body.Length);
            c.Response.Close();
        });
        Assert.NotNull(Get());
    }
}
