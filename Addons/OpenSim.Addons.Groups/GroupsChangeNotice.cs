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

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;

namespace OpenSim.Groups;

/// <summary>
/// "These groups changed": what Robust's groups service tells the region servers after a change, so each forgets
/// what its groups caches hold about those groups at once instead of keeping it until the cache's 30 s are up. A
/// POST of <c>GroupIDs=uuid,uuid</c> to <see cref="Path"/>, carrying the groups key (<see cref="GroupsSecretKey"/>).
/// Both sides take part only when <c>[Groups] SecretKey</c> is set; without it the caches expire as before.
/// </summary>
public static class GroupsChangeNotice
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public const string Path = "/groups-changed";

    private static readonly List<WeakReference<RemoteConnectorCacheWrapper>> s_caches = new();
    private static int s_listening;

    /// <summary>Every groups cache of this process takes part (called by each cache as it is made).</summary>
    internal static void Register(RemoteConnectorCacheWrapper cache)
    {
        lock (s_caches)
        {
            s_caches.RemoveAll(w => !w.TryGetTarget(out _));
            s_caches.Add(new WeakReference<RemoteConnectorCacheWrapper>(cache));
        }
    }

    /// <summary>Every groups cache of this process forgets the groups; how many caches did.</summary>
    public static int Forget(IEnumerable<UUID> groups)
    {
        List<RemoteConnectorCacheWrapper> caches = new();
        lock (s_caches)
            foreach (var w in s_caches)
                if (w.TryGetTarget(out RemoteConnectorCacheWrapper c))
                    caches.Add(c);

        UUID[] ids = groups.ToArray();
        foreach (RemoteConnectorCacheWrapper c in caches)
            foreach (UUID g in ids)
                c.ForgetGroup(g);
        return caches.Count;
    }

    /// <summary>
    /// Take the notices on this region server's HTTP server, once per process, when a key is set. Robust posts to the
    /// region's server URI, which is this server.
    /// </summary>
    public static bool Listen(IHttpServer server, string key)
    {
        if (server is null || string.IsNullOrEmpty(key))
            return false;
        if (Interlocked.Exchange(ref s_listening, 1) == 1)
            return true;
        server.AddStreamHandler(new GroupsChangedHandler(key));
        m_log.LogInformation("[Groups]: taking Robust's notices of group changes at {0}", Path);
        return true;
    }

    /// <summary>The body of a notice.</summary>
    public static string Body(IEnumerable<UUID> groups) => "GroupIDs=" + string.Join(",", groups);

    /// <summary>The groups a notice names; malformed ids are left out.</summary>
    public static List<UUID> Groups(string body)
    {
        var groups = new List<UUID>();
        foreach (string part in (body ?? string.Empty).Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq < 0 || !part[..eq].Equals("GroupIDs", StringComparison.Ordinal))
                continue;
            foreach (string id in WebUtility.UrlDecode(part[(eq + 1)..]).Split(','))
                if (UUID.TryParse(id.Trim(), out UUID g) && g.IsNotZero())
                    groups.Add(g);
        }
        return groups;
    }
}

/// <summary>A region server's end of <see cref="GroupsChangeNotice"/>.</summary>
public sealed class GroupsChangedHandler : BaseStreamHandler
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private readonly string m_key;
    private long m_lastRefusalLog = -60001;

    public GroupsChangedHandler(string key) : base("POST", GroupsChangeNotice.Path)
    {
        m_key = key ?? string.Empty;
    }

    protected override byte[] ProcessRequest(string path, Stream requestData, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        if (m_key.Length == 0 || !GroupsSecretKey.Matches(m_key, httpRequest.Headers?[GroupsSecretKey.Header]))
        {
            // the region's port is public: say so at most once a minute
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref m_lastRefusalLog);
            if (now - last > 60000 && Interlocked.CompareExchange(ref m_lastRefusalLog, now, last) == last)
                m_log.LogWarning("[Groups]: refused a notice of group changes from {0}: it did not carry the groups SecretKey",
                    httpRequest.RemoteIPEndPoint);
            httpResponse.StatusCode = (int)HttpStatusCode.Forbidden;
            return Array.Empty<byte>();
        }

        string body;
        using (var reader = new StreamReader(requestData))
            body = reader.ReadToEnd();
        List<UUID> groups = GroupsChangeNotice.Groups(body);
        int caches = GroupsChangeNotice.Forget(groups);
        m_log.LogDebug("[Groups]: {0} group(s) changed; {1} cache(s) forgot them", groups.Count, caches);

        httpResponse.StatusCode = (int)HttpStatusCode.OK;
        return Encoding.UTF8.GetBytes("OK");
    }
}

