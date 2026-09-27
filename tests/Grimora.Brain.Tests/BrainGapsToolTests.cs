using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainGapsToolTests
{
    [Fact]
    public void CliShapeReportsNoOpenGapsOnAFreshStore()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-gaps-cli-empty");
        try
        {
            // Oracle: today's grimora.cs BrainGaps() (grimora.cs:1596).
            GrimoraCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain gaps --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainGapsTool().ExecuteCli(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no open gaps.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithAnOpenGap()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-gaps-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-gaps-cli-new");
        try
        {
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"query --instance {oldInstance} nothing-ever-matches-this-term-here");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain gaps --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            GrimoraCliRunner.Run($"query --instance {newInstance} nothing-ever-matches-this-term-here");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = Normalize(new BrainGapsTool().ExecuteCli(connection));

            Assert.Equal(expected, actual);
            Assert.Contains("nothing ever matches term here", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeReportsNoOpenGapsOnAFreshStore()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-gaps-mcp-empty");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_gaps")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainGapsTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Equal("no open gaps — every recent lookup was answerable.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // Same normalisation QueryToolTests uses: the redirected child process re-encodes "—" through the
    // OEM codepage on a box that cannot represent it (e.g. codepage 850), a capture artifact unrelated
    // to this move; both sides get the same substitution before comparing shape.
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim().Replace('—', '-');
}
