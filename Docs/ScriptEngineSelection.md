<!-- Copyright (c) 2026 Legion Builds. This Source Code Form is subject to the terms of the Mozilla Public
     License, v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain one at
     https://mozilla.org/MPL/2.0/. -->
# Which script engine runs a script

A region can load several script engines (YEngine, Phlox). When a script starts, the region picks
the engine in this order and stops at the first step that gives an answer:

0. **The viewer's compile target.** A script saved from a viewer's script editor with the compiler "Lua" (`luau`)
   or "LSL on Lua" (`lsl-luau`) carries that choice with its item, and the SLua engine takes it ahead of the
   first line, as described under "The viewer's compile target" below. Saved state for the item and asset still
   places a script that already runs on another engine. A script with no such choice skips this step.
1. **The first line.** `//<engine>:<language>` names an engine loaded in the region: its engine name
   (`InWorldz.Phlox`, `YEngine`) or a header alias the engine lists (`//Phlox:` for Phlox). Names are
   compared as written.
2. **Saved state.** The engine that holds saved state for the script's item and asset keeps it, the
   `DefaultScriptEngine` first. A running script therefore stays on its engine across a restart, whatever the
   settings below say. Editing a script gives it a new asset and so no saved state.
3. **A claim.** A script with source is offered to each engine that can claim scripts (an engine that implements
   `IScriptEngineSelection`), in the order the engines are loaded; the first to say yes gets it. Phlox and YEngine
   do not claim scripts.
4. **The default.** `[Startup] DefaultScriptEngine`. Two files give it a value, and they differ: the shipped
   `Source/OpenSim.Server.RegionServer/AppData/RegionServerDefaults.ini` sets `DefaultScriptEngine = "InWorldz.Phlox"`,
   while the code default, used when no configuration sets the key (`Source/OpenSim.Region.Framework/Scenes/Scene.cs`),
   is `YEngine`. A region started from the shipped defaults file therefore defaults to Phlox; a host that supplies
   its own configuration without the key defaults to YEngine.

An engine that is not a script-selection participant is unaffected: it is chosen by name, by saved state or as
the default.

## Early access engines

A script engine may mark itself early access (`IScriptModule.EarlyAccess`; an engine is not early access unless it
says so). The region lists the early-access engines it runs, by engine name, in the `SimulatorFeatures` capability
as `OpenSimExtras["ScriptEngineEarlyAccess"]`, an array of strings, beside `OpenSimExtras["ScriptEngines"]`, which
still lists every engine. A region that runs no early-access engine does not send the key.

**An early-access engine may not keep script state across future updates until it is declared final.** After an
update of the region, scripts that run on such an engine may start fresh: their global variables and current state
are not guaranteed to survive.

For this list, the Lua compile targets (`luau` and `lsl-luau`) count as the engine named `SLua`: when `SLua` is
listed as early access, a viewer can treat both targets as early access. The region does not tie the targets to an
engine name; a target goes to whichever loaded engine accepts it (see "The viewer's compile target").

## The viewer's compile target

Second Life's script editor has a compiler list: LSL Legacy (`lsl2`), LSL on Mono (`mono`), LSL on Lua
(`lsl-luau`) and Lua (`luau`). It sends the choice as the `target` field of the script upload capabilities
(`UpdateScriptTask` for a script in an object, `UpdateScriptAgent` for one in the avatar's inventory).

- `luau` and `lsl-luau` are kept with the script item, in its flags, so no database column is added and the choice
  survives a failed compile. `luau` is the script subtype Second Life's viewer defines for Lua (the value 1 in the
  low byte of the flags). `lsl-luau` is not a subtype the viewer defines, so it is a flag bit of its own (0x01000000)
  and the subtype byte stays that of an LSL script; the region never sends that bit to a viewer. The SLua
  engine compiles the script as the language the choice names (Lua, or LSL on the Luau virtual machine), whatever
  its first line says; a first line naming another engine is ignored for that script.
- A script that carries a choice is started by one engine only, the one the region selected. An engine that
  implements `IScriptEngineSelection` hears every script start and lets go of a script that is not its own. An
  engine that cannot read the choice (YEngine reads only the first line) is not told of a script that carries one
  unless it is the engine selected, so a script whose first line names it, saved with `luau`, runs on the selected
  engine alone.
- `mono` and `lsl2` make no explicit choice. They clear an earlier `luau` or `lsl-luau`, and the first line, a
  claim and the default decide as described above, so a viewer that only offers `mono` and `lsl2` behaves as before.
  A save that sends no `target` leaves the stored choice as it was.
- The choice is honoured only while the SLua engine is enabled and running; otherwise the script goes where the
  first line, saved state and the default put it, and the stored choice waits.
- When the editor asks whether a script is running (`GetScriptRunning`), the reply (`ScriptRunningReply`) carries
  `Mono`, `Luau` and `LuauLanguage` as the editor reads them, so its compiler list shows the saved choice. The
  SLua engine answers for the scripts it runs; for a script without a stored choice it reports the language the
  script was compiled as.

## Lua in the viewer

A viewer enables the Lua entries of its compiler list, and creates new scripts with the Lua subtype, when the
`SimulatorFeatures` capability has `LuaScriptsEnabled` set to true. The SLua engine adds that entry when
`[SLua] Enabled` is true and the engine is working (its runtime loaded, and the region's world communication
present); with the engine off the capability is as it was.

A new script created with the Lua subtype, in an object or in the avatar's inventory, gets a Lua default script
instead of the LSL one (it says "Script running" on channel 0 when it starts, as the LSL default does), and the
item keeps the Lua subtype. The Lua default is used only when a loaded engine takes Lua scripts; otherwise the
LSL default is kept and the item has no Lua subtype.
