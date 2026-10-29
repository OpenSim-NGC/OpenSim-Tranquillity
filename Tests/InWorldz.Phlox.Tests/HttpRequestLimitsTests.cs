/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llHTTPRequest's back-pressure and header rules.
/// <para>Back-pressure, under [InWorldz.Phlox] HttpInFlightThrottle: Halcyon's llHTTPRequest sleeps up to 50 ms when the
/// script's event queue is 60% or more full, and 80 ms after any request that returns NULL_KEY (LSLSystemAPI.cs
/// :13757-13774, :13856). A throttled request also shouts on DEBUG_CHANNEL unless the script set HTTP_VERBOSE_THROTTLE
/// to FALSE (SL wiki llHTTPRequest: "If TRUE, shout error messages to DEBUG_CHANNEL if the outgoing request rate
/// exceeds the server limit", default TRUE).</para>
/// <para>Headers, as YEngine's llHTTPRequest refuses them (LSL_Api.HttpForbiddenHeaders): a name in the fatal half of its
/// table, or starting "proxy-" or "sec-", stops the request with YEngine's error; a name in the other half is left out
/// silently; at most 8 custom headers; a name and value of at most 253 characters together.</para>
/// </summary>
// No test reaches a network service: the HTTP module is an in-memory fake. No process-wide state: the class runs in parallel.
public class HttpRequestLimitsTests
{
    private const int HTTP_VERBOSE_THROTTLE = 4, HTTP_CUSTOM_HEADER = 5;
    private const string Url = "http://example.org/w1";

    private sealed class Http
    {
        public bool Throttled;
        public bool Refuse;
        public readonly ConcurrentQueue<Dictionary<string, string>> Started = new();

        public IHttpRequestModule Module => Fake<IHttpRequestModule>.Create((m, a) => m.Name switch
        {
            nameof(IHttpRequestModule.CheckThrottle) => !Throttled,
            nameof(IHttpRequestModule.CheckAllowed) => true,
            nameof(IHttpRequestModule.StartHttpRequest) => Start((Dictionary<string, string>)a[4]),
            _ => null,
        });

        private object Start(Dictionary<string, string> headers)
        {
            Started.Enqueue(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
            return Refuse ? UUID.Zero : UUID.Random();
        }
    }

    private static (ApiCallRig R, Http Http) Rig(bool inFlightThrottle = true)
    {
        var r = new ApiCallRig(cfg => { if (!inFlightThrottle) cfg.Configs["InWorldz.Phlox"].Set("HttpInFlightThrottle", "false"); });
        var http = new Http();
        r.H.Scene.RegisterModuleInterface(http.Module);
        return (r, http);
    }

    private static (int Ms, string Ret) Request(ApiCallRig r, params object[] options)
    {
        var (ms, ret) = r.Accounted(api => api.llHTTPRequest(Url, ApiCallRig.L(options), ""));
        return (ms, (string)ret);
    }

    // ---------------------------------------------------------------- back-pressure

    [Fact]
    public void AThrottledRequestSleeps80MsAndShouts()
    {
        var (r, http) = Rig();
        using (r)
        {
            http.Throttled = true;
            var (ms, ret) = Request(r);
            Assert.Equal(UUID.Zero.ToString(), ret);
            Assert.Equal(80, ms);
            Assert.Single(r.Errors, e => e.Contains("llHTTPRequest") && e.Contains("throttle"));
        }
    }

    [Fact]
    public void VerboseThrottleFalseKeepsItQuiet()
    {
        var (r, http) = Rig();
        using (r)
        {
            http.Throttled = true;
            var (ms, _) = Request(r, HTTP_VERBOSE_THROTTLE, 0);
            Assert.Equal(80, ms);
            Assert.Empty(r.Errors);
        }
    }

    [Fact]
    public void ARequestTheModuleRefusesSleeps80Ms()
    {
        var (r, http) = Rig();
        using (r)
        {
            http.Refuse = true;
            var (ms, ret) = Request(r);
            Assert.Equal(UUID.Zero.ToString(), ret);
            Assert.Equal(80, ms);
        }
    }

