using Xunit;

// DocToolTests and ShedDocToolTests both mutate the process-wide GRIMOIRA_INSTANCE environment variable
// around their in-process mcp.dll reflection calls (the same pattern Grimoira.Memory.Tests uses). xunit
// puts each test class in its own collection by default, and different collections run in parallel, so
// two classes racing to set/read GRIMOIRA_INSTANCE at the same time can read each other's instance.
// Serialising this assembly avoids that race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
