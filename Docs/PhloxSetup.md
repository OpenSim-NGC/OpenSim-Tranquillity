# Setting up Phlox

Phlox is a script engine that runs beside YEngine in the same region server. It compiles
LSL, OSSL and SLua scripts to bytecode and runs them on a virtual machine whose state can
be saved at any point, so a script resumes where it was after a restart.

This guide is for operators. It covers:
- turning Phlox on, making it the default engine, and keeping YEngine as the backup;
- the first-line engine header, and what changing the default does to existing scripts;
- every `[InWorldz.Phlox]` setting, and the other sections Phlox reads;
- where Phlox keeps its files, and its console commands.

How Phlox differs from SL and from YEngine is in
[PhloxKnownDefects.md](PhloxKnownDefects.md).

## Enabling Phlox and choosing the default engine

Two settings decide which engine runs a script:

```ini
[Startup]
    DefaultScriptEngine = "InWorldz.Phlox"   ; or "YEngine"

[InWorldz.Phlox]
    Enabled = true

[YEngine]
    Enabled = true
```

- **`[InWorldz.Phlox] Enabled`** loads Phlox. When the section is missing, or `Enabled` is
  false, Phlox does nothing at all.
- **`[Startup] DefaultScriptEngine`** names the engine that runs every script without an
  engine header. The shipped `OpenSimDefaults.ini` sets it to `"InWorldz.Phlox"`, with
  both engines enabled. The code default, used only when no ini file sets it, is
  `"YEngine"`.
- **Keep YEngine enabled.** With Phlox the default and YEngine still loaded, YEngine is the
  backup: any script whose first line is `//YEngine:` stays on YEngine. With Phlox loaded but
  YEngine the default, Phlox runs only scripts whose first line is `//InWorldz.Phlox:`.
- **Keep the default engine enabled.** YEngine takes a script without a header only when
  YEngine is itself the default. If `DefaultScriptEngine` names Phlox, Phlox must be
  enabled, or no engine takes those scripts.

The engine name is case-sensitive: `InWorldz.Phlox` and `YEngine`, exactly.

## The first-line engine header

A script can name its engine on its first line. Phlox reads this header exactly as YEngine
does:

```lsl
//YEngine:
default { state_entry() { llOwnerSay("I run on YEngine"); } }
```

- **Form.** The header is `//` followed by the engine name and a colon. It must be the very
  first characters of the script, with no blank line or space before it, and the first line
  must end with a line break.
- **Names.** `//YEngine:` keeps a script on YEngine; `//InWorldz.Phlox:` puts it on Phlox.
- **Unknown names.** A header naming an engine that is not loaded in the region is ignored,
  and the script runs on the default engine.
- **The language part.** Text after the colon is a language name. Both engines read it the
  same way: blanks around it are ignored, case does not matter, and a single character
  written directly after the colon is ignored.
  - Nothing, or `lsl`, means LSL, on either engine.
  - `//InWorldz.Phlox:slua` puts the script on Phlox and compiles the rest as SLua (see below).
  - Any other text after `//InWorldz.Phlox:` is a compile error on line 1, shown in the
    script editor. The script does not run on either engine.
  - YEngine does not run a script whose `//YEngine:` header names a language other than
    LSL, and reports no error.

An LSL header is an ordinary comment to the compiler, so the same script compiles on either
engine.

## What changing the default engine does to existing scripts

- **Scripts without a header move.** Changing `DefaultScriptEngine` moves every such script
  to the new default engine the next time it starts: at region start, on rez, or when it is
  saved.
- **They start fresh.** The engines do not share saved state, so a script that changes
  engine starts from `state_entry` with its globals at their initial values. This includes
  timers, listens and the current state.
- **Phlox keeps its saved state.** When Phlox hands a script to another engine, it keeps its
  own saved state for that script. If you switch back, a script that comes back to Phlox
  unchanged resumes where it left off under Phlox. What happened under the other engine is
  not carried over. A script that was edited in the meantime starts fresh.
- **Headed scripts stay.** Scripts whose first line names a loaded engine keep that engine
  and their state.

Before switching a region that runs scripts which must keep their state (counters, vendors,
door states held in globals), either add `//YEngine:` as their first line or accept that they
start fresh on the other engine.

## Settings in `[InWorldz.Phlox]`

