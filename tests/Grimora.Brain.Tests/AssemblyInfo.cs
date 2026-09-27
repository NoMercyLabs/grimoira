using Xunit;

// Several test classes in this assembly mutate the process-wide GRIMORA_INSTANCE environment variable
// around their in-process mcp.dll reflection calls (the same pattern QueryToolTests uses in
// Grimora.Facts.Tests). xunit puts each test class in its own collection by default, and different
// collections run in parallel, so two classes racing to set/read GRIMORA_INSTANCE at the same time can
// read each other's instance. Serialising this assembly avoids that race without touching the
// env-var-based instance resolution mcp.cs already has — the same fix Grimora.Docs.Tests,
// Grimora.Graph.Tests and Grimora.Memory.Tests already carry.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
