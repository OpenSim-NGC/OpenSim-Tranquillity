# Tranquillity 1.1 - Release Notes

**Previous release:** `v1.0` (2026-08-31, commit `29f312cb790a`)
**This release:** `1.1` (draft; no release date assigned here)
**Coverage:** changes present at `01f27f1f54` (2026-10-13), compared with `v1.0`.

These notes describe the code changes since the released `v1.0` tag, including
work developed in parallel with the 1.0 release branch and merged into the
development line. Commit dates alone therefore do not define the release range.
The comparison includes fixes also delivered on the 1.0 hotfix line; installations
already using those hotfixes may already have some of the fixes below.

This is a substantial scripting, compatibility and operational update. The
Generic Host, SkiaSharp/CoreJ2K imaging, DotNetCorePlugins and reorganized solution
were introduced in 1.0; they are not new introductions in 1.1. See the
[1.0 release notes](RELEASE_NOTES_1.0.md) for that earlier modernization.

## Highlights

- **.NET 10** replaces .NET 8 as the solution target.
- **Phlox and SLua scripting** add a bytecode compiler, serializable virtual
  machine, region script engine, InWorldz/Halcyon APIs and scripted bots.
  Phlox is the default in the shipped region configuration; YEngine remains
  available, and saved-state routing helps existing YEngine scripts stay on it.
- **Experiences** gain expanded consent, permission, persistence and viewer
  support, plus operator console commands.
- **AIS v3 inventory and server-side appearance baking** provide new,
  independently opt-in paths for modern viewers.
- **Trusted Hypergrid** adds signed gatekeeper calls and an operator-managed grid
  trust registry. It is off by default and is not a content-copy prevention system.
- **Control-plane hardening** requires explicit trust for simulator/service
  traffic in multi-host deployments and changes some Hypergrid behavior.
- **Logging is migrated from log4net to Microsoft.Extensions.Logging**, with
  Serilog rolling files, prompt-safe console output and optional separate stats
  logs.
- **Physics, meshing, crossings, inventory and database fixes** improve core
  reliability. Tests move to xUnit, with substantial new regression coverage.

## Upgrade checklist and compatibility changes

Read this section before replacing an existing installation.

1. **Install the .NET 10 runtime appropriate to the server executables.**
   Source builds use the .NET 10 SDK; `global.json` currently requests `10.0.110`
   with `latestMajor` roll-forward, and the solution targets `net10.0`.
   Update service definitions, build agents and publish-path assumptions that
   still refer to .NET 8.
2. **Back up databases, configuration, inventory and script-state storage.**
   Region/task inventory and estate persistence have changed. Phlox introduces
   its own SQLite state database and bytecode cache; it does not translate
   YEngine state.
3. **Merge the new configuration templates deliberately.** Shipped templates
   have been renamed:

   | Earlier name | Current name |
   |---|---|
   | `OpenSimDefaults.ini` | `RegionServerDefaults.ini` |
   | `OpenSim.ini.example` | `RegionServer.ini.example` |
   | `Robust.ini.example` | `GridServer.ini.example` |
   | `Robust.HG.ini.example` | `GridServer.HG.ini.example` |
   | `robuststartuplogo.txt` | `gridserver.startuplogo.txt` |

   Review explicit `--inimaster`, `--inifile` and `--inidirectory` arguments in
   launch scripts. Template renames do not automatically rename or migrate
   operator-owned configuration files. Avoid assuming legacy default filenames
   are the same as the current executable's defaults.
4. **Choose the default script engine.** The shipped defaults enable Phlox and
   YEngine and set `[Startup] DefaultScriptEngine = "InWorldz.Phlox"`.
   To retain YEngine for new/unheaded scripts, explicitly set it to `"YEngine"`.
   With Phlox as default, keep YEngine enabled for existing scripts with matching
   YEngine state. New or edited unheaded scripts use the default engine; scripts
   without usable state start fresh. See [Phlox setup](PhloxSetup.md).
5. **Configure `ControlPlaneTrustedHosts` before a multi-host rollout.**
   Destination regions must trust the actual outbound addresses of the login/grid
   server and peer simulators. Missing entries can break login placement, child
   agents, neighbour announcements and crossings. Region-control endpoints still
   trust loopback. `/assets` and `/xinventory` control-plane checks are currently
   disabled pending replacement authentication/authorization.
   See [Security hardening](security/Hardening.md) for exact configuration,
   reverse-proxy guidance and `403`/`404` diagnosis.
