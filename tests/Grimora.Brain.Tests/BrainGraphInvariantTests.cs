using Grimora.Brain.Data;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

// RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` checks that exercised the brain schema's own
// write-time invariants directly (grimora.cs's old SelfTest: "brain: predicate guard rejects an unknown
// predicate" / "brain: dangling subject is a write error, not a silent miss" / "brain: intersection
// finds the shared seam in one query" / "brain: supersede keeps one live node + full history" /
// "conflicts: requires+forbids on the same pair is detected") now that selftest itself is gone. These
// are schema-level triggers and views (BrainSchema), not tool behaviour, so they are pinned here at the
// data layer the same way the old selftest pinned them with raw SQL.
public class BrainGraphInvariantTests
{
    [Fact]
    public void PredicateGuardRejectsAnUnknownPredicate()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-invariant-predicate-guard");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "proj:test-a", "project", "Test A", "app A");
            BrainTestFixtures.InsertNode(dbPath, "seam:test-auth", "seam", "Test Auth", "shared authentication seam");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO triple(s,p,o,o_is_literal) VALUES('proj:test-a','consoom','seam:test-auth',0)";
            Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery());
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void DanglingSubjectIsAWriteErrorNotASilentMiss()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-invariant-dangling-subject");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "seam:test-auth", "seam", "Test Auth", "shared authentication seam");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO triple(s,p,o,o_is_literal) VALUES('proj:ghost','consumes','seam:test-auth',0)";
            Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery());
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void IntersectionFindsTheSharedSeamInOneQuery()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-invariant-intersection");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "proj:test-a", "project", "Test A", "app A");
            BrainTestFixtures.InsertNode(dbPath, "proj:test-b", "project", "Test B", "app B");
            BrainTestFixtures.InsertNode(dbPath, "seam:test-auth", "seam", "Test Auth", "shared authentication seam");
            BrainTestFixtures.InsertSharingTriple(dbPath, "proj:test-a", "seam:test-auth");
            BrainTestFixtures.InsertSharingTriple(dbPath, "proj:test-b", "seam:test-auth");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            using SqliteCommand select = connection.CreateCommand();
            select.CommandText = """
                SELECT count(*) FROM (
                    SELECT t.o FROM triple_now t WHERE t.p='consumes' AND t.s IN ('proj:test-a','proj:test-b')
                    GROUP BY t.o HAVING COUNT(DISTINCT t.s)=2
                )
                """;
            Assert.Equal(1L, (long)select.ExecuteScalar()!);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void SupersedeOnAChangedNodeKeepsOneLiveRowPlusFullHistory()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-invariant-supersede");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "proj:test-a", "project", "Test A", "app A");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            bool wrote = BrainWriters.AddNode(connection, "proj:test-a", "project", "Test A", "app A v2", "", false, "test");
            Assert.True(wrote);

            using SqliteCommand history = connection.CreateCommand();
            history.CommandText = "SELECT count(*) FROM node WHERE k='proj:test-a'";
            Assert.Equal(2L, (long)history.ExecuteScalar()!);

            using SqliteCommand live = connection.CreateCommand();
            live.CommandText = "SELECT count(*) FROM node_now WHERE k='proj:test-a'";
            Assert.Equal(1L, (long)live.ExecuteScalar()!);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void RequiresAndForbidsOnTheSamePairIsDetectedAsAConflict()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-invariant-conflicts");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "c:s", "concept", "CS", "");
            BrainTestFixtures.InsertNode(dbPath, "c:o", "rule", "CO", "");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            Assert.Equal(BrainWriters.TripleWrite.Inserted, BrainWriters.AddTriple(connection, "c:s", "requires", "c:o", "", "test", false, "test"));
            Assert.Equal(BrainWriters.TripleWrite.Inserted, BrainWriters.AddTriple(connection, "c:s", "forbids", "c:o", "", "test", false, "test"));

            using SqliteCommand select = connection.CreateCommand();
            select.CommandText = """
                SELECT count(*) FROM (
                    SELECT DISTINCT t1.s FROM triple_now t1
                    JOIN pred_vocab v ON v.p=t1.p AND v.conflicts IS NOT NULL
                    JOIN triple_now t2 ON t2.s=t1.s AND t2.o=t1.o AND t2.p=v.conflicts
                    WHERE t1.s='c:s'
                )
                """;
            Assert.Equal(1L, (long)select.ExecuteScalar()!);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
