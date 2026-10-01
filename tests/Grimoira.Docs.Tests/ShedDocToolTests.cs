using Grimoira.Docs.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Docs.Tests;

public class ShedDocToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndDeletesMatchingSections()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("shed-doc-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("shed-doc-new");
        string dir = MakeFixtureDir("shed-doc-fixture");
        try
        {
            // Oracle: today's grimoira.cs ShedDoc() (grimoira.cs:1159).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Run($"index-docs --instance {oldInstance} --from \"{dir}\"");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"shed-doc --instance {oldInstance} --path \"{Path.GetFileName(dir)}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedDocTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            GrimoiraCliRunner.Run($"index-docs --instance {newInstance} --from \"{dir}\"");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ShedDocTool().Execute(connection, Path.GetFileName(dir)).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Contains("shed 1 section(s)", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM docs";
            Assert.Equal(0L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReportsNoDocsForAnUnmatchedPathFragment()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("shed-doc-missing");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"shed-doc --instance {instance} --path never-existed-fragment");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedDocTool().Execute(connection, "never-existed-fragment").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no docs match path 'never-existed-fragment'.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimoira-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            "# shedoctopic\n\nThis section carries the durable knowledge worth absorbing here, long enough.\n");
        return dir;
    }
}