This is every key the engine reads from the section. All of them are optional.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Loads the engine. Without it Phlox does nothing. |
| `MinTimerInterval` | `0.1` | Floor in seconds for `llSetTimerEvent`: a positive interval below it is raised to it. 0 turns the floor off; a negative value counts as 0. |
| `AllowGodFunctions` | `[YEngine] AllowGodFunctions`, else `false` | Allows `llSetInventoryPermMask` and `llSetObjectPermMask`. |
| `ResetThrottle` | `true` | Anti-abuse: more than 5 resets of one script in one second puts it to sleep for 5 s. |
| `ChatThrottle` | `true` | Anti-abuse: 15 ms pause after `llSay`, `llShout`, `llWhisper`, `llRegionSay`, `llRegionSayTo`, `llOwnerSay`, and after every shouted script error. |
| `BotThrottle` | `true` | Anti-abuse: 15 ms pause after bot chat, typing, sit, stand and touch calls. |
| `PhysicsThrottle` | `true` | Anti-abuse: physics setters pause for the average physics frame time when that is over 30 ms. |
| `LinkMessageThrottle` | `true` | Anti-abuse: `llMessageLinked` pauses 50 ms when a receiving script's event queue is nearly full. |
| `NotecardThrottle` | `true` | Anti-abuse: short delays on notecard reads. |
| `NotecardCache` | `true` | Keeps parsed notecards in memory for notecard reads. |
| `NotecardLineReadCharsMax` | `[YEngine] NotecardLineReadCharsMax`, else `1024` | Most bytes (UTF-8, not characters) a notecard line read returns, as SL's 1024-byte limit; longer lines are cut without splitting a character. 0 or below means 1024; the most is 65535. |
| `FormatStringThrottle` | `true` | Anti-abuse: 100 ms pause after `iwFormatString`. |
| `HttpInFlightThrottle` | `true` | Anti-abuse: at most 10 `llHTTPRequest` calls in flight per object and 200 per region. A refused call returns `NULL_KEY` after 80 ms. |
| `MaxListenEventsPerSecond` | `20` | Listen events one script may receive per second; the rest of that second's are dropped, with one log line per script per second. `0` or below: no limit (as YEngine and Halcyon). |
| `ServiceCallDeferral` | `auto` | How script calls that may wait on a grid service run. `auto`: on the scheduler when the answer is local or cached, otherwise on worker threads. `always`: every such call on worker threads. `never`: every call on the scheduler. Any other value means `auto`. |
| `ServiceCallTimeoutMs` | `35000` | Deadline in milliseconds for a call running on a worker thread (minimum 1). |
| `ServiceCallThreads` | `4` | Worker threads per region for those calls (minimum 1). |
| `SchedulerThreadPriority` | `Lowest` | Priority of the one thread that runs every script in the region: `Lowest`, `BelowNormal`, `Normal`, `AboveNormal` or `Highest` (case does not matter). Any other value logs a warning at start and means `Lowest`. A higher priority keeps scripts responsive when the machine is busy, at the cost of the rest of the simulator (and of other programs on the host), which gets the processor less often. No value is right for every host: it depends on how busy the machine is and what else runs on it. The start-up log line `SchedulerThreadPriority = ...` shows the value in use. |
| `SensorMaxRange` | `96.0` | Largest range in metres a sensor may use. |
| `SensorMaxResults` | `16` | Most objects or avatars one sensor reports. |
| `StateRowMaxAgeDays` | `0` | Deletes saved-state rows not saved or loaded for this many days whose scripts are not loaded in the simulator; checked every 6 hours, first 1 hour after start. `0`: never (the default). |

Setting any anti-abuse switch to `false` removes that slowdown and nothing else.

When there is no `[OSSL]` section, Phlox also reads the OSSL keys below from
`[InWorldz.Phlox]`.

## Other sections Phlox reads

### `[OSSL]`
Phlox's OSSL functions honour the same keys as YEngine, with the same meanings:
- `AllowOSFunctions` (default `true`);
- `OSFunctionThreatLevel` (default `VeryLow`);
- `PermissionErrorToOwner` (default `false`);
- `Allow_<function>` and `Creators_<function>`.

An existing `[OSSL]` section therefore applies to both engines. Only the fallback differs: without `[OSSL]`, YEngine reads these keys from
`[YEngine]` and Phlox from `[InWorldz.Phlox]`.

### `[NPC]`
- The `osNpc*` and `bot*` functions run through the region's bot manager, which is built on
  the core NPC module.
