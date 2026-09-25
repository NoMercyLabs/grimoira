using Xunit;

// DocToolTests and ShedDocToolTests both mutate the process-wide AITM_INSTANCE environment variable
// around their in-process mcp.dll reflection calls (the same pattern Aitm.Memory.Tests uses). xunit
// puts each test class in its own collection by default, and different collections run in parallel, so
// two classes racing to set/read AITM_INSTANCE at the same time can read each other's instance.
// Serialising this assembly avoids that race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
