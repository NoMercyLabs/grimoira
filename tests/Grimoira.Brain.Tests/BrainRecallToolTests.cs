using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainRecallToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAnFtsHit()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-recall-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-recall-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainRecall() (grimoira.cs:1512).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            SeedNode(oldDb);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain recall --instance {oldInstance} paginated cursor");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            SeedNode(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = Normalize(new BrainRecallTool().ExecuteCli(connection, "paginated cursor"));

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeFallsBackToSubstringAndLogsAGapWhenNothingMatches()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-recall-cli-gap");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain recall --instance {instance} nothing-ever-matches-this-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new BrainRecallTool().ExecuteCli(connection, "nothing-ever-matches-this-term"));

            Assert.Equal(expected, actual);
            Assert.Contains("substring fallback", actual);

            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM gaps WHERE tool='brain_recall'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageForEmptyText()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-recall-cli-usage");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain recall --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainRecallTool().ExecuteCli(connection, "").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain recall <free text>", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAnFtsHit()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-recall-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");
            SeedNode(dbPath);

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_recall", "paginated cursor")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainRecallTool().ExecuteMcp(connection, "paginated cursor");

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeLogsAGapWhenNothingMatches()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-recall-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_recall", "nothing-ever-matches-this-mcp-term")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainRecallTool().ExecuteMcp(connection, "nothing-ever-matches-this-mcp-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);

            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM gaps WHERE tool='brain_recall'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` checks that exercised the alias-expansion
    // SQL pattern in BrainRecallTool's own CTE (grimoira.cs's old SelfTest: "recall: hyphenated alias
    // expansion is quoted (no FTS5 syntax crash)" / "recall: a synonym alias expands the query (hub ->
    // signalr)") now that selftest itself is gone. Both go through the real tool call rather than a raw
    // SQL fragment, so they exercise the exact code path a session hits.
    [Fact]
    public void HyphenatedAliasCanonicalExpandsWithoutAnFts5SyntaxCrash()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-recall-hyphen-alias");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                using SqliteCommand insertAlias = setup.CreateCommand();
                insertAlias.CommandText = "INSERT OR IGNORE INTO term_alias(term,canonical) VALUES('screen','compose-screen')";
                insertAlias.ExecuteNonQuery();
            }
            BrainTestFixtures.InsertNode(dbPath, "kind:hyp-test", "codekind", "hyphen test", "compose-screen widget");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new BrainRecallTool().ExecuteCli(connection, "screen");

            Assert.Contains("kind:hyp-test", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ASynonymAliasExpandsTheQuery()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-recall-synonym-alias");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                using SqliteCommand insertAlias = setup.CreateCommand();
                insertAlias.CommandText = "INSERT OR IGNORE INTO term_alias(term,canonical) VALUES('hub','signalr')";
                insertAlias.ExecuteNonQuery();
            }
            BrainTestFixtures.InsertNode(dbPath, "seam:hubtest", "seam", "Realtime thing", "signalr realtime sync");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new BrainRecallTool().ExecuteCli(connection, "hub");

            Assert.Contains("seam:hubtest", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedNode(string dbPath) =>
        BrainTestFixtures.InsertNode(dbPath, "contract:paginated", "contract", "Paginated Response", "cursor based paging shape");

    // Same normalisation QueryToolTests uses: the redirected child process re-encodes "•"/"—" through
    // the OEM codepage on a box that cannot represent them (e.g. codepage 850), a capture artifact
    // unrelated to this move; both sides get the same substitution before comparing shape.
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim().Replace('•', '*').Replace('—', '-');
}
