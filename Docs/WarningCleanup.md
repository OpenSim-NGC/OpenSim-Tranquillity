# Build warning cleanup

## Current dependency-update baseline

On the dependency-update branch at `a225f5581b`, a non-incremental Release
solution build with .NET SDK 10.0.115 reported 120 warnings and 242 errors.
After the first test cleanup it reported 120 warnings and 216 errors.
Adapting the Jolt backend and Jolt tests to the pinned JoltPhysicsSharp 2.26
API removed the eight backend compilation errors and three readonly-reference
test call-contract errors. A refreshed solution build reports 120 warnings and
208 errors, concentrated in test-project xUnit migration, nullable fixture
initialization and inherited test-double methods. The solution does not yet
build.

The follow-up cleanup resolves the remaining warning-level nullable diagnostics
in Release, but does not make the solution warning-free. Release promotes
warnings to errors through `TreatWarningsAsErrors` in `Directory.Build.props`;
its zero-warning summary must not be interpreted as a completed cleanup.
The committed follow-up reported **0 warnings and 179 errors** in Release:

```sh
dotnet build Tranquillity.sln -c Release --no-restore --no-incremental -m:1
```

After adding the missing `System.Reflection` import in
`OSSL_ApiSetScriptEngineTests.cs`, non-incremental builds on .NET SDK 10.0.115
report **178 warnings and 0 errors in Debug**, and **0 warnings and 178 errors
in Release**. All 178 diagnostics are in test projects. The Debug log prints
each warning again in its summary (356 warning lines, 178 unique diagnostics).

```sh
dotnet build Tranquillity.sln -c Debug --no-restore --no-incremental -m:1
dotnet build Tranquillity.sln -c Release --no-incremental -m:1
```

The remaining diagnostics include hidden test-double members (CS0108, 56),
nullable fixture initialization (CS8618, 52), xUnit analyzers (52), nullable
annotations outside an enabled context (CS8632, 11), null arguments (CS8625, 4),
obsolete HTTP APIs (SYSLIB0014, 2), and nullable value access (CS8629, 1).
Release still cannot build; no tests were run for this follow-up.

The Jolt native package is also pinned consistently to 1.3.0:
`JoltPhysics.Native`, managed API and native hash allowlist now agree. The native
allowlist tests pass 35/35. The full Jolt test project reports 1316 passed,
2 skipped and 5 failed. The failures are native-behavior changes in hold/resume,
terrain jitter, rough-ground vehicle/box response and mesh-asset settling, not
compiler or hash-record failures. Their thresholds were not weakened.

The first cleanup moves inventory-handler scene initialization into the
fixture methods' return values and initializes framework test fixtures in their
constructors. It also corrects nullable theory parameters and uses asynchronous
task completion in the rate-limited log tests. The full Release test projects
passed: `OpenSim.Capabilities.Handlers.Tests` 11 passed, 0 failed, 0 skipped;
`OpenSim.Framework.Tests` 267 passed, 0 failed, 0 skipped.

The framework package migration now replaces xUnit v2 with `xunit.v3` 4.0.3
in 35 projects, including mixed NUnit/xUnit assemblies and the NUnit AIS
harness, whose shared helpers require an xUnit app host. NUnit tests remain
NUnit, and VSTest discovery and TRX reporting are retained. The shared fixture
uses `ValueTask` lifecycle methods and forwards async disposal to existing
synchronous teardown overrides; its regression test fails without that
forwarding and the seven shared tests pass with it.

New v3 cancellation-token, custom-Fact source-location and nullable-return
diagnostics were resolved without suppressions. The non-incremental Debug
build still reports **178 warnings and 0 errors**, and Release still reports
**0 warnings and 178 promoted errors**. This migration does not close the
remaining warning backlog. Six migrated projects outside the solution remain
blocked at restore by pre-existing direct-package downgrades against
`OpenSim.Tests.Common`; their test bodies could not be validated.

