# T1 — test-infrastructure defects at the deployed tree

Tree: `D:\tranq-fix`, branch `fix/test-fixtures`, HEAD `db7c746248` (deployed). Runs are serial
(`-- xUnit.ParallelizeTestCollections=false`) so order-flaky tests do not blur the count.

## Part 1 — `OpenSim.Region.CoreModules.Tests`: 35 failures at clean HEAD

### Failing tests before the fix (35 of 79)

| Class | Tests | First exception line |
|---|---|---|
| `Asset.Tests.FlotsamAssetCacheTests` | TestCacheAsset, TestClearCache, TestExpireAsset | `NullReferenceException` at `FlotsamAssetCacheTests.cs:70` (`m_cache` null) |
| `World.Serialiser.Tests.SerialiserTests` | TestSerializeXml, TestSerializeXml2, TestDeserializeXml2 | `NullReferenceException` at `SerialiserTests.cs:714` (`m_scene` null) |
| `World.Land.Tests.PrimCountModuleTests` | TestInitialCounts, TestAddOwnerObject, TestAddGroupObject, TestAddOthersObject, TestCopyOwnerObject, TestMoveOwnerObject, TestRemoveOwnerObject, TestRemoveGroupObject, TestRemoveOthersObject, TestTaint | `NullReferenceException` at `PrimCountModuleTests.cs:85` (`m_lo` null) |
| `World.Media.Moap.Tests.MoapTests` | TestSetMediaUrl, TestClearMediaUrl | `NullReferenceException` in `SceneHelpers.AddSceneObject` (`SceneHelpers.cs:625`, scene argument null) |
| `Framework.InventoryAccess.Tests.InventoryAccessModuleTests` | TestRezObject | `NullReferenceException` at `InventoryAccessModuleTests.cs:141` (`m_scene` null) |
| `Avatar.Inventory.Transfer.Tests.InventoryTransferModuleTests` | TestAcceptGivenItem, TestRejectGivenItem, TestAcceptGivenFolder, TestRejectGivenFolder | `NullReferenceException` in `UserAccountHelpers.CreateUserWithInventory` (`UserAccountHelpers.cs:154`, scene argument null) |
| `Avatar.Inventory.Archiver.Tests.InventoryArchiveSaveTests` | TestOrder, TestSaveItemToIar, TestSaveItemToIarNoAssets, TestSaveRootFolderToIar, TestSaveNonRootFolderToIar | `ArgumentNullException (buffer)` at `InventoryArchiveSaveTests.cs:67` (`m_iarStreamBytes` null) / `NullReferenceException` in `CreateUserWithInventory` |
| `Avatar.Inventory.Archiver.Tests.InventoryArchiveLoadTests` | TestLoadIarCreatorAccountPresent, TestLoadCoalesecedItem, TestLoadIarV0_1AbsentCreator | `NullReferenceException` in `CreateUserWithInventory` (`m_scene` null) |
| `Avatar.Inventory.Archiver.Tests.InventoryArchiveLoadPathTests` | TestLoadIarPathStartsWithSlash, TestLoadIarToInventoryPaths | `NullReferenceException` in `InventoryArchiveReadRequest.Execute` (`:183`, then `:246` closing a null stream) |
| `Avatar.AvatarFactory.AvatarFactoryModuleTests` | TestSetAppearance, TestSaveBakedTextures | `Assert.NotNull() Failure` at `AvatarFactoryModuleTests.cs:88` (`scene.AssetService.Get` returns null) |

`ChatModuleTests.TestInterRegionChatDistanceEastWest` passes serially and fails only under parallel collections;
known order-flaky, not chased.

### Root cause (33 of 35): NUnit lifecycle hooks orphaned by the xunit migration

The xunit migration (`a115734ff3`, "Feature/xunit tests (#197)") removed the NUnit attributes but left the
methods behind:

