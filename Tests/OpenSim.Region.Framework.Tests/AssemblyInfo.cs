// These tests exercise scenes that share process-wide static state (MainServer,
// Util.FireAndForgetMethod, static caps registries), so they cannot run in parallel.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
