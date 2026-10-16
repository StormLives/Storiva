// The UI palette (StorivaPalette.Current) is process-wide state, and several tests start loopback servers:
// run the test classes one after another so they cannot interfere.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
