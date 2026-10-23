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
 * replies to the peer_ctl requests it already sends (see JanusPeerCtlBatchSink.MixerVisProtocol). When it has
 * sent none (arming off and nobody excluded or muted), the pump asks once, with a request that names no room
 * (JanusPeerCtlBatchSink.BuildProtocolCheck). A mixer that does not know peer_pos is never sent one, and one
 * that advertises no version is not asked again until a request to it fails.
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
using osWebRtcVoice.NonSpatial;   // RefusalLogGate

namespace osWebRtcVoice
{
    public sealed class VoicePositionPump
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[VOICE POSITIONS]";

        // How often a persistent send failure is logged at Warn; in between it is Debug.
        private const long FailureLogIntervalMs = 60_000;
        private const long SummaryIntervalMs = 60_000;
        private static readonly DateTime s_gateEpoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly IPositionWorld _world;
        private readonly IPeerPosSink _sink;
        private readonly PositionSettings _settings;
        private readonly VoicePositionFeeder _feeder;
        private readonly Func<UUID, int?> _roomOf;
        private readonly RoomRefusals _refusals;   // rooms the mixer answered unknown_room for; null holds nothing back
        private readonly Func<double> _engineMaxCmS;
        private readonly string _region;
        private readonly Func<long> _nowMs;

        private long _lastRunMs = long.MinValue;
        private long _lastFailureLogMs = long.MinValue;
        private readonly RefusalLogGate _failureGate = new RefusalLogGate();   // one warning a minute for each kind of failure
        private int _inFlight;
        private const int NothingLogged = int.MinValue;
        private int _knownProtocol = NothingLogged;   // the protocol last logged, so a change is logged once
        private long _sent;
        private long _failed;
        private long _heldBack;
        // The one-minute summary: the window's start and the totals at that moment.
        private long _summaryStartMs = long.MinValue;
        private long _summarySent;
        private long _summaryFailed;
        private long _summaryHeldBack;
        private PositionReplyCounts _summaryReplies;

        /// <param name="roomOf">The mixer room a participant is in, or null when it cannot be placed.</param>
        /// <param name="engineMaxCmS">The physics engine's maximum speed in cm/s, or zero when it declares none.</param>
        /// <param name="nowMs">Clock in milliseconds; defaults to the process tick count. Injectable for tests.</param>
        /// <param name="refusals">Rooms the mixer has refused as unknown. Nothing is sent to them until a join into them is
        /// recorded.</param>
        public VoicePositionPump(IPositionWorld world, IPeerPosSink sink, PositionSettings settings,
            Func<UUID, int?> roomOf, Func<double> engineMaxCmS, string region, Func<long> nowMs = null,
            RoomRefusals refusals = null)
        {
            _world = world;
            _sink = sink;
            _settings = (settings ?? new PositionSettings()).Normalised();
            _roomOf = roomOf;
            _refusals = refusals;
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

        /// <summary>Requests not sent because the mixer had refused their room as unknown and no join into it has been
        /// recorded since.</summary>
        public long MessagesHeldBack => Interlocked.Read(ref _heldBack);

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
                LogSummaryIfDue(now);

                bool known = _sink.MixerProtocolKnown;
                int protocol = known ? _sink.MixerVisProtocol : -1;
                LogProtocolChange(protocol);
                if (known && protocol < VisAuthority.PositionProtocol)
                    return Task.CompletedTask;

                if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
                    return Task.CompletedTask;   // the previous send is still out

                if (!known)
                    return CheckProtocolAsync();

                IReadOnlyList<PositionMessage> messages;
                try
                {
                    messages = _feeder.Build(now, _world.SnapshotPositions(), _roomOf, _engineMaxCmS());
                }
                catch (Exception e)
                {
                    Interlocked.Exchange(ref _inFlight, 0);
                    LogGated("reading positions failed", e, "reading positions failed; trying again next tick");
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
                LogGated("position tick failed", e, "position tick failed");
                return Task.CompletedTask;
            }
        }

