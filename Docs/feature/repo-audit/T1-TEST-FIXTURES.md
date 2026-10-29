# Test fixtures after the move to xunit

A note for anyone writing or porting tests under `Tests/`. Several test files cite it.

## Per-test setup

xunit has no `[SetUp]` attribute. When the tests moved from NUnit to xunit (#197), the attributes were removed
but the methods stayed, so nothing called them. Every field those methods assigned stayed null.

The fix lives in the shared base class, `Tests/OpenSim.Tests.Common/OpenSimTestCase.cs`:

- `OpenSimTestCase` implements `Xunit.IAsyncLifetime`.
- xunit calls `InitializeAsync()` after the constructor (subclass constructor bodies included) and before the
  test method. That is where NUnit ran `[SetUp]`. `InitializeAsync()` calls `SetUp()`.
- Subclasses override `SetUp()` and call `base.SetUp()`.
- Teardown stays on `Dispose()`. xunit 2 calls `DisposeAsync()` and then `Dispose()`, so `DisposeAsync()` is a
  no-op.

**Do not call `SetUp()` from the base constructor.** It would then run before subclass constructors have done
their own field work.

## Former fixture-level setup

xunit builds a fresh test-class instance for every test, so former `[TestFixtureSetUp]` work has to run per
instance:

- `InventoryArchiveTestCase` (`Tests/OpenSim.Region.CoreModules.Tests/Avatar/Inventory/Archiver/Tests/`) calls
  `FixtureSetup()` from its `SetUp()`.
- `SerialiserTests` (`Tests/OpenSim.Region.CoreModules.Tests/World/Serialiser/Tests/`) overrides `SetUp()` to call
  its former `[SetUp]` method.

## When porting a test class

- Remove `[SetUp]`, `[TearDown]`, `[TestFixtureSetUp]` and `[TestFixtureTearDown]` only once the method they
  marked is reached some other way: through `SetUp()`, `Dispose()` or the constructor.
- A test that passes while its fixture fields are null is not testing anything. Check that the setup actually
  ran.

## `OpenSim.Region.ClientStack.LindenCaps.Tests`

This project is in `Tranquillity.sln`. It declares no test packages of its own and uses the xunit set from
`Tests/OpenSim.Tests.Common`. Its event-queue tests override the base-class `SetUp()` described above.
