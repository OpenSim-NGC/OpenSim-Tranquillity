# Hosted Service Sprint Plan

## Purpose

This sprint plan operationalizes the hosted-service migration backlog into timeboxed execution.

Primary inputs:

- `Docs/HostedServiceImplementationBacklog.md`
- `Docs/HostedServiceMigrationPlan.md`

## Planning Assumptions

1. Sprint length: 2 weeks.
2. Team shape: 1 to 2 engineers actively implementing, with review support.
3. Change strategy: small PRs, each preserving runnable state.
4. Quality gate for each sprint:
- build passes for touched projects
- targeted tests pass for touched startup paths
- no uncontrolled expansion of scope
5. Capacity rule: plan approximately 70 percent feature work and 30 percent stabilization, review, and unknowns.

## Release-Level Outcomes

By the end of this sprint sequence:

1. MoneyServer, GridServer, and RegionServer startup flows are host-owned.
2. Constructor-time startup side effects are removed from migrated paths.
3. Console loops are isolated in cancellable hosted services.
4. Migrated startup and shutdown paths no longer rely on direct process exit.
5. Shared startup substrate is reused by all three executables.

## Sprint Cadence Overview

### Sprint 1: Foundations Part 1

Goal:

- establish common startup abstractions and unblock the first host-lifecycle refactors

Backlog alignment:

- A1, A2 (initial), A5 (initial), B1 (initial)

Exit criteria:

- shared startup option types are in place
- reusable ini loader exists with baseline tests
- console factory and console-context adapter exist
- direct writes to `MainConsole.Instance` are reduced in at least one migrated path

### Sprint 2: Foundations Part 2 + MoneyServer Lifecycle Start

Goal:

- complete the core compatibility layer and begin MoneyServer lifecycle correctness

Backlog alignment:

- A3, B4, C1 (phase 1), C2 (start)

Exit criteria:

- legacy Nini adapter is injectable
- startup failure coordination pattern is in place
- MoneyService constructor no longer performs major startup side effects

### Sprint 3: MoneyServer Completion

Goal:

- finish MoneyServer hosted-lifecycle behavior and establish reference pattern

Backlog alignment:

- C1 (finish), C3, C4, C5

Exit criteria:

- MoneyServer `StartAsync()` returns promptly
- console prompting is in a dedicated hosted service
- migrated MoneyServer paths no longer use direct exit calls
- host-lifetime integration tests exist and pass

### Sprint 4: Shared Hardening + GridServer Start

Goal:

- harden shared bootstrap and begin removing GridServer constructor-time boot

Backlog alignment:

- A4, A6, B2, B3, D1 (phase 1)

Exit criteria:

- shared log bootstrap and process setup services are available
- wrapper services for `MainServer`, watchdog, and work manager exist
- GridService no longer executes `Run()` or process-exit logic in constructor

### Sprint 5: GridServer Completion

Goal:

- finish GridServer host-owned startup and shutdown behavior

Backlog alignment:

- D1 (finish), D2, D3, D4, D5, D6

Exit criteria:

- GridServer listener and connector startup is explicit and host-controlled
- console loop is hosted and cancellable
- migrated GridServer paths no longer use direct exit calls
- host-lifetime integration tests exist and pass

### Sprint 6: RegionServer Host Adoption

Goal:

- move RegionServer lifecycle under generic host without full domain rewrite

Backlog alignment:

- E1, E2, E3 (start), E4 (start), E5 (start)

Exit criteria:

- RegionServer starts through `Program.cs` host bootstrap
- legacy region runtime is host-managed via adapter service
- foreground and background lifetime ownership is no longer tied to static main-loop behavior

### Sprint 7: RegionServer Composition Refactor

Goal:

- begin decomposition of inheritance-driven startup concerns

Backlog alignment:

- F1, F2, F3

Exit criteria:

- diagnostics, watchdog, HTTP startup, and plugin/script concerns have explicit service seams

### Sprint 8: RegionServer Completion + Cross-Cutting Cleanup

Goal:

- finalize RegionServer migration and complete cross-cutting checks

Backlog alignment:

