/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Diagnostics;
using System.Net;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Groups;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// A region's groups calls to Robust (and to other grids' Hypergrid groups services) run side by side: they used to
/// wait for each other behind one lock per connector, so every arrival and member list waited for the slowest call.
/// A stand-in server takes half a second per request; six calls at once take about that, not three seconds.
/// Each caller has its own thread, so how many run at once does not depend on how fast the thread pool grows.
/// </summary>
[Collection(GroupsCachesCollection.Name)]
public class GroupsConnectorConcurrencyTests
{
    private const int Calls = 6;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    /// <summary>A server answering every request after <see cref="Delay"/>, each on its own task.</summary>
    private sealed class SlowServer : IDisposable
    {
        private readonly HttpListener m_listener = new();
        private readonly CancellationTokenSource m_stop = new();
        public int Port { get; }
        public int Served;
        public int MostAtOnce;
        private int m_now;

        public SlowServer(string reply)
        {
            Port = FreePort();
            m_listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            m_listener.Start();
            byte[] body = Encoding.UTF8.GetBytes(reply);
            _ = Task.Run(async () =>
            {
                while (!m_stop.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await m_listener.GetContextAsync(); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        int now = Interlocked.Increment(ref m_now);
                        int seen;
                        while ((seen = MostAtOnce) < now && Interlocked.CompareExchange(ref MostAtOnce, now, seen) != seen) { }
                        await Task.Delay(Delay);
                        Interlocked.Decrement(ref m_now);
                        Interlocked.Increment(ref Served);
                        ctx.Response.ContentType = "text/xml";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    });
                }
            });
        }

        public void Dispose()
        {
            m_stop.Cancel();
            m_listener.Stop();
            m_listener.Close();
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Runs <paramref name="call"/> on <paramref name="count"/> threads of its own, started together.</summary>
    private static bool OnThreads(int count, Action<int> call, TimeSpan limit)
    {
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var go = new ManualResetEventSlim(false);
        Thread[] threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            go.Wait();
            try { call(i); }
            catch (Exception e) { errors.Enqueue(e); }
        }) { IsBackground = true }).ToArray();
        foreach (Thread t in threads)
            t.Start();
        go.Set();

        var deadline = Stopwatch.StartNew();
        foreach (Thread t in threads)
            if (!t.Join(limit - deadline.Elapsed > TimeSpan.Zero ? limit - deadline.Elapsed : TimeSpan.Zero))
                return false;
        if (!errors.IsEmpty)
            throw new AggregateException(errors);
        return true;
    }

    private static TimeSpan AllAtOnce(Action call)
    {
        var t = Stopwatch.StartNew();
        Assert.True(OnThreads(Calls, _ => call(), TimeSpan.FromSeconds(20)), "calls still waiting");
        return t.Elapsed;
    }

    [Fact]
    public void TheRegionsCacheFetchesAGroupOnceForEveryoneWaitingOnIt()
    {
        // many callers at once: one fetch per group, the others wait for it; different groups fetch side by side
        var cache = new RemoteConnectorCacheWrapper(null);
        UUID[] groups = Enumerable.Range(0, 4).Select(_ => UUID.Random()).ToArray();
        var fetches = new System.Collections.Concurrent.ConcurrentDictionary<UUID, int>();
        int atOnce = 0, mostAtOnce = 0;

        ExtendedGroupRecord Fetch(UUID g)
        {
            fetches.AddOrUpdate(g, 1, (_, n) => n + 1);
            int now = Interlocked.Increment(ref atOnce);
            int seen;
            while ((seen = mostAtOnce) < now && Interlocked.CompareExchange(ref mostAtOnce, now, seen) != seen) { }
            Thread.Sleep(200);
            Interlocked.Decrement(ref atOnce);
            return new ExtendedGroupRecord { GroupID = g, GroupName = "G " + g };
        }

        Assert.True(OnThreads(40, i =>
        {
            UUID g = groups[i % groups.Length];
            ExtendedGroupRecord r = cache.GetGroupRecord(UUID.Random().ToString(), g, null, () => Fetch(g));
            Assert.Equal(g, r.GroupID);
        }, TimeSpan.FromSeconds(20)), "callers still waiting");

        Assert.All(groups, g => Assert.Equal(1, fetches[g]));
        Assert.True(mostAtOnce > 1, "the groups were fetched one at a time");
    }

    /// <summary>One avatar reads the member list (cached), another changes the group through the same region.</summary>
    private static void AChangeIsSeenByEveryoneOnTheRegion(Action<RemoteConnectorCacheWrapper, UUID> change)
    {
        var cache = new RemoteConnectorCacheWrapper(null);
        UUID group = UUID.Random();
        string reader = UUID.Random().ToString();
        int fetches = 0;
        List<ExtendedGroupMembersData> Fetch() { fetches++; return new List<ExtendedGroupMembersData>(); }

        cache.GetGroupMembers(reader, group, Fetch);
        cache.GetGroupMembers(reader, group, Fetch);
        Assert.Equal(1, fetches);   // cached

        change(cache, group);

        cache.GetGroupMembers(reader, group, Fetch);
        Assert.Equal(2, fetches);   // fetched again after the change
    }

    [Fact]
    public void ARoleGivenIsSeenInEveryonesMemberList()
        => AChangeIsSeenByEveryoneOnTheRegion((c, g) =>
            c.AddAgentToGroupRole(UUID.Random().ToString(), UUID.Random().ToString(), g, UUID.Random(), () => true));

    [Fact]
    public void ARoleTakenIsSeenInEveryonesMemberList()
        => AChangeIsSeenByEveryoneOnTheRegion((c, g) =>
            c.RemoveAgentFromGroupRole(UUID.Random().ToString(), UUID.Random().ToString(), g, UUID.Random(), () => true));

    [Fact]
    public void AMemberWhoLeftIsGoneFromEveryonesMemberList()
        => AChangeIsSeenByEveryoneOnTheRegion((c, g) =>
            c.RemoveAgentFromGroup(UUID.Random().ToString(), UUID.Random().ToString(), g, () => { }));

    [Fact]
    public void AMemberWhoTurnedNoticesOffIsSeenSoByEveryone()
        => AChangeIsSeenByEveryoneOnTheRegion((c, g) =>
            c.UpdateMembership(UUID.Random().ToString(), g, false, true, () => { }));

    [Fact]
    public void ATitleChosenIsSeenInEveryonesMemberList()
        => AChangeIsSeenByEveryoneOnTheRegion((c, g) =>
            c.SetAgentActiveGroupRole(UUID.Random().ToString(), g, () => { }));

    [Fact]
    public void AGroupUpdateTheServiceDoesNotAnswerIsAFailureNotACrash()
    {
        var cache = new RemoteConnectorCacheWrapper(null);
        Assert.False(cache.UpdateGroup(UUID.Random(), () => null));
    }

    [Fact]
    public void ARegionsCallsToRobustRunSideBySide()
    {
        using var server = new SlowServer("<?xml version=\"1.0\"?><ServerResponse><RESULT>NULL</RESULT><REASON>none</REASON></ServerResponse>");
        var config = new IniConfigSource();
        config.AddConfig("Groups").Set("GroupsServerURI", $"http://127.0.0.1:{server.Port}/");
        var connector = new GroupsServiceRemoteConnector(config);

        TimeSpan took = AllAtOnce(() => connector.GetGroupRecord(UUID.Random().ToString(), UUID.Random(), string.Empty));

        Assert.Equal(Calls, server.Served);
        Assert.True(server.MostAtOnce > 1, $"the calls came one at a time ({took.TotalMilliseconds:0} ms)");
        Assert.True(took < Delay * (Calls - 1), $"{Calls} calls took {took.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void CallsToAnotherGridsGroupsRunSideBySide()
    {
        using var server = new SlowServer("<?xml version=\"1.0\"?><ServerResponse><RESULT>true</RESULT></ServerResponse>");
        var connector = new GroupsServiceHGConnector($"http://127.0.0.1:{server.Port}/");

        TimeSpan took = AllAtOnce(() => Assert.True(connector.VerifyNotice(UUID.Random(), UUID.Random())));

        Assert.Equal(Calls, server.Served);
        Assert.True(server.MostAtOnce > 1, $"the calls came one at a time ({took.TotalMilliseconds:0} ms)");
        Assert.True(took < Delay * (Calls - 1), $"{Calls} calls took {took.TotalMilliseconds:0} ms");
    }
}

/// <summary>
/// The tests of the region's groups caches run alone. Every cache of the process hears a change notice
/// (<see cref="GroupsChangeNotice"/>), so a notice test running beside a cache test would make it forget what it is
/// counting; and the concurrency tests time their calls, which other tests running at once would slow.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class GroupsCachesCollection
{
    public const string Name = "Groups caches";
}
