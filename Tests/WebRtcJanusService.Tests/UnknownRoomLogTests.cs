/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The mixer answers unknown_room for a room it has not been asked to create yet. That happens on the first
 * arming pass after an avatar appears: the avatar is addressed at the room its parcel resolves to, and the room
 * is created by the avatar's own provision, which can come after that first pass. The first such reply is
 * expected and is logged at Debug. Every other unknown_room stays a Warning: a second reply for a room that
 * has still never answered, a room that did answer before, and a room something has proved exists.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class UnknownRoomLogTests
    {
        private const int Room = 4242;
        private static readonly UUID Listener = new UUID("11111111-2222-3333-4444-555555555555");

        private sealed class Capture : ILoggerProvider, ILogger
        {
            public readonly List<(LogLevel Level, string Text)> Lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (Lines)
                    Lines.Add((logLevel, formatter(state, exception)));
            }
            public void Dispose() { }
            public List<string> At(LogLevel level)
            {
                lock (Lines)
                    return Lines.Where(l => l.Level == level && l.Text.Contains("unknown_room")).Select(l => l.Text).ToList();
            }
        }

        private Capture _log;
        private ILoggerFactory _previous;
        private long _clock;

        [SetUp]
        public void SetUp()
        {
            _log = new Capture();
            _previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(_log).SetMinimumLevel(LogLevel.Trace));
            _clock = 10_000;
        }

        [TearDown]
        public void TearDown()
        {
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = _previous;
            ours.Dispose();
            _log.Dispose();
        }

        private VisAuthority NewAuthority() => new VisAuthority(VisAuthority.NewEpoch(), "unknown-room-log", () => _clock);

        private static JanusPeerCtlBatchSink.SlvoiceReply Unknown()
            => new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" };

        private static JanusPeerCtlBatchSink.SlvoiceReply Applied()
            => new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "applied" };

        private void Batch(VisAuthority a, JanusPeerCtlBatchSink.SlvoiceReply reply)
            => a.OnBatchOutcome(Room, VisOp.Replace, 1, new[] { Listener }, true, reply);

        [Test]
        public void TheFirstUnknownRoom_ForARoomThatNeverAnswered_IsDebugNotWarning()
        {
            VisAuthority a = NewAuthority();
            Batch(a, Unknown());

            Assert.That(_log.At(LogLevel.Warning), Is.Empty, "the room has not been created yet: expected");
            Assert.That(_log.At(LogLevel.Debug), Has.Count.EqualTo(1));
            Assert.That(a.CanArmNow(Listener), Is.False, "the listener still backs off and retries as before");
        }

        [Test]
        public void ASecondUnknownRoom_ForTheSameRoom_IsAWarning()
        {
            VisAuthority a = NewAuthority();
            Batch(a, Unknown());
            _clock += 2000;
            Batch(a, Unknown());

            Assert.That(_log.At(LogLevel.Warning), Has.Count.EqualTo(1), "the room is still missing after its first retry");
            Assert.That(_log.At(LogLevel.Debug), Has.Count.EqualTo(1));
        }

        [Test]
        public void UnknownRoom_ForARoomThatAnsweredBefore_IsAWarning()
        {
            VisAuthority a = NewAuthority();
            Batch(a, Applied());
            Batch(a, Unknown());

            Assert.That(_log.At(LogLevel.Warning), Has.Count.EqualTo(1), "the mixer lost a room it once had");
            Assert.That(_log.At(LogLevel.Debug), Is.Empty);
        }

        [Test]
        public void UnknownRoom_ForARoomSomethingProvedExists_IsAWarning()
        {
            VisAuthority a = NewAuthority();
            a.RoomExists(Room);
            Batch(a, Unknown());

            Assert.That(_log.At(LogLevel.Warning), Has.Count.EqualTo(1));
            Assert.That(_log.At(LogLevel.Debug), Is.Empty);
        }

        [Test]
        public void TheFirstUnknownRoom_InAHeartbeatReply_IsDebugToo_AndALaterOneWarns()
        {
            VisAuthority a = NewAuthority();
            VisAuthority.HeartbeatReply hb = new VisAuthority.HeartbeatReply { IsHeartbeat = true, VisProtocol = 2, MixerInstance = "m1" };
            hb.Rooms[Room] = new VisAuthority.HeartbeatRoomReply { Status = "unknown_room" };

            a.OnHeartbeatOutcome(true, hb);
            Assert.That(_log.At(LogLevel.Warning), Is.Empty);

            a.OnHeartbeatOutcome(true, hb);
            Assert.That(_log.At(LogLevel.Warning), Has.Count.EqualTo(1));
        }
    }
}
