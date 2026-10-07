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
- [ ] **Batch 2 / Tier 2: obsolete production APIs - 39 closed, 27 remaining**
  - [x] **2a: compiler, rendering, TLS and certificate loading - 39 closed**
    - CS0618 (34), SYSLIB0039 (4), SYSLIB0057 (1).
  - [ ] **2b: shared HTTP transport and startup defaults - 14**
    - SYSLIB0014: Framework (1), Framework.Servers.HttpServer (4),
      Framework.Servers (1), Server.Base (5), GridServer (1),
      RegionServer (1), ConsoleClient (1).
    - Preserve authorization, timeouts, callback outcomes, certificate policy
      and process settings when migrating away from WebRequest/ServicePointManager.
  - [ ] **2c: downloads and service connectors - 6**
    - SYSLIB0014: CoreModules (2), Services.Connectors (2), LLLoginService (1),
      ApplicationPlugins.LoadRegions (1).
    - Preserve streaming ownership, file handling, status-code handling and retries.
  - [ ] **2d: optional voice/XML-RPC/broker and payment transports - 7**
    - SYSLIB0014: OptionalModules (5), GloebitMoneyModule (2).
    - Exercise payment callbacks and failures against local fixtures; do not
      contact live payment services for validation.
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

After Batch 1 the reconciled inventory was 661 occurrences: 264 closed and
397 remaining. After Batch 2a it is **661 occurrences: 303 closed and 358 remaining**.
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

## Batch 2a implementation and verification

The user selected a contained 39-warning sub-batch before the wider HTTP
transport migrations, and selected operating-system TLS defaults for script HTTP.

- Compiler definition and branch locations now reference ANTLR4 `IToken`
  directly, rather than constructing obsolete `LSLAst` compatibility objects.
  Semantic data remains in `LSLNodeAnnotations`. Tests pin duplicate-definition
  and missing-return messages to the identifier's original line and column.
  `Symbol.Def` and branch node/constructor types are a source/binary API change
  for external compiler consumers; those consumers must update and rebuild.
- Skia drawing uses explicit `SKSamplingOptions.Default`, preserving the previous
  default sampling, and `SKPathBuilder` for polygon construction. Tests verify
  opacity, filled/stroked polygons, closed edges and dynamic texture integration.
- Both script HTTP handlers use `SslProtocols.None` (OS policy) instead of
  explicitly enabling TLS 1.0/1.1. Certificate verification flags are unchanged.
  Legacy TLS-only endpoints may stop working; upgrade them or review OS policy,
  rather than silently re-enabling deprecated protocols in application code.
- HTTP certificates use `X509CertificateLoader`, retaining both PKCS#12
  (with private key/password) and certificate-only input. Loading errors retain
  their underlying exception. Tests cover correct/empty/wrong PKCS#12 passwords,
  certificate identity, private keys and hostname matching.

Verification:

- Full non-incremental Release rebuild: **358 warnings, 0 errors**.
- CS0618, SYSLIB0039, SYSLIB0057 and CS0436: **0 remaining**.
- No new diagnostic messages compared with the Batch 1 inventory.
- Selected CoreModules tests: **80 passed**, including TLS 1.2/1.3 loopback
  requests for both `HTTP_VERIFY_CERT` modes.
- Selected Phlox compiler/drawing tests: **85 passed**.

On Linux, these test outputs do not currently copy the Skia native asset.
The first test runs failed with `DllNotFoundException`; rerunning with the
matching, already cached Skia 4.151.1 library passed. No packages were changed.
For this checkout the repeatable commands are:

```sh
export LD_LIBRARY_PATH="/var/opt/opensim/.nuget/packages/skiasharp.nativeassets.linux/4.151.1/runtimes/linux-x64/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
dotnet test Tests/OpenSim.Region.CoreModules.Tests/OpenSim.Region.CoreModules.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~ModernSkiaRenderingTests|FullyQualifiedName~HttpCertificateLoadingTests|FullyQualifiedName~HttpMimeTypeTests|FullyQualifiedName~VectorRenderImageFilterTests|FullyQualifiedName~PrimMesherIdentityTests|FullyQualifiedName~OutboundWiringTests'
dotnet test Tests/InWorldz.Phlox.Tests/InWorldz.Phlox.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~CompilerHarnessTests|FullyQualifiedName~CompileTimeTypeErrorTests|FullyQualifiedName~UseBeforeDefineTests|FullyQualifiedName~BuiltinOverloadTests|FullyQualifiedName~CompilerCrashTests|FullyQualifiedName~OsslDrawTests|FullyQualifiedName~AsyncCommandIdentityTests'
```
