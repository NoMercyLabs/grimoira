using Grimora.Graph.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Graph.Tests;

public class CandidatesToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputFilteredBySymbol()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("candidates-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("candidates-new");
        try
        {
            // Oracle: today's grimora.cs ListCandidates() (grimora.cs:2950).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedCandidate(GrimoraCliRunner.InstanceDbPath(oldInstance), "has_more", "web", "src/list.ts", 3, 1, "page.has_more");
            SeedCandidate(GrimoraCliRunner.InstanceDbPath(oldInstance), "other_field", "api", "src/x.ts", 5, 0, "");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"candidates --instance {oldInstance} --symbol has_more");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: CandidatesTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            SeedCandidate(dbPath, "has_more", "web", "src/list.ts", 3, 1, "page.has_more");
            SeedCandidate(dbPath, "other_field", "api", "src/x.ts", 5, 0, "");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = Normalize(new CandidatesTool().Execute(connection, "has_more"));
            }

            Assert.Equal(expected, actual);
            Assert.Contains("has_more", actual);
            Assert.DoesNotContain("other_field", actual);
            Assert.Contains("[HARDCODED]", actual);
            Assert.EndsWith("(1 pending candidate(s) - promote with: grimora promote <id>)", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void MatchesTodaysCliOutputWithNoFilter()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("candidates-all-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("candidates-all-new");
        try
        {
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"candidates --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new CandidatesTool().Execute(connection, null));

            Assert.Equal(expected, actual);
            Assert.Equal("(0 pending candidate(s) - promote with: grimora promote <id>)", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    private static void SeedCandidate(string dbPath, string symbol, string project, string file, int line, int hardcoded, string usage)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO edge_candidates(symbol,contract,project,file,line,usage,hardcoded,status) VALUES($s,'C',$p,$f,$l,$u,$h,'pending')";
        insert.Parameters.AddWithValue("$s", symbol);
        insert.Parameters.AddWithValue("$p", project);
        insert.Parameters.AddWithValue("$f", file);
        insert.Parameters.AddWithValue("$l", line);
        insert.Parameters.AddWithValue("$u", usage);
        insert.Parameters.AddWithValue("$h", hardcoded);
        insert.ExecuteNonQuery();
    }

    // Windows redirects the child's stdout through the OEM codepage, not UTF-8, so grimora.cs's "—" arrives
    // mangled regardless of the encoding this side decodes with — a capture artifact, not a behaviour
    // difference (QueryToolTests documents the same substitution for its own dashes/bullets).
    private static string Normalize(string s) => s.Trim().Replace('—', '-');
}
