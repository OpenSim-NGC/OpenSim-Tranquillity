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

using System.Reflection;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.InstantMessage;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
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
        protected override object Invoke(MethodInfo method, object[] args) => true;
    }

    public MutedInstantMessageTests()
    {
        InstantMessageMuteCheck.ForgetAll();
    }

    public override void Dispose()
    {
        InstantMessageMuteCheck.ForgetAll();
        base.Dispose();
    }

    private TestScene m_scene;
    private TestClient m_recipientClient;
    private IMessageTransferModule m_transfer;
    private readonly List<GridInstantMessage> m_received = new();
    private readonly List<bool> m_results = new();
    private int m_undelivered;

    private void SetUpRegion(string module, StandInMuteService mutes, bool withMuteListModule = false)
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
        SceneObjectPart child = Array.Find(so.Parts, p => !p.UUID.Equals(so.UUID));
        mutes.Text = Row(2, so.UUID, 0);

        Send(FromObject(SenderId, child.UUID));

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
        MuteListEntryUpdate handler = (MuteListEntryUpdate)typeof(TestClient)
            .GetField("OnUpdateMuteListEntry", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(m_recipientClient);
        handler(m_recipientClient, id, "Test User", 1, 0);
    }

    private void RecipientUnmutes(UUID id)
    {
        MuteListEntryRemove handler = (MuteListEntryRemove)typeof(TestClient)
            .GetField("OnRemoveMuteListEntry", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(m_recipientClient);
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

        Assert.Empty(m_received.Where(im => new UUID(im.fromAgentID).Equals(SenderId)));
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
        FieldInfo cacheField = typeof(InstantMessageMuteCheck).GetField("m_cache", BindingFlags.NonPublic | BindingFlags.Static);
        object cache = cacheField.GetValue(null);
        lock (cache)
        {
            object[] args = { agent, null };
            Assert.True((bool)cache.GetType().GetMethod("TryGetValue").Invoke(cache, args));
            object entry = args[1];
            PropertyInfo readAt = entry.GetType().GetProperty("ReadAt");
            readAt.SetValue(entry, (long)readAt.GetValue(entry) - ms);
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
}