The full Debug test run recorded **15416 passed, 1963 failed and 102 skipped
or not executed**, with no timeout abort. Phlox completed with 5951 passed,
0 failed and 2 skipped. The suite is not green: NUnit voice tests reject
their timeout attributes, physics and UDP tests fail, and CoreModules reports
foreground threads left running after its 1004 tests pass. The AIS app-host
compatibility fix was then verified separately: 232 passed, 0 failed, 0 skipped.
Child-process physics diagnostics now use `ITestOutputHelper`, preserving the
output their parent tests parse under v3. No failing assertions or known-failure
entries were weakened.

## Earlier cleanup baseline

Original baseline: Release build of `Tranquillity.sln` on .NET SDK 10.0.112,
648 warning occurrences, 0 errors. Counts identify diagnostic occurrences by
project, file, position, code and message; the build summary repeats diagnostics
and must not be counted again.

These tiers are cleanup priorities, not compiler-assigned severity or confirmed
vulnerabilities. Review each occurrence before changing behavior. Close a batch
only after its diagnostics disappear from a full rebuild and relevant tests pass;
do not close warnings by blanket suppression.

Closeout: full non-incremental Release and Debug rebuilds of `Tranquillity.sln`
are **warning-free (0 warnings, 0 errors)** on .NET SDK 10.0.112. All seven
batches are closed. This inventory covers projects built by the solution,
not every standalone project in the repository; see the excluded-project
backlog below.

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
- [x] **Batch 3 / Tier 2: correctness and call-contract diagnostics - 68 closed, 0 remaining**
  - [x] **3a: partial reads, malformed templates and the hash contract - 12 closed**
    - CA2022 (10), CA2023 (1), CS0659 (1).
  - [x] **3b: logging message templates - 26 closed**
    - CA2017 (26).
  - [x] **3c: inheritance and initialization - 11 closed**
    - CS0114 (8), CS0108 (1), CS0649 (2).
  - [x] **3d: ref/in call contracts - 19 closed**
    - CS9192 (9), CS9193 (10).
  - Counts include test occurrences; review production occurrences first.
- [x] **Batch 4 / Tier 3: production nullability - 13 closed, 0 remaining**
  - ExperienceService's CS8600, CS8603, CS8602 and CS8625 occurrences.
- [x] **Batch 5 / Tier 3: test nullability - 181 closed, 0 remaining**
  - CS8600/01/02/03/05, CS8610/18/19, CS8620/25, CS8765/67.
  - Fix fixture initialization and nullable contracts in project-sized batches.
- [x] **Batch 6 / Tier 3b: xUnit analyzers - 32 closed, 0 remaining**
  - xUnit2013 (22), xUnit1031 (6), xUnit2017 (2), xUnit2029 (1),
    xUnit2009 (1).
  - Preserve assertion meaning and remove blocking async test operations.
- [x] **Batch 7 / Tier 4: cosmetic, documentation and dead code - 33 closed, 0 remaining**
  - CS3021 (12), CS8981 (12), CS0067 (1), CS0414 (3), CS0168 (2),
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
397 remaining. After Batch 2 it was 661 occurrences: 330 closed and 331 remaining.
After Batch 3a it was 661 occurrences: 342 closed and 319 remaining.
After Batch 3b it was 661 occurrences: 368 closed and 293 remaining.
After Batch 3c it was 661 occurrences: 379 closed and 282 remaining.
After Batch 3d it was 661 occurrences: 398 closed and 263 remaining.
After Batch 4 it was 661 occurrences: 411 closed and 250 remaining.
After Batch 5 it was 661 occurrences: 596 closed and 65 remaining.
After Batch 6 it was 661 occurrences: 628 closed and 33 remaining.
After Batch 7 it is **661 occurrences: 661 closed and 0 remaining**:
658 corrected and three accepted, declaration-specific compatibility exceptions.
Use the current counts above for future batches, rather than subtracting 264
from the incomplete original 648-warning baseline.

## Batches 6 and 7 implementation and closeout

Reassessment of `develop` at `588be916a0` (merged Batches 4/5 and intervening
bug fixes) found exactly the expected **65 warnings, 0 errors** in a full
non-incremental Release rebuild. There were no added solution diagnostic
occurrences or warning codes.

