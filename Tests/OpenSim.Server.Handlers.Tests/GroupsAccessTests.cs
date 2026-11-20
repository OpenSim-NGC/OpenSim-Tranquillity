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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Groups;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Who Groups V2 believes. Robust's groups service takes the requesting avatar from the request, so with
/// <c>[Groups] SecretKey</c> set it answers only callers that send the key, and the regions send it. The Hypergrid
/// groups service, on the public port, keeps a proxy group at the grid it came from, and takes a notice for a proxy
/// group only when that grid confirms sending it.
/// </summary>
[Collection("SQLite stores")]
public class GroupsAccessTests
{
    private const string Key = "the groups key";

    private static GroupsService Service => SqliteGroupsStoreTests.Service;

    private static (UUID Group, string Owner) NewGroup()
    {
        string owner = UUID.Random().ToString();
        UUID id = Service.CreateGroup(owner, "Access " + Guid.NewGuid().ToString("N")[..8], "", true, UUID.Zero, 0,
            false, true, false, new UUID(owner), out string reason);
        Assert.True(id.IsNotZero(), reason);
        return (id, owner);
    }

    /// <summary>A GETGROUP request to Robust's groups handler, with the key header when <paramref name="key"/> is set.</summary>
    private static (int Status, string Body) GetGroup(GroupsServicePostHandler handler, UUID group, string owner, string key)
    {
        var headers = new NameValueCollection();
        if (key != null)
            headers[GroupsSecretKey.Header] = key;
        var request = new TestOSHttpRequest { Headers = headers };
        var response = new TestOSHttpResponse { StatusCode = 200 };
        string body = $"METHOD=GETGROUP&RequestingAgentID={owner}&GroupID={group}";
        byte[] reply = handler.Handle("/groups", new MemoryStream(Encoding.UTF8.GetBytes(body)), request, response);
        return (response.StatusCode, Encoding.UTF8.GetString(reply ?? []));
    }

    [Fact]
    public void WithAKeyRobustAnswersOnlyCallersThatSendIt()
    {
        var (group, owner) = NewGroup();
        var handler = new GroupsServicePostHandler(Service, null, Key);

        Assert.Equal(403, GetGroup(handler, group, owner, null).Status);
        Assert.Equal(403, GetGroup(handler, group, owner, "not the key").Status);
        Assert.Equal(403, GetGroup(handler, group, owner, Key + "x").Status);

        var (status, body) = GetGroup(handler, group, owner, Key);
        Assert.Equal(200, status);
        Assert.Contains(group.ToString(), body);
    }

    [Fact]
    public void WithoutAKeyRobustAnswersAsBefore()
    {
        var (group, owner) = NewGroup();
        var handler = new GroupsServicePostHandler(Service, null);
        var (status, body) = GetGroup(handler, group, owner, null);
        Assert.Equal(200, status);
        Assert.Contains(group.ToString(), body);
    }

    [Fact]
    public void TheKeyIsAddedToBothKindsOfHeaders()
    {
        var auth = new GroupsSecretKeyAuth(Key, null);
        var plain = new NameValueCollection();
        auth.AddAuthorization(plain);
        Assert.Equal(Key, plain[GroupsSecretKey.Header]);

        using var message = new System.Net.Http.HttpRequestMessage();
        auth.AddAuthorization(message.Headers);
        auth.AddAuthorization(message.Headers);   // once, however often it is added
        Assert.Equal(new[] { Key }, message.Headers.GetValues(GroupsSecretKey.Header));
    }

