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
  `Script error: Script <asset> encountered a problem and was stopped: Out of memory` on
  DEBUG_CHANNEL. SL's Mono limit is 64 KB
  ([LlSetMemoryLimit](https://wiki.secondlife.com/wiki/LlSetMemoryLimit)).
- `llGetUsedMemory` and `llGetFreeMemory` report the VM's own count of the memory the script
  uses, and what is left of its 128 KB.
- The limit cannot change:
  - `llGetMemoryLimit` always returns 131072.
  - `llSetMemoryLimit` returns TRUE only for exactly 131072, and FALSE for every other value.
    The limit never changes.
- `llScriptProfiler` with `PROFILE_SCRIPT_MEMORY`, followed by `llGetSPMaxMemory`, does report
  the real peak.
- `llGetObjectDetails` `OBJECT_SCRIPT_MEMORY` counts 128 KB for each Phlox script in the object
  (stopped ones too), and 16 KB for each running script of another engine, as YEngine counts
  it. SL counts 64 KB per Mono script
  ([LlGetObjectDetails](https://wiki.secondlife.com/wiki/LlGetObjectDetails)).

### Run-time errors
- Errors are said on DEBUG_CHANNEL with the prefix `Script error: `. Scripts listening on
  DEBUG_CHANNEL hear them as far as `llSay` reaches (20 m by default), as SL documents:
  "Server-generated errors are broadcast the same distance as llSay"
  ([DEBUG_CHANNEL](https://wiki.secondlife.com/wiki/DEBUG_CHANNEL)). Viewers show them only to
  the object's owner. An error is cut to 1024 bytes, as `llSay` text is.
- The errors Phlox gives in YEngine's words, without the prefix (`llHTTPRequest`'s refused
  headers and MIME types, the outbound URL filter), reach YEngine's listens at the same
  `llSay` distance.
- A script's own `llWhisper`, `llShout` or `llRegionSay` on DEBUG_CHANNEL keeps its own
  distance. DEBUG_CHANNEL chat that no Phlox script spoke (another engine's errors) reaches
  Phlox listeners at `llSay` distance.
- A script killed by an error (out of memory, a VM fault) shouts
  `Script <asset> encountered a problem and was stopped: <message>`, the wording InWorldz used.
  Its Running flag is cleared, and it stays stopped across a region restart until it is reset
  or set running again.
- With `ChatThrottle` on (the default), a script error also pauses the script for 15 ms
  wherever InWorldz paused for it. See "Anti-abuse slowdowns" below.

### Listens
- A script may hold 65 listens and a region 1000. The limits come from `[LL-Functions]`
  `max_listens_per_script` and `max_listens_per_region`, the same keys YEngine reads.
- Past the limit, `llListen` returns -1 and raises **no** error. SL raises a run-time
  "Too Many Listens" error ([LlListen](https://wiki.secondlife.com/wiki/LlListen)).
- An `llListen` identical to one the script already holds active returns the existing handle.
- Handles are each script's own, numbered from 1 with the lowest free one first, as SL, YEngine
  and the core WorldComm number them; two scripts can hold the same handle number.
- A script receives at most 20 listen events per second; the rest of that second's are dropped.
  SL documents no such limit, and YEngine and Halcyon have none. `[InWorldz.Phlox]
  MaxListenEventsPerSecond` sets the number; 0 turns the limit off. The region log gets one line
  when a script starts being capped, then at most one a minute with how many deliveries were
  refused since the line before, for as long as the script goes on being capped; a minute with
  no refusal ends that run, and the next refusal starts a new one with its own first line.
- A prim never hears its own chat.
- `llRegionSayTo` refuses `DEBUG_CHANNEL` with the error "Cannot use llRegionSayTo() on
  DEBUG_CHANNEL.". Only its target hears it: the target prim's listens, or, for an avatar,
  the listens in that avatar's attachments (the avatar itself sees it on channel 0).
- Empty chat (`llSay(5, "")`) reaches listeners, as it does on YEngine.
- Chat is cut before anyone hears it. `llSay`, `llShout`, `llWhisper` and `llRegionSayTo` send
  at most 1024 bytes of UTF-8 ([LlSay](https://wiki.secondlife.com/wiki/LlSay): "msg can be a
  maximum of 1024 bytes"); a character the cut would split is dropped whole. `llRegionSay` sends
  at most 1024 characters ([LlRegionSay](https://wiki.secondlife.com/wiki/LlRegionSay)). YEngine
  does not cut what scripted listeners hear.

### Sounds
- A sound that is neither a sound in the prim's inventory nor a UUID gives the error
  "Could not find sound '<name>'" on DEBUG_CHANNEL
  ([LlPlaySound](https://wiki.secondlife.com/wiki/LlPlaySound)). `llPlaySound`, `llLoopSound`,
  `llLoopSoundMaster`, `llLoopSoundSlave`, `llPlaySoundSlave` and `llLinkPlaySound` also stop
  the sound the prim is playing; `llTriggerSound` and `llTriggerSoundLimited` only give the
  error. YEngine does nothing in both cases.
- `llSoundPreload` preloads the sound without `llPreloadSound`'s 1 s delay.

### Lists, strings and maths
- `llList2Key` returns `""` for an index outside the list and for an element that is not a
  string or key ([LlList2Key](https://wiki.secondlife.com/wiki/LlList2Key)).
- Keys are held as strings in Phlox lists, so `llGetListEntryType` reports `TYPE_KEY` for any
  string that is a valid UUID, as YEngine does. SL reports `TYPE_STRING` for a UUID written as
  a string.
- `llStringTrim` returns the string unchanged for a type other than `STRING_TRIM_HEAD`,
  `STRING_TRIM_TAIL` and `STRING_TRIM`.
- `llListStatistics`: `LIST_STAT_STD_DEV` is the sample standard deviation, as SL documents; it
  is 0 for a list of one number. `LIST_STAT_GEOMETRIC_MEAN` is NaN when the product of the
  numbers is negative ("Geometric mean applies only to numbers of the same sign");
  `LIST_STAT_HARMONIC_MEAN` is 0 when a number is 0.
- `llRot2Angle` returns an angle in [0, PI] with no small-angle cut-off, and `llRot2Axis`
  the axis that goes with it (`ZERO_VECTOR` for no rotation); both ignore the rotation's
  scale, as does `llAngleBetween`.

### JSON
- `llJsonGetValue` and `llJson2List` give `JSON_TRUE`, `JSON_FALSE` and `JSON_NULL` for JSON
  `true`, `false` and `null`; `llJsonValueType` reports `JSON_STRING` for a string.
- The getters skip a leading byte-order mark. An integer specifier indexes an array and a string
  specifier names an object member; the other way round is `JSON_INVALID`.
- `llJson2List` of a single JSON value is a one-item list; of text that is not JSON, a list
  holding that text.
- `llList2Json` writes `true`, `false` and `null` (and the `JSON_*` constants) as literals, keeps
  JSON objects, arrays and quoted strings as they are, trims strings, writes other strings
  (number-looking ones too) as JSON strings with control characters escaped, writes NaN and
  infinities as the strings `"NaN"`, `"Inf"` and `"-Inf"`, and returns `JSON_INVALID` for a
  `JSON_OBJECT` list of odd length
  ([LlList2Json](https://wiki.secondlife.com/wiki/LlList2Json)).
- `llJsonSetValue` on an empty string starts an array for an index (`llJsonSetValue("", [0],
  "x")` is `["x"]`) and an object for a key. The words `true`, `false` and `null` become
  literals; a value is written as a bare number only when it is a JSON number; a quoted value
  stays a string, quotes included. A path through a value of the wrong type replaces it, as in
  YEngine.

### Anti-abuse slowdowns
Phlox keeps a set of slowdowns from its InWorldz/Halcyon heritage. SL has none of them in
this form. Each is on by default and can be switched off under `[InWorldz.Phlox]` (see
[PhloxSetup.md](PhloxSetup.md)):

| Key | Effect |
|---|---|
| `ChatThrottle` | 15 ms pause after `llSay`, `llShout`, `llWhisper`, `llRegionSay`, `llRegionSayTo`, `llOwnerSay`, and after the script errors InWorldz paused for |
| `ResetThrottle` | more than 5 resets of one script in one second puts it to sleep for 5 s |
| `LinkMessageThrottle` | `llMessageLinked` pauses 50 ms when a receiving script's event queue is nearly full |
| `PhysicsThrottle` | physics setters pause for the average physics frame time when that is over 30 ms |
| `NotecardThrottle` | short delays on notecard reads |
| `BotThrottle` | 15 ms pause after bot chat, typing, sit, stand and touch calls |
| `FormatStringThrottle` | 100 ms pause after `iwFormatString` |
| `HttpInFlightThrottle` | at most 10 `llHTTPRequest` calls in flight per object and 200 per region; a request that returns `NULL_KEY` (throttled, refused or over these caps) costs 80 ms; a script whose event queue is 60% or more full pauses up to 50 ms before each request |

The function delays SL documents (for example 0.2 s for `llSetPos`, 2 s for
`llInstantMessage`, 20 s for `llEmail`) are applied as fixed times.

Gives follow SL's and InWorldz's delays: `llGiveInventory` and `iwGiveLinkInventory` sleep
2 s and `iwDeliverInventory` 100 ms only when giving to an avatar; `llGiveInventoryList`
sleeps 3 s on every call, as SL documents it, also for a prim
([LlGiveInventoryList](https://wiki.secondlife.com/wiki/LlGiveInventoryList)), where
InWorldz did not wait; `iwGiveLinkInventoryList` (3 s) and `iwDeliverInventoryList`
(100 ms) wait only for an avatar. `llRemoteLoadScriptPin` sleeps 3 s on every path, also
when it fails early, as SL documents; InWorldz returned at once from an early failure.

### HTTP
- The metadata list in `http_response` is always empty: `HTTP_BODY_TRUNCATED` is never sent
  ([Http_response](https://wiki.secondlife.com/wiki/Http_response)).
- The simulator's `X-SecondLife-*` headers are added after the script's own headers, so a
  script cannot forge them. A custom header beginning with `x-secondlife` is dropped.
- Custom headers follow YEngine's rules, with its error texts: `Host`, `User-Agent`, `Referer`,
  `Accept`, `From`, `Via` and a few others, or a name starting `proxy-` or `sec-`, stop the
  request (it returns `""`); `Cookie`, `Connection` and the other headers the HTTP stack sets
  itself are left out silently; at most 8 custom headers are sent; a header's name and value
  together may be at most 253 characters.
- A throttled request shouts on DEBUG_CHANNEL unless the script sets `HTTP_VERBOSE_THROTTLE`
  to `FALSE` ([LlHTTPRequest](https://wiki.secondlife.com/wiki/LlHTTPRequest)). The text is
  Phlox's own; SL's exact wording is not documented.
- `llHTTPRequest` and `llSendRemoteData` obey the same outbound URL filter as YEngine
  (`[Network] OutboundDisallowForUserScripts`).

### Values that differ from SL
- `llHash` uses the DJB2 hash. SL uses SDBM
  ([LlHash](https://wiki.secondlife.com/wiki/LlHash)), so the values differ from SL's (and
  from YEngine's, which follows SL).
- `llGetEnv` ([LlGetEnv](https://wiki.secondlife.com/wiki/LlGetEnv)):
  - `dynamic_pathfinding` is always `"disabled"`;
  - `region_cpu_ratio` is always `"1"` and `region_idle` always `"0"`.
  - These keys return `""`: `agent_limit_max`, `agent_reserved`, `agent_unreserved`,
    `region_rating`, and the damage-system keys.
- `llSetRegionPos` follows SL ([LlSetRegionPos](https://wiki.secondlife.com/wiki/LlSetRegionPos)), with one
  difference: up to 10 m past the region edge the object crosses into the region there only if there is one;
  where there is none the call returns FALSE and the object stays. Halcyon kept every request inside the region
  and teleported the wearer from an attachment; Phlox returns FALSE for attachments, as SL does.
- `iwGetWorldBoundingBox` gives the axis-aligned box, in region coordinates, around the object's box as it is
  turned.
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
- A local variable is in scope from the end of its declaration onward. A use before that
  point, or inside its own initialiser (`integer x = x + 1;`), means the variable of that
  name one scope out: an outer block's local, a parameter or a global. With none it is a
  compile error ("Symbol 'x' can not be used before it is defined").
- `==` and `!=` need the same type on both sides, where integer and float mix and key and
  string mix. `<`, `>`, `<=` and `>=` follow the same rule and do not take strings. Unlike
  SL, Phlox also compiles `<` and `>` between two keys, two vectors, two rotations or two
  lists, as Halcyon did.
- `.x`, `.y`, `.z` (and `.s` on a rotation) apply only to a vector or rotation variable;
  `llGetPos().z` does not compile, as in SL.
- A list cannot contain another list, and vector and rotation components must be integers
  or floats, as in SL.
- A function that returns nothing cannot be used as a value: as an operand, a list element,
  a cast or a condition.
- A call to an undefined function, and a character the language does not use (such as `#`,
  `$` or a backtick outside a string or comment), is a compile error with its line.
- Invisible characters pasted from web pages or chat (non-ASCII outside strings and
  comments) are dropped before compiling. String literals and comments are kept exactly
  as written.

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
- **What survives.** A Phlox script resumes where it was after a region or simulator restart,
  including in the middle of an event (asleep, in a loop or in a blocking call): its globals,
  state, queued events, listens and timer, which keeps its phase (the next `timer()` comes after
  the time it had left). Listens come back with their handles, on or off as `llListenControl`
  left them, with their `osListenRegex` filters, and a `botListen` listen still hears from its
  bot, as YEngine saves each listen's on/off state and regex bitfield. A row saved by an earlier
  build has none of these, and loads as before: its `llListen` listens on, and no
  `osListenRegex` or `botListen` listen. A restored `botListen` listen is registered whether or
  not its bot is in the region yet, as a live one stays registered when its bot leaves; it hears
  said chat from the bot's position once the bot is there. A script that was stopped stays
  stopped. It starts fresh when its source changes, and when it is reset.
- **What does not.** `llGetStartParameter` is 0 after a restart or a crossing, as SL documents
  ([LlGetStartParameter](https://wiki.secondlife.com/wiki/LlGetStartParameter): "The start
  parameter does not survive region restarts ... or region change"); a rez gives it the rez's
  parameter.
- **Rows that cannot be read.** A saved row that cannot be decoded is moved to the
  `script_state_rejected` table of the state database and the script starts fresh, as
  YEngine resets a script whose state file is bad. When the database itself fails (busy or
  locked), the script is held stopped and its row kept, and the next restart tries again.
- **Writes the database refuses.** A save or a delete the database does not take (busy,
  locked, or failing) stays queued in order and is tried again; a batch of writes is all
  or nothing. A write that fails five times is given up, with an error in the log naming
  the script. At shutdown every queued write is tried to that bound, and the log names
  each write that was not saved. A script loaded while its last save is still queued is
  restored from that save, not from the older row.
- **A state that cannot be saved.** A script whose tables nest more than 200 deep, or that
  holds a closure referring back to itself, keeps running, but its state cannot be saved
  while it is so. Its older saved state is not kept for a restart to resume from: the row
  is moved to the `script_state_rejected` table, with one error in the log naming the
  script ("cannot be captured ... cannot be restored in its place"), and a restart starts
  the script fresh with `state_entry`. As soon as its state can be saved again, the next
  save writes a normal row. The error is written once, and again only after a later save of
  it has worked. A save that fails only because the state changed while it was copied
  keeps the older row.
- **Shutdown with a script stuck in a call.** The final save waits 5 seconds for the
  script scheduler to stop. If a script is still inside a call then, the scheduler runs
  nothing further, that script keeps its last saved state, and every other script is
  saved. A part of a script's state that cannot be copied fails that script's save; it is
  never saved empty.
- **When rows go.** Deleting a script from a prim deletes its row. A derez, take or crossing
  keeps it; an object that comes back carrying its state uses that state first. `[InWorldz.Phlox]
  StateRowMaxAgeDays` (default 0, off) deletes rows not saved or loaded for that many days
  whose scripts are not loaded in the simulator. When an object crosses between two regions
  of one simulator, the region it left never writes over the row the region it entered has
  saved, whichever region handles the move first.
- **State carried inside objects.** Phlox puts a script's saved state in its serialized
  object, in YEngine's envelope
  (`<State Engine="InWorldz.Phlox" UUID="item" Asset="asset" Version="1"><ScriptState>...`),
  so a script carries on where it was, with no `state_entry`, after:
  - taking an object (or a copy) into inventory and rezzing it, in any simulator; it gets
    `on_rez` with the rez's parameter;
  - detaching and wearing it again, and logging in or teleporting while wearing it;
  - a region crossing, also into a region of another simulator process; its start parameter
    is then 0.

  The SL wiki ([State](https://wiki.secondlife.com/wiki/State)): "A script will NOT
  automatically re-enter the default state state_entry event when the task is rezzed or
  attached (even by a new owner), nor if the task is moved to another SIM, nor on SIM
  restart." Carried state is used before the state database's row; carried state saved for
  another asset (the script was edited) gives way to the row.

  What the script is told on arrival, once, after `state_entry` or the event it was in the
  middle of (a script restored asleep or mid-event finishes that event first):

  | Arrival | Event |
  |---|---|
  | The object crosses into another region | `changed(CHANGED_REGION)` |
  | A worn object crosses with its wearer | `changed(CHANGED_REGION)` |
  | The wearer teleports to another region | `changed(CHANGED_REGION \| CHANGED_TELEPORT)`, one event |
  | The wearer teleports within the region | `changed(CHANGED_TELEPORT)` (sent by the simulator; nothing is restored) |
  | Take and rez | `on_rez` |
  | Wear from inventory, or log in wearing it | `on_rez`, then `attach` |
  | The region starts | `changed(CHANGED_REGION_START)` |

  As the SL wiki says for
  [CHANGED_REGION](https://wiki.secondlife.com/wiki/CHANGED_REGION) and
  [CHANGED_TELEPORT](https://wiki.secondlife.com/wiki/CHANGED_TELEPORT), only scripts in the
  root prim get `CHANGED_REGION` and `CHANGED_TELEPORT`; scripts in child prims get neither.
  YEngine posts them to every script in the object. `CHANGED_TELEPORT` goes to worn
  objects only. A script that arrives with no state it can use starts fresh and gets
  `state_entry`, then the same event. Nothing else is posted for a crossing or a teleport:
  no `on_rez`, no `attach`, no `run_time_permissions`.

  Limits:
  - The simulator offers a crossing's states, and a teleport's attachment states, only to
    the region's default script engine (`[Startup] DefaultScriptEngine`). In a region whose
    default is not Phlox, a Phlox script arrives that way with a fresh start. Take, rez,
    attach and login are not affected.
  - An OAR export and load carries no script state, for any engine.
  - State another engine wrote (YEngine, XEngine), state in a newer envelope version, and
    state that cannot be read are refused: the script starts fresh and the object always
    rezzes. YEngine refuses Phlox's state the same way.
  - An event that reaches a crossing object's script after its state was captured, and
    before the object has left, reaches the copy left behind and is lost when the crossing
    succeeds. Halcyon's crossing wait has the same gap.
  - After a crossing that fails, a dataserver, HTTP or XML-RPC reply the script was waiting
    for when the crossing started does not arrive: the hold drops the replies a script is
    still owed, as Halcyon's crossing wait did.
  - A script that arrives on a parcel where scripts may not run is paused, and the arrival's
    `changed` event is dropped with whatever else reaches a paused script. SL queues it and
    posts it once the object is somewhere scripts run.
  - A seated avatar's crossing: the simulator moves the vehicle before its riders. When a
    script in the vehicle holds a grant from an avatar who has not arrived yet, the
    vehicle's `changed(CHANGED_REGION)` waits until that avatar has arrived: seated on the
    vehicle, with the grant given back, so the script reads it inside the event; or
    anywhere else in the region, and then without it. The wait ends after 10 seconds
    whatever has arrived, and the event comes once. The script runs its other events
    meanwhile. Halcyon waits for every rider the same way, with no limit. A rider whose
    grant no script in the vehicle holds is not waited for, as the simulator does not tell
    the new region which riders are coming. A script of the vehicle that arrived with state
    and has not loaded yet counts as holding such a grant until it has loaded, within the
    same 10 seconds.
  - A script held stopped because its row could not be read carries no state.
  - Objects saved by an earlier Phlox build carry no Phlox state and start fresh as before.
- **A crossing holds the object's scripts.** When an object starts to cross into another
  region (a region crossing, or an object teleport to another region), the simulator
  announces it (`EventManager.OnGroupBeginInTransit`) before the transfer starts, and Phlox
  holds every script of the object until the crossing ends
  (`EventManager.OnGroupEndInTransit`), as Halcyon's crossing wait did. While held:
  - nothing runs: an event in progress stops where it is, and a sleep, the timer and the
    touch repeat stop;
  - every event that reaches the script waits on its queue, in order, up to the usual 64,
    except chat on its listens, which is dropped (Halcyon took a held script's listens away);
  - the sensor repeat stops, and replies still owed to it (dataserver, HTTP, XML-RPC) are
    dropped, as Halcyon's crossing wait did; taken controls stay;
  - the script's URLs stay. A crossing that succeeds releases them in the region the object
    left, when its scripts are removed there; a crossing that fails leaves the object where
    it was, so its URLs keep working. Halcyon released them when the hold started.

  The state the crossing captures includes the held events: they run in the new region, in
  the order they came, and not in the region the object left. When the crossing fails
  (no region beyond, or the transfer is refused) the object is put back inside the region
  and its scripts carry on there: the held events run in order, each once, the timer comes
  back with the time it had left, and the sensor repeat starts again. Scripts of other
  objects are not held. The SL wiki says nothing about events during a crossing; it says
  events are "queued FIFO" and that when a script is paused "pending events are preserved"
  ([LSL Events](https://wiki.secondlife.com/wiki/Category:LSL_Events)).

  Before this hold, an event that reached the object during a crossing ran in the region it
  was leaving, and could run a second time in the new one.
- **Carried state is checked as input from outside.** It can come from anywhere: inventory
  from another grid, a Hypergrid visitor's attachments, an object another resident made.
  - It must fit the compiled script it is loaded for: its state index, its number of
    globals, its execution position, call frames and return addresses inside the script's
    code, its queued events (known types, the script's states, as many arguments as the
    handler takes) and its records (the shapes the script's own calls write). A state that
    does not fit is refused with one warning in the log; the script starts fresh and nothing
    is moved aside.
  - A global holding another type than the script declared cannot be told from the state:
    the first instruction that uses it stops that one script with its usual error ("Script
    ... encountered a problem and was stopped"), as any runtime error does.
  - Memory in use is recomputed from the restored values, never taken from the state, and a
    state above the 128 KiB script memory limit is refused. The event queue keeps what a
    running script's queue lets in (64 events), the timer is held to `MinTimerInterval` as
    `llSetTimerEvent` is, and listens to `[LL-Functions] max_listens_per_script` and
    `max_listens_per_region`.
  - An envelope above 65 x 128 KiB of state (the memory limit, plus 64 queued events) is
    refused before anything is decoded, and Phlox does not carry a state above it.
- **A grant comes back with a state, within limits.** The simulator clears a script item's
  grant every time it starts the script, so Phlox saves the grant with the state (the
  granter, the mask, and the object's owner then) and puts it back when the script is
  restored. The SL wiki does not say what happens to a grant across a restart, rez or
  crossing; [llRequestPermissions](https://wiki.secondlife.com/wiki/LlRequestPermissions)
  says only "Permissions persist across state changes".
  - From the region's own state database (a restart): the grant comes back whole, when the
    object's owner is still the owner saved with it. Otherwise nothing comes back. A grant
    still waiting for its granter when the state was saved is decided as carried state
    below, from a row too. A row saved when the object can no longer be found keeps the
    grant the script holds, which Phlox notes in the state each time the grant changes:
    after a derez, at a region stop (the simulator empties the scene before the final
    save) and when a region is removed from the engine. A grant still waiting for its
    granter is never saved as a grant in such a row.
  - From state carried inside an object (take and rez, take copy, attach, login, teleport,
    crossing): only what `llRequestPermissions` would grant at that moment without a
    dialog comes back, by the same decision: the granter wears the object (take controls,
    trigger animation, attach, track camera, control camera, override animations) or sits
    on it (take controls, trigger animation, track camera, control camera), or is an NPC the
    object's owner owns, or one seated on an object of that owner's (trigger animation); and
    the object's owner is still the owner saved with it. A granter who has not arrived yet (a vehicle
    crossing before its driver) leaves the grant waiting; it is decided the same way when
    that avatar arrives seated on the object or wearing it, and a grant whose granter never
    arrives that way never acts.
  - **Different from YEngine:** permissions Phlox grants only through a dialog (debit,
    change links, teleport, silent estate management and the others) never come back from
    carried state; the script asks again. YEngine restores every saved bit from carried state.
  - A restore posts no `run_time_permissions`; `llGetPermissionsKey` answers the restored
    granter.
  - Detaching into inventory loses take controls and control camera, as the simulator
    removes them before it saves the object (SL: a script loses `PERMISSION_TAKE_CONTROLS`
    "on reset, or if the object is deleted, detached, or dropped",
    [llTakeControls](https://wiki.secondlife.com/wiki/LlTakeControls)). A logout and login
    or a teleport keeps them, and the controls are taken again.
  - The records of taken controls and of `PERMISSION_SILENT_ESTATE_MANAGEMENT` act only
    while the item holds their grant, and wait with a grant that waits.
  - `llResetScript`, an owner change and a new `llRequestPermissions` or
    `llRequestExperiencePermissions` clear the saved grant and a waiting one.
  - A permission request still waiting for its answer, from `llRequestPermissions` or
    `llRequestExperiencePermissions`, ends when the script is reset, the object changes
    owner, or the script leaves the region (derez, crossing, teleport): an answer that comes
    later does nothing, and posts no event. The SL wiki says so for
    [llRequestExperiencePermissions](https://wiki.secondlife.com/wiki/LlRequestExperiencePermissions)
    ("Outstanding permission requests will be lost if the script is de-rezzed, moved to
    another region, or reset"); the llRequestPermissions page says nothing on it, and the
    same rule is applied.
  - A grant from an Experience: when `llRequestExperiencePermissions` grants (the agent
    allowed the Experience before, the Experience is trusted here, or the agent accepts the
    dialog), the script holds the permissions SL lists for it (take controls, trigger
    animation, attach, track camera, control camera, teleport), granted by that agent, so
    `llGetPermissions` and `llGetPermissionsKey` answer them as in SL
    ([llRequestExperiencePermissions](https://wiki.secondlife.com/wiki/LlRequestExperiencePermissions)),
    and the grant is saved as that Experience's. From the region's own state database it
    comes back whole, as any grant, unless the region no longer lets it run (the estate blocks
    it, or neither allows nor trusts it): then it ends as the script starts, with the controls
    it took, and the script gets `experience_permissions_denied` once with
    `XP_ERROR_NOT_PERMITTED_LAND` (17), as below for a parcel. The start does not ask the
    Experience service anything: an Experience that is now disabled or suspended is found by the
    read of its state shortly after the start (below). From carried state it comes back only when
    `llRequestExperiencePermissions` would grant it at that moment with no dialog: the script
    is still in that Experience, the Experience is allowed in the region and not blocked, and
    the granter is in the region, has not blocked it, and has allowed it (or it is trusted
    here). A granter who has not arrived yet leaves it waiting; it is decided when that avatar
    arrives anywhere in the region, as such a grant needs no seat or attachment. Neither
    `run_time_permissions` nor `experience_permissions` is posted by a restore.
  - A script's Experience is the one its script item names, never one named in saved state or
    in state that came with the object. A saved grant noted with an Experience the item does
    not name, whether from the region's own state database or carried in the object, ends
    before the script runs, with no event and nothing left waiting for its granter. State
    that came with an object whose script is in no Experience also loses any
    `experience_permissions` event still on its queue.
  - A grant from an Experience ends when its granter enters a parcel where the Experience
    cannot run: the estate blocks it, or neither allows nor trusts it. The script loses the
    grant and the controls it took, and gets `experience_permissions_denied` with
    `XP_ERROR_NOT_PERMITTED_LAND` (17), as SL lists under "The experience can no longer run":
    "The agent has moved to a parcel where the experience cannot run"
    ([experience_permissions_denied](https://wiki.secondlife.com/wiki/Experience_permissions_denied)).
  - When an avatar blocks an Experience from its profile, the simulator ends every grant that
    avatar gave to a script of that Experience and posts `experience_permissions_denied` with
    `XP_ERROR_NOT_PERMITTED` (4), as SL lists: "The agent has blocked the experience from the
    experience profile". The script gets that event once, loses the controls it took, and a
    region restart does not give the grant back. A request the script is still waiting on from
    that avatar ends with that one answer. The same avatar's grants to scripts of other
    Experiences, and other avatars' grants, stay.
  - States saved by an earlier Phlox build hold no grant and restore without one.
  - When a region starts, once the scripts it starts have loaded, one line in the log says
    how many came back with their saved state and how many of those got a grant back:
    `[PhloxExe]: Region start: N scripts restored, M with a permission grant put back`.
  A seated driver's taken controls also travel with a crossing in the simulator's own agent
  data.
- **The capture wait is per object.** The simulator asks for an object's script states on a
  region thread, one script at a time. Phlox captures all of the object's scripts in one
  pass of its scheduler and waits at most 10 seconds for that object, however many scripts
  it holds. On a timeout every script of the object travels without its state and starts
  fresh where it arrives, with one warning in the log.
- **Halcyon script-state databases are not imported.** Phlox's state database
  (`ScriptEngines/Phlox/state/script_state.db`, table `script_state`) differs from
  Halcyon's in file and table, and Phlox does not read Halcyon's; there is no importer.
  Scripts from a Halcyon simulator start fresh on a Phlox region.

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
  - nine OSSL functions: `osGiveLinkInventory`, `osGiveLinkInventoryList`,
    `osMessageAttachments`, `osNpcLookAt`, `osNpcSayTo`, `osReplaceAgentEnvironment`,
    `osReplaceParcelEnvironment`, `osReplaceRegionEnvironment`, `osResetEnvironment`.
- **Spelled differently:** YEngine's `osTemperature2sRBG` is `osTemperature2sRGB` on Phlox.
- **Phlox has, YEngine lacks:**
  - the `iw*` and `bot*` families (section 3);
  - some newer SL functions, for example `llSetAgentRot`, `llSortListStrided` and
    `llTransferOwnership`.
- Phlox declares 259 OSSL functions. They honour the `[OSSL]` keys (`AllowOSFunctions`,
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
- Notecard lines are cut at `NotecardLineReadCharsMax` bytes of UTF-8. Phlox reads the key
  from `[InWorldz.Phlox]`, else from `[YEngine]`, so one value can govern both engines. Unset,
  Phlox uses SL's 1024 bytes ([LlGetNotecardLine](https://wiki.secondlife.com/wiki/LlGetNotecardLine))
  and YEngine its own 255 characters. Values above 65535 are read as 65535.

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
- **Events dropped by a full queue.** When a script's event queue holds 64 events, new ones
  are dropped, as SL drops them. The region log gets one line when a run of drops starts, with
  how many of each kind were dropped, then one a minute with the drops since the line before,
  and the rest when the script is unloaded. A run lasts until a whole minute passes with no drop
  for that script, so events that keep arriving in bursts, with room in the queue between them,
  are one run and write about one line a minute, not one a burst.
- **`llInstantMessage` length.** A message longer than 1023 bytes of UTF-8 is cut to 1023
  bytes, as SL documents ([LlInstantMessage](https://wiki.secondlife.com/wiki/LlInstantMessage)).
  A character the cut would split is dropped whole. YEngine cuts at 1024 characters.
  An object's instant message is kept for a recipient who is offline, and carries the
  object's location (`Region/x/y/z`), which viewers show as a link.
- **Touches.** A touch reaches only the prim the region routes it to: the touched prim when its scripts handle the
  touch, and the root as well with `llPassTouches(TRUE)` or when the touched prim has no handler
  ([LlPassTouches](https://wiki.secondlife.com/wiki/LlPassTouches)). `llDetectedLinkNumber` is the prim that was touched.
  While the touch is held, `touch()` repeats every 100 ms, as Halcyon did; the viewer's grab updates only change the
  data the next repeat carries, and `touch_end` stops it. A script whose state has `touch()` but no `touch_start` or
  `touch_end` repeats the same way, and a child prim with such a script takes its own touch, as SL's `touch()` is
  "Triggered on touch start, each minimum event delay while held, and touch end"
  ([Touch](https://wiki.secondlife.com/wiki/Touch)). YEngine raises `touch()` once for each grab update the viewer
  sends.
- `llDetectedGroup` compares the group captured with the event, so it still answers after the avatar or object has
  left. An object with no group and an avatar with no active group count as the same group, as
  [LlSameGroup](https://wiki.secondlife.com/wiki/LlSameGroup) counts them; YEngine does the same.
- `llGroundSlope`, `llGroundNormal` and `llGroundContour` give Halcyon's values: the slope is the heightmap
  triangle's downhill vector, not normalised, with a negative z on sloped ground (its length gives the steepness);
  the normal is `<slope.x, slope.y, 1.0>`; the contour is `<-slope.y, slope.x, 0>`. YEngine normalises all three.
  The SL wiki says of `llGroundNormal`: "This function does not return a unit vector."
- `llApplyImpulse` in an attachment pushes the wearer, as in YEngine and Halcyon. `llApplyRotationalImpulse` in an
  attachment does nothing, as the SL wiki says ("It does not work on attachments"); Halcyon turned the wearer.
- `llCastRay` that the physics engine refuses (for example a cast over the engine's time or hit budget) returns
  `[RCERR_CAST_TIME_EXCEEDED]`. The region log gets at most one warning per script per minute for it: the first
  refused cast writes one at once, and the next, written by the first cast refused after the minute is up, gives the
  number of casts refused since that script's warning before:
  `[PhloxAPI]: llCastRay refused for script <item id> in <object>; refused casts since its last warning: <count>; reason: <reason>`.
  Before, every refused cast wrote its own line, so a script casting in a loop could write thousands a second.
- `llPushObject` with `local` TRUE turns the impulse by the target's rotation, avatar or object, as the SL wiki says:
  "if TRUE uses the local axis of target, if FALSE uses the region axis"
  ([LlPushObject](https://wiki.secondlife.com/wiki/LlPushObject)). YEngine does the same. A local push on an avatar
  used to be turned by the pushing prim's rotation. `ang_impulse` is not applied to an avatar ("ang_impulse is
  ignored when applying to agents or their attachments"), and Phlox does not apply it to objects either.
- `llPushObject` on land where pushing is restricted, by the parcel's Restrict Pushing or by the region's setting:
  an object may push its owner, and so an attachment may push its wearer, as the SL wiki says: "In no-push areas an
  object can only push its owner or itself." Phlox used to refuse it. Pushing another avatar there still works only
  for an object owned by the land's owner or by the estate owner or an estate manager, and on a region that restricts
  pushing, an avatar with no parcel under it can only be pushed by its own objects. YEngine allows an object to push its
  owner on both settings, but refuses when the region restricts pushing and finds no parcel; YEngine also lets any
  object push on group-owned land, and does not give estate managers an exception.
- `llGetPos` and `PRIM_POSITION` of a child prim in an attachment give the child's offset turned by the wearer's
  rotation plus the wearer's position (Halcyon's rule). YEngine gives the child's position from the attachment
  root's rotation, which does not follow the wearer turning.
- `llGetLinkName` follows Halcyon's table: in an unlinked prim only 0 and `LINK_THIS` name it; from the root, 0 is
  `NULL_KEY` and negative link numbers other than `LINK_THIS` and `LINK_ROOT` name link 2; from a child, 0,
  `LINK_ROOT` and other negative numbers name the root. A link that is not there is `NULL_KEY`, as SL documents.
- Money: `llGiveMoney`, `llTransferLindenDollars` and `iwGiveMoney` pay out through the
  region's money module, from the object's root prim and its owner; without one they shout
  "Command not implemented: llGiveMoney". `llGiveMoney` always returns 0 and has no delay
  ([LlGiveMoney](https://wiki.secondlife.com/wiki/LlGiveMoney)). `llTransferLindenDollars`
  answers in `transaction_result` with `"<destination>,<amount>"` on success or an error tag
  (`MISSING_PERMISSION_DEBIT`, `INVALID_DESTINATION`, `INVALID_AMOUNT`, `SERVICE_ERROR`, or the
  money module's reason). `iwGiveMoney` returns the transaction key, or the error tag, and
  posts no event.
- `llGiveInventoryList` gives nothing to an avatar with no presence in the region and says so
  on DEBUG_CHANNEL, as SL and YEngine; `llGiveInventory` and `iwDeliverInventory[List]` still
  deliver to an avatar elsewhere or offline.
- `llGetUsername` returns "First Last" where YEngine returns "first.last". Both answer only for
  an avatar the region holds (root or child agent), else `""`; `llRequestUsername` answers for
  any avatar with an account, here or not. For a key no account has, `llRequestUsername` and
  `llRequestDisplayName` raise no dataserver event, as the SL wiki says of both.
- `llManageEstateAccess` never bans the estate owner's partner, the partner named on the estate
  owner's profile, as Halcyon refused it: the call returns `FALSE`, nothing changes, and neither
  an IM nor an error is sent, as for the estate owner. When the estate owner's profile cannot be
  read, the ban goes ahead and the region's log says so. SL documents no partner rule.
- A change `llManageEstateAccess` makes to the estate's lists is stored and then sent to the
  estate's other regions, as a change from the viewer's estate tools is: the estate module's
  change event reloads the estate on this simulator's regions of the estate and sends
  `update_estate` to the others. A call that changes nothing (a no-op, a refusal or a query)
  stores and sends nothing.
- `llGetExperienceDetails(NULL_KEY)` gives the details of the script's own Experience, the one
  its script item names, and an empty list for a script in no Experience, as SL documents: "If
  experience_id is NULL_KEY, then information about the script's experience is returned. In
  this situation, if the script isn't associated with an experience, an empty list is returned"
  ([LlGetExperienceDetails](https://wiki.secondlife.com/wiki/LlGetExperienceDetails)).
- An Experience its owner has disabled, or one that is suspended, cannot be joined:
  `llRequestExperiencePermissions` answers `experience_permissions_denied` with
  `XP_ERROR_EXPERIENCE_DISABLED` (8) or `XP_ERROR_EXPERIENCE_SUSPENDED` (9), and 8 when both
  apply, before the land or the avatar is looked at. `llGetExperienceDetails` gives the same code
  and its message as the state. The SL wiki gives the codes ("The experience owner has temporarily
  disabled the experience.", "The experience has been suspended by Linden Lab customer support.",
  [llGetExperienceErrorMessage](https://wiki.secondlife.com/wiki/LlGetExperienceErrorMessage)) but
  not when they are raised; YEngine raises them in the same places and order. The state is read
  from the Experience service at each call. When the service cannot answer, the request is refused
  with `XP_ERROR_NOT_FOUND` (6), "The sim was unable to verify the validity of the experience."
  An Experience the service answers it does not know is refused with
  `XP_ERROR_INVALID_EXPERIENCE` (7), "The script is associated with an experience that no longer
  exists."
  - A grant a script already holds ends when its Experience is disabled or suspended, whether or
    not the script makes another call. Nothing tells a region of the change (the owner's edit is
    stored by the Experience service, a suspension is set there alone), so the region reads the
    state of every Experience a script in it holds a grant from: a minute after its last read,
    and about two seconds after grants are given back at a region start or come in with an
    object. The read runs on its own thread, one lookup at a time, and neither the scripts nor
    the region's start wait for it. When it finds the Experience disabled or suspended, every
    grant held from it in the region ends with the controls it took, and each script gets
    `experience_permissions_denied` once, with 8 or 9 (8 when both apply). A call of
    `llRequestExperiencePermissions` or `llGetExperienceDetails` that finds the same ends them
    at once; a script refused with 8 or 9 when asking one avatar is then also told that the
    grant it held from another has ended. The calls that use a grant do not look at the state.
    While the Experience service answers, a grant outlives its Experience's suspension by at most
    about a minute. A read the service does not answer changes nothing and is made again a
    minute later; the region's log warns of it at most once every ten minutes. SL documents
    nothing for this case; YEngine ends such a grant, with no event, at the script's next
    permission call.
  - `llAgentInExperience` and the key-value functions do not look at the state, as in YEngine.
- Start-up events come in SL's order: `state_entry` (a new script), then `on_rez`, then
  `attach` (an attachment worn from inventory), then `changed(CHANGED_REGION_START)`, which every
  script started by the region's start gets, new or restored. YEngine posts them in the same order.
- A state change, as the SL wiki's [State](https://wiki.secondlife.com/wiki/State) page lists it: "The event queue is
  cleared.", "All listens are released." and "Repeating sensors are released." After the `state` statement the only
  handler of the old state that runs is `state_exit`, then the new state's `state_entry`. An event posted to the
  script before the statement, and a `sensor`, `no_sensor` or `listen` raised by a sensor repeat or a listen of the
  old state while the state changes, is dropped: it runs neither in the old state's handler nor in the new state.
  Phlox used to run such an event after the statement, now and then, when a sensor sweep or a listen delivery was
  under way at that moment. The timer carries on into the new state, as the SL wiki says of `llSetTimerEvent`: "The
  timer persists across state changes".
- A reset, as the SL wiki's [llResetScript](https://wiki.secondlife.com/wiki/LlResetScript) page lists it: "Timers
  (including repeating sensors) are cleared.", "Listeners are removed.", "The event queue is cleared." and "If it has
  a state_entry event, then it is queued." This holds for every reset: `llResetScript`, `llResetOtherScript`,
  `osResetAllScripts`, the viewer's Reset, a reset asked for while the script is still compiling, and starting a
  crashed script again. The fresh script's first event is its `state_entry`. An event posted to the script before
  the reset, and a `sensor`, `no_sensor` or `listen` raised by a sensor repeat or a listen of the old script while
  it resets, is dropped. Phlox used to run such an event in the fresh script before its `state_entry` when a sensor
  sweep or a listen delivery was under way at that moment.
- Notecard text, as every notecard reader reads it: `llGetNotecardLine`, `llGetNumberOfNotecardLines`,
  `llGetNotecardLineSync`, `llFindNotecardTextSync`, `iwGetNotecardSegment`, the `iwGetLink*` notecard functions,
  `osGetNotecard`, `osGetNotecardLine` and `osGetNumberOfNotecardLines`. A notecard's stored "Text length" counts bytes
  of UTF-8, and Phlox reads exactly that many bytes, as YEngine does. A newline ends the line before it and does not
  start another: text that ends with a newline has no empty line after it, and an empty notecard has no lines. The SL
  wiki pages for `llGetNotecardLine` and `llGetNumberOfNotecardLines` do not say; this is how YEngine reads them. So a
  notecard made by `osMakeNotecard`, which writes a newline after the string and after each list item, reads back as
  the string's lines or the list's items. Phlox used to take the stored length as a count of characters, so text with
  characters of more than one byte could end in a stray `}` (and, when a `}` and a newline closed the text, an extra
  empty line after it); it answered one empty line more
  than YEngine for text that ends with a newline, and `""` instead of EOF for line 0 of an empty notecard.

---

## 3. Extensions

### Language
- `<<=` and `>>=` (integer only).
- Built-in functions may be overloaded by argument type. This is how the three forms of
  `osTeleportAgent` and `osTeleportOwner`, and the typed forms of `osApproxEquals`,
  `osSetPenColor` and others, coexist. User functions cannot be overloaded.

### Events
`bot_update(string, integer, list)` is the only event beyond SL's set. The bot manager
raises it for the `bot*` functions, as Halcyon did: `BOT_MOVE_COMPLETE` with `[bot position]` when
a navigation path is done; `BOT_MOVE_UPDATE` with `[next node, bot position]` each time the bot
moves on to another point (after a teleport point too, the last one included); `BOT_MOVE_FAILED`
with `[next node, bot position]` when a move times out; `BOT_MOVE_AVATAR_LOST` with
`[avatar position, 0.0, bot position]`, once, when a followed avatar leaves the region (the
position is then `ZERO_VECTOR`) or is farther than `BOT_LOST_AVATAR_DISTANCE` (default 1000 m).
It goes to every script registered with `botRegisterForNavigationEvents`, and to the script that
created the bot with `botCreateBot`.

### `iw*` functions (82)
| Area | Functions |
|---|---|
| Avatars and names | `iwAvatarName2Key` (answers at once; a blank last name means "Resident"), `iwGetAgentData` (an immediate `llRequestAgentData`), `iwGetAgentList` (`llGetAgentList` with a bounding box that applies only when both corners are set, an axis of 0 and 0 matching any value; it returns the `llGetObjectDetails` values asked for, or the agents' keys when none are asked; agents in god mode are left out), `iwGetAppearanceParam`, `iwAvatarOnLink`, `iwIsPlusUser` (always 0: the grid has no premium tier), `iwDetectedBot` |
| Groups and land | `iwActiveGroup`, `iwGroupInvite`, `iwGroupEject`, `iwHasParcelPowers` (with the `IW_POWER_*` constants), `iwSetGround`, `iwWind`, `iwSetWind`, `iwGroundSurfaceNormal` |
| Teleport | `iwTeleportAgent` |
| Rezzing and objects | `iwRezObject`, `iwRezAtRoot` (return the new object's key), `iwRezAt`, `iwRezPrim`, `iwCheckRezError`, `iwGetWorldBoundingBox`, `iwGetObjectMassMKS`, `iwGetAngularVelocity`, `iwGetLastOwner`, `iwLinkTargetOmega`, `iwStandTarget`, `iwLinkStandTarget`, `iwStartLinkAnimation`, `iwStopLinkAnimation`, `iwSearchLinksByName`, `iwSearchLinksByDesc` |
| Inventory of other links | `iwGetLinkInventoryNumber`, `iwGetLinkInventoryType`, `iwGetLinkInventoryPermMask`, `iwGetLinkInventoryName`, `iwGetLinkInventoryKey`, `iwGetLinkInventoryCreator`, `iwGetLinkInventoryDesc`, `iwGetLinkInventoryLastOwner`, `iwRemoveLinkInventory`, `iwGiveLinkInventory`, `iwGiveLinkInventoryList`, `iwDeliverInventory`, `iwDeliverInventoryList` (return `IW_DELIVER_*` codes), `iwSearchInventory`, `iwSearchLinkInventory`, `iwRemoteLoadScriptPin` (returns `IW_REMOTELOAD_*`) |
| Notecards and assets | `iwMakeNotecard` (writes a notecard of up to 64 KB), `iwGetNotecardSegment`, `iwGetLinkNumberOfNotecardLines`, `iwGetLinkNotecardLine`, `iwGetLinkNotecardSegment`, `iwRequestAnimationData` (these answer in `dataserver`) |
| Strings | `iwSubStringIndex`, `iwMatchString` (with `IW_MATCH_*`), `iwReplaceString`, `iwFormatString`, `iwStringCodec`, `iwReverseString`, `iwChar2Int`, `iwInt2Char`, `iwSHA256String`, `iwParseString2List`, `iwVerifyType`, `iwFormatTime`, `iwGetLocalTime`, `iwGetLocalTimeOffset` |
| Lists | `iwMatchList`, `iwListIncludesElements`, `iwReverseList`, `iwListRemoveElements`, `iwListRemoveDuplicates` |
| Numbers, colour, misc | `iwClampInt`, `iwClampFloat`, `iwIntRand`, `iwIntRandRange`, `iwFrandRange`, `iwColorConvert` (RGB/HSL/HSV), `iwNameToColor`, `iwValidateURL`, `iwGiveMoney` |

The `iwGetLink*` inventory and notecard functions search every prim of a multi-prim target
(`LINK_SET`, `LINK_ALL_OTHERS`, `LINK_ALL_CHILDREN`): the first prim, in link order, that
holds the item answers. InWorldz looked at the first prim only, and refused a multi-prim
target for `iwGetLinkNumberOfNotecardLines`. `iwGetLinkInventoryName` and
`iwSearchLinkInventory` list the names of every selected prim together, in LSL order;
`iwRemoveLinkInventory` removes the item from every selected prim; `iwGetLinkInventoryPermMask`
answers the bits common to every selected prim that holds the item, and -1 when none does.

- `iwIntRand(max)` returns 0..max, or max..0 for a negative max.
- `iwParseString2List` keeps an empty field at a leading or doubled separator whether or not
  `keepnulls` is set, as Halcyon does; `keepnulls` still controls empty fields a trim leaves.

Section 4 lists the `iw*` functions that do nothing or only part of their job.

`iwGetAgentData` and `llRequestAgentData` answer InWorldz's `DATA_ACCOUNT_TYPE` (11001) with the
account's user title, the label the viewer's profile shows; most accounts have none, so `""`.

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

`iwDetectedBot` returns the bot's key in the `sensor` and `no_sensor` events of `botSensor` and `botSensorRepeat` and in
the `listen` events of `botListen`, `NULL_KEY` in other events with detect data, and `""` in events without. A bot
does not sense itself. The option lists of `botFollowAvatar`, `botSetNavigationPoints` and `botWanderWithin` take an
integer key and an integer or float value (`botFollowAvatar` also a vector); any other pair makes `botFollowAvatar`
return `BOT_ERROR` and the other two do nothing. In `botSetNavigationPoints` a number among the points is the wait
for `BOT_TRAVELMODE_WAIT`, in seconds.

### Constants
- `IW_PRIM_ALPHA` and `IW_PRIM_PROJECTOR*` are extra prim-params rules.
- `IW_OBJECT_SCRIPT_MEMORY_USED` is an extra `llGetObjectDetails` code.
- `llGetEnv` also answers Halcyon's platform keys: `script_engine` (`"Phlox"`), `region_size_x`,
  `region_size_y`, `region_size_z`, `short_version`, `long_version`, and from
  `[GridInfoService]` `platform`, `grid_management`, `grid_nick`; `grid_name` is the grid's
  name, as `grid` is. `inworldz` and `halcyon` return `""`.
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
- `llGodLikeRezObject`, `llCollisionSprite`, `llPointAt`, `llStopPointAt`, `botChangeOwner`
  and `iwRezPrim` shout "Command not implemented". `iwRezPrim` returns `NULL_KEY`.
- `llTakeCamera`, `llReleaseCamera`, `llSound`, `llMakeExplosion`, `llMakeFountain`,
  `llMakeSmoke` and `llMakeFire` shout "Command deprecated".
- `llParcelMediaCommandList` and `llParcelMediaQuery` reject the parameters they do not
  handle.
- `iwSetWind` shouts "Command not implemented: iwSetWind" on `DEBUG_CHANNEL` when its owner is an estate manager
  or a god (the callers Halcyon let set the wind); for anyone else it does nothing. The region's wind module has no
  way to set the wind at a point.

### Functions that return a fixed value
| Function | Returns |
|---|---|
| `llGetAccel`, `llGetOmega`, `llGetTorque`, `iwGetAngularVelocity` | `ZERO_VECTOR` |
| `llGetEnergy` | 1.0 |
| `llCloud` | 0 |
| `llGetCameraAspect`, `llGetCameraFOV` | 1.7778 and 1.0472 (the viewer does not send them) |
| `llGetAgentLanguage` | `""` |
| `llWorldPosToHUD` | `<0.5, 0.5, 0>` |

`llSetPrimURL` and `llCloseFloater` do nothing. `llRefreshPrimURL` does nothing
and gives the error "llRefreshPrimURL - not yet supported", as Halcyon did; SL documents it as
deprecated and doing nothing.

`iwCheckRezError` answers from the region's rez checks: `IW_REZ_NOT_PERMITTED` for an owner the
region blocks from rezzing (asked first, as Halcyon did), `IW_REZ_NO_LAND_PARCEL` where there is
no parcel, `IW_REZ_NOT_PERMITTED` where the owner may not rez, `IW_REZ_PARCEL_LAND_IMPACT`
where the prims would go over the parcel's limits, otherwise `IW_REZ_OK`. It never returns
`IW_REZ_REGION_SCENIC` or `IW_REZ_REGION_LAND_IMPACT`. `isTemp` is not used, as in Halcyon.
When the prims would not fit, the region's prim-limit module may also send the owner the
message it sends for a refused rez.

A region can block an owner from rezzing (the console's `block owner`, or `[BlockedOwners]
BlockEstateBanned`). A Phlox script whose object's owner is blocked rezzes nothing: `llRezObject`,
`llRezAtRoot`, `llRezObjectWithParams`, `iwRezObject`, `iwRezAtRoot` and `iwRezAt` fail with no
error and a 100 ms pause, before any other check, as Halcyon's bad-user check did. SL has no
such list.

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
- `llGetVehicleFlags` returns the vehicle type, not its flags.
- `llGetAgentInfo` never sets `AGENT_AUTOPILOT`.
- `llGetParcelDetails` / `osGetParcelDetails`:
  - name, description, owner, group, area and id are real;
  - `PARCEL_DETAILS_SEE_AVATARS` is always 1;
  - every later key (prim capacity, prims used, landing point, flags and others) returns 0.
- `llGetObjectDetails`:
  - `OBJECT_PRIM_EQUIVALENCE` is the prim count, which is what this simulator's parcels count;
    `OBJECT_SERVER_COST` is 0;
  - any `OBJECT_*` code not handled returns `OBJECT_UNKNOWN_DETAIL` (-1), as SL does for an
    unknown code. That includes `OBJECT_PRIM_COUNT`, `OBJECT_TOTAL_INVENTORY_COUNT`,
    `OBJECT_REZZER_KEY`, `OBJECT_CREATION_TIME`, `OBJECT_SIT_COUNT`, `OBJECT_TEXT`,
    `OBJECT_SCALE` and others.
- `llHash`: see section 1.
- `iwReverseString` reverses UTF-16 code units, as Halcyon does: a character outside the
  Basic Multilingual Plane (most emoji) comes back as two broken halves, and a combining
  accent moves in front of the letter it belonged to.
- `iwMatchList` supports only `IW_MATCH_HEAD` and `IW_MATCH_TAIL`. The regex and count
  match types shout "not implemented" and return 0.
- `iwStringCodec` validation (`VALIDATE`) of the base4k codec always answers
  `"INVALID CODEC"`.
- `iwStandTarget` and `iwLinkStandTarget` set the stand offset, which is saved with the object;
  the rotation is ignored.
- `llSetForce` and `llSetForceAndTorque` in an attachment, physical or not, set a constant force on the wearer
  ([LlSetForce](https://wiki.secondlife.com/wiki/LlSetForce): "Used on an attachment, it will apply the force to the
  avatar"). The avatar holds it and hands it to the physics engine (`PhysicsActor.SetConstantForce`), which is to
  apply it on every step; a local force (`local` TRUE) is in the avatar's own axes and turns with the avatar. A new
  call from any attachment replaces it and `ZERO_VECTOR` ends it. As in Halcyon, detaching the object or resetting or
  removing the script does not end it, so a script that should stop pushing when taken off sets `ZERO_VECTOR` in its
  `attach` event; that works when the object is detached to inventory, but not when it is dropped, because by then
  it is no longer an attachment. When the avatar crosses or teleports to another region the force goes with it in the
  agent data (optional fields `constant_force` and `constant_force_local`); arriving from a simulator that does not
  send them, the avatar has no force.
  **No physics engine in this repository applies the force yet** (ubODE, BulletSim, POS and BasicPhysics keep the
  default `SetConstantForce`, which does nothing), so the avatar does not move. ubODE's avatar has no constant force
  at all: its `Force` property reports the avatar's walking target velocity and setting it does nothing. An engine
  outside this repository, such as a Jolt module, shows no effect either until it implements `SetConstantForce`.
- `llSetForce` in an unattached physical object: a local force (`local` TRUE) is turned once by the object's rotation
  when it is set, not kept in the object's frame as it turns.
- `botSetNavigationPoints`: a move that times out (about 60 s) raises `BOT_MOVE_FAILED` and ends the path. Halcyon
  teleported the bot to the point and went on, after `BOT_MOVEMENT_TELEPORT_AFTER` seconds (60 by default); that
  option and `BOT_MOVEMENT_TYPE` (`BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY` repeats the path) are not read.
- `botWanderWithin` raises no `bot_update`. Halcyon raised `BOT_MOVE_UPDATE` as the bot reached each wander point (when
  there was a wait between points) and `BOT_MOVE_FAILED` when it did not reach one.
- `botFollowAvatar` walks once toward the avatar and does not keep following it; `BOT_REQUIRES_LINE_OF_SIGHT`, the
  start and stop following distances and the allow-running, flying and jumping options are not read.

### Prim-params rules
- `PRIM_HEALTH` and the damage type in `PRIM_DAMAGE` are accepted and dropped. Reading them
  back gives 0.0 and `DAMAGE_TYPE_GENERIC`.
- `PRIM_PHYSICS_MATERIAL` can be set, but reading it returns nothing.
- `PRIM_SIT_FLAGS`: `SIT_FLAG_NO_COLLIDE` and `SIT_FLAG_NO_DAMAGE` are stored for read-back
  only.
- `PRIM_SIT_TARGET` with a nonzero active value sets a target at `ZERO_VECTOR` with
  `ZERO_ROTATION`, as SL documents, and it reads back exactly. The region database does not
  save the target's on/off state, so after a region restart such a target is off (a take and
  rez, a crossing or an archive keep it). YEngine keeps it across a restart by storing a
  1e-5 m offset, which reads back.
- For seated avatars, only position and rotation rules apply.
- `PRIM_MATERIAL` with a value outside 0 to 7 is ignored (Halcyon refused the whole call).
- `PRIM_FLEXIBLE` makes the whole object phantom when it turns a prim flexible, as YEngine does.
  The SL wiki does not say whether only the flexible prim becomes phantom.

### Events
- `game_control` compiles but is never raised.
- When an avatar blocks an Experience from its profile while a script that holds no grant from
  that avatar is waiting on `llRequestExperiencePermissions` for it, the simulator raises
  nothing: the request is answered by the avatar's answer to the dialog, or after 5 minutes
  with `XP_ERROR_REQUEST_PERM_TIMEOUT`.
- `money` is raised only when the region has a money module.

### Engine
Script state is not exported with objects; see "Saved state" in section 2.
