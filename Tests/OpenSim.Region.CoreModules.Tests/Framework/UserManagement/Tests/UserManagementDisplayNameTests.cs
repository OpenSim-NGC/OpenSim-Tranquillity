/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Framework.UserManagement.Tests;

/// <summary>
/// Issue #127: a resident's display name, stored in their account, must reach the viewer's
/// GetDisplayNames request (which reads <see cref="UserManagementModule.GetKnownUsers"/>) after they
/// arrive in a region, after the module caches their account, and after the account service failed
/// to answer for a while.
/// </summary>
public class UserManagementDisplayNameTests : OpenSimTestCase
{
    private static readonly UUID ResidentID = new("00000000-0000-4000-8000-000000000127");

    /// <summary>The account service the module asks; it can be made to fail every lookup.</summary>
    private sealed class Accounts
    {
        public readonly Dictionary<UUID, UserAccount> Stored = new();
        public bool Down;

        public IUserAccountService Service => StubProxy.Create<IUserAccountService>((method, args) =>
        {
            if (method.Name == nameof(IUserAccountService.GetUserAccount) && args.Length == 2 && args[1] is UUID id)
                return (true, !Down && Stored.TryGetValue(id, out var account) ? account : null);

            if (method.Name == nameof(IUserAccountService.GetUserAccounts) && args[1] is List<string> ids)
            {
                List<UserAccount> found = new();
                if (!Down)
                {
                    foreach (string s in ids)
                        if (UUID.TryParse(s, out UUID uuid) && Stored.TryGetValue(uuid, out var account))
                            found.Add(account);
                }
                return (true, found);
            }

            return (false, null);
        });
    }

    private sealed class TestUserManagementModule : UserManagementModule
    {
        public TestUserManagementModule(IUserAccountService accounts)
        {
            m_userAccountService = accounts;
            // The grid user service knows the resident by name only; it has no display names.
            m_gridUserService = StubProxy.Create<IGridUserService>();
        }
    }

    private readonly Accounts m_accounts = new();
    private readonly TestScene m_scene;
    private readonly TestUserManagementModule m_umm;

    public UserManagementDisplayNameTests()
    {
        m_accounts.Stored[ResidentID] = new UserAccount(ResidentID)
        {
            FirstName = "Kanika",
            LastName = "Resident",
            DisplayName = "Kitty",
            NameChanged = 1700000000,
            LocalToGrid = true
        };

        m_umm = new TestUserManagementModule(m_accounts.Service);
        m_scene = new SceneHelpers().SetupScene();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules");
        SceneHelpers.SetupSceneModules(m_scene, config, m_umm);
    }

    private void ResidentArrives()
    {
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(ResidentID);
        acd.firstname = "Kanika";
        acd.lastname = "Resident";
        SceneHelpers.AddScenePresence(m_scene, acd);
    }

    private UserData DisplayNamesLookup()
    {
        return Assert.Single(m_umm.GetKnownUsers(new[] { ResidentID.ToString() }, UUID.Zero));
    }

    [Fact]
    public void DisplayNameIsKnownAfterTheResidentArrives()
    {
        ResidentArrives();

        UserData ud = DisplayNamesLookup();
        Assert.Equal("Kitty", ud.DisplayName);
        Assert.False(ud.IsNameDefault);
    }

    [Fact]
    public void DisplayNameIsKeptWhenTheAccountIsCached()
    {
        // IsLocalGridUser caches the whole account of a resident it has not seen yet.
        Assert.True(m_umm.IsLocalGridUser(ResidentID));

        m_accounts.Down = true; // answered from the cache, not the service
        Assert.Equal("Kitty", DisplayNamesLookup().DisplayName);
    }

    [Fact]
    public void FailedBatchLookupIsAskedAgain()
    {
        ResidentArrives();

        m_accounts.Down = true;
        UserData during = DisplayNamesLookup();
        Assert.Equal("Kanika", during.FirstName);
        Assert.Equal("Resident", during.LastName);

        m_accounts.Down = false;
        Assert.Equal("Kitty", DisplayNamesLookup().DisplayName);
    }

    [Fact]
    public void FailedSingleLookupIsAskedAgain()
    {
        ResidentArrives();

        m_accounts.Down = true;
        Assert.Equal("Kanika Resident", m_umm.GetUserName(ResidentID));
        Assert.True(m_umm.IsLocalGridUser(ResidentID));

        m_accounts.Down = false;
        Assert.Equal("Kitty", DisplayNamesLookup().DisplayName);
    }
}
