# Phlox: differences, extensions and known gaps

This document is for region operators and scripters who run scripts on Phlox, the
bytecode script engine that ships beside YEngine. It covers four things:

1. where Phlox behaves differently from Second Life (SL) in ways a scripter will notice;
2. where Phlox behaves differently from YEngine;
3. what Phlox adds beyond SL: the `iw*` and `bot*` functions, extra constants and events,
   and the syntax it accepts;
4. known gaps: functions, rules and events that compile but do nothing, or do only part
   of their job.

Setting Phlox up, its configuration keys and its console commands are in
[PhloxSetup.md](PhloxSetup.md). SLua (Luau-style scripts on the same engine) is described
in [PhloxSLua.md](PhloxSLua.md).

SL behaviour is cited from the SL wiki (`https://wiki.secondlife.com/wiki/<Page>`).

---

## 1. Differences from Second Life

### Timers
`llSetTimerEvent` raises any positive interval below 0.1 s to 0.1 s. The floor is
`[InWorldz.Phlox] MinTimerInterval`, and 0 turns it off. SL documents no minimum
([LlSetTimerEvent](https://wiki.secondlife.com/wiki/LlSetTimerEvent)). YEngine uses the same
0.1 s floor by default.

### Script memory
- A Phlox script may use up to 128 KB. Going over stops the script with
  `Script error: Script <asset> stopped: Out of memory` on DEBUG_CHANNEL. SL's Mono limit is
  64 KB ([LlSetMemoryLimit](https://wiki.secondlife.com/wiki/LlSetMemoryLimit)).
- The memory functions do not report real usage:
  - `llGetFreeMemory` always returns 65536.
  - `llGetUsedMemory` returns an estimate, not the VM's counter.
  - `llGetMemoryLimit` always returns 131072.
  - `llSetMemoryLimit` returns TRUE only for exactly 131072, and FALSE for every other value.
    The limit never changes.
- `llScriptProfiler` with `PROFILE_SCRIPT_MEMORY`, followed by `llGetSPMaxMemory`, does report
  the real peak.
- `llGetObjectDetails` returns 0 for `OBJECT_SCRIPT_MEMORY` and `OBJECT_SCRIPT_TIME`
  ([LlGetObjectDetails](https://wiki.secondlife.com/wiki/LlGetObjectDetails)).

### Run-time errors
- Errors are shouted on DEBUG_CHANNEL with the prefix `Script error: `, as in SL
  ([DEBUG_CHANNEL](https://wiki.secondlife.com/wiki/DEBUG_CHANNEL)).
- A script killed by an error (out of memory, a VM fault) has its Running flag cleared. It
  stays stopped across a region restart until it is reset or set running again.
- With `ChatThrottle` on (the default), every shouted script error also pauses the script for
  15 ms. See "Anti-abuse slowdowns" below.

### Listens
- A script may hold 65 listens and a region 1000. The limits come from `[LL-Functions]`
  `max_listens_per_script` and `max_listens_per_region`, the same keys YEngine reads.
- Past the limit, `llListen` returns -1 and raises **no** error. SL raises a run-time
  "Too Many Listens" error ([LlListen](https://wiki.secondlife.com/wiki/LlListen)).
- An `llListen` identical to one the script already holds active returns the existing handle.
- A prim never hears its own chat.
- `llRegionSayTo` refuses `DEBUG_CHANNEL` with the error "Cannot use llRegionSayTo() on
  DEBUG_CHANNEL.". Only its target hears it: the target prim's listens, or, for an avatar,
  the listens in that avatar's attachments (the avatar itself sees it on channel 0).

### Anti-abuse slowdowns
Phlox keeps a set of slowdowns from its InWorldz/Halcyon heritage. SL has none of them in
this form. Each is on by default and can be switched off under `[InWorldz.Phlox]` (see
[PhloxSetup.md](PhloxSetup.md)):

| Key | Effect |
|---|---|
| `ChatThrottle` | 15 ms pause after `llSay`, `llShout`, `llWhisper`, `llRegionSay`, `llRegionSayTo`, `llOwnerSay`, and after every shouted script error |
| `ResetThrottle` | more than 5 resets of one script in one second puts it to sleep for 5 s |
| `LinkMessageThrottle` | `llMessageLinked` pauses 50 ms when a receiving script's event queue is nearly full |
| `PhysicsThrottle` | physics setters pause for the average physics frame time when that is over 30 ms |
| `NotecardThrottle` | short delays on notecard reads |
| `BotThrottle` | 15 ms pause after bot chat, typing, sit, stand and touch calls |
| `FormatStringThrottle` | 100 ms pause after `iwFormatString` |
| `HttpInFlightThrottle` | at most 10 `llHTTPRequest` calls in flight per object and 200 per region; a refused call returns `NULL_KEY` after 80 ms |

The function delays SL documents (for example 0.2 s for `llSetPos`, 2 s for
`llInstantMessage`, 20 s for `llEmail`) are applied as fixed times.

### HTTP
- The metadata list in `http_response` is always empty: `HTTP_BODY_TRUNCATED` is never sent
  ([Http_response](https://wiki.secondlife.com/wiki/Http_response)).
- The simulator's `X-SecondLife-*` headers are added after the script's own headers, so a
  script cannot forge them. A custom header beginning with `x-secondlife` is dropped.
- `llHTTPRequest` and `llSendRemoteData` obey the same outbound URL filter as YEngine
  (`[Network] OutboundDisallowForUserScripts`).

### Values that differ from SL
- `llHash` uses the DJB2 hash. SL uses SDBM
  ([LlHash](https://wiki.secondlife.com/wiki/LlHash)), so the values differ from SL's (and
  from YEngine's, which follows SL).
- `llGetEnv` ([LlGetEnv](https://wiki.secondlife.com/wiki/LlGetEnv)):
  - `frame_number` returns the simulator's frames per second, not a frame number;
  - `region_start_time` is always `"0"`;
  - `sim_version` is always `"0.9.3.0"`;
  - `dynamic_pathfinding` is always `"disabled"`;
  - `region_cpu_ratio` is always `"1"` and `region_idle` always `"0"`.
  - These keys return `""`: `region_up_time`, `whisper_range`, `chat_range`, `shout_range`,
    `agent_limit_max`, `agent_reserved`, `agent_unreserved`, `region_rating`, and the
    damage-system keys.
- `llGetTimeOfDay` is the UTC time of day modulo 4 hours. It does not follow the region's
  own day cycle.

### Compiler
These are facts about Phlox's compiler. Where SL's rule is known, it is cited.
- A string literal may contain only the escapes `\t`, `\n`, `\"` and `\\`. Any other escape
  is a compile error. SL compiles it and drops the backslash, so `"\a"` means `"a"`
  ([String](https://wiki.secondlife.com/wiki/String)).
- A statement must be an assignment, a function call, `++`/`--` on a variable, a
  declaration, a control statement, a state change, a jump, a label or a block. An
  expression on its own, such as `x;` or `a + b;`, does not compile.
- The three parts of a `for` header each take one expression; a comma list such as
  `for (i = 0, j = 0; ...)` does not compile.
- Hexadecimal literals must start with a lowercase `0x`. Floats take no `f` suffix.
- Nesting limits: expressions 1000 deep, blocks 500 deep, `else if` chains 2500 long,
  chained assignments 64 long. Past a limit the compile fails with a message naming it.
- A user function defined twice is an error ("Symbol '...' already defined"), as in SL.

---

## 2. Differences from YEngine

### Which engine runs a script
- A script runs on the region's default engine (`[Startup] DefaultScriptEngine`) unless
  its first line names another one. Phlox reads that line exactly as YEngine does:
  - `//YEngine:` keeps a script on YEngine;
  - `//InWorldz.Phlox:` puts it on Phlox;
  - `//InWorldz.Phlox:slua` puts it on Phlox and compiles the rest as SLua. Another language
    name after `//InWorldz.Phlox:` is a compile error in the editor.
- The header must be the very first characters of the script. The name is case-sensitive.
  A header naming an engine that is not loaded is ignored, and the script goes to the default
  engine.
- Details are in [PhloxSetup.md](PhloxSetup.md).

### Saved state
- **Where it is kept.** Phlox saves each script's run-time state (globals, current state,
  queued events, timers, listens) in its own SQLite database, keyed by the script item and
  checked against the script asset. YEngine writes a `.state` file per script. The engines
  never read each other's state: a script that changes engine starts fresh, with
  `state_entry`.
- **Handed-off scripts keep their Phlox state.** When a script moves to another engine,
  Phlox keeps its saved state, as YEngine keeps its own for a script it declines. If the
  script comes back to Phlox unchanged, it resumes from that state. Anything it did on the
  other engine is not carried over.
- **What survives.** A Phlox script resumes where it was after a region or simulator restart.
  It starts fresh when its source changes, and when it is reset.
- **State is not carried inside objects.** YEngine embeds script state in the serialized
  object; Phlox does not (its `GetXMLState` returns nothing). Phlox scripts therefore start
  fresh after:
  - an OAR or IAR export and load;
  - taking an object into inventory and rezzing it in another simulator process;
  - a region crossing or a teleport with attachments to another simulator process, including
    Hypergrid.

  Phlox finds a script's state only in the database of the simulator process that saved it.

### Language
- YEngine's XMR extensions are not available: `switch`, `break`, `continue`, `constant`,
  `try`/`catch`/`finally`/`throw`, arrays, `foreach`, classes and the `xmr*` functions.
- YEngine accepts several user functions with the same name and different parameters.
  Phlox rejects them, as SL does.
- Phlox compiles SLua scripts; YEngine does not. Where YEngine is the default, an SLua script
  needs `//InWorldz.Phlox:slua` as its first line to run on Phlox.

### Function sets
- **YEngine has, Phlox lacks:**
  - the LightShare `ls*` functions;
  - the `mod*` functions (`modInvoke*`, `modSendCommand`);
  - `llRemoteLoadScript`;
  - ten OSSL functions: `osGiveLinkInventory`, `osGiveLinkInventoryList`, `osMakeNotecard`,
    `osMessageAttachments`, `osNpcLookAt`, `osNpcSayTo`, `osReplaceAgentEnvironment`,
    `osReplaceParcelEnvironment`, `osReplaceRegionEnvironment`, `osResetEnvironment`.
- **Spelled differently:** YEngine's `osTemperature2sRBG` is `osTemperature2sRGB` on Phlox.
- **Phlox has, YEngine lacks:**
  - the `iw*` and `bot*` families (section 3);
  - some newer SL functions, for example `llSetAgentRot`, `llSortListStrided` and
    `llTransferOwnership`.
- Phlox declares 258 OSSL functions. They honour the `[OSSL]` keys (`AllowOSFunctions`,
  `OSFunctionThreatLevel`, `PermissionErrorToOwner`, `Allow_<function>`,
  `Creators_<function>`) with the same meanings as YEngine.

### Configuration differences that affect scripts
- YEngine scales its function delays and distance limits by `ScriptDelayFactor` and
  `ScriptDistanceLimitFactor`. Phlox does not read those keys; its delays are fixed.
- When there is no `[OSSL]` section, YEngine reads the OSSL keys from `[YEngine]` and Phlox
  reads them from `[InWorldz.Phlox]`.
- Phlox reads `AllowGodFunctions` and `AutomaticLinkPermission` from `[YEngine]`, so one
  value governs both engines. An `AllowGodFunctions` in `[InWorldz.Phlox]` overrides it for
  Phlox.

### Memory reporting
YEngine reports a 64 KB limit and refuses `llSetMemoryLimit`; Phlox reports 128 KB
(section 1).

### Chat
YEngine pauses a script for 2 s after a burst of channel-0 `llSay`/`llShout` calls. Phlox
pauses 15 ms after every chat call instead (`ChatThrottle`).

### Scripts on both engines in one object
- Chat crosses between the engines in both directions.
- An `http_response` is delivered to the scripts in the prim whichever engine they run on.
- Events a Phlox script raises reach every script SL names, YEngine scripts included:
  - a `dataserver` answer, `object_rez` and `email`: every script in the calling script's prim;
  - `osMessageObject`'s `dataserver`: every script in the target prim;
  - `llMessageLinked`: every script in the targeted prims;
  - `linkset_data`: every script in the linkset;
  - `botMessageLinked`: every script in the bot's attachments.
- Events a YEngine script raises follow YEngine's own delivery.

### Other behaviour
- **Compiling.** Phlox compiles on its own thread, so saving a script does not pause the
  others. Compile errors appear in the viewer's script editor.
- **No Scripts parcels.** On a parcel where scripts are not allowed, Phlox scripts pause, and
  resume when the parcel rules allow them again.
- **`llCreateLink` and the region's edit rules.** Phlox also asks the region whether the
  object's owner may edit both objects (the same check as editing them by hand). If not,
  nothing is linked, no error is shown and the call returns without its delay. YEngine
  does not make this check.

---

## 3. Extensions

### Language
- `<<=` and `>>=` (integer only).
- Built-in functions may be overloaded by argument type. This is how the three forms of
  `osTeleportAgent` and `osTeleportOwner`, and the typed forms of `osApproxEquals`,
  `osSetPenColor` and others, coexist. User functions cannot be overloaded.

### Events
`bot_update(string, integer, list)` is the only event beyond SL's set. The bot manager
raises it for the `bot*` functions.

### `iw*` functions (82)
| Area | Functions |
|---|---|
| Avatars and names | `iwAvatarName2Key` (answers at once; a blank last name means "Resident"), `iwGetAgentData` (an immediate `llRequestAgentData`), `iwGetAgentList` (`llGetAgentList` with a bounding box), `iwGetAppearanceParam`, `iwAvatarOnLink`, `iwIsPlusUser`, `iwDetectedBot` |
| Groups and land | `iwActiveGroup`, `iwGroupInvite`, `iwGroupEject`, `iwHasParcelPowers` (with the `IW_POWER_*` constants), `iwSetGround`, `iwWind`, `iwSetWind`, `iwGroundSurfaceNormal` |
| Teleport | `iwTeleportAgent` |
| Rezzing and objects | `iwRezObject`, `iwRezAtRoot` (return the new object's key), `iwRezAt`, `iwRezPrim`, `iwCheckRezError`, `iwGetWorldBoundingBox`, `iwGetObjectMassMKS`, `iwGetAngularVelocity`, `iwGetLastOwner`, `iwLinkTargetOmega`, `iwStandTarget`, `iwLinkStandTarget`, `iwStartLinkAnimation`, `iwStopLinkAnimation`, `iwSearchLinksByName`, `iwSearchLinksByDesc` |
| Inventory of other links | `iwGetLinkInventoryNumber`, `iwGetLinkInventoryType`, `iwGetLinkInventoryPermMask`, `iwGetLinkInventoryName`, `iwGetLinkInventoryKey`, `iwGetLinkInventoryCreator`, `iwGetLinkInventoryDesc`, `iwGetLinkInventoryLastOwner`, `iwRemoveLinkInventory`, `iwGiveLinkInventory`, `iwGiveLinkInventoryList`, `iwDeliverInventory`, `iwDeliverInventoryList` (return `IW_DELIVER_*` codes), `iwSearchInventory`, `iwSearchLinkInventory`, `iwRemoteLoadScriptPin` (returns `IW_REMOTELOAD_*`) |
| Notecards and assets | `iwMakeNotecard` (writes a notecard of up to 64 KB), `iwGetNotecardSegment`, `iwGetLinkNumberOfNotecardLines`, `iwGetLinkNotecardLine`, `iwGetLinkNotecardSegment`, `iwRequestAnimationData` (these answer in `dataserver`) |
| Strings | `iwSubStringIndex`, `iwMatchString` (with `IW_MATCH_*`), `iwReplaceString`, `iwFormatString`, `iwStringCodec`, `iwReverseString`, `iwChar2Int`, `iwInt2Char`, `iwSHA256String`, `iwParseString2List`, `iwVerifyType`, `iwFormatTime`, `iwGetLocalTime`, `iwGetLocalTimeOffset` |
| Lists | `iwMatchList`, `iwListIncludesElements`, `iwReverseList`, `iwListRemoveElements`, `iwListRemoveDuplicates` |
| Numbers, colour, misc | `iwClampInt`, `iwClampFloat`, `iwIntRand`, `iwIntRandRange`, `iwFrandRange`, `iwColorConvert` (RGB/HSL/HSV), `iwNameToColor`, `iwValidateURL`, `iwGiveMoney` |

Section 4 lists the `iw*` functions that do nothing or only part of their job.

### `bot*` functions (59)
Scripted bots (NPCs) through the region's bot manager:
- creating and removing bots, and tagging them;
- movement: follow, navigation points, wander, pause, resume, stop, speed, teleport, rotation;
- chat, instant messages and typing;
- sit, stand and touch;
- animations, outfits and profiles;
- bot sensors, `botListen` and `botMessageLinked`;
- giving inventory;
- collision and navigation event registration;
- persistence.

They need the region's NPC support (`[NPC] Enabled`, on by default).

### Constants
- `IW_PRIM_ALPHA` and `IW_PRIM_PROJECTOR*` are extra prim-params rules.
- `IW_OBJECT_SCRIPT_MEMORY_USED` is an extra `llGetObjectDetails` code.
- `IW_POWER_*` (group powers), `IW_MATCH_*`, `IW_COLORSPACE_*`, `IW_DELIVER_*`,
  `IW_REMOTELOAD_*`, `IW_REZ_*` and `IWERR_*` serve the `iw*` functions.
- `BOT_*` serves the `bot*` functions.
- The OSSL constants (`OS_NPC_*`, `OS_LISTEN_REGEX_*`, `WL_*` and others) are the same as
  YEngine's.

### Experience key-value store
- SL's forms (`llCreateKeyValue`, `llReadKeyValue`, `llUpdateKeyValue`, `llDeleteKeyValue`,
  `llKeyCountKeyValue`, `llKeysKeyValue`, `llDataSizeKeyValue`) return a request key and
  answer in `dataserver`, as in SL.
- Phlox also has `llCreateKeyValueSL`, `llReadKeyValueSL`, `llUpdateKeyValueSL` and
  `llClearKeyValue`:
  - they answer at once;
  - a script with no Experience uses its owner's key as the store.

### Linkset data
All 13 `llLinksetData*` functions and the `linkset_data` event are implemented.

---

## 4. Known gaps

The items below compile. They either do nothing or do only part of what SL (or their own
name) promises.

### Functions that only raise an error
- `llGodLikeRezObject`, `llCollisionSprite`, `llPointAt`, `llStopPointAt` and
  `botChangeOwner` shout "Command not implemented".
- `llTakeCamera`, `llReleaseCamera`, `llSound`, `llMakeExplosion`, `llMakeFountain`,
  `llMakeSmoke` and `llMakeFire` shout "Command deprecated".
- `llParcelMediaCommandList` and `llParcelMediaQuery` reject the parameters they do not
  handle.

### Functions that return a fixed value
| Function | Returns |
|---|---|
| `llGetAccel`, `llGetOmega`, `llGetTorque`, `iwGetAngularVelocity` | `ZERO_VECTOR` |
| `iwGetLocalTime`, `iwGetLocalTimeOffset` | 0 |
| `llGetEnergy` | 1.0 |
| `llCloud` | 0 |
| `llGetCameraAspect`, `llGetCameraFOV` | 1.7778 and 1.0472 (the viewer does not send them) |
| `llGetAgentLanguage` | `""` |
| `llWorldPosToHUD` | `<0.5, 0.5, 0>` |
| `iwIsPlusUser` | 0 |
| `iwDetectedBot` | the string `"0"`, not a key |
| `iwCheckRezError` | 0 |
| `iwRezPrim` | `NULL_KEY`; rezzes nothing |

`llSetPrimURL`, `llRefreshPrimURL`, `llCloseFloater` and `iwSetWind` do nothing.

### Functions that act only in part
- `llRezObjectWithParams`:
  - It returns the new object's key, the one `object_rez` reports, and `""` on failure, as
    SL's LSL does ([LlRezObjectWithParams](https://wiki.secondlife.com/wiki/LlRezObjectWithParams)).
    SLua's `ll.RezObjectWithParams` also returns `""` on failure, where SL's Lua returns
    `NULL_KEY`. YEngine returns `NULL_KEY` on failure.
  - `REZ_FLAGS` and `REZ_DAMAGE` are ignored.
  - A `REZ_*` rule it does not handle is skipped without consuming its value, so the rules
    after it are misread.
- `llDerezObject` refuses `DEREZ_TO_INVENTORY` (returns 0). `DEREZ_DIE` and
  `DEREZ_MAKE_TEMP` work.
- `llTargetedEmail`, in its four-argument form, sends only to external addresses (target type
  2).
- `llSetEnvironment` applies only `ENV_DAY_LENGTH` and `ENV_DAY_OFFSET`. Other parameters
  raise "not yet supported for setting".
- `llReplaceEnvironment` applies the day length and offset but never loads the named
  environment asset.
- `llSetAgentEnvironment` and `llReplaceAgentEnvironment` change nothing, but return 0
  (success).
- `llGetEnvironment` returns fixed defaults for most sky parameters.
- `llTransferLindenDollars` and `iwGiveMoney` move no money. They answer with a `dataserver`
  event `"LINDENDOLLAR_INSUFFICIENTFUNDS"` rather than `transaction_result`.
- `llGiveMoney` returns 0.
- `llGetVehicleFlags` returns the vehicle type, not its flags.
- `llGetAgentInfo` never sets `AGENT_TYPING`, `AGENT_BUSY`, `AGENT_CROUCHING` or
  `AGENT_AUTOPILOT`.
- `llGetParcelDetails` / `osGetParcelDetails`:
  - name, description, owner, group, area and id are real;
  - `PARCEL_DETAILS_SEE_AVATARS` is always 1;
  - every later key (prim capacity, prims used, landing point, flags and others) returns 0.
- `llGetObjectDetails`:
  - script counts, script memory, script time and the cost codes return 0;
  - any `OBJECT_*` code not handled returns `""`. That includes `OBJECT_LAST_OWNER_ID`,
    `OBJECT_PRIM_COUNT`, `OBJECT_TOTAL_INVENTORY_COUNT`, `OBJECT_REZZER_KEY`,
    `OBJECT_CREATION_TIME`, `OBJECT_SIT_COUNT`, `OBJECT_TEXT`, `OBJECT_SCALE` and others.
- `llHash`: see section 1.
- `iwReverseString` reverses UTF-16 code units, as Halcyon does: a character outside the
  Basic Multilingual Plane (most emoji) comes back as two broken halves, and a combining
  accent moves in front of the letter it belonged to.
- `iwMatchList` supports only `IW_MATCH_HEAD` and `IW_MATCH_TAIL`. The regex and count
  match types shout "not implemented" and return 0.
- `iwStringCodec` validation (`VALIDATE`) of the base4k codec always answers
  `"INVALID CODEC"`.
- `iwGetWorldBoundingBox` returns the same as `llGetBoundingBox`.
- `iwStandTarget` and `iwLinkStandTarget` set the stand offset; the rotation is ignored.

### Prim-params rules
- `PRIM_HEALTH` and the damage type in `PRIM_DAMAGE` are accepted and dropped. Reading them
  back gives 0.0 and `DAMAGE_TYPE_GENERIC`.
- `PRIM_PHYSICS_MATERIAL` can be set, but reading it returns nothing.
- `PRIM_SIT_FLAGS`: `SIT_FLAG_NO_COLLIDE` and `SIT_FLAG_NO_DAMAGE` are stored for read-back
  only.
- For seated avatars, only position and rotation rules apply.

### Events
- `transaction_result` and `game_control` compile but are never raised.
- `money` is raised only when the region has a money module.

### Engine
Script state is not exported with objects; see "Saved state" in section 2.