    [Fact]
    public void ARegionSendsTheKeyToRobust()
    {
        // a stand-in for Robust's groups service that records the key each request carries
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = new List<string>();
        var served = Task.Run(() =>
        {
            HttpListenerContext ctx = listener.GetContext();
            lock (seen)
                seen.Add(ctx.Request.Headers[GroupsSecretKey.Header]);
            byte[] reply = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><ServerResponse><RESULT>NULL</RESULT><REASON>none</REASON></ServerResponse>");
            ctx.Response.ContentType = "text/xml";
            ctx.Response.OutputStream.Write(reply);
            ctx.Response.Close();
        });

        var config = new IniConfigSource();
        IConfig groups = config.AddConfig("Groups");
        groups.Set("GroupsServerURI", $"http://127.0.0.1:{port}/");
        groups.Set("SecretKey", Key);
        var connector = new GroupsServiceRemoteConnector(config);
        connector.GetGroupRecord(UUID.Random().ToString(), UUID.Random(), string.Empty);

        Assert.True(served.Wait(TimeSpan.FromSeconds(10)), "the region sent no request");
        Assert.Equal(new[] { Key }, seen);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    #region Hypergrid
    private sealed class NoOfflineIM : IOfflineIMService
    {
        public readonly List<GridInstantMessage> Stored = new();
        public List<GridInstantMessage> GetMessages(UUID principalID) => new();
        public bool StoreMessage(GridInstantMessage im, out string reason) { Stored.Add(im); reason = string.Empty; return true; }
        public void DeleteMessages(UUID userID) { }
    }

    private static readonly System.Lazy<HGGroupsService> s_hg =
        new(() => new HGGroupsService(SqliteGroupsStoreTests.Config(), new NoOfflineIM(), null, "http://home.example:8002/"));

    private const string Origin = "http://origin.example:8002/";

    private static string Visitor() => UUID.Random() + ";" + Origin + ";Far Away";

    private static UUID NewProxy(out string member)
    {
        UUID group = UUID.Random();
        member = UUID.Random().ToString();
        Assert.True(s_hg.Value.CreateGroupProxy(Visitor(), member, "token-" + group, group, Origin, "Far Group", out string reason), reason);
        return group;
    }

    [Fact]
    public void AProxyGroupStaysWithTheGridItCameFrom()
    {
        UUID group = NewProxy(out _);

        // another grid sends the same group as its own: refused, the proxy keeps its location
        string newcomer = UUID.Random().ToString();
        Assert.False(s_hg.Value.CreateGroupProxy(Visitor(), newcomer, "theirs", group, "http://elsewhere.example:8002/", "Taken", out string reason));
        Assert.NotEmpty(reason);
        Assert.Equal(Origin, s_hg.Value.GetGroupRecord(newcomer, group).ServiceLocation);
        Assert.Null(s_hg.Value.GetAgentGroupMembership(newcomer, newcomer, group));

        // the same grid, written another way, still adds its members
        string second = UUID.Random().ToString();
        Assert.True(s_hg.Value.CreateGroupProxy(Visitor(), second, "token-2", group, Origin.TrimEnd('/').ToUpperInvariant(), "Far Group", out reason), reason);
        Assert.NotNull(s_hg.Value.GetAgentGroupMembership(second, second, group));
    }

    [Fact]
    public void ALocalGroupCannotBeMadeAProxy()
    {
        var (group, owner) = NewGroup();
        Assert.False(s_hg.Value.CreateGroupProxy(Visitor(), UUID.Random().ToString(), "t", group, Origin, "Local", out _));
        Assert.Equal(string.Empty, s_hg.Value.GetGroupRecord(owner, group).ServiceLocation ?? string.Empty);
    }

    [Fact]
    public void ANoticeIsTakenOnlyWhenItsGridConfirmsIt()
    {
        UUID group = NewProxy(out _);
        HGGroupsService hg = s_hg.Value;
        var asked = new List<(string, UUID, UUID)>();
        Func<string, UUID, UUID, bool> before = hg.NoticeIsFromOrigin;
        try
        {
            bool confirm = false;
            hg.NoticeIsFromOrigin = (location, notice, g) => { asked.Add((location, notice, g)); return confirm; };

            UUID forged = UUID.Random();
            Assert.False(hg.AddNotice(Visitor(), group, forged, "Someone", "Forged", "body", false, 0, "", UUID.Zero, ""));
            Assert.Null(hg.GetGroupNotice(string.Empty, forged));
            Assert.Equal((Origin, forged, group), Assert.Single(asked));

            confirm = true;
            UUID real = UUID.Random();
            Assert.True(hg.AddNotice(Visitor(), group, real, "Someone", "Real", "body", false, 0, "", UUID.Zero, ""));
            Assert.NotNull(hg.GetGroupNotice(string.Empty, real));

            // a grid that cannot be asked confirms nothing
            hg.NoticeIsFromOrigin = (_, _, _) => throw new WebException("unreachable");
            UUID unasked = UUID.Random();
            Assert.False(hg.AddNotice(Visitor(), group, unasked, "Someone", "Unasked", "body", false, 0, "", UUID.Zero, ""));
            Assert.Null(hg.GetGroupNotice(string.Empty, unasked));
        }
        finally
        {
            hg.NoticeIsFromOrigin = before;
        }
    }
    #endregion
}