Batch 6 replaces collection-size, membership and substring boolean assertions
with xUnit's `Empty`, `Single`, `Contains` and `DoesNotContain` assertions,
preserving the expected contents and counts. Four Phlox tests now return
`async Task` and await results rather than reading `Task.Result`. They still
drive the scheduler with bounded synchronous pumping until each task completes
before awaiting it, preserving the scheduler-driving thread and timeout checks.
All 32 xUnit diagnostic occurrences are gone.

Batch 7:

- Declares the Phlox compiler assembly non-CLS-compliant, matching its generated
  ANTLR types. All 12 CS3021 occurrences disappear without editing generated
  parsers or changing the grammar regeneration workflow.
- Replaces ten lowercase internal type aliases with the existing `LSL_Key`,
  `LSL_Rotation` convention and `LSL_Vector`. The underlying CLR types, public
  signatures, parameter names and script-facing names are unchanged.
- Retains the public `LSL_Types.list` and `LSL_Types.key` names for compiled
  scripts and serialized-state compatibility. With user approval, CS8981 is
  disabled only around each declaration, then immediately restored.
- Retains Phlox's required `IScriptModule.OnObjectRemoved` event and existing
  subscription behavior. As in YEngine, the unused-event diagnostic is handled
  locally: CS0067 is disabled only around that declaration, then restored.
  No speculative object-removal notification behavior is introduced.
- Removes three unused certificate-path/password fields in the DTL money module,
  one unused particle-system local and an unused exception variable, retaining
  the narrow exception catch and existing failure result.
- Documents all parameters of the compositor's `Bake` overload.

The three scoped exceptions above are intentional compatibility decisions, not
project-wide suppression or missing implementations claimed as runtime fixes.
No project-level `NoWarn` entries were added.

### Verification

- Full non-incremental Release rebuild: **0 warnings, 0 errors**.
- Full non-incremental Debug rebuild: **0 warnings, 0 errors**.
  The first Debug attempt aborted with an internal CLR error (`0x80131506`);
  retrying with MSBuild server reuse disabled and one worker succeeded.
- Selected Release tests: **207 passed, 0 failed**:
  Phlox state/save/parcel/compiler/cleanup/cross-engine tests (123),
  scene and inventory tests (43), event queue tests (5), muted messages (36).
- `git diff --check` and editor diagnostics: clean.

Repeat the solution verification with:

```sh
dotnet build Tranquillity.sln -c Release --no-restore --no-incremental
DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet build Tranquillity.sln \
  -c Debug --no-restore --no-incremental -m:1 -nr:false
```

### Excluded-project backlog

An additional compatibility check built
`Tests/OpenSim.Region.ScriptEngine.Tests/OpenSim.Region.ScriptEngine.Tests.csproj`,
which is not included in `Tranquillity.sln`. Its separate build reported
**74 warnings**: CS8600 (11), CS8604 (5), CS8618 (29), SYSLIB0014 (2),
xUnit1013 (10), xUnit2013 (17). These are outside the tracked 661-occurrence
solution inventory; the user chose to document this separate backlog rather
than expand the closeout scope.

Selected LSL type/list and OSSL API tests in that project produced **51 passed,
2 failed**. Both failures are in unchanged float-conversion tests:
`LSL_TypesTestLSLFloat.TestExplicitCastStringToLSLFloat` and
`TestExplicitCastLSLStringToLSLFloat` expect `-100000` but receive `0`.
Neither the float converter nor those tests was changed by this cleanup.
These failures are not counted as successful verification and remain outside
this batch.

Linux tests used the already cached Skia native asset via `LD_LIBRARY_PATH`,
as in the earlier batches. No dependency manifests were changed.

## Batch 3a implementation and verification

Ten `Stream.Read` calls assumed one read returns every requested byte. A short
read left the rest of the buffer zero-filled, so the affected code silently
worked on truncated data:

- `HttpRequest.Clone` copied the request body with a single read. It now rewinds
  the body, copies it in full and restores the original position, so a clone of a
  partially buffered request carries the whole body instead of trailing zeros.
- The authentication POST handler's `crypt` branch reads its capped body exactly.
  That branch's decryption is still an unimplemented stub, so this is hardening.
- Map tile detection reads the JPEG signature with `ReadAtLeast` and reports
  "not a JPEG" for a file shorter than three bytes, instead of inspecting
  uninitialized buffer bytes.
