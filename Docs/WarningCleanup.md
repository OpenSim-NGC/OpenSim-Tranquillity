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
- [x] **Batch 2 / Tier 2: obsolete production APIs - 66 closed, 0 remaining**
  - [x] **2a: compiler, rendering, TLS and certificate loading - 39 closed**
    - CS0618 (34), SYSLIB0039 (4), SYSLIB0057 (1).
  - [x] **2b: shared HTTP transport and startup defaults - 14 closed**
    - SYSLIB0014: Framework (1), Framework.Servers.HttpServer (4),
      Framework.Servers (1), Server.Base (5), GridServer (1),
      RegionServer (1), ConsoleClient (1).
    - Preserve authorization, timeouts, callback outcomes, certificate policy
      and process settings when migrating away from WebRequest/ServicePointManager.
  - [x] **2c: downloads and service connectors - 6 closed**
    - SYSLIB0014: CoreModules (2), Services.Connectors (2), LLLoginService (1),
      ApplicationPlugins.LoadRegions (1).
    - Preserve streaming ownership, file handling, status-code handling and retries.
  - [x] **2d: optional voice/XML-RPC/broker and payment transports - 7 closed**
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
397 remaining. After Batch 2 it is **661 occurrences: 330 closed and 331 remaining**.
Use the current counts above for future batches, rather than subtracting 264
from the incomplete original 648-warning baseline.

## Batch 2b implementation and verification

### Review follow-up verification

- Restored the three-minute default lifetime in process setup and both server
  startup paths; explicitly configured lifetimes, including -1 and 0, remain supported.
- Separated pooled legacy/system-proxy clients from existing direct grid-service
  clients, with strict HELO/welcome-message certificate checks and visible console
  request errors. Script HTTP still uses OS TLS policy and its existing proxy/filter.
- Release solution build succeeds with 0 errors (incremental build; not a new
  warning-inventory baseline).
- Selected transport, archive/terrain, outbound and TLS regressions: **140 passed**.
  Selected startup/defaults and server lifecycle regressions: **26 passed**.
  Coverage includes system proxies with direct shared handlers, optional-module
  requests, voice HTTPS CONNECT, rejection of an untrusted HELO certificate despite
  shared verification bypasses, and console failures without a logging host.
- All proxy/TLS tests use local fixtures; no live grid, voice or payment endpoints
  were contacted.

### Original implementation

- Shared REST posters, session posters, asynchronous XML requests and the console
  client now use HttpClient with shared, policy-configured handlers. XML/form
  encoding, authorization, session envelopes and 10/20/100-second timeouts remain.
- Asynchronous requester failures are logged and complete once with the default
  response; poster/console success callbacks are not invoked on failure. Console
  request failures are printed to stderr without requiring a logging host; failed
  polls explicitly report that polling stopped and reconnection is needed. Callback
  exceptions are logged instead of escaping an unobserved asynchronous callback.
  Synchronous session errors propagate as HttpRequestException rather than WebException.
- Startup connection limits and idle timeouts now configure SocketsHttpHandler.
  The user approved mapping DnsTimeout to pooled-connection lifetime: expiration
  retires connections so subsequent connections resolve DNS again; this is not a
  process-wide DNS-cache TTL and does not abort active requests. -1 disables expiry,
  and 0 disables reuse. Process setup and server Startup defaults are 180000
  milliseconds, preserving the previous three-minute shared connection lifetime.
  An explicitly configured DnsTimeout now affects pooling, unlike the old
  ServicePointManager DNS setting.
- TCP_NODELAY remains enabled. UseNagleAlgorithm=true is unsupported and explicitly
  logged. Certificate chain/hostname settings remain on the shared handlers, not a
  process-global ServicePointManager callback.
- Full non-incremental Release rebuild: **344 warnings, 0 errors**; exactly
  **14 SYSLIB0014 removed**, no added diagnostic messages.
- Selected CoreModules transport/outbound tests: **84 passed**. Selected startup,
  configuration and host lifecycle tests: **17 passed**.
- Live integration of Batch 2a was reported good except for a sit setter package
  compilation issue that the user will address separately; its cause is unverified.

