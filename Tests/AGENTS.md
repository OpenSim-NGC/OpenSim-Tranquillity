# AGENTS.md — xUnit.v3 Test Suite

This directory contains automated tests built on **xUnit.net v3** and **Microsoft.Testing.Platform (MTP)**. Because xUnit v3 builds test projects as standalone executables rather than VSTest class libraries, traditional VSTest CLI flags do not work here.

## Running Tests

Always pass runner arguments after `--` (or run the test project directly with `dotnet run`). Do **not** use legacy VSTest `--filter` expressions.

* **Run all tests in this directory:**
  ```bash
  dotnet test
  ```
* **Run a single test class:**
  ```bash
  dotnet run -- --filter-class "YourNamespace.YourTestClass"
  ```
* **Run a single test method (supports `*` wildcards at start/end):**
  ```bash
  dotnet run -- --filter-method "*MethodName*"
  ```
* **Filter by namespace:**
  ```bash
  dotnet run -- --filter-namespace "YourNamespace.SubNamespace*"
  ```
* **Filter by trait (replaces `TestCategory`):**
  ```bash
  dotnet run -- --filter-trait "Category=Unit"
  ```
* **Exclude traits (e.g., skipping slow or integration tests):**
  ```bash
  dotnet run -- --filter-not-trait "Category=Integration"
  ```
* **List all discovered tests without executing them:**
  ```bash
  dotnet run -- --list-tests
  ```

> **Filter Logic:** Multiple filters of the same flag type combine with `OR` logic; different filter flag types combine with `AND` logic.

## xUnit v3 Architecture & Conventions

* **Standalone Executable Model:** Test projects use `<OutputType>Exe</OutputType>` and reference `xunit.v3` (not `xunit` v2) alongside `Microsoft.NET.Test.Sdk` or `xunit.v3.mtp-v2`.
* **Async Lifecycle (`IAsyncLifetime`):** In xUnit v3, `IAsyncLifetime.InitializeAsync()` and `DisposeAsync()` return `ValueTask` instead of `Task`. Always use `ValueTask` when implementing `IAsyncLifetime`.
* **Async Assertions:** Prefer native async assertions (`Assert.ThrowsAsync<T>`, `Assert.RaisesAsync`) and avoid `.Result` or `.Wait()` on tasks to prevent thread-pool starvation.
* **Test Context & Cancellation:** Use `TestContext.Current.CancellationToken` when calling async APIs inside tests instead of `CancellationToken.None`.
* **Console & Output Capture:** Use `ITestOutputHelper` injected via constructor, or `TestContext.Current.TestOutputHelper`, rather than `Console.WriteLine`.
* **Theory Data:** Prefer strongly typed `TheoryData<T1, T2, ...>` over `IEnumerable<object[]>` for `[MemberData]` and `[ClassData]` sources to preserve compile-time type safety.

## Structure & Naming

* **Project Mirroring:** Each `Source/<ProjectName>` project should map to `Tests/<ProjectName>.Tests`.
* **File & Class Naming:** Mirror the production folder structure. Name test classes `<TargetClassName>Tests.cs`.
* **Method Naming:** Use `MethodName_StateUnderTest_ExpectedBehavior` or clear descriptive names with `[Fact(DisplayName = "...")]` when appropriate.
* **AAA Pattern:** Organize every test body clearly into **Arrange**, **Act**, and **Assert** blocks separated by a blank line.

## Boundaries & Guardrails

* **Always:**
  * Run the affected test class or project before marking a task complete.
  * Keep unit tests isolated from network, filesystem, and system clock dependencies unless explicitly tagged with `[Trait("Category", "Integration")]`.
* **Never:**
  * Install `xunit` (v2), `xunit.runner.visualstudio` (v2), or mix v2 and v3 packages in the same project.
  * Use legacy VSTest CLI syntax (`dotnet test --filter "FullyQualifiedName~..."`).
  * Disable or `[Fact(Skip = "...")]` failing tests to make the suite pass without explicit instruction.

