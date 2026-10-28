<!-- Copyright (c) 2026 Legion Builds. This Source Code Form is subject to the terms of the Mozilla Public
     License, v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain one at
     https://mozilla.org/MPL/2.0/. -->
# Which script engine runs a script

A region can load several script engines (YEngine, Phlox). When a script starts, the region picks
the engine in this order and stops at the first step that gives an answer:

1. **The first line.** `//<engine>:<language>` names an engine loaded in the region: its engine name
   (`InWorldz.Phlox`, `YEngine`) or a header alias the engine lists (`//Phlox:` for Phlox). Names are
   compared as written.
2. **Saved state.** The engine that holds saved state for the script's item and asset keeps it, the
   `DefaultScriptEngine` first. A running script therefore stays on its engine across a restart, whatever the
   settings below say. Editing a script gives it a new asset and so no saved state.
3. **A claim.** A script with source is offered to each engine that can claim scripts (an engine that implements
   `IScriptEngineSelection`), in the order the engines are loaded; the first to say yes gets it. Phlox and YEngine
   do not claim scripts.
4. **The default.** `[Startup] DefaultScriptEngine`.

An engine that is not a script-selection participant is unaffected: it is chosen by name, by saved state or as
the default.
