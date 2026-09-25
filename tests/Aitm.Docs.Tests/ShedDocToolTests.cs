using Aitm.Docs.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Docs.Tests;

public class ShedDocToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndDeletesMatchingSections()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("shed-doc-old");
        string newInstance = AitmCliRunner.NewTestInstance("shed-doc-new");
        string dir = MakeFixtureDir("shed-doc-fixture");
        try
        {
            // Oracle: today's aitm.cs ShedDoc() (aitm.cs:1159).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"index-docs --instance {oldInstance} --from \"{dir}\"");
            (string stdout, int exitCode) = AitmCliRunner.Run($"shed-doc --instance {oldInstance} --path \"{Path.GetFileName(dir)}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedDocTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            AitmCliRunner.Run($"index-docs --instance {newInstance} --from \"{dir}\"");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
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
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReportsNoDocsForAnUnmatchedPathFragment()
    {
        string instance = AitmCliRunner.NewTestInstance("shed-doc-missing");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"shed-doc --instance {instance} --path never-existed-fragment");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedDocTool().Execute(connection, "never-existed-fragment").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no docs match path 'never-existed-fragment'.", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"aitm-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            "# shedoctopic\n\nThis section carries the durable knowledge worth absorbing here, long enough.\n");
        return dir;
    }
}
