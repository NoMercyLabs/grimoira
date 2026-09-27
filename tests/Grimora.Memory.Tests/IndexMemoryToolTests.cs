using Grimora.Memory.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Memory.Tests;

public class IndexMemoryToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndIndexesEachFileIntoTheMemoryTable()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("index-memory-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("index-memory-new");
        string memDir = MakeFixtureDir();
        try
        {
            // Oracle: today's grimora.cs IndexMemory() (grimora.cs:1207).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"index-memory --instance {oldInstance} --from \"{memDir}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: IndexMemoryTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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

            // MEMORY.md itself is the index, not a memory — skipped (grimora.cs's IndexMemory comment).
            using SqliteCommand indexRow = check.CreateCommand();
            indexRow.CommandText = "SELECT count(*) FROM memory WHERE k='MEMORY'";
            Assert.Equal(0L, (long)(indexRow.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` check "memory: re-index of an unchanged rule
    // logs no phantom mutation" now that selftest itself is gone.
    [Fact]
    public void ReindexingAnUnchangedDirectoryLogsNoPhantomMutation()
    {
        string instance = GrimoraCliRunner.NewTestInstance("index-memory-idempotent");
        string memDir = MakeFixtureDir();
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
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
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void ReportsAMissingDirectoryTheSameWayTheCliOracleDoes()
    {
        string instance = GrimoraCliRunner.NewTestInstance("index-memory-missing");
        string missingDir = Path.Combine(Path.GetTempPath(), $"grimora-index-memory-missing-{Guid.NewGuid():N}");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"index-memory --instance {instance} --from \"{missingDir}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new IndexMemoryTool().Execute(connection, missingDir).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal($"memory dir not found: {missingDir}", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-index-memory-fixture-{Guid.NewGuid():N}");
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
