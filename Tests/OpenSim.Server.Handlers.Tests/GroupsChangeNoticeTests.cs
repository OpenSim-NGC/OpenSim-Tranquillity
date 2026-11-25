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

using System.Collections.Specialized;
using System.Net;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Groups;
using OpenSim.Tests.Common;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Robust tells the region servers of group changes (GroupsChangeNotice), so a change made through one region is
/// seen at once on the others instead of after their caches' 30 s. A region takes a notice only with the groups key.
/// </summary>
[Collection(GroupsCachesCollection.Name)]
public class GroupsChangeNoticeTests
{
    private const string Key = "the groups key";

    /// <summary>A region cache with one member list cached, and how many times it has been fetched.</summary>
    private sealed class CachedRegion
    {
        public readonly RemoteConnectorCacheWrapper Cache = new(null);
        public readonly UUID Group = UUID.Random();
        private readonly string m_reader = UUID.Random().ToString();
        public int Fetches;

        public CachedRegion()
        {
            Read();
            Read();
            Assert.Equal(1, Fetches);
        }

        public void Read() => Cache.GetGroupMembers(m_reader, Group, () => { Fetches++; return new List<ExtendedGroupMembersData>(); });
    }

    private static int Notify(string key, string body)
    {
        var headers = new NameValueCollection();
        if (key != null)
            headers[GroupsSecretKey.Header] = key;
        var response = new TestOSHttpResponse { StatusCode = 200 };
        new GroupsChangedHandler(Key).Handle(GroupsChangeNotice.Path, new MemoryStream(Encoding.UTF8.GetBytes(body)),
            new TestOSHttpRequest { Headers = headers }, response);
        return response.StatusCode;
    }

    [Fact]
    public void ARegionForgetsAGroupItIsToldOf()
    {
        var region = new CachedRegion();
        Assert.Equal(200, Notify(Key, GroupsChangeNotice.Body(new[] { region.Group })));
        region.Read();
        Assert.Equal(2, region.Fetches);
    }

    [Fact]
    public void ARegionTakesNoNoticeWithoutTheKey()
    {
        var region = new CachedRegion();
        Assert.Equal(403, Notify(null, GroupsChangeNotice.Body(new[] { region.Group })));
        Assert.Equal(403, Notify("not the key", GroupsChangeNotice.Body(new[] { region.Group })));
        region.Read();
        Assert.Equal(1, region.Fetches);
    }

    [Fact]
    public void AChangeToOneGroupKeepsWhatIsCachedAboutAnother()
    {
        var a = new CachedRegion();
        var b = new CachedRegion();
        GroupsChangeNotice.Forget(new[] { a.Group });
        a.Read();
        b.Read();
        Assert.Equal(2, a.Fetches);   // fetched again
        Assert.Equal(1, b.Fetches);   // still cached
    }

    [Fact]
    public void AnAvatarsOwnGroupsAreForgottenWhenTheyChange()
    {
        // an avatar's list of groups, cached; they join a group that is not in it: only their name tells
        var cache = new RemoteConnectorCacheWrapper(null);
        string agent = UUID.Random() + ";http://elsewhere.example:8002/;Visiting Person";
        UUID other = UUID.Random();
        int fetches = 0;
        List<GroupMembershipData> Fetch() { fetches++; return new List<GroupMembershipData>(); }

        cache.GetAgentGroupMemberships(agent, Fetch);
        GroupsChangeNotice.Forget(new[] { other });
        cache.GetAgentGroupMemberships(agent, Fetch);
        Assert.Equal(1, fetches);   // a group they are not in changed: kept

        Assert.Equal(200, Notify(Key, GroupsChangeNotice.Body(new[] { other }, new[] { agent.ToUpperInvariant() })));
        cache.GetAgentGroupMemberships(agent, Fetch);
        Assert.Equal(2, fetches);   // they changed: fetched again
    }

    [Fact]
    public void ANoticeNamesItsAvatarsToo()
    {
        UUID g = UUID.Random();
        string visitor = UUID.Random() + ";http://elsewhere.example:8002/;Visiting Person";
        string local = UUID.Random().ToString();
        string body = GroupsChangeNotice.Body(new[] { g }, new[] { visitor, local });
        Assert.Equal(new[] { g }, GroupsChangeNotice.Groups(body));
        Assert.Equal(new[] { visitor, local }, GroupsChangeNotice.Agents(body));
        Assert.Empty(GroupsChangeNotice.Agents(GroupsChangeNotice.Body(new[] { g })));
    }

