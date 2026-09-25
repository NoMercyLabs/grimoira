using Aitm.Docs.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Docs.Tests;

public class IndexDocsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndIndexesEachSection()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("index-docs-old");
        string newInstance = AitmCliRunner.NewTestInstance("index-docs-new");
        string dir = MakeFixtureDir("index-docs-fixture");
        try
        {
            // Oracle: today's aitm.cs IndexDocs() (aitm.cs:831).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = AitmCliRunner.Run($"index-docs --instance {oldInstance} --from \"{dir}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: IndexDocsTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new IndexDocsTool().Execute(connection, dir, "doc").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Contains("indexed 1 doc(s)", actual);
            Assert.Contains("shed 1 outdated section(s)", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM docs";
            Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RunningTwiceOnTheSameDirIsIdempotent()
    {
        string instance = AitmCliRunner.NewTestInstance("index-docs-idempotent");
        string dir = MakeFixtureDir("index-docs-idempotent-fixture");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            new IndexDocsTool().Execute(connection, dir, "doc");
            new IndexDocsTool().Execute(connection, dir, "doc");

            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM docs";
            Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));

            using SqliteCommand ftsCount = connection.CreateCommand();
            ftsCount.CommandText = "SELECT count(*) FROM docs_fts";
            Assert.Equal(1L, (long)(ftsCount.ExecuteScalar() ?? 0L));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"aitm-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            "# indexdocsfixture topic\n\nThis section carries the durable knowledge worth absorbing here.\n\n" +
            "# Changelog\n\n- [x] did the first thing\n- [x] did the second thing\n- [x] did the third thing\n- [x] did the fourth thing\n");
        return dir;
    }
}