- Vector render's image fetch reads the response body through
  `ReadAsByteArrayAsync`, which does not depend on a seekable content stream or
  on one read returning `Length` bytes. The outbound URL filter, redirect limit
  and failure handling are unchanged.
- Estate terrain download reads exactly, and the data snapshot notification drain
  explicitly tolerates a short read where the bytes are discarded. The web stats
  log tail tolerates concurrent truncation, decodes only available bytes and
  disposes its shared-read file stream on every path.
- A Phlox test's loopback HTTP server keeps its deliberate single read; the
  discarded result is now explicit.

`LSLList` overrode `Object.Equals` without `GetHashCode`, so equal lists hashed
differently and a list could not be found in a dictionary or set. It now hashes
its length and members, with null members contributing a stable value.
Equality itself is unchanged. SLua tables use a dedicated comparer for list keys:
separately constructed lists remain distinct keys by reference, matching Lua,
while ordinary .NET dictionaries retain the `Equals`/`GetHashCode` contract.

The Meshmerizer's unbalanced `[Mesh}` log prefix is now `[MESH]`, matching the
other messages in that file and leaving the template without stray braces.

### Verification

- Full non-incremental Release rebuild: **319 warnings, 0 errors**; exactly
  **12 occurrences removed**, with no added diagnostic messages.
  CA2022, CA2023 and CS0659 are all zero.
- New regression coverage: `LSLListHashTests` (equal lists hash alike, dictionary
  and set lookup, empty lists, differing lengths), `LSLTableKeyTests` (equal list
  instances remain separate keys through lookup, removal, iteration and rebuild)
  and a `HttpRequestTests` clone test whose body stream returns one byte per read.
- Selected Phlox list/outbound/mesh tests: **61 passed**. Selected CoreModules
  render, estate, terrain and archiver tests: **58 passed**.
- Two failures are pre-existing on `develop` and unrelated to this batch:
  `VersionInfoTests.TestVersionLength`, which depends on the branch name in the
  informational version, and `AssetServerPostHandlerTests.TestGoodAssetStoreRequest`.

## Batch 3b implementation and verification

Every CA2017 occurrence was a real mismatch between a logging message template
and its arguments, so each one either dropped a value the caller meant to log or
left a placeholder with nothing to fill it. The fixes keep the values callers
already pass:

- Missing placeholders were added where an argument had no slot, so the Gloebit
  transaction type, the archiver's rejected user name, the Bullet water-height
  result, the overlapping region count, the `ServiceBase` plugin exception, the
  sculpt-map error text and the user-agent reply text are now printed instead of
  silently dropped.
- Extra or duplicated placeholders were removed where no argument backed them,
  including the authentication handler's account message, the Groups member
  lookup, and the Gloebit subscription counts that repeated `{0}`.
- Interpolated-style named placeholders in non-interpolated strings, which these
  logging calls print literally, became the positional form already used nearby:
  the HG lure failure, grid connector empty replies, the Janus provisioning error
  and the WebRTC non-spatial load failure.
- Where the exception is already passed through the logging overload's first
  parameter, the leftover `{0}` was removed rather than re-logging the exception:
  `RestClient`, both JSON store modules and the ubODE box-creation failure.
- `TerrainChannel`'s two out-of-bounds messages concatenate `LogHeader` and then
  started at `{0}` without passing a matching argument, so every coordinate
  landed in the previous slot's label and the last one had no value. Their
  placeholders were renumbered to `{0}`-`{3}`, which labels all four coordinates
  correctly and still prints the header once.

No logging call was suppressed or removed. Correcting the authentication and
terrain templates also restores their intended failure paths: authentication
can return its failure response, and terrain recovery can log and continue
instead of throwing while formatting the diagnostic.

### Verification

- Full non-incremental Release rebuild: **293 warnings, 0 errors**; exactly
  **26 CA2017 occurrences removed**, with no added diagnostic messages.
  CA2017 is zero, and the Batch 3a codes remain zero.
- Selected CoreModules terrain and optional transport tests: **27 passed**.
- Server-side baking tests: **115 passed, 3 skipped**. The first run failed four
  tests inside Skia native-library initialization; they pass with the cached
  Linux Skia asset on `LD_LIBRARY_PATH`, as recorded for Batch 2a.

