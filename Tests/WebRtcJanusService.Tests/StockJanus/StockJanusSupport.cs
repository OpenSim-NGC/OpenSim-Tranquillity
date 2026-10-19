/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Support for the opt-in Janus integration tests: the environment they read, a real WebRTC peer (aiortc,
 * driven through aiortc_peer.py) and a small Janus API client that checks what the gateway itself holds.
 *
 * The integration tests run only when WEBRTC_JANUS_TEST_URL names a Janus endpoint; without it every one of
 * them is skipped, so a normal test run is unchanged. Point them at a scratch gateway, never a grid's.
 *
 *   WEBRTC_JANUS_TEST_URL           the gateway API, e.g. http://localhost:8088/janus   (required)
 *   WEBRTC_JANUS_TEST_API_SECRET    its api_secret
 *   WEBRTC_JANUS_TEST_ADMIN_URL     its admin API, e.g. http://localhost:7088/admin
 *   WEBRTC_JANUS_TEST_ADMIN_SECRET  its admin_secret
 *   WEBRTC_JANUS_TEST_PLUGIN        the plugin to drive (default janus.plugin.audiobridge)
 *   WEBRTC_JANUS_TEST_PYTHON        a Python interpreter that has aiortc installed (needed by the tests with a peer)
 *   WEBRTC_JANUS_TEST_CONTAINER     the Docker container running the gateway (needed by the restart test, which
 *                                   runs `docker restart` on it)
 *   WEBRTC_JANUS_TEST_GRACE_SECONDS how long to wait for an emptied room to disappear (default 70)
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nini.Config;

namespace osWebRtcVoice.Tests.StockJanus
{
    public static class StockJanusEnvironment
    {
        public const string UrlVariable = "WEBRTC_JANUS_TEST_URL";

        public static string Get(string name) => Environment.GetEnvironmentVariable(name);

        public static bool Configured => !string.IsNullOrEmpty(Get(UrlVariable));

        public static string Url => Get(UrlVariable);
        public static string ApiSecret => Get("WEBRTC_JANUS_TEST_API_SECRET") ?? string.Empty;
        public static string AdminUrl => Get("WEBRTC_JANUS_TEST_ADMIN_URL") ?? string.Empty;
        public static string AdminSecret => Get("WEBRTC_JANUS_TEST_ADMIN_SECRET") ?? string.Empty;
        public static string PluginName => Get("WEBRTC_JANUS_TEST_PLUGIN") ?? "janus.plugin.audiobridge";
        public static string Python => Get("WEBRTC_JANUS_TEST_PYTHON");
        public static string Container => Get("WEBRTC_JANUS_TEST_CONTAINER");

        public static int GraceSeconds =>
            int.TryParse(Get("WEBRTC_JANUS_TEST_GRACE_SECONDS"), out int g) && g > 0 ? g : 70;

        /// <summary>Skips the calling fixture unless a Janus endpoint is named.</summary>
        public static void IgnoreUnlessConfigured()
        {
            if (!Configured)
                NUnit.Framework.Assert.Ignore($"{UrlVariable} is not set: the Janus integration tests are opt-in");
        }

        /// <summary>Skips the calling test unless a Python with aiortc is named.</summary>
        public static void IgnoreUnlessPeerAvailable()
        {
            if (string.IsNullOrEmpty(Python))
                NUnit.Framework.Assert.Ignore("WEBRTC_JANUS_TEST_PYTHON is not set: this test needs a real WebRTC peer (aiortc)");
        }

        /// <summary>Skips the calling test unless the plugin under test is the stock AudioBridge.</summary>
        public static void IgnoreUnlessAudioBridge()
        {
            if (PluginName != "janus.plugin.audiobridge")
                NUnit.Framework.Assert.Ignore("this test is about the stock AudioBridge plugin");
        }

        public static void IgnoreUnlessRestartable()
        {
            if (string.IsNullOrEmpty(Container))
                NUnit.Framework.Assert.Ignore("WEBRTC_JANUS_TEST_CONTAINER is not set: this test restarts the gateway's container");
        }

