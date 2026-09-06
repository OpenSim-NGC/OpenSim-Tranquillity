using Xunit;

// TrustedHypergridHooks.Runtime / .Lookup are process-wide statics set by more than one test
// class; run classes sequentially so one class's hooks cannot leak into another mid-test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
