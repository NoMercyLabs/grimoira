using Grimoira.Graph.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Graph.Tests;

// RESTRUCTURE.md slice 24 bullet 1: PromoteTool had no pinned test of its own (slice 12's card excused
// it: "all except promote and seed-edges (selftest has them)"). Ports the 3 `selftest` checks that
// covered it (grimoira.cs's old SelfTest: "promote: candidate enters the curated graph" / "insert is logged
// to the cold edge log" / "candidate is marked promoted") now that selftest itself is gone.
public class PromoteToolTests
{
    [Fact]
    public void PromotingAPendingCandidateInsertsTheEdgeLogsItAndMarksTheCandidatePromoted()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("promote-tool");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            int id;
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                using SqliteCommand insert = setup.CreateCommand();
                insert.CommandText = "INSERT INTO edge_candidates(symbol,contract,project,file,line,usage,hardcoded,status) " +
                    "VALUES('foo_field','TestDto','web','src/x.ts',10,'a.foo_field',1,'pending')";
                insert.ExecuteNonQuery();
                using SqliteCommand select = setup.CreateCommand();
                select.CommandText = "SELECT id FROM edge_candidates WHERE symbol='foo_field'";
                id = (int)(long)select.ExecuteScalar()!;
            }

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new PromoteTool().Execute(connection, id);

            Assert.StartsWith("promoted", result);

            using SqliteCommand edgeCount = connection.CreateCommand();
            edgeCount.CommandText = "SELECT count(*) FROM edges WHERE symbol='foo_field'";
            Assert.Equal(1L, (long)edgeCount.ExecuteScalar()!);

            using SqliteCommand mutationCount = connection.CreateCommand();
            mutationCount.CommandText = "SELECT count(*) FROM mutations WHERE kind='edge' AND k LIKE 'foo_field%'";
            Assert.Equal(1L, (long)mutationCount.ExecuteScalar()!);

            using SqliteCommand statusCount = connection.CreateCommand();
            statusCount.CommandText = $"SELECT count(*) FROM edge_candidates WHERE id={id} AND status='promoted'";
            Assert.Equal(1L, (long)statusCount.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
