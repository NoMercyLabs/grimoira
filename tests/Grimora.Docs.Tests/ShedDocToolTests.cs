using Grimora.Docs.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Docs.Tests;

public class ShedDocToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndDeletesMatchingSections()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("shed-doc-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("shed-doc-new");
        string dir = MakeFixtureDir("shed-doc-fixture");
        try
        {
            // Oracle: today's grimora.cs ShedDoc() (grimora.cs:1159).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"index-docs --instance {oldInstance} --from \"{dir}\"");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-doc --instance {oldInstance} --path \"{Path.GetFileName(dir)}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedDocTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            GrimoraCliRunner.Run($"index-docs --instance {newInstance} --from \"{dir}\"");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReportsNoDocsForAnUnmatchedPathFragment()
    {
        string instance = GrimoraCliRunner.NewTestInstance("shed-doc-missing");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-doc --instance {instance} --path never-existed-fragment");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedDocTool().Execute(connection, "never-existed-fragment").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no docs match path 'never-existed-fragment'.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            "# shedoctopic\n\nThis section carries the durable knowledge worth absorbing here, long enough.\n");
        return dir;
    }
}
