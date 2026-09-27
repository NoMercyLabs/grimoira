using Xunit;

// ImpactToolTests mutates the process-wide GRIMORA_INSTANCE environment variable around its in-process
// mcp.dll reflection calls (the same pattern Grimora.Docs.Tests and Grimora.Memory.Tests use). xunit puts
// each test class in its own collection by default, and different collections run in parallel, so two
// classes racing to set/read GRIMORA_INSTANCE at the same time can read each other's instance. Serialising
// this assembly avoids that race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
