// These tests exercise scenes that share process-wide static state (MainServer,
// Util.FireAndForgetMethod, static caps registries), so they cannot run in parallel.
//
// Without this the project is order-dependent and flaky: a full run fails a different set of
// tests each time (9 failures at HEAD, but not the same 9), and failures appear inside
// SceneHelpers.SetupScene rather than in any assertion. The same declaration, for the same
// reason, is already in Tests/OpenSim.Region.Framework.Tests/AssemblyInfo.cs.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
