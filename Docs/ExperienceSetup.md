# Experience settings and console commands

Reference for operators: the settings that turn Experiences on, and the console commands that manage them.
Experiences are stored only by the MySQL data plugin (`OpenSim.Data.MySQL.dll`); there is no SQLite or PostgreSQL
Experience store.

## Settings

### Grid: the grid server (Robust)

```ini
[ServiceList]
    ExperienceServiceConnector = "${Const|PrivatePort}/OpenSim.Server.Handlers.dll:ExperienceServiceConnector"

[ExperienceService]
    LocalServiceModule = "OpenSim.Services.ExperienceService.dll:ExperienceService"
    UserAccountService = "OpenSim.Services.UserAccountService.dll:UserAccountService"
    ; StorageProvider and ConnectionString default to [DatabaseService]; set them here to use another database.
```

`UserAccountService` is needed by the commands that look up an owner.

### Grid: each region server

```ini
[Modules]
    ExperienceServices = "RemoteExperienceServicesConnector"

[ExperienceService]
    LocalServiceModule = "OpenSim.Services.Connectors.dll:RemoteExperienceServicesConnector"
    ExperienceServerURI = "${Const|BaseURL}:${Const|PrivatePort}"

[Experience]
    Enabled = true
    ; Who may use "Acquire an Experience" in the viewer: EstateManagersAndRegionOwners (default), Anyone, AdminsOnly.
    ; ExperienceCreators = EstateManagersAndRegionOwners
```

`[Experience] Enabled` is off by default; with it off, a region offers no Experience capabilities.
`ExperienceCreators` is read per region.

### Standalone

```ini
[Modules]
    ExperienceService = "LocalExperienceServicesConnector"

[ExperienceService]
    LocalServiceModule = "OpenSim.Services.ExperienceService.dll:ExperienceService"
    UserAccountService = "OpenSim.Services.UserAccountService.dll:UserAccountService"

[Experience]
    Enabled = true
```

## Console commands

They are on the console of the process that loads `OpenSim.Services.ExperienceService.dll:ExperienceService`: the
grid server on a grid, the region server in standalone. An argument with spaces goes in double quotes. A missing
argument is asked for, except where it is shown as optional.

| Command | What it does |
| --- | --- |
| `create named experience [<name> [<owner first> <owner last> \| <owner key> [<experience key>]]]` | Creates an Experience with a name, owned by a user account given by name or key. The key is random unless given. |
| `show experiences [<text>]` | Lists Experiences with key, state, owner and name; with text, those whose name contains it. |
| `show experience <name or key>` | Shows one Experience. |
| `set experience name <name or key> <new name>` | Renames an Experience. |
| `set experience description <name or key> <description>` | Sets the description; an empty one clears it. |
| `set experience state <name or key> <enabled\|disabled\|suspended>` | Sets the state. |
| `create experience <first> <last> [<key>]` | Creates an Experience with no name. |
| `suspend experience <key> <true/false>` | Suspends or unsuspends an Experience. |

- A name is 1 to 42 characters and a description at most 128 (the store's column widths). A name that another
  Experience already has, ignoring case, is refused.
- An owner who is not a user account is refused.
- `<name or key>`: a key, or the whole name ignoring case. When several Experiences have that name, they are listed
  and the key must be used.
- States: `disabled` is the state an owner can also set in the viewer (the "Enable Experience" box when editing the
  Experience's profile); the owner cannot change `suspended`. `enabled` clears both; `disabled` clears `suspended`.
  How a script is affected depends on the script engine: YEngine refuses a script's permission request for a
  disabled or suspended Experience.
- There is no command that deletes an Experience.
