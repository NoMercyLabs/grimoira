using Aitm.Graph.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Graph.Tests;

public class CandidatesToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputFilteredBySymbol()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("candidates-old");
        string newInstance = AitmCliRunner.NewTestInstance("candidates-new");
        try
        {
            // Oracle: today's aitm.cs ListCandidates() (aitm.cs:2950).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedCandidate(AitmCliRunner.InstanceDbPath(oldInstance), "has_more", "web", "src/list.ts", 3, 1, "page.has_more");
            SeedCandidate(AitmCliRunner.InstanceDbPath(oldInstance), "other_field", "api", "src/x.ts", 5, 0, "");
            (string stdout, int exitCode) = AitmCliRunner.Run($"candidates --instance {oldInstance} --symbol has_more");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: CandidatesTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
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
            Assert.EndsWith("(1 pending candidate(s) - promote with: aitm promote <id>)", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void MatchesTodaysCliOutputWithNoFilter()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("candidates-all-old");
        string newInstance = AitmCliRunner.NewTestInstance("candidates-all-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = AitmCliRunner.Run($"candidates --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new CandidatesTool().Execute(connection, null));

            Assert.Equal(expected, actual);
            Assert.Equal("(0 pending candidate(s) - promote with: aitm promote <id>)", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
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

    // Windows redirects the child's stdout through the OEM codepage, not UTF-8, so aitm.cs's "—" arrives
    // mangled regardless of the encoding this side decodes with — a capture artifact, not a behaviour
    // difference (QueryToolTests documents the same substitution for its own dashes/bullets).
    private static string Normalize(string s) => s.Trim().Replace('—', '-');
}
