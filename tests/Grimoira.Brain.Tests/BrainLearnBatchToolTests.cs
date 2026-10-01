using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainLearnBatchToolTests
{
    private const string Batch = """
        node | batch:n1 | concept | a batch node | some gloss | 0 | myscheme
        node | batch:n2 | concept | another node | | 1 |
        triple | batch:n1 | related | batch:n2 | because text
        slot | batch:n1 | place | somewhere | text | 0
        # a comment line is skipped
        bogus line with no pipes
        """;

    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndWritesTheRows()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-learn-batch-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-learn-batch-new");
        string file = Path.Combine(Path.GetTempPath(), $"brain-learn-batch-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(file, Batch);

            // Oracle: today's grimoira.cs BrainLearnBatch() (grimoira.cs:2031).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain learn-batch --instance {oldInstance} --from {file}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainLearnBatchTool().ExecuteCli(connection, file).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("learn-batch: 2 nodes, 1 triples, 1 slots (1 skipped).", actual);

            using SqliteCommand nodeCount = connection.CreateCommand();
            nodeCount.CommandText = "SELECT count(*) FROM node_now WHERE k IN ('batch:n1','batch:n2')";
            Assert.Equal(2L, (long)nodeCount.ExecuteScalar()!);

            using SqliteCommand tripleCount = connection.CreateCommand();
            tripleCount.CommandText = "SELECT count(*) FROM triple_now WHERE s='batch:n1' AND p='related' AND o='batch:n2'";
            Assert.Equal(1L, (long)tripleCount.ExecuteScalar()!);

            using SqliteCommand slotCount = connection.CreateCommand();
            slotCount.CommandText = "SELECT count(*) FROM slot_now WHERE frame_k='batch:n1' AND name='place' AND value='somewhere'";
            Assert.Equal(1L, (long)slotCount.ExecuteScalar()!);

            using SqliteCommand mutations = connection.CreateCommand();
            mutations.CommandText = "SELECT count(*) FROM mutations WHERE why='learn-batch'";
            Assert.Equal(4L, (long)mutations.ExecuteScalar()!); // 2 nodes + 1 triple + 1 slot
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsAMissingFile()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-learn-batch-missing");
        string missingFile = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.txt");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain learn-batch --instance {instance} --from {missingFile}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainLearnBatchTool().ExecuteCli(connection, missingFile).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal($"file not found: {missingFile}", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
