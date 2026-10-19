/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Drives the position feed for one region: every PositionSettings.TickMs it reads where each voice
 * participant is, lets VoicePositionFeeder decide what changed, and posts the resulting peer_pos requests.
 *
 * Called from the visibility feeder's own tick thread, so the scene walk happens on the one thread that
 * already walks the scene for the visibility matrix. The POSTs are fire-and-forget and single-flight: while
 * one send is still outstanding no new scan starts, so a slow mixer cannot build a queue. What a skipped
 * tick would have sent is carried by the next one, or by the keep-alive snapshot.
 *
 * Nothing is computed or sent until the mixer has advertised vis_protocol 3. The sim learns that from the
 * replies to the peer_ctl requests it already sends (see JanusPeerCtlBatchSink.MixerVisProtocol), so a mixer
 * that does not know peer_pos is never sent one.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public sealed class VoicePositionPump
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[VOICE POSITIONS]";

        // How often a persistent send failure is logged at Warn; in between it is Debug.
        private const long FailureLogIntervalMs = 60_000;

        private readonly IPositionWorld _world;
        private readonly IPeerPosSink _sink;
        private readonly PositionSettings _settings;
        private readonly VoicePositionFeeder _feeder;
        private readonly Func<UUID, int?> _roomOf;
        private readonly Func<double> _engineMaxCmS;
        private readonly string _region;
        private readonly Func<long> _nowMs;

        private long _lastRunMs = long.MinValue;
        private long _lastFailureLogMs = long.MinValue;
        private int _inFlight;
        private int _knownProtocol = -1;   // the protocol last logged, so a change is logged once
        private long _sent;
        private long _failed;

        /// <param name="roomOf">The mixer room a participant is in, or null when it cannot be placed.</param>
        /// <param name="engineMaxCmS">The physics engine's maximum speed in cm/s, or zero when it declares none.</param>
        /// <param name="nowMs">Clock in milliseconds; defaults to the process tick count. Injectable for tests.</param>
        public VoicePositionPump(IPositionWorld world, IPeerPosSink sink, PositionSettings settings,
            Func<UUID, int?> roomOf, Func<double> engineMaxCmS, string region, Func<long> nowMs = null)
        {
            _world = world;
            _sink = sink;
            _settings = (settings ?? new PositionSettings()).Normalised();
            _roomOf = roomOf;
            _engineMaxCmS = engineMaxCmS ?? (() => 0.0);
            _region = region ?? "?";
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            // A sequence number above any a previous run could have reached, so a restarted sim is not
            // mistaken for late traffic from before.
            _feeder = new VoicePositionFeeder(_settings, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>Requests the mixer answered.</summary>
        public long MessagesSent => Interlocked.Read(ref _sent);

        /// <summary>Requests that failed in transport.</summary>
        public long MessagesFailed => Interlocked.Read(ref _failed);

        /// <summary>Called on every feeder tick; does work only when the position interval has passed. Never throws.</summary>
        public void OnFeederTick() => _ = TickAsync();

        /// <summary>The awaitable core of <see cref="OnFeederTick"/>. Completes when this tick's sends have.</summary>
        public Task TickAsync()
        {
            try
            {
                long now = _nowMs();
                if (_lastRunMs != long.MinValue && now - _lastRunMs < _settings.TickMs)
                    return Task.CompletedTask;
                _lastRunMs = now;

                int protocol = _sink.MixerVisProtocol;
                LogProtocolChange(protocol);
                if (protocol < VisAuthority.PositionProtocol)
                    return Task.CompletedTask;

                if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
                    return Task.CompletedTask;   // the previous send is still out

                IReadOnlyList<PositionMessage> messages;
                try
                {
                    messages = _feeder.Build(now, _world.SnapshotPositions(), _roomOf, _engineMaxCmS());
                }
                catch (Exception e)
                {
                    Interlocked.Exchange(ref _inFlight, 0);
                    m_log.LogWarning(e, "{LogHeader} region {Region}: reading positions failed; trying again next tick", LogHeader, _region);
                    return Task.CompletedTask;
                }
                if (messages.Count == 0)
                {
                    Interlocked.Exchange(ref _inFlight, 0);
                    return Task.CompletedTask;
                }
                return SendAllAsync(messages, now);
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _inFlight, 0);
                m_log.LogWarning(e, "{LogHeader} region {Region}: position tick failed", LogHeader, _region);
                return Task.CompletedTask;
            }
        }

        private async Task SendAllAsync(IReadOnlyList<PositionMessage> messages, long now)
        {
            PositionFeedMode mode = PositionFeedModes.Active(_settings.Mode);
            try
            {
                foreach (PositionMessage message in messages)
                {
                    bool ok = await _sink.SendPositionsAsync(PositionBatchSerializer.BuildRequest(message, mode)).ConfigureAwait(false);
                    if (ok)
                    {
                        Interlocked.Increment(ref _sent);
                        continue;
                    }
                    Interlocked.Increment(ref _failed);
                    LogFailure(message.Room, now);
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failed);
                m_log.LogWarning(e, "{LogHeader} region {Region}: sending positions failed", LogHeader, _region);
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        private void LogFailure(int room, long now)
        {
            if (_lastFailureLogMs == long.MinValue || now - _lastFailureLogMs >= FailureLogIntervalMs)
            {
                _lastFailureLogMs = now;
                m_log.LogWarning("{LogHeader} region {Region}: the mixer did not accept a peer_pos request for room {Room}; the next " +
                    "full snapshot repeats it ({Failed} failed so far)", LogHeader, _region, room, MessagesFailed);
            }
            else
            {
                m_log.LogDebug("{LogHeader} region {Region}: peer_pos for room {Room} failed", LogHeader, _region, room);
            }
        }

        private void LogProtocolChange(int protocol)
        {
            if (protocol == _knownProtocol)
                return;
            bool first = _knownProtocol < 0;
            _knownProtocol = protocol;
            if (protocol >= VisAuthority.PositionProtocol)
                m_log.LogInformation("{LogHeader} region {Region}: the mixer advertises vis_protocol {Protocol}; sending positions in {Mode} mode",
                    LogHeader, _region, protocol, PositionFeedModes.WireName(PositionFeedModes.Active(_settings.Mode)));
            else if (protocol == 0)
            {
                if (first)
                    m_log.LogInformation("{LogHeader} region {Region}: waiting to learn the mixer's protocol version from its replies to " +
                        "peer_ctl requests; no positions are sent until it advertises vis_protocol {Needed}",
                        LogHeader, _region, VisAuthority.PositionProtocol);
                else
                    m_log.LogWarning("{LogHeader} region {Region}: the mixer no longer advertises a protocol version; positions stop",
                        LogHeader, _region);
            }
            else
                m_log.LogWarning("{LogHeader} region {Region}: the mixer advertises vis_protocol {Protocol}, and peer_pos needs {Needed}: " +
                    "no positions are sent", LogHeader, _region, protocol, VisAuthority.PositionProtocol);
        }
    }
}
