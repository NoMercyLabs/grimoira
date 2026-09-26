using System.Text.RegularExpressions;
using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class EvalToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputOnAFreshStore()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("eval-cli-fresh-old");
        string newInstance = AitmCliRunner.NewTestInstance("eval-cli-fresh-new");
        try
        {
            // Oracle: today's aitm.cs Eval() (aitm.cs:2375). No facts seeded, so every answerable
            // question misses and only the 4 unanswerable ones pass.
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = AitmCliRunner.Run($"eval --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = StripTiming(stdout.Trim());

            AitmCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(AitmCliRunner.InstanceDbPath(newInstance));
            string actual = StripTiming(new EvalTool().ExecuteCli(connection).Trim());

            Assert.Equal(expected, actual);
            Assert.Contains("accuracy 4/14 (29%)", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    // The strip pattern used by QueryToolTests for the same reason: the oracle and the new tool run at
    // slightly different times, so every "N.NNms" tail (per-line and the trailing average) never matches
    // byte for byte between the two runs. EvalTool right-aligns the elapsed time in a 5-wide field
    // ({ms,5:F2}ms), so a run under 10ms prints one extra leading space that a run at 10ms or over does
    // not; the mask must eat that optional pad (the same rule BinCliThinClientMatchesBinCliOldTests
    // already applies to eval's CLI output) or two runs that land on opposite sides of that 10ms boundary
    // mismatch on whitespace alone.
    private static string StripTiming(string s) => Regex.Replace(s.Replace("\r\n", "\n"), @"[ \t]*\d+[.,]\d+ms", "<MS>");
}
