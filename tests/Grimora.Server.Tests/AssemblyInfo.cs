using Xunit;

// Slice 25 tests mutate process-wide state (the Grimora_DATA_DIR env var read once by
// Program's top-level statements; Grimora_SERVER_PORT is no longer read since Slice P1) and take an exclusive OS-level lock file per data directory. xunit
// puts each test class in its own collection by default, and different collections run in parallel, so
// two classes racing to acquire or read the same temp lock file at the same time could see each other's
// state. Serialising this assembly is the same fix Grimora.Brain.Tests, Grimora.Docs.Tests, Grimora.Graph.Tests
// and Grimora.Memory.Tests already carry for the equivalent Grimora_INSTANCE race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