    [Fact]
    public void ANoticeNamesItsGroupsAndNothingElse()
    {
        UUID a = UUID.Random(), b = UUID.Random();
        Assert.Equal(new[] { a, b }, GroupsChangeNotice.Groups(GroupsChangeNotice.Body(new[] { a, b })));
        Assert.Equal(new[] { a }, GroupsChangeNotice.Groups("GroupIDs=" + a + ",not-a-uuid," + UUID.Zero + "&Other=" + b));
        Assert.Equal(new[] { a, b }, GroupsChangeNotice.Groups("GroupIDs=" + WebUtility.UrlEncode(a + "," + b)));
        Assert.Empty(GroupsChangeNotice.Groups(string.Empty));
    }

    [Fact]
    public void RobustTellsEachRegionServerOnceOfTheChangesGathered()
    {
        var posts = new System.Collections.Concurrent.ConcurrentBag<(string Url, string Body, string Key)>();
        string[] servers = { "http://sim-a.example:9000/", "http://sim-a.example:9000", "http://SIM-A.example:9000/", "http://sim-b.example:9010/" };
        using var notifier = new GroupsChangeNotifier(() => servers, Key,
            (url, body, key) => { posts.Add((url, body, key)); return true; }, TimeSpan.FromMilliseconds(100));

        UUID a = UUID.Random(), b = UUID.Random();
        notifier.Changed(a);
        notifier.Changed(b);
        notifier.Changed(a);

        Assert.True(WaitFor(() => posts.Count >= 2, TimeSpan.FromSeconds(5)), $"{posts.Count} posts");
        Thread.Sleep(300);   // nothing more comes
        Assert.Equal(2, posts.Count);
        Assert.Equal(new[] { "http://sim-a.example:9000" + GroupsChangeNotice.Path, "http://sim-b.example:9010" + GroupsChangeNotice.Path },
            posts.Select(p => p.Url).OrderBy(u => u, StringComparer.Ordinal));
        Assert.All(posts, p => Assert.Equal(Key, p.Key));
        Assert.All(posts, p => Assert.Equal(new[] { a, b }.OrderBy(g => g), GroupsChangeNotice.Groups(p.Body).OrderBy(g => g)));
    }

