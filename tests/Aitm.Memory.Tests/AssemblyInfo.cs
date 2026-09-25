using Xunit;

// MemToolTests and ShedMemoryToolTests both mutate the process-wide AITM_INSTANCE environment
// variable around their in-process mcp.dll reflection calls (the same pattern QueryToolTests uses
// in Aitm.Facts.Tests). xunit puts each test class in its own collection by default, and different
// collections run in parallel, so two classes racing to set/read AITM_INSTANCE at the same time can
// read each other's instance. Serialising this assembly avoids that race without touching the
// env-var-based instance resolution mcp.cs already has (RESTRUCTURE.md: old and new run side by
// side; that resolution is the oracle, not something this slice changes).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
