/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A test fixture for avatar-to-avatar calls: the call store the module uses (the non-spatial engine over an
 * in-memory store, wrapped by A2ACalls) with a clock the test controls and the verbs the call tests speak in
 * (record, issue a token, mark provisioned, mark gone, decline). It holds no state of its own; every answer
 * comes from the engine, so the tests exercise the same code the module does.
 *
 * A2ASession values it returns are copies, as in production: read the call again after changing it.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    public sealed class A2ARegistry
    {
        public static readonly TimeSpan DefaultInviteTtl = NonSpatialVoiceSessionEngine.DefaultInviteTtl;
        public static readonly TimeSpan DefaultActiveIdleTtl = NonSpatialVoiceSessionEngine.DefaultIdleTtl;
        public const int TokenBytes = NonSpatialVoiceSessionEngine.TokenBytes;

        /// <summary>The region every call in these tests is taken from.</summary>
        public static readonly UUID Region = new UUID("0e610000-0000-4000-8000-000000000001");

        public NonSpatialVoiceSessionEngine Engine { get; }
        public A2ACalls Calls { get; }
        public TimeSpan InviteTtl { get; }
        public TimeSpan ActiveIdleTtl { get; }

        public A2ARegistry() : this(DefaultInviteTtl, DefaultActiveIdleTtl, null) { }

        public A2ARegistry(TimeSpan inviteTtl, Func<DateTime> clock) : this(inviteTtl, DefaultActiveIdleTtl, clock) { }

        public A2ARegistry(TimeSpan inviteTtl, TimeSpan activeIdleTtl, Func<DateTime> clock)
        {
            InviteTtl = inviteTtl > TimeSpan.Zero ? inviteTtl : DefaultInviteTtl;
            ActiveIdleTtl = activeIdleTtl > TimeSpan.Zero ? activeIdleTtl : DefaultActiveIdleTtl;
            Engine = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission() },
                "example-grid", clock, InviteTtl, ActiveIdleTtl);
            Calls = new A2ACalls(() => Engine);
        }

        /// <summary>The calls API stands in wherever a test hands the registry to the code under test.</summary>
        public static implicit operator A2ACalls(A2ARegistry r) => r.Calls;

        public static UUID ComputeSessionId(UUID a, UUID b) => A2ACalls.ComputeSessionId(a, b);

        public static bool ShouldMarkGone(bool presenceFound, bool isChildAgent) => A2ACalls.ShouldMarkGone(presenceFound, isChildAgent);

        public A2ASession Record(UUID caller, UUID callee, out bool created) => Calls.Record(caller, callee, Region, out created);

        public A2ASession IssueToken(UUID sessionId, UUID agent) => Calls.IssueToken(sessionId, agent);

        public A2ASession MarkProvisioned(UUID sessionId, UUID agent, string viewerSession)
            => Calls.MarkProvisioned(sessionId, agent, Region, viewerSession);

        public A2ASession MarkProvisioned(UUID sessionId, UUID agent, string viewerSession, out bool becameActive)
            => Calls.MarkProvisioned(sessionId, agent, Region, viewerSession, out becameActive);

        public A2ASession TryGet(UUID sessionId) => Calls.TryGet(sessionId, UUID.Zero);

        public A2ASession TryGetByChannel(string channel) => Calls.TryGetByChannel(channel, UUID.Zero);

        public bool Decline(UUID sessionId, UUID agent, out bool wasParty) => Calls.Decline(sessionId, agent, out wasParty);

        public void MarkInviteSent(UUID sessionId) => Calls.MarkInviteSent(sessionId);

        /// <summary>A party left: its logout provision (when <paramref name="viewerSession"/> is given) or its client closing.</summary>
        public List<A2ASession> MarkGoneSessions(UUID agent, string viewerSession)
            => Calls.Ended(viewerSession == null
                ? Engine.DepartAll(agent, DepartureReason.PresenceLost)
                : Engine.DepartByViewerSession(agent, viewerSession, DepartureReason.VoiceTeardown));

        public List<UUID> MarkGone(UUID agent, string viewerSession)
        {
            List<UUID> ids = new List<UUID>();
            foreach (A2ASession s in MarkGoneSessions(agent, viewerSession))
                ids.Add(s.SessionId);
            return ids;
        }

        /// <summary>The call a party joined under <paramref name="viewerSession"/>, or null.</summary>
        public A2ASession FindByViewerSession(UUID agent, string viewerSession)
        {
            if (string.IsNullOrEmpty(viewerSession))
                return null;
            foreach (NonSpatialVoiceSession s in Engine.Store.All())
            {
                if (s.Type != NonSpatialSessionType.P2P || s.IsDead)
                    continue;
                NonSpatialMember m = s.Find(agent);
                if (m != null && m.HoldsSeat && string.Equals(m.ViewerSession, viewerSession, StringComparison.Ordinal))
                    return TryGet(s.SessionId);
            }
            return null;
        }

        /// <summary>Number of calls that have not ended; the sweep runs first, as a timer does in production.</summary>
        public int Count
        {
            get
            {
                Engine.Sweep();
                int n = 0;
                foreach (NonSpatialVoiceSession s in Engine.Store.All())
                    if (s.Type == NonSpatialSessionType.P2P && !s.IsDead)
                        n++;
                return n;
            }
        }

        public bool Remove(UUID sessionId) => Engine.Store.Remove(sessionId);
    }
}
