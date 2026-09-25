using Xunit;

// Test classes here set the process-wide AITM_INSTANCE environment variable around their in-process
// mcp.dll calls. xunit runs different test classes in parallel by default, so two classes can read
// each other's instance. Serialise the assembly, as Aitm.Brain.Tests, Docs, Graph and Memory do.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