        /// <summary>Fills the [WebRtcVoice] and [JanusWebRtcVoice] sections the service and region module read.
        /// The caller adds its own feature switches through <paramref name="tweak"/>.</summary>
        public static IConfigSource MakeConfig(Action<IConfig, IConfig> tweak = null)
        {
            var source = new IniConfigSource();
            IConfig voice = source.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            voice.Set("SpatialVoiceService", "WebRtcJanusService.dll:WebRtcJanusService");
            voice.Set("NonSpatialVoiceService", "WebRtcJanusService.dll:WebRtcJanusService");
            IConfig janus = source.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", Url);
            janus.Set("APIToken", ApiSecret.Length > 0 ? ApiSecret : "unused");
            janus.Set("JanusGatewayAdminURI", AdminUrl.Length > 0 ? AdminUrl : Url);
            janus.Set("AdminAPIToken", AdminSecret.Length > 0 ? AdminSecret : "unused");
            janus.Set("PluginName", PluginName);
            tweak?.Invoke(voice, janus);
            return source;
        }

        public static string PeerScriptPath => Path.Combine(AppContext.BaseDirectory, "StockJanus", "aiortc_peer.py");
    }

    /// <summary>One level measurement a peer made of the audio it received.</summary>
    public sealed class Measurement
    {
        public int Frames;
        public double Rms;
        public double TotalPower;
        public Dictionary<double, double> Power = new();

        /// <summary>Share of the received signal power that sits at <paramref name="hz"/> (1.0 is a pure tone).</summary>
        public double Share(double hz) => TotalPower > 0 ? Power[hz] / TotalPower : 0;

        /// <summary>The median over several windows of the share of the signal power at <paramref name="hz"/>.</summary>
        public static double MedianShare(IReadOnlyList<Measurement> windows, double hz)
        {
            double[] v = windows.Select(w => w.Share(hz)).OrderBy(x => x).ToArray();
            return v.Length == 0 ? 0 : v[v.Length / 2];
        }

        public static double MedianRms(IReadOnlyList<Measurement> windows)
        {
            double[] v = windows.Select(w => w.Rms).OrderBy(x => x).ToArray();
            return v.Length == 0 ? 0 : v[v.Length / 2];
        }

        public static string Describe(IReadOnlyList<Measurement> windows, params double[] freqs) =>
            $"{windows.Count} windows, median rms {MedianRms(windows):F4}, median share " +
            string.Join(" ", freqs.Select(f => $"{f:F0}Hz={MedianShare(windows, f):P0}"));

        public override string ToString() =>
            $"frames={Frames} rms={Rms:F4} " + string.Join(" ", Power.Select(p => $"{p.Key:F0}Hz={Share(p.Key):P1}"));
    }

    /// <summary>A real WebRTC peer: a child process running aiortc_peer.py. It makes the SDP offer a viewer would,
    /// applies the service's answer, and measures the audio it receives.</summary>
    public sealed class AiortcPeer : IDisposable
    {
        private readonly Process _process;
        private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>();
        private readonly StringBuilder _stderr = new();

        public string Offer { get; private set; }
        public string ConnectionState { get; private set; } = "new";

        private AiortcPeer(Process p) { _process = p; }

        public static async Task<AiortcPeer> StartAsync(double toneHz = 0)
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            var psi = new ProcessStartInfo(StockJanusEnvironment.Python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add(StockJanusEnvironment.PeerScriptPath);
            psi.ArgumentList.Add("--tone");
            psi.ArgumentList.Add(toneHz.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var peer = new AiortcPeer(Process.Start(psi));
            peer._process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (peer._stderr) peer._stderr.AppendLine(e.Data); };
            peer._process.BeginErrorReadLine();
            _ = Task.Run(peer.ReadLoop);
            JsonObject offer = await peer.NextAsync("offer", TimeSpan.FromSeconds(30));
            peer.Offer = offer["sdp"].GetValue<string>();
            return peer;
        }

        private async Task ReadLoop()
        {
            try
            {
                string line;
                while ((line = await _process.StandardOutput.ReadLineAsync()) is not null)
                {
                    JsonObject o = JsonNode.Parse(line) as JsonObject;
                    if (o is null) continue;
                    if (o["event"]?.GetValue<string>() == "state")
                        ConnectionState = o["connection"].GetValue<string>();
                    await _events.Writer.WriteAsync(o);
                }
            }
            catch (Exception) { }
            finally { _events.Writer.TryComplete(); }
        }

