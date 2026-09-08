# Phlox / YEngine Runtime Consolidation — Discussion Notes

Status: **discussion / not started**. Captured 2026-09-07 for future reference. No
implementation work has begun; this document exists so the analysis doesn't need
to be redone from scratch when the project is picked up.

## Context

Tranquillity currently ships two independent script engines side by side:

- **YEngine** (`Source/OpenSim.Region.ScriptEngine.YEngine` +
  `Source/OpenSim.Region.ScriptEngine.Shared`) — the long-standing upstream
  OpenSim engine. LSL/OSSL compiled to real .NET IL (`MMRScriptCodeGen`/
  `MMRScriptObjWriter`), executed as normal CLR methods.
- **Phlox** (`Source/Phlox.ScriptEngine` + `Source/InWorldz.Phlox`) — the ported
  InWorldz/Halcyon engine. LSL/OSSL/SLua compiled to custom bytecode, executed on
  a cooperative stack-machine VM (`InWorldz.Phlox.VM`). This buys **serializable
  mid-execution state** (a script resumes with variables, current state, pending
  timers, and active listens intact across a region restart, instead of
  re-running `state_entry`).
- **SLua** is not a third runtime — it's an additional front-end compiler that
  emits the same Phlox bytecode as the LSL front end, so it's already unified
  with Phlox. See `Docs/PhloxSLua.md`.

The original question: can the two runtimes be merged into one shared
implementation to avoid dual maintenance? Follow-up questions covered whether
retiring YEngine in favor of Phlox/SLua is the better goal, and how feasible it
would be to port YEngine's LSL language extensions to Phlox.

## Part 1 — Can the two runtimes be merged?

### What's already shared (no work needed)
- Outer engine contract: both implement `IScriptEngine`/`IScriptModule` from
  `OpenSim.Region.ScriptEngine.Shared`.
- `EventParams`, `DetectParams`, and (partially) `LSL_Types` are reused by Phlox
  already (e.g. `XmlRequest.cs` posts `remote_data` using the shared
  `LSL_Types`).

### Layer-by-layer merge difficulty

| Layer | Difficulty | Notes |
|---|---|---|
| Region module / event delivery contract | Done | Already shared via `IScriptEngine`/`IScriptModule`. |
| Async request plumbing (`AsyncCommandManager` + Http/Xml/Sensor plugins) | Low–Medium | Smallest duplicated surface (~250–700 lines/file each side). Both route through the same `IHttpRequestModule`/`IXMLRPC` region-side interfaces already. Realistic to unify. Timer/Dataserver/Listener are handled natively inside Phlox's scheduler/VM state and would be risky to force into the shared plugin shape (ties into serializable-state guarantees) — leave Phlox-native. |
| Syscall / API layer (`LSL_Api`/`OSSL_Api`/`MOD_Api`/`LS_Api` vs `LSLSystemAPI`/`ISystemAPI`) | High — not recommended as a literal merge | Fundamentally incompatible calling conventions: YEngine's compiler-emitted IL calls interface methods typed in `LSL_Types` wrapper structs; Phlox's VM dispatch calls `ISystemAPI` methods typed in raw CLR primitives (`InWorldz.Phlox.Types`). Unifying means rewriting one side's syscall boundary — a rewrite, not a refactor. ~30k shared-side lines vs ~13k Phlox-side lines of behavior to cross-check for parity if attempted. |
| Compiler / execution engine (ANTLR4+SLua bytecode+VM vs MMR IL codegen) | Not recommended to unify | These exist because they solve different problems (serializable state vs. native IL speed). Merging = deleting one execution model; that's a product decision, not a refactor. |

### Bottom line on merging
Low value, high complexity, especially in the syscall layer where all the
actual duplicated maintenance burden lives. Not recommended as a goal in
itself.

## Part 2 — Retire YEngine, standardize on Phlox/SLua?