- F4, F5, X1, X2, X3

Exit criteria:

- RegionServer host-lifetime integration tests pass
- migrated startup and shutdown documentation is up to date
- exit-call audit and regression checklist are complete

## Sprint 1 Detailed Plan

## Sprint Goal

Stand up the minimum shared startup substrate needed to start lifecycle-safe migrations without immediately rewriting all server internals.

## Committed Scope

### Ticket S1-01: Shared startup option models

Backlog mapping:

- A1

Tasks:

- create common startup option types
- split common versus server-specific option definitions
- add parser or binder helper used by MoneyServer and GridServer

Acceptance criteria:

- one shared common-options model is used by both servers
- defaults for common switches are centralized

### Ticket S1-02: Reusable ini loader baseline

Backlog mapping:

- A2 (initial)

Tasks:

- implement base ini loading pipeline for `inimaster`, `inifile`, `inidirectory`
- implement deterministic precedence rules
- add unit tests for precedence and missing-file behavior

Acceptance criteria:

- loader can replace duplicated ini loading in at least one `Program.cs`
- tests cover the primary load order

### Ticket S1-03: Console factory and context adapter

Backlog mapping:

- A5 (initial)

Tasks:

- add console factory abstraction for `basic`, `local`, `rest`, `mock`
- add console context adapter that controls `MainConsole.Instance`
- wire into one server startup path as first consumer

Acceptance criteria:

- at least one server obtains console instance from factory
- static console assignment goes through adapter

### Ticket S1-04: Early static-console wrapper adoption

Backlog mapping:

- B1 (initial)

Tasks:

- identify direct `MainConsole.Instance` writes in startup path targeted this sprint
- route those writes through the new adapter
- document remaining direct assignments for later sprints

Acceptance criteria:

- no new direct `MainConsole.Instance` writes are introduced
- migration notes include remaining direct references

## Stretch Scope

### Ticket S1-05: Begin Nini adapter skeleton

Backlog mapping:

- A3 (prep only)

Tasks:

- define adapter interface and registration shape
- no full adoption required in Sprint 1

Acceptance criteria:

- interface and DI wiring are ready for Sprint 2 completion

## Non-Goals For Sprint 1

1. No full MoneyService constructor refactor yet.
2. No GridService constructor-time boot removal yet.
3. No RegionServer entry-point replacement yet.
4. No broad watchdog or work manager lifecycle changes yet.

## Sprint 1 PR Plan

1. PR 1: option model extraction and parser helper.
2. PR 2: reusable ini loader with tests.
3. PR 3: console factory and console context adapter.
4. PR 4: first consumer wiring and static-console wrapper adoption.

## Sprint 1 Risk Controls

1. Keep legacy behavior parity by preserving option names and defaults.
2. Add tests before replacing existing ini-loading call sites.
3. Limit first-adoption scope to one server path to reduce blast radius.
4. Defer any large inheritance refactors until lifecycle seams are stable.

## Sprint 1 Validation Checklist

1. Build passes for modified projects.
2. Option parsing behavior matches existing command-line expectations.
3. Ini precedence behavior is validated by tests.
4. Console type selection still honors existing values.
5. No regression in startup logging for the touched server path.

## Cadence and Ceremonies

1. Planning: day 1, confirm committed versus stretch scope.
2. Mid-sprint checkpoint: day 5, validate adoption strategy and cut scope if needed.
3. End-sprint review: day 10, demo startup flow and publish migration notes.
4. Retrospective focus: identify blockers for Sprint 2 MoneyService lifecycle work.

## Definition Of Ready For Sprint 2

1. Shared option and ini-loader APIs are stable enough for reuse.
2. Console factory and context adapter are available in DI.
3. Remaining direct static-console assignments are inventoried.
4. Nini adapter interface contract is agreed.

## Definition Of Done For The Sprint Plan

This sprint plan is considered actionable when:

1. Sprint 1 tickets are created from S1-01 through S1-04.
2. Owners and estimates are assigned for committed scope.
3. PR sequence is accepted by the team.
4. Sprint 2 entry criteria are explicitly tracked.