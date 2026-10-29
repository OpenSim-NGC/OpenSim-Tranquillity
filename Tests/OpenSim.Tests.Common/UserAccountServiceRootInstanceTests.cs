using System.Collections.Concurrent;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.UserAccountService;

namespace OpenSim.Tests.Common;

/// <summary>
/// Serial: resets and races the process-wide static UserAccountService.m_RootInstance, which every
/// UserAccountService constructed in this assembly (SceneHelpers builds one per scene helper) also reads.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class UserAccountServiceRootInstanceCollection
{
    public const string Name = "UserAccountService root instance";
}

[Collection(UserAccountServiceRootInstanceCollection.Name)]
public class UserAccountServiceRootInstanceTests
{
    private const int Threads = 8;
    private const int Rounds = 2000;

    private static readonly FieldInfo s_rootInstance = typeof(UserAccountService).GetField(
        "m_RootInstance", BindingFlags.NonPublic | BindingFlags.Static);

    [Fact]
    public void ConstructedInParallel_ExactlyOneBecomesRoot()
    {
        s_rootInstance.Should().NotBeNull();
        object saved = s_rootInstance.GetValue(null);

        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                s_rootInstance.SetValue(null, null);
                ConcurrentBag<UserAccountService> services = new ConcurrentBag<UserAccountService>();

                List<Exception> errors = ConcurrencyTestHelpers.RunTogether(
                    Threads, t => services.Add(new UserAccountService(Config())), TimeSpan.FromSeconds(60));

                errors.Should().BeEmpty();
                services.Should().HaveCount(Threads);

                // Only the root instance creates the GRID SERVICES account, and each instance has its own
                // NullUserAccountData, so the number of instances holding it is the number that ran as root.
                int roots = services.Count(s => s.GetUserAccount(UUID.Zero, Constants.servicesGodAgentID) != null);
                roots.Should().Be(1, "round {0}", round);
                s_rootInstance.GetValue(null).Should().BeOneOf(services);
            }
        }
        finally
        {
            s_rootInstance.SetValue(null, saved);
        }
    }

    private static IConfigSource Config()
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("UserAccountService");
        config.Configs["UserAccountService"].Set("StorageProvider", "OpenSim.Data.Null.dll");
        return config;
    }
}
