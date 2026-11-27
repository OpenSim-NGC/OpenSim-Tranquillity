/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the viewer_session binding.
 *
 * Hazard: VoiceViewerSession.TryGetViewerSession binds by id string only, so any agent presenting
 * another agent's viewer_session id could drive that Janus session (re-provision it into a room, feed it
 * ICE candidates, log it out). Both cap sites in WebRtcVoiceServiceModule now resolve through
 * TryGetViewerSessionFor, which additionally requires the AgentId of the Janus session to equal the cap-bound
 * requester; a mismatch is treated exactly as NOT FOUND (no new error shape) and logged at WARN
 * naming both agent ids.
 */
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class ViewerSessionBindingTests : IAsyncLifetime
        {
        private static readonly UUID Region = new UUID("aaaaaaaa-0000-0000-0000-00000000a2a5");
        private static readonly UUID Owner = new UUID("11111111-1111-1111-1111-1111111a2a05");
        private static readonly UUID Other = new UUID("22222222-2222-2222-2222-2222222a2a05");

        /// <summary>Minimal ILogger that records what was logged, so the WARN is observable.</summary>
        private sealed class CapturingLogger : ILogger
        {
            public readonly List<(LogLevel Level, string Message)> Entries = new List<(LogLevel, string)>();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }

        private VoiceViewerSession _session;
        private CapturingLogger _log;
        private void SetUp()
        {
            _session = new VoiceViewerSession(null, Region, Owner);
            VoiceViewerSession.AddViewerSession(_session);
            _log = new CapturingLogger();
        }
        private void TearDown()
        {
            VoiceViewerSession.RemoveViewerSession(_session.ViewerSessionID);
        }

        [InlineData("ProvisionVoiceAccountRequest")]
        [InlineData("VoiceSignalingRequest")]
        [Theory(Timeout = 60000)]
        public void OwnerLookup_Found_NoWarn(string site)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            bool found = WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Owner, site, _log, out IVoiceViewerSession s);
            Assert.True(found);
            Assert.Same(_session, s);
            Assert.Empty(_log.Entries);
        }

        [InlineData("ProvisionVoiceAccountRequest")]
        [InlineData("VoiceSignalingRequest")]
        [Theory(Timeout = 60000)]
        public void OtherAgentLookup_TreatedAsNotFound_AndWarns(string site)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            bool found = WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Other, site, _log, out IVoiceViewerSession s);
            Assert.False(found, "another agent's viewer_session must resolve to nothing");
            {
                var assertedValue = s;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the mismatched session must not leak to the caller");
            }
            Assert.Single(_log.Entries);
            Assert.Equal(LogLevel.Warning, _log.Entries[0].Level);
            Assert.Contains(site, _log.Entries[0].Message);
            {
                var assertedValue = _log.Entries[0].Message;
                TestAssert.WithMessage(() => Assert.Contains(Other.ToString(), assertedValue), "names the requester");
            }
            {
                var assertedValue = _log.Entries[0].Message;
                TestAssert.WithMessage(() => Assert.Contains(Owner.ToString(), assertedValue), "names the session's bound agent");
            }
            Assert.Contains(_session.ViewerSessionID, _log.Entries[0].Message);
        }

        [Fact(Timeout = 60000)]
        public void OtherAgentLookup_LeavesTheSessionRegistered()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Treated as not-found for the requester only: the owner's session is untouched.
            WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Other, "VoiceSignalingRequest", _log, out _);
            Assert.True(VoiceViewerSession.TryGetViewerSession(_session.ViewerSessionID, out IVoiceViewerSession still));
            Assert.Same(_session, still);
            Assert.True(WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Owner, "VoiceSignalingRequest", _log, out _));
        }

        [Fact(Timeout = 60000)]
        public void RemoveViewerSession_DropsRegistryEntryAndMembership()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The inverse of OtherAgentLookup_LeavesTheSessionRegistered, and the contract it establishes:
            // a removal must drop BOTH the ViewerSessions entry and its membership-index row,
            // so the agent stops being a matrix member the moment the Janus session is gone.
            Assert.True(VoiceViewerSession.IsAgentInRegion(Region, Owner), "precondition: the added session makes the agent a member");
            VoiceViewerSession.RemoveViewerSession(_session.ViewerSessionID);
            Assert.False(VoiceViewerSession.TryGetViewerSession(_session.ViewerSessionID, out _), "the registry entry is gone");
            Assert.False(VoiceViewerSession.IsAgentInRegion(Region, Owner), "the membership row is gone with it");
        }

        [Fact(Timeout = 60000)]
        public void UnknownId_NotFound_NoWarn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The pre-existing not-found path: no session at all is not a spoof, so no WARN here
            // (the call sites keep their existing "not found" ERROR line).
            bool found = WebRtcVoiceServiceModule.TryGetViewerSessionFor(UUID.Random().ToString(), Owner, "ProvisionVoiceAccountRequest", _log, out IVoiceViewerSession s);
            Assert.False(found);
            Assert.Null(s);
            Assert.Empty(_log.Entries);
        }

        [Fact(Timeout = 60000)]
        public void ZeroRequester_NeverMatches()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A cap handler always has a real agent; UUID.Zero must not become a wildcard.
            bool found = WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, UUID.Zero, "ProvisionVoiceAccountRequest", _log, out _);
            Assert.False(found);
            Assert.Single(_log.Entries);
        }

        [Fact(Timeout = 60000)]
        public void NullLogger_StillRefuses()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(WebRtcVoiceServiceModule.TryGetViewerSessionFor(_session.ViewerSessionID, Other, "VoiceSignalingRequest", null, out IVoiceViewerSession s));
            Assert.Null(s);
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
