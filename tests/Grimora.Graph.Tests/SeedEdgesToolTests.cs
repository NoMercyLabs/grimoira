using Grimora.Graph.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Graph.Tests;

// RESTRUCTURE.md slice 24 bullet 1: SeedEdgesTool had no pinned test of its own (slice 12's card excused
// it: "all except promote and seed-edges (selftest has them)"). Ports the 2 `selftest` checks that
// covered it (grimora.cs's old SelfTest: "impact: has_more is flagged hardcoded" / "edges: re-sync is
// idempotent (no new mutations)") now that selftest itself is gone. Faithful to the old check: SeedEdges
// never wrote to the cold `mutations` log for kind='edge' (only `promote` does), so this pins that the
// edge-kind mutation count stays at zero across a re-sync, exactly as the old selftest measured it.
public class SeedEdgesToolTests
{
    [Fact]
    public void ASeededEdgeMarkedHardcodedIsFlaggedAndAReSyncAddsNoEdgeMutations()
    {
        string instance = GrimoraCliRunner.NewTestInstance("seed-edges-tool");
        string spinePath = Path.Combine(Path.GetTempPath(), $"grimora-seed-edges-{Guid.NewGuid():N}.json");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            File.WriteAllText(spinePath, """
                {"edges":[{"symbol":"has_more","contract":"PaginatedResponse","project":"test-a","file":"src/list.ts","line":1,"usage":"page.has_more","hardcoded":1}]}
                """);

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            new SeedEdgesTool().Execute(connection, spinePath);

            using (SqliteCommand hardcoded = connection.CreateCommand())
            {
                hardcoded.CommandText = "SELECT count(*) FROM edges WHERE symbol='has_more' AND hardcoded=1";
                Assert.True((long)hardcoded.ExecuteScalar()! >= 1);
            }

            long edgeMutationsBefore;
            using (SqliteCommand before = connection.CreateCommand())
            {
                before.CommandText = "SELECT count(*) FROM mutations WHERE kind='edge'";
                edgeMutationsBefore = (long)before.ExecuteScalar()!;
            }

            new SeedEdgesTool().Execute(connection, spinePath);

            using SqliteCommand after = connection.CreateCommand();
            after.CommandText = "SELECT count(*) FROM mutations WHERE kind='edge'";
            Assert.Equal(edgeMutationsBefore, (long)after.ExecuteScalar()!);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            File.Delete(spinePath);
        }
    }
}
