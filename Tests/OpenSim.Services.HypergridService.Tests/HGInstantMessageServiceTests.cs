/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using Xunit;

namespace OpenSim.Services.HypergridService.Tests;

/// <summary>
/// The gatekeeper's instant message service receives messages that other grids send to this grid's
/// residents. When the resident is not online, a message that is kept for them offline has been
/// delivered as far as the sending grid is concerned, so the sender is not told the resident is not
/// logged in. A message that is not kept is still answered as undelivered.
/// </summary>
public class HGInstantMessageServiceTests
{
    // HGInstantMessageService reads its configuration once per process, into static fields, so every
    // test here shares this one gatekeeper configuration and steers the offline store instead.
    private static readonly HGInstantMessageService Service = CreateGatekeeperService();

    private static HGInstantMessageService CreateGatekeeperService()
    {
        string testAssembly = typeof(HGInstantMessageServiceTests).Assembly.Location;

        IConfigSource config = new IniConfigSource();
        IConfig im = config.AddConfig("HGInstantMessageService");
        // Never called: no resident has a presence, so no region is looked up.
        im.Set("GridService", "OpenSim.Services.Connectors.dll:GridServicesConnector");
        im.Set("PresenceService", testAssembly + ":" + nameof(NobodyOnlinePresenceService));
        im.Set("InGatekeeper", "true");
        config.AddConfig("GridService").Set("GridServerURI", "http://grid.invalid:8003");
        config.AddConfig("Messaging").Set("OfflineIMService", testAssembly + ":" + nameof(RecordingOfflineIMService));

        return new HGInstantMessageService(config);
    }

    private static GridInstantMessage MessageToOfflineResident(InstantMessageDialog dialog) => new()
    {
        fromAgentID = UUID.Random().Guid,
        fromAgentName = "Visitor",
        toAgentID = UUID.Random().Guid,
        dialog = (byte)dialog,
        message = "hello"
    };

    [Fact]
    public void MessageKeptOfflineIsDelivered()
    {
        RecordingOfflineIMService.Reset(storeSucceeds: true);
        GridInstantMessage im = MessageToOfflineResident(InstantMessageDialog.MessageFromAgent);

        Assert.True(Service.IncomingInstantMessage(im));
        Assert.Same(im, Assert.Single(RecordingOfflineIMService.Stored));
    }

    [Fact]
    public void MessageTheOfflineStoreRefusesIsNotDelivered()
    {
        RecordingOfflineIMService.Reset(storeSucceeds: false);
        GridInstantMessage im = MessageToOfflineResident(InstantMessageDialog.MessageFromAgent);

        Assert.False(Service.IncomingInstantMessage(im));
        Assert.Single(RecordingOfflineIMService.Stored);
    }

    [Fact]
    public void MessageThatIsNotKeptOfflineIsNotDelivered()
    {
        RecordingOfflineIMService.Reset(storeSucceeds: true);

        Assert.False(Service.IncomingInstantMessage(MessageToOfflineResident(InstantMessageDialog.StartTyping)));
        Assert.Empty(RecordingOfflineIMService.Stored);
    }
}

/// <summary>Loaded by name as the gatekeeper's presence service: nobody is online.</summary>
public class NobodyOnlinePresenceService : IPresenceService
{
    public NobodyOnlinePresenceService(IConfigSource config)
    {
    }

    public bool LoginAgent(string userID, UUID sessionID, UUID secureSessionID) => false;
    public bool LogoutAgent(UUID sessionID) => false;
    public bool LogoutRegionAgents(UUID regionID) => false;
    public bool ReportAgent(UUID sessionID, UUID regionID) => false;
    public PresenceInfo GetAgent(UUID sessionID) => null;
    public PresenceInfo[] GetAgents(string[] userIDs) => Array.Empty<PresenceInfo>();
}

/// <summary>Loaded by name as the offline message store: records what it is asked to keep.</summary>
public class RecordingOfflineIMService : IOfflineIMService
{
    public static readonly List<GridInstantMessage> Stored = new();
    private static bool s_storeSucceeds;

    public RecordingOfflineIMService(IConfigSource config)
    {
    }

    public static void Reset(bool storeSucceeds)
    {
        Stored.Clear();
        s_storeSucceeds = storeSucceeds;
    }

    public List<GridInstantMessage> GetMessages(UUID principalID) => new();

    public bool StoreMessage(GridInstantMessage im, out string reason)
    {
        Stored.Add(im);
        reason = s_storeSucceeds ? string.Empty : "store refused";
        return s_storeSucceeds;
    }

    public void DeleteMessages(UUID userID)
    {
    }
}