## Batch 2c/2d implementation and verification

- Archive and terrain downloads stream through a response-owning wrapper. Disposing
  the returned stream disposes the HTTP response and client; terrain URI loading
  now disposes its download on both success and failure. Header timeout remains
  100 seconds and each streamed read has a five-minute timeout. Empty files and
  failed statuses still fail explicitly; unknown-length responses remain supported.
- Map-image downloads use a temporary file beside the destination and replace it
  only after a complete transfer. Failed transfers clean up their own temporary
  file instead of leaving a truncated cache entry or destroying an existing file.
- HELO still uses GET and reads X-Handlers-Provided. Welcome messages retain their
  configured fallback, now with an explicit warning on download failure. Region
  loading retains its 30-second timeout, three attempts/two-second waits for empty
  results, and permits HTTP 404 only when allow_regionless is enabled.
- Migrated WebRequest/WebClient callers retain system/environment proxy behavior
  through separate pooled handlers. Existing direct grid-service and script HTTP
  transports are unchanged. Explicit shared proxies also apply to legacy callers.
  HELO and welcome-message fetches use strict certificate chain/hostname validation,
  independent of shared NoVerifyCertChain/NoVerifyCertHostname bypasses. This preserves
  Robust's previous checks and intentionally also enforces them in region hosting.
- Groups and money XML-RPC preserve their wire encoding, keep-alive choices,
  verification header and client certificates. Request failures now propagate
  HttpRequestException/OperationCanceledException to existing error handling.
  NSL's NoVerifyCert header is still a header, not a new TLS-policy override.
- Vivox and FreeSwitch retain their existing module-specific certificate policies
  in dedicated, pooled handlers, disposed when modules close. Those policies do
  not weaken the shared handler's certificate checks. OS TLS policy still applies.
- Concierge broker posts now use UTF-8 byte lengths and task-based cancellation;
  failures are explicitly logged without leaking abort timers.
- Gloebit request building no longer sends network data synchronously. Async
  responses are completely read and validated as JSON objects before one
  continuation is invoked. HTTP/network/parse/callback failures are logged, with
  no success callback fabricated. Existing domain failure maps still reach the
  continuation; payment decisions and application retry logic are unchanged.
  As before, a transport failure does not invent a financial completion result.
- The old public GloebitWebResponseCallback APM implementation was removed;
  external code directly referencing that helper must update and rebuild.
- Search/mutelist XML-RPC requests now pass the shared HttpClient into the
  library's existing overload so startup certificate/proxy policy stays consistent
  after removal of the process-global certificate callback.
- Full non-incremental Release rebuild: **331 warnings, 0 errors**.
  Relative to Batch 2a, exactly **27 SYSLIB0014 removed**, with no added diagnostic
  messages. CS0436, CS0618, SYSLIB0014, SYSLIB0039 and SYSLIB0057 are all zero.
- Selected CoreModules tests: **128 passed** across REST/callbacks, real
  connection-limit thresholds, downloads/retries, archive/terrain regressions,
  outbound filtering, TLS, module-specific transport and local payment fixtures.
  Selected process/configuration/grid/region/money host lifecycle tests:
  **23 passed** (**151 selected tests total**).
  Money client-certificate and FreeSwitch HTTPS CONNECT tests use local TLS servers.
  No live voice/payment endpoints were contacted; no new live integration tests
  or deployment were performed by the assistant.

Repeat the transport tests with the cached Linux Skia asset:

```sh
LD_LIBRARY_PATH=/var/opt/opensim/.nuget/packages/skiasharp.nativeassets.linux/4.151.1/runtimes/linux-x64/native \
dotnet test Tests/OpenSim.Region.CoreModules.Tests/OpenSim.Region.CoreModules.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~SharedRestTransportTests|FullyQualifiedName~HttpDownloadTests|FullyQualifiedName~OptionalHttpTransportTests|FullyQualifiedName~Archiver|FullyQualifiedName~Terrain|FullyQualifiedName~HttpMimeTypeTests|FullyQualifiedName~OutboundWiringTests|FullyQualifiedName~XmlRpcOutboundFilterTests'
```

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
