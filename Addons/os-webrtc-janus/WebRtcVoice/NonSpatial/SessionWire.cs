/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What crosses the wire between a region and the grid session service: plain data, no live objects.
 * A reply carries the outcome of one operation and, where the operation touched a session, a copy of
 * that session taken under the service's lock. The copies are rebuilt into NonSpatialVoiceSession
 * objects on the region side so the code that reads a session (Find, Members, SeatsHeld, WasInvited)
 * is the same whether the engine runs in the region or behind the service.
 */
using System;
using System.Collections.Generic;
using System.Text.Json;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class MemberData
    {
        public string Agent { get; set; }
        public int State { get; set; }
        public int Departure { get; set; }
        public string Origin { get; set; }
        public string ViewerSession { get; set; }
        public long InvitedTicks { get; set; }
        public long LastSeenTicks { get; set; }
        public bool InChat { get; set; }
    }

    /// <summary>One removal. ExpiresTicks is 0 for a removal that lasts until the call ends.</summary>
    public sealed class RemovalData
    {
        public string Agent { get; set; }
        public string By { get; set; }
        public long AtTicks { get; set; }
        public long ExpiresTicks { get; set; }
        public string Reason { get; set; }
    }

    public sealed class SessionData
    {
        public int Type { get; set; }
        public string Id { get; set; }
        public string Temp { get; set; }
        public string RoomKey { get; set; }
        public string Owner { get; set; }
        public string Creator { get; set; }
        public long CreatedTicks { get; set; }
        public long LastSeenTicks { get; set; }
        public int Cap { get; set; }
        public string Token { get; set; }
        public bool RingSent { get; set; }
        public List<string> Invited { get; set; } = new List<string>();

        // Moderation. Absent from a copy a service without it made, which reads as no moderation.
        public bool VoiceModerated { get; set; }
        public int ModerationGeneration { get; set; }
        public List<string> VoiceMuted { get; set; } = new List<string>();
        public List<string> VoiceAllowed { get; set; } = new List<string>();
        public List<string> TextMuted { get; set; } = new List<string>();

        // Removals. Absent from a copy a service without them made, which reads as nobody removed.
        public List<RemovalData> Removed { get; set; } = new List<RemovalData>();
        public List<MemberData> Members { get; set; } = new List<MemberData>();
    }

    /// <summary>One reply from the service. Which fields matter depends on the operation.</summary>
    public sealed class SessionReply
    {
        public bool Ok { get; set; }
        public string Decision { get; set; }
        public bool Created { get; set; }
        public bool StartedRinging { get; set; }
        public SessionData Session { get; set; }
        public List<SessionData> Sessions { get; set; }

        /// <summary>A string result (the credential from IssueToken).</summary>
        public string Text { get; set; }

        /// <summary>A yes/no result (the request-rate bucket).</summary>
        public bool Flag { get; set; }

        /// <summary>A count result (seats released, seats renewed).</summary>
        public int Count { get; set; }

        /// <summary>
        /// For RenewSeats: the seats named in the call that the service no longer holds because the member was removed from
        /// the call (written "session:agent"). The region that holds the seat takes the member out of its room.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<string> RemovedSeats { get; set; }

        public static SessionReply Fail(string decision) => new SessionReply { Ok = false, Decision = decision };

        public string ToJson() => JsonSerializer.Serialize(this);

        public static SessionReply FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<SessionReply>(json); }
            catch (JsonException) { return null; }
        }
    }

    public static class SessionWire
    {
        /// <summary>Copy a session. The caller holds the store's lock (call from inside Mutate).</summary>
        public static SessionData ToData(NonSpatialVoiceSession s)
        {
            SessionData d = new SessionData
            {
                Type = (int)s.Type,
                Id = s.SessionId.ToString(),
                Temp = s.TempSessionId.ToString(),
                RoomKey = s.RoomKey,
                Owner = s.Owner.ToString(),
                Creator = s.Creator.ToString(),
                CreatedTicks = s.CreatedUtc.Ticks,
                LastSeenTicks = s.LastSeenUtc.Ticks,
                Cap = s.Cap,
                Token = s.Token,
                RingSent = s.RingSent,
            };
            foreach (NonSpatialMember m in s.Members)
            {
                d.Members.Add(new MemberData
                {
                    Agent = m.AgentId.ToString(),
                    State = (int)m.State,
                    Departure = (int)m.Departure,
                    Origin = m.OriginRegion.ToString(),
                    ViewerSession = m.ViewerSession,
                    InvitedTicks = m.InvitedUtc.Ticks,
                    LastSeenTicks = m.LastSeenUtc.Ticks,
                    InChat = m.InChat,
                });
                if (s.WasInvited(m.AgentId)) d.Invited.Add(m.AgentId.ToString());
            }
            d.VoiceModerated = s.VoiceModerated;
            d.ModerationGeneration = s.ModerationGeneration;
            foreach (UUID a in s.VoiceMuted) d.VoiceMuted.Add(a.ToString());
            foreach (UUID a in s.VoiceAllowed) d.VoiceAllowed.Add(a.ToString());
            foreach (UUID a in s.TextMuted) d.TextMuted.Add(a.ToString());
            foreach (KeyValuePair<UUID, CallRemoval> r in s.Removed)
                d.Removed.Add(new RemovalData
                {
                    Agent = r.Key.ToString(),
                    By = r.Value.By.ToString(),
                    AtTicks = r.Value.AtUtc.Ticks,
                    ExpiresTicks = r.Value.ExpiresUtc?.Ticks ?? 0,
                    Reason = r.Value.Reason,
                });
            return d;
        }

        /// <summary>Rebuild a detached session from a copy. Null when the copy is unreadable.</summary>
        public static NonSpatialVoiceSession FromData(SessionData d)
        {
            if (d == null) return null;
            if (!UUID.TryParse(d.Id, out UUID id) || !UUID.TryParse(d.Temp, out UUID temp)
                || !UUID.TryParse(d.Owner, out UUID owner) || !UUID.TryParse(d.Creator, out UUID creator)
                || !Enum.IsDefined(typeof(NonSpatialSessionType), d.Type))
                return null;
            NonSpatialVoiceSession s = new NonSpatialVoiceSession((NonSpatialSessionType)d.Type, id, temp, d.RoomKey,
                                                                  owner, creator, d.Cap, new DateTime(d.CreatedTicks, DateTimeKind.Utc));
            s.LastSeenUtc = new DateTime(d.LastSeenTicks, DateTimeKind.Utc);
            s.Token = d.Token;
            s.RingSent = d.RingSent;
            if (d.Members != null)
                foreach (MemberData md in d.Members)
                {
                    if (!UUID.TryParse(md.Agent, out UUID agent)) continue;
                    UUID.TryParse(md.Origin, out UUID origin);
                    NonSpatialMember m = s.Upsert(agent, (MemberState)md.State, origin, new DateTime(md.InvitedTicks, DateTimeKind.Utc));
                    m.Departure = (DepartureReason)md.Departure;
                    m.ViewerSession = md.ViewerSession;
                    m.InChat = md.InChat;
                    m.LastSeenUtc = new DateTime(md.LastSeenTicks, DateTimeKind.Utc);
                }
            if (d.Invited != null)
                foreach (string a in d.Invited)
                    if (UUID.TryParse(a, out UUID invited)) s.MarkInvited(invited);
            s.RestoreModeration(d.VoiceModerated, d.ModerationGeneration, Ids(d.VoiceMuted), Ids(d.VoiceAllowed), Ids(d.TextMuted));
            if (d.Removed != null)
                foreach (RemovalData rd in d.Removed)
                    if (UUID.TryParse(rd.Agent, out UUID removedAgent))
                    {
                        UUID.TryParse(rd.By, out UUID by);
                        s.RestoreRemoval(removedAgent, new CallRemoval(by, new DateTime(rd.AtTicks, DateTimeKind.Utc), rd.Reason,
                            rd.ExpiresTicks == 0 ? null : new DateTime(rd.ExpiresTicks, DateTimeKind.Utc)));
                    }
            return s;
        }

        private static List<UUID> Ids(List<string> text)
        {
            List<UUID> ids = new List<UUID>();
            if (text != null)
                foreach (string t in text)
                    if (UUID.TryParse(t, out UUID id)) ids.Add(id);
            return ids;
        }

        public static SessionOutcome ToOutcome(SessionReply r)
        {
            if (r == null) return SessionOutcome.Fail(SessionOutcome.StoreUnavailable);
            return new SessionOutcome(r.Ok, r.Decision, FromData(r.Session), r.Created, r.StartedRinging);
        }

        public static List<NonSpatialVoiceSession> ToSessions(SessionReply r)
        {
            List<NonSpatialVoiceSession> list = new List<NonSpatialVoiceSession>();
            if (r?.Sessions != null)
                foreach (SessionData d in r.Sessions)
                {
                    NonSpatialVoiceSession s = FromData(d);
                    if (s != null) list.Add(s);
                }
            return list;
        }
    }
}
