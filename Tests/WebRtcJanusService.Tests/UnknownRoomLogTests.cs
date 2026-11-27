/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The mixer answers unknown_room for a room it has not been asked to create yet. That can happen when a batch
 * for a room reaches the mixer before the provision that creates the room has completed there. The first such
 * reply is expected and is logged at Debug. Every other unknown_room stays a Warning: a second reply for a room that
 * has still never answered, a room that did answer before, and a room something has proved exists.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class UnknownRoomLogTests : IAsyncLifetime
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
        private LogCapture _logging;
        private long _clock;
        private void SetUp()
        {
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
            _clock = 10_000;
        }
        private void TearDown()
        {
            _logging.Dispose();
            _log.Dispose();
        }

        private VisAuthority NewAuthority() => new VisAuthority(VisAuthority.NewEpoch(), "unknown-room-log", () => _clock);

        private static JanusPeerCtlBatchSink.SlvoiceReply Unknown()
            => new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" };

        private static JanusPeerCtlBatchSink.SlvoiceReply Applied()
            => new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "applied" };

        private void Batch(VisAuthority a, JanusPeerCtlBatchSink.SlvoiceReply reply)
            => a.OnBatchOutcome(Room, VisOp.Replace, 1, new[] { Listener }, true, reply);

        [Fact(Timeout = 60000)]
        public void TheFirstUnknownRoom_ForARoomThatNeverAnswered_IsDebugNotWarning()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VisAuthority a = NewAuthority();
            Batch(a, Unknown());

            {
                var assertedValue = _log.At(LogLevel.Warning);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the room has not been created yet: expected");
            }
            Assert.Single(_log.At(LogLevel.Debug));
            Assert.False(a.CanArmNow(Listener), "the listener still backs off and retries as before");
        }

        [Fact(Timeout = 60000)]
        public void ASecondUnknownRoom_ForTheSameRoom_IsAWarning()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VisAuthority a = NewAuthority();
            Batch(a, Unknown());
            _clock += 2000;
            Batch(a, Unknown());

            {
                var assertedValue = _log.At(LogLevel.Warning);
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the room is still missing after its first retry");
            }
            Assert.Single(_log.At(LogLevel.Debug));
        }

        [Fact(Timeout = 60000)]
        public void UnknownRoom_ForARoomThatAnsweredBefore_IsAWarning()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VisAuthority a = NewAuthority();
            Batch(a, Applied());
            Batch(a, Unknown());

            {
                var assertedValue = _log.At(LogLevel.Warning);
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the mixer lost a room it once had");
            }
            Assert.Empty(_log.At(LogLevel.Debug));
        }

        [Fact(Timeout = 60000)]
        public void UnknownRoom_ForARoomSomethingProvedExists_IsAWarning()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VisAuthority a = NewAuthority();
            a.RoomExists(Room);
            Batch(a, Unknown());

            Assert.Single(_log.At(LogLevel.Warning));
            Assert.Empty(_log.At(LogLevel.Debug));
        }

        [Fact(Timeout = 60000)]
        public void TheFirstUnknownRoom_InAHeartbeatReply_IsDebugToo_AndALaterOneWarns()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VisAuthority a = NewAuthority();
            VisAuthority.HeartbeatReply hb = new VisAuthority.HeartbeatReply { IsHeartbeat = true, VisProtocol = 2, MixerInstance = "m1" };
            hb.Rooms[Room] = new VisAuthority.HeartbeatRoomReply { Status = "unknown_room" };

            a.OnHeartbeatOutcome(true, hb);
            Assert.Empty(_log.At(LogLevel.Warning));

            a.OnHeartbeatOutcome(true, hb);
            Assert.Single(_log.At(LogLevel.Warning));
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            TearDown();
            return ValueTask.CompletedTask;
        }
    }
}
