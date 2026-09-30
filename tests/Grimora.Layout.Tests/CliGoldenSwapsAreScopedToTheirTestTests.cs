using Grimora.TestSupport;
using Xunit;

namespace Grimora.Layout.Tests;

// CliGoldens.ForThisRun learns "frozen argument -> this run's argument" swaps so a folder or id named by an
// earlier call of the same test is recognised in a later call's output. Test classes in this assembly run in
// parallel, so ForThisRun is called from many threads at once. Seen at suite run 104 under load:
// `ArgumentException: The index is equal to or greater than the length of the array` out of
// Enumerable.ToArray <- OrderedIterator.MoveNext <- CliGoldens.ForThisRun, because the swap map was one
// process-wide dictionary sorted in place while another test was adding to it. The swaps also belong to one test
// only: a swap learned by test A must never rewrite test B's output.
public sealed class CliGoldenSwapsAreScopedToTheirTestTests
{
    private const string Here = "CliGoldenSwapsAreScopedToTheirTestTests.cs";

    [Fact]
    public async Task ManyTestsLearningSwapsAtOnceNeverThrow()
    {
        // Every call swaps two fresh arguments longer than 3 chars, so the map grows on every call while
        // other threads are sorting it. 16 threads x 2000 calls hit the window every run on this box.
        Task[] workers = [.. Enumerable.Range(0, 16).Select(n => Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                string frozen = $"frozen-{n}-{i}-aaaa frozen-{n}-{i}-bbbb";
                string now = $"now-{n}-{i}-aaaa now-{n}-{i}-bbbb";
                CliGoldens.Entry golden = new($"Member{n}", frozen, $"out {frozen}", "", 0, frozen);
                string got = CliGoldens.ForThisRun(golden, golden.Stdout, now, null, Here, $"Member{n}");
                Assert.Equal($"out {now}", got);
            }
        }))];
        Exception? failure = await Record.ExceptionAsync(() => Task.WhenAll(workers));
        Assert.Null(failure);
    }

    [Fact]
    public void ASwapLearnedByOneTestDoesNotRewriteAnotherTestsOutput()
    {
        CliGoldens.Entry learned = new("TestA", "folder-one", "made folder-one", "", 0, "folder-one");
        Assert.Equal("made folder-two", CliGoldens.ForThisRun(learned, learned.Stdout, "folder-two", null, Here, "TestA"));

        // TestB passes the same argument it froze with, so it learns nothing; "folder-one" in its output stays.
        CliGoldens.Entry other = new("TestB", "same-args", "saw folder-one", "", 0, "same-args");
        Assert.Equal("saw folder-one", CliGoldens.ForThisRun(other, other.Stdout, "same-args", null, Here, "TestB"));
    }
}
