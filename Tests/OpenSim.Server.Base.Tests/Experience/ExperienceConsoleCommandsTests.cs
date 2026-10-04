/*
 * Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.ExperienceService;
using OpenSim.Services.Interfaces;
using Xunit;

namespace OpenSim.Server.Base.Tests.Experience;

/// <summary>
/// The experience service's console commands, run through a real <see cref="CommandConsole"/> (its parser,
/// command tree and prompts) against an in-memory experience store and user account service that the service
/// loads as plugins from this assembly, as it loads the MySQL store and the account service from configuration.
/// The service registers its commands on the process-wide <see cref="MainConsole.Instance"/>, so these tests
/// share the "MainConsole" collection and restore that static afterwards.
/// </summary>
[Collection("MainConsole")]
public sealed class ExperienceConsoleCommandsTests : IDisposable
{
    private static readonly UUID OwnerId = UUID.Parse("5d1c3f6e-7a42-4b8e-9c11-2f6a0b9d4e01");
    private static readonly UUID OtherId = UUID.Parse("a3e0b7c2-1d54-4f69-8e2a-6c9b5d0f7a12");

    private readonly ICommandConsole m_previousConsole;
    private readonly ScriptedConsole m_console;
    private readonly string m_storeName;
    private readonly ExperienceService m_service;

    public ExperienceConsoleCommandsTests()
    {
        m_previousConsole = MainConsole.Instance;
        m_console = new ScriptedConsole();
        MainConsole.Instance = m_console;

        TestUserAccountService.Accounts.Clear();
        TestUserAccountService.Accounts.Add(new UserAccount(UUID.Zero, OwnerId, "Test", "User", string.Empty));
        TestUserAccountService.Accounts.Add(new UserAccount(UUID.Zero, OtherId, "Other", "Resident", string.Empty));

        m_storeName = "experience-console-" + UUID.Random();
        string assembly = typeof(ExperienceConsoleCommandsTests).Assembly.Location;

        IniConfigSource config = new IniConfigSource();
        IConfig section = config.AddConfig("ExperienceService");
        section.Set("StorageProvider", assembly + ":" + nameof(InMemoryExperienceData));
        section.Set("ConnectionString", m_storeName);
        section.Set("UserAccountService", assembly + ":" + nameof(TestUserAccountService));

        m_service = new ExperienceService(config);
    }

    public void Dispose()
    {
        MainConsole.Instance = m_previousConsole;
        InMemoryExperienceData.Stores.TryRemove(m_storeName, out _);
    }

    private InMemoryExperienceData Store => InMemoryExperienceData.Stores[m_storeName];

    private List<string> Run(string line, params string[] answers)
    {
        m_console.Answers.Clear();
        foreach (string answer in answers)
            m_console.Answers.Enqueue(answer);
        m_console.Lines.Clear();
        m_console.Prompts.Clear();

        m_console.RunCommand(line);

        Assert.Empty(m_console.Answers);
        return new List<string>(m_console.Lines);
    }

    private ExperienceInfoData Only()
    {
        ExperienceInfoData[] all = Store.All();
        Assert.Single(all);
        return all[0];
    }

    private ExperienceInfoData Add(string name, UUID owner, int properties = 0, string description = "")
    {
        ExperienceInfoData data = new ExperienceInfoData
        {
            public_id = UUID.Random(), owner_id = owner, name = name, description = description,
            marketplace = string.Empty, slurl = string.Empty, properties = properties
        };
        Store.UpdateExperienceInfo(data);
        return data;
    }

    // ---- the gap: what the existing command makes ----

    [Fact]
    public void TheExistingCreateCommandMakesANamelessExperienceThatNoNameSearchFinds()
    {
        UUID key = UUID.Random();
        List<string> output = Run($"create experience Test User {key}");

        Assert.Contains("Experience created!", output);
        ExperienceInfoData made = Only();
        Assert.Equal(key, made.public_id);
        Assert.Equal(OwnerId, made.owner_id);
        Assert.Equal(string.Empty, made.name);
        Assert.Empty(m_service.FindExperiencesByName("Test"));
    }

    // ---- the existing syntax behaves as before ----

    [Fact]
    public void TheExistingCreateCommandStillPromptsForWhatIsMissing()
    {
        UUID key = UUID.Random();
        List<string> output = Run("create experience", "Test", "User", key.ToString());

        Assert.Equal(new[] { "Experience owner first name [Test]: ", "Experience owner last name [Resident]: ",
            "Experience Key [" }, m_console.Prompts.Select(p => p.StartsWith("Experience Key [") ? "Experience Key [" : p));
        Assert.Contains("Experience created!", output);
        Assert.Equal(key, Only().public_id);
    }

    [Fact]
    public void TheExistingCreateCommandStillRefusesAnUnknownOwnerAndATakenKey()
    {
        Assert.Contains("No such user as Nobody Here", Run($"create experience Nobody Here {UUID.Random()}"));
        Assert.Empty(Store.All());

        UUID key = UUID.Random();
        Run($"create experience Test User {key}");
        Assert.Contains("Experience already exists!", Run($"create experience Test User {key}"));
        Assert.Contains("Invalid UUID", Run("create experience Test User not-a-key"));
        Assert.Single(Store.All());
    }

    [Fact]
    public void TheExistingSuspendCommandStillSuspendsAndUnsuspends()
    {
        ExperienceInfoData data = Add("Garden Tour", OwnerId);

        Assert.Contains("Experience has been suspended", Run($"suspend experience {data.public_id} true"));
        Assert.Equal((int)ExperienceFlags.Suspended, Only().properties);
        Assert.Contains("Experience has been unsuspended", Run($"suspend experience {data.public_id} false"));
        Assert.Equal(0, Only().properties);
    }

    // ---- create named experience ----

    [Fact]
    public void ANamedExperienceHasItsNameAndOwnerAndIsFoundByName()
    {
        List<string> output = Run("create named experience \"Garden Tour\" Test User");

        ExperienceInfoData made = Only();
        Assert.Equal("Garden Tour", made.name);
        Assert.Equal(OwnerId, made.owner_id);
        Assert.Equal(string.Empty, made.description);
        Assert.Equal(0, made.properties);
        Assert.Contains($"Experience \"Garden Tour\" created with key {made.public_id}, owned by Test User.", output);

        ExperienceInfo[] found = m_service.FindExperiencesByName("Garden");
        Assert.Single(found);
        Assert.Equal(made.public_id, found[0].public_id);
    }

    [Fact]
    public void ANamedExperienceCanBeOwnedByKeyAndGivenAKey()
    {
        UUID key = UUID.Random();
        Run($"create named experience \"Garden Tour\" {OtherId} {key}");

        ExperienceInfoData made = Only();
        Assert.Equal(key, made.public_id);
        Assert.Equal(OtherId, made.owner_id);
        Assert.Equal("Garden Tour", made.name);
    }

    [Fact]
    public void ANamedExperienceCanBeOwnedByNameAndGivenAKey()
    {
        UUID key = UUID.Random();
        Run($"create named experience \"Garden Tour\" Other Resident {key}");

        ExperienceInfoData made = Only();
        Assert.Equal(key, made.public_id);
        Assert.Equal(OtherId, made.owner_id);
    }

    [Fact]
    public void CreateNamedExperiencePromptsForTheNameAndTheOwner()
    {
        Run("create named experience", "Garden Tour", "Test User");

        Assert.Equal(new[] { "Experience name: ", "Owner (first and last name, or key): " }, m_console.Prompts);
        ExperienceInfoData made = Only();
        Assert.Equal("Garden Tour", made.name);
        Assert.Equal(OwnerId, made.owner_id);
    }

    [Fact]
    public void CreateNamedExperiencePromptsForAMissingOwnerLastName()
    {
        Run("create named experience \"Garden Tour\" Other", "Resident");

        Assert.Equal(new[] { "Owner last name: " }, m_console.Prompts);
        Assert.Equal(OtherId, Only().owner_id);
    }

    [Fact]
    public void CreateNamedExperienceIsRefusedWhenNoUserAccountServiceIsSet()
    {
        ScriptedConsole console = new ScriptedConsole();
        MainConsole.Instance = console;
        IniConfigSource config = new IniConfigSource();
        IConfig section = config.AddConfig("ExperienceService");
        section.Set("StorageProvider", typeof(ExperienceConsoleCommandsTests).Assembly.Location + ":" + nameof(InMemoryExperienceData));
        section.Set("ConnectionString", m_storeName);
        new ExperienceService(config);

        console.RunCommand("create named experience \"Garden Tour\" Test User");

        Assert.Contains("No user account service is set ([ExperienceService] UserAccountService).", console.Lines);
        Assert.Empty(Store.All());
    }

    [Fact]
    public void AMissingNameIsRefused()
    {
        List<string> output = Run("create named experience", "");

        Assert.Equal(new[] { "Experience name: " }, m_console.Prompts);
        Assert.Contains("An experience needs a name.", output);
        Assert.Empty(Store.All());
    }

    [Fact]
    public void ANameLongerThanTheStoreKeepsIsRefused()
    {
        string name = new string('n', 43);
        List<string> output = Run($"create named experience {name} Test User");

        Assert.Contains("An experience name can be at most 42 characters.", output);
        Assert.Empty(Store.All());
    }

    [Fact]
    public void AnOwnerWhoIsNotAnAccountIsRefused()
    {
        Assert.Contains("No such user as Nobody Here", Run("create named experience \"Garden Tour\" Nobody Here"));
        UUID stranger = UUID.Random();
        Assert.Contains($"No user with key {stranger}", Run($"create named experience \"Garden Tour\" {stranger}"));
        Assert.Contains("Give the owner as a first and last name, or as a key.",
            Run("create named experience", "Garden Tour", "Test"));
        Assert.Empty(Store.All());
    }

    [Fact]
    public void ANameAnotherExperienceHasIsRefused()
    {
        ExperienceInfoData first = Add("Garden Tour", OwnerId);

        List<string> output = Run("create named experience \"garden tour\" Other Resident");

        Assert.Contains($"Experience {first.public_id} is already named \"Garden Tour\".", output);
        Assert.Single(Store.All());
    }

    [Fact]
    public void ATakenKeyOrABadKeyIsRefused()
    {
        ExperienceInfoData first = Add("Garden Tour", OwnerId);

        Assert.Contains("Experience already exists!", Run($"create named experience \"River Ride\" Test User {first.public_id}"));
        Assert.Contains("Invalid UUID", Run("create named experience \"River Ride\" Test User not-a-key"));
        Assert.Single(Store.All());
    }

    // ---- show ----

    [Fact]
    public void ShowExperiencesListsEveryExperienceWithItsStateOwnerAndName()
    {
        ExperienceInfoData garden = Add("Garden Tour", OwnerId);
        ExperienceInfoData river = Add("River Ride", OtherId, (int)ExperienceFlags.Suspended);
        ExperienceInfoData nameless = Add(string.Empty, OwnerId, (int)ExperienceFlags.Disabled);

        List<string> output = Run("show experiences");

        Assert.Contains(output, l => l.Contains(garden.public_id.ToString()) && l.Contains("enabled")
            && l.Contains("Test User") && l.Contains("Garden Tour"));
        Assert.Contains(output, l => l.Contains(river.public_id.ToString()) && l.Contains("suspended")
            && l.Contains("Other Resident") && l.Contains("River Ride"));
        Assert.Contains(output, l => l.Contains(nameless.public_id.ToString()) && l.Contains("disabled")
            && l.Contains("(no name)"));
        Assert.Contains("3 experiences.", output);
    }

    [Fact]
    public void ShowExperiencesWithTextListsOnlyNamesContainingIt()
    {
        Add("Garden Tour", OwnerId);
        ExperienceInfoData river = Add("River Ride", OtherId);

        List<string> output = Run("show experiences river");

        Assert.DoesNotContain(output, l => l.Contains("Garden Tour"));
        Assert.Contains(output, l => l.Contains(river.public_id.ToString()));
        Assert.Contains("1 experience.", output);
    }

    [Fact]
    public void ShowExperienceByNameOrKeyShowsItsDetails()
    {
        ExperienceInfoData garden = Add("Garden Tour", OwnerId, (int)ExperienceFlags.Disabled, "A walk.");

        foreach (string which in new[] { "\"garden tour\"", garden.public_id.ToString() })
        {
            List<string> output = Run("show experience " + which);
            Assert.Contains($"Key:         {garden.public_id}", output);
            Assert.Contains("Name:        Garden Tour", output);
            Assert.Contains("Description: A walk.", output);
            Assert.Contains($"Owner:       Test User ({OwnerId})", output);
            Assert.Contains("State:       disabled", output);
        }
    }

    [Fact]
    public void ShowExperienceSaysWhenNothingOrMoreThanOneMatches()
    {
        Assert.Contains("No experience named River Ride", Run("show experience \"River Ride\""));
        UUID unknown = UUID.Random();
        Assert.Contains($"No experience with key {unknown}", Run($"show experience {unknown}"));

        ExperienceInfoData one = Add("Garden Tour", OwnerId);
        ExperienceInfoData two = Add("Garden Tour", OtherId);
        List<string> output = Run("show experience \"Garden Tour\"");
        Assert.Contains("2 experiences are named Garden Tour; give the key of the one you mean:", output);
        Assert.Contains(output, l => l.Contains(one.public_id.ToString()));
        Assert.Contains(output, l => l.Contains(two.public_id.ToString()));
    }

    // ---- set name / description ----

    [Fact]
    public void SetExperienceNameRenamesItByKeyOrName()
    {
        ExperienceInfoData data = Add(string.Empty, OwnerId);

        Assert.Contains("Experience " + data.public_id + " is now named \"Garden Tour\".",
            Run($"set experience name {data.public_id} \"Garden Tour\""));
        Assert.Equal("Garden Tour", Only().name);

        Run("set experience name \"Garden Tour\" \"River Ride\"");
        Assert.Equal("River Ride", Only().name);
        Assert.Single(m_service.FindExperiencesByName("River"));
    }

    [Fact]
    public void SetExperienceNameRefusesAnEmptyALongOrATakenName()
    {
        ExperienceInfoData garden = Add("Garden Tour", OwnerId);
        ExperienceInfoData river = Add("River Ride", OwnerId);

        Assert.Contains("An experience needs a name.", Run($"set experience name {river.public_id}", ""));
        Assert.Contains("An experience name can be at most 42 characters.",
            Run($"set experience name {river.public_id} {new string('n', 43)}"));
        Assert.Contains($"Experience {garden.public_id} is already named \"Garden Tour\".",
            Run($"set experience name {river.public_id} \"GARDEN TOUR\""));
        Assert.Equal("River Ride", Store.All().Single(e => e.public_id == river.public_id).name);
    }

    [Fact]
    public void SetExperienceNameMayChangeOnlyTheCaseOfItsOwnName()
    {
        Add("Garden tour", OwnerId);
        Run("set experience name \"Garden tour\" \"Garden Tour\"");
        Assert.Equal("Garden Tour", Only().name);
    }

    [Fact]
    public void SetExperienceNamePromptsForWhatIsMissing()
    {
        ExperienceInfoData data = Add("Garden Tour", OwnerId);
        Run("set experience name", data.public_id.ToString(), "River Ride");

        Assert.Equal(new[] { "Experience (name or key): ", "New name: " }, m_console.Prompts);
        Assert.Equal("River Ride", Only().name);
    }

    [Fact]
    public void SetExperienceDescriptionSetsAndClearsIt()
    {
        Add("Garden Tour", OwnerId);

        Assert.Contains("The description of Garden Tour is set.",
            Run("set experience description \"Garden Tour\" \"A walk through the garden.\""));
        Assert.Equal("A walk through the garden.", Only().description);

        Run("set experience description \"Garden Tour\"", "");
        Assert.Equal(string.Empty, Only().description);
    }

    [Fact]
    public void SetExperienceDescriptionRefusesOneLongerThanTheStoreKeeps()
    {
        Add("Garden Tour", OwnerId, 0, "Before.");

        Assert.Contains("An experience description can be at most 128 characters.",
            Run($"set experience description \"Garden Tour\" {new string('d', 129)}"));
        Assert.Equal("Before.", Only().description);
    }

    [Fact]
    public void SetExperienceNameOrDescriptionOnAnUnknownExperienceChangesNothing()
    {
        Add("Garden Tour", OwnerId);
        Assert.Contains("No experience named River Ride", Run("set experience name \"River Ride\" Other"));
        Assert.Contains("No experience named River Ride", Run("set experience description \"River Ride\" Other"));
        Assert.Equal("Garden Tour", Only().name);
    }

    // ---- set state ----

    [Theory]
    [InlineData(0, "disabled", (int)ExperienceFlags.Disabled)]
    [InlineData(0, "suspended", (int)ExperienceFlags.Suspended)]
    [InlineData((int)ExperienceFlags.Disabled, "suspended", (int)(ExperienceFlags.Disabled | ExperienceFlags.Suspended))]
    [InlineData((int)ExperienceFlags.Suspended, "disabled", (int)ExperienceFlags.Disabled)]
    [InlineData((int)(ExperienceFlags.Disabled | ExperienceFlags.Suspended), "enabled", 0)]
    [InlineData((int)(ExperienceFlags.Grid | ExperienceFlags.Suspended), "enabled", (int)ExperienceFlags.Grid)]
    public void SetExperienceStateSetsOnlyTheDisabledAndSuspendedFlags(int before, string state, int after)
    {
        Add("Garden Tour", OwnerId, before);

        List<string> output = Run($"set experience state \"Garden Tour\" {state}");

        Assert.Equal(after, Only().properties);
        Assert.Contains($"Garden Tour is now {state}.", output);
    }

    [Fact]
    public void SetExperienceStateRefusesAStateTheStoreDoesNotHave()
    {
        Add("Garden Tour", OwnerId);

        List<string> output = Run("set experience state \"Garden Tour\" private");

        Assert.Contains("The state must be enabled, disabled or suspended.", output);
        Assert.Equal(0, Only().properties);
    }

    [Fact]
    public void SetExperienceStatePromptsForWhatIsMissing()
    {
        Add("Garden Tour", OwnerId);
        Run("set experience state", "Garden Tour", "suspended");

        Assert.Equal(new[] { "Experience (name or key): ", "State (enabled, disabled or suspended): " },
            m_console.Prompts);
        Assert.Equal((int)ExperienceFlags.Suspended, Only().properties);
    }

    // ---- help ----

    [Theory]
    [InlineData("create named experience",
        "create named experience [<name> [<owner first> <owner last> | <owner key> [<experience key>]]]")]
    [InlineData("show experiences", "show experiences [<text>]")]
    [InlineData("show experience", "show experience <name or key>")]
    [InlineData("set experience name", "set experience name <name or key> <new name>")]
    [InlineData("set experience description", "set experience description <name or key> <description>")]
    [InlineData("set experience state", "set experience state <name or key> <enabled|disabled|suspended>")]
    [InlineData("create experience", "create experience <first> <last>")]
    [InlineData("suspend experience", "suspend experience <key> <true/false>")]
    public void EachCommandHasHelpText(string command, string usage)
    {
        List<string> output = Run("help " + command);

        // The console prints the usage line and then the line that says what the command does.
        int at = output.IndexOf(usage);
        Assert.True(at >= 0, "no usage line: " + string.Join(" | ", output));
        Assert.False(string.IsNullOrWhiteSpace(output[at + 1]), "no description after the usage line");
    }

    // ---- test doubles ----

    /// <summary>A real command console whose input comes from a queue and whose output is kept.</summary>
    private sealed class ScriptedConsole : CommandConsole
    {
        public readonly Queue<string> Answers = new Queue<string>();
        public readonly List<string> Prompts = new List<string>();
        public readonly List<string> Lines = new List<string>();

        public ScriptedConsole() : base("test") { }

        public override string ReadLine(string p, bool isCommand, bool e)
        {
            Prompts.Add(p);
            Assert.True(Answers.Count > 0, "unexpected prompt: " + p);
            return Answers.Dequeue();
        }

        public override void Output(string format) => Lines.AddRange((format ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')));

        public override void Output(string format, params object[] components)
        {
            Output(components == null || components.Length == 0 ? format : string.Format(format, components));
        }
    }
}

/// <summary>An experience store in memory, one per connection string. Loaded by the service as a plugin.</summary>
public sealed class InMemoryExperienceData : IExperienceData
{
    public static readonly ConcurrentDictionary<string, InMemoryExperienceData> Stores =
        new ConcurrentDictionary<string, InMemoryExperienceData>();

    private readonly Dictionary<UUID, ExperienceInfoData> m_rows = new Dictionary<UUID, ExperienceInfoData>();

    public InMemoryExperienceData(string connectionString)
    {
        Stores[connectionString] = this;
    }

    public ExperienceInfoData[] All()
    {
        lock (m_rows)
            return m_rows.Values.Select(Copy).ToArray();
    }

    private static ExperienceInfoData Copy(ExperienceInfoData d) => new ExperienceInfoData
    {
        public_id = d.public_id, owner_id = d.owner_id, group_id = d.group_id, name = d.name,
        description = d.description, logo = d.logo, marketplace = d.marketplace, slurl = d.slurl,
        maturity = d.maturity, properties = d.properties
    };

    public ExperienceInfoData[] GetExperienceInfos(UUID[] experiences)
    {
        lock (m_rows)
            return experiences.Where(m_rows.ContainsKey).Select(k => Copy(m_rows[k])).ToArray();
    }

    // As the MySQL store: name LIKE '%search%', which ignores case under its collation.
    public ExperienceInfoData[] FindExperiences(string search)
    {
        lock (m_rows)
            return m_rows.Values.Where(d => d.name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(Copy).ToArray();
    }

    public UUID[] GetAgentExperiences(UUID agent_id)
    {
        lock (m_rows)
            return m_rows.Values.Where(d => d.owner_id == agent_id).Select(d => d.public_id).ToArray();
    }

    // The store's columns are NOT NULL (Experience.migrations), so a null would fail there.
    public bool UpdateExperienceInfo(ExperienceInfoData data)
    {
        if (data.name == null || data.description == null || data.marketplace == null || data.slurl == null)
            return false;
        lock (m_rows)
            m_rows[data.public_id] = Copy(data);
        return true;
    }

    public Dictionary<UUID, bool> GetExperiencePermissions(UUID agent_id) => new Dictionary<UUID, bool>();
    public bool ForgetExperiencePermissions(UUID agent_id, UUID experience_id) => false;
    public bool SetExperiencePermissions(UUID agent_id, UUID experience_id, bool allow) => false;
    public UUID[] GetGroupExperiences(UUID agent_id) => new UUID[0];
    public UUID[] GetExperiencesForGroups(UUID[] groups) => new UUID[0];
    public string GetKeyValue(UUID experience, string key) => null;
    public bool SetKeyValue(UUID experience, string key, string val) => false;
    public bool DeleteKey(UUID experience, string key) => false;
    public int GetKeyCount(UUID experience) => 0;
    public string[] GetKeys(UUID experience, int start, int count) => new string[0];
    public int GetKeyValueSize(UUID experience) => 0;
}

/// <summary>A user account service over a fixed list. Loaded by the experience service as a plugin.</summary>
public sealed class TestUserAccountService : IUserAccountService
{
    public static readonly List<UserAccount> Accounts = new List<UserAccount>();

    public TestUserAccountService(IConfigSource config) { }

    public UserAccount GetUserAccount(UUID scopeID, UUID userID) =>
        Accounts.FirstOrDefault(a => a.PrincipalID == userID);

    public UserAccount GetUserAccount(UUID scopeID, string FirstName, string LastName) =>
        Accounts.FirstOrDefault(a => string.Equals(a.FirstName, FirstName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.LastName, LastName, StringComparison.OrdinalIgnoreCase));

    public UserAccount GetUserAccount(UUID scopeID, string Email) => null;
    public bool SetDisplayName(UUID agentID, string displayName) => false;
    public List<UserAccount> GetUserAccounts(UUID scopeID, string query) => new List<UserAccount>();
    public List<UserAccount> GetUserAccountsWhere(UUID scopeID, string where) => new List<UserAccount>();
    public List<UserAccount> GetUserAccounts(UUID scopeID, List<string> IDs) => new List<UserAccount>();
    public bool StoreUserAccount(UserAccount data) => false;
    public void InvalidateCache(UUID userID) { }
}