    [Fact]
    public void ARegionServerThatCannotBeReachedDoesNotStopTheOthers()
    {
        var reached = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var notifier = new GroupsChangeNotifier(() => new[] { "http://down.example/", "http://up.example/" }, Key,
            (url, body, key) =>
            {
                if (url.StartsWith("http://down"))
                    throw new WebException("down");
                reached.Add(url);
                return true;
            },
            TimeSpan.FromMilliseconds(50));
        notifier.Changed(UUID.Random());
        Assert.True(WaitFor(() => reached.Count == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RobustsHandlerTellsOfAChangeAndNotOfAQuestion()
    {
        var told = new System.Collections.Concurrent.ConcurrentBag<UUID>();
        var recorder = new Recorder(told);
        var handler = new GroupsServicePostHandler(SqliteGroupsStoreTests.Service, null, string.Empty, recorder);
        string owner = UUID.Random().ToString();
        UUID group = SqliteGroupsStoreTests.Service.CreateGroup(owner, "Notice " + Guid.NewGuid().ToString("N")[..8], "", true,
            UUID.Zero, 0, false, true, false, new UUID(owner), out string reason);
        Assert.True(group.IsNotZero(), reason);

        Post(handler, $"METHOD=GETGROUP&RequestingAgentID={owner}&GroupID={group}");
        Assert.Empty(told);

        Post(handler, $"METHOD=UPDATEMEMBERSHIP&RequestingAgentID={owner}&AgentID={owner}&GroupID={group}&AcceptNotices=False&ListInProfile=True");
        Assert.Equal(new[] { group }, told);
        Assert.Equal(new[] { owner }, recorder.Agents);
    }

    [Fact]
    public void AChangeThroughRobustReachesARegionOverHttp()
    {
        // a stand-in region server: its HTTP listener hands each notice to the region's handler
        var region = new CachedRegion();
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var handled = Task.Run(() =>
        {
            HttpListenerContext ctx = listener.GetContext();
            var headers = new NameValueCollection();
            headers[GroupsSecretKey.Header] = ctx.Request.Headers[GroupsSecretKey.Header];
            var response = new TestOSHttpResponse { StatusCode = 200 };
            byte[] reply = new GroupsChangedHandler(Key).Handle(ctx.Request.Url.AbsolutePath, ctx.Request.InputStream,
                new TestOSHttpRequest { Headers = headers }, response);
            ctx.Response.StatusCode = response.StatusCode;
            ctx.Response.ContentLength64 = reply.Length;
            ctx.Response.OutputStream.Write(reply);
            ctx.Response.Close();
        });

        using var notifier = new GroupsChangeNotifier(() => new[] { $"http://127.0.0.1:{port}/" }, Key, gather: TimeSpan.FromMilliseconds(50));
        notifier.Changed(region.Group);

        Assert.True(handled.Wait(TimeSpan.FromSeconds(10)), "the region was not told");
        region.Read();
        Assert.Equal(2, region.Fetches);
    }

    [Fact]
    public void BansGoThroughRobust()
    {
        // a stand-in Robust: its HTTP listener hands each request to the real groups handler
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var handler = new GroupsServicePostHandler(SqliteGroupsStoreTests.Service, null, string.Empty);
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                var response = new TestOSHttpResponse { StatusCode = 200 };
                byte[] reply = handler.Handle("/groups", ctx.Request.InputStream,
                    new TestOSHttpRequest { Headers = new NameValueCollection() }, response);
                ctx.Response.StatusCode = response.StatusCode;
                ctx.Response.ContentLength64 = reply.Length;
                ctx.Response.OutputStream.Write(reply);
                ctx.Response.Close();
            }
        });

        var config = new Nini.Config.IniConfigSource();
        config.AddConfig("Groups").Set("GroupsServerURI", $"http://127.0.0.1:{port}/");
        var robust = new GroupsServiceRemoteConnector(config);

        string owner = UUID.Random().ToString(), member = UUID.Random().ToString(), stranger = UUID.Random().ToString();
        UUID group = SqliteGroupsStoreTests.Service.CreateGroup(owner, "Robust bans " + Guid.NewGuid().ToString("N")[..8], "",
            true, UUID.Zero, 0, true, true, false, new UUID(owner), out string reason);
        Assert.True(group.IsNotZero(), reason);
        Assert.True(SqliteGroupsStoreTests.Service.AddAgentToGroup(member, member, group, UUID.Zero, string.Empty, out reason), reason);

        Assert.Empty(robust.GetGroupBans(owner, group));          // none yet: an empty list
        Assert.Null(robust.GetGroupBans(member, group));          // not theirs to see: no list at all
        Assert.False(robust.ChangeGroupBans(member, group, true, new[] { stranger }));
        Assert.True(robust.ChangeGroupBans(owner, group, true, new[] { stranger }));

        ExtendedGroupBanData ban = Assert.Single(robust.GetGroupBans(owner, group));
        Assert.Equal(stranger, ban.AgentID);
        Assert.True(ban.BanTime > 0);

        Assert.True(robust.ChangeGroupBans(owner, group, false, new[] { stranger }));
        Assert.Empty(robust.GetGroupBans(owner, group));
        stop.Cancel();
    }

    private sealed class Recorder : IGroupsChangeNotifier
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<UUID> m_told;
        public Recorder(System.Collections.Concurrent.ConcurrentBag<UUID> told) => m_told = told;
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Agents = new();
        public void Changed(UUID groupID, string agentID = null)
        {
            m_told.Add(groupID);
            if (agentID != null)
                Agents.Add(agentID);
        }
    }

    private static void Post(GroupsServicePostHandler handler, string body)
        => handler.Handle("/groups", new MemoryStream(Encoding.UTF8.GetBytes(body)),
            new TestOSHttpRequest { Headers = new NameValueCollection() }, new TestOSHttpResponse { StatusCode = 200 });

    private static bool WaitFor(Func<bool> done, TimeSpan within)
    {
        var t = System.Diagnostics.Stopwatch.StartNew();
        while (t.Elapsed < within)
        {
            if (done())
                return true;
            Thread.Sleep(20);
        }
        return done();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