/// <summary>Robust's end of <see cref="GroupsChangeNotice"/>: told of each change, it tells the region servers.</summary>
public interface IGroupsChangeNotifier
{
    void Changed(UUID groupID);
}

/// <summary>
/// Gathers the groups changed over a short while (<see cref="Gather"/>), then posts them once to every region server
/// online (each server once, however many regions it runs), all at once, each with a short timeout. Nothing waits for
/// it and nothing is sent again: a region that misses a notice keeps its answers until its cache's 30 s are up.
/// </summary>
public sealed class GroupsChangeNotifier : IGroupsChangeNotifier, IDisposable
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(250);
    private const int PostTimeoutSeconds = 5;

    private readonly Func<IEnumerable<string>> m_servers;
    private readonly string m_key;
    private readonly Func<string, string, string, bool> m_post;
    private readonly TimeSpan m_gather;
    private readonly ConcurrentDictionary<UUID, byte> m_pending = new();
    private int m_scheduled;
    private volatile bool m_disposed;

    /// <param name="servers">The region servers' URIs (several regions may share one).</param>
    /// <param name="post">Sends one notice (server URI, body, key); replaced in tests. True when it was taken.</param>
    public GroupsChangeNotifier(Func<IEnumerable<string>> servers, string key,
        Func<string, string, string, bool> post = null, TimeSpan? gather = null)
    {
        m_servers = servers;
        m_key = key;
        m_post = post ?? Post;
        m_gather = gather ?? Gather;
    }

    /// <summary>The region servers online in the grid service, each once.</summary>
    public static Func<IEnumerable<string>> ServersOf(OpenSim.Services.Interfaces.IGridService grid)
        => () => grid.GetOnlineRegions(UUID.Zero, 0, 0, int.MaxValue)
            .Select(r => r.ServerURI)
            .Where(u => !string.IsNullOrEmpty(u));

    public void Changed(UUID groupID)
    {
        if (m_disposed || groupID.IsZero())
            return;
        m_pending.TryAdd(groupID, 0);
        if (Interlocked.Exchange(ref m_scheduled, 1) == 0)
            _ = Task.Run(SendSoon);
    }

    private async Task SendSoon()
    {
        await Task.Delay(m_gather);
        Interlocked.Exchange(ref m_scheduled, 0);

        List<UUID> groups = new();
        foreach (UUID g in m_pending.Keys)
            if (m_pending.TryRemove(g, out _))
                groups.Add(g);
        if (groups.Count == 0)
            return;

        List<string> servers;
        try
        {
            servers = m_servers()
                .Select(u => u.TrimEnd('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e)
        {
            m_log.LogWarning("[Groups]: could not list the region servers to tell of group changes: {0}", e.Message);
            return;
        }

        string body = GroupsChangeNotice.Body(groups);
        await Task.WhenAll(servers.Select(server => Task.Run(() =>
        {
            try
            {
                if (!m_post(server + GroupsChangeNotice.Path, body, m_key))
                    m_log.LogDebug("[Groups]: {0} did not take the notice of group changes", server);
            }
            catch (Exception e)
            {
                m_log.LogDebug("[Groups]: could not tell {0} of group changes: {1}", server, e.Message);
            }
        })));
    }

    private static bool Post(string url, string body, string key)
    {
        string reply = SynchronousRestFormsRequester.MakePostRequest(url, body, new GroupsSecretKeyAuth(key, null),
            PostTimeoutSeconds, keepalive: false);
        return reply == "OK";
    }

    public void Dispose() => m_disposed = true;
}
