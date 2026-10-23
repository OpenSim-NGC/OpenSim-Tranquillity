/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in integration tests (see StockJanusRegionTests.cs): the voice connector programs of the slvoice mixer's
 * repository (the Python recorder and injector in its connectors directory) against a gateway running the mixer with a
 * join-capability secret and JS_JOIN_CAP_REQUIRED=1.
 *
 * Besides the settings every opt-in test needs, they read
 *   WEBRTC_JANUS_TEST_JOIN_CAP_SECRET   the mixer's JS_JOIN_CAP_SECRET
 *   WEBRTC_JANUS_TEST_PYTHON            a Python 3.12 with aiortc and aiohttp
 *   WEBRTC_JANUS_TEST_CONNECTORS_DIR    the connectors directory of the mixer's repository
 * and skip without them. They create rooms on the gateway, so point them at a scratch mixer.
 *
 * The region side is real: the room is made by the voice service's EnsureSpatialRoom (so it is declared by the sim),
 * and the capability the programs join with is minted by the connector module's own endpoint, served on a local port.
 *
 *   - The recorder refuses to start without RECORDING_OPT_IN=yes.
 *   - The recorder, and the injector, join the room with a capability the region minted and appear in it.
 *   - A program whose capability was signed with another key is not admitted.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
#pragma warning disable CS0618
    [TestFixture, NUnit.Framework.Timeout(240000)]
