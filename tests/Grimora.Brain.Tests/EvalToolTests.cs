using System.Text.RegularExpressions;
using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public partial class EvalToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputOnAFreshStore()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("eval-cli-fresh-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("eval-cli-fresh-new");
        try
        {
            // Oracle: today's grimora.cs Eval() (grimora.cs:2375). No facts seeded, so every answerable
            // question misses and only the 4 unanswerable ones pass.
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"eval --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = StripTiming(stdout.Trim());

            GrimoraCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(newInstance));
            string actual = StripTiming(new EvalTool().ExecuteCli(connection).Trim());

            Assert.Equal(expected, actual);
            Assert.Contains("accuracy 4/14 (29%)", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
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
