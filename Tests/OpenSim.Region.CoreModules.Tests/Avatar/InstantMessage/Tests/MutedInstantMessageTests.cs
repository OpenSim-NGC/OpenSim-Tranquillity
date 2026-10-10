/*
 * Copyright (c) Legion Builds
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
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.InstantMessage;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Connectors;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Avatar.InstantMessage.Tests;

/// <summary>
/// An instant message to someone who has muted (blocked) its sender is dropped by the message transfer module,
/// in both transfer modules. The recipient is a root agent in the region, so a delivered message reaches
/// their client at once.
/// </summary>
/// <remarks>
/// The mute list cache is process-wide; each test starts and ends with it empty. This project runs its test
/// classes one at a time (AssemblyInfo.cs).
/// </remarks>
public class MutedInstantMessageTests : OpenSimTestCase
{
    private static readonly UUID RecipientId = new("6b1e04d7-93fa-4c2e-8a57-d0c3f9a2e816");
    private static readonly UUID SenderId = new("e2a7c931-58d4-4b0f-a6e3-71b9d8f04c25");
    private static readonly UUID OtherId = new("40f8b2d6-1c97-4e3a-b5d0-9a26e7c3f158");

    /// <summary>A mute list service holding the recipient's list, in MuteListService's text form.</summary>
    private sealed class StandInMuteService : IMuteListService
    {
        public string Text = string.Empty;
        public bool Throws;
        public int Reads;

        public byte[] MuteListRequest(UUID agent, uint crc)
        {
            Reads++;
            if (Throws)
                throw new InvalidOperationException("mute service down");
            if (crc != 0)
                return new byte[] { 1 };   // MuteListService's "your copy is current"
            return agent.Equals(RecipientId) ? Encoding.UTF8.GetBytes(Text) : Array.Empty<byte>();
        }

        public bool UpdateMute(MuteData mute)
        {
            Text += $"{mute.MuteType} {mute.MuteID} {mute.MuteName}|{mute.MuteFlags}\n";
            return true;
        }

        public bool RemoveMute(UUID agentID, UUID muteID, string muteName)
        {
            Text = string.Concat(Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !l.Contains(muteID.ToString())).Select(l => l + "\n"));
            return true;
        }
    }

    /// <summary>The hypergrid transfer module without its IM server connector, which needs a live HTTP server.</summary>
    private sealed class LocalHGMessageTransferModule : HGMessageTransferModule
    {
        public LocalHGMessageTransferModule(Scene scene)
        {
            m_Enabled = true;
            m_Scenes.Add(scene);
        }
    }

    /// <summary>The file transfer the mute list module needs to be enabled; nothing is sent in these tests.</summary>
    public class StandInXfer : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => true;
    }

    /// <summary>Records warnings, so a test can count the ones the mute check logs.</summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory, ILogger
    {
        public readonly List<string> Warnings = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                lock (Warnings)
                    Warnings.Add(formatter(state, exception));
        }
    }

    private readonly ILoggerFactory m_savedLoggerFactory;
    private readonly RecordingLoggerFactory m_log = new();

    public MutedInstantMessageTests()
    {
        InstantMessageMuteCheck.ForgetAll();
        m_savedLoggerFactory = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = m_log;
    }

    public override void Dispose()
    {
        InstantMessageMuteCheck.ForgetAll();
        LoggerProvider.LoggerFactory = m_savedLoggerFactory;
        m_slowServer?.Dispose();
        base.Dispose();
    }

    private int ReadWarnings()
    {
        lock (m_log.Warnings)
            return m_log.Warnings.Count(w => w.Contains("could not be read"));
    }

    private TestScene m_scene = null!;
    private TestClient m_recipientClient = null!;
    private IMessageTransferModule m_transfer = null!;
    private readonly List<GridInstantMessage> m_received = new();
    private readonly List<bool> m_results = new();
    private int m_undelivered;

    private void SetUpRegion(string module, StandInMuteService? mutes, bool withMuteListModule = false)
    {
        m_scene = new SceneHelpers().SetupScene();
        if (mutes is not null)
            m_scene.RegisterModuleInterface<IMuteListService>(mutes);

        if (withMuteListModule)
        {
            // With no permissions module everyone counts as an administrator, and the mute list module
            // refuses to mute an administrator.
            m_scene.Permissions.OnIsAdministrator += _ => false;
            m_scene.RegisterModuleInterface<IXfer>(DispatchProxy.Create<IXfer, StandInXfer>());
            IniConfigSource config = new();
            config.AddConfig("Messaging").Set("MuteListModule", "MuteListModule");
            MuteListModule mlm = new();
            mlm.Initialise(config);
            mlm.AddRegion(m_scene);
            mlm.RegionLoaded(m_scene);
        }

        if (module == "MessageTransferModule")
        {
            // Not PostInitialise: it registers an XML-RPC handler on the process-wide HTTP server.
            MessageTransferModule mtm = new();
            mtm.Initialise(new IniConfigSource());
            mtm.AddRegion(m_scene);
            mtm.RegionLoaded(m_scene);
            m_transfer = mtm;
        }
        else
        {
            m_transfer = new LocalHGMessageTransferModule(m_scene);
        }
        m_transfer.OnUndeliveredMessage += _ => m_undelivered++;

        ScenePresence recipient = SceneHelpers.AddScenePresence(m_scene, RecipientId);
        m_recipientClient = (TestClient)recipient.ControllingClient;
        m_recipientClient.OnReceivedInstantMessage += im => m_received.Add(im);
    }

    private void Send(GridInstantMessage im) => m_transfer.SendInstantMessage(im, ok => m_results.Add(ok));

    private static GridInstantMessage FromAgent(UUID from, InstantMessageDialog dialog = InstantMessageDialog.MessageFromAgent)
        => new()
        {
            fromAgentID = from.Guid, toAgentID = RecipientId.Guid, dialog = (byte)dialog,
            message = "hello", fromAgentName = "Test User"
        };

    private static GridInstantMessage FromObject(UUID owner, UUID prim)
        => new()
        {
            fromAgentID = owner.Guid, toAgentID = RecipientId.Guid, imSessionID = prim.Guid,
            dialog = (byte)InstantMessageDialog.MessageFromObject, message = "hello", fromAgentName = "Example Object"
        };

    private static string Row(int type, UUID id, int flags) => $"{type} {id} Test Name|{flags}\n";

    public static IEnumerable<object[]> Modules()
        => new[] { new object[] { "MessageTransferModule" }, new object[] { "HGMessageTransferModule" } };

    [Theory]
    [MemberData(nameof(Modules))]
    public void AMessageFromSomeoneTheRecipientMutedIsDroppedAndCountsAsDelivered(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0) };
        SetUpRegion(module, mutes);

        Send(FromAgent(SenderId));

        Assert.Empty(m_received);
        Assert.Equal(new[] { true }, m_results);
        Assert.Equal(0, m_undelivered);   // no offline copy
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AMessageFromSomeoneElseIsDelivered(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, OtherId, 0) };
        SetUpRegion(module, mutes);

        Send(FromAgent(SenderId));

        Assert.Single(m_received);
        Assert.Equal(new[] { true }, m_results);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AMuteThatLeavesTextChatOnDoesNotBlockMessages(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0x1) };
        SetUpRegion(module, mutes);

        Send(FromAgent(SenderId));

        Assert.Single(m_received);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnObjectsMessageIsDroppedWhenTheRecipientMutedItsOwner(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0) };
        SetUpRegion(module, mutes);
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(2, SenderId);
        m_scene.AddNewSceneObject(so, false);

        Send(FromObject(SenderId, so.UUID));

        Assert.Empty(m_received);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnObjectsMessageFromAChildPrimIsDroppedWhenTheRecipientMutedTheObject(string module)
    {
        StandInMuteService mutes = new();
        SetUpRegion(module, mutes);
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(2, SenderId);
        m_scene.AddNewSceneObject(so, false);
        SceneObjectPart? child = Array.Find(so.Parts, p => !p.UUID.Equals(so.UUID));
        Assert.NotNull(child);
        mutes.Text = Row(2, so.UUID, 0);

        Send(FromObject(SenderId, child!.UUID));

        Assert.Empty(m_received);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnObjectsMessageIsDeliveredWhenNeitherItNorItsOwnerIsMuted(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, OtherId, 0) + Row(2, OtherId, 0) };
        SetUpRegion(module, mutes);
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, SenderId);
        m_scene.AddNewSceneObject(so, false);

        Send(FromObject(SenderId, so.UUID));

        Assert.Single(m_received);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void OtherKindsOfMessageAreNotChecked(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0) };
        SetUpRegion(module, mutes);

        Send(FromAgent(SenderId, InstantMessageDialog.GroupInvitation));

        Assert.Single(m_received);
        Assert.Equal(0, mutes.Reads);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ARegionWithNoMuteServiceDeliversEverything(string module)
    {
        SetUpRegion(module, null);

        Send(FromAgent(SenderId));

        Assert.Single(m_received);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AMuteServiceThatFailsDeliversTheMessage(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0), Throws = true };
        SetUpRegion(module, mutes);

        Send(FromAgent(SenderId));

        Assert.Single(m_received);
        Assert.Equal(1, mutes.Reads);
    }

    // ---- the per-recipient cache ----------------------------------------------------------------------------

    /// <summary>The recipient changes their mute list from their viewer, as the mute list module receives it.</summary>
    private void RecipientMutes(UUID id)
    {
        FieldInfo field = typeof(TestClient).GetField("OnUpdateMuteListEntry", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(TestClient).FullName, "OnUpdateMuteListEntry");
        MuteListEntryUpdate handler = field.GetValue(m_recipientClient) as MuteListEntryUpdate
            ?? throw new InvalidOperationException("TestClient.OnUpdateMuteListEntry handler is missing.");
        handler(m_recipientClient, id, "Test User", 1, 0);
    }

    private void RecipientUnmutes(UUID id)
    {
        FieldInfo field = typeof(TestClient).GetField("OnRemoveMuteListEntry", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(TestClient).FullName, "OnRemoveMuteListEntry");
        MuteListEntryRemove handler = field.GetValue(m_recipientClient) as MuteListEntryRemove
            ?? throw new InvalidOperationException("TestClient.OnRemoveMuteListEntry handler is missing.");
        handler(m_recipientClient, id, "Test User");
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ASecondMessageInsideTheLifetimeDoesNotAskTheServiceAgain(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0) };
        SetUpRegion(module, mutes);

        Send(FromAgent(SenderId));
        Send(FromAgent(SenderId));
        Send(FromAgent(OtherId));

        Assert.DoesNotContain(m_received, im => new UUID(im.fromAgentID).Equals(SenderId));
        Assert.Single(m_received);
        Assert.Equal(1, mutes.Reads);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AListOlderThanTheLifetimeIsReadAgain(string module)
    {
        StandInMuteService mutes = new();
        SetUpRegion(module, mutes);
        Send(FromAgent(SenderId));
        mutes.Text = Row(1, SenderId, 0);   // changed through another simulator: this one is not told

        AgeCachedList(RecipientId, InstantMessageMuteCheck.CacheLifetimeMs);
        Send(FromAgent(SenderId));

        Assert.Single(m_received);
        Assert.Equal(2, mutes.Reads);
    }

    /// <summary>Move a cached list's read time back, as if that much time had passed.</summary>
    private static void AgeCachedList(UUID agent, long ms)
    {
        FieldInfo cacheField = typeof(InstantMessageMuteCheck).GetField("m_cache", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(typeof(InstantMessageMuteCheck).FullName, "m_cache");
        object cache = cacheField.GetValue(null)
            ?? throw new InvalidOperationException("InstantMessageMuteCheck cache is missing.");
        lock (cache)
        {
            object?[] args = { agent, null };
            MethodInfo tryGetValue = cache.GetType().GetMethod("TryGetValue")
                ?? throw new MissingMethodException(cache.GetType().FullName, "TryGetValue");
            if (tryGetValue.Invoke(cache, args) is not true)
                throw new InvalidOperationException("No cached mute list exists for the test recipient.");
            object entry = args[1]
                ?? throw new InvalidOperationException("The cached mute list entry is missing.");
            PropertyInfo readAt = entry.GetType().GetProperty("ReadAt")
                ?? throw new MissingMemberException(entry.GetType().FullName, "ReadAt");
            if (readAt.GetValue(entry) is not long cachedAt)
                throw new InvalidOperationException("The cached mute list has no read timestamp.");
            readAt.SetValue(entry, cachedAt - ms);
        }
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AMuteAddedThroughThisRegionAppliesToTheNextMessage(string module)
    {
        StandInMuteService mutes = new();
        SetUpRegion(module, mutes, withMuteListModule: true);
        Send(FromAgent(SenderId));
        Assert.Single(m_received);

        RecipientMutes(SenderId);
        Send(FromAgent(SenderId));

        Assert.Single(m_received);
        Assert.Equal(2, mutes.Reads);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AMuteRemovedThroughThisRegionAppliesToTheNextMessage(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0) };
        SetUpRegion(module, mutes, withMuteListModule: true);
        Send(FromAgent(SenderId));
        Assert.Empty(m_received);

        RecipientUnmutes(SenderId);
        Send(FromAgent(SenderId));

        Assert.Single(m_received);
        Assert.Equal(2, mutes.Reads);
    }

    // ---- the read's own timeout, failed reads, and the viewer's own request ------------------------------------

    /// <summary>
    /// A mute list service on another server that answers only after a delay, reached through the grid's mute
    /// list connector. With headersFirst it sends the reply's headers and first byte at once and the rest of the
    /// body after the delay. WebUtil's shared HTTP handlers are process-wide and are put back afterwards.
    /// </summary>
    private sealed class SlowMuteServer : IDisposable
    {
        private readonly HttpListener m_listener = new();
        private readonly CancellationTokenSource m_stop = new();
        private readonly SocketsHttpHandler m_savedRedir = WebUtil.SharedSocketsHttpHandler;
        private readonly SocketsHttpHandler m_savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        public readonly string Url;
        public int Requests;
        public int Finished;
        private readonly bool m_headersFirst;
        private readonly string m_list;

        public SlowMuteServer(TimeSpan delay, bool headersFirst = false, string list = "")
        {
            m_headersFirst = headersFirst;
            m_list = list;
            WebUtil.SetupHTTPClients(false, false, null, 4);
            int port;
            using (TcpListener probe = new(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            Url = $"http://127.0.0.1:{port}/";
            m_listener.Prefixes.Add(Url);
            m_listener.Start();
            _ = Task.Run(() => Serve(delay));
        }

        private async Task Serve(TimeSpan delay)
        {
            while (m_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await m_listener.GetContextAsync(); }
                catch { return; }
                Interlocked.Increment(ref Requests);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(ServerUtils.BuildXmlResponse(
                            new Dictionary<string, object> { ["result"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(m_list)) }));
                        ctx.Response.ContentType = "text/xml";
                        ctx.Response.ContentLength64 = bytes.Length;
                        int first = 0;
                        if (m_headersFirst)
                        {
                            await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(0, 1));
                            await ctx.Response.OutputStream.FlushAsync();
                            first = 1;
                        }
                        await Task.Delay(delay, m_stop.Token);
                        await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(first));
                        ctx.Response.Close();
                        Interlocked.Increment(ref Finished);
                    }
                    catch { try { ctx.Response.Abort(); } catch { } }
                });
            }
        }

        public void Dispose()
        {
            m_stop.Cancel();
            m_listener.Close();
            SocketsHttpHandler ours = WebUtil.SharedSocketsHttpHandler;
            SocketsHttpHandler oursNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
            WebUtil.SharedSocketsHttpHandler = m_savedRedir;
            WebUtil.SharedSocketsHttpHandlerNoRedir = m_savedNoRedir;
            ours?.Dispose();
            oursNoRedir?.Dispose();
        }
    }

    private SlowMuteServer m_slowServer = null!;

    [Fact]
    public void ADeadMuteServiceHoldsAnImNoLongerThanTheReadTimeoutAndOnlyOncePerRecipient()
    {
        SetUpRegion("MessageTransferModule", null);
        m_slowServer = new SlowMuteServer(TimeSpan.FromSeconds(12));
        m_scene.RegisterModuleInterface<IMuteListService>(new MuteListServicesConnector(m_slowServer.Url));

        Stopwatch first = Stopwatch.StartNew();
        Send(FromAgent(SenderId));
        first.Stop();
        Stopwatch second = Stopwatch.StartNew();
        Send(FromAgent(SenderId));
        second.Stop();

        Assert.Equal(2, m_received.Count);   // failed open
        Assert.True(first.Elapsed < TimeSpan.FromSeconds(8), $"first IM took {first.Elapsed}");
        Assert.True(second.Elapsed < TimeSpan.FromSeconds(1), $"second IM took {second.Elapsed}");
        Assert.Equal(1, m_slowServer.Requests);
        Assert.Equal(1, ReadWarnings());
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AFailedReadIsKeptForTheLifetime(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0), Throws = true };
        SetUpRegion(module, mutes);

        Send(FromAgent(SenderId));
        Send(FromAgent(SenderId));

        Assert.Equal(2, m_received.Count);
        Assert.Equal(1, mutes.Reads);
    }

    [Fact]
    public void TheReadWarningIsLoggedAtMostOncePerLifetime()
    {
        StandInMuteService mutes = new() { Throws = true };
        SetUpRegion("MessageTransferModule", mutes);
        UUID secondRecipient = new("9e3c5a71-2b84-4f06-a1d9-64c0e8b7f523");
        ScenePresence second = SceneHelpers.AddScenePresence(m_scene, secondRecipient);
        ((TestClient)second.ControllingClient).OnReceivedInstantMessage += im => m_received.Add(im);

        Send(FromAgent(SenderId));
        GridInstantMessage toSecond = FromAgent(SenderId);
        toSecond.toAgentID = secondRecipient.Guid;
        Send(toSecond);

        Assert.Equal(2, mutes.Reads);
        Assert.Equal(2, m_received.Count);
        Assert.Equal(1, ReadWarnings());
    }

    /// <summary>The recipient's viewer asks this simulator for its mute list, as at login.</summary>
    private void RecipientViewerAsksForItsList(uint crc)
    {
        FieldInfo field = typeof(TestClient).GetField("OnMuteListRequest", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(TestClient).FullName, "OnMuteListRequest");
        MuteListRequest handler = field.GetValue(m_recipientClient) as MuteListRequest
            ?? throw new InvalidOperationException("TestClient.OnMuteListRequest handler is missing.");
        handler(m_recipientClient, crc);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AListTheRecipientsViewerAskedForIsUsedForTheNextMessage(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0) };
        SetUpRegion(module, mutes, withMuteListModule: true);

        RecipientViewerAsksForItsList(0);
        Send(FromAgent(SenderId));

        Assert.Empty(m_received);
        Assert.Equal(1, mutes.Reads);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AViewerWhoseCopyIsCurrentLeavesTheNextMessageToReadTheList(string module)
    {
        StandInMuteService mutes = new() { Text = Row(1, SenderId, 0) };
        SetUpRegion(module, mutes, withMuteListModule: true);

        RecipientViewerAsksForItsList(1234);
        Send(FromAgent(SenderId));

        Assert.Empty(m_received);
        Assert.Equal(2, mutes.Reads);
    }

    [Fact]
    public void AReplyThatStallsAfterItsHeadersHoldsAnImNoLongerThanTheReadTimeout()
    {
        SetUpRegion("MessageTransferModule", null);
        m_slowServer = new SlowMuteServer(TimeSpan.FromSeconds(12), headersFirst: true);
        m_scene.RegisterModuleInterface<IMuteListService>(new MuteListServicesConnector(m_slowServer.Url));

        Stopwatch first = Stopwatch.StartNew();
        Send(FromAgent(SenderId));
        first.Stop();

        Assert.Single(m_received);
        Assert.True(first.Elapsed < TimeSpan.FromSeconds(8), $"IM took {first.Elapsed}");
        Assert.Equal(1, m_slowServer.Requests);
        Assert.Equal(1, ReadWarnings());
    }

    [Fact]
    public void ALateAnswerFromAnAbandonedReadIsIgnored()
    {
        SetUpRegion("MessageTransferModule", null);
        // The late answer mutes the sender; the read that asked for it gave up at the limit.
        m_slowServer = new SlowMuteServer(TimeSpan.FromSeconds(5), headersFirst: true, list: Row(1, SenderId, 0));
        m_scene.RegisterModuleInterface<IMuteListService>(new MuteListServicesConnector(m_slowServer.Url));

        Send(FromAgent(SenderId));
        Assert.Single(m_received);

        // Wait for the server to have sent the late answer, then give the abandoned read time to finish with
        // it: a window in which the cache must NOT change.
        Stopwatch wait = Stopwatch.StartNew();
        while (Volatile.Read(ref m_slowServer.Finished) == 0 && wait.Elapsed < TimeSpan.FromSeconds(30))
            Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref m_slowServer.Finished));
        Thread.Sleep(1000);

        Send(FromAgent(SenderId));

        Assert.Equal(2, m_received.Count);
        Assert.Equal(1, m_slowServer.Requests);
    }
}
