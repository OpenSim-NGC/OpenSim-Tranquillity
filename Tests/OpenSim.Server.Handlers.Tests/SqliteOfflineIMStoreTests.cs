/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.OfflineIM;
using OpenSim.Server.Base;
using OpenSim.Tests.Common;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// "Offline Message Module V2" on a SQLite standalone: the real OfflineIMService with no OfflineMessageURL, its store
/// taken from [DatabaseService] as on a default standalone. A SQLite store keeps one connection per table type for
/// the process, so every test here shares one database, with recipients of its own.
/// </summary>
[Collection("SQLite stores")]
public class SqliteOfflineIMStoreTests
{
    private const int MaxOfflineIMs = 3;

    private static readonly string s_dir = Environment.GetEnvironmentVariable("SQLITE_QUERY_TEST_DIR")
        is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;
    private static readonly string s_connection =
        "Data Source=" + Path.Combine(s_dir, "offlineim-" + Guid.NewGuid().ToString("N") + ".db") + ";Version=3";

    private static readonly System.Lazy<OfflineIMService> s_service = new(() =>
    {
        var config = new IniConfigSource();
        IConfig db = config.AddConfig("DatabaseService");
        db.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        db.Set("ConnectionString", s_connection);
        config.AddConfig("Messaging").Set("MaxOfflineIMs", MaxOfflineIMs.ToString());
        return new OfflineIMService(config);
    });

    private static GridInstantMessage Message(UUID from, UUID to, string text) => new()
    {
        fromAgentID = from.Guid,
        fromAgentName = "Sender",
        toAgentID = to.Guid,
        dialog = (byte)InstantMessageDialog.MessageFromAgent,
        message = text,
        imSessionID = UUID.Random().Guid,
        binaryBucket = [],
    };

    [Fact]
    public void AStoredMessageIsDeliveredOnceThenDeleted()
    {
        UUID from = UUID.Random(), to = UUID.Random();

        Assert.True(s_service.Value.StoreMessage(Message(from, to, "first"), out string reason), reason);
        Assert.True(s_service.Value.StoreMessage(Message(from, to, "second <&> é"), out reason), reason);

        List<GridInstantMessage> got = s_service.Value.GetMessages(to);
        Assert.Equal(["first", "second <&> é"], got.Select(m => m.message).Order());
        Assert.All(got, m => Assert.Equal(from.Guid, m.fromAgentID));
        Assert.All(got, m => Assert.Equal(to.Guid, m.toAgentID));

        Assert.Empty(s_service.Value.GetMessages(to));
    }

    [Fact]
    public void MessagesBeyondTheCapAreRefusedForThatRecipientOnly()
    {
        UUID from = UUID.Random(), full = UUID.Random(), other = UUID.Random();
        for (int i = 0; i < MaxOfflineIMs; i++)
            Assert.True(s_service.Value.StoreMessage(Message(from, full, "m" + i), out string reason), reason);

        Assert.False(s_service.Value.StoreMessage(Message(from, full, "one too many"), out string refused));
        Assert.NotEmpty(refused);
        Assert.True(s_service.Value.StoreMessage(Message(from, other, "still room"), out string ok), ok);

        Assert.Equal(MaxOfflineIMs, s_service.Value.GetMessages(full).Count);
        // once delivered, the recipient has room again
        Assert.True(s_service.Value.StoreMessage(Message(from, full, "after delivery"), out string again), again);
    }

    [Fact]
    public void DeletingAUsersMessagesRemovesThoseToAndFromThem()
    {
        UUID user = UUID.Random(), friend = UUID.Random(), bystander = UUID.Random();
        Assert.True(s_service.Value.StoreMessage(Message(friend, user, "to the user"), out string reason), reason);
        Assert.True(s_service.Value.StoreMessage(Message(user, friend, "from the user"), out reason), reason);
        Assert.True(s_service.Value.StoreMessage(Message(bystander, friend, "unrelated"), out reason), reason);

        s_service.Value.DeleteMessages(user);

        Assert.Empty(s_service.Value.GetMessages(user));
        Assert.Equal(["unrelated"], s_service.Value.GetMessages(friend).Select(m => m.message));
    }

    [Fact]
    public void TheStoreDeletesMessagesOlderThanTwoWeeks()
    {
        _ = s_service.Value;   // the service's store runs the migration on this database
        IOfflineIMData store = ServerUtils.LoadPlugin<IOfflineIMData>("OpenSim.Data.SQLite.dll",
            new object[] { s_connection, "im_offline" });
        Assert.NotNull(store);

        UUID to = UUID.Random();
        OfflineIMData Row(string message, string? stamp)
        {
            var data = new Dictionary<string, string> { ["Message"] = message };
            if (stamp is not null)
                data["TMStamp"] = stamp;
            return new OfflineIMData { PrincipalID = to, FromID = UUID.Random(), Data = data };
        }
        string old = DateTime.UtcNow.AddDays(-15).ToString("yyyy-MM-dd HH:mm:ss");
        string recent = DateTime.UtcNow.AddDays(-13).ToString("yyyy-MM-dd HH:mm:ss");
        Assert.True(store.Store(Row("old", old)));
        Assert.True(store.Store(Row("recent", recent)));
        Assert.True(store.Store(Row("new", null)));
        Assert.Equal(3, store.GetCount("PrincipalID", to.ToString()));

        store.DeleteOld();

        Assert.Equal(["new", "recent"], store.Get("PrincipalID", to.ToString()).Select(r => r.Data["Message"]).Order());
    }
}

/// <summary>A region whose offline IM store cannot be loaded runs without offline messages instead of stopping.</summary>
[Collection("LogCapture")]
public class OfflineIMModuleMissingStoreTests
{
    [Fact]
    public void AProviderWithoutAnOfflineIMStoreDisablesTheModuleWithAnError()
    {
        var config = new IniConfigSource();
        IConfig messaging = config.AddConfig("Messaging");
        messaging.Set("OfflineMessageModule", "Offline Message Module V2");
        messaging.Set("StorageProvider", "OpenSim.Framework.dll");   // loads, but has no IOfflineIMData

        var module = new OfflineIMRegionModule();
        using (var log = new LogCapture())
        {
            module.Initialise(config);

            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, log.LevelOf("Offline messages are disabled"));
            Assert.True(log.Contains("OpenSim.Framework.dll"));
        }

        // disabled: the region does not offer the offline IM service
        var scene = new SceneHelpers().SetupScene();
        module.AddRegion(scene);
        Assert.Null(scene.RequestModuleInterface<OpenSim.Services.Interfaces.IOfflineIMService>());
    }
}