## Batch 3c implementation and verification

JPEG terrain loading inherited virtual implementations from
`GenericSystemDrawing`, but declared methods with the same signatures instead of
overriding them. A future call through a `GenericSystemDrawing` reference would
therefore use the base PNG/grayscale behavior rather than JPEG's behavior,
although current production wiring calls it through `ITerrainLoader`. The seven
virtual members now override the base members: both load methods, stream loading,
file and stream saving, tiled saving and `SupportsTileSave`. `FileExtension`
remains an intentional interface-level hide and is marked `new`; changing the
base property to virtual would widen this cleanup into a public base-class API
change.

Phlox's `GenVisitor` had a private state-block helper whose name collided with a
generated virtual visitor method. It is now named `EmitStateBlock`, preserving
its private-helper behavior rather than changing visitor dispatch. The SLua
compiler's obsolete `ExprStmt` node and its four unreachable switch arms were
removed; current call statements use `CallStmt`. An unused cross-engine test
delegate was also removed.

### Verification

- Full non-incremental Release rebuild: **282 warnings, 0 errors**; exactly
  **8 CS0114, 1 CS0108 and 2 CS0649 occurrences removed**, with no added
  diagnostic messages. All three codes are zero.
- New JPEG regression coverage verifies that base and interface references use
  JPEG's tile-save, extension and unsupported-load contracts.
- Selected JPEG and cross-engine tests: **36 passed**. Selected Phlox compiler
  tests: **46 passed**.

## Batch 3d implementation and verification

OpenMetaverse vector and quaternion helpers now expose readonly-reference
parameters. Nine calls already passed stable variables and now mark that contract
explicitly with `in`. Ten calls passed values from properties, indexers, nullable
casts or constructed expressions; those values are materialized once into local
variables before the readonly-reference call.

The affected paths are scene keyframe rotation, object inertia, inventory object
rotation, caps linkset upload, ubODE orientation, and Phlox vector normalization,
rotation math, impulse limiting and look-at behavior. Matching Phlox test
expectations use the same explicit readonly-reference contract. The values and
calculation order are unchanged.

### Verification

- Full non-incremental Release rebuild: **263 warnings, 0 errors**; exactly
  **9 CS9192 and 10 CS9193 occurrences removed**. Both codes are zero.
- No diagnostic message was added. The existing `tempi` CS0168 appears at a new
  line number because the Phlox method formatting added lines.
- Selected Phlox rotation, position, force, terrain, keyframe and rez tests:
  **42 passed**. Selected scene and inventory tests: **8 passed**.

## Batch 4 implementation and verification

Nullable annotations now reflect the ExperienceService's real failure paths:
owner and experience lookups and `UpdateExperienceInfo` may return null, and
the service interface marks that return with `MaybeNull`. The user-account and
database fields no longer use null initializers. Both experience-creation
commands now report a missing user-account service rather than dereferencing
one, and a null owner prompt is treated as an empty answer.

### Verification

- Full non-incremental Release rebuild: **250 warnings, 0 errors**; all 13
  Batch 4 production nullability warnings are gone.
- `ExperienceConsoleCommandsTests`: **42 passed**, including the missing
  user-account-service guard for both creation commands.

## Batch 5 implementation and verification

The 181 test nullability warnings were confined to `OpenSim.Region.CoreModules.Tests`
(151 occurrences) and `OpenSim.Server.Base.Tests` (30). Fixtures initialized by
the test setup lifecycle are explicitly marked as such; optional results and
callbacks now use nullable types with assertions or guards before dereference.
Reflection-backed test doubles report missing members explicitly, and proxy,
logger, and web-proxy implementations now match their nullable framework
contracts. Four `CS0067` warnings also disappeared when the affected test event
stubs were initialized to satisfy their non-null event contracts.

### Verification

- Full non-incremental Release rebuild: **65 warnings, 0 errors**; all 181
  Batch 5 test nullability warnings are gone.
- `OpenSim.Region.CoreModules.Tests`: **451 passed** with the cached Linux Skia
  asset on `LD_LIBRARY_PATH`.
- `OpenSim.Server.Base.Tests`: **152 passed**.

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