- `OpenSimTestCase.SetUp()` (`Tests/OpenSim.Tests.Common/OpenSimTestCase.cs:53`) is `public virtual` and was
  **called by nothing**. Subclasses that had `[SetUp] public override void SetUp()` kept the override; xunit,
  which has no `[SetUp]`, never invoked it. Every field those methods assign (`m_scene`, `m_cache`, `m_lo`,
  `m_iarStream`, …) stayed null. Affected here: FlotsamAssetCacheTests, PrimCountModuleTests, MoapTests,
  InventoryAccessModuleTests, InventoryTransferModuleTests, InventoryArchiveSaveTests, InventoryArchiveLoadTests,
  InventoryArchiveLoadPathTests, InventoryArchiveTestCase (26 tests). Nine more classes across
  `LindenUDP.Tests`, `OptionalModules.Tests`, `ScriptEngine.Tests`, `Permissions.Tests` and `LindenCaps.Tests`
  override the same method and were silently unset the same way (`grep -rl 'override void SetUp()' Tests`).
- `InventoryArchiveTestCase.FixtureSetup()` / `TearDown()` (`InventoryArchiveTestCase.cs:72, :80`) were
  `[TestFixtureSetUp]` / `[TestFixtureTearDown]`; with the attributes gone nothing built the default IAR bytes,
  so `SetUp()` at `:91` threw on `new MemoryStream(null)` (5 IAR save tests plus, once the base fix landed, every IAR
  test).
- `SerialiserTests.Init()` (`SerialiserTests.cs:592`) was `[SetUp]`; it became a private method with no caller (3 tests).

Candidates eliminated:
- **Skia rework changed the in-memory asset service**: no. `SceneHelpers.StartAssetService` (`SceneHelpers.cs:214-234`)
  still wires `LocalAssetServicesConnector` over the `OpenSim.Tests.Common.dll` storage provider and, when given,
  `TestsAssetCache`; the cache is registered before the connector's `RegionLoaded` (`SceneHelpers.cs:171-179`). The
  null objects are test-class fields, not service results.
- **A config default the fixture no longer supplies**: no. Every null is a field a lifecycle hook was supposed to
  assign; `SetupScene` itself succeeds in the tests that construct the scene inline (44 passed before the fix).

### The fix (fixture only)

1. `Tests/OpenSim.Tests.Common/OpenSimTestCase.cs`: the base class implements `Xunit.IAsyncLifetime`;
   `InitializeAsync()` calls `SetUp()`. xunit invokes `InitializeAsync` after the constructor (subclass constructor
   bodies included) and before the test method, which is exactly where NUnit ran `[SetUp]`. `DisposeAsync` is a
   no-op because xunit 2 also calls `Dispose`, where the existing teardown overrides live. (A first attempt called
   `SetUp()` from the constructor; that runs before subclass field work in `InventoryArchiveTestCase` and was
   replaced.)
2. `InventoryArchiveTestCase`: `SetUp()` calls `FixtureSetup()` (per instance; xunit builds one per test) and a
   `Dispose()` override calls `TearDown()`.
3. `SerialiserTests`: a `SetUp()` override calls the orphaned `Init()`.

No test assertion and no production code was changed.

### Counts

| Run (serial) | Failed | Passed | Total |
|---|---|---|---|
| clean HEAD `db7c746248` | 35 | 44 | 79 |
| after the base-class hook alone | 19 | 60 | 79 |
| after wiring `FixtureSetup`/`Init` as well | **5** | **74** | 79 |

### Remaining 5 — separate causes, not fixed here

