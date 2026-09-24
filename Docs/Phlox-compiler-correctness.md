# Phlox compiler: expression correctness fixes

The ANTLR4 port of the Phlox LSL compiler (`LSL.g4` plus `DefVisitor`, `TypesVisitor`,
`AnalyzeVisitor`, `GenVisitor` and `ByteCodeEmitter`) compiled several common expression forms
to the wrong bytecode without reporting an error. Scripts compiled and ran, but computed
different values from LSL (and from Halcyon's original ANTLR3 compiler). This page lists what
was wrong, what scripts computed before and after the fix, and what happens to scripts that
are already compiled.

The fixes are pinned by `Tests/InWorldz.Phlox.Tests`. These tests compile and run scripts on
the real interpreter against a recording syscall shim and compare what they say with LSL's
values (SL wiki `LSL_Operators`, `Integer`, `Typecast`, `For`):

- `ExpressionConformanceTests`: the expression matrix, each case alone and all cases together in one script.
- `ConstantLoadTests`: every `DefaultConstants` entry.
- `AssignmentStatementTypeTests`, `ForConditionAndVectorLiteralTests`.
- `SLuaExpressionTests`: the same shapes in SLua.

## What was wrong

| Defect | Cause | Before | After |
|---|---|---|---|
| Assignment used as an expression was not performed | `assignmentExpression` is `booleanExpression (op assignmentExpression)*`; the generator and type pass waited for an `assignmentExpression(1)` that never exists and emitted only a load of the target | `integer i = 5; for (i = 0; i < 3; i++) {}` leaves `i` = 5; `for (j = 0; j < 10; j += 2)` never advances (runs until stopped); `a = b = 7` leaves both unchanged; `if ((k = 5) == 5)` is false and `k` stays 0; `f(v = 21)` passes the old `v` | `i` = 3; `j` = 10 after 5 passes; `a` = `b` = 7; true, `k` = 5; passes 21 |
| `+` / `-` chosen by counting minus signs | `VisitAdditiveExpression` subtracted the first *k* pairs of a chain with *k* minus signs | `10 + 5 - 3` = 8; `a + b + c - d` (1,2,3,4) = 6; vector `a + b - c` = `a - b + c` | 12; 2; `a + b - c` |
| `&&` / `\|\|` chains dropped operands | only the first two operands were emitted, with the first operator | `1 && 1 && 0` = 1; `0 \|\| 0 \|\| 1` = 0; `1 \|\| 0 && 0` = 1; later operands never evaluated | 0; 1; 0 (the two operators share one level, left to right); every operand is evaluated (LSL does not short-circuit) |
| Mixed `\| & ^` chains used the first operator throughout | `GetBinaryOpText` returned the chain's first operator | `6 & 3 \| 8` = 0; `12 ^ 5 \| 1` = 8; `3 & 5 ^ 6` = 0 | 10; 9; 7 |
| `\| & ^` had one precedence level | the grammar (like Halcyon's) parses them as one flat left-to-right chain; LSL gives `&` higher precedence than `^`, and `^` higher than `\|` | `1 \| 2 & 0` = 0; `4 \| 1 ^ 5` = 0; `8 ^ 6 & 3 \| 1` = 3 | 1; 4; 11 (the generator re-associates the chain by precedence; operands are still evaluated in source order) |
| Integer and float constants loaded as strings | `ByteCodeEmitter.SysConstLoad` had no `iconst`/`fconst` case | `(string)DEBUG_CHANNEL` = `"0x7FFFFFFF"`; `(string)PI` = `"3.14159274"`; `[TRUE]` holds a string; `llSay(DEBUG_CHANNEL, …)`, `flags & PARCEL_FLAG_ALLOW_SCRIPTS` throw `FormatException` | `"2147483647"`; `"3.141593"`; an integer; work |
| `TOUCH_INVALID_FACE` had the wrong value | table entry `0x7FFFFFFF` | never equal to `llDetectedTouchFace`'s -1 | -1 (`0xFFFFFFFF`, as SL defines it) |
| Later pairs of a chain used the whole chain's type | after the first pair the left type became the chain's result type | `1 + 2 + [3]` and `0.5 * 2 * <1,1,1>` throw `InvalidCastException` | `[3, 3]`; `<1,1,1>` |
| `-2147483648` was 1 | the literal 2147483648 is out of range (loads as -1) and was negated at run time | 1 | -2147483648 (a minus on a literal is folded into it) |
| `-r` on a rotation did not compile | `TypesVisitor.VisitUnaryMinus` allowed integer, float and vector only | compile error | negated rotation |
| Statement assignments were not type-checked or promoted | no `TypesVisitor.VisitAssignmentStmt` | `f = 1;` stores an integer (`(string)f` = `"1"`); `v.z = 25;` throws `InvalidCastException`; `integer i; i = "abc";` and `PI = 3.0;` compile | `"1.000000"`; `25.0`; compile errors |
| A non-integer `for` condition was not converted to a boolean | the condition's type was read from the unannotated `exprStatement` | `for (; f; …)` with `f` = 0.5 never runs; a string or key condition throws | runs while non-zero / non-empty / a valid key |
| Hex components in a vector or rotation literal | the literal was folded into `vconst` text that cannot hold `0x10` | `<0x10, 0, 0>` fails at run time | `<16, 0, 0>` |

## Scripts already compiled

Compiled bytecode is cached in `ScriptEngines/Phlox/bytecode/*.plx`. The cache schema version
(`CACHE_SCHEMA_VERSION` in `Source/Phlox.ScriptEngine/PhloxScriptLoader.cs`) is now **3** (it was
2). At region start, `PhloxScriptLoader.EnsureCacheSchemaVersion` compares it with the
`.schema_version` stamp on disk. When the stamp is older, it deletes every `.plx` and writes the
new stamp, so every script is compiled once more by the fixed compiler the next time it loads.
Expect a one-time compile burst at the first region start after the upgrade.

Saved script state (`StateManager`) is matched to a script by asset id. A state saved *during*
an event (asleep in `llSleep`, parked in a syscall, or running) also holds an execution position:
the IP, call frames, operand stack and running event. That position only means something in the
bytecode it was captured on.

Each saved state now records the identity of that bytecode, `CompiledScript.BytecodeIdentity`.
This is a SHA-256 over the bytecode, the constant pool, the event table and the global count,
saved as `SerializedRuntimeState` tag 27. The identity is the same for a fresh compile and for
the copy loaded from the bytecode cache. On restore, `SerializedRuntimeState.ToRuntimeStateFor`
compares the saved identity with the script it is restoring into. If the identity differs, or
the state predates the identity and was saved mid-event (every such state at the first start
after this upgrade):

- the globals, the current LSL state, queued events, timers and listens are kept;
- the in-progress event is dropped: the running event, call frames, IP, operand stack, sleep and
  any pending syscall;
- the script resumes idle, and the engine logs one line for it:
  `[PhloxState]: <item> recompiled since its state was saved; resumed idle in state <n>, in-progress event dropped`.

A state restored onto unchanged bytecode, and a state saved idle, restore exactly as before.
Global variable slots are unchanged by these fixes, so saved globals keep their meaning.

## Behaviour left as it was

- **Evaluation order.** SL evaluates operands right to left. Phlox evaluates them left to
  right, as Halcyon did. Only expressions whose operands have side effects on each other can
  tell the two apart.
- **Group-power constants (known limitation).** The `IW_POWER_*` constants are 64-bit group
  power values stored in integer slots. An LSL integer is 32 bits, so each of them loads as -1,
  as it did in Halcyon. A script cannot test these powers with them. The power checks need a
  different representation, which is to be decided together with the group and land powers.
- **Rotation multiply.** The VM's rotation multiply negates its result
  (`ZERO_ROTATION * ZERO_ROTATION` is `<0,0,0,-1>`). This is a VM behaviour, not part of these
  fixes.
- **SLua compound assignment.** The SLua front end does not parse `a += 1` or `s ..= "x"`. SLua
  operator chains and `and` / `or` chains compute correctly.