6. **Plan for changed Hypergrid behavior.** Hypergrid return-home requests are
   restricted; returning users must log in again. Public profile reads remain,
   but sensitive profile and group writes require trusted sources. Review the
   [hardening guide](security/Hardening.md) before enabling production traffic.
7. **Replace log4net configuration.** Remove `--logconfig` from launch commands
   and do not rely on the old XML appenders/configuration. Configure application
   and stats paths in `[Startup]`; details are below.
8. **Leave AIS, server-side baking and Trusted Hypergrid off until configured
   and tested.** They are separate opt-ins. Server-side baking needs an
   appearance service and its login URL; AIS changes the viewer's inventory
   mutation path, without a mutation fallback.
9. **If using Gloebit, test its database migrations and callbacks before
   production.** Transaction callback keys and OAuth state are now persisted.
   See [Security hardening](security/Hardening.md#gloebit-addon).
10. **Rebuild locally maintained modules against 1.1.** The framework target,
    logging contracts, script-engine interfaces and region/physics integration
    surfaces have changed. See [Plugin development](PLUGIN_DEVELOPMENT.md).

## Platform, dependencies and packaging

- The solution targets .NET 10; AppVeyor is updated to a .NET 10 builder.
- OpenMetaverse packages are now obtained from **nuget.org**, at version `1.1.7`
  in the shared build properties. The repository's `nuget.config` no longer
  declares the GitHub Packages feed, so the old GitHub package credentials are
  not required for the dependencies declared there.
- Removed Warp3D's stale explicit Meshmerizer `1.0.6` dependency so it uses the
  solution's shared OpenMetaverse version.
- SQLite access is normalized on `System.Data.SQLite`, removing the parallel
  `Microsoft.Data.Sqlite` usage.
- The EF data-model project switches from Pomelo to
  `Microting.EntityFrameworkCore.MySql`, with EF Core and related packages
  updated for .NET 10. This is a data-model provider change, not a replacement
  of all runtime database plugins.
- Removed remaining BinaryFormatter-based persistence/serialization paths.
- Release version formatting no longer automatically appends the Git hash.
- Configuration templates and startup-logo naming now follow RegionServer and
  GridServer terminology.

See [Building](BUILDING.md) for build/publish workflows. Some older documentation
still describes .NET 8, NUnit or private-feed credentials; the current project
files, `global.json` and `nuget.config` define the build requirements for 1.1.

## Scripting: Phlox, SLua and multi-engine operation

### New engine and language

Phlox ports the InWorldz/Halcyon script engine to Tranquillity. LSL and OSSL
compile to bytecode and execute on a cooperative virtual machine. SLua adds a
Luau-flavored front end on the same runtime, including closures, metatables,
varargs, multiple returns, the `ll.*` API, vectors and string/table/math libraries.

- Runtime state can preserve globals, current state, queued events, timers,
  listens and mid-event execution across restarts on unchanged bytecode.
- Script state can travel with serialized objects for take/rez, attachments and
  region transfers. Arriving state is dispatched to the engine owning each
  script rather than indiscriminately to the default engine.
- Phlox exposes LSL/OSSL, the InWorldz `iw*` APIs, `bot*` APIs, Experience
  key-value operations and all 13 `llLinksetData*` functions.
- Phlox OSSL calls honor the existing `[OSSL]` enablement, threat-level and
  per-function permission settings. `osMakeNotecard` is included in both forms.
- Per-script statistics and transient console suspend/resume support are added.
- Blocking work is isolated through bounded workers and a per-region service
  lane; scheduler thread priority is configurable.

SLua is an additional compiler front end, **not a third script runtime**.
YEngine is not removed, and this release does not merge the two engines.

References:
[Phlox/SLua overview](PhloxSLua.md),
[operator setup and commands](PhloxSetup.md),
[differences, extensions and known gaps](PhloxKnownDefects.md).

### Engine selection and migration

- The first-line headers `//YEngine:` and `//InWorldz.Phlox:` explicitly select
  a loaded engine. `//InWorldz.Phlox:slua` selects Phlox and SLua regardless of
  the default engine.
- For unheaded scripts, matching state in the default engine takes precedence;
  otherwise another loaded engine with matching state retains the script.
  With no matching state, the default engine is used.
- Editing/recompiling changes the asset ID, so old state no longer matches.
  Changing engines starts fresh; **no YEngine-to-Phlox state conversion occurs**.
  Resetting a running script does not itself migrate it.
- Phlox state remains available when a script is handed to another engine.
  Returning unchanged scripts can resume that earlier Phlox state; work done
  under the other engine is not merged into it.

The full routing rules, error behavior and backup advice are in
[Phlox setup](PhloxSetup.md#what-changing-the-default-engine-does-to-existing-scripts)
(#304).

### Compiler correctness and state safety

- Fixed silent LSL miscompiles involving assignment expressions, chained
  addition/subtraction, boolean chains, bitwise precedence and typed constants.
- Fixed assignment type-checking/promotion, the minimum 32-bit integer literal,
  rotation negation, non-integer `for` conditions and hexadecimal vector
  components. `TOUCH_INVALID_FACE` has its correct value.
- Bytecode cache versioning forces recompilation when its format changes.
  Saved states record bytecode identity: after an incompatible recompile, globals,
  LSL state, timers, listens and queued events are preserved, but an in-progress
  event is dropped and the script resumes idle with a diagnostic.
- State writes are queued and retried with explicit failure reporting; corrupt
  local state can be quarantined in `script_state_rejected`. Failed database
  reads do not silently overwrite saved state with a fresh script.
- Carried state is validated against the compiled script and memory/event limits.
- Region stop captures events awaiting the scheduler; crossings hold scripts and
  preserve queued events rather than executing them on both sides.
- Restart restores listen enablement, regex filters and bot listens; stopped
  scripts remain stopped.
- Resets and LSL state changes discard stale events from the previous execution.

See [Compiler correctness](Phlox-compiler-correctness.md) for examples and the
one-time recompile implications, and [Phlox setup](PhloxSetup.md#where-phlox-keeps-its-files)
for storage paths and upgrade behavior (#213, #238, #260, #267, #272, #286).

### API behavior and event delivery

- HTTP, XML-RPC and other asynchronous responses reach the owning engine in the
  request's own region, fixing Phlox responses lost through cross-load-context
  type mismatches.
- `link_message`, `dataserver`, `osMessageObject`, `object_rez`, email and
  `linkset_data` delivery now reaches the scripts required by each event's scope
  across multiple engines, not just one engine or the requesting script.
- Script inventory reads use the locks taken by core inventory writers.
- Notecard parsing uses the declared UTF-8 byte length, avoids an extra line
  after a final newline, and handles embedded-item notecards as EOF.
  Missing fetched notecards are logged.
- Phlox's `llGetNotecardLine` and `llGetNumberOfNotecardLines` pause for 0.1 s by
  default, matching SL/YEngine. Operators can select legacy Halcyon timing with
  `NotecardPause`; see [Phlox setup](PhloxSetup.md).
- Queue-drop and listen-limit diagnostics are rate-limited while reporting
  sustained drops. Name requests, inventory gives, detected avatar types and
  permission-release notifications receive correctness fixes.
- Fixed `llCastRay` hit limits up to 256, rejection accounting and requested
  phantom-object detection; refusal diagnostics are bounded.
- Corrected rotation-axis status flags, physics read-outs, non-finite impulses,
  `llPushObject` rules, and attachment buoyancy/hover behavior.
- Reflection-probe flags are returned as integers, and loaded ambiance is
  retained up to 100 rather than incorrectly clamped to 1.
- Added movement heartbeat event support and a group-level linkset-data accessor.

Notable changes: #186, #221, #225, #228, #229, #239, #257, #269, #278, #282,
#284, #285, #294.

### Scripted bots/NPCs

- Added `IBotManager`, `BotManager` and `BotPersistenceManager` on top of the core
  NPC facilities, supporting Phlox's scripted bot API.
- Bots retain their keys across restart.
- Profile parameters, wait points, timed-out movement, wandering, following and
  `bot_update` behavior have been brought into line with the port's Halcyon
  contracts.
- Bot and NPC APIs require the NPC module; follow steering is not full obstacle
  avoidance.

See [Phlox setup](PhloxSetup.md#other-sections-phlox-reads) and the
[bot API reference](PhloxKnownDefects.md) (#181, #268, #277).

## Experiences and permissions

- Expanded Experience viewer capabilities, consent/admission and script API
  behavior toward SL compatibility.
- Added console commands to create named Experiences, list/show them, rename
  them, set descriptions and enable, disable or suspend them.
- A script's Experience association is persisted in task inventory and retained
  through user inventory. Foreign-grid object data has its Experience links
  cleared on import.
- Estate blocked-Experience lists are sent in viewer replies; Experience lists
  persist in the SQLite and PostgreSQL estate stores.
- Blocking an Experience revokes the avatar's grants across script engines.
  Phlox also refuses new grants and ends held grants for disabled/suspended
  Experiences, and handles revocation by parcel/estate policy.
- Fixed estate access enforcement for `DenyIdentified`/`DenyTransacted` and
  erroneous failure reports for allowed estate-manager access changes.

**Deployment limits:** `[Experience] Enabled` is off by default. The Experience
service's own database store is **MySQL-only**; estate-list support in SQLite or
PostgreSQL does not provide an Experience service store for those providers.

See [Experience settings and console commands](ExperienceSetup.md)
(#184, #188, #223, #242-246, #248, #250, #261).

## Viewer compatibility: inventory and appearance

### AIS v3 inventory (opt-in)

The new region module advertises `InventoryAPIv3` and read-only `LibraryAPIv3`
capabilities and translates requests into the existing inventory service.

- Supports inventory fetches, item/folder updates and deletion, folder/link
  creation, library copying, Trash purging and outfit-link replacement ("slam").
- Agent-scoped access checks prevent mutations to another user's inventory.
- Rejects malformed LLSD and oversized bodies without writes, serializes slams
  on the same folder, and maintains viewer-facing folder versions/responses.
- Global `[AIS] Enabled = false` is the default. Region sections can override
  it using `AIS_Enabled`.

**Important limits:** creating inventory items through AIS returns 501 for a
non-empty `items` array (stock viewers use legacy creation); folder thumbnails
and favourites are not persisted; HG inventory services still refuse folder
deletion. Once AIS is advertised, LL viewer mutation failures do not fall back
to the old path.

Read the [AIS specification/operator guide](feature/ais-v3/AIS-V3-SPEC.md) and
follow the [live rollout checklist](feature/ais-v3/A5-LIVE-CHECKLIST.md) (#207).

### Server-side baking (opt-in)

- Added a SkiaSharp/JPEG 2000 compositor for avatar bake channels, including
  Bakes-on-Mesh auxiliary channels.
- Added the region protocol flag, `UpdateAvatarAppearance` capability and
  `AppearanceData` needed by current LL viewers.
- Bakes follow appearance saves, respect Current Outfit folder versions and
  layer ordering, reuse unchanged channel hashes, and supersede old bake assets.
- Added an appearance service to serve stored bakes and the
  `appearance serverbake <first> <last>` console command.
- Guarded against losing body-part inputs and against baking a closed presence.

**Deployment:** `[Appearance] ServerSideBaking = false` is the default.
Enabling it makes Firestorm use server bakes on that region. Configure the
appearance service and `[LoginService] AgentAppearanceServiceURL` before enabling
it. AIS is independently optional for baking, but the LL viewer needs AIS to
change outfits.

Bake size defaults to 1024 (supported sizes: 512/1024/2048). Physics wearables,
hover height, animesh and PBR overrides are not bake inputs; there is no expiry
reaper for abandoned bakes.

See [Server-side baking](feature/ssb-appearance/DESIGN-BRIEF-ssb-appearance.md)
for setup, behavior and rollout guidance (#207, #276).

## Hypergrid, security and outbound networking

### Trusted Hypergrid (opt-in)

- Ed25519 signing/verification is wired to gatekeeper `link_region` and
  `get_region` calls, with timestamp/nonce checks.
- MySQL and SQLite trust registries track grid keys, aliases, first contact and
  key changes. `hgtrust` commands let operators inspect, approve, block or forget
  grids and view the local fingerprint.
- Unknown/unsigned/unverifiable callers remain Open. Trust is never promoted
  automatically, and Open and Trusted callers retain identical access.
- Blocking requires both `[TrustedHypergrid] Enabled = true` and the separate
  `TrustedGridAuthentication` gatekeeper setting. Only a verified blocked key is
  refused on the wired calls.

**Scope:** this is attribution and revocation, not DRM or copy prevention.
UserAgent/HGFriends and HG asset/inventory signing are not wired; export-bit
enforcement is not implemented by this feature. Unsigned callers and a blocked
grid using a new key remain Open. Region processes do not initialize the
gatekeeper trust runtime.

See [Trusted Hypergrid](feature/trusted-hypergrid/DESIGN-trusted-hypergrid.md)
for configuration, commands and limitations (#200).

### Control-plane and federation hardening

- Privileged region agent/object creation, neighbour announcements and friendship
  control requests require trusted socket-peer addresses.
- Client-supplied `X-Forwarded-For` is not used for source trust, and requests
  carrying `X-SecondLife-Shard` are rejected on control-plane paths.
- Restricted sensitive profile operations, HG group writes, privileged IM
  dialogs and untrusted logout/return-home requests.
- Hardened foreign duplicate-presence handling and HG friendship deletion.
- Filtered caller-supplied HG destinations against private/reserved addresses.
- Disabled the OpenID connector in default grid templates, removed login-key
  logging and fixed map-request lock release on malformed input.
- Gloebit callbacks require random transaction callback keys; OAuth uses
  persisted one-shot state.

These changes are not governed by the Trusted Hypergrid feature flag. See
[Security hardening](security/Hardening.md) for deployment requirements and
remaining network-layer limitations (#210).

### Script and shared HTTP transports

- Script `llHTTPRequest` connects only to addresses approved by the outbound
  filter, rather than checking one DNS result and connecting to another.
- The outbound URL filter also covers XML-RPC and vector-render image fetches;
  HTTP MIME types are validated.
- Modernized legacy WebRequest transport paths while retaining the intended
  proxy, authentication, timeout and response-stream behavior.
- Script HTTPS follows operating-system TLS policy rather than forcing TLS
  1.0/1.1. HELO and welcome-message HTTPS fetches validate certificate chains and
  hostnames even when general certificate bypass settings are enabled.
- `[Startup] DnsTimeout` controls pooled connection lifetime in milliseconds,
  not process-wide DNS-cache TTL: the default is three minutes, `-1` disables
  expiry and `0` disables reuse.
- Remote console failures are reported to stderr; failed polling stops and
  requires reconnection, and failed commands are not automatically retried.

See [Security hardening](security/Hardening.md) and the transport/correctness
sections of [Warning cleanup](WarningCleanup.md) (#220, #234, #259, #262).

## Logging and diagnostics

Application/module logging now uses `Microsoft.Extensions.Logging.ILogger`.
The log4net package, `OpenSimAppender`, bootstrap classes and legacy log4net
configuration are removed. Custom modules using those interfaces must migrate.

- **Console:** `OpenSimConsoleLoggerProvider` and `OpenSimConsoleLogSink` route
  output without corrupting the interactive prompt/readline buffer.
  `set log level <level>` adjusts console verbosity at runtime.
- **Files:** Serilog daily rolling files with shared-file support. Level labels
  retain `INFO`, `WARN`, `ERROR`, etc., rather than abbreviated Serilog labels.
  The formatter writes a timestamp, level and rendered message; it does not
  automatically insert a category or `- [LoggerName]` prefix.
- **Stats:** an explicitly configured `[Startup] StatsLogFile` routes
  `special.StatsLogger` events to a separate rolling file and excludes them
  from the main application file. Without that setting, those events stay in
  the main file; the fallback stats pathname does not enable a separate sink.
- **Consumers:** resolved file paths are shared with log-reading/statistics
  code rather than assuming the old log filenames.

### File location configuration

The application log base path is selected in this order:

1. `[Startup] logfile`, when explicitly configured.
2. `LOGDIR/<serviceName>.log`, when `LOGDIR` is set.
3. `./<serviceName>.log`.

Service names include `OpenSim.Server.RegionServer`, `OpenSim.Server.GridServer`
and `OpenSim.Server.MoneyServer`. Serilog adds the date to daily rolling filenames;
a configured path is the rolling-file base path, not a promise of an undated file.
`LOGDIR` does **not** relocate stats logs automatically.

For example, include this in an INI file loaded by the server:

```ini
[Startup]
    logfile = /var/log/opensim/region.log
    StatsLogFile = /var/log/opensim/stats.log
```

Or set only the application log directory:

```bash
LOGDIR=/var/log/opensim dotnet OpenSim.Server.RegionServer.dll
```

Use the supported `--inifile` option to load an override INI. Do not use
`--logconfig` or assume arbitrary `--Startup:logfile=...` switches are accepted:
the executables validate their declared command-line options (#198, #298).

## Physics, meshing, scene objects and sitting

- Mesh caching distinguishes physical/non-physical requests and returns the hull
  list and bounding hull for the requested mesh, not another cached shape.
- Sculpt invert/mirror flags now match viewer construction.
- Physical-object mouse dragging uses move-to-target; rotation dragging uses
  look-at, and engines with rotation targets receive the correct target.
- Delinked physical parts inherit their linkset velocity.
- Keyframed motion tells the prim's physics actor while moving the object.
- Serialized vehicle records retain the two InWorldz vehicle types and six
  additional float parameters; object copies get independent vehicle records.
  Base vehicle setup clears flags.
- Out-of-bounds reports make the whole object non-physical; collision depth is
  no longer treated as collision speed.
- Fixed non-finite position/rotation handling, rubber/light friction,
  `llMoveToTarget` with zero tau, avatar mass reads without an actor, and shutdown
  with an avatar present.
- Corrected stand-up, Release Keys and detach behavior. Manual sits avoid
  scripted-only targets; zero-offset active sit targets are supported and their
  active state persists in region stores.

Notable changes: #215, #230, #251, #253, #279, #280, #283, #287-292, #295-297.

**Physics integration limit:** attachment constant force is carried through
avatar state and the new actor hook, but the bundled physics engines do not yet
apply `SetConstantForce`. Do not expect that hook alone to move an avatar; see
[Phlox differences and gaps](PhloxKnownDefects.md).

## Persistence, inventory and core reliability

- Persisted task inventory Experience IDs and explicit sit-target active state,
  with related database migrations.
- PostgreSQL fixes cover prim-store columns, inventory `MoveItem` quoting,
  `SetDataItem` parameters and estate ban-list saves that previously lost other
  estate lists.
- SQLite fixes cover NULL task-item descriptions, region extra settings,
  estate-region lookup and transactional estate-list saves.
- HG inventory export honors each item's Export permission bit and corrects the
  outbound-permission default.
- Muted senders' IMs are dropped; group invite/eject report their results, and
  region code can query a user's partner.
- Operators can block an owner's rezzing, duplication and object entry with
  `block owner`, `unblock owner` and `show blocked owners`.
  The console list is in memory and clears on restart; optionally use
  `[BlockedOwners] BlockEstateBanned = true` to apply estate bans.
- Parcel-for-sale classification checks the ForSale flag, not merely a positive
  sale price.
- Fixed texture transport to read actual JPEG 2000 layer boundaries. Dynamic
  textures use single-tile J2K and map tiles are sized to their region.
- Xfer downloads use 1000-byte pieces, matching the LL sender.
- Fixed short-read assumptions in stream handling and log-tail resource cleanup.
  Corrected diagnostic templates, list equality/hash consistency and
  readonly-reference call contracts.
- Null data stores and test scene-presence handling are made thread-safe.

Notable changes: #187, #189, #201, #222, #232, #235, #240, #241, #247, #249,
#252, #258, #262, #266, #273.

## Tests and engineering quality

- Migrated the repository's primary unit tests from NUnit to xUnit and updated
  test project/solution wiring.
- Added compiler/VM conformance tests, SLua proof tooling, engine-routing and
  persistence tests, bot/Experience regressions, AIS golden fixtures and malformed
  input tests, baking tests, Trusted Hypergrid tests, outbound transport tests,
  and scene/physics/store regressions.
- Fixed test projects that did not restore/build/run and corrected CoreModules
  test failures.
- Replaced blocking test operations with async/await where appropriate and
  resolved xUnit analyzer findings.
- Completed seven warning-cleanup batches. The recorded closeout reports
  non-incremental Release and Debug solution rebuilds with **0 warnings and
  0 errors on SDK 10.0.112**. This is the documented cleanup result, not a claim
  that every standalone/excluded project is warning-free or that a fresh full
  release validation has been performed while preparing these notes.

See [Warning cleanup](WarningCleanup.md) for scope, validation commands and
excluded-project backlog. [SLua overview](PhloxSLua.md) describes the proof runner
and the `phlox sluaproof` console self-test (#197, #227, #231, #259, #262,
#274, #275, #293).

## Known limitations and work not included

- **Phlox is not a drop-in semantic clone of YEngine or Second Life.** It has
  different limits, evaluation order, throttles and API gaps; YEngine's XMR
  language extensions are not supported. Review representative content before
  changing the default. See [Phlox differences and gaps](PhloxKnownDefects.md).
  Notable defaults include a fixed 128 KiB script memory limit, a 0.1 s timer
  floor and a 20-events-per-second listen-delivery cap (the latter two are
  configurable). LightShare `ls*`, module `mod*`, `llRemoteLoadScript` and some
  OSSL functions are absent, even though much of the OSSL surface is implemented.
- **Engine state is not translated.** Keep YEngine and its state files for old
  content. A changed bytecode identity can drop an in-progress Phlox event during
  restore, as described above.
- **Phlox/YEngine runtime consolidation is a discussion, not a delivered
  feature.** [Consolidation notes](PhloxYEngineConsolidation.md) capture an earlier
  analysis; their historical API counts are not the current parity inventory.
- **AIS, baking and Trusted Hypergrid have bounded scopes.** Use their operator
  guides/checklists rather than assuming complete LL viewer or federation parity.
- **Security hardening does not replace firewall/egress policy.** In particular,
  the HG destination checks have different connection/redirect limitations from
  the script HTTP address-pinning work.
- **Some supporting documents/configuration comments predate follow-up fixes.**
  In particular, older descriptions of unconditional default-engine selection,
  default-engine-only carried-state delivery and zero-offset sit-target loss are
  superseded by the routing/persistence fixes summarized here.

## Documentation map

| Topic | Reference |
|---|---|
| Previous modernization release | [1.0 notes](RELEASE_NOTES_1.0.md) |
| Build and publish | [Building](BUILDING.md) |
| Module development | [Plugin development](PLUGIN_DEVELOPMENT.md) |
| Existing hosted-service architecture | [Hosted-service architecture](HostedServiceArchitecture.md) |
| Phlox configuration, routing, storage and console | [Phlox setup](PhloxSetup.md) |
| SLua architecture and proof tooling | [Phlox/SLua](PhloxSLua.md) |
| Scripting differences, APIs and remaining gaps | [Phlox differences and gaps](PhloxKnownDefects.md) |
| Compiler fixes and recompile/state implications | [Compiler correctness](Phlox-compiler-correctness.md) |
| Experience deployment and management | [Experience setup](ExperienceSetup.md) |
| AIS configuration and protocol | [AIS v3 guide](feature/ais-v3/AIS-V3-SPEC.md) |
| AIS production rollout checks | [AIS live checklist](feature/ais-v3/A5-LIVE-CHECKLIST.md) |
| Server-side appearance baking | [Baking guide](feature/ssb-appearance/DESIGN-BRIEF-ssb-appearance.md) |
| Grid identity and trust registry | [Trusted Hypergrid](feature/trusted-hypergrid/DESIGN-trusted-hypergrid.md) |
| Mandatory hardening and compatibility changes | [Security hardening](security/Hardening.md) |
| Build-quality closeout and outstanding scope | [Warning cleanup](WarningCleanup.md) |

### Maintainer provenance

This draft was assembled from the local `v1.0..01f27f1f54` Git history, source
comparison, configuration defaults, dependency manifests and documentation.
The PR numbers above identify changes in this repository; they are navigation
aids, not an exhaustive commit list. Historical implementation plans are not
treated as proof that a feature shipped. Publication should add the final tag,
release date and any changes merged after the coverage commit.
