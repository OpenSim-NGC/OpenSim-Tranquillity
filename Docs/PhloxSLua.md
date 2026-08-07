# Phlox / SLua Script Engine

## What it is

Phlox is the InWorldz/Halcyon script engine, ported to Tranquillity: LSL/OSSL
scripts are compiled to bytecode and executed on a stack-based virtual machine
with serializable runtime state, so **script state survives region restarts**
(a script resumes with its variables, current state, pending timers, and active
listens intact, instead of re-running `state_entry`). The engine also carries
the InWorldz `iz*` heritage functions alongside standard LSL/OSSL.

On top of the same VM, this port adds **SLua**: Second Life-conformant
Luau-flavored scripting. Scripts beginning with `--!slua` are compiled by the
SLua compiler (closures, metatables, varargs, multiple returns, the `ll.*`
API surface, Luau `vector` type, string/table/math stdlib) and run on the same
scheduler and persistence infrastructure as LSL scripts. Conformance is
tracked by `Tests/SluaProofRunner`, an offline runner that executes Luau
snippets on the VM and buckets results as PASS / DIVERGENCE / GAP.

## Enabling it

Phlox coexists with YEngine; each engine only handles scripts routed to it.
Two settings in `OpenSim.ini` control it:

```ini
[Startup]
    ;; Which engine compiles newly-saved scripts. YEngine is the default;
    ;; set this to hand new scripts to Phlox instead.
    DefaultScriptEngine = "InWorldz.Phlox"

[InWorldz.Phlox]
    ;; Phlox disables itself unless this section exists with Enabled = true.
    ;; With Enabled = true but DefaultScriptEngine = "YEngine", Phlox loads
    ;; and stays idle — safe to keep available.
    Enabled = true
```

## Configuration keys

| Section | Key | Default | Meaning |
|---|---|---|---|
| `[Startup]` | `DefaultScriptEngine` | `YEngine` | Engine that compiles new scripts (`YEngine` or `InWorldz.Phlox`). |
| `[InWorldz.Phlox]` | `Enabled` | `false` (absent) | Master switch; the engine does not initialize without it. |

Runtime data lives under `ScriptEngines/Phlox/` in the region's working
directory (auto-created): the compiled-bytecode cache and the script-state
SQLite database (via `Microsoft.Data.Sqlite`; the native `e_sqlite3` library
ships with publish output).

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
syscall returns. The scripted-bot subsystem (`IBotManager` /
`BotManager` in OptionalModules) and the experience/key-value adapter (over
Tranquillity's native Experience service) provide the region-side services the
Phlox script API expects.
