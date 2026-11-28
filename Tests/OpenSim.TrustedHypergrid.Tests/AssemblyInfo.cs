using Xunit;

// TrustedHypergridHooks.Runtime / .Lookup are process-wide statics set by more than one test
// class; run classes sequentially so one class's hooks cannot leak into another mid-test.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