Directionally correct as a way to actually eliminate dual maintenance (since
merging the runtimes isn't practical — see above). **But gated on a major fact
found during this analysis: OSSL parity.**

### Functional parity check (measured against source, not assumed)

| Surface | YEngine (`ILSL_Api`/`IOSSL_Api`) | Phlox (`ISystemAPI`) |
|---|---|---|
| Core `ll*` functions | 486 | 532 declared; only 5 remaining `Stub()` calls in the 13k-line `LSLSystemAPI.cs` impl — core LSL is close to complete (InWorldz/Halcyon's LSL implementation was historically thorough). |
| `os*` (OSSL) functions | 268 | **2** (`osTeleportAgent`, `osGetAvatarList`). Everything else is simply absent from the interface, not stubbed. |

OSSL is the surface most OpenSim-specific content, mods, and grid tooling
actually depends on (NPCs, appearance, teleports, materials, formatting,
console/admin hooks, physics tweaks, etc.). Right now Phlox would break almost
any script using `os*` functions.

### Recommendation
- Treat "port/implement the OSSL surface in Phlox" as its own tracked backlog
  item (comparable in scope to the `Docs/HostedService*` sprint docs already in
  this repo), with a conformance suite analogous to `Tests/SluaProofRunner` but
  for OSSL, **before** committing to a YEngine deprecation date.
- Keep YEngine available (config-gated, non-default) as a fallback for
  OSSL-heavy content until that closes — lower risk than a hard cutover.
- Everything else (dropping duplicated `AsyncCommandManager`/Http/Xml/Sensor
  plugins once YEngine is gone, consolidating docs/tests, removing the huge
  shared `LSL_Api`/`OSSL_Api`/`MOD_Api`/`LS_Api` files) is straightforward
  cleanup *after* parity, not a blocker before it.
- "As soon as practical" should mean "as soon as OSSL parity (or an accepted
  documented subset) exists," not a calendar date set now.

## Part 3 — Feasibility of porting YEngine's LSL extensions to Phlox

YEngine gates all of these behind an opt-in pragma: a `//yoptions;` comment on
line 2 of the script (confirmed in `MMRScriptTokenize.cs`'s `Options` struct:
`advFlowCtl`, `tryCatch`, `arrays`, `chars`, etc., cross-checked against the
wiki pages). None of it is default LSL behavior — same additive, non-breaking
pattern should be used if ported to Phlox.

| Feature | Effort | Notes |
|---|---|---|
| `&&&` / `\|\|\|` short-circuit AND/OR | Low, lowest risk | YEngine deliberately did **not** make `&&`/`\|\|` short-circuit (would silently change existing script semantics) — it added new operators instead. Phlox's `booleanExpression` rule / `VisitBooleanExpression` currently compiles `&&`/`\|\|` to unconditional `booland`/`boolor` (matches stock LSL, evaluates both sides). Adding `&&&`/`\|\|\|` = two new tokens + a grammar alternative + conditional-branch codegen reusing existing label/branch machinery (`NextLabel`, same infra `if`/`while` use). No VM changes. Zero regression risk — pure new syntax. |
| `constant` | Low–Medium | Phlox already has `ConstantSymbol : VariableSymbol` with per-type emission templates (`iconst`/`fconst`/`syssconst`/…) — the "inline value substituted at compile time" mechanism already exists (used for built-in constants like `PI`, `TRUE`, `ZERO_VECTOR`). Work = new global-scope grammar rule + compile-time evaluator for the documented limited operator set (`& \| ^ ~` int; `- * / %` int/float; `+` string concat; type inferred from RHS) + wiring into `DefVisitor`/`SymbolTable`. Front-end only. |
| `break` / `continue` (loops) | Medium | `while`/`do-while`/`for` codegen already allocates a start-label and out-label per loop (`NextLabel("while_start_", "while_out_")` etc.) — targets already exist, just not exposed to nested statements. Needs a loop/switch context stack threaded through statement visiting (entries tagged Loop vs Switch, with break/continue targets). `continue` must skip enclosing `switch` scopes and hit the nearest enclosing *loop*; for `for`-loops the continue target must be the increment step (needs one more label). Standard compiler-construction work, self-contained in `GenVisitor`/`AnalyzeVisitor`, no VM changes. |
| `switch`/`case`/`default`/`break` | Medium | Fully desugarable into the `if`/`else-if` chains Phlox already generates — no new bytecode ops. Real work: grammar for `switch`/`case`/`default`, case-range support (`5 ... 20`), validating non-overlapping ranges (real compile-time check), string vs integer switch typing. Builds directly on the break-target stack from the `break`/`continue` item. Each `case` in the wiki examples ends in `break` (no C-style fallthrough), which simplifies codegen. |
| `try`/`catch`/`finally` | Medium–High, the one real risk item | Not a front-end desugaring — needs actual runtime exception handling in the VM: try-region metadata in bytecode (protected range, catch-handler addresses by type — `scriptexception` vs `exception`, ordered first-match per the wiki — and a finally address); VM dispatch loop must catch CLR exceptions from built-in ops and script-level `throw`, map to the two catch categories, unwind the VM's operand/call stack to the right handler; `finally` must run on every exit path (fall-through, `return`, `break`/`continue` out of the block, state changes) — which is exactly why YEngine's wiki bans `llResetScript`/`osResetAllScripts`/`llDie` inside these blocks (those tear down the running instance in ways incompatible with normal unwind). Because Phlox's core value proposition is serializing execution state at arbitrary points, exception unwinding interacting correctly with that persistence model is the highest-risk correctness surface on this list. Needs its own design + tests, not just a ported grammar rule. Two trivial helper syscalls (`yExceptionMessage`/`yExceptionTypeName`) come along with it once the exception object model exists. |

### Suggested order if pursued
1. `&&&`/`|||` — cheap, safe, good warm-up for the grammar/codegen pattern.
2. `constant` — cheap, reuses existing `ConstantSymbol` infra.
3. `break`/`continue` in loops — medium, self-contained.
4. `switch` — medium, builds directly on #3's break-target stack.
5. `try`/`catch`/`finally` — do last, budget real design time for VM unwind +
   persistence interaction; treat as its own mini-project.

All five are additive (new keywords/operators behind a `yoptions`-style
opt-in), so — unlike the OSSL parity gap — none of it threatens existing
Phlox/SLua scripts or blocks a YEngine-retirement timeline. It's incremental
catch-up that can happen post-parity, roughly days-to-weeks per item except
try/catch which is closer to weeks.

## Open items for whoever picks this back up
- [ ] Size the OSSL parity backlog (268 functions) — group by subsystem
      (NPC/appearance, teleport, materials, formatting, admin/console, physics)
      and identify which already have equivalent region-side services Phlox can
      call into vs. which need new adapters.
- [ ] Decide whether to build an OSSL conformance suite analogous to
      `Tests/SluaProofRunner`.
- [ ] Decide on the config/pragma mechanism for opt-in language extensions in
      Phlox (mirror YEngine's `//yoptions;` line-2 pragma, or a per-engine INI
      default).
- [ ] Once OSSL parity is close, revisit whether YEngine can move to
      config-gated/non-default before removal.
