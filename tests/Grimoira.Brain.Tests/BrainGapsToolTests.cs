using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainGapsToolTests
{
    [Fact]
    public void CliShapeReportsNoOpenGapsOnAFreshStore()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-gaps-cli-empty");
        try
        {
            // Oracle: today's grimoira.cs BrainGaps() (grimoira.cs:1596).
            GrimoiraCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain gaps --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainGapsTool().ExecuteCli(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no open gaps.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithAnOpenGap()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-gaps-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-gaps-cli-new");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Run($"query --instance {oldInstance} nothing-ever-matches-this-term-here");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain gaps --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            GrimoiraCliRunner.Run($"query --instance {newInstance} nothing-ever-matches-this-term-here");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = Normalize(new BrainGapsTool().ExecuteCli(connection));

            Assert.Equal(expected, actual);
            Assert.Contains("nothing ever matches term here", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeReportsNoOpenGapsOnAFreshStore()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-gaps-mcp-empty");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_gaps")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainGapsTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Equal("no open gaps — every recent lookup was answerable.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // Same normalisation QueryToolTests uses: the redirected child process re-encodes "—" through the
    // OEM codepage on a box that cannot represent it (e.g. codepage 850), a capture artifact unrelated
    // to this move; both sides get the same substitution before comparing shape.
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim().Replace('—', '-');
}