| Test | What it fails on now | Cause |
|---|---|---|
| `InventoryArchiveLoadTests.TestLoadIarCreatorAccountPresent`, `TestLoadIarV0_1AbsentCreator` | `Assert.Equal` expects the creator **name** ("Lord Lucan" / "Mr Tiddles"), the loaded item carries the creator **UUID string** | assertion written against an older `CreatorId` convention; the tests were unreachable (null scene) since #197 and never ran green on xunit; the assertion, not the fixture, is wrong or the loader changed — needs its own session |
| `InventoryArchiveLoadTests.TestLoadCoalesecedItem` | `Assert.Single` sees 2 coalesced objects (`Object1Part1`, `Object2Part1`) | same: reached for the first time; expectation vs `CoalescedSceneObjects` reader to be settled separately |
| `AvatarFactoryModuleTests.TestSetAppearance`, `TestSaveBakedTextures` | `scene.AssetService.Get(bakedTextureID)` returns null after `Store` of a `Temporary + Local` asset | not a lifecycle problem (the scene is built inline); `LocalAssetServicesConnector.Store` (`LocalAssetServiceConnector.cs`, `Store`) caches a Local asset only, `Get` asks the cache first — where the round trip breaks is in the connector/`TestsAssetCache`, unrelated to the shared cause |

## Part 2 — `Tests/OpenSim.Region.ClientStack.LindenCaps.Tests`

Not in `Tranquillity.sln`; last touched by the dotnet 10 SDK bump (`0914c8104a`). Contents:

| File | State | Verdict |
|---|---|---|
| `EventQueue/Tests/EventQueueTests.cs` | 5 `[Fact]` tests (`TestAddForClient`, `TestRemoveForClient`, `TestEnqueueMessage`, `TestEnqueueMessageNoUser`, `TestEnqueueMessageToNpc`) exercising `EventQueueGetModule` through a real `BaseHttpServer` and `SceneHelpers`. Six assertions were mangled in the Skia rework `b6ee976cb7` (#130): `Assert.That(x, Is.EqualTo(1))` → `Assert.True(x));` etc. (`:89, :104, :130, :149, :167, :191`), which is why it does not compile. Setup is a `SetUp()` override — fixed by Part 1. | **keep**: the only tests of the event queue in the tree |
| `Tests/WebFetchInvDescModuleTests.cs` | the whole class is inside a `/* … */` block comment (`:55-`); it targets a `BaseHttpServer` constructor that no longer exists and uses `[TestFixtureSetUp]` | dead; left as is (already compiled out), noted for a later FetchInventoryDescendents2 harness |
| `.csproj` | references NUnit 4 while the sources use xunit `[Fact]`/`Assert`; pins `Microsoft.NET.Test.Sdk 17.14.1` and `Logging.Console 9.0.7` below `Tests.Common` (NU1605 downgrade errors) | rewritten to the `Tests.Common` package set (xunit 2.9.3, runner 3.1.5, Test.Sdk 18.8.1, Logging.Console 10.0.10) |

Restored, in this order:
1. `.csproj` rewritten to the `Tests.Common` package set (NUnit dropped; `<Using Include="NUnit.Framework">` gone).
2. The six mangled assertions rewritten to their pre-#130 meaning in xunit form (`Assert.Equal(1, keys.Count)`,
   `Assert.Equal(0, ...)`, `Assert.Equal((int)HttpStatusCode.OK/NotFound, (int)response["int_response_code"])`,
   `Assert.True(foundUpdate, "Did not find {0} in response")`).
3. `using Xunit;`, `using Nini.Config;`, `using OpenSim.Region.ClientStack.LindenCaps;` added (the class lives in
   `OpenSim.Region.ClientStack.Linden.Tests`; the module moved namespaces).
4. Setup ported to the current `MainServer` API: `MainServer` is instance-based (`IMainServer.Instance`, read-only
   `DefaultServer`), so `MainServer.RemoveHttpServer(port)` / `AddHttpServer(server)` / `Instance = server` became
   `MainServer.Instance.RemoveHttpServer(port)` / `MainServer.Instance.AddHttpServer(m_server)` — the base test class
   removes the previous default server, so the new one becomes `DefaultServer` — and
   `MainServer.Instance.GetPollServiceHandlerKeys()` (no longer on `IMainServer`) became
   `m_server.GetPollServiceHandlerKeys()` on the `BaseHttpServer` the test owns.
5. Project added to `Tranquillity.sln` under `Tests`.

Result: 5 passed, 0 failed (serial). No test was skipped.
