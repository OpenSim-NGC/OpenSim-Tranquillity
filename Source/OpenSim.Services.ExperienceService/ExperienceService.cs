using System.Diagnostics.CodeAnalysis;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

using Microsoft.Extensions.Logging;

namespace OpenSim.Services.ExperienceService;

public class ExperienceService : ExperienceServiceBase, IExperienceService
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(
            typeof(ExperienceService));

    private IUserAccountService? m_UserService;

    // SL per-experience KV quota: 128 MiB (was 16 MiB). Raised to the SL value so this
    // internal check (a redundant backstop) aligns with the authoritative Phlox-layer quota gate
    // in LSLSystemAPI, which emits XP_ERROR_QUOTA_EXCEEDED (11) on a byte-basis pre-write check.
    private const int MAX_QUOTA = 1024 * 1024 * 128;

    public ExperienceService(IConfigSource config)
        : base(config)
    {
        m_log.LogDebug("[EXPERIENCE SERVICE]: Starting experience service");

        IConfig userConfig = config.Configs["ExperienceService"];
        if (userConfig == null)
            throw new Exception("No ExperienceService configuration");

        string userServiceDll = userConfig.GetString("UserAccountService", string.Empty);
        if (userServiceDll != string.Empty)
            m_UserService = LoadPlugin<IUserAccountService>(userServiceDll, new Object[] { config });

        if (MainConsole.Instance != null)
        {
            MainConsole.Instance.Commands.AddCommand("Experience", false,
                    "create experience",
                    "create experience <first> <last>",
                    "Create a new experience owned by a user.", HandleCreateNewExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                    "suspend experience",
                    "suspend experience <key> <true/false>",
                    "Suspend/unsuspend an experience by its key.", HandleSuspendExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                    "create named experience",
                    "create named experience [<name> [<owner first> <owner last> | <owner key> [<experience key>]]]",
                    "Create an experience with a name, owned by a user account given by name or by key."
                        + " The experience key is random unless given. A name that another experience has is refused.",
                    HandleCreateNamedExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                    "show experiences",
                    "show experiences [<text>]",
                    "List experiences with their key, state, owner and name; with text, only those whose name contains it.",
                    HandleShowExperiences);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                    "show experience",
                    "show experience <name or key>",
                    "Show an experience. A name must match the whole name; case is ignored.",
                    HandleShowExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                    "set experience name",
                    "set experience name <name or key> <new name>",
                    "Rename an experience. A name that another experience has is refused.",
                    HandleSetExperienceName);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                    "set experience description",
                    "set experience description <name or key> <description>",
                    "Set an experience's description. An empty description clears it.",
                    HandleSetExperienceDescription);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                    "set experience state",
                    "set experience state <name or key> <enabled|disabled|suspended>",
                    "Set an experience's state: enabled, disabled (which its owner can also set in the viewer)"
                        + " or suspended (which its owner cannot change).",
                    HandleSetExperienceState);
        }
    }

    // The widths of the name and description columns of the experiences table (Experience.migrations).
    private const int MAX_NAME_LENGTH = 42;
    private const int MAX_DESCRIPTION_LENGTH = 128;

    private void HandleCreateNamedExperience(string module, string[] cmdparams)
    {
        string name;
        if (cmdparams.Length < 4)
            name = MainConsole.Instance.Prompt("Experience name");
        else name = cmdparams[3];

        if (!CheckName(name, UUID.Zero))
            return;

        // The owner is one word (a key) or two (first and last name); the optional experience key follows it.
        UserAccount? owner;
        int keyIndex;
        if (cmdparams.Length < 5)
        {
            string? answer = MainConsole.Instance.Prompt("Owner (first and last name, or key)");
            string[] words = (answer ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2)
                owner = FindOwner(words[0], words[1]);
            else if (words.Length == 1 && UUID.TryParse(words[0], out UUID answerKey))
                owner = FindOwner(answerKey);
            else
            {
                MainConsole.Instance.Output("Give the owner as a first and last name, or as a key.");
                return;
            }
            keyIndex = cmdparams.Length;
        }
        else if (UUID.TryParse(cmdparams[4], out UUID ownerKey))
        {
            owner = FindOwner(ownerKey);
            keyIndex = 5;
        }
        else
        {
            string lastName;
            if (cmdparams.Length < 6)
                lastName = MainConsole.Instance.Prompt("Owner last name");
            else lastName = cmdparams[5];
            owner = FindOwner(cmdparams[4], lastName);
            keyIndex = 6;
        }

        if (owner == null)
            return;

        UUID experienceKey = UUID.Random();
        if (cmdparams.Length > keyIndex && !UUID.TryParse(cmdparams[keyIndex], out experienceKey))
        {
            MainConsole.Instance.Output("Invalid UUID");
            return;
        }

        if (GetExperienceInfos(new UUID[] { experienceKey }).Length > 0)
        {
            MainConsole.Instance.Output("Experience already exists!");
            return;
        }

        ExperienceInfo new_info = new ExperienceInfo
        {
            public_id = experienceKey,
            owner_id = owner.PrincipalID,
            name = name.Trim()
        };

        if (UpdateExperienceInfo(new_info) == null)
            MainConsole.Instance.Output("Unable to create experience!");
        else
            MainConsole.Instance.Output("Experience \"{0}\" created with key {1}, owned by {2}.",
                new_info.name, experienceKey, AccountName(owner));
    }

    private void HandleShowExperiences(string module, string[] cmdparams)
    {
        string text = cmdparams.Length > 2 ? string.Join(" ", cmdparams, 2, cmdparams.Length - 2) : string.Empty;

        ExperienceInfo[] infos = FindExperiencesByName(text);
        foreach (ExperienceInfo info in infos.OrderBy(i => i.name, StringComparer.OrdinalIgnoreCase))
        {
            MainConsole.Instance.Output("{0}  {1,-9}  {2}  {3}", info.public_id, StateOf(info),
                OwnerName(info.owner_id), info.name == string.Empty ? "(no name)" : info.name);
        }
        MainConsole.Instance.Output(infos.Length == 1 ? "1 experience." : string.Format("{0} experiences.", infos.Length));
    }

    private void HandleShowExperience(string module, string[] cmdparams)
    {
        string which;
        if (cmdparams.Length < 3)
            which = MainConsole.Instance.Prompt("Experience (name or key)");
        else which = cmdparams[2];

        ExperienceInfo? info = FindExperienceForCommand(which);
        if (info == null)
            return;

        MainConsole.Instance.Output("Key:         {0}", info.public_id);
        MainConsole.Instance.Output("Name:        {0}", info.name);
        MainConsole.Instance.Output("Description: {0}", info.description);
        MainConsole.Instance.Output("Owner:       {0} ({1})", OwnerName(info.owner_id), info.owner_id);
        MainConsole.Instance.Output("Group:       {0}", info.group_id);
        MainConsole.Instance.Output("State:       {0}", StateOf(info));
        MainConsole.Instance.Output("Maturity:    {0}", info.maturity);
    }

    private void HandleSetExperienceName(string module, string[] cmdparams)
    {
        string which;
        if (cmdparams.Length < 4)
            which = MainConsole.Instance.Prompt("Experience (name or key)");
        else which = cmdparams[3];

        ExperienceInfo? info = FindExperienceForCommand(which);
        if (info == null)
            return;

        string name;
        if (cmdparams.Length < 5)
            name = MainConsole.Instance.Prompt("New name");
        else name = cmdparams[4];

        if (!CheckName(name, info.public_id))
            return;

        info.name = name.Trim();
        if (UpdateExperienceInfo(info) == null)
            MainConsole.Instance.Output("Error updating experience!");
        else
            MainConsole.Instance.Output("Experience {0} is now named \"{1}\".", info.public_id, info.name);
    }

    private void HandleSetExperienceDescription(string module, string[] cmdparams)
    {
        string which;
        if (cmdparams.Length < 4)
            which = MainConsole.Instance.Prompt("Experience (name or key)");
        else which = cmdparams[3];

        ExperienceInfo? info = FindExperienceForCommand(which);
        if (info == null)
            return;

        string description;
        if (cmdparams.Length < 5)
            description = MainConsole.Instance.Prompt("Description");
        else description = cmdparams[4];

        description = description.Trim();
        if (description.Length > MAX_DESCRIPTION_LENGTH)
        {
            MainConsole.Instance.Output("An experience description can be at most {0} characters.", MAX_DESCRIPTION_LENGTH);
            return;
        }

        info.description = description;
        if (UpdateExperienceInfo(info) == null)
            MainConsole.Instance.Output("Error updating experience!");
        else
            MainConsole.Instance.Output("The description of {0} is set.", DisplayName(info));
    }

    private void HandleSetExperienceState(string module, string[] cmdparams)
    {
        string which;
        if (cmdparams.Length < 4)
            which = MainConsole.Instance.Prompt("Experience (name or key)");
        else which = cmdparams[3];

        ExperienceInfo? info = FindExperienceForCommand(which);
        if (info == null)
            return;

        string state;
        if (cmdparams.Length < 5)
            state = MainConsole.Instance.Prompt("State (enabled, disabled or suspended)");
        else state = cmdparams[4];

        // Only the Disabled and Suspended flags change; the other flags are kept.
        switch (state.Trim().ToLowerInvariant())
        {
            case "enabled":
                info.properties &= ~(int)(ExperienceFlags.Disabled | ExperienceFlags.Suspended);
                break;
            case "disabled":
                info.properties |= (int)ExperienceFlags.Disabled;
                info.properties &= ~(int)ExperienceFlags.Suspended;
                break;
            case "suspended":
                info.properties |= (int)ExperienceFlags.Suspended;
                break;
            default:
                MainConsole.Instance.Output("The state must be enabled, disabled or suspended.");
                return;
        }

        if (UpdateExperienceInfo(info) == null)
            MainConsole.Instance.Output("Error updating experience!");
        else
            MainConsole.Instance.Output("{0} is now {1}.", DisplayName(info), StateOf(info));
    }

    // A key, or the whole name (case ignored). Several experiences may share a name, as the viewer does not
    // stop a rename to a name in use; then they are listed and the key is asked for.
    private ExperienceInfo? FindExperienceForCommand(string which)
    {
        which = which.Trim();
        if (UUID.TryParse(which, out UUID key))
        {
            ExperienceInfo[] byKey = GetExperienceInfos(new UUID[] { key });
            if (byKey.Length == 1)
                return byKey[0];
            MainConsole.Instance.Output("No experience with key {0}", key);
            return null;
        }

        ExperienceInfo[] named = FindExperiencesByName(which)
            .Where(i => string.Equals(i.name, which, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 1)
            return named[0];

        if (named.Length == 0)
            MainConsole.Instance.Output("No experience named {0}", which);
        else
        {
            MainConsole.Instance.Output("{0} experiences are named {1}; give the key of the one you mean:", named.Length, which);
            foreach (ExperienceInfo info in named)
                MainConsole.Instance.Output("{0}  {1}", info.public_id, OwnerName(info.owner_id));
        }
        return null;
    }

    private bool CheckName(string name, UUID self)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            MainConsole.Instance.Output("An experience needs a name.");
            return false;
        }
        if (name.Length > MAX_NAME_LENGTH)
        {
            MainConsole.Instance.Output("An experience name can be at most {0} characters.", MAX_NAME_LENGTH);
            return false;
        }

        ExperienceInfo? taken = FindExperiencesByName(name).FirstOrDefault(
            i => i.public_id != self && string.Equals(i.name, name, StringComparison.OrdinalIgnoreCase));
        if (taken != null)
        {
            MainConsole.Instance.Output("Experience {0} is already named \"{1}\".", taken.public_id, taken.name);
            return false;
        }
        return true;
    }

    private UserAccount? FindOwner(string firstName, string lastName)
    {
        if (!HaveUserService())
            return null;
        UserAccount? account = m_UserService.GetUserAccount(UUID.Zero, firstName, lastName);
        if (account == null)
            MainConsole.Instance.Output("No such user as {0} {1}", firstName, lastName);
        return account;
    }

    private UserAccount? FindOwner(UUID key)
    {
        if (!HaveUserService())
            return null;
        UserAccount? account = m_UserService.GetUserAccount(UUID.Zero, key);
        if (account == null)
            MainConsole.Instance.Output("No user with key {0}", key);
        return account;
    }

    [MemberNotNullWhen(true, nameof(m_UserService))]
    private bool HaveUserService()
    {
        if (m_UserService != null)
            return true;
        MainConsole.Instance.Output("No user account service is set ([ExperienceService] UserAccountService).");
        return false;
    }

    private string OwnerName(UUID owner)
    {
        UserAccount? account = m_UserService?.GetUserAccount(UUID.Zero, owner);
        return account == null ? owner.ToString() : AccountName(account);
    }

    private static string AccountName(UserAccount account)
    {
        return account.FirstName + " " + account.LastName;
    }

    private static string DisplayName(ExperienceInfo info)
    {
        return info.name == string.Empty ? info.public_id.ToString() : info.name;
    }

    private static string StateOf(ExperienceInfo info)
    {
        if ((info.properties & (int)ExperienceFlags.Suspended) != 0)
            return "suspended";
        if ((info.properties & (int)ExperienceFlags.Disabled) != 0)
            return "disabled";
        return "enabled";
    }

    private void HandleCreateNewExperience(string module, string[] cmdparams)
    {
        if (!HaveUserService())
            return;

        string firstName;
        string lastName;
        string experienceKey;

        if (cmdparams.Length < 3)
            firstName = MainConsole.Instance.Prompt("Experience owner first name", "Test");
        else firstName = cmdparams[2];

        if (cmdparams.Length < 4)
            lastName = MainConsole.Instance.Prompt("Experience owner last name", "Resident");
        else lastName = cmdparams[3];

        UUID suggestedKey = UUID.Random();

        if (cmdparams.Length < 5)
            experienceKey = MainConsole.Instance.Prompt("Experience Key", suggestedKey.ToString());
        else experienceKey = cmdparams[4];

        UUID newExperienceKey;

        if(!UUID.TryParse(experienceKey, out newExperienceKey))
        {
            MainConsole.Instance.Output("Invalid UUID");
            return;
        }

        UserAccount? account = m_UserService.GetUserAccount(UUID.Zero, firstName, lastName);
        if (account == null)
        {
            MainConsole.Instance.Output("No such user as {0} {1}", firstName, lastName);
            return;
        }

        var existing = GetExperienceInfos(new UUID[] { newExperienceKey });
        if(existing.Length > 0)
        {
            MainConsole.Instance.Output("Experience already exists!");
            return;
        }

        ExperienceInfo new_info = new ExperienceInfo
        {
            public_id = newExperienceKey,
            owner_id = account.PrincipalID
        };

        var stored_info = UpdateExperienceInfo(new_info);

        if (stored_info == null)
            MainConsole.Instance.Output("Unable to create experience!");
        else
        {
            MainConsole.Instance.Output("Experience created!");
        }
    }

    private void HandleSuspendExperience(string module, string[] cmdparams)
    {
        string experience_key;
        string enabled_str;

        if (cmdparams.Length < 3)
            experience_key = MainConsole.Instance.Prompt("Experience Key");
        else experience_key = cmdparams[2];

        UUID experienceID;
        if(!UUID.TryParse(experience_key, out experienceID))
        {
            MainConsole.Instance.Output("Invalid key!");
            return;
        }

        if (cmdparams.Length < 4)
            enabled_str = MainConsole.Instance.Prompt("Suspended:", "false");
        else enabled_str = cmdparams[3];

        bool suspend = enabled_str == "true";

        var infos = GetExperienceInfos(new UUID[] { experienceID });
        if(infos.Length != 1)
        {
            MainConsole.Instance.Output("Experience not found!");
            return;
        }

        ExperienceInfo info = infos[0];

        bool is_suspended = (info.properties & (int)ExperienceFlags.Suspended) != 0;

        string message = "";
        bool update = false;

        if (suspend && !is_suspended)
        {
            info.properties |= (int)ExperienceFlags.Suspended;
            message = "Experience has been suspended";
            update = true;
        }
        else if(!suspend && is_suspended)
        {
            info.properties &= ~(int)ExperienceFlags.Suspended;
            message = "Experience has been unsuspended";
            update = true;
        }
        else if(suspend && is_suspended)
        {
            message = "Experience is already suspended";
        }
        else if (!suspend && !is_suspended)
        {
            message = "Experience is not suspended";
        }

        if(update)
        {
            var updated = UpdateExperienceInfo(info);
            if (updated != null)
            {
                MainConsole.Instance.Output(message);
            }
            else
                MainConsole.Instance.Output("Error updating experience!");
        }
        else
        {
            MainConsole.Instance.Output(message);
        }
    }

    public Dictionary<UUID, bool> FetchExperiencePermissions(UUID agent_id)
    {
        return m_Database.GetExperiencePermissions(agent_id);
    }

    public ExperienceInfo[] FindExperiencesByName(string search)
    {
        List<ExperienceInfo> infos = new List<ExperienceInfo>();
        ExperienceInfoData[] datas = m_Database.FindExperiences(search);

        foreach (var data in datas)
        {
            ExperienceInfo info = new ExperienceInfo(data.ToDictionary());
            infos.Add(info);
        }

        return infos.ToArray();
    }

    public UUID[] GetAgentExperiences(UUID agent_id)
    {
        return m_Database.GetAgentExperiences(agent_id);
    }

    public ExperienceInfo[] GetExperienceInfos(UUID[] experiences)
    {
        ExperienceInfoData[] datas = m_Database.GetExperienceInfos(experiences);

        List<ExperienceInfo> infos = new List<ExperienceInfo>();

        foreach (var data in datas)
        {
            infos.Add(new ExperienceInfo(data.ToDictionary()));
        }

        return infos.ToArray();
    }

    public UUID[] GetExperiencesForGroups(UUID[] groups)
    {
        return m_Database.GetExperiencesForGroups(groups);
    }

    public UUID[] GetGroupExperiences(UUID group_id)
    {
        return m_Database.GetGroupExperiences(group_id);
    }

    public ExperienceInfo? UpdateExperienceInfo(ExperienceInfo info)
    {
        ExperienceInfoData data = new ExperienceInfoData();

        data.public_id = info.public_id;
        data.owner_id = info.owner_id;
        data.name = info.name;
        data.description = info.description;
        data.group_id = info.group_id;
        data.slurl = info.slurl;
        data.logo = info.logo;
        data.marketplace = info.marketplace;
        data.maturity = info.maturity;
        data.properties = info.properties;

        if (m_Database.UpdateExperienceInfo(data))
        {
            var find = GetExperienceInfos(new UUID[] { data.public_id });
            if(find.Length == 1)
            {
                return new ExperienceInfo(find[0].ToDictionary());
            }
        }
        return null;
    }

    public bool UpdateExperiencePermissions(UUID agent_id, UUID experience, ExperiencePermission perm)
    {
        if (perm == ExperiencePermission.None)
            return m_Database.ForgetExperiencePermissions(agent_id, experience);
        else return m_Database.SetExperiencePermissions(agent_id, experience, perm == ExperiencePermission.Allowed);
    }

    public string GetKeyValue(UUID experience, string key)
    {
        return m_Database.GetKeyValue(experience, key);
    }

    public string CreateKeyValue(UUID experience, string key, string value)
    {
        int current_size = m_Database.GetKeyValueSize(experience);
        if (current_size + key.Length + value.Length > MAX_QUOTA)
            return "full";

        string get = m_Database.GetKeyValue(experience, key);
        if (get == null)
        {
            if (m_Database.SetKeyValue(experience, key, value))
                return "success";
            else return "error";
        }
        else return "exists";
    }

    public string UpdateKeyValue(UUID experience, string key, string val, bool check, string original)
    {
        string get = m_Database.GetKeyValue(experience, key);
        if (get != null)
        {
            if (check && get != original)
                return "mismatch";

            int current_size = m_Database.GetKeyValueSize(experience);
            if ((current_size - get.Length) + val.Length > MAX_QUOTA)
                return "full";

            if (m_Database.SetKeyValue(experience, key, val))
                return "success";
            else return "error";
        }
        else return "missing";
    }

    public string DeleteKey(UUID experience, string key)
    {
        string get = m_Database.GetKeyValue(experience, key);
        if (get != null)
        {
            return m_Database.DeleteKey(experience, key) ? "success" : "failed";
        }
        return "missing";
    }

    public int GetKeyCount(UUID experience)
    {
        return m_Database.GetKeyCount(experience);
    }

    public string[] GetKeys(UUID experience, int start, int count)
    {
        return m_Database.GetKeys(experience, start, count);
    }

    public int GetSize(UUID experience)
    {
        return m_Database.GetKeyValueSize(experience);
    }
}
