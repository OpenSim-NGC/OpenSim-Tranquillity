using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.Null;

namespace OpenSim.Tests.Common;

/// <summary>
/// The OpenSim.Data.Null stores keep process-wide data (static dictionaries, or NullPresenceData.Instance) that
/// every service instance in the process shares. Test classes build scenes in parallel, so these stores must take
/// concurrent callers: no exception and no lost entry.
/// </summary>
public class NullDataStoreConcurrencyTests
{
    private const int Threads = 8;
    private const int PerThread = 2000;
    private const int Rounds = 5;
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    [Fact]
    public void NullAuthenticationData_ParallelStoreGetAndTokens_LosesNothing()
    {
        for (int round = 0; round < Rounds; round++)
        {
            NullAuthenticationData store = new NullAuthenticationData(string.Empty, "auth");
            UUID[][] ids = NewIds();

            List<Exception> errors = ConcurrencyTestHelpers.RunTogether(Threads, t =>
            {
                UUID[] other = ids[(t + 1) % Threads];
                for (int i = 0; i < PerThread; i++)
                {
                    UUID id = ids[t][i];
                    store.Store(new AuthenticationData { PrincipalID = id, Data = new Dictionary<string, object>() });
                    store.SetToken(id, "token-" + id, 30);
                    store.Get(other[i]);
                    store.CheckToken(other[i], "token-" + other[i], 30);
                }
            }, Cap);

            errors.Should().BeEmpty();
            foreach (UUID[] threadIds in ids)
                foreach (UUID id in threadIds)
                {
                    store.Get(id).Should().NotBeNull();
                    store.Get(id).PrincipalID.Should().Be(id);
                    store.CheckToken(id, "token-" + id, 30).Should().BeTrue();
                }
        }
    }

    [Fact]
    public void NullAvatarData_ParallelStoreGetAndDelete_LosesNothing()
    {
        for (int round = 0; round < Rounds; round++)
        {
            NullAvatarData store = new NullAvatarData(string.Empty, "avatars");
            UUID[][] ids = NewIds();

            List<Exception> errors = ConcurrencyTestHelpers.RunTogether(Threads, t =>
            {
                UUID[] other = ids[(t + 1) % Threads];
                for (int i = 0; i < PerThread; i++)
                {
                    UUID id = ids[t][i];
                    store.Store(new AvatarBaseData
                    {
                        PrincipalID = id,
                        Data = new Dictionary<string, string> { ["keep"] = "1", ["drop"] = "1" }
                    });
                    store.Delete(id, "drop");
                    store.Get("PrincipalID", other[i].ToString());
                }
            }, Cap);

            errors.Should().BeEmpty();
            foreach (UUID[] threadIds in ids)
                foreach (UUID id in threadIds)
                {
                    AvatarBaseData[] found = store.Get("PrincipalID", id.ToString());
                    found.Should().ContainSingle();
                    found[0].Data.Should().ContainKey("keep").And.NotContainKey("drop");
                }

            // Whole-row deletes from every thread at once.
            errors = ConcurrencyTestHelpers.RunTogether(Threads, t =>
            {
                foreach (UUID id in ids[t])
                    store.Delete("PrincipalID", id.ToString()).Should().BeTrue();
            }, Cap);

            errors.Should().BeEmpty();
            foreach (UUID[] threadIds in ids)
                foreach (UUID id in threadIds)
                    store.Get("PrincipalID", id.ToString()).Should().BeEmpty();
        }
    }

    [Fact]
    public void NullPresenceData_ParallelStoreGetAndReport_LosesNothing()
    {
        // A new NullPresenceData delegates to the process-wide Instance when there is one, as every presence
        // service in a standalone does; this test uses whatever store the process has.
        NullPresenceData store = new NullPresenceData(string.Empty, "presence");

        for (int round = 0; round < Rounds; round++)
        {
            UUID[][] sessions = NewIds();
            UUID region = UUID.Random();
            string[] users = new string[Threads];
            for (int t = 0; t < Threads; t++)
                users[t] = UUID.Random().ToString();

            List<Exception> errors = ConcurrencyTestHelpers.RunTogether(Threads, t =>
            {
                UUID[] other = sessions[(t + 1) % Threads];
                for (int i = 0; i < PerThread; i++)
                {
                    UUID session = sessions[t][i];
                    store.Store(new PresenceData
                    {
                        UserID = users[t],
                        SessionID = session,
                        RegionID = UUID.Zero,
                        Data = new Dictionary<string, string>()
                    });
                    store.ReportAgent(session, region);
                    store.Get(other[i]);
                    if (i % 200 == 0)
                        store.Get("UserID", users[(t + 1) % Threads]);
                }
            }, Cap);

            errors.Should().BeEmpty();
            for (int t = 0; t < Threads; t++)
            {
                foreach (UUID session in sessions[t])
                {
                    PresenceData pd = store.Get(session);
                    pd.Should().NotBeNull();
                    pd.RegionID.Should().Be(region);
                }
                store.Get("UserID", users[t]).Should().HaveCount(PerThread);
            }

            store.Get("RegionID", region.ToString()).Should().HaveCount(Threads * PerThread);
            store.LogoutRegionAgents(region);
            store.Get("RegionID", region.ToString()).Should().BeEmpty();
        }
    }

    private static UUID[][] NewIds()
    {
        UUID[][] ids = new UUID[Threads][];
        for (int t = 0; t < Threads; t++)
        {
            ids[t] = new UUID[PerThread];
            for (int i = 0; i < PerThread; i++)
                ids[t][i] = UUID.Random();
        }
        return ids;
    }
}