        private async Task<JsonObject> NextAsync(string name, TimeSpan timeout)
        {
            using var cts = new System.Threading.CancellationTokenSource(timeout);
            try
            {
                while (await _events.Reader.WaitToReadAsync(cts.Token))
                {
                    while (_events.Reader.TryRead(out JsonObject o))
                    {
                        string ev = o["event"].GetValue<string>();
                        if (ev == name) return o;
                        if (ev == "error") throw new InvalidOperationException("peer: " + o["message"]);
                    }
                }
            }
            catch (OperationCanceledException) { }
            string err;
            lock (_stderr) err = _stderr.ToString();
            throw new TimeoutException($"peer sent no '{name}' event within {timeout.TotalSeconds:F0}s. stderr: {err}");
        }

        private void Send(JsonObject o)
        {
            _process.StandardInput.WriteLine(o.ToJsonString());
            _process.StandardInput.Flush();
        }

        public void Answer(string sdp) => Send(new JsonObject { ["cmd"] = "answer", ["sdp"] = sdp });

        public async Task WaitConnectedAsync(TimeSpan timeout)
        {
            DateTime until = DateTime.UtcNow + timeout;
            while (ConnectionState != "connected")
            {
                if (DateTime.UtcNow > until)
                    throw new TimeoutException($"peer connection state '{ConnectionState}' after {timeout.TotalSeconds:F0}s");
                try { await NextAsync("state", TimeSpan.FromSeconds(1)); } catch (TimeoutException) { }
            }
        }

        public async Task<Measurement> MeasureAsync(double seconds, params double[] freqs)
        {
            Send(new JsonObject
            {
                ["cmd"] = "measure",
                ["seconds"] = seconds,
                ["freqs"] = new JsonArray(freqs.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            });
            JsonObject m = await NextAsync("measure", TimeSpan.FromSeconds(seconds + 30));
            var result = new Measurement
            {
                Frames = m["frames"].GetValue<int>(),
                Rms = m["rms"].GetValue<double>(),
                TotalPower = m["total_power"].GetValue<double>(),
            };
            foreach (double f in freqs)
                result.Power[f] = m["power"][f.ToString(System.Globalization.CultureInfo.InvariantCulture)].GetValue<double>();
            return result;
        }

        /// <summary>Several short measurements in a row. A sender that hiccups corrupts one window, not the verdict:
        /// judge the median window, not the whole span.</summary>
        public async Task<List<Measurement>> MeasureWindowsAsync(int windows, double secondsEach, params double[] freqs)
        {
            var list = new List<Measurement>();
            for (int i = 0; i < windows; i++)
                list.Add(await MeasureAsync(secondsEach, freqs));
            return list;
        }

        public void Dispose()
        {
            try { Send(new JsonObject { ["cmd"] = "close" }); } catch (Exception) { }
            try { if (!_process.WaitForExit(5000)) _process.Kill(); } catch (Exception) { }
            _process.Dispose();
        }
    }

    /// <summary>A bare Janus API client: reads what the gateway itself holds, independent of the service under test.</summary>
    public sealed class RawJanus
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private async Task<JsonObject> PostAsync(string url, JsonObject body)
        {
            body["transaction"] = Guid.NewGuid().ToString("N");
            if (StockJanusEnvironment.ApiSecret.Length > 0) body["apisecret"] = StockJanusEnvironment.ApiSecret;
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage r = await _http.PostAsync(url, content);
            return JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject;
        }

        /// <summary>One plugin request on a short-lived session; the plugin answers synchronously for list-type requests.</summary>
        public async Task<JsonObject> PluginRequestAsync(JsonObject pluginBody)
        {
            string api = StockJanusEnvironment.Url.TrimEnd('/');
            JsonObject s = await PostAsync(api, new JsonObject { ["janus"] = "create" });
            string sid = s["data"]["id"].ToString();
            try
            {
                JsonObject h = await PostAsync($"{api}/{sid}", new JsonObject { ["janus"] = "attach", ["plugin"] = StockJanusEnvironment.PluginName });
                string hid = h["data"]["id"].ToString();
                JsonObject r = await PostAsync($"{api}/{sid}/{hid}", new JsonObject { ["janus"] = "message", ["body"] = pluginBody });
                return r["plugindata"]?["data"] as JsonObject ?? r;
            }
            finally
            {
                try { await PostAsync($"{api}/{sid}", new JsonObject { ["janus"] = "destroy" }); } catch (Exception) { }
            }
        }