#pragma warning restore CS0618
    [NeedsPlugin(JanusPluginKind.Mixer)]
    public class StockJanusConnectorTests
    {
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);

        private readonly RawJanus _janus = new();
        private string _joinCapSecret;
        private string _connectors;

        [OneTimeSetUp]
        public void RequireGatewayPythonAndConnectors()
        {
            StockJanusEnvironment.IgnoreUnlessConfigured();
            _joinCapSecret = StockJanusEnvironment.Get("WEBRTC_JANUS_TEST_JOIN_CAP_SECRET");
            if (string.IsNullOrEmpty(_joinCapSecret))
                Assert.Ignore("WEBRTC_JANUS_TEST_JOIN_CAP_SECRET is not set: it is the mixer's JS_JOIN_CAP_SECRET");
            if (string.IsNullOrEmpty(StockJanusEnvironment.Python))
                Assert.Ignore("WEBRTC_JANUS_TEST_PYTHON is not set");
            _connectors = StockJanusEnvironment.Get("WEBRTC_JANUS_TEST_CONNECTORS_DIR");
            if (string.IsNullOrEmpty(_connectors) || !File.Exists(Path.Combine(_connectors, "recorder", "recorder.py")))
                Assert.Ignore("WEBRTC_JANUS_TEST_CONNECTORS_DIR is not set to the mixer repository's connectors directory");
        }

        /// <summary>A connector program started as a child process. Its output is kept, never the environment it was given.</summary>
        private sealed class Program : IDisposable
        {
            private readonly Process _process;
            private readonly ConcurrentQueue<string> _lines = new();

            public Program(string python, string connectors, string module, IDictionary<string, string> env)
            {
                var info = new ProcessStartInfo(python)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetTempPath(),
                };
                info.ArgumentList.Add("-u");
                info.ArgumentList.Add("-m");
                info.ArgumentList.Add(module);
                info.Environment["PYTHONPATH"] = connectors;   // the programs import their shared "common" package
                info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                foreach (var kv in env)
                    info.Environment[kv.Key] = kv.Value;
                _process = Process.Start(info);
                _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _lines.Enqueue(e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _lines.Enqueue(e.Data); };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }

            public bool HasExited => _process.HasExited;
            public int ExitCode => _process.ExitCode;
            public bool WaitForExit(TimeSpan t) => _process.WaitForExit((int)t.TotalMilliseconds);
            public string Output => string.Join("\n", _lines);

            public void Dispose()
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit(10000);
                    }
                }
                catch (InvalidOperationException) { }
                _process.Dispose();
            }
        }

        /// <summary>The connector module's endpoint, served on a local port the way the region's HTTP server serves it.</summary>
        private sealed class EndpointHost : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly VoiceConnectorJoinCapEndpoint _endpoint;
            private int _fetches;
            private readonly ConcurrentQueue<int> _statuses = new();

            public string Url { get; }
            public int Fetches => Volatile.Read(ref _fetches);
            public IReadOnlyCollection<int> Statuses => _statuses.ToArray();

            public EndpointHost(string name, VoiceConnectorRecord record, string mintSecret, Func<int?> ensure)
            {
                var tcp = new TcpListener(IPAddress.Loopback, 0);
                tcp.Start();
                int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                tcp.Stop();
                string prefix = $"http://127.0.0.1:{port}/";
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                Url = $"{prefix}voice/connector/{name}/join-cap";
                _endpoint = new VoiceConnectorJoinCapEndpoint(new BaseHttpServer(9), LoggerProvider.CreateLogger("test.connector"));
                _endpoint.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { record }, true, mintSecret,
                    r => ensure()));
                _ = Task.Run(Serve);
            }

            private async Task Serve()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    VoiceConnectorJoinCapEndpoint.Response r = _endpoint.Handle(c.Request.HttpMethod, c.Request.Url.AbsolutePath,
                        c.Request.Headers["Authorization"]);
                    Interlocked.Increment(ref _fetches);
                    _statuses.Enqueue(r.Status);
                    c.Response.StatusCode = r.Status;
                    if (r.ContentType is not null)
                        c.Response.ContentType = r.ContentType;
                    c.Response.OutputStream.Write(r.Body, 0, r.Body.Length);
                    c.Response.Close();
                }
            }

            public void Dispose() => _listener.Close();
        }

        private Dictionary<string, string> Env(string display, int room, string capUrl, string capSecret, bool optIn)
        {
            var env = new Dictionary<string, string>
            {
                ["JANUS_URL"] = StockJanusEnvironment.Url,
                ["JANUS_API_SECRET"] = StockJanusEnvironment.ApiSecret,
                ["ROOM"] = room.ToString(),
                ["DISPLAY"] = display,
                ["CONNECTOR_CAP_URL"] = capUrl,
                ["CONNECTOR_CAP_SECRET"] = capSecret,
                ["OUT_DIR"] = Path.Combine(Path.GetTempPath(), "connector-out-" + Guid.NewGuid().ToString("N")),
                ["LOG_LEVEL"] = "INFO",
            };
            if (optIn)
                env["RECORDING_OPT_IN"] = "yes";
            return env;
        }

        private static VoiceConnectorRecord Record(string name, string capabilitySecret, int room)
        {
            var r = new VoiceConnectorRecord(name, true, name, "NPC", new Vector3(128, 128, 25),
                VoiceConnectorScope.Estate, false, "Operator", null, capabilitySecret);
            r.NpcId = UUID.Random();
            r.ViewerSessionId = Guid.NewGuid().ToString();
            r.Room = room;
            return r;
        }

        private void CapabilityOn(Nini.Config.IConfig voice, Nini.Config.IConfig janus)
        {
            voice.Set("JoinCapabilityEnabled", "true");
            janus.Set("JoinCapabilitySecret", _joinCapSecret);
        }

        private static string NewSecret() => Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());

        [Test]
        public void TheRecorder_WithoutTheOptIn_RefusesToStart()
        {
            using var recorder = new Program(StockJanusEnvironment.Python, _connectors, "recorder.recorder",
                Env(UUID.Random().ToString(), 1, null, null, optIn: false));

            Assert.That(recorder.WaitForExit(TimeSpan.FromSeconds(30)), Is.True, "it exits: " + recorder.Output);

            Assert.That(recorder.ExitCode, Is.Not.EqualTo(0));
            Assert.That(recorder.Output, Does.Contain("RECORDING_OPT_IN=<unset>").And.Contain("FATAL"));
        }

        [TestCase("recorder.recorder", true)]
        [TestCase("injector.injector", false)]
        public async Task AProgramJoinsWithACapabilityTheRegionMinted(string module, bool recording)
        {
            using var region = StockRegion.Create(CapabilityOn);
            UUID regionId = region.Scene.RegionInfo.RegionID;
            int? room = region.Service.EnsureSpatialRoom(regionId, JanusAudioBridge.REGION_ROOM_ID);
            Assert.That(room, Is.Not.Null, "the region made the room");
            Assert.That(await _janus.RoomExistsAsync(room.Value), Is.True, "the mixer holds it, with no avatar in it");

            string secret = NewSecret();
            VoiceConnectorRecord record = Record("Prog", secret, room.Value);
            using var host = new EndpointHost("Prog", record, _joinCapSecret,
                () => region.Service.EnsureSpatialRoom(regionId, JanusAudioBridge.REGION_ROOM_ID));
            using var program = new Program(StockJanusEnvironment.Python, _connectors, module,
                Env(record.NpcId.ToString(), room.Value, host.Url, secret, optIn: recording));

            bool joined = await _janus.WaitForParticipantsAsync(room.Value, 1, Settle);

            Assert.That(joined, Is.True, "the program is in the room. Output: " + program.Output);
            Assert.That(program.HasExited, Is.False, program.Output);
            Assert.That(host.Fetches, Is.GreaterThanOrEqualTo(1), "it fetched a capability from the region");
            Assert.That(host.Statuses, Has.All.EqualTo(200));
        }

        [Test]
        public async Task AProgramWhoseCapabilityWasSignedWithAnotherKey_IsNotAdmitted()
        {
            using var region = StockRegion.Create(CapabilityOn);
            UUID regionId = region.Scene.RegionInfo.RegionID;
            int? room = region.Service.EnsureSpatialRoom(regionId, JanusAudioBridge.REGION_ROOM_ID);
            Assert.That(room, Is.Not.Null);

            string secret = NewSecret();
            VoiceConnectorRecord record = Record("Forged", secret, room.Value);
            using var host = new EndpointHost("Forged", record, new string('x', 48),   // not the mixer's key
                () => region.Service.EnsureSpatialRoom(regionId, JanusAudioBridge.REGION_ROOM_ID));
            using var program = new Program(StockJanusEnvironment.Python, _connectors, "recorder.recorder",
                Env(record.NpcId.ToString(), room.Value, host.Url, secret, optIn: true));

            // A window in which something must NOT happen: the program keeps trying and the mixer keeps refusing.
            await Task.Delay(TimeSpan.FromSeconds(15));

            Assert.That(host.Fetches, Is.GreaterThanOrEqualTo(1), "it did ask: " + program.Output);
            Assert.That(await _janus.ParticipantCountAsync(room.Value), Is.EqualTo(0), "the mixer did not admit it. Output: " + program.Output);
        }
    }
}