- Both need `[NPC] Enabled = true`: the default once the `[NPC]` section exists.
- The other `[NPC]` settings are read by the core NPC module.
- Without NPC support, `osNpc*` calls report that the NPC module is not enabled.

### `[YEngine]`
- `AllowGodFunctions` is the default for Phlox's own key of that name.
- `AutomaticLinkPermission` (default `false`) is read only from `[YEngine]`. When true,
  `llCreateLink` and `llBreakLink` need no `PERMISSION_CHANGE_LINKS`, on both engines.

### `[LL-Functions]`
- `max_listens_per_script` (65) and `max_listens_per_region` (1000) are the listen limits,
  read as YEngine reads them.
- A value below 1 means no limit.

### `[Chat]`
`whisper_distance`, `say_distance` and `shout_distance` (10, 20 and 100 m) set how far
script chat travels, as for every other listener.

### `[Network]`: the outbound URL filter
- `llHTTPRequest` and `llSendRemoteData` on Phlox pass through the same filter as
  YEngine's `llHTTPRequest`, configured by `OutboundDisallowForUserScripts` and
  `OutboundDisallowForUserScriptsExcept`.
- A refused URL raises a script error, and the call returns without sending.
- `shard` (default `OpenSim`) is the value of the `X-SecondLife-Shard` header.

## Where Phlox keeps its files

Both paths are relative to the region server's working directory, and neither is
configurable. `[YEngine] ScriptEnginesPath` does not move them.

- **Saved script state:** `ScriptEngines/Phlox/state/script_state.db`, one SQLite database
  shared by every region in the process.
  - Each row is keyed by the script item and checked against the script asset. A row for an
    older version of the script is discarded.
  - Changed state is written every 2.5 s, and when a script unloads.
  - A reset deletes the script's row.
- **Bytecode cache:** `ScriptEngines/Phlox/bytecode/`, one `.plx` file per script asset.
  - Files are grouped in sub-folders named by the first two characters of the asset id.
  - A `.schema_version` file records the cache format.

**After an upgrade that changes the cache format:**
- At the first start, Phlox finds the older stamp, deletes every cached `.plx` file, writes
  the new stamp, and recompiles each script as it loads. Expect that start to take longer.
- Saved state is kept. A script resumes with its globals, state, timers and queued events.
- A script that was saved in the middle of an event resumes idle, without finishing that
  event.

A cache file that cannot be read is deleted, and its script recompiles. Phlox needs the
native SQLite library that ships with the region server.

## Console commands

| Command | What it does |
|---|---|
| `phlox status <script-item-uuid \| object-name>` | Read-only report on a script: run state, flags, queued events, LSL state, timer, the prim's event mask and the Running flag. |
| `phlox suspend <script-item-uuid \| object-name>` | Pauses the script(s) without stopping them: timers, listens and state are kept. Not saved: a restart clears it. Does not change the Running flag. |
| `phlox resume <script-item-uuid \| object-name>` | Resumes script(s) paused with `phlox suspend`. Events that arrived meanwhile are delivered. |
| `phlox sluaproof` | Runs an offline self-test of the SLua back end. It touches no region. |

Object names match without regard to case. The commands act in the region selected with
`change region`, or in every region when none is selected.

## SLua

- **What counts as SLua.** Phlox compiles a script as SLua when its text, after any leading
  white space, begins with `--`. The usual first line is `--!slua`. It also compiles a script
  as SLua when its first line is `//InWorldz.Phlox:slua`.
- **Where it runs.** Only Phlox knows SLua. Without a header, an SLua script goes to the
  default engine, so it runs only where Phlox is the default. Where YEngine is the default,
  YEngine receives it as LSL and it fails to compile.
- **The `//InWorldz.Phlox:slua` header.** Make this the first line, and the script runs on
  Phlox as SLua in any region where Phlox is loaded, whatever the default engine:

  ```lua
  //InWorldz.Phlox:slua
  ll.Say(0, "Hello from SLua")
  ```

  - The header is read like any engine header. The engine name `InWorldz.Phlox` is exact
    and case-sensitive. The language part may be written in any case (`slua`, `SLua`).
  - Phlox blanks the header line before compiling. Line numbers in error messages still
    match the script as written.
  - In a region where Phlox is not loaded, the header names no loaded engine, so the
    script goes to the default engine. YEngine then does not run it, because the language
    is not LSL.

More on SLua is in [PhloxSLua.md](PhloxSLua.md).