        /// <summary>Joins a room straight on the gateway with the gateway's API secret and an SDP offer, the way anything
        /// that holds that secret can, bypassing the region. Returns the answer SDP, or null when the join was refused.</summary>
        public async Task<string> JoinDirectAsync(int room, string display, string offerSdp)
        {
            string api = StockJanusEnvironment.Url.TrimEnd('/');
            JsonObject s = await PostAsync(api, new JsonObject { ["janus"] = "create" });
            string sid = s["data"]["id"].ToString();
            JsonObject h = await PostAsync($"{api}/{sid}", new JsonObject { ["janus"] = "attach", ["plugin"] = StockJanusEnvironment.PluginName });
            string hid = h["data"]["id"].ToString();
            await PostAsync($"{api}/{sid}/{hid}", new JsonObject
            {
                ["janus"] = "message",
                ["body"] = new JsonObject { ["request"] = "join", ["room"] = room, ["display"] = display },
                ["jsep"] = new JsonObject { ["type"] = "offer", ["sdp"] = offerSdp },
            });
            string poll = $"{api}/{sid}?maxev=1&apisecret={Uri.EscapeDataString(StockJanusEnvironment.ApiSecret)}";
            DateTime until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                JsonObject ev = JsonNode.Parse(await _http.GetStringAsync(poll)) as JsonObject;
                if (ev?["jsep"]?["sdp"] is JsonNode sdp) return sdp.GetValue<string>();
                if (ev?["plugindata"]?["data"]?["error_code"] is not null) return null;
            }
            return null;
        }

        public async Task<bool> IsUpAsync()
        {
            try
            {
                string api = StockJanusEnvironment.Url.TrimEnd('/');
                return (await _http.GetAsync(api + "/info")).IsSuccessStatusCode;
            }
            catch (Exception) { return false; }
        }

        public async Task<List<int>> RoomsAsync()
        {
            JsonObject d = await PluginRequestAsync(new JsonObject { ["request"] = "list" });
            var rooms = new List<int>();
            if (d["list"] is JsonArray a)
                foreach (JsonNode n in a) rooms.Add(n["room"].GetValue<int>());
            return rooms;
        }

        public async Task<bool> RoomExistsAsync(int room) => (await RoomsAsync()).Contains(room);

        /// <summary>Number of participants the gateway reports in a room, or -1 when the room does not exist.</summary>
        public async Task<int> ParticipantCountAsync(int room)
        {
            JsonObject d = await PluginRequestAsync(new JsonObject { ["request"] = "listparticipants", ["room"] = room });
            return d["participants"] is JsonArray a ? a.Count : -1;
        }

        /// <summary>Polls until the participant count in a room reaches the wanted value; false on timeout.</summary>
        public async Task<bool> WaitForParticipantsAsync(int room, int wanted, TimeSpan timeout)
        {
            DateTime until = DateTime.UtcNow + timeout;
            while (true)
            {
                if (await ParticipantCountAsync(room) == wanted) return true;
                if (DateTime.UtcNow > until) return false;
                await Task.Delay(250);
            }
        }
    }

    public static class DockerControl
    {
        /// <summary>Runs `docker restart` on the container named in the environment and returns when Janus answers again.</summary>
        public static async Task RestartGatewayAsync(RawJanus janus)
        {
            var psi = new ProcessStartInfo("docker", "restart " + StockJanusEnvironment.Container)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process p = Process.Start(psi);
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException("docker restart failed: " + await p.StandardError.ReadToEndAsync());
            DateTime until = DateTime.UtcNow.AddSeconds(60);
            while (!await janus.IsUpAsync())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("the gateway did not answer 60 s after the restart");
                await Task.Delay(500);
            }
        }
    }
}
