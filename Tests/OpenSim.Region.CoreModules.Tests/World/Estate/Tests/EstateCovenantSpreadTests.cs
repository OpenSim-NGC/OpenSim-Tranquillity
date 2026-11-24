/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Estate.Tests;

/// <summary>
/// EstateModule sends a region's covenant to the estate's other regions (EstateConnector.SendUpdateCovenant, one
/// POST per other simulator) when the region's info changes. The region info change is raised for every region
/// setting, "set water height" among them, so only a new covenant is sent.
///
/// Each send starts by asking the estate store for the estate's regions (Scene.GetEstateRegions); the tests count
/// those requests. The store names one region on another simulator; the grid does not know it, so nothing is
/// posted. Fire-and-forget calls run inline (FireAndForgetMethod.RegressionTest), so the count is final when the
/// command returns.
/// </summary>
public class EstateCovenantSpreadTests : OpenSimTestCase
{
    // The module registers its handler on the default HTTP server; it is not started, only needed to exist.
    private const uint Port = 19438;
    private bool m_addedServer;

    private Scene m_scene = null!;
    private EstateManagementModule m_estateManagement = null!;
    private int m_estateLookups;

    // When set, an estate lookup waits for it; each finished lookup releases m_lookupsDone.
    private ManualResetEventSlim? m_lookupGate;
    private readonly SemaphoreSlim m_lookupsDone = new(0);

    public override void SetUp()
    {
        base.SetUp();
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;

        m_addedServer = MainServer.Instance.DefaultServer is null;
        if (m_addedServer)
            MainServer.Instance.AddHttpServer(new BaseHttpServer(Port));

        m_scene = new SceneHelpers().SetupScene();
        UUID otherSimulatorRegion = UUID.Random();
        m_scene.RegisterModuleInterface(StubProxy.Create<IEstateDataService>((method, args) =>
        {
            if (method.Name != nameof(IEstateDataService.GetRegions))
                return (false, null);
            m_lookupGate?.Wait(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref m_estateLookups);
            m_lookupsDone.Release();
            return (true, new List<UUID> { m_scene.RegionInfo.RegionID, otherSimulatorRegion });
        }));

        m_estateManagement = new EstateManagementModule();
        SceneHelpers.SetupSceneModules(m_scene, new IniConfigSource(), m_estateManagement, new EstateModule());
    }

    public override void Dispose()
    {
        if (m_addedServer)
            MainServer.Instance.RemoveHttpServer(Port);
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private void SetWaterHeight(string height)
    {
        // The console command "set water height <height>", as the console runs it.
        object commands = typeof(EstateManagementModule)
            .GetField("m_commands", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m_estateManagement)!;
        commands.GetType()
            .GetMethod("consoleSetWaterHeight", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(commands, new object[] { "Regions", new[] { "set", "water", "height", height } });
    }

    [Fact]
    public void SettingTheWaterHeightSendsNoCovenant()
    {
        SetWaterHeight("25");

        Assert.Equal(25, m_scene.RegionInfo.RegionSettings.WaterHeight);
        Assert.Equal(0, m_estateLookups);
    }

    [Fact]
    public void ANewCovenantIsSentOnceAndNotAgainForALaterWaterHeight()
    {
        TestClient manager = (TestClient)SceneHelpers.AddScenePresence(m_scene, UUID.Random()).ControllingClient;
        UUID covenant = UUID.Random();

        manager.TriggerEstateChangeCovenant(covenant);

        Assert.Equal(covenant, m_scene.RegionInfo.RegionSettings.Covenant);
        Assert.Equal(1, m_estateLookups);

        SetWaterHeight("30");

        Assert.Equal(1, m_estateLookups);
    }

    [Fact]
    public async Task ACovenantChangeDoesNotWaitForTheOtherSimulators()
    {
        // As on a running simulator, the send runs on the thread pool; the estate lookup is held until the change
        // has returned.
        Util.FireAndForgetMethod = FireAndForgetMethod.QueueUserWorkItem;
        TestClient manager = (TestClient)SceneHelpers.AddScenePresence(m_scene, UUID.Random()).ControllingClient;
        using ManualResetEventSlim gate = new(false);
        m_lookupGate = gate;

        Task change = Task.Run(() => manager.TriggerEstateChangeCovenant(UUID.Random()));
        Task first = await Task.WhenAny(change, Task.Delay(TimeSpan.FromSeconds(10)));
        gate.Set();
        await change;

        Assert.True(first == change, "the covenant change waited for the send to the estate's other regions");
        Assert.True(m_lookupsDone.Wait(TimeSpan.FromSeconds(30)), "the covenant was not sent");
        Assert.Equal(1, m_estateLookups);
    }

    [Fact]
    public void EachNewCovenantIsSent()
    {
        TestClient manager = (TestClient)SceneHelpers.AddScenePresence(m_scene, UUID.Random()).ControllingClient;

        manager.TriggerEstateChangeCovenant(UUID.Random());
        manager.TriggerEstateChangeCovenant(UUID.Random());

        Assert.Equal(2, m_estateLookups);
    }
}