        // Asks the mixer which protocol it speaks. The next tick sends positions if it answered 3 or later.
        private async Task CheckProtocolAsync()
        {
            try
            {
                if (!await _sink.CheckMixerProtocolAsync().ConfigureAwait(false))
                    LogGated("the mixer version check failed", null, "the mixer did not answer the version check; trying again next tick");
            }
            catch (Exception e)
            {
                LogGated("the mixer version check failed", e, "the mixer version check failed");
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        private async Task SendAllAsync(IReadOnlyList<PositionMessage> messages, long now)
        {
            PositionFeedMode mode = PositionFeedModes.Active(_settings.Mode);
            try
            {
                foreach (PositionMessage message in messages)
                {
                    if (_refusals != null && _refusals.IsRefused(message.Room))
                    {
                        // The mixer holds no such room. What it will need once a viewer joins is a whole snapshot.
                        _feeder.Forget(message.Room);
                        Interlocked.Increment(ref _heldBack);
                        continue;
                    }
                    long unknownBefore = _sink.Replies?.Counts.UnknownRoom ?? 0;
                    bool ok = await _sink.SendPositionsAsync(PositionBatchSerializer.BuildRequest(message, mode)).ConfigureAwait(false);
                    if (ok)
                    {
                        Interlocked.Increment(ref _sent);
                        // The room did not take it: whatever the room holds once a viewer joins, it holds none of this.
                        if ((_sink.Replies?.Counts.UnknownRoom ?? 0) > unknownBefore)
                            _feeder.Forget(message.Room);
                        continue;
                    }
                    Interlocked.Increment(ref _failed);
                    LogFailure(message.Room, now);
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failed);
                LogGated("sending positions failed", e, "sending positions failed");
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        // Once a minute, at Debug, what the feed did in that minute: the requests sent and failed, and what the mixer
        // answered. A minute in which nothing was sent or answered writes nothing.
        private void LogSummaryIfDue(long now)
        {
            if (_summaryStartMs == long.MinValue)
            {
                _summaryStartMs = now;
                return;
            }
            if (now - _summaryStartMs < SummaryIntervalMs)
                return;
            long sent = MessagesSent, failed = MessagesFailed, heldBack = MessagesHeldBack;
            PositionReplyCounts replies = _sink.Replies?.Counts ?? default;
            PositionReplyCounts delta = replies.Since(_summaryReplies);
            if (sent != _summarySent || failed != _summaryFailed || heldBack != _summaryHeldBack || delta.Total != 0)
                m_log.LogDebug("{LogHeader} region {Region}: position summary: {Sent} request(s) sent and {Failed} failed over {WindowS} s; " +
                    "the mixer answered {Replies}; {HeldBack} not sent because the mixer holds no such room", LogHeader, _region,
                    sent - _summarySent, failed - _summaryFailed, (now - _summaryStartMs) / 1000, delta.ToString(),
                    heldBack - _summaryHeldBack);
            _summaryStartMs = now;
            _summarySent = sent;
            _summaryFailed = failed;
            _summaryHeldBack = heldBack;
            _summaryReplies = replies;
        }

        // A failure that repeats every tick is one warning a minute, with the number held back; the rest are Debug lines.
        private void LogGated(string kind, Exception e, string what)
        {
            if (_failureGate.ShouldLog(kind, UUID.Zero, s_gateEpoch.AddMilliseconds(_nowMs()), out int held))
            {
                if (held > 0)
                    m_log.LogWarning(e, "{LogHeader} region {Region}: {What}; {Held} more failure(s) since the last one logged",
                        LogHeader, _region, what, held);
                else
                    m_log.LogWarning(e, "{LogHeader} region {Region}: {What}", LogHeader, _region, what);
            }
            else
                m_log.LogDebug(e, "{LogHeader} region {Region}: {What}", LogHeader, _region, what);
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

        // protocol is -1 while the mixer's version is not known, 0 for a mixer that advertises none.
        private void LogProtocolChange(int protocol)
        {
            if (protocol == _knownProtocol)
                return;
            bool first = _knownProtocol == NothingLogged;
            _knownProtocol = protocol;
            if (protocol >= VisAuthority.PositionProtocol)
                m_log.LogInformation("{LogHeader} region {Region}: the mixer advertises vis_protocol {Protocol}; sending positions in {Mode} mode",
                    LogHeader, _region, protocol, PositionFeedModes.WireName(PositionFeedModes.Active(_settings.Mode)));
            else if (protocol < 0)
            {
                if (first)
                    m_log.LogInformation("{LogHeader} region {Region}: asking the mixer which protocol version it speaks; no positions " +
                        "are sent until it advertises vis_protocol {Needed}", LogHeader, _region, VisAuthority.PositionProtocol);
                else
                    m_log.LogDebug("{LogHeader} region {Region}: a request to the mixer failed; asking it again which protocol it speaks",
                        LogHeader, _region);
            }
            else if (protocol == 0)
                m_log.LogWarning("{LogHeader} region {Region}: the mixer advertises no protocol version, and peer_pos needs {Needed}: " +
                    "no positions are sent", LogHeader, _region, VisAuthority.PositionProtocol);
            else
                m_log.LogWarning("{LogHeader} region {Region}: the mixer advertises vis_protocol {Protocol}, and peer_pos needs {Needed}: " +
                    "no positions are sent", LogHeader, _region, protocol, VisAuthority.PositionProtocol);
        }
    }
}
