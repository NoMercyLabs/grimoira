using Aitm.Memory.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Memory.Tests;

public class IndexMemoryToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndIndexesEachFileIntoTheMemoryTable()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("index-memory-old");
        string newInstance = AitmCliRunner.NewTestInstance("index-memory-new");
        string memDir = MakeFixtureDir();
        try
        {
            // Oracle: today's aitm.cs IndexMemory() (aitm.cs:1207).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = AitmCliRunner.Run($"index-memory --instance {oldInstance} --from \"{memDir}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: IndexMemoryTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new IndexMemoryTool().Execute(connection, memDir).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("indexed 2 memor(ies) (1 hard rule(s)).", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM memory";
            Assert.Equal(2L, (long)(count.ExecuteScalar() ?? 0L));

            using SqliteCommand hardTitle = check.CreateCommand();
            hardTitle.CommandText = "SELECT title FROM memory WHERE hard=1";
            Assert.Equal("Index Memory Hard Rule Fixture", (string)hardTitle.ExecuteScalar()!);

            // MEMORY.md itself is the index, not a memory — skipped (aitm.cs's IndexMemory comment).
            using SqliteCommand indexRow = check.CreateCommand();
            indexRow.CommandText = "SELECT count(*) FROM memory WHERE k='MEMORY'";
            Assert.Equal(0L, (long)(indexRow.ExecuteScalar() ?? 0L));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` check "memory: re-index of an unchanged rule
    // logs no phantom mutation" now that selftest itself is gone.
    [Fact]
    public void ReindexingAnUnchangedDirectoryLogsNoPhantomMutation()
    {
        string instance = AitmCliRunner.NewTestInstance("index-memory-idempotent");
        string memDir = MakeFixtureDir();
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new IndexMemoryTool().Execute(connection, memDir);
            }

            using SqliteConnection check = StoreConnection.Open(dbPath);
            using SqliteCommand before = check.CreateCommand();
            before.CommandText = "SELECT count(*) FROM mutations WHERE kind='memory' AND k='index-memory-hard-fixture'";
            long beforeCount = (long)before.ExecuteScalar()!;
            Assert.Equal(1L, beforeCount);

            new IndexMemoryTool().Execute(check, memDir);

            using SqliteCommand after = check.CreateCommand();
            after.CommandText = "SELECT count(*) FROM mutations WHERE kind='memory' AND k='index-memory-hard-fixture'";
            Assert.Equal(beforeCount, (long)after.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void ReportsAMissingDirectoryTheSameWayTheCliOracleDoes()
    {
        string instance = AitmCliRunner.NewTestInstance("index-memory-missing");
        string missingDir = Path.Combine(Path.GetTempPath(), $"aitm-index-memory-missing-{Guid.NewGuid():N}");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = AitmCliRunner.Run($"index-memory --instance {instance} --from \"{missingDir}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new IndexMemoryTool().Execute(connection, missingDir).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal($"memory dir not found: {missingDir}", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"aitm-index-memory-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index-memory-plain-fixture.md"),
            "---\ntype: feedback\nname: Index Memory Plain Fixture\ndescription: a plain fixture memory\n---\n\nSome body text about the plain fixture.\n");
        File.WriteAllText(Path.Combine(dir, "index-memory-hard-fixture.md"),
            "---\ntype: feedback\nname: Index Memory Hard Rule Fixture\ndescription: this is a hard rule that always applies\n---\n\nAlways do this.\n");
        // MEMORY.md is the index itself, not a memory — must be skipped.
        File.WriteAllText(Path.Combine(dir, "MEMORY.md"), "# index\n\nsome links.\n");
        return dir;
    }
}
