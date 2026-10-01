using System.Text.RegularExpressions;
using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public partial class EvalToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputOnAFreshStore()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("eval-cli-fresh-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("eval-cli-fresh-new");
        try
        {
            // Oracle: today's grimoira.cs Eval() (grimoira.cs:2375). No facts seeded, so every answerable
            // question misses and only the 4 unanswerable ones pass.
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"eval --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = StripTiming(stdout.Trim());

            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(newInstance));
            string actual = StripTiming(new EvalTool().ExecuteCli(connection).Trim());

            Assert.Equal(expected, actual);
            Assert.Contains("accuracy 4/14 (29%)", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    // The strip pattern used by QueryToolTests for the same reason: the oracle and the new tool run at
    // slightly different times, so every "N.NNms" tail (per-line and the trailing average) never matches
    // byte for byte between the two runs. EvalTool right-aligns the elapsed time in a 5-wide field
    // ({ms,5:F2}ms), so a run under 10ms prints one extra leading space that a run at 10ms or over does
    // not; the mask must eat that optional pad (the same rule BinCliThinClientMatchesBinCliOldTests
    // already applies to eval's CLI output) or two runs that land on opposite sides of that 10ms boundary
    // mismatch on whitespace alone.
    private static string StripTiming(string s) => PaddedTimingMs().Replace(s.Replace("\r\n", "\n"), "<MS>");

    [GeneratedRegex(@"[ \t]*\d+[.,]\d+ms", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PaddedTimingMs();
}