    [Fact]
    public void AFullEventQueueSlowsTheCaller()
    {
        var (r, _) = Rig();
        using (r)
        {
            for (int i = 0; i < InWorldz.Phlox.VM.RuntimeState.MAX_EVENT_QUEUE_SIZE; i++) r.H.QueueEventOnScriptState(r.Item);
            var (ms, ret) = Request(r);
            Assert.NotEqual(UUID.Zero.ToString(), ret);
            Assert.Equal(50, ms);
        }
    }

    [Fact]
    public void AnAcceptedRequestOnAQuietScriptDoesNotSleep()
    {
        var (r, _) = Rig();
        using (r)
        {
            var (ms, ret) = Request(r);
            Assert.NotEqual(UUID.Zero.ToString(), ret);
            Assert.Equal(0, ms);
        }
    }

    [Fact]
    public void WithTheSwitchOffThereIsNoSleepButStillTheShout()
    {
        var (r, http) = Rig(inFlightThrottle: false);
        using (r)
        {
            http.Throttled = true;
            var (ms, _) = Request(r);
            Assert.Equal(0, ms);
            Assert.Single(r.Errors, e => e.Contains("throttle"));
            for (int i = 0; i < InWorldz.Phlox.VM.RuntimeState.MAX_EVENT_QUEUE_SIZE; i++) r.H.QueueEventOnScriptState(r.Item);
            http.Throttled = false;
            Assert.Equal(0, Request(r).Ms);
        }
    }

    // ---------------------------------------------------------------- headers

    [Theory]
    [InlineData("Host")]
    [InlineData("user-agent")]
    [InlineData("Referer")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Sec-Fetch-Mode")]
    public void AForbiddenHeaderStopsTheRequestWithYEnginesError(string name)
    {
        var (r, http) = Rig();
        using (r)
        {
            var (_, ret) = Request(r, HTTP_CUSTOM_HEADER, name, "v");
            Assert.Equal("", ret);
            Assert.Empty(http.Started);
            Assert.Contains("llHTTPRequest: Name is invalid as a custom header at parameter 1", r.Errors);
        }
    }

    [Theory]
    [InlineData("Cookie")]
    [InlineData("Connection")]
    [InlineData("Accept-Encoding")]
    public void AnIgnoredHeaderIsLeftOutSilently(string name)
    {
        var (r, http) = Rig();
        using (r)
        {
            Request(r, HTTP_CUSTOM_HEADER, name, "v", HTTP_CUSTOM_HEADER, "X-Mine", "1");
            Assert.True(http.Started.TryDequeue(out var sent));
            Assert.False(sent.ContainsKey(name));
            Assert.Equal("1", sent["X-Mine"]);
            Assert.Empty(r.Errors);
        }
    }

    [Fact]
    public void AtMostEightCustomHeaders()
    {
        var (r, http) = Rig();
        using (r)
        {
            var options = Enumerable.Range(1, 9).SelectMany(i => new object[] { HTTP_CUSTOM_HEADER, "X-H" + i, "v" }).ToArray();
            Request(r, options);
            Assert.True(http.Started.TryDequeue(out var sent));
            Assert.Equal(8, sent.Keys.Count(k => k.StartsWith("X-H")));
            Assert.False(sent.ContainsKey("X-H9"));
            Assert.Contains("llHTTPRequest: Max number of custom headers is 8, excess ignored", r.Errors);
        }
    }

    [Fact]
    public void ANameAndValueOver253CharactersStopTheRequest()
    {
        var (r, http) = Rig();
        using (r)
        {
            Request(r, HTTP_CUSTOM_HEADER, "X-Long", new string('v', 247));   // 6 + 247 = 253: fine
            Assert.Single(http.Started);
            var (_, ret) = Request(r, HTTP_CUSTOM_HEADER, "X-Long", new string('v', 248));
            Assert.Equal("", ret);
            Assert.Single(http.Started);
            Assert.Contains("llHTTPRequest: name and value length exceds 253 characters for custom header at parameter 1", r.Errors);
        }
    }
}
