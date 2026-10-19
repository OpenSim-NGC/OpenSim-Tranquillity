/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The same fixed set of requests, sent to a region that has every optional voice switch off. It compiles against
 * both this tree and the unmodified addon in NGC develop, so the two can be compared answer for answer.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    internal sealed class ProbeRow
    {
        public string Name;
        public int Status;
        public bool Provisioned;
        public string Response;
        public int NewRooms;
        public override string ToString() =>
            $"{Name,-44} HTTP {Status}  provisioned={Provisioned,-5} response={Response,-22} new rooms on the gateway={NewRooms}";
    }

    internal static class DefaultsProbe
    {
        public static async Task<List<ProbeRow>> RunAsync(StockRegion region, UUID agent, RawJanus janus)
        {
            var rows = new List<ProbeRow>();
            using var peer = await AiortcPeer.StartAsync();
            UUID other = UUID.Random();

            async Task Probe(string name, Func<Task<CapAnswer>> send)
            {
                List<int> before = await janus.RoomsAsync();
                CapAnswer a;
                string thrown = null;
                try { a = await send(); }
                catch (Exception e)
                {
                    // A handler that throws is a finding of its own: the HTTP layer answers it with an empty 500.
                    a = new CapAnswer { Status = 500 };
                    thrown = e.GetBaseException().GetType().Name;
                }
                List<int> after = await janus.RoomsAsync();
                rows.Add(new ProbeRow
                {
                    Name = name,
                    Status = a.Status,
                    Provisioned = a.Provisioned,
                    Response = thrown ?? (a.Body is not null && a.Body.ContainsKey("response") ? a.Body["response"].AsString() : "-"),
                    NewRooms = after.Except(before).Count(),
                });
            }

            OSDMap Bare(string channelType, string channel)
            {
                OSDMap m = StockRegion.MultiagentBody(peer.Offer, channel, "credentials");
                if (channelType is null) m.Remove("channel_type"); else m["channel_type"] = OSD.FromString(channelType);
                return m;
            }

            OSDMap Chat(string method) => new OSDMap
            {
                ["method"] = OSD.FromString(method),
                ["session-id"] = OSD.FromUUID(UUID.Random()),
            };

            await Probe("provision multiagent, unknown session", () => region.ProvisionAsync(agent, Bare("multiagent", other.ToString())));
            await Probe("provision multiagent, group-style channel", () => region.ProvisionAsync(agent, Bare("multiagent", "nsv1:group:" + other)));
            await Probe("provision with no channel_type", () => region.ProvisionAsync(agent, Bare(null, other.ToString())));
            await Probe("provision channel_type=group", () => region.ProvisionAsync(agent, Bare("group", other.ToString())));
            await Probe("logout, unknown viewer session", () => region.ProvisionAsync(agent, StockRegion.LogoutBody(UUID.Random().ToString())));
            await Probe("chat session: call", () => region.ChatSessionAsync(agent, Chat("call")));
            await Probe("chat session: start conference", () => region.ChatSessionAsync(agent, Chat("start conference")));
            await Probe("chat session: decline invitation", () => region.ChatSessionAsync(agent, Chat("decline invitation")));
            await Probe("chat session: fetch history", () => region.ChatSessionAsync(agent, Chat("fetch history")));
            await Probe("chat session: unknown method", () => region.ChatSessionAsync(agent, Chat("no such method")));
            return rows;
        }
    }
}
