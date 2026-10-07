# Build warning cleanup

Original baseline: Release build of `Tranquillity.sln` on .NET SDK 10.0.112,
648 warning occurrences, 0 errors. Counts identify diagnostic occurrences by
project, file, position, code and message; the build summary repeats diagnostics
and must not be counted again.

These tiers are cleanup priorities, not compiler-assigned severity or confirmed
vulnerabilities. Review each occurrence before changing behavior. Close a batch
only after its diagnostics disappear from a full rebuild and relevant tests pass;
do not close warnings by blanket suppression.

## Ranked task list

- [x] **Batch 1 / Tier 1: duplicate type identities - 264 closed, 0 remaining**
  - CS0436: Meshing (151), ubODEMeshing (84), Phlox.ScriptEngine (29).
  - Isolate implementation namespaces without replacing meshing algorithms or
    Phlox's adapted async plugins with upstream implementations.
- [ ] **Batch 2 / Tier 2: obsolete production APIs - 66**
  - CS0618 (34), SYSLIB0014 (27), SYSLIB0039 (4), SYSLIB0057 (1).
  - Review replacements and TLS policy; preserve intended interoperability.
- [ ] **Batch 3 / Tier 2: correctness and call-contract diagnostics - 68**
  - CA2017 (26), CA2022 (10), CA2023 (1), CS0114 (8), CS0108 (1),
    CS0659 (1), CS0649 (2), CS9192 (9), CS9193 (10).
  - Prioritize partial reads, logging templates and equality/hash contracts,
    then review inheritance, initialization and ref/in calls.
  - Counts include test occurrences; review production occurrences first.
- [ ] **Batch 4 / Tier 3: production nullability - 13**
  - ExperienceService's CS8600, CS8603, CS8602 and CS8625 occurrences.
  - Promote possible runtime failures to correctness priority.
- [ ] **Batch 5 / Tier 3: test nullability - 181**
  - CS8600/01/02/03/05, CS8610/18/19, CS8620/25, CS8765/67.
  - Fix fixture initialization and nullable contracts in project-sized batches.
- [ ] **Batch 6 / Tier 3b: xUnit analyzers - 32**
  - xUnit2013 (22), xUnit1031 (6), xUnit2017 (2), xUnit2029 (1),
    xUnit2009 (1).
  - Preserve assertion meaning and remove blocking async test operations.
- [ ] **Batch 7 / Tier 4: cosmetic, documentation and dead code - 37**
  - CS3021 (12), CS8981 (12), CS0067 (5), CS0414 (3), CS0168 (2),
    CS1573 (3).
  - Check generated-source ownership before editing or removing declarations.

The original analysis overcounted nullability by two: the verified total is
194 (13 production + 181 test).

The Batch 1 non-incremental rebuild exposed 13 additional occurrences absent
from the original log, all in unchanged files:

| Project | Code | Additional occurrences | Batch |
| --- | --- | ---: | ---: |
| OpenSim.Framework | SYSLIB0014 | 1 | 2 |
| OpenSim.Framework | CA2017 | 2 | 3 |
| OpenSim.Framework | CS0168 | 1 | 7 |
| OpenSim.Framework.Servers.HttpServer | SYSLIB0014 | 4 | 2 |
| OpenSim.Framework.Servers.HttpServer | SYSLIB0057 | 1 | 2 |
| OpenSim.Framework.Servers.HttpServer | CA2022 | 1 | 3 |
| OpenSimNGC.Appearance.Baking | CS1573 | 3 | 7 |

Thus the reconciled inventory is **661 occurrences: 264 closed and 397 remaining**.
Use the current counts above for future batches, rather than subtracting 264
from the incomplete original 648-warning baseline.

## Batch 1 implementation

The two physics meshers contain different PrimMesher implementations. Both formerly
used the upstream `PrimMesher` namespace also exported by
`OpenMetaverse.Rendering.Meshmerizer`. Keep the local implementations, but place
them under `OpenSim.Region.PhysicsModules.Meshing.PrimMesher` and
`OpenSim.Region.PhysicsModules.ubODEMeshing.PrimMesher`.

Phlox's adapted async manager and sensor/HTTP/XML-RPC plugins formerly used
`OpenSim.Region.ScriptEngine.Shared.Api` and `.Plugins`, colliding with YEngine's
shared implementations. Their namespaces are now `Phlox.ScriptEngine.AsyncCommand`
and `.Plugins`; Phlox construction and script cleanup must use its own manager.

Module class names, INI selections, script events and meshing algorithms are
unchanged. These helper type names are a source/binary API change: external code
directly referencing the old helper types must update its namespace and rebuild.
No compatibility wrappers are provided, since they would recreate the collisions.

### Verification

- Full rebuild: `dotnet build Tranquillity.sln -c Release --no-incremental`
  succeeds with 397 warnings, 0 errors, and **0 CS0436 occurrences**.
- Mesher regression tests: 3 passed (exported type identity isolation, unit-box
  dimensions and valid triangle indices for both meshers).
- Selected Phlox tests: 37 passed (async manager/plugin identities, script
  cleanup, sensors, and cross-engine HTTP/XML-RPC event delivery).

Repeat the targeted tests with:

```sh
dotnet test Tests/OpenSim.Region.CoreModules.Tests/OpenSim.Region.CoreModules.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~PrimMesherIdentityTests'
dotnet test Tests/InWorldz.Phlox.Tests/InWorldz.Phlox.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~AsyncCommandIdentityTests|FullyQualifiedName~ScriptCleanupTests|FullyQualifiedName~BotDetectTests|FullyQualifiedName~DetectDataSourceTests|FullyQualifiedName~PhloxCrossEngineHttpResponseTests'
```
