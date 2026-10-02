# Phlox / SLua Script Engine

## What it is

Phlox is the InWorldz/Halcyon script engine, ported to Tranquillity: LSL/OSSL
scripts are compiled to bytecode and executed on a stack-based virtual machine
with serializable runtime state, so **script state survives region restarts**
(a script resumes with its variables, current state, pending timers, and active
listens intact, instead of re-running `state_entry`). The engine also carries
the InWorldz `iw*` heritage functions alongside standard LSL/OSSL.

On top of the same VM, this port adds **SLua**: Second Life-conformant
Luau-flavored scripting, compiled by the SLua compiler (closures, metatables,
varargs, multiple returns, the `ll.*` API surface, Luau `vector` type,
string/table/math stdlib) and run on the same scheduler and persistence
infrastructure as LSL scripts. Conformance is tracked by
`Tests/SluaProofRunner`, an offline runner that executes Luau snippets on the
VM and buckets results as PASS / DIVERGENCE / GAP. The console command
`phlox sluaproof` runs the same kind of offline self-test in a running region
server.

## Enabling it

Phlox is the default script engine as shipped: `OpenSimDefaults.ini` sets
`[Startup] DefaultScriptEngine = "InWorldz.Phlox"` and enables both Phlox and
YEngine. Phlox coexists with YEngine; each engine only handles the scripts
routed to it, and YEngine stays loaded as the backup engine.

```ini
[Startup]
    DefaultScriptEngine = "InWorldz.Phlox"   ; or "YEngine"

[InWorldz.Phlox]
    Enabled = true

[YEngine]
    Enabled = true
```

How the two engines share a region, what changing the default does to existing
scripts, and every `[InWorldz.Phlox]` setting are in
[PhloxSetup.md](PhloxSetup.md). How Phlox differs from SL and from YEngine is
in [PhloxKnownDefects.md](PhloxKnownDefects.md).

## Choosing the engine and the language

- **The engine header.** A script can name its engine on its first line:
  `//InWorldz.Phlox:` puts it on Phlox, `//YEngine:` keeps it on YEngine. A
  script without a header runs on the default engine.
- **SLua.** Phlox compiles a script as SLua when its text, after any leading
  white space, begins with `--`; the usual first line is `--!slua`. Such a
  script runs on Phlox only where Phlox is the default engine.
- **`//InWorldz.Phlox:slua`.** As the first line, this header puts the script on
  Phlox and compiles the rest as SLua in any region where Phlox is loaded,
  whatever the default engine:

  ```lua
  //InWorldz.Phlox:slua
  ll.Say(0, "Hello from SLua")
  ```

The header rules in full are in [PhloxSetup.md](PhloxSetup.md).

## Where it keeps its data

Runtime data lives under `ScriptEngines/Phlox/` in the region server's working
directory (auto-created): the compiled-bytecode cache and the script-state
SQLite database. The paths, and what an upgrade does to the cache, are in
[PhloxSetup.md](PhloxSetup.md).

## Architecture note

`PhloxEngine` (in `Source/Phlox.ScriptEngine`) registers as a region module by
interface reflection like the other engines and implements
`IScriptEngine`/`IScriptModule`. Script sources are compiled by
`InWorldz.Phlox` (in `Source/InWorldz.Phlox`) — an ANTLR4 LSL front end or the
SLua compiler, both emitting the same bytecode — and executed cooperatively by
a single-threaded round-robin scheduler in fixed timeslices, which is what
makes runtime state cheap to serialize at any wait point. Long-running
syscalls (HTTP, dataserver, sensors) are dispatched to a bounded FIFO worker
pool on the .NET thread pool and their results re-enter the scheduler as
syscall returns. Syscalls that call grid services run on a small per-region
service lane, so a slow service stalls only the script that asked. The
scripted-bot subsystem (`IBotManager` / `BotManager` in OptionalModules) and
the experience/key-value adapter (over Tranquillity's native Experience
service) provide the region-side services the Phlox script API expects.
