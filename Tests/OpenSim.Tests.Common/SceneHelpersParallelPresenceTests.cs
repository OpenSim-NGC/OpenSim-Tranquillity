using OpenMetaverse;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Presence;
using OpenSim.Services.Interfaces;

namespace OpenSim.Tests.Common;

/// <summary>
/// Test classes set up scenes in parallel. A scene set up on one thread must not throw for, or drop the
/// presences of, a scene another thread set up.
/// </summary>
public class SceneHelpersParallelPresenceTests
{
    private const int Threads = 2;
    private const int Rounds = 5;
    private const int AgentsPerScene = 20;

    [Fact]
    public void ScenesSetUpInParallel_KeepEachOthersPresences()
    {
        for (int round = 0; round < Rounds; round++)
        {
            using Barrier step = new Barrier(Threads);
            int r = round;

            List<Exception> errors = ConcurrencyTestHelpers.RunTogether(Threads, t =>
            {
                TestScene scene = NewScene(r, t, 0);

                // As SceneHelpers.AddScenePresence does: the region connector does not log agents in, so go to
                // the presence service behind it.
                IPresenceService presence = ((LocalPresenceServicesConnector)scene.PresenceService).m_PresenceService;
                UUID region = scene.RegionInfo.RegionID;

                UUID[] sessions = new UUID[AgentsPerScene];
                for (int i = 0; i < AgentsPerScene; i++)
                {
                    sessions[i] = UUID.Random();
                    presence.LoginAgent(UUID.Random().ToString(), sessions[i], UUID.Random()).Should().BeTrue();
                }

                // Every thread has logged its agents in; now each sets up another scene, as the next test class
                // starting in parallel would.
                step.SignalAndWait();
                NewScene(r, t, 1);
                step.SignalAndWait();

                // ReportAgent reaches the store (a missing row answers false); GetAgent alone could be answered
                // from PresenceService's own cache.
                foreach (UUID session in sessions)
                {
                    presence.ReportAgent(session, region).Should().BeTrue("scene {0} must keep presence {1}", t, session);
                    presence.GetAgent(session).RegionID.Should().Be(region);
                }
            }, TimeSpan.FromSeconds(120));

            errors.Should().BeEmpty();
        }
    }

    private static TestScene NewScene(int round, int thread, int n)
    {
        return new SceneHelpers().SetupScene(
            "parallel presence " + round + "-" + thread + "-" + n, UUID.Random(),
            (uint)(2000 + thread * 10 + n), (uint)(2000 + round * 10));
    }
}
